using System.Globalization;
using System.Linq;

namespace Fravashi.Domain.Autopilot;

/// <summary>
/// The rules a run's limits and its restart follow, as pure functions, mirroring <c>OutboxDiscipline</c>'s shape
/// because the same question (what does a restart mean, what stops first) has the same right answer: never guess
/// an unknown into a safe-looking continuation.
/// </summary>
public static class AutopilotDiscipline
{
    /// <summary>Whichever item-scoped limit fires first for the given item at <paramref name="now"/>, or null.</summary>
    public static AutopilotStop? CheckItemLimits(AutopilotRun run, AutopilotRunItem item, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(item);

        var limits = run.Settings.Limits;

        if (item.SpentUsd > limits.MoneyPerItemUsd)
        {
            return new AutopilotStop(
                AutopilotStopKind.MoneyPerItem,
                AutopilotStopScope.Item,
                "money per item",
                $"{Money(item.SpentUsd)} spent, limit {Money(limits.MoneyPerItemUsd)}",
                now,
                item.Item);
        }

        if (item.FailedReviews >= limits.FailedCommitReviewsPerItem)
        {
            return new AutopilotStop(
                AutopilotStopKind.FailedCommitReviews,
                AutopilotStopScope.Item,
                "failed commit reviews per item",
                $"{item.FailedReviews.ToString(CultureInfo.InvariantCulture)} failed review(s), limit "
                    + limits.FailedCommitReviewsPerItem.ToString(CultureInfo.InvariantCulture),
                now,
                item.Item);
        }

        if (item.StartedUtc is { } started && now - started > limits.TimePerItem)
        {
            return new AutopilotStop(
                AutopilotStopKind.TimePerItem,
                AutopilotStopScope.Item,
                "time per item",
                $"{Duration(now - started)} elapsed, limit {Duration(limits.TimePerItem)}",
                now,
                item.Item);
        }

        return null;
    }

    /// <summary>Whichever run-scoped limit fires first at <paramref name="now"/>, or null.</summary>
    public static AutopilotStop? CheckRunLimits(AutopilotRun run, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(run);

        var limits = run.Settings.Limits;
        var spent = run.SpentUsd;
        if (spent > limits.MoneyPerRunUsd)
        {
            return new AutopilotStop(
                AutopilotStopKind.MoneyPerRun,
                AutopilotStopScope.Run,
                "money per run",
                $"{Money(spent)} spent, limit {Money(limits.MoneyPerRunUsd)}",
                now,
                null);
        }

        if (now - run.LastProgressUtc > limits.NoProgressWindow)
        {
            return new AutopilotStop(
                AutopilotStopKind.NoProgress,
                AutopilotStopScope.Run,
                "no progress",
                $"{Duration(now - run.LastProgressUtc)} with no progress, limit {Duration(limits.NoProgressWindow)}",
                now,
                null);
        }

        return null;
    }

