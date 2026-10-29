using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bunit;
using Fravashi.Application.AgentSync;
using Fravashi.Application.Autopilot;
using Fravashi.Application.Persistence;
using Fravashi.Application.Sessions;
using Fravashi.Application.Wiki;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Autopilot;
using Fravashi.Domain.Projects;
using Fravashi.Infrastructure.AgentRuntimes;
using Fravashi.Infrastructure.Autopilot;
using Fravashi.Infrastructure.Browser;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.Wiki;
using Fravashi.Infrastructure.WorkItems;
using Fravashi.Web.Components.Dashboard;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// #313's live tier: the Autopilot card's own bUnit-rendered markup (the retired autopilot page's before work item #358), measured in a real headless Chromium against
/// the real studio.css. Kept in its own class, not alongside <see cref="AutopilotCardTests"/>'s plain bUnit
/// tests: backlog 295's coverage scanner names the ENCLOSING class of any custom FactAttribute usage, so
/// a browser-live test sharing a class with unconditional tests reports the whole class as unclear
/// whether it runs in the container. Shares its fixture with <see cref="AutopilotCardTests"/> through
/// <see cref="AutopilotCardTestHarness"/>.
/// </summary>
public sealed class AutopilotCardLiveTests : AutopilotCardTestHarness
{
    private static bool BrowserLiveEnabled => Environment.GetEnvironmentVariable("FRAVASHI_BROWSER_LIVE") == "1";

    /// <summary>Skipped with its reason at discovery time, because Assert.Skip needs xunit v3.</summary>
    private sealed class BrowserLiveFactAttribute : FactAttribute
    {
        public BrowserLiveFactAttribute() => Skip = BrowserLiveEnabled
            ? null
            : "Set FRAVASHI_BROWSER_LIVE=1 where a Chromium is installed (the studio container) to drive a real browser.";
    }

    /// <summary>A runner whose events are pushed in by the test (#313's live tier), rather than the Silent
    /// runner's empty stream: a real conversation's accumulated height is what the defect needs to show.
    /// Keyed by session so two sessions started through the same registry never share one stream.</summary>
    private sealed class EmittingSessionRunner : IAgentSessionRunner
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<AgentSessionId, System.Threading.Channels.Channel<AgentEvent>> _channels = [];

        public IAsyncEnumerable<AgentEvent> RunAsync(AgentSessionRequest request, CancellationToken cancellationToken = default) =>
            ChannelFor(request.SessionId).Reader.ReadAllAsync(cancellationToken);

