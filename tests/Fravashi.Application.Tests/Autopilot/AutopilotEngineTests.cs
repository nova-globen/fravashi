using AwesomeAssertions;
using Fravashi.Application.Autopilot;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Autopilot;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Xunit;

namespace Fravashi.Application.Tests.Autopilot;

/// <summary>
/// The engine (#297) as ADR 0031's deterministic state machine: it drives the four seams, and every limit,
/// checked after the seam call that just ran, overrides whatever that call decided. These tests exercise
/// exactly #297's own acceptance: each limit firing first stops the item (or the run) with a stop record naming
/// the limit and the figure, progress is seen both ways, and a restart resumes or stops honestly.
/// </summary>
public sealed class AutopilotEngineTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);
    private static readonly WorkItemId Item1 = new("item-1");
    private static readonly WorkItemId Item2 = new("item-2");

    private readonly FakeOperator _operator = new();
    private readonly FakeBuilder _builder = new();
    private readonly FakeCommitReviews _reviews = new();
    private readonly FakeDelivery _delivery = new();
    private readonly FakeRunStore _store = new();
    private readonly FixedClock _clock = new(Start);

    private AutopilotEngine Engine(IAutopilotWorkspaceReturn? workspaceReturn = null) =>
        new(_operator, _builder, _reviews, _delivery, _store, _clock, workspaceReturn);

    /// <summary>
    /// The run's own effective concurrency cap (#308), resolved once at dispatch and never read live: 1
    /// (the shipped default, and every test in this file that does not pass another) keeps the engine's
    /// original single-active-item behaviour; a test of #308's own parallel ticking passes a higher one.
    /// </summary>
    private static AutopilotRun RunOf(AutopilotLimits limits, params WorkItemId[] items) =>
        RunOf(limits, 1, items);

    private static AutopilotRun RunOf(AutopilotLimits limits, int effectiveConcurrencyCap, params WorkItemId[] items) =>
        AutopilotRun.Start(
            Guid.NewGuid(), ProjectId.Default,
            AutopilotDispatchSettings.Default with { Limits = limits, EffectiveConcurrencyCap = effectiveConcurrencyCap },
            items, Start);

    private static AutopilotRun RunWith(AutopilotLimits limits, params AutopilotRunItem[] queue) =>
        RunWith(limits, 1, queue);

    private static AutopilotRun RunWith(AutopilotLimits limits, int effectiveConcurrencyCap, params AutopilotRunItem[] queue) =>
        new(
            Guid.NewGuid(), ProjectId.Default,
            AutopilotDispatchSettings.Default with { Limits = limits, EffectiveConcurrencyCap = effectiveConcurrencyCap },
            queue, AutopilotRunState.Running, Start, Start, []);

    // ---- the happy path, one stage per tick --------------------------------------------------------------------

    [Fact]
    public async Task A_single_item_moves_through_every_stage_in_order_and_the_run_completes()
    {
        var run = RunOf(AutopilotLimits.Default, Item1);
        var engine = Engine();

        run = await engine.TickAsync(run); // Queued -> Prompting
        run.CurrentItem!.Stage.Should().Be(AutopilotItemStage.Prompting);

        run = await engine.TickAsync(run); // Prompting -> Building
        run.CurrentItem!.Stage.Should().Be(AutopilotItemStage.Building);

        run = await engine.TickAsync(run); // Building -> Reviewing (a commit landed: progress)
        run.CurrentItem!.Stage.Should().Be(AutopilotItemStage.Reviewing);
        run.CurrentItem!.BranchHeadSha.Should().Be("sha-1");
        run.LastProgressUtc.Should().Be(Start);

        run = await engine.TickAsync(run); // Reviewing -> Delivering
        run.CurrentItem!.Stage.Should().Be(AutopilotItemStage.Delivering);

        run = await engine.TickAsync(run); // Delivering -> Verifying
        run.CurrentItem!.Stage.Should().Be(AutopilotItemStage.Verifying);

        run = await engine.TickAsync(run); // Verifying -> Done (the queue shrank: progress)
        run.Queue.Single().Stage.Should().Be(AutopilotItemStage.Done);
        run.State.Should().Be(AutopilotRunState.Completed);

        // A completed run does not change on another tick.
        var settled = await engine.TickAsync(run);
        settled.Should().Be(run);
    }

    // ---- item-scoped limits -------------------------------------------------------------------------------------

    [Fact]
    public async Task Money_per_item_stops_only_the_item_and_the_run_moves_to_the_next_one()
    {
        var limits = AutopilotLimits.Default with { MoneyPerItemUsd = 5m };
        var run = RunWith(
            limits,
            new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, null, null),
            AutopilotRunItem.Queue(Item2, 1));
        _builder.Result = (_, _) => new AutopilotBuildResult(true, "sha-x", CostUsd: 10m, NeedsPerson: false, Problem: null);

        var afterItem1 = await Engine().TickAsync(run);

        var item1 = afterItem1.Queue.Single(i => i.Item == Item1);
        item1.Stage.Should().Be(AutopilotItemStage.Stopped);
        afterItem1.State.Should().Be(AutopilotRunState.Running);
        afterItem1.Stops.Should().ContainSingle();
        afterItem1.Stops[0].Kind.Should().Be(AutopilotStopKind.MoneyPerItem);
        afterItem1.Stops[0].Scope.Should().Be(AutopilotStopScope.Item);
        afterItem1.Stops[0].Item.Should().Be(Item1);
        afterItem1.Stops[0].Figure.Should().Contain("$10.00").And.Contain("$5.00");

        var afterItem2Starts = await Engine().TickAsync(afterItem1);
        afterItem2Starts.CurrentItem!.Item.Should().Be(Item2);
        afterItem2Starts.CurrentItem!.Stage.Should().Be(AutopilotItemStage.Prompting);
    }

    [Fact]
    public async Task A_tick_on_an_earlier_item_leaves_the_queue_in_board_order()
    {
        // AutopilotRun.Queue is documented as board order; a tick replaces one item in place and must not move
        // it to the end, which is what naively appending the replacement would do.
        var run = RunWith(
            AutopilotLimits.Default,
            AutopilotRunItem.Queue(Item1, 0),
            AutopilotRunItem.Queue(Item2, 1));

        var result = await Engine().TickAsync(run); // Item1: Queued -> Prompting

        result.Queue.Select(i => i.Order).Should().Equal(0, 1);
        result.Queue.Select(i => i.Item).Should().Equal(Item1, Item2);
    }

    [Fact]
    public async Task Failed_commit_reviews_stop_the_item_once_it_reaches_its_ceiling()
    {
        var limits = AutopilotLimits.Default with { FailedCommitReviewsPerItem = 2 };
        var run = RunWith(limits, AutopilotRunItem.Queue(Item1, 0));
        _reviews.Result = _ => new CommitReview(CommitReviewOutcome.Failed, new CommitReviewerRole(true, AgentTool.Claude, "claude-sonnet-5"));

        var engine = Engine();
        for (var i = 0; i < 20 && run.CurrentItem is { Stage: not AutopilotItemStage.Stopped }; i++)
        {
            run = await engine.TickAsync(run);
        }

        var item = run.Queue.Single();
        item.Stage.Should().Be(AutopilotItemStage.Stopped);
        item.FailedReviews.Should().Be(2);
        run.Stops.Should().ContainSingle(s => s.Kind == AutopilotStopKind.FailedCommitReviews);
        run.Stops[0].Figure.Should().Contain("2");
    }

    /// <summary>
    /// #321: the real path a review-denied <c>attempt_commit</c> takes never reaches the Reviewing stage at
    /// all (the broker refuses the commit before the tool runs, backlog 243), so the only place the engine
    /// ever learns of it is <c>RunBuildAsync</c>'s own <see cref="AutopilotBuildResult.ReviewFailed"/> branch,
    /// never <see cref="Failed_commit_reviews_stop_the_item_once_it_reaches_its_ceiling"/>'s Reviewing-stage
    /// path above. The proof run's item failed review three times in a row this way and the limit never
    /// fired.
    /// </summary>
    [Fact]
    public async Task A_build_turn_the_reviewer_refused_before_a_commit_landed_counts_against_the_ceiling_too()
    {
        var limits = AutopilotLimits.Default with { FailedCommitReviewsPerItem = 2 };
        var run = RunWith(limits, AutopilotRunItem.Queue(Item1, 0));
        _builder.Result = (_, _) => new AutopilotBuildResult(
            CommitMade: false, BranchHeadSha: null, CostUsd: 0m, NeedsPerson: false,
            Problem: "the reviewer refused this commit", ReviewFailed: true);

        var engine = Engine();
        for (var i = 0; i < 20 && run.CurrentItem is { Stage: not AutopilotItemStage.Stopped }; i++)
        {
            run = await engine.TickAsync(run);
        }

        var item = run.Queue.Single();
        item.Stage.Should().Be(AutopilotItemStage.Stopped);
        item.FailedReviews.Should().Be(2);
        run.Stops.Should().ContainSingle(s => s.Kind == AutopilotStopKind.FailedCommitReviews);
        run.Stops[0].Figure.Should().Contain("2");
    }

    /// <summary>
    /// #321: a single turn can retry <c>attempt_commit</c> more than once before it ends with nothing
    /// committed, each attempt reviewed on its own, and <c>AgentAutopilotBuilder.NotCommitted</c> folds every
    /// failed one into <see cref="AutopilotBuildResult.FailedReviewCount"/> on that ONE result. This is the
    /// engine's own half of that: a single tick carrying a count above one must still fold the WHOLE count in
    /// at once, which is what actually stops the item the instant its ceiling is reached rather than one tick
    /// late, or not at all if nothing else ever drives a further tick.
    /// </summary>
    [Fact]
    public async Task A_single_tick_whose_turn_failed_review_more_than_once_counts_every_one_of_them_at_once()
    {
        var limits = AutopilotLimits.Default with { FailedCommitReviewsPerItem = 2 };
        var run = RunWith(limits, new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, null, null));
        _builder.Result = (_, _) => new AutopilotBuildResult(
            CommitMade: false, BranchHeadSha: null, CostUsd: 0m, NeedsPerson: false,
            Problem: "two attempts in this one turn both failed review", ReviewFailed: true, FailedReviewCount: 2);

        run = await Engine().TickAsync(run);

        var item = run.Queue.Single();
        item.FailedReviews.Should().Be(2);
        item.Stage.Should().Be(AutopilotItemStage.Stopped);
        run.Stops.Should().ContainSingle(s => s.Kind == AutopilotStopKind.FailedCommitReviews);
        run.Stops[0].Figure.Should().Contain("2");
    }

    /// <summary>
    /// #321: the figure records what really happened, not a count capped at the limit: a turn that failed
    /// review three times against a ceiling of two still stopped the item (it was already past the ceiling
    /// after the second), but the record says three, because that is how many actually failed.
    /// </summary>
    [Fact]
    public async Task A_turn_with_more_failed_reviews_than_the_limit_reports_the_real_total_not_a_clamped_one()
    {
        var limits = AutopilotLimits.Default with { FailedCommitReviewsPerItem = 2 };
        var run = RunWith(limits, new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, null, null));
        _builder.Result = (_, _) => new AutopilotBuildResult(
            CommitMade: false, BranchHeadSha: null, CostUsd: 0m, NeedsPerson: false,
            Problem: "three attempts in this one turn all failed review", ReviewFailed: true, FailedReviewCount: 3);

        run = await Engine().TickAsync(run);

        var item = run.Queue.Single();
        item.FailedReviews.Should().Be(3, "every failed review counts, even past the limit");
        item.Stage.Should().Be(AutopilotItemStage.Stopped);
        run.Stops[0].Figure.Should().Contain("3");
    }

    /// <summary>
    /// #321: an EARLIER attempt_commit in a turn can fail review before a LATER one lands the commit; that
    /// earlier failure is folded onto <see cref="AutopilotBuildResult.FailedReviewCount"/> alongside
    /// <see cref="AutopilotBuildResult.CommitMade"/>, and the engine must count it on the SAME tick the
    /// commit moves the item into Reviewing, not drop it because the turn also made progress.
    /// </summary>
    [Fact]
    public async Task A_commit_that_landed_after_an_earlier_failed_review_in_the_same_turn_still_counts_that_failure()
    {
        var run = RunWith(AutopilotLimits.Default, new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, null, null));
        _builder.Result = (_, _) => new AutopilotBuildResult(
            CommitMade: true, BranchHeadSha: "sha-1", CostUsd: 0m, NeedsPerson: false, Problem: null, FailedReviewCount: 1);

        run = await Engine().TickAsync(run);

        var item = run.Queue.Single();
        item.Stage.Should().Be(AutopilotItemStage.Reviewing);
        item.FailedReviews.Should().Be(1, "the earlier failure in this same turn must not be dropped just because the turn also landed a commit");
    }

    /// <summary>
    /// #321 (fourth pass): a Failed review captured earlier in a turn that then also asks a person about
    /// something else must count WHILE the item waits, not only once that wait eventually settles, or the
    /// limit never fires during exactly the shape the proof run's own evidence describes (a builder asking a
    /// person around the same time as the uncounted failures). ADR 0031 decision 6 ("whichever limit fires
    /// first") already lets money and time stop an item mid-wait; this is the same rule for failed reviews.
    /// </summary>
    [Fact]
    public async Task A_needs_person_result_carrying_a_failed_review_count_at_the_ceiling_stops_the_item_mid_wait()
    {
        var limits = AutopilotLimits.Default with { FailedCommitReviewsPerItem = 2 };
        var run = RunWith(limits, new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, null, null));
        _builder.Result = (_, _) => new AutopilotBuildResult(
            CommitMade: false, BranchHeadSha: null, CostUsd: 0m, NeedsPerson: true,
            Problem: "the building session is waiting on a person about something else", FailedReviewCount: 2);

        run = await Engine().TickAsync(run);

        var item = run.Queue.Single();
        item.FailedReviews.Should().Be(2, "the count must be folded in before the wait is even considered");
        item.Stage.Should().Be(AutopilotItemStage.Stopped, "the limit fires the moment it is reached, even while the item is otherwise waiting on a person");
        run.Stops.Should().ContainSingle(s => s.Kind == AutopilotStopKind.FailedCommitReviews);
        run.Stops[0].Figure.Should().Contain("2");
    }

    [Fact]
    public async Task Time_per_item_overrides_whatever_the_stage_action_just_decided()
    {
        var limits = AutopilotLimits.Default with { TimePerItem = TimeSpan.FromMinutes(30) };
        var run = RunWith(limits, new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, null, null));
        _clock.Now = Start.AddMinutes(31);
        // The builder reports a clean success; the time limit still wins, because it is checked after every action.
        _builder.Result = (_, _) => new AutopilotBuildResult(true, "sha-1", 0m, false, null);

        var result = await Engine().TickAsync(run);

        var item = result.Queue.Single();
        item.Stage.Should().Be(AutopilotItemStage.Stopped);
        // Two stops, not one: the item limit, and then the queue-exhausted stop that ends the run because this
        // was its only item and it did not reach Done. See AutopilotDisciplineTests for Conclude on its own.
        result.Stops.Should().HaveCount(2);
        result.Stops[0].Kind.Should().Be(AutopilotStopKind.TimePerItem);
        result.Stops[0].Scope.Should().Be(AutopilotStopScope.Item);
    }

    // ---- run-scoped limits --------------------------------------------------------------------------------------

    [Fact]
    public async Task Money_per_run_stops_the_whole_run_once_the_queues_total_spending_passes_its_ceiling()
    {
        var limits = AutopilotLimits.Default with { MoneyPerItemUsd = 1000m, MoneyPerRunUsd = 100m };
        var run = RunWith(
            limits,
            new AutopilotRunItem(Item1, 0, AutopilotItemStage.Done, 60m, 0, Start, "sha-done", null),
            new AutopilotRunItem(Item2, 1, AutopilotItemStage.Building, 0m, 0, Start, null, null));
        _builder.Result = (_, _) => new AutopilotBuildResult(true, "sha-2", CostUsd: 50m, NeedsPerson: false, Problem: null);

        var result = await Engine().TickAsync(run);

        result.State.Should().Be(AutopilotRunState.Stopped);
        result.Stops.Should().ContainSingle();
        result.Stops[0].Kind.Should().Be(AutopilotStopKind.MoneyPerRun);
        result.Stops[0].Scope.Should().Be(AutopilotStopScope.Run);
        result.Stops[0].Item.Should().BeNull();
        result.Stops[0].Figure.Should().Contain("$110.00").And.Contain("$100.00");
    }

    [Fact]
    public async Task A_run_limit_crossed_on_the_same_tick_as_an_item_limit_stops_both_at_once()
    {
        // The item's own overspend is what pushes the run over its ceiling too; both must fire on this tick,
        // not the item on this one and the run one tick later with the next item already started.
        var limits = AutopilotLimits.Default with { MoneyPerItemUsd = 40m, MoneyPerRunUsd = 100m };
        var run = RunWith(
            limits,
            new AutopilotRunItem(Item1, 0, AutopilotItemStage.Done, 60m, 0, Start, "sha-done", null),
            new AutopilotRunItem(Item2, 1, AutopilotItemStage.Building, 0m, 0, Start, null, null));
        _builder.Result = (_, _) => new AutopilotBuildResult(true, "sha-2", CostUsd: 50m, NeedsPerson: false, Problem: null);

        var result = await Engine().TickAsync(run);

        result.State.Should().Be(AutopilotRunState.Stopped);
        result.Stops.Should().HaveCount(2);
        result.Stops[0].Kind.Should().Be(AutopilotStopKind.MoneyPerItem);
        result.Stops[0].Scope.Should().Be(AutopilotStopScope.Item);
        result.Stops[1].Kind.Should().Be(AutopilotStopKind.MoneyPerRun);
        result.Stops[1].Scope.Should().Be(AutopilotStopScope.Run);
        result.Stops[0].AtUtc.Should().Be(result.Stops[1].AtUtc);
        result.Queue.Single(i => i.Item == Item2).Stage.Should().Be(AutopilotItemStage.Stopped);
    }

    [Fact]
    public async Task No_progress_stops_the_run_when_neither_the_queue_shrinks_nor_a_commit_lands()
    {
        var limits = AutopilotLimits.Default with { NoProgressWindow = TimeSpan.FromMinutes(30) };
        var run = RunWith(limits, new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, null, null));
        _clock.Now = Start.AddMinutes(31);
        // No commit, and the operator says try again: the build keeps failing quietly, but nothing moves.
        _builder.Result = (_, _) => new AutopilotBuildResult(false, null, 0m, false, "no commit yet");
        _operator.Advice = (_, _) => new AutopilotAdvice(RetryBuild: true);

        var result = await Engine().TickAsync(run);

        result.State.Should().Be(AutopilotRunState.Stopped);
        result.Stops.Should().ContainSingle(s => s.Kind == AutopilotStopKind.NoProgress);
        result.Queue.Single().Stage.Should().Be(AutopilotItemStage.Building, "the item itself was not stopped, only the run");
    }

    [Fact]
    public async Task A_commit_landing_counts_as_progress_and_resets_the_no_progress_window()
    {
        var limits = AutopilotLimits.Default with { NoProgressWindow = TimeSpan.FromMinutes(30) };
        var run = RunWith(limits, new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, null, null));
        _builder.Result = (_, _) => new AutopilotBuildResult(true, "sha-1", 0m, false, null);
        _clock.Now = Start.AddMinutes(10); // well short of the 30 minute window, but moved forward from Start

        var result = await Engine().TickAsync(run);

        result.LastProgressUtc.Should().Be(Start.AddMinutes(10), "a commit landing on the branch is progress");
        result.State.Should().Be(AutopilotRunState.Running);
    }

    [Fact]
    public async Task Progress_is_stamped_at_the_moment_the_seam_call_actually_finished_not_when_the_tick_started()
    {
        // A build that takes 40 minutes must stamp progress at ITS end, not at the tick's start: reading the
        // clock before awaiting the seam would make the very next tick see that stamp as 40 minutes stale and
        // stop the run on no progress for a run that just committed.
        var limits = AutopilotLimits.Default with { NoProgressWindow = TimeSpan.FromMinutes(30) };
        var run = RunWith(limits, new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, null, null));
        _builder.Result = (_, _) =>
        {
            _clock.Now = Start.AddMinutes(40); // the build itself took 40 minutes
            return new AutopilotBuildResult(true, "sha-1", 0m, false, null);
        };

        var afterBuild = await Engine().TickAsync(run);

        afterBuild.LastProgressUtc.Should().Be(Start.AddMinutes(40));
        afterBuild.State.Should().Be(AutopilotRunState.Running);

        _reviews.Result = _ => new CommitReview(CommitReviewOutcome.Passed, new CommitReviewerRole(true, AgentTool.Claude, "claude-sonnet-5"));
        _clock.Now = Start.AddMinutes(41);

        var afterReview = await Engine().TickAsync(afterBuild);

        afterReview.State.Should().Be(AutopilotRunState.Running, "the build that just committed is not 40 minutes of no progress");
        afterReview.Stops.Should().BeEmpty();
    }

    [Fact]
    public async Task A_builder_that_reports_a_commit_at_the_same_head_is_not_progress_and_no_progress_still_fires()
    {
        // Progress is the branch actually moving, not the builder's word for it: a result that reports a commit
        // but names the SAME head the item already held must not reset the no-progress window.
        var limits = AutopilotLimits.Default with { NoProgressWindow = TimeSpan.FromMinutes(30) };
        var run = RunWith(limits, new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, "sha-same", null));
        _builder.Result = (_, _) => new AutopilotBuildResult(true, "sha-same", 0m, false, null);
        _clock.Now = Start.AddMinutes(31);

        var result = await Engine().TickAsync(run);

        result.Queue.Single().Stage.Should().Be(AutopilotItemStage.Reviewing);
        result.LastProgressUtc.Should().Be(Start, "the branch head did not actually move, so reporting a commit is not progress");
        result.State.Should().Be(AutopilotRunState.Stopped);
        result.Stops.Should().ContainSingle(s => s.Kind == AutopilotStopKind.NoProgress);
    }

    [Fact]
    public async Task A_resumed_run_does_not_stop_on_no_progress_from_time_the_studio_was_simply_not_running()
    {
        var limits = AutopilotLimits.Default with { NoProgressWindow = TimeSpan.FromMinutes(30) };
        var run = RunWith(limits, new AutopilotRunItem(Item1, 0, AutopilotItemStage.Prompting, 0m, 0, Start, null, null));
        await _store.SaveAsync(run);
        _clock.Now = Start.AddHours(6); // long downtime, well past the no-progress window

        var engine = Engine();
        await engine.ResumeAllAsync();
        var resumed = (await _store.GetAsync(run.Id))!;
        resumed.State.Should().Be(AutopilotRunState.Running);

        var ticked = await engine.TickAsync(resumed);

        ticked.State.Should().Be(AutopilotRunState.Running, "the outage itself must not be read as the run having stalled");
    }

    [Fact]
    public async Task The_queue_shrinking_to_done_counts_as_progress_with_the_clock_actually_moved()
    {
        // The clock is past the no-progress window, but the item is about to finish: the queue shrinking must
        // itself count as the progress that resets the window, in the SAME tick, or a run that finishes its last
        // item late would stop on no-progress instead of completing.
        var limits = AutopilotLimits.Default with { NoProgressWindow = TimeSpan.FromMinutes(30) };
        var run = RunWith(limits, new AutopilotRunItem(Item1, 0, AutopilotItemStage.Verifying, 0m, 0, Start, "sha-1", null));
        _clock.Now = Start.AddMinutes(31);
        _delivery.Verify = (_, _) => new AutopilotVerifyResult(Passed: true, NeedsPerson: false, Problem: null);

        var result = await Engine().TickAsync(run);

        result.Queue.Single().Stage.Should().Be(AutopilotItemStage.Done);
        result.LastProgressUtc.Should().Be(Start.AddMinutes(31));
        result.Stops.Should().BeEmpty("the queue shrinking landed before the no-progress check ran, so it never fired");
        result.State.Should().Be(AutopilotRunState.Completed);
    }

    // ---- waiting and parking (ADR 0031 decision 12, #305) -------------------------------------------------------

    [Fact]
    public async Task A_builder_that_needs_a_person_waits_in_its_stage_rather_than_parking_at_once()
    {
        var run = RunWith(
            AutopilotLimits.Default,
            new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, null, null),
            AutopilotRunItem.Queue(Item2, 1));
        _builder.Result = (_, _) => new AutopilotBuildResult(false, null, 0m, NeedsPerson: true, Problem: "needs a decision");

        var result = await Engine().TickAsync(run);

        result.State.Should().Be(AutopilotRunState.Running);
        var item = result.Queue.Single(i => i.Item == Item1);
        item.Stage.Should().Be(AutopilotItemStage.Building, "the run waits rather than parking at once");
        item.ParkedReason.Should().BeNull();
        item.WaitingSinceUtc.Should().Be(Start);
        result.LastProgressUtc.Should().Be(Start, "waiting, within the timeout, is not stagnation");
    }

    [Fact]
    public async Task An_answer_within_the_wait_timeout_continues_the_item_in_the_same_session()
    {
        // ADR 0031 decision 12's acceptance: "a wait answered in time continues the item." The fake builder's
        // second call stands in for the person having answered the pending approval: the SAME session, asked
        // again, now reports a commit instead of NeedsPerson.
        var run = RunWith(
            AutopilotLimits.Default, new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, null, null));
        _builder.Result = (_, _) => new AutopilotBuildResult(false, null, 0m, NeedsPerson: true, Problem: "needs a decision");

        var waiting = await Engine().TickAsync(run);
        waiting.CurrentItem!.Stage.Should().Be(AutopilotItemStage.Building);
        waiting.CurrentItem!.WaitingSinceUtc.Should().Be(Start);

        _clock.Now = Start.AddMinutes(10); // well within the default one-hour PersonWaitTimeout
        _builder.Result = (_, _) => new AutopilotBuildResult(true, "sha-answered", CostUsd: 0m, NeedsPerson: false, Problem: null);
        var answered = await Engine().TickAsync(waiting);

        answered.CurrentItem!.Stage.Should().Be(AutopilotItemStage.Reviewing);
        answered.CurrentItem!.BranchHeadSha.Should().Be("sha-answered");
        answered.CurrentItem!.WaitingSinceUtc.Should().BeNull("the wait is over once the item moved on");
        answered.Stops.Should().BeEmpty("answered in time, so nothing ever timed out");
    }

    [Fact]
    public async Task A_wait_that_runs_out_parks_the_item_with_a_stop_and_the_run_moves_to_the_next_item()
    {
        // A generous NoProgressWindow keeps this test about the person-wait timeout alone: the single clock
        // jump below simulates an engine that only ticks this run twice, rather than every few seconds as the
        // real hosted service does, and a tighter window would let the run-level no-progress stop fire in the
        // very same tick as the timeout, for a reason that has nothing to do with what this test is about.
        var limits = AutopilotLimits.Default with { NoProgressWindow = TimeSpan.FromHours(3) };
        var run = RunWith(
            limits,
            new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, null, null),
            AutopilotRunItem.Queue(Item2, 1));
        _builder.Result = (_, _) => new AutopilotBuildResult(false, null, 0m, NeedsPerson: true, Problem: "needs a decision");
        var engine = Engine();

        var waiting = await engine.TickAsync(run);
        waiting.CurrentItem!.Item.Should().Be(Item1);

        _clock.Now = Start.Add(AutopilotDispatchSettings.Default.PersonWaitTimeout).AddMinutes(1);
        var timedOut = await engine.TickAsync(waiting);

        var item1 = timedOut.Queue.Single(i => i.Item == Item1);
        item1.Stage.Should().Be(AutopilotItemStage.Parked);
        item1.ParkedReason.Should().Be("needs a decision");
        item1.WaitingSinceUtc.Should().BeNull();
        timedOut.Stops.Should().ContainSingle(s => s.Kind == AutopilotStopKind.PersonWaitTimedOut && s.Item == Item1);
        timedOut.State.Should().Be(AutopilotRunState.Running, "the run moves on to the next marked item");

        var afterItem2Starts = await engine.TickAsync(timedOut);
        afterItem2Starts.CurrentItem!.Item.Should().Be(Item2);
    }

    [Fact]
    public async Task A_run_whose_only_item_times_out_waiting_ends_the_run_saying_so_after_the_wait_not_instead_of_it()
    {
        var limits = AutopilotLimits.Default with { NoProgressWindow = TimeSpan.FromHours(3) };
        var run = RunWith(limits, new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, null, null));
        _builder.Result = (_, _) => new AutopilotBuildResult(false, null, 0m, NeedsPerson: true, Problem: "needs a decision");
        var engine = Engine();

        var waiting = await engine.TickAsync(run);
        waiting.State.Should().Be(AutopilotRunState.Running, "it waits first, it does not park or end on this tick");

        _clock.Now = Start.Add(AutopilotDispatchSettings.Default.PersonWaitTimeout).AddMinutes(1);
        var result = await engine.TickAsync(waiting);

        result.Queue.Single().Stage.Should().Be(AutopilotItemStage.Parked, "parked items stay listed in the run's own queue");
        result.State.Should().Be(AutopilotRunState.Stopped);
        result.Stops.Should().Contain(s => s.Kind == AutopilotStopKind.PersonWaitTimedOut)
            .And.Contain(s => s.Kind == AutopilotStopKind.QueueExhausted);
    }

    [Fact]
    public async Task Waiting_past_the_shipped_no_progress_window_but_within_the_person_wait_timeout_never_stops_the_run()
    {
        // The shipped NoProgressWindow (30 minutes) is shorter than the shipped PersonWaitTimeout (one hour):
        // without the exemption, a run legitimately waiting on a person for 31 minutes would stop itself on
        // no-progress alone, well before ADR decision 12's own hour is up.
        var run = RunWith(AutopilotLimits.Default, new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, null, null));
        _builder.Result = (_, _) => new AutopilotBuildResult(false, null, 0m, NeedsPerson: true, Problem: "needs a decision");
        var engine = Engine();

        var waiting = await engine.TickAsync(run);
        _clock.Now = Start.AddMinutes(31);
        var stillWaiting = await engine.TickAsync(waiting);

        stillWaiting.State.Should().Be(AutopilotRunState.Running);
        stillWaiting.Stops.Should().BeEmpty();
        stillWaiting.LastProgressUtc.Should().Be(Start.AddMinutes(31), "a continuing wait counts as progress");
    }

    [Fact]
    public async Task An_item_limit_firing_while_an_item_waits_stops_it_and_clears_the_stale_wait_marker()
    {
        // TimePerItem is checked against StartedUtc, not WaitingSinceUtc, so it can fire on the SAME tick
        // that keeps the item waiting on a person: Settle's own item-stop branch must still clear
        // WaitingSinceUtc when it does, or a Stopped item would carry a stale wait marker nothing ever reads
        // again but that the record itself claims can never happen once Stage leaves Building.
        var limits = AutopilotLimits.Default with { TimePerItem = TimeSpan.FromMinutes(30) };
        var run = RunWith(limits, new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, null, null));
        _builder.Result = (_, _) => new AutopilotBuildResult(false, null, 0m, NeedsPerson: true, Problem: "needs a decision");
        var engine = Engine();

        var waiting = await engine.TickAsync(run); // starts the wait clock
        waiting.CurrentItem!.WaitingSinceUtc.Should().Be(Start);

        _clock.Now = Start.AddMinutes(31); // past TimePerItem, still well within the default PersonWaitTimeout
        var result = await engine.TickAsync(waiting);

        var item = result.Queue.Single();
        item.Stage.Should().Be(AutopilotItemStage.Stopped, "the time-per-item limit fired, not the person-wait timeout");
        item.WaitingSinceUtc.Should().BeNull("Stopped is terminal; nothing may read a stale wait marker off it");
        result.Stops.Should().Contain(s => s.Kind == AutopilotStopKind.TimePerItem);
    }

    [Fact]
    public async Task A_run_limit_firing_on_the_very_first_wait_tick_clears_the_items_stale_wait_marker_too()
    {
        // The builder's own pre-approval cost folds into the item on the SAME tick ApplyPersonWait first
        // starts the wait (the item stays open, in Building), so a run-level limit checked right after can
        // fire while that item carries a fresh WaitingSinceUtc; Settle's run-stop branch must clear it too,
        // not only the item-stop branch right above it.
        var limits = AutopilotLimits.Default with { MoneyPerRunUsd = 5m };
        var run = RunWith(limits, new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, null, null));
        _builder.Result = (_, _) => new AutopilotBuildResult(false, null, CostUsd: 10m, NeedsPerson: true, Problem: "needs a decision");

        var result = await Engine().TickAsync(run);

        var item = result.Queue.Single();
        item.Stage.Should().Be(AutopilotItemStage.Building, "the run limit stopped the RUN, not this item");
        item.WaitingSinceUtc.Should().BeNull("the run around it is Stopped and terminal; nothing may read a stale wait marker off it");
        result.State.Should().Be(AutopilotRunState.Stopped);
        result.Stops.Should().ContainSingle(s => s.Kind == AutopilotStopKind.MoneyPerRun);
    }

    [Fact]
    public async Task A_failed_build_with_no_commit_and_no_person_also_asks_the_operator_in_the_same_tick()
    {
        // The one named exception to "one call per tick": advice about a result is asked for in the tick that
        // produced it, not deferred to a tick of its own. When the advice is to retry, the item stays in
        // Building: that is the effect of the advice, not merely that it was asked for.
        var run = RunWith(AutopilotLimits.Default, new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, null, null));
        var builderCalled = false;
        var operatorAskedFor = false;
        _builder.Result = (_, _) =>
        {
            builderCalled = true;
            return new AutopilotBuildResult(false, null, 0m, NeedsPerson: false, Problem: "no commit yet");
        };
        _operator.Advice = (_, failure) =>
        {
            operatorAskedFor = true;
            failure.Should().Be("no commit yet");
            return new AutopilotAdvice(RetryBuild: true);
        };

        var result = await Engine().TickAsync(run);

        builderCalled.Should().BeTrue();
        operatorAskedFor.Should().BeTrue();
        result.Queue.Single().Stage.Should().Be(AutopilotItemStage.Building, "the advice was to retry");
    }

    [Fact]
    public async Task Advice_against_retrying_parks_the_item_at_once_with_the_operators_reason_and_the_run_moves_on()
    {
        // Unlike the builder's own NeedsPerson result, the operator's "no retry warranted" advice is not a
        // live, pollable wait (RunBuildAsync's own doc comment explains why): the session's turn has already
        // ended, so there is nothing left open for a person to answer, and this parks at once, exactly as
        // before ADR 0031 decision 12 (#305) landed.
        var run = RunWith(
            AutopilotLimits.Default,
            new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, null, null),
            AutopilotRunItem.Queue(Item2, 1));
        _builder.Result = (_, _) => new AutopilotBuildResult(false, null, 0m, NeedsPerson: false, Problem: "no commit yet");
        _operator.Advice = (_, _) => new AutopilotAdvice(RetryBuild: false, Reason: "needs a redesign");

        var afterItem1 = await Engine().TickAsync(run);

        var item1 = afterItem1.Queue.Single(i => i.Item == Item1);
        item1.Stage.Should().Be(AutopilotItemStage.Parked);
        item1.ParkedReason.Should().Be("needs a redesign");
        afterItem1.State.Should().Be(AutopilotRunState.Running);

        var afterItem2Starts = await Engine().TickAsync(afterItem1);
        afterItem2Starts.CurrentItem!.Item.Should().Be(Item2);
    }

    // ---- the operator's own advice-call cost (#300) ----------------------------------------------------------

    [Fact]
    public async Task The_operators_advice_cost_is_folded_into_the_items_spending_and_can_itself_trip_a_money_limit()
    {
        var limits = AutopilotLimits.Default with { MoneyPerItemUsd = 5m };
        var run = RunWith(limits, new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, null, null));
        _builder.Result = (_, _) => new AutopilotBuildResult(false, null, 0m, NeedsPerson: false, Problem: "no commit yet");
        _operator.Advice = (_, _) => new AutopilotAdvice(RetryBuild: true, CostUsd: 10m);

        var result = await Engine().TickAsync(run);

        var item = result.Queue.Single();
        item.SpentUsd.Should().Be(10m, "the advice call's own cost must land on the item, not only the builder's");
        item.Stage.Should().Be(AutopilotItemStage.Stopped);
        result.Stops.Should().ContainSingle(s => s.Kind == AutopilotStopKind.MoneyPerItem);
        result.Stops[0].Figure.Should().Contain("$10.00").And.Contain("$5.00");
    }

    [Fact]
    public async Task A_parked_items_spending_includes_the_operators_advice_cost()
    {
        var run = RunWith(
            AutopilotLimits.Default, new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, null, null));
        _builder.Result = (_, _) => new AutopilotBuildResult(false, null, 0m, NeedsPerson: false, Problem: "no commit yet");
        _operator.Advice = (_, _) => new AutopilotAdvice(RetryBuild: false, Reason: "needs a redesign", CostUsd: 2m);

        var result = await Engine().TickAsync(run);

        var item = result.Queue.Single();
        item.Stage.Should().Be(AutopilotItemStage.Parked);
        item.ParkedReason.Should().Be("needs a redesign");
        item.SpentUsd.Should().Be(2m, "the advice call's own cost must be on the item even when it is the one that parks it");
    }

    [Theory]
    [InlineData("the document was not valid JSON")]
    [InlineData("the tracker could not be reached")]
    public async Task A_malformed_or_missing_prompt_answer_stops_the_item_with_a_stop_naming_the_operator(string message)
    {
        // Neither message mentions "operator" itself, unlike the real implementation's own exception text: the
        // record must name the operator because RunPromptAsync wraps the call (the same way RunBuildAsync wraps
        // the advice call), not because an exception happens to say so. This is the Prompting stage's own
        // version of the acceptance "a malformed answer and no answer each stop the item with a stop record
        // naming the operator": the existing seam-throw tests cover only the builder and the advice call.
        var run = RunWith(AutopilotLimits.Default, new AutopilotRunItem(Item1, 0, AutopilotItemStage.Prompting, 0m, 0, Start, null, null));
        _operator.Prompt = _ => throw new InvalidOperationException(message);

        var result = await Engine().TickAsync(run);

        var item = result.Queue.Single();
        item.Stage.Should().Be(AutopilotItemStage.Stopped);
        result.Stops.Should().ContainSingle(s => s.Kind == AutopilotStopKind.SeamFailed);
        result.Stops[0].Figure.Should().Contain("operator").And.Contain(message);
    }

    [Fact]
    public async Task A_seam_that_throws_stops_the_item_rather_than_being_silently_repeated_on_the_next_tick()
    {
        // Without a recorded stop, the item would stay in Building and the next tick would call the builder
        // again, which is exactly the duplicate (a second commit) the restart rule exists to prevent. A second
        // item is queued so the run itself stays Running (Conclude would otherwise end it too, confounding
        // whether item1 specifically was skipped).
        var run = RunWith(
            AutopilotLimits.Default,
            new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, null, null),
            AutopilotRunItem.Queue(Item2, 1));
        var calls = 0;
        _builder.Result = (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("the agent process crashed");
        };

        var result = await Engine().TickAsync(run);

        var item1 = result.Queue.Single(i => i.Item == Item1);
        item1.Stage.Should().Be(AutopilotItemStage.Stopped);
        result.Stops.Should().ContainSingle(s => s.Kind == AutopilotStopKind.SeamFailed);
        result.Stops[0].Reason.Should().Contain("duplicate");
        result.State.Should().Be(AutopilotRunState.Running);
        calls.Should().Be(1);

        var afterItem2Starts = await Engine().TickAsync(result);

        calls.Should().Be(1, "item1 already stopped; the run moved on to item2 instead of calling the builder again");
        afterItem2Starts.CurrentItem!.Item.Should().Be(Item2);
    }

    [Fact]
    public async Task A_cost_already_spent_in_the_tick_is_not_dropped_when_a_later_call_in_that_same_tick_throws()
    {
        // Building makes two calls in one tick (the builder, then the operator's advice). The builder's cost
        // must still land on the item, and from there on the run's total, even though it is the SECOND call
        // that throws.
        var limits = AutopilotLimits.Default with { MoneyPerRunUsd = 5m };
        var run = RunWith(limits, new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, null, null));
        _builder.Result = (_, _) => new AutopilotBuildResult(false, null, CostUsd: 10m, NeedsPerson: false, Problem: "no commit yet");
        _operator.Advice = (_, _) => throw new InvalidOperationException("the operator process crashed");

        var result = await Engine().TickAsync(run);

        var item = result.Queue.Single();
        item.SpentUsd.Should().Be(10m, "the builder's cost must not be dropped just because the follow-up advice call threw");
        item.Stage.Should().Be(AutopilotItemStage.Stopped);
        result.Stops.Should().HaveCount(2);
        result.Stops[0].Kind.Should().Be(AutopilotStopKind.SeamFailed);
        result.Stops[0].Figure.Should().Contain("the operator's advice");
        result.Stops[1].Kind.Should().Be(AutopilotStopKind.MoneyPerRun);
        result.State.Should().Be(AutopilotRunState.Stopped);
    }

    // ---- a run with nothing left open must not idle forever ------------------------------------------------------

    [Fact]
    public async Task A_run_whose_only_item_stops_on_an_item_limit_ends_the_run_instead_of_idling()
    {
        var limits = AutopilotLimits.Default with { MoneyPerItemUsd = 5m };
        var run = RunWith(limits, new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, null, null));
        _builder.Result = (_, _) => new AutopilotBuildResult(true, "sha-x", CostUsd: 10m, NeedsPerson: false, Problem: null);

        var result = await Engine().TickAsync(run);

        result.Queue.Single().Stage.Should().Be(AutopilotItemStage.Stopped);
        result.State.Should().Be(AutopilotRunState.Stopped);
        result.Stops.Should().HaveCount(2);
        result.Stops[0].Kind.Should().Be(AutopilotStopKind.MoneyPerItem);
        result.Stops[1].Kind.Should().Be(AutopilotStopKind.QueueExhausted);

        await _store.SaveAsync(result);
        (await _store.OpenRunsAsync()).Should().BeEmpty("a run that has ended must not be offered to resume at the next start");
    }

    // ---- #308 host note point 8: a worktree (through IAutopilotBuilder.EndItemAsync) is pruned once its item ends ---

    [Fact]
    public async Task An_item_reaching_done_calls_end_item_on_the_builder()
    {
        var run = RunWith(
            AutopilotLimits.Default, new AutopilotRunItem(Item1, 0, AutopilotItemStage.Verifying, 0m, 0, Start, "sha-1", null));
        _delivery.Verify = (_, _) => new AutopilotVerifyResult(Passed: true, NeedsPerson: false, Problem: null);

        var result = await Engine().TickAsync(run);

        result.Queue.Single().Stage.Should().Be(AutopilotItemStage.Done);
        _builder.Ended.Should().ContainSingle().Which.Should().Be(Item1);
    }

    /// <summary>
    /// #308 host note point 8 / plan @126 point 8: a prune refusal (a dirty worktree) is never silently
    /// swallowed; EndItemAsync RETURNS the words, and the engine folds them onto the run's own record, the
    /// same way a thrown exception from the seam already is.
    /// </summary>
    [Fact]
    public async Task An_end_item_refusal_is_recorded_on_the_run_rather_than_swallowed()
    {
        var run = RunWith(
            AutopilotLimits.Default, new AutopilotRunItem(Item1, 0, AutopilotItemStage.Verifying, 0m, 0, Start, "sha-1", null));
        _delivery.Verify = (_, _) => new AutopilotVerifyResult(Passed: true, NeedsPerson: false, Problem: null);
        _builder.EndItemResult = wid => $"item {wid.Value}: worktree not pruned: uncommitted changes";

        var result = await Engine().TickAsync(run);

        result.Queue.Single().Stage.Should().Be(AutopilotItemStage.Done);
        result.WorkspaceReturnNote.Should().Contain(Item1.Value).And.Contain("uncommitted changes");
    }

    [Fact]
    public async Task An_item_stopped_by_a_limit_calls_end_item_on_the_builder()
    {
        var limits = AutopilotLimits.Default with { MoneyPerItemUsd = 5m };
        var run = RunWith(limits, new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, null, null));
        _builder.Result = (_, _) => new AutopilotBuildResult(true, "sha-x", CostUsd: 10m, NeedsPerson: false, Problem: null);

        var result = await Engine().TickAsync(run);

        result.Queue.Single().Stage.Should().Be(AutopilotItemStage.Stopped);
        _builder.Ended.Should().ContainSingle().Which.Should().Be(Item1);
    }

    [Fact]
    public async Task An_item_parked_at_once_calls_end_item_on_the_builder()
    {
        var run = RunWith(AutopilotLimits.Default, new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, null, null));
        _builder.Result = (_, _) => new AutopilotBuildResult(false, null, 0m, NeedsPerson: false, Problem: "no commit yet");
        _operator.Advice = (_, _) => new AutopilotAdvice(RetryBuild: false, Reason: "needs a redesign");

        var result = await Engine().TickAsync(run);

        result.Queue.Single().Stage.Should().Be(AutopilotItemStage.Parked);
        _builder.Ended.Should().ContainSingle().Which.Should().Be(Item1);
    }

    [Fact]
    public async Task StopNowAsync_calls_end_item_on_the_builder_for_the_item_it_stops()
    {
        var run = RunWith(AutopilotLimits.Default, new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, null, null));
        await _store.SaveAsync(run);

        await Engine().StopNowAsync(run.Id);

        _builder.Ended.Should().ContainSingle().Which.Should().Be(Item1);
    }

    /// <summary>
    /// Under cap 1, OnRestart's own run-scoped stop deliberately leaves the item's OWN stage exactly as found
    /// (the record the stop itself names), so it never genuinely "closes" and EndItemAsync is rightly never
    /// called there; that run never gave the item a worktree to prune anyway (#308 decision 2). Above cap 1,
    /// the in-flight item's own stage DOES move to Stopped, which is the shape worth proving here.
    /// </summary>
    [Fact]
    public async Task ResumeAllAsync_calls_end_item_on_the_builder_for_a_parallel_item_that_cannot_resume()
    {
        var run = RunWith(
            AutopilotLimits.Default,
            2,
            new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, null, null),
            new AutopilotRunItem(Item2, 1, AutopilotItemStage.Prompting, 0m, 0, Start, null, null));
        await _store.SaveAsync(run);

        await Engine().ResumeAllAsync();

        _builder.Ended.Should().ContainSingle().Which.Should().Be(Item1);
    }

    // A run whose only item parks is covered under "waiting and parking" above
    // (A_run_whose_only_item_times_out_waiting_ends_the_run_saying_so_after_the_wait_not_instead_of_it), now that
    // parking is no longer a one-tick affair.

    // ---- restart (ADR 0031 decision 10, via the engine and the store) --------------------------------------------

    [Fact]
    public async Task Resuming_at_start_stops_a_run_caught_mid_an_in_flight_stage_and_leaves_another_running()
    {
        var inFlight = RunWith(AutopilotLimits.Default, new AutopilotRunItem(Item1, 0, AutopilotItemStage.Reviewing, 0m, 0, Start, "sha-1", null));
        var resumable = RunWith(AutopilotLimits.Default, new AutopilotRunItem(Item2, 0, AutopilotItemStage.Prompting, 0m, 0, Start, null, null));
        await _store.SaveAsync(inFlight);
        await _store.SaveAsync(resumable);
        _clock.Now = Start.AddMinutes(5);

        var resumed = await Engine().ResumeAllAsync();

        resumed.Should().HaveCount(2);
        var stoppedRun = await _store.GetAsync(inFlight.Id);
        stoppedRun!.State.Should().Be(AutopilotRunState.Stopped);
        stoppedRun.Stops.Should().ContainSingle(s => s.Kind == AutopilotStopKind.CannotResume);

        var stillRunning = await _store.GetAsync(resumable.Id);
        stillRunning!.State.Should().Be(AutopilotRunState.Running);
    }

    [Fact]
    public async Task TickAndSaveAsync_persists_the_ticked_run_through_the_store()
    {
        var run = RunOf(AutopilotLimits.Default, Item1);
        await _store.SaveAsync(run);

        var ticked = await Engine().TickAndSaveAsync(run.Id);

        ticked!.CurrentItem!.Stage.Should().Be(AutopilotItemStage.Prompting);
        (await _store.GetAsync(run.Id))!.CurrentItem!.Stage.Should().Be(AutopilotItemStage.Prompting);
    }

    // ---- stop (ADR 0031 decision 13, backlog 309) ---------------------------------------------------------------

    [Fact]
    public async Task Stop_now_ends_the_current_item_and_the_run_and_stops_the_builders_live_process_when_building()
    {
        var run = RunWith(
            AutopilotLimits.Default,
            new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 3m, 0, Start, null, null));
        await _store.SaveAsync(run);

        var stopped = await Engine().StopNowAsync(run.Id);

        stopped!.State.Should().Be(AutopilotRunState.Stopped);
        stopped.Queue.Single().Stage.Should().Be(AutopilotItemStage.Stopped);
        stopped.Stops.Should().ContainSingle(
            s => s.Kind == AutopilotStopKind.StoppedByPerson && s.Scope == AutopilotStopScope.Run && s.Item == Item1);
        _builder.Stopped.Should().ContainSingle().Which.Should().Be(Item1);
        (await _store.GetAsync(run.Id))!.State.Should().Be(AutopilotRunState.Stopped, "stop now is saved, not only returned");
    }

    [Fact]
    public async Task Stop_now_on_an_item_mid_wait_on_a_person_clears_the_stale_waiting_marker()
    {
        var run = RunWith(
            AutopilotLimits.Default,
            new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, null, null, WaitingSinceUtc: Start));
        await _store.SaveAsync(run);

        var stopped = await Engine().StopNowAsync(run.Id);

        stopped!.Queue.Single().Stage.Should().Be(AutopilotItemStage.Stopped);
        stopped.Queue.Single().WaitingSinceUtc.Should().BeNull("Stopped is terminal; nothing will ever read this wait again");
    }

    [Fact]
    public async Task Stop_now_also_ends_an_earlier_parked_items_live_process_since_the_builder_never_stops_one_just_because_it_waits()
    {
        var run = RunWith(
            AutopilotLimits.Default,
            new AutopilotRunItem(Item1, 0, AutopilotItemStage.Parked, 2m, 0, Start, null, "waiting on a person"),
            new AutopilotRunItem(Item2, 1, AutopilotItemStage.Building, 3m, 0, Start, null, null));
        await _store.SaveAsync(run);

        await Engine().StopNowAsync(run.Id);

        _builder.Stopped.Should().BeEquivalentTo([Item1, Item2]);
    }

    [Fact]
    public async Task Stop_now_ends_the_builders_live_process_even_when_the_item_has_moved_past_building()
    {
        // The building session stays alive between turns (an ordinary studio session, one per workspace),
        // so it can still be running while the item itself has moved on to Reviewing. Stop now must end it
        // there too, or the autopilot keeps the workspace after a person asked it to stop.
        var run = RunWith(
            AutopilotLimits.Default,
            new AutopilotRunItem(Item1, 0, AutopilotItemStage.Reviewing, 3m, 0, Start, "sha-1", null));
        await _store.SaveAsync(run);

        await Engine().StopNowAsync(run.Id);

        _builder.Stopped.Should().ContainSingle().Which.Should().Be(Item1);
    }

    [Fact]
    public async Task Stop_now_also_ends_an_earlier_items_live_process_even_once_a_limit_already_stopped_that_item()
    {
        // A per-item limit stops the ITEM, never its session: only the next item's own fresh start ends that
        // session in the ordinary course of things, so stop now must not assume a Stopped item has none.
        var run = RunWith(
            AutopilotLimits.Default,
            new AutopilotRunItem(Item1, 0, AutopilotItemStage.Stopped, 20m, 0, Start, null, null),
            new AutopilotRunItem(Item2, 1, AutopilotItemStage.Building, 3m, 0, Start, null, null));
        await _store.SaveAsync(run);

        await Engine().StopNowAsync(run.Id);

        _builder.Stopped.Should().BeEquivalentTo([Item1, Item2]);
    }

    [Fact]
    public async Task Stop_now_on_a_run_that_is_not_running_does_nothing()
    {
        var run = RunWith(AutopilotLimits.Default, AutopilotRunItem.Queue(Item1, 0)) with { State = AutopilotRunState.Stopped };
        await _store.SaveAsync(run);

        var result = await Engine().StopNowAsync(run.Id);

        result.Should().Be(run);
        _builder.Stopped.Should().BeEmpty();
    }

    [Fact]
    public async Task Stop_now_for_an_unknown_run_returns_null()
    {
        (await Engine().StopNowAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task A_stop_now_that_lands_while_a_tick_is_still_building_is_not_overwritten_by_that_ticks_own_save()
    {
        var run = RunOf(AutopilotLimits.Default, Item1);
        var started = await _store.SaveAsync(await Engine().TickAsync(run)); // Queued -> Prompting
        started = await _store.SaveAsync(await Engine().TickAsync(started)); // Prompting -> Building
        var gate = new TaskCompletionSource();
        _builder.Gate = () => gate.Task;
        var engine = Engine();

        var tick = engine.TickAndSaveAsync(started.Id); // blocks inside BuildAsync until the gate opens

        // The SAME engine instance as the tick: in production there is exactly one, a DI singleton, and the
        // per-run lock that makes this race safe is held on the instance, not shared across separate ones.
        var stopped = await engine.StopNowAsync(started.Id); // a person's own call, landing mid-tick

        stopped!.State.Should().Be(AutopilotRunState.Stopped);
        gate.SetResult();
        await tick;

        var final = await _store.GetAsync(started.Id);
        final!.State.Should().Be(
            AutopilotRunState.Stopped, "the person's stop must win, not the tick that was already running when it landed");
        final.Stops.Should().ContainSingle(s => s.Kind == AutopilotStopKind.StoppedByPerson);
        _builder.Stopped.Should().ContainSingle().Which.Should().Be(Item1, "the host must not start a fresh session for an item a person just stopped");
    }

    [Fact]
    public async Task A_stop_now_that_lands_while_the_builder_call_is_in_flight_skips_the_operators_advice_entirely()
    {
        // No commit and no person asked for would ordinarily go on to ask the operator for advice, a second
        // billed call in the same tick; once a person has stopped the run, TickAndSaveAsync's tail is going
        // to discard this tick's result anyway, so asking is pure waste the fix must not pay for.
        var run = RunOf(AutopilotLimits.Default, Item1);
        var started = await _store.SaveAsync(await Engine().TickAsync(run)); // Queued -> Prompting
        started = await _store.SaveAsync(await Engine().TickAsync(started)); // Prompting -> Building
        var gate = new TaskCompletionSource();
        _builder.Gate = () => gate.Task;
        _builder.Result = (_, _) => new AutopilotBuildResult(CommitMade: false, BranchHeadSha: null, CostUsd: 1m, NeedsPerson: false, Problem: "no commit");
        var engine = Engine();

        var tick = engine.TickAndSaveAsync(started.Id); // blocks inside BuildAsync (the gate), before any result exists

        var stopped = await engine.StopNowAsync(started.Id); // lands while the builder call is still gated
        stopped!.State.Should().Be(AutopilotRunState.Stopped);
        gate.SetResult(); // the builder call now returns "no commit, no person"
        await tick;

        _operator.AdviceCalls.Should().Be(0, "the run was already stopped by the time the builder's call returned, so asking for advice would only spend money on an answer nobody reads");
    }

    [Fact]
    public async Task A_stop_after_this_item_that_lands_while_a_tick_is_still_building_is_carried_into_that_ticks_save()
    {
        var run = RunOf(AutopilotLimits.Default, Item1);
        var started = await _store.SaveAsync(await Engine().TickAsync(run)); // Queued -> Prompting
        started = await _store.SaveAsync(await Engine().TickAsync(started)); // Prompting -> Building
        var gate = new TaskCompletionSource();
        _builder.Gate = () => gate.Task;
        var engine = Engine();

        var tick = engine.TickAndSaveAsync(started.Id); // blocks inside BuildAsync, started before the flag below is set

        // The SAME engine instance as the tick, for the same reason as the stop-now race test above.
        await engine.StopAfterCurrentItemAsync(started.Id); // a person's own call, landing mid-tick
        gate.SetResult();
        var ticked = await tick;

        ticked!.StopAfterCurrentItem.Should().BeTrue("the tick started before the flag was set, but must not save over it");
        (await _store.GetAsync(started.Id))!.StopAfterCurrentItem.Should().BeTrue();
    }

    [Fact]
    public async Task A_stop_after_this_item_set_in_the_tick_that_closes_the_item_stops_the_run_before_the_next_item_opens()
    {
        // The item that is about to close (Verifying -> Done) is the "current item" the flag refers to: if it
        // lands too late for THIS tick's own Settle to see it, the fix must still stop the run HERE, with the
        // second item left Queued, rather than letting the next item run in full before anyone checks again.
        var run = RunOf(AutopilotLimits.Default, Item1, Item2);
        var started = run;
        foreach (var _ in Enumerable.Range(0, 5))
        {
            started = await _store.SaveAsync(await Engine().TickAsync(started)); // Queued -> ... -> Verifying
        }
        started.CurrentItem!.Stage.Should().Be(AutopilotItemStage.Verifying);

        var gate = new TaskCompletionSource();
        _delivery.VerifyGate = () => gate.Task;
        var engine = Engine();

        var tick = engine.TickAndSaveAsync(started.Id); // blocks inside VerifyAsync, about to report Passed

        await engine.StopAfterCurrentItemAsync(started.Id); // lands while Verifying is still in flight
        gate.SetResult();
        var ticked = await tick;

        ticked!.State.Should().Be(AutopilotRunState.Stopped);
        ticked.Queue.Single(i => i.Item == Item1).Stage.Should().Be(AutopilotItemStage.Done);
        ticked.Queue.Single(i => i.Item == Item2).Stage.Should().Be(
            AutopilotItemStage.Queued, "stop after this item must not let the second item open at all");
        ticked.Stops.Should().ContainSingle(s => s.Kind == AutopilotStopKind.StoppedByPerson && s.Scope == AutopilotStopScope.Run);
    }

    /// <summary>
    /// #308 host note point 7: above cap 1, "stop after this item" lets EVERY running item finish, not only
    /// the first one to close. Settle's own fixed condition (every non-Queued item closed) is what this pins.
    /// </summary>
    [Fact]
    public async Task Stop_after_this_item_above_cap_one_lets_every_still_building_sibling_finish_first()
    {
        var run = RunWith(
            AutopilotLimits.Default,
            2,
            new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, null, null),
            new AutopilotRunItem(Item2, 1, AutopilotItemStage.Building, 0m, 0, Start, null, null))
            with { StopAfterCurrentItem = true };
        // Item1 commits on its first Building tick and runs through Reviewing/Delivering/Verifying normally;
        // Item2 makes no commit and the operator keeps retrying it, so it stays in Building throughout.
        _builder.Result = (wid, _) => wid == Item1
            ? new AutopilotBuildResult(true, "sha-1", 0m, false, null)
            : new AutopilotBuildResult(false, null, 0m, NeedsPerson: false, Problem: "still working");
        _operator.Advice = (_, _) => new AutopilotAdvice(RetryBuild: true);

        var afterFirstTick = await Engine().TickAsync(run); // Item1: Building -> Reviewing; Item2: retries
        afterFirstTick = await Engine().TickAsync(afterFirstTick); // Item1: Reviewing -> Delivering
        afterFirstTick = await Engine().TickAsync(afterFirstTick); // Item1: Delivering -> Verifying
        afterFirstTick = await Engine().TickAsync(afterFirstTick); // Item1: Verifying -> Done

        afterFirstTick.State.Should().Be(AutopilotRunState.Running, "item2 is still building and must be let finish");
        afterFirstTick.Queue.Single(i => i.Item == Item1).Stage.Should().Be(AutopilotItemStage.Done);
        afterFirstTick.Queue.Single(i => i.Item == Item2).Stage.Should().Be(AutopilotItemStage.Building);

        _builder.Result = (_, _) => new AutopilotBuildResult(true, "sha-2", 0m, false, null);
        var run2 = await Engine().TickAsync(afterFirstTick); // Building -> Reviewing
        run2.Queue.Single(i => i.Item == Item2).Stage.Should().Be(AutopilotItemStage.Reviewing);

        run2 = await Engine().TickAsync(run2); // Reviewing -> Delivering
        run2.Queue.Single(i => i.Item == Item2).Stage.Should().Be(AutopilotItemStage.Delivering);

        run2 = await Engine().TickAsync(run2); // Delivering -> Verifying
        run2.Queue.Single(i => i.Item == Item2).Stage.Should().Be(AutopilotItemStage.Verifying);

        run2 = await Engine().TickAsync(run2); // Verifying -> Done, and now the run stops
        run2.Queue.Single(i => i.Item == Item2).Stage.Should().Be(AutopilotItemStage.Done);
        run2.State.Should().Be(
            AutopilotRunState.Stopped, "once nothing already-started is still open, the flag finally stops the run");
    }

    [Fact]
    public async Task Stop_after_this_item_sets_the_flag_but_changes_nothing_else_on_the_same_call()
    {
        var run = RunOf(AutopilotLimits.Default, Item1);
        await _store.SaveAsync(run);

        var updated = await Engine().StopAfterCurrentItemAsync(run.Id);

        updated!.StopAfterCurrentItem.Should().BeTrue();
        updated.State.Should().Be(AutopilotRunState.Running);
        updated.CurrentItem!.Stage.Should().Be(AutopilotItemStage.Queued);
    }

    [Fact]
    public async Task Stop_after_this_item_lets_the_current_item_finish_then_stops_the_run_rather_than_the_next_item()
    {
        var run = RunOf(AutopilotLimits.Default, Item1, Item2);
        await _store.SaveAsync(run);
        var engine = Engine();
        run = (await engine.StopAfterCurrentItemAsync(run.Id))!;
        var builder = Engine(); // same seams, a fresh engine instance over the already-flagged run

        run = await builder.TickAsync(run); // Queued -> Prompting
        run = await builder.TickAsync(run); // Prompting -> Building
        run = await builder.TickAsync(run); // Building -> Reviewing (a commit: progress)
        run.CurrentItem!.Stage.Should().Be(AutopilotItemStage.Reviewing);
        run.State.Should().Be(AutopilotRunState.Running, "the current item has not closed yet");

        run = await builder.TickAsync(run); // Reviewing -> Delivering
        run = await builder.TickAsync(run); // Delivering -> Verifying
        run = await builder.TickAsync(run); // Verifying -> Done, and the flag stops the run right here

        run.Queue.Single(i => i.Item == Item1).Stage.Should().Be(AutopilotItemStage.Done);
        run.Queue.Single(i => i.Item == Item2).Stage.Should().Be(
            AutopilotItemStage.Queued, "stop after this item never opens the next item");
        run.State.Should().Be(AutopilotRunState.Stopped);
        run.Stops.Should().ContainSingle(s => s.Kind == AutopilotStopKind.StoppedByPerson && s.Scope == AutopilotStopScope.Run);
    }

    [Fact]
    public async Task Stop_after_this_item_lets_an_item_limit_stop_close_the_item_and_then_stops_the_run_too()
    {
        var limits = AutopilotLimits.Default with { MoneyPerItemUsd = 5m };
        var run = RunWith(
            limits,
            new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, null, null),
            AutopilotRunItem.Queue(Item2, 1));
        await _store.SaveAsync(run);
        _builder.Result = (_, _) => new AutopilotBuildResult(true, "sha-x", CostUsd: 10m, NeedsPerson: false, Problem: null);
        var engine = Engine();
        run = (await engine.StopAfterCurrentItemAsync(run.Id))!;

        run = await engine.TickAsync(run);

        run.Queue.Single(i => i.Item == Item1).Stage.Should().Be(AutopilotItemStage.Stopped);
        run.Queue.Single(i => i.Item == Item2).Stage.Should().Be(AutopilotItemStage.Queued);
        run.State.Should().Be(AutopilotRunState.Stopped);
        run.Stops.Should().Contain(s => s.Kind == AutopilotStopKind.MoneyPerItem)
            .And.Contain(s => s.Kind == AutopilotStopKind.StoppedByPerson && s.Scope == AutopilotStopScope.Run);
    }

    // ---- verifying: pending, the operator's advice, and the fix loop (#301) --------------------------------

    [Fact]
    public async Task Verification_still_pending_stays_in_verifying_with_no_cost_and_no_progress()
    {
        var run = RunWith(
            AutopilotLimits.Default,
            new AutopilotRunItem(Item1, 0, AutopilotItemStage.Verifying, 2m, 0, Start, "sha-1", null));
        _delivery.Verify = (_, _) => new AutopilotVerifyResult(Passed: false, NeedsPerson: false, Problem: null, Pending: true);

        var ticked = await Engine().TickAsync(run);

        ticked.CurrentItem!.Stage.Should().Be(AutopilotItemStage.Verifying, "CI or the scenario has not settled, so nothing has actually happened yet");
        ticked.CurrentItem!.SpentUsd.Should().Be(2m);
        ticked.LastProgressUtc.Should().Be(Start, "pending is neither progress nor a failure");
        ticked.Stops.Should().BeEmpty();
    }

    [Fact]
    public async Task A_verify_failure_asks_the_operator_and_a_retry_stays_in_building()
    {
        var run = RunWith(
            AutopilotLimits.Default,
            new AutopilotRunItem(Item1, 0, AutopilotItemStage.Verifying, 0m, 0, Start, "sha-1", null));
        _delivery.Verify = (_, _) => new AutopilotVerifyResult(Passed: false, NeedsPerson: false, Problem: "CI is red: build (failure).");
        string? askedAbout = null;
        _operator.Advice = (_, failure) => { askedAbout = failure; return new AutopilotAdvice(RetryBuild: true, CostUsd: 1m); };

        var ticked = await Engine().TickAsync(run);

        _operator.AdviceCalls.Should().Be(1);
        askedAbout.Should().Be("CI is red: build (failure).");
        ticked.CurrentItem!.Stage.Should().Be(AutopilotItemStage.Building);
        ticked.CurrentItem!.SpentUsd.Should().Be(1m, "the advice call's own cost is folded in");
    }

    [Fact]
    public async Task A_verify_failure_the_operator_declines_parks_at_once_with_its_reason()
    {
        // Verification has already finished (unlike the builder's own NeedsPerson result), so there is
        // nothing pending to re-poll: this parks at once, the same as every Park(...) site besides
        // RunBuildAsync's own NeedsPerson branch (see that method's own doc comment, ADR 0031 decision 12).
        var run = RunWith(
            AutopilotLimits.Default,
            new AutopilotRunItem(Item1, 0, AutopilotItemStage.Verifying, 0m, 0, Start, "sha-1", null));
        _delivery.Verify = (_, _) => new AutopilotVerifyResult(Passed: false, NeedsPerson: false, Problem: "The scenario failed.");
        _operator.Advice = (_, failure) => new AutopilotAdvice(RetryBuild: false, "Not worth another attempt: " + failure);

        var ticked = await Engine().TickAsync(run);

        var item = ticked.Queue.Single();
        item.Stage.Should().Be(AutopilotItemStage.Parked);
        item.ParkedReason.Should().Be("Not worth another attempt: The scenario failed.");
    }

    [Fact]
    public async Task The_fix_loop_runs_several_times_within_its_limit_and_stops_at_it_rather_than_looping_forever()
    {
        // Verifying always fails and the operator always says retry: Building -> Reviewing -> Delivering ->
        // Verifying loops, bounded only by the item's own money ceiling, exactly as ADR decision 5 describes
        // ("within the item's limits"). Delivering is reached more than once, which is the shape "a repeated
        // delivery finds the existing pull request rather than opening a second one" needs at the engine level;
        // AgentAutopilotDeliveryTests proves that idempotency for the real seam itself.
        var limits = AutopilotLimits.Default with { MoneyPerItemUsd = 1m };
        var run = RunWith(limits, new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, null, null));
        var buildCount = 0;
        _builder.Result = (_, _) =>
        {
            buildCount++;
            return new AutopilotBuildResult(true, $"sha-{buildCount}", CostUsd: 0.3m, NeedsPerson: false, Problem: null);
        };
        var deliverCount = 0;
        _delivery.Deliver = (_, _) => { deliverCount++; return new AutopilotDeliveryResult(true, "https://example.invalid/pr/1", null); };
        _delivery.Verify = (_, _) => new AutopilotVerifyResult(Passed: false, NeedsPerson: false, Problem: "still red");
        _operator.Advice = (_, _) => new AutopilotAdvice(RetryBuild: true);

        var engine = Engine();
        for (var i = 0; i < 20 && run.CurrentItem is not null; i++)
        {
            run = await engine.TickAsync(run);
        }

        run.Queue.Single().Stage.Should().Be(AutopilotItemStage.Stopped, "the loop must stop at the money-per-item ceiling, never run forever");
        run.Stops.Should().ContainSingle(s => s.Kind == AutopilotStopKind.MoneyPerItem);
        deliverCount.Should().BeGreaterThan(1, "the fix loop must have reached Delivering more than once before the limit fired");
    }

    // ---- returning the workspace to its base branch when a run ends (#310) ---------------------------------

    [Fact]
    public async Task A_run_that_completes_returns_the_workspace_to_its_base_branch()
    {
        var run = RunOf(AutopilotLimits.Default, Item1) with { BaseBranch = "main", BaseWorkspaceId = "w1" };
        var workspace = new FakeWorkspaceReturn();
        var engine = Engine(workspace);

        for (var i = 0; i < 20 && run.CurrentItem is not null; i++)
        {
            run = await engine.TickAsync(run);
        }

        run.State.Should().Be(AutopilotRunState.Completed);
        run.WorkspaceReturnNote.Should().BeNull();
        workspace.Calls.Should().ContainSingle().Which.Should().Be(("w1", "main"));
    }

    [Fact]
    public async Task A_run_with_no_recorded_base_branch_never_calls_the_workspace_return_seam()
    {
        var run = RunOf(AutopilotLimits.Default, Item1); // no BaseBranch: dispatched before #310, or never resolved
        var workspace = new FakeWorkspaceReturn();
        var engine = Engine(workspace);

        for (var i = 0; i < 20 && run.CurrentItem is not null; i++)
        {
            run = await engine.TickAsync(run);
        }

        run.State.Should().Be(AutopilotRunState.Completed);
        workspace.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_run_stopped_by_a_run_limit_returns_the_workspace_and_a_dirty_answer_records_the_note()
    {
        var limits = AutopilotLimits.Default with { MoneyPerItemUsd = 1000m, MoneyPerRunUsd = 100m };
        var run = RunWith(
            limits,
            new AutopilotRunItem(Item1, 0, AutopilotItemStage.Done, 60m, 0, Start, "sha-done", null),
            new AutopilotRunItem(Item2, 1, AutopilotItemStage.Building, 0m, 0, Start, null, null))
            with { BaseBranch = "main", BaseWorkspaceId = "w1" };
        _builder.Result = (_, _) => new AutopilotBuildResult(true, "sha-2", CostUsd: 50m, NeedsPerson: false, Problem: null);
        var workspace = new FakeWorkspaceReturn
        {
            Result = (_, branch) => new AutopilotWorkspaceReturnResult(Returned: false, Note: $"left on {branch}, uncommitted changes"),
        };

        var result = await Engine(workspace).TickAsync(run);

        result.State.Should().Be(AutopilotRunState.Stopped);
        result.WorkspaceReturnNote.Should().Be("left on main, uncommitted changes");
        workspace.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task An_item_scoped_limit_that_leaves_the_run_running_never_calls_the_workspace_return_seam()
    {
        var limits = AutopilotLimits.Default with { MoneyPerItemUsd = 5m };
        var run = RunWith(
            limits,
            new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, null, null),
            AutopilotRunItem.Queue(Item2, 1))
            with { BaseBranch = "main", BaseWorkspaceId = "w1" };
        _builder.Result = (_, _) => new AutopilotBuildResult(true, "sha-x", CostUsd: 10m, NeedsPerson: false, Problem: null);
        var workspace = new FakeWorkspaceReturn();

        var result = await Engine(workspace).TickAsync(run);

        result.State.Should().Be(AutopilotRunState.Running, "the run moves on to the next item, so it has not ended");
        workspace.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Stop_now_returns_the_workspace_once_the_stopped_row_is_saved()
    {
        var run = RunWith(
            AutopilotLimits.Default,
            new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 3m, 0, Start, null, null))
            with { BaseBranch = "main", BaseWorkspaceId = "w1" };
        await _store.SaveAsync(run);
        var workspace = new FakeWorkspaceReturn();

        var stopped = await Engine(workspace).StopNowAsync(run.Id);

        stopped!.State.Should().Be(AutopilotRunState.Stopped);
        workspace.Calls.Should().ContainSingle().Which.Should().Be(("w1", "main"));
        (await _store.GetAsync(run.Id))!.WorkspaceReturnNote.Should().BeNull();
    }

    [Fact]
    public async Task Stop_now_with_a_dirty_workspace_saves_the_note_on_the_stopped_run()
    {
        var run = RunWith(
            AutopilotLimits.Default,
            new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 3m, 0, Start, null, null))
            with { BaseBranch = "main", BaseWorkspaceId = "w1" };
        await _store.SaveAsync(run);
        var workspace = new FakeWorkspaceReturn
        {
            Result = (_, branch) => new AutopilotWorkspaceReturnResult(Returned: false, Note: $"left on {branch}, uncommitted changes"),
        };

        var stopped = await Engine(workspace).StopNowAsync(run.Id);

        stopped!.WorkspaceReturnNote.Should().Be("left on main, uncommitted changes");
        (await _store.GetAsync(run.Id))!.WorkspaceReturnNote.Should().Be("left on main, uncommitted changes");
    }

    [Fact]
    public async Task Stop_after_this_item_landing_too_late_for_the_ticks_own_settle_still_returns_the_workspace()
    {
        var run = RunOf(AutopilotLimits.Default, Item1, Item2) with { BaseBranch = "main", BaseWorkspaceId = "w1" };
        var started = run;
        var workspace = new FakeWorkspaceReturn();
        var engine = Engine(workspace);
        foreach (var _ in Enumerable.Range(0, 5))
        {
            started = await _store.SaveAsync(await engine.TickAsync(started)); // Queued -> ... -> Verifying
        }
        started.CurrentItem!.Stage.Should().Be(AutopilotItemStage.Verifying);

        var gate = new TaskCompletionSource();
        _delivery.VerifyGate = () => gate.Task;

        var tick = engine.TickAndSaveAsync(started.Id); // blocks inside VerifyAsync, about to report Passed
        await engine.StopAfterCurrentItemAsync(started.Id); // lands while Verifying is still in flight
        gate.SetResult();
        var ticked = await tick;

        ticked!.State.Should().Be(AutopilotRunState.Stopped);
        workspace.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task Resuming_a_run_that_cannot_resume_returns_the_workspace()
    {
        var inFlight = RunWith(
            AutopilotLimits.Default, new AutopilotRunItem(Item1, 0, AutopilotItemStage.Reviewing, 0m, 0, Start, "sha-1", null))
            with { BaseBranch = "main", BaseWorkspaceId = "w1" };
        await _store.SaveAsync(inFlight);
        var workspace = new FakeWorkspaceReturn();

        await Engine(workspace).ResumeAllAsync();

        workspace.Calls.Should().ContainSingle().Which.Should().Be(("w1", "main"));
        (await _store.GetAsync(inFlight.Id))!.Stops.Should().ContainSingle(s => s.Kind == AutopilotStopKind.CannotResume);
    }

    /// <summary>
    /// Commit review #310 (third pass): a Parked item's own building session stays alive (it is still waiting
    /// on a person to answer its pending approval), so the folder must not be checked out from under it before
    /// that session is stopped, or a late answer would land the agent's attempt_commit on the base branch
    /// instead of the item's own attempt branch. Every item in the queue is stopped, not only the parked one,
    /// the same reasoning StopNowAsync's own loop already uses.
    /// </summary>
    [Fact]
    public async Task A_parked_items_session_is_stopped_before_the_workspace_is_returned()
    {
        var limits = AutopilotLimits.Default with { NoProgressWindow = TimeSpan.FromHours(3) };
        var run = RunWith(
            limits,
            new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, null, null))
            with { BaseBranch = "main", BaseWorkspaceId = "w1" };
        _builder.Result = (_, _) => new AutopilotBuildResult(false, null, 0m, NeedsPerson: true, Problem: "needs a decision");
        var workspace = new FakeWorkspaceReturn();
        var stoppedBeforeReturn = false;
        workspace.Result = (_, _) =>
        {
            stoppedBeforeReturn = _builder.Stopped.Contains(Item1);
            return new AutopilotWorkspaceReturnResult(Returned: true, Note: null);
        };
        var engine = Engine(workspace);

        var waiting = await engine.TickAsync(run);
        waiting.Queue.Single().Stage.Should().Be(AutopilotItemStage.Building, "the run waits before parking, and the workspace return seam must not be asked yet");

        _clock.Now = Start.Add(AutopilotDispatchSettings.Default.PersonWaitTimeout).AddMinutes(1);
        var result = await engine.TickAsync(waiting);

        result.Queue.Single().Stage.Should().Be(AutopilotItemStage.Parked);
        result.State.Should().Be(AutopilotRunState.Stopped, "nothing reached Done, so the queue-exhausted stop ends the run");
        stoppedBeforeReturn.Should().BeTrue("the session must already be stopped by the time the return seam is asked");
        _builder.Stopped.Should().Contain(Item1);
    }

    /// <summary>
    /// Commit review #310 (third pass): the session-stop loop runs INSIDE the same try the seam call does, so
    /// a session that could not be stopped must still leave the run ended with a note, never propagate out of
    /// the tick and leave the stored row Running, which would have the next tick repeat the same seam call.
    /// </summary>
    [Fact]
    public async Task A_session_that_cannot_be_stopped_still_ends_the_run_with_a_note_and_never_calls_the_return_seam()
    {
        var run = RunOf(AutopilotLimits.Default, Item1) with { BaseBranch = "main", BaseWorkspaceId = "w1" };
        _builder.ThrowOnStop = _ => true;
        var workspace = new FakeWorkspaceReturn();
        var engine = Engine(workspace);

        for (var i = 0; i < 20 && run.CurrentItem is not null; i++)
        {
            run = await engine.TickAsync(run);
        }

        run.State.Should().Be(AutopilotRunState.Completed);
        run.WorkspaceReturnNote.Should().Contain("could not stop");
        workspace.Calls.Should().BeEmpty("stopping every session must come before the return seam is ever asked");
    }

    /// <summary>
    /// Commit review #310: an exception from the seam itself (a checkout that throws, a workspace the bridge
    /// could not reach and threw instead of answering) must still leave a note on the ended run, or the record
    /// is silent exactly when a person most needs to know where the folder was left.
    /// </summary>
    [Fact]
    public async Task A_workspace_return_seam_that_throws_still_records_a_note_on_the_ended_run()
    {
        var run = RunOf(AutopilotLimits.Default, Item1) with { BaseBranch = "main", BaseWorkspaceId = "w1" };
        var workspace = new FakeWorkspaceReturn
        {
            Result = (_, _) => throw new InvalidOperationException("the bridge connection reset"),
        };
        var engine = Engine(workspace);

        for (var i = 0; i < 20 && run.CurrentItem is not null; i++)
        {
            run = await engine.TickAsync(run);
        }

        run.State.Should().Be(AutopilotRunState.Completed);
        run.WorkspaceReturnNote.Should().Be("not returned to main: the bridge connection reset");
    }

    [Fact]
    public async Task A_resumable_run_is_still_running_and_never_calls_the_workspace_return_seam()
    {
        var resumable = RunWith(
            AutopilotLimits.Default, new AutopilotRunItem(Item2, 0, AutopilotItemStage.Prompting, 0m, 0, Start, null, null))
            with { BaseBranch = "main", BaseWorkspaceId = "w1" };
        await _store.SaveAsync(resumable);
        var workspace = new FakeWorkspaceReturn();

        await Engine(workspace).ResumeAllAsync();

        workspace.Calls.Should().BeEmpty();
    }

    // ---- #308: the autopilot runs marked items in parallel up to the cap ---------------------------------------

    [Fact]
    public async Task Two_items_both_advance_in_the_same_tick_once_the_runs_effective_cap_allows_it()
    {
        // Both Queued at the start of the tick: a cap-1 run (every other test in this file) would only ever
        // move the first one to Prompting, leaving the second exactly as it was. A cap-2 run advances both.
        var run = RunOf(AutopilotLimits.Default, 2, Item1, Item2);

        var result = await Engine().TickAsync(run);

        result.Queue.Should().HaveCount(2);
        result.Queue.Should().OnlyContain(i => i.Stage == AutopilotItemStage.Prompting);
    }

    [Fact]
    public async Task A_limit_firing_on_one_parallel_item_does_not_stop_the_other()
    {
        var limits = AutopilotLimits.Default with { TimePerItem = TimeSpan.FromMinutes(30) };
        var run = RunWith(
            limits,
            2,
            new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, null, null),
            // Started later than Item1: at the clock below, Item2's own elapsed time is well inside the
            // limit, proving the stop is Item1's own and never bleeds onto a sibling that is not over it.
            new AutopilotRunItem(Item2, 1, AutopilotItemStage.Building, 0m, 0, Start.AddMinutes(25), null, null));
        _clock.Now = Start.AddMinutes(31); // past Item1's own time-per-item ceiling, not Item2's
        _builder.Result = (item, _) => item == Item1
            ? new AutopilotBuildResult(true, "sha-1", 0m, false, null) // would otherwise succeed; the limit still wins
            : new AutopilotBuildResult(true, "sha-2", 0m, false, null);

        var result = await Engine().TickAsync(run);

        var item1 = result.Queue.Single(i => i.Item == Item1);
        var item2 = result.Queue.Single(i => i.Item == Item2);
        item1.Stage.Should().Be(AutopilotItemStage.Stopped);
        item2.Stage.Should().Be(AutopilotItemStage.Reviewing, "item2's own commit landed; item1's limit is its own");
        result.State.Should().Be(AutopilotRunState.Running, "the run itself keeps going for item2");
        result.Stops.Should().ContainSingle(s => s.Kind == AutopilotStopKind.TimePerItem && s.Item == Item1);
    }

    [Fact]
    public async Task A_build_result_waiting_for_capacity_leaves_the_item_exactly_as_it_was_and_spends_nothing()
    {
        var run = RunWith(
            AutopilotLimits.Default, new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 2m, 0, Start, null, null));
        _builder.Result = (_, _) => new AutopilotBuildResult(
            CommitMade: false, BranchHeadSha: null, CostUsd: 0m, NeedsPerson: false,
            Problem: "The concurrency cap is reached.", WaitingForCapacity: true);
        _clock.Now = Start.AddHours(3); // well past TimePerItem, if the wait counted as elapsed work

        var result = await Engine().TickAsync(run);

        var item = result.Queue.Single();
        item.Stage.Should().Be(AutopilotItemStage.Building, "a capacity refusal is not a failure of the item");
        item.SpentUsd.Should().Be(2m, "nothing ran, so nothing more was spent");
        item.WaitingForCapacityReason.Should().Be("The concurrency cap is reached.");
        item.StartedUtc.Should().Be(_clock.Now, "the wait spends no limit at all, including TimePerItem, so its clock is rebased");
        result.State.Should().Be(AutopilotRunState.Running);
        result.Stops.Should().BeEmpty();
    }

    /// <summary>
    /// #315: the builder could not put the folder back on the run's base branch (the previous item left
    /// uncommitted changes), so nothing started. The item parks at once with those words, the operator is never
    /// asked for advice on an attempt that never ran, and the run moves on to the next item.
    /// </summary>
    [Fact]
    public async Task A_refused_build_parks_the_item_at_once_without_advice_and_the_run_moves_on()
    {
        var run = RunWith(
            AutopilotLimits.Default,
            new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 2m, 0, Start, null, null),
            AutopilotRunItem.Queue(Item2, 1));
        _builder.Result = (_, _) => new AutopilotBuildResult(
            CommitMade: false, BranchHeadSha: null, CostUsd: 0m, NeedsPerson: false,
            Problem: "Item #1 was not started: left on fravashi/item-0-abc, uncommitted changes.", Refused: true);

        var result = await Engine().TickAsync(run);

        var item1 = result.Queue.Single(i => i.Item == Item1);
        item1.Stage.Should().Be(AutopilotItemStage.Parked);
        item1.ParkedReason.Should().Contain("uncommitted changes");
        item1.SpentUsd.Should().Be(2m, "nothing ran, so nothing more was spent");
        _operator.AdviceCalls.Should().Be(0, "there is no attempt to advise on");
        result.State.Should().Be(AutopilotRunState.Running);

        var next = await Engine().TickAsync(result);
        next.CurrentItem!.Item.Should().Be(Item2);
    }

    /// <summary>
    /// #308 host note point 8 / ADR 0031 decision 15: a run-scoped limit can end the run while a parallel
    /// sibling's own stage never closed; EndItemAsync must still reach it, not only the item whose own fold
    /// tripped the limit.
    /// </summary>
    [Fact]
    public async Task A_run_scoped_limit_ending_the_run_calls_end_item_for_every_sibling_still_open()
    {
        var limits = AutopilotLimits.Default with { MoneyPerItemUsd = 1000m, MoneyPerRunUsd = 50m };
        var run = RunWith(
            limits,
            2,
            new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, null, null),
            new AutopilotRunItem(Item2, 1, AutopilotItemStage.Building, 0m, 0, Start, null, null));
        _builder.Result = (wid, _) => wid == Item1
            ? new AutopilotBuildResult(true, "sha-1", CostUsd: 60m, NeedsPerson: false, Problem: null) // trips the run limit
            : new AutopilotBuildResult(false, null, 0m, NeedsPerson: false, Problem: "still working");
        _operator.Advice = (_, _) => new AutopilotAdvice(RetryBuild: true);

        var result = await Engine().TickAsync(run);

        result.State.Should().Be(AutopilotRunState.Stopped);
        result.Stops.Should().ContainSingle(s => s.Kind == AutopilotStopKind.MoneyPerRun);
        _builder.Ended.Should().Contain(Item1).And.Contain(Item2, "item2 was still open when the run-scoped limit ended the run");
    }

    /// <summary>
    /// ADR 0031 decision 15 (#308, commit review's finding): a parallel run (effective cap above 1) never
    /// switches the person's own folder at all, since every item got its own worktree. Ending such a run must
    /// never ask the workspace-return seam to check the base branch out over whatever that folder is on now.
    /// </summary>
    [Fact]
    public async Task A_parallel_run_ending_never_calls_the_workspace_return_seam()
    {
        var limits = AutopilotLimits.Default with { MoneyPerItemUsd = 1000m, MoneyPerRunUsd = 50m };
        var run = RunWith(
            limits,
            2,
            new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 0m, 0, Start, null, null),
            new AutopilotRunItem(Item2, 1, AutopilotItemStage.Building, 0m, 0, Start, null, null))
            with { BaseBranch = "main", BaseWorkspaceId = "w1" };
        _builder.Result = (wid, _) => wid == Item1
            ? new AutopilotBuildResult(true, "sha-1", CostUsd: 60m, NeedsPerson: false, Problem: null)
            : new AutopilotBuildResult(false, null, 0m, NeedsPerson: false, Problem: "still working");
        _operator.Advice = (_, _) => new AutopilotAdvice(RetryBuild: true);
        var workspace = new FakeWorkspaceReturn();

        var result = await Engine(workspace).TickAsync(run);

        result.State.Should().Be(AutopilotRunState.Stopped);
        workspace.Calls.Should().BeEmpty("a parallel run never switched this folder, so there is nothing to return");
    }
}
