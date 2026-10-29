using AwesomeAssertions;
using Fravashi.Domain.Autopilot;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Xunit;

namespace Fravashi.Domain.Tests.Autopilot;

/// <summary>
/// ADR 0031 decision 6: "Whichever fires first stops the item (or, for a run limit, the run) and the stop names
/// the limit and the figure. A stop is a record, never a log line only." And decision 10's restart answer, the
/// same shape as the outbox's own: an in-flight step found after a restart is never assumed to have finished.
/// </summary>
public sealed class AutopilotDisciplineTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);
    private static readonly WorkItemId Item1 = new("item-1");
    private static readonly WorkItemId Item2 = new("item-2");

    private static AutopilotRun Run(AutopilotLimits limits, IReadOnlyList<AutopilotRunItem> queue, DateTimeOffset? lastProgress = null) =>
        new(
            Guid.NewGuid(),
            ProjectId.Default,
            AutopilotDispatchSettings.Default with { Limits = limits },
            queue,
            AutopilotRunState.Running,
            Start,
            lastProgress ?? Start,
            []);

    private static AutopilotRunItem Item(WorkItemId item, int order, AutopilotItemStage stage, decimal spent = 0m, int failed = 0, DateTimeOffset? started = null) =>
        new(item, order, stage, spent, failed, started ?? Start, null, null);

    // ---- item-scoped limits -----------------------------------------------------------------------------------

    [Fact]
    public void Money_per_item_fires_when_the_item_has_spent_more_than_its_ceiling()
    {
        var limits = AutopilotLimits.Default with { MoneyPerItemUsd = 10m };
        var item = Item(Item1, 0, AutopilotItemStage.Building, spent: 10.01m);
        var run = Run(limits, [item]);

        var stop = AutopilotDiscipline.CheckItemLimits(run, item, Start);

        stop.Should().NotBeNull();
        stop!.Kind.Should().Be(AutopilotStopKind.MoneyPerItem);
        stop.Scope.Should().Be(AutopilotStopScope.Item);
        stop.LimitName.Should().Be("money per item");
        stop.Figure.Should().Contain("$10.01").And.Contain("$10.00");
        stop.Item.Should().Be(Item1);
    }

    [Fact]
    public void Money_per_item_does_not_fire_at_exactly_the_ceiling()
    {
        var limits = AutopilotLimits.Default with { MoneyPerItemUsd = 10m };
        var item = Item(Item1, 0, AutopilotItemStage.Building, spent: 10.00m);

        AutopilotDiscipline.CheckItemLimits(Run(limits, [item]), item, Start).Should().BeNull();
    }

    [Fact]
    public void Failed_commit_reviews_fires_once_the_item_reaches_its_ceiling()
    {
        var limits = AutopilotLimits.Default with { FailedCommitReviewsPerItem = 2 };
        var item = Item(Item1, 0, AutopilotItemStage.Building, failed: 2);
        var run = Run(limits, [item]);

        var stop = AutopilotDiscipline.CheckItemLimits(run, item, Start);

        stop.Should().NotBeNull();
        stop!.Kind.Should().Be(AutopilotStopKind.FailedCommitReviews);
        stop.Scope.Should().Be(AutopilotStopScope.Item);
        stop.LimitName.Should().Be("failed commit reviews per item");
        stop.Figure.Should().Contain("2").And.Contain("limit 2");
    }

    [Fact]
    public void Time_per_item_fires_once_the_item_has_run_longer_than_its_ceiling()
    {
        var limits = AutopilotLimits.Default with { TimePerItem = TimeSpan.FromMinutes(30) };
        var item = Item(Item1, 0, AutopilotItemStage.Building, started: Start);
        var run = Run(limits, [item]);
        var now = Start.AddMinutes(31);

        var stop = AutopilotDiscipline.CheckItemLimits(run, item, now);

        stop.Should().NotBeNull();
        stop!.Kind.Should().Be(AutopilotStopKind.TimePerItem);
        stop.Scope.Should().Be(AutopilotStopScope.Item);
        stop.LimitName.Should().Be("time per item");
    }

    [Fact]
    public void An_item_under_every_ceiling_fires_no_stop()
    {
        var item = Item(Item1, 0, AutopilotItemStage.Building, spent: 1m, failed: 0, started: Start);
        AutopilotDiscipline.CheckItemLimits(Run(AutopilotLimits.Default, [item]), item, Start.AddMinutes(1)).Should().BeNull();
    }

    // ---- run-scoped limits ------------------------------------------------------------------------------------

    [Fact]
    public void Money_per_run_fires_once_the_whole_queues_spending_passes_its_ceiling()
    {
        var limits = AutopilotLimits.Default with { MoneyPerRunUsd = 20m };
        var queue = new[]
        {
            Item(Item1, 0, AutopilotItemStage.Done, spent: 15m),
            Item(Item2, 1, AutopilotItemStage.Building, spent: 5.01m),
        };
        var run = Run(limits, queue);

        var stop = AutopilotDiscipline.CheckRunLimits(run, Start);

        stop.Should().NotBeNull();
        stop!.Kind.Should().Be(AutopilotStopKind.MoneyPerRun);
        stop.Scope.Should().Be(AutopilotStopScope.Run);
        stop.Item.Should().BeNull();
        stop.Figure.Should().Contain("$20.01").And.Contain("$20.00");
    }

    [Fact]
    public void No_progress_fires_once_the_window_passes_with_neither_a_shrunk_queue_nor_a_moved_branch()
    {
        var limits = AutopilotLimits.Default with { NoProgressWindow = TimeSpan.FromMinutes(30) };
        var item = Item(Item1, 0, AutopilotItemStage.Building);
        var run = Run(limits, [item], lastProgress: Start);

        AutopilotDiscipline.CheckRunLimits(run, Start.AddMinutes(29)).Should().BeNull("29 minutes has not reached the 30 minute window");

        var stop = AutopilotDiscipline.CheckRunLimits(run, Start.AddMinutes(31));
        stop.Should().NotBeNull();
        stop!.Kind.Should().Be(AutopilotStopKind.NoProgress);
        stop.Scope.Should().Be(AutopilotStopScope.Run);
    }

    [Fact]
    public void Progress_recorded_either_way_resets_the_no_progress_window()
    {
        var limits = AutopilotLimits.Default with { NoProgressWindow = TimeSpan.FromMinutes(30) };
        var item = Item(Item1, 0, AutopilotItemStage.Building);

        // A commit landing on the item's branch, or the queue shrinking, both move LastProgressUtc forward; either
        // way the window restarts from there.
        var afterACommitLanded = Run(limits, [item], lastProgress: Start.AddMinutes(20));

        AutopilotDiscipline.CheckRunLimits(afterACommitLanded, Start.AddMinutes(45)).Should().BeNull();
    }

    // ---- what a restart means ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(AutopilotItemStage.Building)]
    [InlineData(AutopilotItemStage.Reviewing)]
    [InlineData(AutopilotItemStage.Delivering)]
    [InlineData(AutopilotItemStage.Verifying)]
    public void A_restart_mid_an_in_flight_stage_stops_the_run_rather_than_guess_whether_it_finished(AutopilotItemStage stage)
    {
        var item = Item(Item1, 0, stage);
        var run = Run(AutopilotLimits.Default, [item]);

        var resumed = AutopilotDiscipline.OnRestart(run, Start.AddMinutes(1));

        resumed.State.Should().Be(AutopilotRunState.Stopped);
        resumed.Stops.Should().ContainSingle(s => s.Kind == AutopilotStopKind.CannotResume);
        resumed.Stops[0].Reason.Should().Contain("unknown");
    }

    [Fact]
    public void A_restart_mid_wait_on_a_person_clears_the_stale_waiting_marker_on_the_stopped_run()
    {
        var item = Item(Item1, 0, AutopilotItemStage.Building) with { WaitingSinceUtc = Start };
        var run = Run(AutopilotLimits.Default, [item]);

        var resumed = AutopilotDiscipline.OnRestart(run, Start.AddMinutes(1));

        resumed.State.Should().Be(AutopilotRunState.Stopped);
        resumed.Queue.Single().WaitingSinceUtc.Should().BeNull("the run is terminal now; nothing will ever read this wait again");
    }

    [Theory]
    [InlineData(AutopilotItemStage.Queued)]
    [InlineData(AutopilotItemStage.Prompting)]
    public void A_restart_mid_a_stage_with_nothing_in_flight_resumes_with_its_clocks_rebased_to_now(AutopilotItemStage stage)
    {
        var item = Item(Item1, 0, stage);
        var run = Run(AutopilotLimits.Default, [item]);
        var afterDowntime = Start.AddHours(6);

        var resumed = AutopilotDiscipline.OnRestart(run, afterDowntime);

        // The studio being down for six hours is real time, but it is not evidence the run was stuck, so the
        // outage itself must never count against the no-progress window or the resumed item's time ceiling.
        resumed.State.Should().Be(AutopilotRunState.Running);
        resumed.LastProgressUtc.Should().Be(afterDowntime);
        resumed.Queue.Single().StartedUtc.Should().Be(afterDowntime);
        resumed.Stops.Should().BeEmpty();
    }

    [Fact]
    public void A_restart_after_downtime_does_not_make_the_no_progress_limit_fire_on_the_very_next_check()
    {
        var limits = AutopilotLimits.Default with { NoProgressWindow = TimeSpan.FromMinutes(30) };
        var run = Run(limits, [Item(Item1, 0, AutopilotItemStage.Prompting)]); // LastProgressUtc = Start
        var afterDowntime = Start.AddHours(6); // far past the window, if the outage counted against it

        var resumed = AutopilotDiscipline.OnRestart(run, afterDowntime);

        AutopilotDiscipline.CheckRunLimits(resumed, afterDowntime).Should().BeNull();
    }

    [Fact]
    public void A_restart_of_a_run_already_stopped_or_completed_is_left_exactly_as_it_was()
    {
        var item = Item(Item1, 0, AutopilotItemStage.Building);
        var run = Run(AutopilotLimits.Default, [item]) with { State = AutopilotRunState.Completed };

        AutopilotDiscipline.OnRestart(run, Start.AddMinutes(1)).Should().Be(run);
    }

    [Fact]
    public void A_restart_with_nothing_open_because_the_only_item_is_parked_is_untouched_with_no_clock_to_rebase()
    {
        // Parked is not an open stage (CurrentItem never returns one), so this run has no current item either
        // way: nothing is in flight for it, but there is also nothing to rebase a clock against.
        var item = Item(Item1, 0, AutopilotItemStage.Parked);
        var run = Run(AutopilotLimits.Default, [item]);
        run.CurrentItem.Should().BeNull();

        AutopilotDiscipline.OnRestart(run, Start.AddMinutes(1)).Should().Be(run);
    }

    /// <summary>
    /// #308: a cap-1 run's items all share ONE workspace, returned to its base branch only once the run itself
    /// ends, so a Queued sibling must never be let in once the in-flight item fails to resume: the whole run
    /// stops, and the sibling is left exactly as it was, never rebased into a run that keeps going.
    /// </summary>
    [Fact]
    public void A_cap_one_restart_mid_an_in_flight_item_stops_the_whole_run_and_leaves_a_queued_sibling_untouched()
    {
        var building = Item(Item1, 0, AutopilotItemStage.Building);
        var queued = AutopilotRunItem.Queue(Item2, 1);
        var run = Run(AutopilotLimits.Default, [building, queued]);
        run.Settings.EffectiveConcurrencyCap.Should().Be(1, "the shipped default, unchanged by this test");

        var resumed = AutopilotDiscipline.OnRestart(run, Start.AddMinutes(1));

        resumed.State.Should().Be(AutopilotRunState.Stopped);
        resumed.Stops.Should().ContainSingle(s => s.Kind == AutopilotStopKind.CannotResume && s.Scope == AutopilotStopScope.Run);
        resumed.Queue.Single(i => i.Item == Item2).Should().Be(queued, "a cap-1 restart never touches a sibling of the in-flight item");
    }

    /// <summary>
    /// Above cap 1, each active item has its own worktree (#308 decision 2), so a Queued sibling is safe to
    /// rebase and the run keeps going for it even while the in-flight item stops on its own.
    /// </summary>
    [Fact]
    public void A_cap_two_restart_mid_an_in_flight_item_stops_only_that_item_and_rebases_a_queued_sibling()
    {
        var building = Item(Item1, 0, AutopilotItemStage.Building);
        var prompting = Item(Item2, 1, AutopilotItemStage.Prompting);
        var run = Run(AutopilotLimits.Default, [building, prompting]);
        run = run with { Settings = run.Settings with { EffectiveConcurrencyCap = 2 } };
        var afterDowntime = Start.AddHours(6);

        var resumed = AutopilotDiscipline.OnRestart(run, afterDowntime);

        resumed.State.Should().Be(AutopilotRunState.Running, "item2 is still open, so the run keeps going for it");
        resumed.Queue.Single(i => i.Item == Item1).Stage.Should().Be(AutopilotItemStage.Stopped);
        resumed.Stops.Should().ContainSingle(s => s.Kind == AutopilotStopKind.CannotResume && s.Scope == AutopilotStopScope.Item && s.Item == Item1);
        resumed.Queue.Single(i => i.Item == Item2).StartedUtc.Should().Be(afterDowntime, "item2 was never in flight, so its own clock is rebased, not stopped");
    }

    // ---- what a run with nothing left open becomes -------------------------------------------------------------

    [Fact]
    public void A_run_with_an_open_item_is_not_concluded()
    {
        var item = Item(Item1, 0, AutopilotItemStage.Building);
        var run = Run(AutopilotLimits.Default, [item]);

        AutopilotDiscipline.Conclude(run, Start).Should().Be(run);
    }

    [Fact]
    public void A_run_whose_every_item_reached_done_is_completed()
    {
        var run = Run(AutopilotLimits.Default, [Item(Item1, 0, AutopilotItemStage.Done)]);

        AutopilotDiscipline.Conclude(run, Start).State.Should().Be(AutopilotRunState.Completed);
    }

    [Fact]
    public void A_run_whose_only_item_was_stopped_by_a_limit_ends_rather_than_idling_forever()
    {
        // Without this, a run whose last open item stops (an item limit fired) or parks (waiting on a person)
        // would sit in Running with CurrentItem null forever: no further tick would ever check a run limit
        // again, and a restart would resume it only to find the same nothing.
        var run = Run(AutopilotLimits.Default, [Item(Item1, 0, AutopilotItemStage.Stopped)]);

        var concluded = AutopilotDiscipline.Conclude(run, Start);

        concluded.State.Should().Be(AutopilotRunState.Stopped);
        concluded.Stops.Should().ContainSingle();
        concluded.Stops[0].Kind.Should().Be(AutopilotStopKind.QueueExhausted);
        concluded.Stops[0].Scope.Should().Be(AutopilotStopScope.Run);
        concluded.Stops[0].Figure.Should().Contain("0 done").And.Contain("1 stopped").And.Contain("0 parked");
    }

    [Fact]
    public void A_run_whose_only_item_is_parked_also_ends_rather_than_idling_forever()
    {
        var run = Run(AutopilotLimits.Default, [Item(Item1, 0, AutopilotItemStage.Parked)]);

        var concluded = AutopilotDiscipline.Conclude(run, Start);

        concluded.State.Should().Be(AutopilotRunState.Stopped);
        concluded.Stops.Should().ContainSingle(s => s.Kind == AutopilotStopKind.QueueExhausted);
    }

    [Fact]
    public void A_run_with_a_mix_of_done_and_stopped_items_and_nothing_open_ends_naming_both_counts()
    {
        var run = Run(AutopilotLimits.Default,
        [
            Item(Item1, 0, AutopilotItemStage.Done),
            Item(Item2, 1, AutopilotItemStage.Stopped),
        ]);

        var concluded = AutopilotDiscipline.Conclude(run, Start);

        concluded.State.Should().Be(AutopilotRunState.Stopped);
        concluded.Stops[0].Figure.Should().Contain("1 done").And.Contain("1 stopped").And.Contain("0 parked");
    }

    // ---- ApplyPersonWait (ADR 0031 decision 12, #305) ----------------------------------------------------------

    [Fact]
    public void The_first_time_an_item_asks_it_stays_in_its_prior_stage_and_starts_the_wait_clock()
    {
        var before = Item(Item1, 0, AutopilotItemStage.Building, spent: 3m);
        var parked = before with { Stage = AutopilotItemStage.Parked, ParkedReason = "needs a decision" };
        var run = Run(AutopilotLimits.Default, [before]);

        var (item, stop) = AutopilotDiscipline.ApplyPersonWait(run, before, parked, Start);

        item.Stage.Should().Be(AutopilotItemStage.Building, "nothing is parked yet; the run waits instead");
        item.ParkedReason.Should().BeNull();
        item.WaitingSinceUtc.Should().Be(Start);
        item.SpentUsd.Should().Be(3m, "every other field change the tick already made must survive the reversion");
        stop.Should().BeNull();
    }

    [Fact]
    public void Still_within_the_timeout_it_keeps_waiting_with_the_same_waiting_since()
    {
        var waitStarted = Start;
        var before = Item(Item1, 0, AutopilotItemStage.Building) with { WaitingSinceUtc = waitStarted };
        var parked = before with { Stage = AutopilotItemStage.Parked, ParkedReason = "needs a decision" };
        var run = Run(AutopilotLimits.Default, [before]); // default PersonWaitTimeout is one hour
        var now = Start.AddMinutes(59);

        var (item, stop) = AutopilotDiscipline.ApplyPersonWait(run, before, parked, now);

        item.Stage.Should().Be(AutopilotItemStage.Building);
        item.WaitingSinceUtc.Should().Be(waitStarted, "the wait clock never restarts while still within the timeout");
        stop.Should().BeNull();
    }

    [Fact]
    public void Past_the_timeout_it_actually_parks_with_a_stop_naming_the_wait_and_its_duration()
    {
        var waitStarted = Start;
        var before = Item(Item1, 0, AutopilotItemStage.Building) with { WaitingSinceUtc = waitStarted };
        var parked = before with { Stage = AutopilotItemStage.Parked, ParkedReason = "needs a decision" };
        var limits = AutopilotLimits.Default;
        var run = new AutopilotRun(
            Guid.NewGuid(), ProjectId.Default,
            AutopilotDispatchSettings.Default with { Limits = limits, PersonWaitTimeout = TimeSpan.FromMinutes(60) },
            [before], AutopilotRunState.Running, Start, Start, []);
        var now = Start.AddMinutes(61);

        var (item, stop) = AutopilotDiscipline.ApplyPersonWait(run, before, parked, now);

        item.Stage.Should().Be(AutopilotItemStage.Parked);
        item.ParkedReason.Should().Be("needs a decision");
        item.WaitingSinceUtc.Should().BeNull("the wait is over once the item has actually parked");
        stop.Should().NotBeNull();
        stop!.Kind.Should().Be(AutopilotStopKind.PersonWaitTimedOut);
        stop.Scope.Should().Be(AutopilotStopScope.Item);
        stop.LimitName.Should().Be("person wait");
        stop.Figure.Should().Contain("61 min").And.Contain("limit 60 min");
        stop.Reason.Should().Be("needs a decision");
        stop.Item.Should().Be(Item1);
    }
}
