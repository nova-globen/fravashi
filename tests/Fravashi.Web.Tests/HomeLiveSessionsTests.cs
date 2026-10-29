using System.Collections.Concurrent;
using System.Threading.Channels;
using Bunit;
using Fravashi.Application.Persistence;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Bridge;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Web.Components.Dashboard;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 35: the home card listing every LIVE agent session across every workspace. These render
/// against the real registry and approval broker (only the CLI is a stand-in, one event stream per
/// session), so a session waiting on an approval is waiting on a real parked call and the card's live
/// updates come from the registry's own events, never from a re-render the test forces.
/// </summary>
public class HomeLiveSessionsTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A questions document in the wire shape the CLI produces (the same shape SessionsUiTests drives the
    /// pane's question block with).
    /// </summary>
    private const string CachingQuestion =
        "{\"version\":\"1\",\"kind\":\"questions\",\"message\":\"Where should cached responses live?\","
        + "\"questions\":[{\"id\":\"storage\",\"question\":\"Where should cached responses live?\","
        + "\"options\":[{\"label\":\"In memory\"},{\"label\":\"Redis\"}]}]}";

    /// <summary>One event stream PER SESSION, so two workspaces' sessions can be driven independently.</summary>
    private sealed class PerSessionRunner : IAgentSessionRunner
    {
        private readonly ConcurrentDictionary<AgentSessionId, Channel<AgentEvent>> _streams = new();

        public IAsyncEnumerable<AgentEvent> RunAsync(
            AgentSessionRequest request, CancellationToken cancellationToken = default) =>
            Stream(request.SessionId).Reader.ReadAllAsync(cancellationToken);

        public Task<bool> SendAsync(AgentSessionId sessionId, string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public bool Stop(AgentSessionId sessionId) => true;

        public bool ChangeModel(AgentSessionId sessionId, string? model) => true;

        public void Emit(AgentSessionId sessionId, AgentEvent e) => Stream(sessionId).Writer.TryWrite(e);

        private Channel<AgentEvent> Stream(AgentSessionId id) =>
            _streams.GetOrAdd(id, _ => Channel.CreateUnbounded<AgentEvent>());
    }

    private sealed class FakeWorkspaceStore : IWorkspaceStore
    {
        private readonly List<WorkspaceRecord> _records = [];

        public IReadOnlyList<WorkspaceRecord> Recent(int max = 10) => _records.Take(max).ToList();

        public WorkspaceRecord? Get(string id) => _records.FirstOrDefault(r => r.Id == id);

        public void Touch(string id, string name, string rootPath)
        {
            _records.RemoveAll(r => r.Id == id);
            _records.Insert(0, new WorkspaceRecord(id, name, rootPath, DateTimeOffset.UtcNow));
        }

        public void Remove(string id) => _records.RemoveAll(r => r.Id == id);

        /// <summary>Work item #350: puts a folder in a project, as Settings > Projects does.</summary>
        public void Bind(string id, string project)
        {
            var index = _records.FindIndex(r => r.Id == id);
            _records[index] = _records[index] with { ProjectId = project };
        }

        /// <summary>Work item #350: an autopilot worktree of <paramref name="repositoryRoot"/>.</summary>
        public void AddWorktree(string id, string repositoryRoot, string number) =>
            _records.Add(new WorkspaceRecord(
                id, number, repositoryRoot + ".fravashi-worktrees\\" + number, DateTimeOffset.UtcNow));
    }

    private sealed class ScriptedBridgeClient(
        BridgeWorkspaceContext context, Func<string, BridgeRegistration> onRegister)
        : BridgeClient(new HttpClient(), context)
    {
        public List<string> Registered { get; } = [];

        public override Task<BridgeRegistration> RegisterWorkspaceAsync(string path, CancellationToken ct)
        {
            Registered.Add(path);
            return Task.FromResult(onRegister(path));
        }
    }

    private sealed class Harness : IDisposable
    {
        public required BunitContext Ctx { get; init; }
        public required PerSessionRunner Runner { get; init; }
        public required AgentApprovalBroker Broker { get; init; }
        public required LiveSessionRegistry Registry { get; init; }
        public required FakeWorkspaceStore Workspaces { get; init; }
        public required ScriptedBridgeClient Bridge { get; init; }

        public NavigationManager Nav => Ctx.Services.GetRequiredService<NavigationManager>();

        public LiveAgentSession Start(string workspaceId, string prompt) =>
            Registry.Start(new SessionStartRequest(workspaceId, $"/workspaces/{workspaceId}", prompt));

        public void Dispose()
        {
            Ctx.Dispose();
            Registry.Dispose();
            Broker.Dispose();
        }
    }

    private static Harness Context(
        string? activeWorkspaceId = "w1", Func<string, BridgeRegistration>? onRegister = null)
    {
        var ctx = new BunitContext();
        var values = new Dictionary<string, string?>
        {
            [BridgeWorkspaceContext.UrlKey] = "http://127.0.0.1:9",
            [BridgeWorkspaceContext.TokenKey] = "t",
        };
        if (activeWorkspaceId is not null)
        {
            values[BridgeWorkspaceContext.ActiveIdKey] = activeWorkspaceId;
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var bridgeContext = new BridgeWorkspaceContext(configuration);
        var bridge = new ScriptedBridgeClient(bridgeContext, onRegister ?? (_ => BridgeRegistration.Missing));
        var workspaces = new FakeWorkspaceStore();
        workspaces.Touch("w1", "widgets", @"C:\repos\widgets");
        workspaces.Touch("w2", "gadgets", @"C:\repos\gadgets");

        var runner = new PerSessionRunner();
        var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(30));
        var registry = new LiveSessionRegistry(runner, new RecordingSessionStore(), broker);

        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(configuration);
        ctx.Services.AddSingleton(bridgeContext);
        ctx.Services.AddSingleton<BridgeClient>(bridge);
        ctx.Services.AddSingleton<IWorkspaceStore>(workspaces);
        ctx.Services.AddSingleton(registry);
        // Work item #350: the card's filters read which sessions the autopilot runs, and the projects there are.
        ctx.Services.AddSingleton<IAgentApprovalBroker>(broker);
        ctx.Services.AddSingleton<Fravashi.Application.Projects.IProjectStore>(
            new NamedProjectStore(("default", "Fravashi"), ("p2", "Gadgets project")));
        ctx.Services.AddLogging();

        return new Harness
        {
            Ctx = ctx,
            Runner = runner,
            Broker = broker,
            Registry = registry,
            Workspaces = workspaces,
            Bridge = bridge,
        };
    }

    private static IRenderedComponent<LiveSessionsCard> Card(Harness harness) =>
        harness.Ctx.Render<LiveSessionsCard>();

    private static AngleSharp.Dom.IElement RowFor(IRenderedComponent<LiveSessionsCard> cut, LiveAgentSession session) =>
        cut.Find($"[data-testid='live-session-row'][data-session-id='{session.Id.Value:D}']");

    private static TurnCompleted Asked(string json) =>
        new(DateTimeOffset.UtcNow, null, false, "completed", AgentTurnDocumentReader.Read(json));

    // ---- what the card lists -------------------------------------------------------------------

    [Fact]
    public void With_no_live_session_the_card_says_so_rather_than_rendering_a_blank_box()
    {
        using var harness = Context();

        var cut = Card(harness);

        Assert.Contains("No agent is running", cut.Find("[data-testid='live-sessions-empty']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='live-session-row']"));
        Assert.Empty(cut.FindAll("[data-testid='live-sessions-count']"));
    }

    [Fact]
    public void Every_live_session_is_listed_across_workspaces_with_its_folder_and_what_it_is_doing()
    {
        using var harness = Context();
        var first = harness.Start("w1", "write the readme");
        var second = harness.Start("w2", "fix the build");

        var cut = Card(harness);

        var rows = cut.FindAll("[data-testid='live-session-row']");
        Assert.Equal(2, rows.Count);
        var firstRow = RowFor(cut, first);
        Assert.Contains("write the readme", firstRow.TextContent);
        Assert.Contains("widgets", firstRow.TextContent);
        Assert.Contains("Sent to the agent", firstRow.TextContent);
        var secondRow = RowFor(cut, second);
        Assert.Contains("fix the build", secondRow.TextContent);
        Assert.Contains("gadgets", secondRow.TextContent);
        Assert.Equal("none", secondRow.GetAttribute("data-waiting"));
        Assert.Contains("2 running", cut.Find("[data-testid='live-sessions-count']").TextContent);
    }

    [Fact]
    public void Every_row_names_its_project_beside_its_folder()
    {
        // Item #373: the card spans every project, so a row says whose it is; a folder in no project says so.
        using var harness = Context();
        harness.Workspaces.Bind("w2", "p2");
        var unbound = harness.Start("w1", "write the readme");
        var bound = harness.Start("w2", "fix the build");

        var cut = Card(harness);

        Assert.Equal("widgets (not set up)", RowFor(cut, unbound).QuerySelector(".live-session-workspace")!.TextContent);
        Assert.Equal("Gadgets project · gadgets", RowFor(cut, bound).QuerySelector(".live-session-workspace")!.TextContent);
    }

    [Fact]
    public void A_session_whose_workspace_has_no_recents_row_still_lists_under_its_id()
    {
        using var harness = Context();
        var session = harness.Start("w-unknown", "tidy up");

        var cut = Card(harness);

        Assert.Contains("w-unknown", RowFor(cut, session).TextContent);
    }

    // ---- live updates, with no re-render forced by the test --------------------------------------

    [Fact]
    public void A_session_that_starts_after_the_card_rendered_appears_and_an_ended_one_leaves()
    {
        using var harness = Context();
        var cut = Card(harness);
        Assert.NotEmpty(cut.FindAll("[data-testid='live-sessions-empty']"));

        var session = harness.Start("w2", "fix the build");

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='live-session-row']")), Patience);

        harness.Runner.Emit(session.Id, new SessionEnded(DateTimeOffset.UtcNow, AgentSessionState.Completed, null));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='live-sessions-empty']")), Patience);
    }

    [Fact]
    public void The_status_line_follows_the_turn_as_the_agent_streams()
    {
        using var harness = Context();
        var session = harness.Start("w1", "write the readme");
        var cut = Card(harness);

        harness.Runner.Emit(session.Id, new TurnActivity(DateTimeOffset.UtcNow, AgentTurnPhase.Thinking));
        cut.WaitForAssertion(() => Assert.Contains("Thinking", RowFor(cut, session).TextContent), Patience);

        harness.Runner.Emit(session.Id, new TurnCompleted(DateTimeOffset.UtcNow, "done", false, "completed"));
        cut.WaitForAssertion(
            () => Assert.Contains("Ready for your next message", RowFor(cut, session).TextContent), Patience);
    }

    // ---- waiting on the owner --------------------------------------------------------------------

    /// <summary>Work item #356: the card says what a waiting studio tool call does, never its raw mcp__ name.</summary>
    [Fact]
    public async Task A_session_waiting_on_a_studio_tool_names_the_call_in_words()
    {
        using var harness = Context();
        var blocked = harness.Start("w2", "fix the build");
        harness.Broker.RegisterSession(blocked.Id);
        var cut = Card(harness);

        var decision = harness.Broker.RequestAsync(
            blocked.Id, "mcp__fravashi__work_item_update", "toolu_1",
            new Dictionary<string, string> { ["number"] = "23", ["description"] = "x" });

        cut.WaitForAssertion(() =>
        {
            var row = RowFor(cut, blocked);
            Assert.Contains("Waiting on your approval: Update #23: description", row.TextContent);
            Assert.DoesNotContain("mcp__fravashi__", cut.Markup);
        }, Patience);

        var pending = harness.Registry.Find(blocked.Id)!.Pending.Single();
        Assert.True(harness.Registry.Answer(pending.RequestId, approved: false, reason: "no"));
        Assert.False((await decision).Approved);
    }

    [Fact]
    public async Task A_session_waiting_on_an_approval_is_visibly_distinct_and_offers_the_answer()
    {
        using var harness = Context();
        var working = harness.Start("w1", "write the readme");
        var blocked = harness.Start("w2", "fix the build");
        harness.Broker.RegisterSession(blocked.Id);
        var cut = Card(harness);
        Assert.Empty(cut.FindAll("[data-testid='live-session-answer']"));

        var decision = harness.Broker.RequestAsync(
            blocked.Id, "Bash", "toolu_1", new Dictionary<string, string> { ["command"] = "rm -rf build" });

        cut.WaitForAssertion(() =>
        {
            var row = RowFor(cut, blocked);
            Assert.Equal("approval", row.GetAttribute("data-waiting"));
            Assert.Contains("live-session-waiting", row.ClassList);
            Assert.Contains("Waiting on your approval: Bash", row.TextContent);
            Assert.NotNull(row.QuerySelector("[data-testid='live-session-answer']"));
        }, Patience);

        // The working session is not drawn as waiting, and the blocked one leads the list.
        var workingRow = RowFor(cut, working);
        Assert.Equal("none", workingRow.GetAttribute("data-waiting"));
        Assert.DoesNotContain("live-session-waiting", workingRow.ClassList);
        Assert.Null(workingRow.QuerySelector("[data-testid='live-session-answer']"));
        Assert.Equal(
            blocked.Id.Value.ToString("D"),
            cut.FindAll("[data-testid='live-session-row']")[0].GetAttribute("data-session-id"));
        Assert.Contains("1 waiting on you", cut.Find("[data-testid='live-sessions-count']").TextContent);

        // Answered in the conversation: the card lets go of the waiting state on its own.
        var pending = harness.Registry.Find(blocked.Id)!.Pending.Single();
        Assert.True(harness.Registry.Answer(pending.RequestId, approved: true, reason: "ok"));
        Assert.True((await decision).Approved);

        cut.WaitForAssertion(
            () => Assert.Equal("none", RowFor(cut, blocked).GetAttribute("data-waiting")), Patience);
    }

    [Fact]
    public async Task A_session_whose_turn_asked_a_question_is_waiting_until_a_user_turn_answers_it()
    {
        using var harness = Context();
        var session = harness.Start("w2", "add caching to the api");
        var cut = Card(harness);

        harness.Runner.Emit(session.Id, Asked(CachingQuestion));

        cut.WaitForAssertion(() =>
        {
            var row = RowFor(cut, session);
            Assert.Equal("question", row.GetAttribute("data-waiting"));
            Assert.Contains("live-session-waiting", row.ClassList);
            Assert.Contains("Waiting on your answer: Where should cached responses live?", row.TextContent);
            Assert.Contains("Answer", row.QuerySelector("[data-testid='live-session-answer']")!.TextContent);
        }, Patience);

        // The answer is an ordinary user turn (ADR 0022), however it was given.
        Assert.True(await harness.Registry.SendAsync(session.Id, "In memory"));

        cut.WaitForAssertion(
            () => Assert.Equal("none", RowFor(cut, session).GetAttribute("data-waiting")), Patience);
    }

    // ---- one click to the conversation -----------------------------------------------------------

    [Fact]
    public async Task Answering_a_session_in_this_workspace_opens_it_without_switching()
    {
        using var harness = Context(activeWorkspaceId: "w1");
        var session = harness.Start("w1", "add caching to the api");
        var cut = Card(harness);
        harness.Runner.Emit(session.Id, Asked(CachingQuestion));
        cut.WaitForAssertion(
            () => Assert.NotEmpty(cut.FindAll("[data-testid='live-session-answer']")), Patience);

        await cut.InvokeAsync(() => cut.Find("[data-testid='live-session-answer']").Click());

        Assert.EndsWith($"/sessions/{session.Id.Value:D}", harness.Nav.Uri);
        Assert.Empty(harness.Bridge.Registered);
    }

    [Fact]
    public async Task Answering_a_session_in_another_workspace_switches_the_window_through_the_reopen_path()
    {
        using var harness = Context(
            activeWorkspaceId: "w1",
            onRegister: path => BridgeRegistration.Registered(new BridgeWorkspaceDto("w2", "gadgets", path)));
        var session = harness.Start("w2", "fix the build");
        harness.Broker.RegisterSession(session.Id);
        var cut = Card(harness);
        _ = harness.Broker.RequestAsync(
            session.Id, "Bash", "toolu_1", new Dictionary<string, string> { ["command"] = "make" });
        cut.WaitForAssertion(
            () => Assert.NotEmpty(cut.FindAll("[data-testid='live-session-answer']")), Patience);

        await cut.InvokeAsync(() => cut.Find("[data-testid='live-session-answer']").Click());

        cut.WaitForAssertion(() =>
            Assert.EndsWith($"/sessions/{session.Id.Value:D}?ws=w2", harness.Nav.Uri), Patience);
        Assert.Equal([@"C:\repos\gadgets"], harness.Bridge.Registered);
    }

    [Fact]
    public async Task A_session_whose_folder_is_gone_says_so_instead_of_navigating()
    {
        using var harness = Context(activeWorkspaceId: "w1", onRegister: _ => BridgeRegistration.Missing);
        var session = harness.Start("w2", "fix the build");
        var cut = Card(harness);
        var before = harness.Nav.Uri;

        await cut.InvokeAsync(() => cut.Find("[data-testid='live-session']").Click());

        cut.WaitForAssertion(() =>
            Assert.Contains("was not found", cut.Find("[data-testid='live-session-message']").TextContent), Patience);
        Assert.Equal(before, harness.Nav.Uri);
        Assert.NotNull(session);
    }

    // ---- work item #350: the head's two filters ------------------------------------------------------------------

    [Fact]
    public async Task Autopilot_sessions_are_listed_until_the_reader_leaves_them_out()
    {
        using var harness = Context();
        harness.Workspaces.AddWorktree("wt-3", @"C:\repos\widgets", "3");
        var mine = harness.Start("w1", "write the readme");
        var marked = harness.Start("w2", "build item 12");
        harness.Broker.SetAutopilot(marked.Id, true);
        var inWorktree = harness.Start("wt-3", "build item 3");
        var cut = Card(harness);

        // On by default: nothing that was listed before the toggle existed is hidden until the reader asks.
        Assert.True(cut.Find("[data-testid='live-sessions-autopilot']").HasAttribute("checked"));
        Assert.Equal(3, cut.FindAll("[data-testid='live-session-row']").Count);

        await cut.InvokeAsync(() => cut.Find("[data-testid='live-sessions-autopilot']").Change(false));

        var row = Assert.Single(cut.FindAll("[data-testid='live-session-row']"));
        Assert.Equal(mine.Id.Value.ToString("D"), row.GetAttribute("data-session-id"));
        Assert.Contains("1 running", cut.Find("[data-testid='live-sessions-count']").TextContent);
        Assert.NotNull(inWorktree);
    }

    [Fact]
    public async Task The_project_filter_keeps_the_sessions_whose_folder_is_in_that_project()
    {
        using var harness = Context();
        harness.Workspaces.Bind("w2", "p2");
        var widgets = harness.Start("w1", "write the readme");
        // Item #373: a folder is in no project until it is put in one; this one is in the default project.
        harness.Workspaces.Bind("w1", "default");
        var gadgets = harness.Start("w2", "fix the build");
        var cut = Card(harness);
        Assert.Equal(2, cut.FindAll("[data-testid='live-session-row']").Count);

        await cut.InvokeAsync(() => cut.Find("[data-testid='live-sessions-project-trigger']").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid='live-sessions-project-opt-p2']").Click());

        var row = Assert.Single(cut.FindAll("[data-testid='live-session-row']"));
        Assert.Equal(gadgets.Id.Value.ToString("D"), row.GetAttribute("data-session-id"));

        await cut.InvokeAsync(() => cut.Find("[data-testid='live-sessions-project-trigger']").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid='live-sessions-project-opt-default']").Click());

        row = Assert.Single(cut.FindAll("[data-testid='live-session-row']"));
        Assert.Equal(widgets.Id.Value.ToString("D"), row.GetAttribute("data-session-id"));
    }

    [Fact]
    public async Task Filtering_every_session_away_says_so_rather_than_that_nothing_runs()
    {
        using var harness = Context();
        var marked = harness.Start("w2", "build item 12");
        harness.Broker.SetAutopilot(marked.Id, true);
        var cut = Card(harness);

        await cut.InvokeAsync(() => cut.Find("[data-testid='live-sessions-autopilot']").Change(false));

        Assert.Contains("1 running", cut.Find("[data-testid='live-sessions-filtered']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='live-sessions-empty']"));
    }
}
