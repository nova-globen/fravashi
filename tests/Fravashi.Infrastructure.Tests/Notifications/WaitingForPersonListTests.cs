using System.Threading.Channels;
using AwesomeAssertions;
using Fravashi.Application.Notifications;
using Fravashi.Application.Sessions;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Autopilot;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Autopilot;
using Fravashi.Infrastructure.Notifications;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.WorkItems;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Notifications;

/// <summary>
/// Backlog 304: the real <see cref="IWaitingForPersonList"/>, over a real <c>LiveSessionRegistry</c> (the
/// same construction <c>LiveSessionRegistryTests</c> already uses), a real <c>SqliteAutopilotRunStore</c> and
/// a real <c>LocalTrackerResolver</c> (the same trio <c>AutopilotCardTests</c> already uses), one temp database,
/// so every parked item across every open run of every project appears, and a live session's pending
/// approval appears beside it.
/// </summary>
public sealed class WaitingForPersonListTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fravashi-waiting-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteSessionStore _sessionStore;
    private readonly AgentApprovalBroker _broker = new(TimeSpan.FromSeconds(30));
    private readonly ScriptedRunner _runner = new();
    private readonly LiveSessionRegistry _registry;
    private readonly LocalTrackerResolver _trackers;
    private readonly SqliteAutopilotRunStore _runs;

    public WaitingForPersonListTests()
    {
        Directory.CreateDirectory(_directory);
        _factory = new SqliteConnectionFactory(new SqliteStoreOptions(Path.Combine(_directory, "test.db"), null, Pooling: false));
        SqliteSchema.EnsureCreated(_factory);
        _sessionStore = new SqliteSessionStore(_factory);
        _registry = new LiveSessionRegistry(_runner, _sessionStore, _broker);
        _trackers = new LocalTrackerResolver(_factory, TimeProvider.System);
        _runs = new SqliteAutopilotRunStore(_factory);
    }

    public void Dispose()
    {
        _registry.Dispose();
        _broker.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<WorkItem> CreateItemAsync(ProjectId project, string title)
    {
        var tracker = _trackers.For(project);
        var created = await tracker.CreateAsync(
            new NewWorkItem { Level = WorkItemLevel.Task, Title = title, Description = "x", Status = WorkItemStatus.Ready },
            MutationContext.User);
        return created.Value;
    }

    /// <summary>
    /// Marked, then moved to In Progress (the mark rule only turns on from Ready), the same shape the real
    /// building agent leaves an item in once it starts work: #305's resume predicate
    /// (<c>AutopilotSelection.ParkedInBoardOrder</c>) reads both; the waiting list reads only the mark.
    /// </summary>
    private async Task<WorkItem> CreateMarkedInProgressItemAsync(ProjectId project, string title)
    {
        var tracker = _trackers.For(project);
        var created = await CreateItemAsync(project, title);
        var marked = await tracker.UpdateAsync(
            created.Id, new WorkItemPatch { MarkedForAutopilot = Optional<bool>.Of(true) }, created.Version, MutationContext.User);
        var started = await tracker.TransitionAsync(
            created.Id, WorkItemStatus.InProgress, null, null, null, marked.Value.Version, MutationContext.User);
        return started.Value;
    }

    /// <summary>
    /// Marked, left Ready: nothing GUARANTEES the building agent transitions the item to In Progress before
    /// its first approval parks it (CLAUDE.md asks it to, but an agent that parks on its very first tool call
    /// may never reach that line). The waiting list must still show this item.
    /// </summary>
    private async Task<WorkItem> CreateMarkedReadyItemAsync(ProjectId project, string title)
    {
        var tracker = _trackers.For(project);
        var created = await CreateItemAsync(project, title);
        var marked = await tracker.UpdateAsync(
            created.Id, new WorkItemPatch { MarkedForAutopilot = Optional<bool>.Of(true) }, created.Version, MutationContext.User);
        return marked.Value;
    }

    [Fact]
    public async Task Parked_items_across_two_different_runs_and_projects_all_appear()
    {
        var projectA = ProjectId.Default;
        var projectB = new ProjectId("project-b");
        var itemA = await CreateMarkedInProgressItemAsync(projectA, "Fix the thing");
        var itemB = new WorkItemId("no-such-item");
        var startedA = DateTimeOffset.UtcNow.AddHours(-2);
        var startedB = DateTimeOffset.UtcNow.AddHours(-1);

        var runA = AutopilotRun.Start(Guid.NewGuid(), projectA, AutopilotDispatchSettings.Default, [itemA.Id], startedA);
        runA = runA.With(runA.Queue[0] with { Stage = AutopilotItemStage.Parked, StartedUtc = startedA, ParkedReason = "a question is open" });
        await _runs.SaveAsync(runA);

        var runB = AutopilotRun.Start(Guid.NewGuid(), projectB, AutopilotDispatchSettings.Default, [itemB], startedB);
        runB = runB.With(runB.Queue[0] with { Stage = AutopilotItemStage.Parked, StartedUtc = startedB, ParkedReason = "a commit review failed" });
        await _runs.SaveAsync(runB);

        var list = new WaitingForPersonList(_registry, _runs, _trackers);

        var entries = await list.CurrentAsync();

        entries.Should().HaveCount(2);
        var a = entries.Single(e => e.Item == itemA.Id);
        a.Kind.Should().Be(WaitingForPersonKind.Parked);
        a.What.Should().Be($"#{itemA.Number} Fix the thing is parked: a question is open");
        a.SinceUtc.Should().Be(startedA);
        a.ItemNumber.Should().Be(itemA.Number);

        var b = entries.Single(e => e.Item == itemB);
        b.What.Should().Be("no-such-item is parked: a commit review failed", "no tracker could resolve it, so the raw id degrades the label");
        b.ItemNumber.Should().BeNull();
    }

    /// <summary>
    /// Nothing in the engine or the building agent's own prompt GUARANTEES the item is transitioned to
    /// In Progress before its first approval parks it: an agent that parks on its very first tool call may
    /// never reach that instruction. The waiting list's own filter must not require a status nothing promises.
    /// </summary>
    [Fact]
    public async Task A_parked_item_still_ready_is_listed_too()
    {
        var item = await CreateMarkedReadyItemAsync(ProjectId.Default, "Needs a decision, still Ready");
        var run = AutopilotRun.Start(Guid.NewGuid(), ProjectId.Default, AutopilotDispatchSettings.Default, [item.Id], DateTimeOffset.UtcNow);
        run = run.With(run.Queue[0] with { Stage = AutopilotItemStage.Parked, StartedUtc = DateTimeOffset.UtcNow, ParkedReason = "needs a decision" });
        await _runs.SaveAsync(run);

        var list = new WaitingForPersonList(_registry, _runs, _trackers);
        var entries = await list.CurrentAsync();

        entries.Should().ContainSingle(e => e.Item == item.Id && e.Kind == WaitingForPersonKind.Parked);
    }

    /// <summary>
    /// ADR 0031 decision 12, #305: "PARKS the item In Progress with its question still listed." A run that
    /// has ENDED (Stopped via QueueExhausted, the shape every timed-out single-item run takes) must not drop
    /// its parked item off the list the instant it ends; only OpenRunsAsync's own scope did that before #305.
    /// </summary>
    [Fact]
    public async Task A_parked_item_stays_listed_after_its_own_run_has_ended()
    {
        var item = await CreateMarkedInProgressItemAsync(ProjectId.Default, "Needs a decision");
        var started = DateTimeOffset.UtcNow.AddHours(-2);
        var run = AutopilotRun.Start(Guid.NewGuid(), ProjectId.Default, AutopilotDispatchSettings.Default, [item.Id], started);
        run = run.With(run.Queue[0] with { Stage = AutopilotItemStage.Parked, StartedUtc = started, ParkedReason = "needs a decision" })
            with
        {
            State = AutopilotRunState.Stopped,
        };
        await _runs.SaveAsync(run);

        var list = new WaitingForPersonList(_registry, _runs, _trackers);
        var entries = await list.CurrentAsync();

        entries.Should().ContainSingle(e => e.Item == item.Id && e.Kind == WaitingForPersonKind.Parked);
    }

    /// <summary>
    /// The stored reason is the stage's own wording from the moment it parked (e.g. "The building session is
    /// waiting on a person to approve..."), which stops being a useful instruction the moment the run that
    /// parked it ends: the run can never be ticked again regardless of whether that exact session is still
    /// alive (MaybeReturnWorkspaceAsync only reaches it when the workspace-return seam is wired and the run
    /// carries a base branch and workspace id). An ended run's entry must say the run has ended and name the
    /// real next step, not repeat the live-session wording as if answering it would still do anything.
    /// </summary>
    [Fact]
    public async Task An_ended_runs_parked_entry_says_the_run_has_ended_rather_than_repeating_the_live_session_wording()
    {
        var item = await CreateMarkedInProgressItemAsync(ProjectId.Default, "Needs a decision");
        var run = AutopilotRun.Start(Guid.NewGuid(), ProjectId.Default, AutopilotDispatchSettings.Default, [item.Id], DateTimeOffset.UtcNow);
        run = run.With(run.Queue[0] with
        {
            Stage = AutopilotItemStage.Parked,
            StartedUtc = DateTimeOffset.UtcNow,
            ParkedReason = "The building session is waiting on a person to approve Bash (\"echo hello\").",
        }) with { State = AutopilotRunState.Stopped };
        await _runs.SaveAsync(run);

        var list = new WaitingForPersonList(_registry, _runs, _trackers);
        var entries = await list.CurrentAsync();

        var entry = entries.Single(e => e.Item == item.Id);
        entry.What.Should().Contain("This run has ended; dispatch the project again to resume it.");
    }

    /// <summary>
    /// Work item #356: a parked entry carries its run, its reason (with any raw mcp__ name put into words) and whether
    /// the run ended, which the Home explanation reads; dismissing it hides that run's notice and nothing else.
    /// </summary>
    [Fact]
    public async Task A_parked_entry_explains_itself_and_a_dismissed_one_is_no_longer_listed()
    {
        var item = await CreateMarkedInProgressItemAsync(ProjectId.Default, "Needs a push");
        var run = AutopilotRun.Start(Guid.NewGuid(), ProjectId.Default, AutopilotDispatchSettings.Default, [item.Id], DateTimeOffset.UtcNow);
        run = run.With(run.Queue[0] with
        {
            Stage = AutopilotItemStage.Parked,
            StartedUtc = DateTimeOffset.UtcNow,
            ParkedReason = "The building session is waiting on a person to approve mcp__fravashi__work_item_update (\"#23\").",
        }) with { State = AutopilotRunState.Stopped };
        await _runs.SaveAsync(run);
        var store = new MemoryConfigurationStore();
        var dismissals = new ConfigurationParkedNoticeDismissals(store);
        var list = new WaitingForPersonList(_registry, _runs, _trackers, dismissals);

        var entry = (await list.CurrentAsync()).Single(e => e.Item == item.Id);

        entry.Run.Should().Be(run.Id);
        entry.RunEnded.Should().BeTrue();
        entry.Reason.Should().Contain("\"work item update\"");
        entry.What.Should().NotContain("mcp__");

        dismissals.Dismiss(run.Id, item.Id);

        (await list.CurrentAsync()).Should().NotContain(e => e.Item == item.Id);
        dismissals.IsDismissed(Guid.NewGuid(), item.Id).Should().BeFalse("a later run parking the item again is a new notice");
        store.Values.Keys.Should().ContainSingle(k => k.StartsWith(ConfigurationParkedNoticeDismissals.Prefix, StringComparison.Ordinal));
    }

    private sealed class MemoryConfigurationStore : Fravashi.Application.Persistence.IConfigurationStore
    {
        public Dictionary<string, string?> Values { get; } = new(StringComparer.Ordinal);

        public string? Get(string key) => Values.GetValueOrDefault(key);

        public IReadOnlyDictionary<string, string?> GetAll() => Values;

        public void Set(string key, string? value) => Values[key] = value;

        public void SetMany(IReadOnlyDictionary<string, string?> values)
        {
            foreach (var (key, value) in values)
            {
                Values[key] = value;
            }
        }

        public void Remove(string key) => Values.Remove(key);
    }

    /// <summary>
    /// A resumed item's own run can stop while an earlier sibling is still open and current, before this
    /// item is ever reached: it is left Queued with no StartedUtc, never named Parked, and the run has ended
    /// (a run-level limit, "stop now", or cannot-resume), so without reading this shape too it would be
    /// marked, In Progress, and permanently invisible on the home page.
    /// </summary>
    [Fact]
    public async Task An_item_left_queued_and_unreached_when_its_own_resuming_run_stopped_is_still_listed()
    {
        var blockedBy = await CreateMarkedInProgressItemAsync(ProjectId.Default, "Blocking item");
        var neverReached = await CreateMarkedInProgressItemAsync(ProjectId.Default, "Never reached");
        var run = AutopilotRun.Start(
            Guid.NewGuid(), ProjectId.Default, AutopilotDispatchSettings.Default, [blockedBy.Id, neverReached.Id], DateTimeOffset.UtcNow);
        run = run.With(run.Queue[0] with { Stage = AutopilotItemStage.Building, StartedUtc = DateTimeOffset.UtcNow })
            with
        {
            State = AutopilotRunState.Stopped,
        };
        await _runs.SaveAsync(run);

        var list = new WaitingForPersonList(_registry, _runs, _trackers);
        var entries = await list.CurrentAsync();

        entries.Should().ContainSingle(e => e.Item == neverReached.Id);
    }

    /// <summary>
    /// The SAME shape (Queued, no StartedUtc) in a STILL-RUNNING run is just an item waiting its ordinary
    /// turn, not stuck: listing it would be a false "waiting for you" entry on every ordinary multi-item run.
    /// </summary>
    [Fact]
    public async Task An_item_merely_waiting_its_turn_in_a_still_running_run_is_not_listed()
    {
        var current = await CreateMarkedInProgressItemAsync(ProjectId.Default, "Currently building");
        var waitingItsTurn = await CreateMarkedInProgressItemAsync(ProjectId.Default, "Waiting its turn");
        var run = AutopilotRun.Start(
            Guid.NewGuid(), ProjectId.Default, AutopilotDispatchSettings.Default, [current.Id, waitingItsTurn.Id], DateTimeOffset.UtcNow);
        run = run.With(run.Queue[0] with { Stage = AutopilotItemStage.Building, StartedUtc = DateTimeOffset.UtcNow });
        await _runs.SaveAsync(run);

        var list = new WaitingForPersonList(_registry, _runs, _trackers);
        var entries = await list.CurrentAsync();

        entries.Should().NotContain(e => e.Item == waitingItsTurn.Id);
    }

    /// <summary>
    /// The list drops an item the moment it is no longer marked, regardless of its status: the mark is the
    /// only thing this list's own filter reads (status is deliberately not part of it, unlike the resume
    /// predicate <c>AutopilotSelection.ParkedInBoardOrder</c> uses), so an OLD ended run's own Parked record
    /// is no longer shown once a person has unmarked the item it names.
    /// </summary>
    [Fact]
    public async Task An_item_a_person_unmarked_no_longer_appears_even_though_an_old_run_still_names_it_parked()
    {
        var item = await CreateMarkedInProgressItemAsync(ProjectId.Default, "Needs a decision");
        var tracker = _trackers.For(ProjectId.Default);
        await tracker.UpdateAsync(
            item.Id, new WorkItemPatch { MarkedForAutopilot = Optional<bool>.Of(false) }, item.Version, MutationContext.User);

        var run = AutopilotRun.Start(Guid.NewGuid(), ProjectId.Default, AutopilotDispatchSettings.Default, [item.Id], DateTimeOffset.UtcNow);
        run = run.With(run.Queue[0] with { Stage = AutopilotItemStage.Parked, StartedUtc = DateTimeOffset.UtcNow, ParkedReason = "needs a decision" })
            with
        {
            State = AutopilotRunState.Stopped,
        };
        await _runs.SaveAsync(run);

        var list = new WaitingForPersonList(_registry, _runs, _trackers);
        var entries = await list.CurrentAsync();

        entries.Should().NotContain(e => e.Item == item.Id);
    }

    [Fact]
    public async Task An_open_building_item_never_appears()
    {
        var item = new WorkItemId("item-building");
        var run = AutopilotRun.Start(Guid.NewGuid(), ProjectId.Default, AutopilotDispatchSettings.Default, [item], DateTimeOffset.UtcNow);
        run = run.With(run.Queue[0] with { Stage = AutopilotItemStage.Building });
        await _runs.SaveAsync(run);

        var list = new WaitingForPersonList(_registry, _runs, _trackers);

        var entries = await list.CurrentAsync();

        entries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_live_sessions_pending_approval_appears_beside_a_parked_item()
    {
        var session = _registry.Start(new SessionStartRequest("ws-1", "/workspaces/ws-1", "do the thing"));
        _broker.RegisterSession(session.Id);
        _ = _broker.RequestAsync(session.Id, "Bash", "toolu_1", new Dictionary<string, string> { ["command"] = "make" });

        var item = new WorkItemId("item-parked");
        var run = AutopilotRun.Start(Guid.NewGuid(), ProjectId.Default, AutopilotDispatchSettings.Default, [item], DateTimeOffset.UtcNow);
        run = run.With(run.Queue[0] with { Stage = AutopilotItemStage.Parked, StartedUtc = DateTimeOffset.UtcNow, ParkedReason = "waiting" });
        await _runs.SaveAsync(run);

        var list = new WaitingForPersonList(_registry, _runs, _trackers);

        IReadOnlyList<WaitingForPersonEntry> entries = [];
        await Eventually(async () =>
        {
            entries = await list.CurrentAsync();
            return entries.Count == 2;
        });

        entries.Should().Contain(e => e.Kind == WaitingForPersonKind.Approval && e.SessionId == session.Id);
        entries.Should().Contain(e => e.Kind == WaitingForPersonKind.Parked && e.Item == item);
    }

    private static async Task Eventually(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(await condition(), "the condition was never met");
    }

    /// <summary>A runner the test drives by hand, one event at a time (the LiveSessionRegistryTests shape).</summary>
    private sealed class ScriptedRunner : IAgentSessionRunner
    {
        private readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>();

        public IAsyncEnumerable<AgentEvent> RunAsync(AgentSessionRequest request, CancellationToken cancellationToken = default) =>
            _events.Reader.ReadAllAsync(cancellationToken);

        public Task<bool> SendAsync(AgentSessionId sessionId, string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> SendAsync(
            AgentSessionId sessionId, string text, IReadOnlyList<AgentImageAttachment> images,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public bool Stop(AgentSessionId sessionId)
        {
            _events.Writer.TryComplete();
            return true;
        }

        public bool ChangeModel(AgentSessionId sessionId, string? model) => true;
    }
}
