using System.Text;
using Fravashi.Infrastructure.Bridge;
using Microsoft.Extensions.Configuration;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Bridge;

public sealed class BridgeClientTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "fravashi-remote-tests", Guid.NewGuid().ToString("N"));
    private HostedBridge _bridge = null!;
    private BridgeClient _client = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "seed.cs"), "var haystack = \"find-me\";\n");
        _bridge = await HostedBridge.StartAsync(_root);
        _client = _bridge.CreateClient();
    }

    [Fact]
    public async Task ListWorkspaces_ListsTheRegisteredFolder()
    {
        var workspaces = await _client.ListWorkspacesAsync(CancellationToken.None);
        workspaces.Should().ContainSingle().Which.Id.Should().Be(_bridge.WorkspaceId);
    }

    [Fact]
    public async Task List_And_Stat_SeeTheSeededFile()
    {
        var entries = await _client.ListAsync("src", CancellationToken.None);
        entries.Should().ContainSingle().Which.Path.Should().Be("src/seed.cs");

        var stat = await _client.StatAsync("src/seed.cs", CancellationToken.None);
        stat!.Size.Should().BeGreaterThan(0);

        (await _client.StatAsync("src/absent.cs", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Read_ReturnsBytesAndStamp_AndNullFor404()
    {
        var content = await _client.ReadAsync("src/seed.cs", CancellationToken.None);
        Encoding.UTF8.GetString(content!.Bytes).Should().Contain("find-me");
        content.ModifiedUtc.Should().NotBeNull();

        (await _client.ReadAsync("src/absent.cs", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task A_refused_read_names_what_was_read_and_keeps_its_status()
    {
        // Backlog 177: seen proving 173, a .gitignore that is a folder. The HTTP client's own message named a
        // 403 and nothing else, and it is what the session pane showed.
        Directory.CreateDirectory(Path.Combine(_root, ".gitignore"));

        var read = () => _client.ReadAsync(".gitignore", CancellationToken.None);

        var thrown = (await read.Should().ThrowAsync<HttpRequestException>()).Which;
        thrown.Message.Should().Be("reading .gitignore was refused (403)");
        thrown.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden, "callers still filter on the status");
    }

    [Fact]
    public async Task A_refused_listing_names_the_folder_and_a_bad_token_is_refused_by_name()
    {
        var bad = new BridgeClient(new HttpClient(), new BridgeWorkspaceContext(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [BridgeWorkspaceContext.UrlKey] = _bridge.BaseUrl,
                [BridgeWorkspaceContext.TokenKey] = "not-the-token",
                [BridgeWorkspaceContext.ActiveIdKey] = _bridge.WorkspaceId,
            })
            .Build()));

        var listing = () => bad.ListAsync("src", CancellationToken.None);
        (await listing.Should().ThrowAsync<HttpRequestException>())
            .Which.Message.Should().Be("listing src was refused (401)");

        var root = () => bad.ListAsync(null, CancellationToken.None);
        (await root.Should().ThrowAsync<HttpRequestException>())
            .Which.Message.Should().Be("listing the workspace root was refused (401)");
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.Forbidden, "reading a.txt was refused (403)")]
    [InlineData(System.Net.HttpStatusCode.Unauthorized, "reading a.txt was refused (401)")]
    [InlineData(System.Net.HttpStatusCode.InternalServerError, "reading a.txt failed (500)")]
    [InlineData(System.Net.HttpStatusCode.BadRequest, "reading a.txt failed (400)")]
    public void A_failure_reads_as_the_act_then_the_status(System.Net.HttpStatusCode status, string expected)
        => BridgeClient.DescribeFailure("reading a.txt", status).Should().Be(expected);

    [Fact]
    public async Task Write_RoundTrips_AndConflictsMapToTheException()
    {
        using var body = new MemoryStream(Encoding.UTF8.GetBytes("v1"));
        var stamp = await _client.WriteAsync("new.txt", body, expectedModifiedUtc: null, CancellationToken.None);

        using var stale = new MemoryStream(Encoding.UTF8.GetBytes("v2"));
        var conflicting = () => _client.WriteAsync(
            "new.txt", stale, stamp.AddMinutes(-30), CancellationToken.None);
        (await conflicting.Should().ThrowAsync<BridgeConflictException>()).Which.Missing.Should().BeFalse();

        using var missing = new MemoryStream(Encoding.UTF8.GetBytes("x"));
        var gone = () => _client.WriteAsync(
            "never.txt", missing, DateTimeOffset.UtcNow, CancellationToken.None);
        (await gone.Should().ThrowAsync<BridgeConflictException>()).Which.Missing.Should().BeTrue();
    }

    [Fact]
    public async Task Rename_Git_And_Search_GoThrough()
    {
        await _client.RenameAsync("src/seed.cs", "src/renamed.cs", CancellationToken.None);
        File.Exists(Path.Combine(_root, "src", "renamed.cs")).Should().BeTrue();

        var git = await _client.GitAsync(["--version"], CancellationToken.None);
        git.ExitCode.Should().Be(0);

        var matches = await _client.SearchAsync(new { pattern = "find-me" }, CancellationToken.None);
        matches.Should().ContainSingle().Which.Path.Should().Be("src/renamed.cs");
    }

    [Fact]
    public async Task RegisterWorkspace_And_PickFolderWithoutHostCallback()
    {
        var extra = Path.Combine(_root, "another");
        Directory.CreateDirectory(extra);

        var registered = await _client.RegisterWorkspaceAsync(extra, CancellationToken.None);
        registered.Outcome.Should().Be(BridgeRegistrationOutcome.Registered);
        registered.Workspace!.Name.Should().Be("another");

        // A folder the host does not have answers 404 (pinned server-side by
        // BridgeApplicationTests.RegisterWorkspace_MissingFolder_Is404); the client turns that into
        // Missing so the studio can offer to drop a stale recents row instead of throwing.
        (await _client.RegisterWorkspaceAsync(Path.Combine(_root, "gone"), CancellationToken.None))
            .Outcome.Should().Be(BridgeRegistrationOutcome.Missing);

        (await _client.PickFolderAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task RegisterWorkspace_OnAnUnattachedVolume_IsUnreachable_NotMissing()
    {
        // The whole point: an unmapped drive letter must not arrive at the studio wearing the same
        // answer as a deleted folder, because the studio offers to delete the recents row on that.
        var offline = Path.Combine(_root, HostedBridge.UnreachableFolderName);

        var result = await _client.RegisterWorkspaceAsync(offline, CancellationToken.None);

        result.Outcome.Should().Be(BridgeRegistrationOutcome.Unreachable);
        result.Workspace.Should().BeNull();
    }

    [Fact]
    public async Task Clone_WithoutAHostCallback_IsNull_AndABadUrlThrows()
    {
        // The test bridge has no PickFolder callback: a valid URL answers 404 -> null.
        (await _client.CloneAsync("https://forge.example.test/a/b.git", CancellationToken.None)).Should().BeNull();

        var act = () => _client.CloneAsync("-upload-pack=calc", CancellationToken.None);
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("cloneable");
    }

    [Fact]
    public async Task SubscribeEvents_DeliversAChange()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var seen = new TaskCompletionSource<BridgeFileEventDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loop = _client.SubscribeEventsAsync(e =>
        {
            if (e.Path == "probe.txt")
            {
                seen.TrySetResult(e);
            }

            return Task.CompletedTask;
        }, cts.Token);

        // Do not swap this for a fixed Task.Delay to "let the watcher arm" (backlog 107): the watch
        // arms after the subscribe GET connects, so a single post-delay write can fall into the arm
        // gap. Re-write probe.txt until the subscriber provably delivers it. Deterministic: stops the
        // moment the event round-trips.
        while (!seen.Task.IsCompleted && !cts.IsCancellationRequested)
        {
            File.WriteAllText(Path.Combine(_root, "probe.txt"), "ping");
            await Task.WhenAny(seen.Task, Task.Delay(100, CancellationToken.None));
        }

        var received = await seen.Task.WaitAsync(cts.Token);
        received.Type.Should().BeOneOf("created", "changed");
        cts.Cancel();
        await loop;
    }

    public async Task DisposeAsync()
    {
        await _bridge.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
