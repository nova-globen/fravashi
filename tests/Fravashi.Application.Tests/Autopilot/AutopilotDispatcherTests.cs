using AwesomeAssertions;
using Fravashi.Application.Autopilot;
using Fravashi.Application.Projects;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Autopilot;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;
using Xunit;

namespace Fravashi.Application.Tests.Autopilot;

/// <summary>
/// #298's acceptance: a dispatch builds the run from the marked Ready items in board order with the
/// form's settings and saves it; a refusal saves nothing.
/// </summary>
public sealed class AutopilotDispatcherTests
{
    private static readonly ProjectId Project = new("proj-1");

    private static WorkItem ReadyMarked(double rank, int number) =>
        WorkItem.Restore(new WorkItemState
        {
            Id = new WorkItemId($"item-{number}"),
            Project = Project,
            Number = number,
            Level = WorkItemLevel.Task,
            Title = "A title",
            Status = WorkItemStatus.Ready,
            MarkedForAutopilot = true,
            Rank = rank,
        }).Value;

    /// <summary>A tracker whose only implemented operation this dispatcher calls: a single-page query.</summary>
    private sealed class FakeTracker(IReadOnlyList<WorkItem> items) : ITracker
    {
        public ProjectId Project => AutopilotDispatcherTests.Project;

        public string ProviderName => "local";

        public TrackerCapabilities Capabilities => TrackerCapabilities.ReadOnly;

