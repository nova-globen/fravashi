using Fravashi.Application.Repository;
using Fravashi.Application.Search;
using Fravashi.Infrastructure.Bridge;
using Fravashi.Infrastructure.Repository;
using Microsoft.Extensions.Configuration;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Bridge;

/// <summary>Search adapter + change subscriber over a real hosted bridge (plan 2b-i Task 6).</summary>
public sealed class BridgeSearchAndEventsTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "fravashi-remote-tests", Guid.NewGuid().ToString("N"));
    private HostedBridge _bridge = null!;
    private BridgeSearchAdapter _search = null!;
    private BridgeChangeSubscriber _events = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        File.WriteAllText(Path.Combine(_root, "src", "one.cs"), "var needle = 1;\nvar needle2 = 2;\n");
        File.WriteAllText(Path.Combine(_root, "bin", "junk.cs"), "var needle = 9;\n");

        _bridge = await HostedBridge.StartAsync(_root);
        var client = _bridge.CreateClient();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SearchDefaults.IgnoreRulesKey] = "bin/\n# a comment line\n",
            })
            .Build();
        var writer = new BridgeRepositoryWriter(client, new RepositoryWriteGate());
        _search = new BridgeSearchAdapter(client, writer, configuration);
        _events = new BridgeChangeSubscriber(client);
    }

    [Fact]
    public async Task Search_GroupsByFile_AndHonorsTheStudioIgnoreRules()
    {
        var results = await _search.SearchAsync(new SearchQuery("needle"));

        results.Engine.Should().Be("bridge");
        results.Files.Should().ContainSingle().Which.RelativePath.Should().Be("src/one.cs");
        results.Files[0].Matches.Should().HaveCount(2);
        results.Files[0].Matches[0].Length.Should().Be("needle".Length);
    }

    [Fact]
    public async Task Replace_AppliesEverywhere_AndRestoreBringsThePreImageBack()
    {
        var replaced = await _search.ReplaceAsync(new SearchQuery("needle"), "pin", ["src/one.cs"]);

        replaced.FilesChanged.Should().Be(1);
        replaced.OccurrencesReplaced.Should().Be(2);
        File.ReadAllText(Path.Combine(_root, "src", "one.cs")).Should().Contain("var pin = 1;");

        var restored = await _search.RestoreAsync(replaced.Files!);
        restored.FilesChanged.Should().Be(1);
        File.ReadAllText(Path.Combine(_root, "src", "one.cs")).Should().Contain("var needle = 1;");
    }

    [Fact]
    public async Task Replace_OnlyAt_TouchesExactlyThatOccurrence()
    {
        var replaced = await _search.ReplaceAsync(
            new SearchQuery("needle"), "pin", ["src/one.cs"], onlyAt: (2, 5));

        replaced.OccurrencesReplaced.Should().Be(1);
        var text = File.ReadAllText(Path.Combine(_root, "src", "one.cs"));
        text.Should().Contain("var needle = 1;");
        text.Should().Contain("var pin2 = 2;");
    }

    [Fact]
    public async Task Subscriber_MapsCreatesAndRenames()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var seen = new List<RepositoryChange>();
        var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renamed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = _events.Subscribe(change =>
        {
            lock (seen)
            {
                seen.Add(change);
            }

            if (change is { Kind: RepositoryChangeKind.ChangedOrCreated, RelativePath: "fresh.txt" })
            {
                created.TrySetResult();
            }

            if (change is { Kind: RepositoryChangeKind.ChangedOrCreated, RelativePath: "moved.txt" })
            {
                renamed.TrySetResult();
            }
        });

        // Do not swap this for a fixed Task.Delay to "let the SSE loop and watcher arm" (backlog 107):
        // both arm after Subscribe returns, so a single post-delay write can fall into the arm gap.
        // Re-write fresh.txt until the subscriber provably delivers its create, then proceed. The later
        // move/delete assertions are unaffected by the extra identical writes. Deterministic: stops the
        // moment the event round-trips.
        while (!created.Task.IsCompleted && !cts.IsCancellationRequested)
        {
            File.WriteAllText(Path.Combine(_root, "fresh.txt"), "x");
            await Task.WhenAny(created.Task, Task.Delay(100, CancellationToken.None));
        }

        await created.Task.WaitAsync(cts.Token);

        File.Move(Path.Combine(_root, "fresh.txt"), Path.Combine(_root, "moved.txt"));
        await renamed.Task.WaitAsync(cts.Token);

        lock (seen)
        {
            seen.Should().Contain(c => c.Kind == RepositoryChangeKind.Deleted && c.RelativePath == "fresh.txt");
        }
    }

    public async Task DisposeAsync()
    {
        _events.Dispose();
        await _bridge.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
