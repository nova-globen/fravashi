using System.Threading.Channels;
using Fravashi.Bridge;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Bridge.Tests;

public sealed class BridgeWatchHubTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "fravashi-bridge-tests", Guid.NewGuid().ToString("N"));
    private readonly WorkspaceRegistry _registry = new();
    private readonly BridgeWatchHub _hub;
    private readonly string _id;

    public BridgeWatchHubTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        _id = _registry.Register(_root).Id;
        _hub = new BridgeWatchHub(_registry);
    }

    [Fact]
    public async Task Subscribe_SeesCreateChangeDelete_ButNeverGitInternals()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reader = _hub.Subscribe(_id, cts.Token);

        var file = Path.Combine(_root, "watched.txt");
        File.WriteAllText(file, "one");
        File.AppendAllText(file, "two");
        File.WriteAllText(Path.Combine(_root, ".git", "index.lock"), "git noise");
        File.Delete(file);

        var events = await CollectAsync(
            reader,
            s => s.Any(e => e.Path == "watched.txt" && e.Type == "created")
                && s.Any(e => e.Path == "watched.txt" && e.Type == "deleted"),
            cts.Token);

        events.Should().Contain(e => e.Path == "watched.txt" && e.Type == "created");
        events.Should().Contain(e => e.Path == "watched.txt" && e.Type == "deleted");
        events.Should().NotContain(e => e.Path.StartsWith(".git"));
    }

    [Fact]
    public async Task Subscribe_SeesRenames()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reader = _hub.Subscribe(_id, cts.Token);

        var from = Path.Combine(_root, "before.txt");
        File.WriteAllText(from, "x");
        File.Move(from, Path.Combine(_root, "after.txt"));

        var events = await CollectAsync(
            reader, s => s.Any(e => e.Type == "renamed" && e.Path == "after.txt"), cts.Token);

        events.Should().Contain(e => e.Type == "renamed" && e.Path == "after.txt" && e.OldPath == "before.txt");
    }

    [Fact]
    public async Task A_subscriber_whose_queue_dropped_events_is_told_so()
    {
        // Backlog 253: the queue drops its oldest when a subscriber falls 1024 behind, and that used to be silent, so
        // a consumer keeping a copy never knew to look again. Nothing reads until the burst is over.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var reader = _hub.Subscribe(_id, cts.Token);
        for (var i = 0; i < 1500; i++)
        {
            File.WriteAllText(Path.Combine(_root, $"burst-{i}.txt"), "x");
        }

        // The queue is full only once the watcher has raised what it saw; then a few more events must drop something.
        while (reader.Count < 1024 && !cts.IsCancellationRequested)
        {
            await Task.Delay(20); // poll interval while the watcher catches up, not a guess at its speed
        }

        for (var i = 0; i < 5; i++)
        {
            File.WriteAllText(Path.Combine(_root, $"after-full-{i}.txt"), "x");
        }

        var events = await CollectAsync(reader, s => s.Any(e => e.Type == BridgeWatchHub.OverflowType), cts.Token);

        events.Should().Contain(e => e.Type == BridgeWatchHub.OverflowType && e.Path == string.Empty);
    }

    [Fact]
    public void Subscribe_UnknownWorkspace_Throws()
    {
        var act = () => _hub.Subscribe("deadbeefdeadbeef", CancellationToken.None);
        act.Should().Throw<KeyNotFoundException>();
    }

    /// <summary>
    /// Reads events until <paramref name="until"/> holds over everything seen so far (or timeout).
    /// The predicate replaces a first-event-plus-fixed-grace stop (backlog 107): a caller that needs
    /// both a create AND a trailing delete waits for both explicitly instead of guessing a 300ms
    /// window for the delete to arrive. Mirrors RepositoryChangeWatcherTests.CollectAsync.
    /// </summary>
    private static async Task<List<BridgeFileEvent>> CollectAsync(
        ChannelReader<BridgeFileEvent> reader, Func<List<BridgeFileEvent>, bool> until, CancellationToken ct)
    {
        var seen = new List<BridgeFileEvent>();
        try
        {
            await foreach (var e in reader.ReadAllAsync(ct))
            {
                seen.Add(e);
                if (until(seen))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout: return what arrived; the assertions then explain what was missing.
        }

        return seen;
    }

    public void Dispose()
    {
        _hub.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