        public Task<bool> SendAsync(AgentSessionId sessionId, string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public bool Stop(AgentSessionId sessionId) => true;

        public bool ChangeModel(AgentSessionId sessionId, string? model) => true;

        public void Emit(AgentSessionId sessionId, AgentEvent e) => ChannelFor(sessionId).Writer.TryWrite(e);

        private System.Threading.Channels.Channel<AgentEvent> ChannelFor(AgentSessionId id) =>
            _channels.GetOrAdd(id, _ => System.Threading.Channels.Channel.CreateUnbounded<AgentEvent>());
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fravashi.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "the test must be able to locate the repository root (Fravashi.sln)");
        return dir!.FullName;
    }

    private static string StudioCss() =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Fravashi.Web", "wwwroot", "studio.css"));

    /// <summary>
    /// The Autopilot card's own rendered markup, wrapped in an approximation of where it really sits
    /// (<c>docs/layout.md</c>: <c>@Body</c> renders inside <c>.studio-main</c> / <c>.studio-main-body</c>).
    /// This stands in for the <c>.studio-shell</c> grid and <c>html.window-host</c>, which this test never
    /// builds; the test's own <c>body</c> and <c>.studio-main</c> rules hand <c>.studio-main-body</c> a
    /// definite height directly instead. That is sufficient here because the only thing the rules under
    /// test depend on is a definite height reaching the card, which this gives it.
    /// </summary>
    private static string Shell(string cardMarkup) => $$"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <title>Autopilot</title>
        <link rel="stylesheet" href="/studio.css">
        <style>html, body { height: 100%; margin: 0; } body { display: flex; } .studio-main { flex: 1; min-height: 0; }</style>
        </head>
        <body>
        <div class="studio-main">
        <div class="studio-main-body">
        {{cardMarkup}}
        </div>
        </div>
        </body>
        </html>
        """;

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed record Box(
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y,
        [property: JsonPropertyName("width")] double Width,
        [property: JsonPropertyName("height")] double Height)
    {
        public bool Intersects(Box other) =>
            X < other.X + other.Width && X + Width > other.X &&
            Y < other.Y + other.Height && Y + Height > other.Y;
    }

    /// <summary>
    /// #313: with two running items, each with a bound live session pane, the second also waiting for
    /// capacity, every measured text box is a real, non-collapsed box and no two of them intersect,
    /// compared pairwise across all of them (within a block as well as across the two blocks), measured in
    /// a REAL headless Chromium (the same launch options every browser the studio starts uses,
    /// <see cref="PlaywrightAgentBrowser.LaunchOptions"/>) against the Autopilot card's own bUnit-rendered markup and
    /// the real studio.css. Also measures #313's own mechanism directly: each pane's transcript is a
    /// readable, non-collapsed region, and the first (25 tool calls, a long reply) overflows and scrolls.
    /// Like the Infrastructure live tiers, it runs only where <c>FRAVASHI_BROWSER_LIVE=1</c> is set and is
    /// skipped with its reason everywhere else.
    /// </summary>
    [BrowserLiveFact]
    public async Task Two_running_blocks_text_boxes_never_intersect_in_a_real_browser()
    {
        var first = await CreateItemAsync("Build the first thing");
        var second = await CreateItemAsync("Build the second thing");
        // No BaseWorkspaceId: each item's pane then resolves ITS OWN session's own workspace (AutopilotCard's
        // RunningWorkspaceId fallback), which is what lets the two live sessions below sit in different
        // workspaces rather than colliding on the registry's one-live-session-per-workspace rule.
        var run = AutopilotRun.Start(
            Guid.NewGuid(), ProjectId.Default, AutopilotDispatchSettings.Default, [first.Id, second.Id], DateTimeOffset.UtcNow);
        run = run.With(run.Queue[0] with { Stage = AutopilotItemStage.Building, StartedUtc = DateTimeOffset.UtcNow });
        run = run.With(run.Queue[1] with
        {
            Stage = AutopilotItemStage.Building,
            StartedUtc = DateTimeOffset.UtcNow,
            WaitingForCapacityReason = "The concurrency cap is reached: 2 of 2 session(s) are already running against this repository. Stop one of them, or raise the cap in Settings > Agents > Autopilot concurrency.",
        });
        await _runs.SaveAsync(run);

        using var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(5));
        var runner = new EmittingSessionRunner();
        using var registry = new LiveSessionRegistry(runner, new RecordingSessionStore(), broker);
        var firstSession = registry.Start(new SessionStartRequest("w1", "/workspaces/w1", "build the first thing"));
        var secondSession = registry.Start(new SessionStartRequest("w2", "/workspaces/w2", "build the second thing"));
        var builderSessions = new AutopilotBuilderSessions(broker);
        builderSessions.Bind(first.Id, firstSession.Id);
        builderSessions.Bind(second.Id, secondSession.Id);

        // A real conversation accumulates many tool calls and long replies; a near-empty one (what the
        // Silent runner gives every other test here) never grows the pane enough to show #313's defect.
        // Acceptance bullet 2 asks for a session pane in EACH running block, so both get one; the second
        // item's WaitingForCapacityReason (set directly on the run above, not reached through the engine)
        // stays too, so the intersection check also covers a block carrying its capacity sentence AND a
        // pane together, the fullest case the page can show.
        for (var i = 0; i < 25; i++)
        {
            runner.Emit(firstSession.Id, new ToolCallStarted(DateTimeOffset.UtcNow, $"toolu_{i}", "Read", $"/workspaces/w1/src/File{i}.cs"));
            runner.Emit(firstSession.Id, new ToolCallCompleted(DateTimeOffset.UtcNow, $"toolu_{i}", "Read", true,
                string.Concat(Enumerable.Repeat($"line {i} of the file, long enough to wrap across the panel's width\n", 4))));
        }

        runner.Emit(firstSession.Id, new MessageEmitted(DateTimeOffset.UtcNow, TranscriptRole.Assistant,
            string.Concat(Enumerable.Repeat("A long streamed reply, several sentences each, the kind a real turn produces. ", 40))));

        for (var i = 0; i < 10; i++)
        {
            runner.Emit(secondSession.Id, new ToolCallStarted(DateTimeOffset.UtcNow, $"toolu_{i}", "Grep", $"pattern{i}"));
            runner.Emit(secondSession.Id, new ToolCallCompleted(DateTimeOffset.UtcNow, $"toolu_{i}", "Grep", true, $"match {i}\n"));
        }

        using var ctx = Context(builderSessions);
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var configuration = new ConfigurationBuilder().Build();
        ctx.Services.AddSingleton(registry);
        ctx.Services.AddSingleton<IWorkspaceStore>(new FakeWorkspaceStore());
        ctx.Services.AddSingleton<ISessionWorkItems>(new SqliteSessionWorkItems(_factory, TimeProvider.System));
        ctx.Services.AddSingleton<ISessionPlans>(new SqliteSessionPlans(_factory, TimeProvider.System));
        var wiki = new SqliteWikiLibrary(_factory, TimeProvider.System, ProjectId.Default);
        ctx.Services.AddSingleton<IWikiDocuments>(wiki);
        ctx.Services.AddSingleton<ISessionGovernance>(new SessionGovernance(
            new SqliteSessionWorkItems(_factory, TimeProvider.System),
            new SqliteSessionPlans(_factory, TimeProvider.System),
            _resolver,
            wiki,
            new SqliteSessionChangedFiles(_factory, TimeProvider.System)));
        ctx.Services.AddSingleton(new WorkspaceNodeAdvisor(
            new NodePathProvisioner(configuration), new FakeNodeCommandRunner()));
        ctx.Services.AddSingleton(new NodeProvisionCoordinator(new FakeNodeProvisioner()));
        ctx.Services.AddSingleton<IAgentBrowser>(new FakeAgentBrowser());
        ctx.Services.AddSingleton(new Fravashi.Infrastructure.Rendering.MarkdownPreviewRenderer());
        ctx.Services.AddLogging();

        var cut = ctx.Render<AutopilotCard>();
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(2, cut.FindAll("[data-testid=autopilot-running-item-block]").Count);
            Assert.Equal(2, cut.FindAll("[data-testid=session-pane]").Count);
            Assert.NotEmpty(cut.FindAll("[data-testid=autopilot-running-item-waiting-capacity]"));
            Assert.Equal(35, cut.FindAll("[data-testid=tool-call]").Count);
        });

        var page = Shell(cut.Markup);
        var port = FreePort();
        var url = $"http://127.0.0.1:{port}/";
        using var server = new HttpListener();
        server.Prefixes.Add(url);
        server.Start();
        _ = Task.Run(async () =>
        {
            while (server.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await server.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }

                var path = context.Request.Url?.AbsolutePath ?? "/";
                var (body, contentType) = path switch
                {
                    "/studio.css" => (StudioCss(), "text/css; charset=utf-8"),
                    "/page" => (page, "text/html; charset=utf-8"),
                    _ => ((string?)null, "text/plain"),
                };

                var bytes = Encoding.UTF8.GetBytes(body ?? "not found");
                context.Response.StatusCode = body is null ? 404 : 200;
                context.Response.ContentType = contentType;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        });

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(PlaywrightAgentBrowser.LaunchOptions(jitless: true));
        var browserPage = await browser.NewPageAsync();
        await browserPage.SetViewportSizeAsync(1280, 900);
        await browserPage.GotoAsync(url + "page");
        await browserPage.WaitForSelectorAsync("[data-testid=autopilot-running-item-block]");

        // #313's own mechanism, measured directly on the TRANSCRIPT itself, not merely the wrapper the
        // fix bounds: without a definite pane height, .session-transcript-wrap's flex: 1 1000 0 basis
        // contributes nothing to an indefinite column, so the transcript collapses to a sliver instead of
        // a readable, scrollable region. Acceptance bullet 2 asks for a pane in EACH running block, so
        // both are measured, not only the first; the first (25 tool calls, a long reply) also has to
        // actually overflow and scroll internally rather than being clipped to nothing.
        var panes = await browserPage.QuerySelectorAllAsync("[data-testid=autopilot-running-item-pane]");
        Assert.Equal(2, panes.Count);
        foreach (var pane in panes)
        {
            var transcript = await pane.QuerySelectorAsync("[data-testid=session-transcript]");
            var box = await transcript!.BoundingBoxAsync();
            Assert.True(box!.Height > 150, $"expected the transcript to be a readable, scrollable region, found height {box.Height}");
        }

        var firstTranscript = await panes[0].QuerySelectorAsync("[data-testid=session-transcript]");
        var scrolls = await firstTranscript!.EvaluateAsync<bool>("el => el.scrollHeight > el.clientHeight");
        Assert.True(scrolls, "expected the first pane's transcript (25 tool calls, a long reply) to overflow and scroll internally");

        var json = await browserPage.EvaluateAsync<string>("""
            () => {
                const targets = [
                    '[data-testid=autopilot-running-item-label]',
                    '[data-testid=autopilot-running-item-waiting-capacity]',
                    '[data-testid=autopilot-limit-money-per-item]',
                    '[data-testid=autopilot-limit-failed-reviews]',
                    '[data-testid=autopilot-limit-time-per-item]',
                ];
                const blocks = document.querySelectorAll('[data-testid=autopilot-running-item-block]');
                const result = {};
                blocks.forEach((block, index) => {
                    const boxes = [];
                    for (const selector of targets) {
                        for (const el of block.querySelectorAll(selector)) {
                            const r = el.getBoundingClientRect();
                            boxes.push({ label: 'block-' + index + ' ' + selector, x: r.x, y: r.y, width: r.width, height: r.height });
                        }
                    }
                    result['block-' + index] = boxes;
                });
                return JSON.stringify(result);
            }
            """);

        var boxesByBlock = JsonSerializer.Deserialize<Dictionary<string, Box[]>>(json)!;
        var block0 = boxesByBlock["block-0"];
        var block1Boxes = boxesByBlock["block-1"];

        Assert.NotEmpty(block0);
        Assert.NotEmpty(block1Boxes);
        // A selector that stops matching (e.g. the label losing its data-testid) must fail this test
        // rather than silently drop out of the intersection check below.
        Assert.Contains(block0, b => b.Label.Contains("autopilot-running-item-label"));
        Assert.Contains(block1Boxes, b => b.Label.Contains("autopilot-running-item-label"));
        Assert.Contains(block1Boxes, b => b.Label.Contains("waiting-capacity"));

        // Every box measured, from both blocks: #313's own screenshot showed the SECOND block's own
        // heading, limit lines and capacity sentence drawn over ONE ANOTHER, not only over the first
        // block, so the check compares every pair, not only across the two blocks. A box collapsed to
        // zero size would pass the strict inequalities below for free, so each box is asserted real first.
        var all = block0.Concat(block1Boxes).ToArray();
        foreach (var box in all)
        {
            Assert.True(box.Width > 0 && box.Height > 0, $"{box.Label} must be a real, non-collapsed box, found {box.Width}x{box.Height}");
        }

        for (var i = 0; i < all.Length; i++)
        {
            for (var j = i + 1; j < all.Length; j++)
            {
                var a = all[i];
                var b = all[j];
                Assert.False(
                    a.Intersects(b),
                    $"{a.Label} at ({a.X},{a.Y},{a.Width}x{a.Height}) must not overlap {b.Label} at ({b.X},{b.Y},{b.Width}x{b.Height})");
            }
        }
    }
}
