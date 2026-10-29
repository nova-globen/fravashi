using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Fravashi.Bridge;
using Microsoft.AspNetCore.Builder;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Bridge.Tests;

public sealed class BridgeApplicationTests : IAsyncLifetime
{
    private const string Token = "integration-test-token";

    private const string OfflineFolderName = "on-an-offline-drive";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "fravashi-bridge-tests", Guid.NewGuid().ToString("N"));

    // The probe seam stands in for a volume that is not attached: no test can unmap a real drive.
    private readonly WorkspaceRegistry _registry = new(probe: path =>
        path.EndsWith(OfflineFolderName, StringComparison.OrdinalIgnoreCase)
            ? WorkspacePathState.Unreachable
            : WorkspacePathProbe.Probe(path));

    private WebApplication? _app;
    private HttpClient _anonymous = null!;
    private HttpClient _authorized = null!;
    private string _id = "";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "seed.cs"), "var haystack = \"find-me\";\n");
        _id = _registry.Register(_root).Id;

        _app = BridgeApplication.Create(new BridgeServerOptions(Token, ["http://127.0.0.1:0"]), _registry);
        await _app.StartAsync();
        var baseUrl = _app.Urls.First();

        _anonymous = new HttpClient { BaseAddress = new Uri(baseUrl) };
        _authorized = new HttpClient { BaseAddress = new Uri(baseUrl) };
        _authorized.DefaultRequestHeaders.Add(BridgeApplication.TokenHeader, Token);
    }

    [Fact]
    public async Task Healthz_NeedsNoToken()
        => (await _anonymous.GetAsync("/bridge/healthz")).StatusCode.Should().Be(HttpStatusCode.OK);

    [Fact]
    public async Task Workspaces_WithoutToken_Is401()
        => (await _anonymous.GetAsync("/bridge/workspaces")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

    [Fact]
    public async Task Workspaces_WithWrongToken_Is401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/bridge/workspaces");
        request.Headers.Add(BridgeApplication.TokenHeader, "wrong");
        (await _anonymous.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Workspaces_ListsTheRegisteredFolder()
    {
        var workspaces = await _authorized.GetFromJsonAsync<List<BridgeWorkspace>>("/bridge/workspaces");
        workspaces.Should().ContainSingle().Which.Id.Should().Be(_id);
    }

    [Fact]
    public async Task Tree_ListsEntries()
    {
        var entries = await _authorized.GetFromJsonAsync<List<BridgeEntry>>($"/bridge/ws/{_id}/tree?path=src");
        entries.Should().ContainSingle().Which.Path.Should().Be("src/seed.cs");
    }

    [Fact]
    public async Task File_PutThenGet_RoundTrips_WithStamps()
    {
        var body = new ByteArrayContent(Encoding.UTF8.GetBytes("round-trip"));
        var put = await _authorized.PutAsync($"/bridge/ws/{_id}/file?path=new/file.txt", body);
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var stampDoc = JsonDocument.Parse(await put.Content.ReadAsStringAsync()))
        {
            stampDoc.RootElement.TryGetProperty("modifiedUtc", out _).Should().BeTrue();
        }

        var get = await _authorized.GetAsync($"/bridge/ws/{_id}/file?path=new/file.txt");
        (await get.Content.ReadAsStringAsync()).Should().Be("round-trip");
        get.Headers.TryGetValues(BridgeApplication.ModifiedUtcHeader, out var stamps).Should().BeTrue();
        DateTimeOffset.TryParse(stamps!.Single(), out _).Should().BeTrue();
    }

    [Fact]
    public async Task File_PutWithAStaleExpectedStamp_Is412()
    {
        var seed = new ByteArrayContent(Encoding.UTF8.GetBytes("v1"));
        (await _authorized.PutAsync($"/bridge/ws/{_id}/file?path=guarded.txt", seed)).EnsureSuccessStatusCode();

        using var request = new HttpRequestMessage(
            HttpMethod.Put, $"/bridge/ws/{_id}/file?path=guarded.txt")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("v2")),
        };
        request.Headers.Add(BridgeApplication.TokenHeader, Token);
        request.Headers.Add(
            BridgeApplication.ExpectedUtcHeader, DateTimeOffset.UtcNow.AddMinutes(-30).ToString("O"));

        (await _anonymous.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
        (await _authorized.GetStringAsync($"/bridge/ws/{_id}/file?path=guarded.txt")).Should().Be("v1");
    }

    [Fact]
    public async Task File_PutWithAnExpectedStamp_OnAMissingFile_Is410()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put, $"/bridge/ws/{_id}/file?path=never-existed.txt")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("x")),
        };
        request.Headers.Add(BridgeApplication.TokenHeader, Token);
        request.Headers.Add(BridgeApplication.ExpectedUtcHeader, DateTimeOffset.UtcNow.ToString("O"));

        (await _anonymous.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task RegisterWorkspace_RegistersTheFolder()
    {
        var extra = Path.Combine(_root, "extra-workspace");
        Directory.CreateDirectory(extra);

        var response = await _authorized.PostAsJsonAsync("/bridge/workspaces", new { path = extra });
        response.EnsureSuccessStatusCode();
        var workspace = await response.Content.ReadFromJsonAsync<BridgeWorkspace>();
        workspace!.Name.Should().Be("extra-workspace");

        var all = await _authorized.GetFromJsonAsync<List<BridgeWorkspace>>("/bridge/workspaces");
        all.Should().HaveCount(2);
    }

    [Fact]
    public async Task RegisterWorkspace_MissingFolder_Is404()
        => (await _authorized.PostAsJsonAsync("/bridge/workspaces", new { path = Path.Combine(_root, "nope") }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

    [Fact]
    public async Task RegisterWorkspace_UnreachableFolder_Is503_NotA404()
    {
        // 404 is the studio's cue that the folder is gone (and that offering to drop the recents
        // row is fair). A folder on a drive that is not attached must not produce it.
        var response = await _authorized.PostAsJsonAsync(
            "/bridge/workspaces", new { path = Path.Combine(_root, OfflineFolderName) });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task PickFolder_WithoutAHostCallback_Is404()
        => (await _authorized.PostAsync("/bridge/pick-folder", content: null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

    [Fact]
    public async Task Notify_WithoutAHostCallback_Is404()
        => (await _authorized.PostAsJsonAsync("/bridge/notify", new { message = "waiting" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

    [Fact]
    public async Task Notify_BlankMessage_Is400_AndNeverCallsTheHost()
    {
        var calls = new List<string>();
        var app = BridgeApplication.Create(
            new BridgeServerOptions(Token, ["http://127.0.0.1:0"], ShowToast: message =>
            {
                calls.Add(message);
                return Task.CompletedTask;
            }),
            new WorkspaceRegistry());
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            client.DefaultRequestHeaders.Add(BridgeApplication.TokenHeader, Token);

            (await client.PostAsJsonAsync("/bridge/notify", new { message = "   " }))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);

            calls.Should().BeEmpty();
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Notify_NonBlankMessage_CallsTheHost_AndIs204()
    {
        var calls = new List<string>();
        var app = BridgeApplication.Create(
            new BridgeServerOptions(Token, ["http://127.0.0.1:0"], ShowToast: message =>
            {
                calls.Add(message);
                return Task.CompletedTask;
            }),
            new WorkspaceRegistry());
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            client.DefaultRequestHeaders.Add(BridgeApplication.TokenHeader, Token);

            var response = await client.PostAsJsonAsync("/bridge/notify", new { message = "Work item #7 is parked, waiting on you." });

            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
            calls.Should().ContainSingle("Work item #7 is parked, waiting on you.");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Clone_WithAnUncloneableUrl_Is400()
        => (await _authorized.PostAsJsonAsync("/bridge/clone", new { url = "-upload-pack=calc" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

    [Fact]
    public async Task Clone_WithoutAHostCallback_Is404()
        => (await _authorized.PostAsJsonAsync("/bridge/clone", new { url = "https://forge.example.test/a/b.git" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

    [Fact]
    public async Task Clone_CancelledPicker_Is204_AndExistingTarget_Is409()
    {
        // A second app instance with a scripted picker: first call cancels, later calls pick a
        // parent that already contains the derived folder name.
        var parent = Path.Combine(_root, "clone-parent");
        Directory.CreateDirectory(Path.Combine(parent, "widgets"));
        var picks = new Queue<string?>([null, parent]);
        var registry = new WorkspaceRegistry();
        var app = BridgeApplication.Create(
            new BridgeServerOptions(Token, ["http://127.0.0.1:0"], PickFolder: () => Task.FromResult(picks.Dequeue())),
            registry);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            client.DefaultRequestHeaders.Add(BridgeApplication.TokenHeader, Token);

            (await client.PostAsJsonAsync("/bridge/clone", new { url = "https://example.invalid/acme/widgets.git" }))
                .StatusCode.Should().Be(HttpStatusCode.NoContent);

            var conflict = await client.PostAsJsonAsync("/bridge/clone", new { url = "https://example.invalid/acme/widgets.git" });
            conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await conflict.Content.ReadAsStringAsync()).Should().Contain("widgets");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Traversal_Is403()
        => (await _authorized.GetAsync($"/bridge/ws/{_id}/file?path=..%2Foutside.txt"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

    [Fact]
    public async Task UnknownWorkspace_Is404()
        => (await _authorized.GetAsync("/bridge/ws/deadbeefdeadbeef/tree"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

    [Fact]
    public async Task Git_RunsWithinTheWorkspace()
    {
        if (!BridgeGitService.IsGitAvailable())
        {
            return; // xunit 2.x has no dynamic skip; a machine without git passes vacuously.
        }

        var response = await _authorized.PostAsJsonAsync($"/bridge/ws/{_id}/git", new { args = new[] { "--version" } });
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<BridgeGitResult>();
        result!.ExitCode.Should().Be(0);
        result.Stdout.Should().Contain("git version");
    }

    [Fact]
    public async Task Git_OnAHostWithNoGit_AnswersAResultThatSaysSo_NotA500()
    {
        // Backlog 266: on a machine with no git, starting it threw and the endpoint answered 500, which the studio
        // could not tell from a bridge that is not there. A program name that does not exist stands in for that host.
        var app = BridgeApplication.Create(
            new BridgeServerOptions(
                Token, ["http://127.0.0.1:0"], GitExecutable: "fravashi-no-such-git-" + Guid.NewGuid().ToString("N")),
            _registry);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            client.DefaultRequestHeaders.Add(BridgeApplication.TokenHeader, Token);

            var response = await client.PostAsJsonAsync($"/bridge/ws/{_id}/git", new { args = new[] { "ls-files", "-z" } });

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var result = await response.Content.ReadFromJsonAsync<BridgeGitResult>();
            result!.ExitCode.Should().Be(127);
            result.Stderr.Should().StartWith("git is not installed on this machine");
            result.Stdout.Should().BeEmpty();
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Search_FindsTheSeededLiteral()
    {
        var response = await _authorized.PostAsJsonAsync($"/bridge/ws/{_id}/search", new { pattern = "find-me" });
        response.EnsureSuccessStatusCode();
        var matches = await response.Content.ReadFromJsonAsync<List<BridgeSearchMatch>>();
        matches.Should().ContainSingle().Which.Path.Should().Be("src/seed.cs");
    }

    [Fact]
    public async Task Events_StreamsAFileChange()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/bridge/ws/{_id}/events");
        request.Headers.Add(BridgeApplication.TokenHeader, Token);
        using var response = await _anonymous.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();

        // Do not swap this for a fixed Task.Delay to "let the watcher arm" (backlog 107): the SSE
        // response headers flush before the FileSystemWatcher is guaranteed armed, so a single
        // post-delay write can fall into the arm gap on a loaded box and no event ever arrives.
        // Instead re-touch the sentinel every ~100ms until the stream provably delivers it (the same
        // round-trip idea as WorkspaceSyncSessionTests.WaitForLiveHostWatchAsync): once one event is
        // observed the whole pipeline is live. Deterministic: stops the moment an event round-trips.
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var probe = Task.Run(async () =>
        {
            while (!probeCts.Token.IsCancellationRequested)
            {
                File.WriteAllText(Path.Combine(_root, "sse-probe.txt"), "ping");
                try { await Task.Delay(100, probeCts.Token); }
                catch (OperationCanceledException) { return; }
            }
        }, CancellationToken.None);

        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream);
            while (!cts.Token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cts.Token);
                if (line is not null && line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    var fileEvent = JsonSerializer.Deserialize<BridgeFileEvent>(
                        line["data: ".Length..], new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    if (fileEvent!.Path == "sse-probe.txt")
                    {
                        return; // observed the change through SSE
                    }
                }
            }

            Assert.Fail("No SSE event for sse-probe.txt arrived within the timeout.");
        }
        finally
        {
            await probeCts.CancelAsync();
            await probe;
        }
    }

    [Fact]
    public async Task Events_sends_a_heartbeat_comment_while_nothing_changes()
    {
        // Backlog 257: a stream that dies without a close (sleep and resume, a network switch) can only be noticed by
        // a client that knows the bridge speaks at least every interval. A comment line is ignored by every SSE reader.
        BridgeApplication.DefaultEventHeartbeat.Should().Be(TimeSpan.FromSeconds(15));
        await using var app = BridgeApplication.Create(
            new BridgeServerOptions(Token, ["http://127.0.0.1:0"], EventHeartbeat: TimeSpan.FromMilliseconds(200)), _registry);
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/bridge/ws/{_id}/events");
        request.Headers.Add(BridgeApplication.TokenHeader, Token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));

        // The first heartbeat comes with the headers, before this client has read a line. The spacing is timed from its
        // ARRIVAL: a stopwatch started earlier counted this client's own start-up against the bridge, and on a loaded CI
        // runner that cost 52 ms of the 600 and failed the bound at 547 ms (run 401, 2026-10-08).
        var heartbeats = 0;
        System.Diagnostics.Stopwatch? started = null;
        while (heartbeats < 4)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            line.Should().NotBeNull("the stream must stay open");
            if (line!.StartsWith(':'))
            {
                heartbeats++;
                started ??= System.Diagnostics.Stopwatch.StartNew();
            }
        }

        // One at once (so a client learns this bridge has them), then one per interval of quiet: spaced, not a burst at
        // connect, because a bridge that went quiet after a burst would make every client drop a quiet stream.
        started!.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(550), "three intervals of quiet follow the first");
        started!.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        reader.Dispose();
        response.Dispose();
        await app.StopAsync();
    }

    public async Task DisposeAsync()
    {
        _anonymous.Dispose();
        _authorized.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