    /// <summary>
    /// What a restart does to a run (the archived autopilot's "a run that did not survive a restart", answered the
    /// way the outbox answers the same question for a write: an entry caught mid in-flight step is never assumed
    /// to have finished). A completed or already-stopped run is a settled fact and stays exactly as it was.
    /// <see cref="AutopilotDispatchSettings.EffectiveConcurrencyCap"/>, resolved once at dispatch and never
    /// re-read from live Settings (#308), decides which rule applies. AT 1, this is the ORIGINAL, unchanged
    /// rule, judged against the single active item only (<see cref="AutopilotRun.ActiveItems"/>'s own FIRST
    /// entry) - any item that <see cref="AutopilotItemStages.MayHaveBeenInFlight"/> stops the WHOLE run, leaving
    /// every other item (a Queued sibling included) exactly as found, because a cap-1 run's items all share ONE
    /// workspace (#308 decision 2) and that workspace is returned to its base branch only once the RUN itself
    /// ends (#310): letting a Queued sibling start there before that return ran would build it on top of the
    /// stopped item's own attempt branch. ABOVE 1, each of the run's first <c>EffectiveConcurrencyCap</c> active
    /// items (each with its own worktree, so none of this risk applies) is judged independently: Queued or
    /// Prompting has its progress and elapsed-time clocks REBASED to <paramref name="now"/> (the studio being
    /// down is real time that passed, but it is not evidence THAT item was stuck), while an in-flight one stops
    /// on its own, by itself, so one item's uncertain restart never costs a sibling that was never at risk.
    /// <see cref="Conclude"/> is what then decides the run's own <see cref="AutopilotRun.State"/> for that case.
    /// The run's own progress clock is rebased whenever ANY item is still open afterward, even one this restart
    /// never touched (outside the cap window, or Parked): the downtime must never count against a run that still
    /// has work left, not only against the items this call happened to judge. A run with nothing open (every
    /// remaining item Parked) has no clock to rebase against and is returned untouched, whatever the cap.
    /// </summary>
    public static AutopilotRun OnRestart(AutopilotRun run, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (run.State != AutopilotRunState.Running)
        {
            return run;
        }

        // ActiveItems never returns a Parked one (AutopilotItemStages.IsOpen excludes it), so a run whose only
        // remaining items are Parked lands here empty: nothing is in flight for any of them either, but there
        // is also nothing to rebase a clock against, so it is returned untouched and Conclude ends it, or the
        // person it is waiting on un-parks it, on the engine's next tick.
        var active = run.ActiveItems;
        if (active.Count == 0)
        {
            return run;
        }

        var cap = Math.Max(1, run.Settings.EffectiveConcurrencyCap);
        if (cap <= 1)
        {
            return OnRestartSequential(run, active[0], now);
        }

        var updated = run;
        foreach (var item in active.Take(cap))
        {
            if (!AutopilotItemStages.MayHaveBeenInFlight(item.Stage))
            {
                // The only stages an active item can carry here are Queued (made no call at all) and Prompting
                // (a real, billed call whose repetition cannot create a duplicate commit, review verdict, pull
                // request or verification run, only a second prompt that overwrites the first; that is the
                // line MayHaveBeenInFlight draws, not whether a call happened). Repeating it is still not free:
                // the interrupted attempt's cost was billed and is never added to the item's SpentUsd, which
                // this rule accepts rather than hides.
                var resumed = item.StartedUtc is null ? item : item with { StartedUtc = now };
                updated = updated.With(resumed);
                continue;
            }

            var stop = new AutopilotStop(
                AutopilotStopKind.CannotResume,
                AutopilotStopScope.Item,
                "resume",
                $"the item was mid-{item.Stage} when the studio stopped",
                now,
                item.Item,
                "An external call or an agent turn may have been in flight, and whether it finished is unknown, "
                    + "so the item stops rather than risk doing it twice or leaving it half done.");

            // The item itself is left exactly as it was found (its stage names what it was doing, which is the
            // record this stop names too), except for WaitingSinceUtc: it is terminal now, so a mid-wait
            // Building item must not carry a wait marker nothing will ever read again as though it were still,
            // genuinely, waiting.
            var stoppedItem = item with { Stage = AutopilotItemStage.Stopped, WaitingSinceUtc = null };
            updated = updated.With(stoppedItem).WithStop(stop);
        }

        // Rebased whenever ANYTHING is still open afterward, not only an item this loop touched (#308, commit
        // review's finding): a Queued sibling sitting OUTSIDE the cap window is just as much "still open" as
        // one inside it, and the outage must never count against the run for either.
        if (updated.ActiveItems.Count > 0)
        {
            updated = updated with { LastProgressUtc = now };
        }

        return Conclude(updated, now);
    }

    /// <summary>
    /// The ORIGINAL restart rule (before #308), for a cap-1 run: judged against the single active item only.
    /// Any in-flight item stops the WHOLE run, leaving every other item untouched; a Queued or Prompting one
    /// has its own clocks rebased and the run keeps going.
    /// </summary>
    private static AutopilotRun OnRestartSequential(AutopilotRun run, AutopilotRunItem current, DateTimeOffset now)
    {
        if (!AutopilotItemStages.MayHaveBeenInFlight(current.Stage))
        {
            // The only stages CurrentItem can return here are Queued (made no call at all) and Prompting (a
            // real, billed call whose repetition cannot create a duplicate commit, review verdict, pull
            // request or verification run, only a second prompt that overwrites the first; that is the line
            // MayHaveBeenInFlight draws, not whether a call happened). Repeating it is still not free: the
            // interrupted attempt's cost was billed and is never added to the item's SpentUsd, which this rule
            // accepts rather than hides.
            var resumed = current.StartedUtc is null ? current : current with { StartedUtc = now };
            return run.With(resumed) with { LastProgressUtc = now };
        }

        var stop = new AutopilotStop(
            AutopilotStopKind.CannotResume,
            AutopilotStopScope.Run,
            "resume",
            $"the item was mid-{current.Stage} when the studio stopped",
            now,
            current.Item,
            "An external call or an agent turn may have been in flight, and whether it finished is unknown, so "
                + "the run stops rather than risk doing it twice or leaving it half done.");

        // The item itself is left exactly as it was found (its stage names what it was doing, which is the
        // record this stop names too), except for WaitingSinceUtc: the run around it is terminal now, so a
        // mid-wait Building item must not carry a wait marker nothing will ever read again as though it
        // were still, genuinely, waiting. Every OTHER item in the queue (a Queued sibling, under a cap-1 run
        // that somehow carries one) is left exactly as found: the run is stopping, not concluding, so
        // Conclude's own done/stopped/parked tally never runs here.
        var stopped = current.WaitingSinceUtc is null ? run : run.With(current with { WaitingSinceUtc = null });
        return stopped with { State = AutopilotRunState.Stopped, Stops = [.. stopped.Stops, stop] };
    }

