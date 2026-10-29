using Bunit;
using Fravashi.Application.Notifications;
using Fravashi.Application.Persistence;
using Fravashi.Application.Projects;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Bridge;
using Fravashi.Infrastructure.Notifications;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Web.Components.Dashboard;
using Fravashi.Web.Components.Pages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Work item #350: Home is the product's dashboard. No page bar and no folder boxes (those moved to the start dialog,
/// <see cref="StartDialogTests"/>); the conversation box on top, then the status row, Progress, the insight cards and
/// the recent conversations. What each older card says is driven by its own tests.
/// </summary>
public class HomeDashboardTests
{
    private static readonly ProjectId Other = new("other");

    private static BunitContext PageContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddNoUpdates();
        ctx.Services.AddSingleton<INotificationHistory>(new InMemoryNotificationHistory());
        ctx.Services.AddUnboundHomeSurface();
        return ctx;
    }

    /// <summary>The same page in a window that has the folder <c>ws1</c> open.</summary>
    private static BunitContext FolderContext(StubWorkspaceAgentSync? sync = null)
    {
        var ctx = PageContext();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [BridgeWorkspaceContext.UrlKey] = "http://127.0.0.1:9",
            [BridgeWorkspaceContext.TokenKey] = "t",
            [BridgeWorkspaceContext.ActiveIdKey] = "ws1",
        }).Build();
        var workspace = new BridgeWorkspaceContext(configuration);
        ctx.Services.AddSingleton(workspace);
        ctx.Services.AddSingleton(new BridgeClient(new HttpClient(), workspace));
        ctx.Services.AddSingleton<IWorkspaceStore>(new OneFolderStore());
        ctx.Services.AddSingleton<Fravashi.Application.AgentSync.IWorkspaceAgentSync>(sync ?? new StubWorkspaceAgentSync());
        return ctx;
    }

    private sealed class OneFolderStore : IWorkspaceStore
    {
        private static readonly WorkspaceRecord Widgets = new("ws1", "widgets", @"C:\repos\widgets", DateTimeOffset.UtcNow);

        public IReadOnlyList<WorkspaceRecord> Recent(int max = 10) => [Widgets];

        public WorkspaceRecord? Get(string id) => id == Widgets.Id ? Widgets : null;

        public void Touch(string id, string name, string rootPath)
        {
        }

        public void Remove(string id)
        {
        }
    }

    // ---- what is gone (1.png, 2.png) --------------------------------------------------------------------------------

    [Fact]
    public void Home_has_no_page_bar_and_none_of_the_four_folder_boxes()
    {
        using var ctx = FolderContext();

        var cut = ctx.Render<Home>();

        Assert.Empty(cut.FindAll(".studio-page-bar"));
        Assert.Empty(cut.FindAll("h1"));
        Assert.Equal("Home", cut.Find("[data-testid=home-heading]").TextContent);
        foreach (var box in new[] { "home-recents", "home-workspace", "home-open-folder", "home-clone" })
        {
            Assert.Empty(cut.FindAll($"[data-testid='{box}']"));
        }

        foreach (var words in new[] { "Open another folder", "Current workspace", "Open Folder", "Clone a repository", "Recent folders" })
        {
            Assert.DoesNotContain(words, cut.Markup, StringComparison.Ordinal);
        }

        Assert.Empty(cut.FindAll(".notification-center-button"));
    }

    // ---- what is there, and in what order (7.png) -------------------------------------------------------------------

    [Fact]
    public void Home_lays_the_dashboard_out_in_the_owners_arrangement()
    {
        using var ctx = PageContext();

        var cut = ctx.Render<Home>();

        // Work item #363: the built-in layout is the arrangement the owner made on Home and handed over with
        // "Copy layout" (2026-10-23); HomeLayoutTests drives arranging the cards.
        var order = new[]
        {
            "home-prompt", "autopilot-card", "progress-card", "recent-dispatches", "live-sessions", "updates-card",
            "recent-conversations", "insight-time", "insight-spend", "insight-throughput", "insight-repo",
        };
        var positions = order.Select(id => cut.Markup.IndexOf($"data-testid=\"{id}\"", StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.OrderBy(p => p), positions);

        // The autopilot beside Progress, the dispatches beside the live sessions (both lists up to four rows before
        // they scroll), and the status cards, the recent conversations and the insight cards in thirds.
        Assert.Contains("--home-w: 6; --home-h: 1", cut.Find("[data-testid=home-card-autopilot]").GetAttribute("style"));
        Assert.Contains("--home-w: 6; --home-h: 4", cut.Find("[data-testid=home-card-recent-dispatches]").GetAttribute("style"));
        Assert.Contains("--home-w: 6; --home-h: 4", cut.Find("[data-testid=home-card-live-sessions]").GetAttribute("style"));
        Assert.Contains("--home-w: 4; --home-h: 1", cut.Find("[data-testid=home-card-updates]").GetAttribute("style"));
        Assert.Contains("Wiki", cut.Find("[data-testid=home-card-wiki]").TextContent);
        Assert.Contains("--home-w: 4; --home-h: 1", cut.Find("[data-testid=home-card-wiki]").GetAttribute("style"));
        Assert.Contains("--home-w: 8; --home-h: 1", cut.Find("[data-testid=home-card-recent-conversations]").GetAttribute("style"));
        foreach (var card in new[] { "agent-spend", "throughput", "time-spent", "repository-health" })
        {
            Assert.Contains("--home-w: 4;", cut.Find($"[data-testid=home-card-{card}]").GetAttribute("style"));
        }
    }

    [Fact]
    public void Home_renders_the_recent_conversations_card()
    {
        // Backlog 34. HomeRecentConversationsTests drives what it lists and what it does.
        using var ctx = PageContext();

        var cut = ctx.Render<Home>();

        Assert.NotNull(cut.Find("[data-testid='recent-conversations']"));
        Assert.Contains("Recent conversations", cut.Markup);
        // The repository clone card retired with the clone model (plan 2c decision 6).
        Assert.DoesNotContain("Clone now", cut.Markup);
    }

    // ---- the studio assistant's box (work item #359) -------------------------------------------------------------------

    /// <summary>A runner whose conversations stay open until the test says otherwise, and that records every turn.</summary>
    /// <summary>
    /// Waits for something the fake runner does off the render path (bunit's WaitForAssertion only re-checks on a
    /// render, so it can check once and time out). A read that races the runner's own write counts as not yet.
    /// </summary>
    private static async Task PollUntilAsync(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            bool met;
            try
            {
                met = condition();
            }
            catch (InvalidOperationException)
            {
                met = false;
            }

            if (met)
            {
                return;
            }

            if (DateTime.UtcNow > until)
            {
                throw new Xunit.Sdk.XunitException("Timed out waiting for " + what + ".");
            }

            await Task.Delay(20);
        }
    }

    private sealed class HoldingRunner : Fravashi.Application.Sessions.IAgentSessionRunner
    {
        private readonly Dictionary<Fravashi.Domain.AgentSessions.AgentSessionId, System.Threading.Channels.Channel<Fravashi.Domain.AgentSessions.AgentEvent>> _streams = [];

        public List<Fravashi.Application.Sessions.AgentSessionRequest> Requests { get; } = [];

        public List<string> Sent { get; } = [];

        public Fravashi.Application.Sessions.AgentSessionRequest? LastRequest => Requests.LastOrDefault();

        public IAsyncEnumerable<Fravashi.Domain.AgentSessions.AgentEvent> RunAsync(
            Fravashi.Application.Sessions.AgentSessionRequest request, CancellationToken cancellationToken = default)
        {
            var stream = System.Threading.Channels.Channel.CreateUnbounded<Fravashi.Domain.AgentSessions.AgentEvent>();
            lock (_streams)
            {
                Requests.Add(request);
                _streams[request.SessionId] = stream;
            }

            return stream.Reader.ReadAllAsync(cancellationToken);
        }

        public void Emit(Fravashi.Domain.AgentSessions.AgentSessionId id, Fravashi.Domain.AgentSessions.AgentEvent e)
        {
            lock (_streams)
            {
                _streams[id].Writer.TryWrite(e);
            }
        }

        public Task<bool> SendAsync(
            Fravashi.Domain.AgentSessions.AgentSessionId sessionId, string text, CancellationToken cancellationToken = default)
        {
            Sent.Add(text);
            return Task.FromResult(true);
        }

        public bool Stop(Fravashi.Domain.AgentSessions.AgentSessionId sessionId)
        {
            Emit(sessionId, new Fravashi.Domain.AgentSessions.SessionEnded(
                DateTimeOffset.UtcNow, Fravashi.Domain.AgentSessions.AgentSessionState.Cancelled, null));
            lock (_streams)
            {
                _streams[sessionId].Writer.TryComplete();
            }

            return true;
        }

        public bool ChangeModel(Fravashi.Domain.AgentSessions.AgentSessionId sessionId, string? model) => true;
    }

    private static (BunitContext Context, HoldingRunner Runner, FakeConfigurationStore Config) AssistantContext()
    {
        var runner = new HoldingRunner();
        var ctx = FolderContext(new StubWorkspaceAgentSync("/workspaces/ws1"));
        ctx.Services.AddSingleton<Fravashi.Application.Sessions.IAgentSessionRunner>(runner);
        var config = new FakeConfigurationStore();
        ctx.Services.AddSingleton<IConfigurationStore>(config);
        return (ctx, runner, config);
    }

    private static async Task SendAsync(IRenderedComponent<Home> cut, string text)
    {
        await cut.InvokeAsync(() => cut.Find("[data-testid=home-prompt] [data-testid=composer-input]").Input(text));
        await cut.InvokeAsync(() => cut.Find("[data-testid=home-prompt] [data-testid=send-prompt]").Click());
    }

    [Fact]
    public async Task Sending_starts_the_folders_assistant_and_shows_the_thread_inline_on_Home()
    {
        var (ctx, runner, config) = AssistantContext();
        using var _ = ctx;
        var registry = ctx.Services.GetRequiredService<LiveSessionRegistry>();
        var cut = ctx.Render<Home>();

        Assert.Contains("widgets", cut.Find("[data-testid=home-prompt]").TextContent);
        Assert.Contains("Fravashi assistant", cut.Find("[data-testid=home-prompt]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=home-assistant-thread]"));
        Assert.Equal("Start", cut.Find("[data-testid=home-prompt] [data-testid=send-prompt]").TextContent.Trim());
        await SendAsync(cut, "The save button does nothing");

        cut.WaitForAssertion(() =>
        {
            var session = Assert.Single(registry.Sessions);
            Assert.True(session.IsAssistant);
            Assert.Equal("ws1", session.WorkspaceId);
            Assert.NotNull(cut.Find("[data-testid=home-assistant-thread] [data-testid=session-transcript]"));
            Assert.Contains("The save button does nothing", cut.Find("[data-testid=home-assistant-thread]").TextContent);
            Assert.Equal($"/sessions/{session.Id.Value:D}", cut.Find("[data-testid=home-prompt-sessions]").GetAttribute("href"));
            Assert.NotNull(cut.Find("[data-testid=home-prompt-new-topic]"));
            Assert.Equal(session.Id, Fravashi.Application.Sessions.StudioAssistantTopic.Read(config, "ws1"));
        });
        Assert.True(Assert.Single(runner.Requests).Assistant);
        var nav = ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        Assert.DoesNotContain("/sessions/", nav.Uri, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=home-prompt-error]"));
    }

    [Fact]
    public async Task A_report_while_an_editing_conversation_runs_starts_the_assistant_with_no_refusal()
    {
        var (ctx, runner, _) = AssistantContext();
        using var __ = ctx;
        var registry = ctx.Services.GetRequiredService<LiveSessionRegistry>();
        var editing = registry.Start(new SessionStartRequest("ws1", "/workspaces/ws1", "already building"));
        var cut = ctx.Render<Home>();

        Assert.NotNull(cut.Find("[data-testid=home-prompt-running]"));
        await SendAsync(cut, "while that runs, I noticed something");

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(2, registry.Sessions.Count);
            Assert.NotNull(registry.RunningAssistantFor("ws1"));
        });
        Assert.Same(editing, registry.RunningFor("ws1"));
        Assert.Empty(cut.FindAll("[data-testid=home-prompt-error]"));
    }

    [Fact]
    public async Task A_second_message_continues_the_running_assistant_rather_than_starting_another()
    {
        var (ctx, runner, _) = AssistantContext();
        using var __ = ctx;
        var registry = ctx.Services.GetRequiredService<LiveSessionRegistry>();
        var cut = ctx.Render<Home>();

        await SendAsync(cut, "first report");
        cut.WaitForAssertion(() => Assert.Single(registry.Sessions));
        var assistant = registry.Sessions[0];

        // The registry lists the session before the runner's RunAsync has registered its stream; the runner adds the
        // request and the stream under one lock, so waiting for the request is waiting for the stream. It is polled,
        // not WaitForAssertion: that one only re-checks on a render, and the runner registering causes none, so on a
        // busy machine it checked once, too early, and timed out (release run 556).
        await PollUntilAsync(() => runner.Requests.Count == 1, "the runner to receive the first request");
        runner.Emit(assistant.Id, new Fravashi.Domain.AgentSessions.MessageEmitted(
            DateTimeOffset.UtcNow, Fravashi.Domain.AgentSessions.TranscriptRole.Assistant, "What did you expect to happen?"));
        runner.Emit(assistant.Id, new Fravashi.Domain.AgentSessions.TurnCompleted(DateTimeOffset.UtcNow, "done", false, "completed"));
        cut.WaitForAssertion(() => Assert.False(assistant.TurnInFlight));

        await SendAsync(cut, "it should save");

        await PollUntilAsync(() => runner.Sent.Contains("it should save"), "the second message to reach the runner");
        Assert.Single(registry.Sessions);
        Assert.Single(runner.Requests);
    }

    [Fact]
    public async Task The_pickers_start_on_the_studio_assistant_default_not_the_new_conversation_one()
    {
        var (ctx, runner, config) = AssistantContext();
        using var _ = ctx;
        Fravashi.Application.Sessions.AgentDefaultsSetting.Write(config, Fravashi.Application.Sessions.AgentDefaults.None with
        {
            NewConversation = new Fravashi.Application.Sessions.AgentDefault("claude-haiku-4-5", Fravashi.Domain.AgentSessions.AgentEffort.Low),
            StudioAssistant = new Fravashi.Application.Sessions.AgentDefault("opus", Fravashi.Domain.AgentSessions.AgentEffort.Max),
        });
        var cut = ctx.Render<Home>();

        var composer = cut.FindComponent<Fravashi.Web.Components.Sessions.SessionComposer>();
        Assert.Equal("opus", composer.Instance.Model);
        Assert.Equal(Fravashi.Domain.AgentSessions.AgentEffort.Max, composer.Instance.Effort);
        await SendAsync(cut, "plan the next item");

        cut.WaitForAssertion(() => Assert.NotNull(runner.LastRequest));
        Assert.Equal("opus", runner.LastRequest!.Model);
        Assert.Equal(Fravashi.Domain.AgentSessions.AgentEffort.Max, runner.LastRequest.Effort);
    }

    [Fact]
    public async Task The_button_is_busy_until_the_turn_starts_streaming()
    {
        var (ctx, runner, _) = AssistantContext();
        using var __ = ctx;
        var registry = ctx.Services.GetRequiredService<LiveSessionRegistry>();
        var cut = ctx.Render<Home>();

        await SendAsync(cut, "first report");

        cut.WaitForAssertion(() =>
        {
            var button = cut.Find("[data-testid=home-prompt] [data-testid=send-prompt]");
            Assert.True(button.HasAttribute("disabled"));
            Assert.NotNull(button.QuerySelector("[data-testid=send-busy]"));
            Assert.Contains("Sending", button.TextContent, StringComparison.Ordinal);
        });

        var assistant = Assert.Single(registry.Sessions);
        runner.Emit(assistant.Id, new Fravashi.Domain.AgentSessions.TurnActivity(
            DateTimeOffset.UtcNow, Fravashi.Domain.AgentSessions.AgentTurnPhase.Thinking));

        cut.WaitForAssertion(() =>
            Assert.Empty(cut.FindAll("[data-testid=home-prompt] [data-testid=send-busy]")));
    }

    [Fact]
    public async Task An_approval_card_is_answered_right_there_on_Home()
    {
        var (ctx, _, _) = AssistantContext();
        using var __ = ctx;
        var registry = ctx.Services.GetRequiredService<LiveSessionRegistry>();
        var broker = (Fravashi.Infrastructure.Sessions.AgentApprovalBroker)ctx.Services
            .GetRequiredService<Fravashi.Application.Sessions.IAgentApprovalBroker>();
        var cut = ctx.Render<Home>();
        await SendAsync(cut, "file it");
        cut.WaitForAssertion(() => Assert.Single(registry.Sessions));
        var assistant = registry.Sessions[0];
        broker.RegisterSession(assistant.Id, Path.GetTempPath(), workspaceId: "ws1");

        var decision = broker.RequestAsync(assistant.Id, Fravashi.Domain.AgentSessions.AgentAssistantTools.StartBuilding, "t1",
            new Dictionary<string, string> { ["number"] = "12" });

        cut.WaitForAssertion(() =>
            Assert.Contains("building conversation", cut.Find("[data-testid=home-assistant-pending] [data-testid=approval-sentence]").TextContent));
        await cut.InvokeAsync(() => cut.Find("[data-testid=home-assistant-pending] [data-testid=approve]").Click());

        Assert.True((await decision.WaitAsync(TimeSpan.FromSeconds(10))).Approved);
    }

    [Fact]
    public async Task New_topic_ends_the_conversation_and_the_next_message_starts_a_fresh_one()
    {
        var (ctx, runner, config) = AssistantContext();
        using var _ = ctx;
        var registry = ctx.Services.GetRequiredService<LiveSessionRegistry>();
        var cut = ctx.Render<Home>();
        await SendAsync(cut, "first topic");
        cut.WaitForAssertion(() => Assert.Single(registry.Sessions));
        var first = registry.Sessions[0];

        await cut.InvokeAsync(() => cut.Find("[data-testid=home-prompt-new-topic]").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.False(first.IsRunning);
            Assert.Empty(cut.FindAll("[data-testid=home-assistant-thread]"));
        });
        Assert.Null(Fravashi.Application.Sessions.StudioAssistantTopic.Read(config, "ws1"));

        await SendAsync(cut, "second topic");
        cut.WaitForAssertion(() => Assert.Equal(2, runner.Requests.Count));
        Assert.Null(runner.Requests[1].ResumeSessionId);
        Assert.NotEqual(first.Id, registry.RunningAssistantFor("ws1")!.Id);
    }

    [Fact]
    public void Without_a_folder_the_box_cannot_start_anything_and_says_why()
    {
        using var ctx = PageContext();

        var cut = ctx.Render<Home>();

        Assert.Empty(cut.FindAll("[data-testid=home-prompt] [data-testid=composer-input]"));
        Assert.Contains("Open a folder", cut.Find("[data-testid=home-prompt-no-folder]").TextContent);
        Assert.Equal("/sessions", cut.Find("[data-testid=home-prompt-sessions]").GetAttribute("href"));
    }

    // ---- the Progress card ------------------------------------------------------------------------------------------

    private static BunitContext ProgressContext()
    {
        var ctx = PageContext();
        var mine = ProjectId.Default;
        ctx.Services.AddSingleton<ICurrentProject>(SingleProject.Instance);
        ctx.Services.AddSingleton<IProjectStore>(new NamedProjectStore((mine.Value, "Fravashi"), (Other.Value, "Other")));
        ctx.Services.AddSingleton<ITrackerResolver>(new SeededTrackers(new Dictionary<ProjectId, IReadOnlyList<WorkItem>>
        {
            [mine] =
            [
                SeededTrackers.Item(mine, 1, WorkItemStatus.Triage),
                SeededTrackers.Item(mine, 2, WorkItemStatus.Triage),
                SeededTrackers.Item(mine, 3, WorkItemStatus.Ready),
                SeededTrackers.Item(mine, 4, WorkItemStatus.InProgress),
                SeededTrackers.Item(mine, 5, WorkItemStatus.InProgress, blocked: true),
                SeededTrackers.Item(mine, 6, WorkItemStatus.InProgress),
                SeededTrackers.Item(mine, 7, WorkItemStatus.InReview),
                SeededTrackers.Item(mine, 8, WorkItemStatus.Done),
                SeededTrackers.Item(mine, 9, WorkItemStatus.Done),
                SeededTrackers.Item(mine, 10, WorkItemStatus.Cancelled),
            ],
            [Other] =
            [
                SeededTrackers.Item(Other, 1, WorkItemStatus.Backlog),
                SeededTrackers.Item(Other, 2, WorkItemStatus.Done),
            ],
        }));
        return ctx;
    }

    private static int CountFor(IRenderedComponent<ProgressCard> cut, string status) =>
        int.Parse(cut.Find($"[data-testid='progress-status-{status}']").GetAttribute("data-count")!,
            System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void The_progress_card_counts_this_projects_items_by_status()
    {
        using var ctx = ProgressContext();

        var cut = ctx.Render<ProgressCard>();

        cut.WaitForAssertion(() => Assert.Equal("10", cut.Find("[data-testid=progress-total]").TextContent));
        Assert.Equal(2, CountFor(cut, "triage"));
        Assert.Equal(0, CountFor(cut, "backlog"));
        Assert.Equal(1, CountFor(cut, "ready"));
        Assert.Equal(3, CountFor(cut, "in-progress"));
        Assert.Equal(1, CountFor(cut, "in-review"));
        Assert.Equal(2, CountFor(cut, "done"));
        Assert.Equal(1, CountFor(cut, "cancelled"));

        Assert.Equal("10", cut.Find("[data-testid=progress-figure-total]").TextContent);
        Assert.Equal("7", cut.Find("[data-testid=progress-figure-open]").TextContent);
        // Work item #358: In progress and In review are two figures, matching the two bars.
        Assert.Equal("3", cut.Find("[data-testid=progress-figure-in-progress]").TextContent);
        Assert.Equal("1", cut.Find("[data-testid=progress-figure-in-review]").TextContent);
        Assert.Equal("1", cut.Find("[data-testid=progress-figure-blocked]").TextContent);
        Assert.Equal("2", cut.Find("[data-testid=progress-figure-done]").TextContent);

        // The ring: one arc per status that has items, and done out of everything not cancelled (2 of 9).
        var ring = cut.Find("[data-testid=progress-ring]");
        Assert.Equal(6, ring.QuerySelectorAll(".progress-ring-seg").Length);
        Assert.Contains("22%", ring.TextContent);
    }

    [Fact]
    public async Task All_projects_adds_up_every_projects_board()
    {
        using var ctx = ProgressContext();
        var cut = ctx.Render<ProgressCard>();
        cut.WaitForAssertion(() => Assert.Equal("10", cut.Find("[data-testid=progress-total]").TextContent));

        await cut.InvokeAsync(() => cut.Find("[data-testid=progress-project-trigger]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=progress-project-opt-all]").Click());

        cut.WaitForAssertion(() => Assert.Equal("12", cut.Find("[data-testid=progress-total]").TextContent));
        Assert.Equal(1, CountFor(cut, "backlog"));
        Assert.Equal(3, CountFor(cut, "done"));
    }

    [Fact]
    public void Home_carries_the_progress_card()
    {
        using var ctx = ProgressContext();

        var cut = ctx.Render<Home>();

        cut.WaitForAssertion(() => Assert.Equal("10", cut.Find("[data-testid=progress-total]").TextContent));
    }
}