        public Task<TrackerResult<WorkItem>> CreateAsync(NewWorkItem item, MutationContext context, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<WorkItem?> GetAsync(WorkItemId id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<WorkItem?> GetByNumberAsync(int number, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<WorkItemPage> QueryAsync(WorkItemQuery query, CancellationToken ct = default) =>
            Task.FromResult(new WorkItemPage(items, null));

        public Task<IReadOnlyList<WorkItemEvent>> HistoryAsync(WorkItemId id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<WorkItemLink>> LinksAsync(WorkItemId id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TrackerResult<WorkItem>> UpdateAsync(WorkItemId id, WorkItemPatch patch, long expectedVersion, MutationContext context, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TrackerResult<WorkItem>> TransitionAsync(WorkItemId id, WorkItemStatus to, WorkItemResolution? resolution, WorkItemId? after, WorkItemId? before, long expectedVersion, MutationContext context, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TrackerResult<WorkItem>> MoveAsync(WorkItemId id, WorkItemId? after, WorkItemId? before, long expectedVersion, MutationContext context, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TrackerResult<WorkItemLink>> LinkAsync(WorkItemId from, WorkItemLinkType type, WorkItemId to, MutationContext context, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TrackerResult<bool>> UnlinkAsync(WorkItemId from, WorkItemLinkType type, WorkItemId to, MutationContext context, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TrackerResult<EvidenceRecord>> RecordEvidenceAsync(WorkItemId id, NewEvidence evidence, MutationContext context, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<EvidenceRecord>> EvidenceAsync(WorkItemId id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<StartabilityReport> WhatCanStartNowAsync(StartableQuery query, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeTrackerResolver(ITracker tracker) : ITrackerResolver
    {
        public ITracker For(ProjectId project) => tracker;
    }

    private sealed class FakeProjectDelivery(AutopilotDeliveryMode mode) : IProjectDelivery
    {
        public AutopilotDeliveryMode For(ProjectId project) => mode;

        public void Set(ProjectId project, AutopilotDeliveryMode value) => throw new NotSupportedException();
    }

    private sealed class FakeDeliveryReadiness(Func<AutopilotDeliveryReadiness> answer) : IAutopilotDeliveryReadiness
    {
        public bool Asked { get; private set; }

        public Task<AutopilotDeliveryReadiness> CheckAsync(CancellationToken ct = default)
        {
            Asked = true;
            return Task.FromResult(answer());
        }
    }

    /// <summary>A clean, reachable workspace by default, standing in for the real git-backed status (#310).</summary>
    private sealed class FakeWorkspaceStatus(
        string branch = "main", int dirtyCount = 0, bool reachable = true, string? workspaceId = "w1")
        : IAutopilotWorkspaceStatus
    {
        public Task<AutopilotWorkspaceState> CheckAsync(ProjectId project, CancellationToken ct = default) =>
            Task.FromResult(new AutopilotWorkspaceState(branch, dirtyCount, reachable, reachable ? workspaceId : null));
    }

    private sealed class FakeConcurrencySettings(int cap) : IAutopilotConcurrencySettings
    {
        public int Cap() => cap;
    }

    private static AutopilotDispatcher Build(
        IReadOnlyList<WorkItem> items,
        AutopilotDeliveryMode mode,
        FakeDeliveryReadiness readiness,
        out FakeRunStore store,
        IAutopilotWorkspaceStatus? workspaceStatus = null,
        int concurrencyCapSetting = 2)
    {
        store = new FakeRunStore();
        return new AutopilotDispatcher(
            new FakeTrackerResolver(new FakeTracker(items)),
            new FakeProjectDelivery(mode),
            readiness,
            workspaceStatus ?? new FakeWorkspaceStatus(),
            store,
            new FixedClock(DateTimeOffset.UtcNow),
            new FakeConcurrencySettings(concurrencyCapSetting));
    }

    [Fact]
    public async Task A_valid_dispatch_saves_a_run_from_the_marked_ready_items_in_board_order()
    {
        var second = ReadyMarked(2, 20);
        var first = ReadyMarked(1, 10);
        var readiness = new FakeDeliveryReadiness(() => new AutopilotDeliveryReadiness(true, "github.com"));
        var dispatcher = Build([second, first], AutopilotDeliveryMode.BranchAndPullRequest, readiness, out var store);

        var settings = AutopilotDispatchSettings.Default;
        var outcome = await dispatcher.DispatchAsync(Project, settings);

        outcome.Dispatched.Should().BeTrue();
        outcome.Run!.Queue.Select(i => i.Item).Should().Equal(first.Id, second.Id);
        // #308: the run's own effective cap is resolved once, here, from the Settings cap (2, Build's own
        // default) and the dispatch's own override (none), and recorded on the saved settings: never the
        // dispatch-supplied settings unchanged.
        outcome.Run.Settings.Should().Be(settings with { EffectiveConcurrencyCap = 2 });
        outcome.Run.Project.Should().Be(Project);
        (await store.GetAsync(outcome.Run.Id)).Should().NotBeNull();
    }

    /// <summary>ADR 0031 decision 16, #307: a dispatch's own cap may only lower the Settings one, never raise it.</summary>
    [Fact]
    public async Task A_concurrency_override_not_lower_than_the_settings_cap_is_refused_and_saves_nothing()
    {
        var marked = ReadyMarked(1, 10);
        var readiness = new FakeDeliveryReadiness(() => new AutopilotDeliveryReadiness(true, "github.com"));
        var dispatcher = Build(
            [marked], AutopilotDeliveryMode.BranchAndPullRequest, readiness, out var store, concurrencyCapSetting: 2);

        var settings = AutopilotDispatchSettings.Default with { ConcurrencyCapOverride = 2 };
        var outcome = await dispatcher.DispatchAsync(Project, settings);

        outcome.Dispatched.Should().BeFalse();
        outcome.Refusal.Should().Contain("never raise it");
        (await store.OpenRunsAsync()).Should().BeEmpty();
    }

    /// <summary>A lower override is a real, accepted dispatch setting: it rides on the saved run unchanged.</summary>
    [Fact]
    public async Task A_lower_concurrency_override_is_accepted_and_saved_on_the_run()
    {
        var marked = ReadyMarked(1, 10);
        var readiness = new FakeDeliveryReadiness(() => new AutopilotDeliveryReadiness(true, "github.com"));
        var dispatcher = Build(
            [marked], AutopilotDeliveryMode.BranchAndPullRequest, readiness, out _, concurrencyCapSetting: 2);

        var settings = AutopilotDispatchSettings.Default with { ConcurrencyCapOverride = 1 };
        var outcome = await dispatcher.DispatchAsync(Project, settings);

        outcome.Dispatched.Should().BeTrue();
        outcome.Run!.Settings.ConcurrencyCapOverride.Should().Be(1);
    }

    /// <summary>
    /// #311: a parallel run's worktrees are each made from the base branch the person's folder has checked
    /// out, and delivery refuses to move that branch's ref while it is checked out there, so a parallel
    /// commit-to-main run can never deliver. The dispatcher forces the effective cap to 1 regardless of the
    /// Settings cap, whatever a dispatch's own override would otherwise have produced.
    /// </summary>
    [Fact]
    public async Task A_commit_to_main_dispatch_forces_the_effective_cap_to_one_with_no_override()
    {
        var item = ReadyMarked(1, 1);
        var readiness = new FakeDeliveryReadiness(() => throw new InvalidOperationException("should not be asked"));
        var dispatcher = Build(
            [item], AutopilotDeliveryMode.CommitToMain, readiness, out _, concurrencyCapSetting: 2);

        var outcome = await dispatcher.DispatchAsync(Project, AutopilotDispatchSettings.Default);

        outcome.Dispatched.Should().BeTrue();
        outcome.Run!.Settings.EffectiveConcurrencyCap.Should().Be(1);
    }

    /// <summary>
    /// A dispatch's own override is still a legitimately LOWER number than the Settings cap (so validation
    /// never refuses it), and it still never raises the effective cap above 1 under commit to main.
    /// </summary>
    [Fact]
    public async Task A_commit_to_main_dispatch_forces_the_effective_cap_to_one_even_with_a_lower_override()
    {
        var item = ReadyMarked(1, 1);
        var readiness = new FakeDeliveryReadiness(() => throw new InvalidOperationException("should not be asked"));
        var dispatcher = Build(
            [item], AutopilotDeliveryMode.CommitToMain, readiness, out _, concurrencyCapSetting: 3);

        var settings = AutopilotDispatchSettings.Default with { ConcurrencyCapOverride = 2 };
        var outcome = await dispatcher.DispatchAsync(Project, settings);

        outcome.Dispatched.Should().BeTrue();
        outcome.Run!.Settings.EffectiveConcurrencyCap.Should().Be(1);
    }

    /// <summary>
    /// The force-to-1 is a dispatch-time decision only: a fresh dispatch builds and saves a brand NEW run and
    /// never rewrites one already in the store. A run saved before this change (v0.9.81) with a cap above 1
    /// under commit to main keeps its own recorded cap untouched by a later dispatch for the same project.
    /// </summary>
    [Fact]
    public async Task A_fresh_dispatch_never_touches_an_existing_runs_own_recorded_cap()
    {
        var finishedItem = InProgressMarked(1);
        var freshItem = ReadyMarked(1, 2);
        var readiness = new FakeDeliveryReadiness(() => throw new InvalidOperationException("should not be asked"));
        var dispatcher = Build([finishedItem, freshItem], AutopilotDeliveryMode.CommitToMain, readiness, out var store);
        var preExisting = EndedRun(
            Project, DateTimeOffset.UtcNow.AddHours(-2),
            new AutopilotRunItem(finishedItem.Id, 0, AutopilotItemStage.Done, 1m, 0, null, null, null)) with
        {
            Settings = AutopilotDispatchSettings.Default with { EffectiveConcurrencyCap = 2 },
        };
        await store.SaveAsync(preExisting);

        var outcome = await dispatcher.DispatchAsync(Project, AutopilotDispatchSettings.Default);

        outcome.Dispatched.Should().BeTrue();
        outcome.Run!.Settings.EffectiveConcurrencyCap.Should().Be(1);
        var rereadExisting = await store.GetAsync(preExisting.Id);
        rereadExisting!.Settings.EffectiveConcurrencyCap.Should().Be(2);
    }

    [Fact]
    public async Task No_marked_ready_items_refuses_and_saves_nothing()
    {
        var readiness = new FakeDeliveryReadiness(() => new AutopilotDeliveryReadiness(true, "github.com"));
        var dispatcher = Build([], AutopilotDeliveryMode.BranchAndPullRequest, readiness, out var store);

        var outcome = await dispatcher.DispatchAsync(Project, AutopilotDispatchSettings.Default);

        outcome.Dispatched.Should().BeFalse();
        outcome.Refusal.Should().Contain("marked for autopilot");
        (await store.OpenRunsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Branch_and_pull_request_with_no_token_refuses_and_saves_nothing()
    {
        var item = ReadyMarked(1, 1);
        var readiness = new FakeDeliveryReadiness(() => new AutopilotDeliveryReadiness(false, "github.com"));
        var dispatcher = Build([item], AutopilotDeliveryMode.BranchAndPullRequest, readiness, out var store);

        var outcome = await dispatcher.DispatchAsync(Project, AutopilotDispatchSettings.Default);

        outcome.Dispatched.Should().BeFalse();
        outcome.Refusal.Should().Contain("github.com");
        (await store.OpenRunsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_second_dispatch_while_the_first_is_still_open_refuses_and_queues_nothing_twice()
    {
        // ADR 0031 decision 1, "one at a time": the item's own status does not leave Ready just because it
        // is sitting in an open run's queue (Queued and Prompting never move it), so a second dispatch
        // would otherwise pick the SAME marked item up again and run it twice over.
        var item = ReadyMarked(1, 1);
        var readiness = new FakeDeliveryReadiness(() => new AutopilotDeliveryReadiness(true, "github.com"));
        var dispatcher = Build([item], AutopilotDeliveryMode.BranchAndPullRequest, readiness, out var store);

        var first = await dispatcher.DispatchAsync(Project, AutopilotDispatchSettings.Default);
        first.Dispatched.Should().BeTrue();

        var second = await dispatcher.DispatchAsync(Project, AutopilotDispatchSettings.Default);

        second.Dispatched.Should().BeFalse();
        second.Refusal.Should().Contain("already open");
        (await store.OpenRunsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Commit_to_main_never_asks_for_readiness()
    {
        var item = ReadyMarked(1, 1);
        var readiness = new FakeDeliveryReadiness(() => throw new InvalidOperationException("should not be asked"));
        var dispatcher = Build([item], AutopilotDeliveryMode.CommitToMain, readiness, out _);

        var outcome = await dispatcher.DispatchAsync(Project, AutopilotDispatchSettings.Default);

        outcome.Dispatched.Should().BeTrue();
        readiness.Asked.Should().BeFalse();
    }

    [Fact]
    public async Task A_dirty_workspace_refuses_and_saves_nothing()
    {
        var item = ReadyMarked(1, 1);
        var readiness = new FakeDeliveryReadiness(() => new AutopilotDeliveryReadiness(true, "github.com"));
        var dispatcher = Build(
            [item], AutopilotDeliveryMode.BranchAndPullRequest, readiness, out var store,
            workspaceStatus: new FakeWorkspaceStatus(dirtyCount: 2));

        var outcome = await dispatcher.DispatchAsync(Project, AutopilotDispatchSettings.Default);

        outcome.Dispatched.Should().BeFalse();
        outcome.Refusal.Should().Contain("2");
        (await store.OpenRunsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_clean_dispatch_carries_the_workspace_branch_onto_the_saved_run()
    {
        var item = ReadyMarked(1, 1);
        var readiness = new FakeDeliveryReadiness(() => new AutopilotDeliveryReadiness(true, "github.com"));
        var dispatcher = Build(
            [item], AutopilotDeliveryMode.BranchAndPullRequest, readiness, out _,
            workspaceStatus: new FakeWorkspaceStatus(branch: "feature/x", dirtyCount: 0, workspaceId: "ws-feature"));

        var outcome = await dispatcher.DispatchAsync(Project, AutopilotDispatchSettings.Default);

        outcome.Dispatched.Should().BeTrue();
        outcome.Run!.BaseBranch.Should().Be("feature/x");
        outcome.Run!.BaseWorkspaceId.Should().Be("ws-feature");
    }

    [Fact]
    public async Task An_unreachable_workspace_refuses_and_saves_nothing()
    {
        var item = ReadyMarked(1, 1);
        var readiness = new FakeDeliveryReadiness(() => new AutopilotDeliveryReadiness(true, "github.com"));
        var dispatcher = Build(
            [item], AutopilotDeliveryMode.BranchAndPullRequest, readiness, out var store,
            workspaceStatus: new FakeWorkspaceStatus(reachable: false));

        var outcome = await dispatcher.DispatchAsync(Project, AutopilotDispatchSettings.Default);

        outcome.Dispatched.Should().BeFalse();
        outcome.Refusal.Should().Contain("cannot be reached");
        (await store.OpenRunsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_detached_head_refuses_and_never_records_head_as_the_base_branch()
    {
        var item = ReadyMarked(1, 1);
        var readiness = new FakeDeliveryReadiness(() => new AutopilotDeliveryReadiness(true, "github.com"));
        var dispatcher = Build(
            [item], AutopilotDeliveryMode.BranchAndPullRequest, readiness, out var store,
            workspaceStatus: new FakeWorkspaceStatus(branch: "HEAD"));

        var outcome = await dispatcher.DispatchAsync(Project, AutopilotDispatchSettings.Default);

        outcome.Dispatched.Should().BeFalse();
        outcome.Refusal.Should().Contain("not on a branch");
        (await store.OpenRunsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_reachable_workspace_with_no_readable_branch_refuses_rather_than_dispatch_with_nothing_to_return_to()
    {
        var item = ReadyMarked(1, 1);
        var readiness = new FakeDeliveryReadiness(() => new AutopilotDeliveryReadiness(true, "github.com"));
        var dispatcher = Build(
            [item], AutopilotDeliveryMode.BranchAndPullRequest, readiness, out var store,
            workspaceStatus: new FakeWorkspaceStatus(branch: string.Empty));

        var outcome = await dispatcher.DispatchAsync(Project, AutopilotDispatchSettings.Default);

        outcome.Dispatched.Should().BeFalse();
        outcome.Refusal.Should().Contain("branch could not be read");
        (await store.OpenRunsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_reachable_workspace_with_no_resolved_id_refuses_rather_than_dispatch_with_nothing_to_return_to()
    {
        var item = ReadyMarked(1, 1);
        var readiness = new FakeDeliveryReadiness(() => new AutopilotDeliveryReadiness(true, "github.com"));
        var dispatcher = Build(
            [item], AutopilotDeliveryMode.BranchAndPullRequest, readiness, out var store,
            workspaceStatus: new FakeWorkspaceStatus(workspaceId: null));

        var outcome = await dispatcher.DispatchAsync(Project, AutopilotDispatchSettings.Default);

        outcome.Dispatched.Should().BeFalse();
        outcome.Refusal.Should().Contain("own id could not be resolved");
        (await store.OpenRunsAsync()).Should().BeEmpty();
    }

    // ---- resuming a parked item from an ended run (ADR 0031 decision 12, #305) ----------------------------------

    private static WorkItem InProgressMarked(int number) =>
        WorkItem.Restore(new WorkItemState
        {
            Id = new WorkItemId($"item-{number}"),
            Project = Project,
            Number = number,
            Level = WorkItemLevel.Task,
            Title = "A title",
            Status = WorkItemStatus.InProgress,
            MarkedForAutopilot = true,
            Rank = number,
        }).Value;

    private static AutopilotRun EndedRun(ProjectId project, DateTimeOffset started, params AutopilotRunItem[] queue) =>
        new(Guid.NewGuid(), project, AutopilotDispatchSettings.Default, queue, AutopilotRunState.Stopped, started, started, []);

    [Fact]
    public async Task A_dispatch_with_nothing_fresh_but_one_resumable_parked_item_still_dispatches()
    {
        var parkedItem = InProgressMarked(1);
        var readiness = new FakeDeliveryReadiness(() => new AutopilotDeliveryReadiness(true, "github.com"));
        var dispatcher = Build([parkedItem], AutopilotDeliveryMode.BranchAndPullRequest, readiness, out var store);
        await store.SaveAsync(EndedRun(
            Project, DateTimeOffset.UtcNow.AddHours(-2),
            new AutopilotRunItem(parkedItem.Id, 0, AutopilotItemStage.Parked, 1m, 0, null, null, "needs a decision")));

        var outcome = await dispatcher.DispatchAsync(Project, AutopilotDispatchSettings.Default);

        outcome.Dispatched.Should().BeTrue();
        outcome.Run!.Queue.Select(i => i.Item).Should().Equal(parkedItem.Id);

        // #305's "an answer after parking resumes the item in the next run" is a FRESH attempt, never a
        // continuation: AutopilotRun.Start builds every item's queue entry the same way regardless of
        // whether it is fresh or resumed, so there is nothing of the old attempt to carry forward by
        // construction (a resumed item's own OLD session, if the workspace-return seam ever reached it,
        // #310, is a separate fact this dispatcher test does not exercise either way). The new run starts
        // this item exactly like any other marked item, Queued with no spend, no branch and no wait marker;
        // an answer a person gave reaches the resumed attempt only through the WORK ITEM itself (whatever
        // they wrote there), read fresh when the operator writes this run's own Prompting-stage prompt.
        var resumed = outcome.Run!.Queue.Single();
        resumed.Stage.Should().Be(AutopilotItemStage.Queued, "resuming starts a fresh attempt, it does not continue the old one");
        resumed.SpentUsd.Should().Be(0m);
        resumed.WaitingSinceUtc.Should().BeNull();
    }

    [Fact]
    public async Task A_resumed_item_and_a_fresh_ready_item_are_both_carried_in_board_order()
    {
        var parkedItem = InProgressMarked(1); // rank 1
        var freshItem = ReadyMarked(0.5, 2); // rank 0.5, ordered before the resumed item
        var readiness = new FakeDeliveryReadiness(() => new AutopilotDeliveryReadiness(true, "github.com"));
        var dispatcher = Build([parkedItem, freshItem], AutopilotDeliveryMode.BranchAndPullRequest, readiness, out var store);
        await store.SaveAsync(EndedRun(
            Project, DateTimeOffset.UtcNow.AddHours(-2),
            new AutopilotRunItem(parkedItem.Id, 0, AutopilotItemStage.Parked, 1m, 0, null, null, "needs a decision")));

        var outcome = await dispatcher.DispatchAsync(Project, AutopilotDispatchSettings.Default);

        outcome.Dispatched.Should().BeTrue();
        outcome.Run!.Queue.Select(i => i.Item).Should().Equal(freshItem.Id, parkedItem.Id);
    }

    [Fact]
    public async Task An_item_the_last_run_parked_but_a_person_unmarked_is_never_resumed()
    {
        var unmarkedItem = WorkItem.Restore(new WorkItemState
        {
            Id = new WorkItemId("item-1"),
            Project = Project,
            Number = 1,
            Level = WorkItemLevel.Task,
            Title = "A title",
            Status = WorkItemStatus.InProgress,
            MarkedForAutopilot = false,
            Rank = 1,
        }).Value;
        var readiness = new FakeDeliveryReadiness(() => new AutopilotDeliveryReadiness(true, "github.com"));
        var dispatcher = Build([unmarkedItem], AutopilotDeliveryMode.BranchAndPullRequest, readiness, out var store);
        await store.SaveAsync(EndedRun(
            Project, DateTimeOffset.UtcNow.AddHours(-2),
            new AutopilotRunItem(unmarkedItem.Id, 0, AutopilotItemStage.Parked, 1m, 0, null, null, "needs a decision")));

        var outcome = await dispatcher.DispatchAsync(Project, AutopilotDispatchSettings.Default);

        outcome.Dispatched.Should().BeFalse();
        outcome.Refusal.Should().Contain("marked for autopilot");
    }

    /// <summary>
    /// A resumed item's own run can stop while an earlier sibling is still open and current (a run-level
    /// limit, "stop now", cannot-resume), before this item is ever reached: it sits Queued with no
    /// StartedUtc, never named Parked by that run. A THIRD dispatch must still pick it up, or it is marked,
    /// In Progress, and permanently invisible to every dispatch from then on.
    /// </summary>
    [Fact]
    public async Task A_third_dispatch_resumes_an_item_its_own_resuming_run_left_queued_and_unreached()
    {
        var blockedBy = InProgressMarked(1);
        var neverReached = InProgressMarked(2);
        var readiness = new FakeDeliveryReadiness(() => new AutopilotDeliveryReadiness(true, "github.com"));
        var dispatcher = Build([blockedBy, neverReached], AutopilotDeliveryMode.BranchAndPullRequest, readiness, out var store);
        await store.SaveAsync(EndedRun(
            Project, DateTimeOffset.UtcNow.AddHours(-1),
            new AutopilotRunItem(blockedBy.Id, 0, AutopilotItemStage.Building, 10m, 0, DateTimeOffset.UtcNow.AddHours(-1), null, null),
            AutopilotRunItem.Queue(neverReached.Id, 1)));

        var outcome = await dispatcher.DispatchAsync(Project, AutopilotDispatchSettings.Default);

        outcome.Dispatched.Should().BeTrue();
        outcome.Run!.Queue.Select(i => i.Item).Should().Equal(neverReached.Id);
    }
}