    /// <summary>
    /// What a run becomes once nothing in its queue is open: every item reached <see cref="AutopilotItemStage.Done"/>
    /// (<see cref="AutopilotRunState.Completed"/>), or the queue still has an item that is not (a
    /// <see cref="AutopilotStopKind.QueueExhausted"/> stop, because a run with nothing open and nothing left
    /// marked Done would otherwise stay <see cref="AutopilotRunState.Running"/> forever: no further tick would
    /// ever check a run limit again, and a restart would resume it only to find the same nothing). A run with an
    /// open item is returned unchanged; there is nothing to conclude yet.
    /// </summary>
    public static AutopilotRun Conclude(AutopilotRun run, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (run.CurrentItem is not null)
        {
            return run;
        }

        if (run.AllDone)
        {
            return run with { State = AutopilotRunState.Completed };
        }

        var done = run.Queue.Count(i => i.Stage == AutopilotItemStage.Done);
        var stopped = run.Queue.Count(i => i.Stage == AutopilotItemStage.Stopped);
        var parked = run.Queue.Count(i => i.Stage == AutopilotItemStage.Parked);
        var stop = new AutopilotStop(
            AutopilotStopKind.QueueExhausted,
            AutopilotStopScope.Run,
            "queue exhausted",
            $"{done.ToString(CultureInfo.InvariantCulture)} done, {stopped.ToString(CultureInfo.InvariantCulture)} "
                + $"stopped, {parked.ToString(CultureInfo.InvariantCulture)} parked of "
                + run.Queue.Count.ToString(CultureInfo.InvariantCulture),
            now,
            null,
            "Nothing left in the queue is open, and not every item reached Done, so the run stops rather than "
                + "sit idle with no way to make a limit fire again.");
        return run.WithStop(stop) with { State = AutopilotRunState.Stopped };
    }

    /// <summary>
    /// What happens when the builder's own turn asks for a person (ADR 0031 decision 12, #305): called ONLY
    /// from <c>AutopilotEngine.RunBuildAsync</c>'s own <c>NeedsPerson</c> branch, turning "parked at once"
    /// into "the run waits, up to the dispatch's own timeout, before parking." No other stage's own
    /// <c>Park(...)</c> call routes through this (<c>RunBuildAsync</c>'s own doc comment names why: the other
    /// four have nothing left open in a live session for a person to resolve mid-run, so they still park at
    /// once, exactly as #297 built them). <paramref name="before"/> is the item as it stood at the START
    /// of this tick (its <see cref="AutopilotRunItem.Stage"/> and any <see cref="AutopilotRunItem.WaitingSinceUtc"/>
    /// already running); <paramref name="parked"/> is what the stage's own action just produced, carrying
    /// every OTHER field change that tick made (money spent, a branch head, a failed-review count) along with
    /// <see cref="AutopilotItemStage.Parked"/> and the reason text the stage itself chose.
    /// <para>
    /// The FIRST time an item asks (<paramref name="before"/> carries no <see cref="AutopilotRunItem.WaitingSinceUtc"/>)
    /// it stays in <paramref name="before"/>'s own stage rather than parking, with the wait clock started at
    /// <paramref name="now"/> and no stop. Still within <see cref="AutopilotRun.Settings"/>'s
    /// <see cref="AutopilotDispatchSettings.PersonWaitTimeout"/>, it stays exactly there, with the SAME
    /// <see cref="AutopilotRunItem.WaitingSinceUtc"/> carried forward: the only way the engine ever learns a
    /// person answered is by asking the same stage's seam again on a later tick and reading back something
    /// other than another park, so a waiting item is deliberately NOT a no-op for the engine, only for what it
    /// shows a person. Once the timeout is exceeded, the item actually parks (<paramref name="parked"/>'s own
    /// stage and reason, with <see cref="AutopilotRunItem.WaitingSinceUtc"/> cleared) and a stop is produced
    /// naming how long it waited against the limit, the record ADR decision 6's "a stop is a record, never a
    /// log line only" already asks for every other limit.
    /// </para>
    /// </summary>
    public static (AutopilotRunItem Item, AutopilotStop? Stop) ApplyPersonWait(
        AutopilotRun run, AutopilotRunItem before, AutopilotRunItem parked, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(parked);

        var timeout = run.Settings.PersonWaitTimeout;
        var reason = parked.ParkedReason ?? "Waiting on a person.";

        if (before.WaitingSinceUtc is not { } since)
        {
            return (parked with { Stage = before.Stage, ParkedReason = null, WaitingSinceUtc = now }, null);
        }

        var elapsed = now - since;
        if (elapsed < timeout)
        {
            return (parked with { Stage = before.Stage, ParkedReason = null, WaitingSinceUtc = since }, null);
        }

        var stop = new AutopilotStop(
            AutopilotStopKind.PersonWaitTimedOut,
            AutopilotStopScope.Item,
            "person wait",
            $"{Duration(elapsed)} waited, limit {Duration(timeout)}",
            now,
            parked.Item,
            reason);
        return (parked with { WaitingSinceUtc = null }, stop);
    }

    private static string Money(decimal value) => "$" + value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Duration(TimeSpan span)
    {
        var minutes = (int)Math.Ceiling(span.TotalMinutes);
        return minutes.ToString(CultureInfo.InvariantCulture) + " min";
    }
}
