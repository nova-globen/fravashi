using System.Collections.Concurrent;
using System.Linq;
using Fravashi.Application.Sessions;
using Fravashi.Domain.Autopilot;

namespace Fravashi.Application.Autopilot;

/// <summary>
/// The deterministic state machine ADR 0031 decision 10 calls for: it owns control flow, limits, stops and every
/// record, and calls the four seams for judgement steps, but never lets one decide what happens next. This is the
/// answer to the archived autopilot's own list of what it got wrong (item #58's note): limits are checked here,
/// in code, rather than by substring matching a failure message; a stop is always <see cref="AutopilotStop"/>,
/// never only a log line; and <see cref="ResumeAllAsync"/> is what makes a run survive a restart.
/// </summary>
public sealed class AutopilotEngine
{
    private readonly IAutopilotOperator _operator;
    private readonly IAutopilotBuilder _builder;
    private readonly IAutopilotCommitReviews _reviews;
    private readonly IAutopilotDelivery _delivery;
    private readonly IAutopilotRunStore _store;
    private readonly IAutopilotWorkspaceReturn? _workspaceReturn;
    private readonly TimeProvider _clock;

    // One lock per run, held around every read-modify-write this engine does against a run's STORED row
    // (never around a seam call): TickAndSaveAsync's own re-read-then-save tail, StopNowAsync and
    // StopAfterCurrentItemAsync all touch the same row without the store itself offering optimistic
    // concurrency, so without this a stop landing between a tick's re-read and its save (or the reverse)
    // is a lost update, not merely a stale read a later re-read could catch. Grown, never pruned: a run
    // ticks and stops rarely enough next to how many it could ever be that the entries this leaves behind
    // for the life of the process are not worth a cleanup pass.
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _runLocks = new();

    private SemaphoreSlim LockFor(Guid runId) => _runLocks.GetOrAdd(runId, static _ => new SemaphoreSlim(1, 1));

    public AutopilotEngine(
        IAutopilotOperator operatorSeam,
        IAutopilotBuilder builder,
        IAutopilotCommitReviews reviews,
        IAutopilotDelivery delivery,
        IAutopilotRunStore store,
        TimeProvider? clock = null,
        IAutopilotWorkspaceReturn? workspaceReturn = null)
    {
        _operator = operatorSeam ?? throw new ArgumentNullException(nameof(operatorSeam));
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _reviews = reviews ?? throw new ArgumentNullException(nameof(reviews));
        _delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? TimeProvider.System;
        _workspaceReturn = workspaceReturn;
    }

    /// <summary>
    /// One deterministic step: ONE call into a seam per active item, for every item the run's own
    /// <see cref="AutopilotDispatchSettings.EffectiveConcurrencyCap"/> (resolved once at dispatch, #308) lets
    /// it advance this tick, run concurrently (<see cref="Task.WhenAll{TResult}(IEnumerable{Task{TResult}})"/>)
    /// and then folded back one at a time, in board order, through the SAME per-item
    /// <see cref="Settle"/>/<see cref="ApplySeamFailure"/> this engine already had before #308: a run dispatched
    /// with the default cap of 1 (today's sequential behaviour) always has exactly one active item, so nothing
    /// here changes what it does. Every limit is still checked and overrides whatever the step decided if one
    /// fires. Does not persist; call <see cref="IAutopilotRunStore.SaveAsync"/> with the result, or use
    /// <see cref="TickAndSaveAsync"/>.
    /// </summary>
    public async Task<AutopilotRun> TickAsync(AutopilotRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (run.State != AutopilotRunState.Running)
        {
            return run;
        }

        // The cap bounds how many items this TICK advances at all, including a Queued item's own synchronous
        // move to Prompting: without this, every Queued item in the whole run would be promoted (and billed
        // the operator's prompt cost) on the very first tick, regardless of the cap, with only the builder's
        // own concurrency gate left to refuse the Building attempts that follow. Capping here instead is what
        // "runs marked items in parallel UP TO THE CAP" means literally, and what keeps a cap-1 run identical
        // to the single-item engine this was before #308. Read off the RUN, never live Settings: a Settings
        // change made while this run is open must never flip it between sharing one workspace and giving each
        // item its own worktree (commit review's own finding).
        var cap = Math.Max(1, run.Settings.EffectiveConcurrencyCap);
        // ADR 0031 decision 13, "stop after this item": once asked, no item may BACKFILL a slot a closure
        // frees (#308 host note point 7), but the flag must never retroactively block an item that was
        // already "the current one" before it was ever asked, Queued or not: a cap-1 run asked to stop
        // before its very first item has even started still lets that item run to completion (the single-
        // item engine's own original behaviour, kept exactly). The line this draws: once ANYTHING in the
        // run's queue has left Queued at least once (a real signal of "a wave already started", read off the
        // whole queue, not only the active window, so a since-closed item still counts), no further Queued
        // item is let into the window; before that, the window fills normally, as a fresh dispatch's first
        // wave always has. A known, accepted conservative edge: a Queued item that was already sitting in a
        // cap-2-or-more window ALONGSIDE an already-started sibling when the flag landed is held back too,
        // rather than trying to tell "was already in the window" apart from "would be backfilling" without
        // tracking the window's own history; the safer failure mode is starting one item fewer than asked,
        // never one more.
        var anyItemEverStarted = run.Queue.Any(i => i.Stage != AutopilotItemStage.Queued || i.StartedUtc is not null);
        var candidates = run.StopAfterCurrentItem && anyItemEverStarted
            ? run.ActiveItems.Where(i => i.Stage != AutopilotItemStage.Queued).ToList()
            : run.ActiveItems;
        var active = candidates.Take(cap).ToList();
        if (active.Count == 0)
        {
            var concludeNow = _clock.GetUtcNow();
            if (run.StopAfterCurrentItem && run.ActiveItems.Count > 0)
            {
                // Everything left open is Queued, held back by the flag above: nothing is actually running
                // to finish, so the run stops here rather than through Conclude, which reads an open item as
                // "not done yet" and does not know this one is being deliberately held back forever.
                var stop = new AutopilotStop(
                    AutopilotStopKind.StoppedByPerson,
                    AutopilotStopScope.Run,
                    "stop after this item",
                    "a person asked the run to stop once the current item closed",
                    concludeNow,
                    null,
                    "The current item reached its end, and a person had asked the run to stop once it did.");
                return await MaybeReturnWorkspaceAsync(
                    run, run.WithStop(stop) with { State = AutopilotRunState.Stopped }, ct).ConfigureAwait(false);
            }

            return await MaybeReturnWorkspaceAsync(run, AutopilotDiscipline.Conclude(run, concludeNow), ct).ConfigureAwait(false);
        }

        var outcomes = await Task.WhenAll(active.Select(item => TickOneItemAsync(run, item, ct))).ConfigureAwait(false);

        // Read once, after every item's own seam call has returned, not before any of them started: a build
        // that takes 40 minutes must stamp progress and be checked against limits at the moment it actually
        // finished, not at the moment it started, the same reasoning the single-item engine always used, now
        // applied to the one "now" every item folded on this tick shares.
        var now = _clock.GetUtcNow();
        var settled = run;
        for (var i = 0; i < active.Count; i++)
        {
            var outcome = outcomes[i];
            if (outcome.Failure is { } failure)
            {
                // Raised by a stage that makes TWO seam calls (Building: the builder, then the operator's
                // advice): the first call's cost is already folded into outcome.Item, so the failure is
                // recorded against that, never against the item as it stood before either call ran.
                settled = ApplySeamFailure(settled, outcome.Item, failure.Seam, failure.Error, now);
                continue;
            }

            // A capacity refusal spends no limit at all (#308 host note point 4), including the item's own
            // TimePerItem: StartedUtc is rebased to now, exactly as OnRestart already rebases a Queued or
            // Prompting item's clock across a studio outage, so the wait itself never counts as elapsed work.
            var item = outcome.Item.WaitingForCapacityReason is { Length: > 0 }
                ? outcome.Item with { StartedUtc = now }
                : outcome.Item;
            var updatedRun = settled.With(item);
            if (outcome.Progressed)
            {
                updatedRun = updatedRun with { LastProgressUtc = now };
            }

            // ADR 0031 decision 12 (#305): only RunBuildAsync's own NeedsPerson branch ever produces one of
            // these, so this is never a second place that decides whether to wait; it only records the stop
            // that branch already produced once the wait it started has actually timed out.
            if (outcome.WaitStop is not null)
            {
                updatedRun = updatedRun.WithStop(outcome.WaitStop);
            }

            settled = Settle(updatedRun, item, now);
        }

        var endNote = await EndItemsAsync(run, settled, ct).ConfigureAwait(false);
        return await MaybeReturnWorkspaceAsync(run, WithEndItemNote(settled, endNote), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Calls <see cref="IAutopilotBuilder.EndItemAsync"/> for every item that needs it: one that just left an
    /// open stage (Done, Stopped or Parked alike, #308 host note point 8), comparing <paramref name="before"/>
    /// against <paramref name="after"/> by <see cref="AutopilotRunItem.Order"/> rather than trusting a caller
    /// to name which ones closed (a parallel tick can close more than one at once, and a restart or "stop now"
    /// can close several that were never all <see cref="AutopilotRun.CurrentItem"/>); AND, when the RUN itself
    /// just ended (<paramref name="before"/> was Running, <paramref name="after"/> is not), every item still
    /// OPEN in <paramref name="after"/> too, since a run-scoped limit or a seam failure can end the run while
    /// a parallel sibling's own stage never closed, and nothing else ever reaches it (ADR 0031 decision 15:
    /// a worktree is pruned when its item's run ends, not only when its own item does). Never throws: a
    /// failure is collected into the returned note instead (plan @126 point 8, "never silently swallowed"),
    /// which the caller folds onto <see cref="AutopilotRun.WorkspaceReturnNote"/>.
    /// </summary>
    private async Task<string?> EndItemsAsync(AutopilotRun before, AutopilotRun after, CancellationToken ct)
    {
        var context = AutopilotBuilderContext.From(after);
        var runEnded = before.State == AutopilotRunState.Running && after.State != AutopilotRunState.Running;
        var failures = new List<string>();
        foreach (var item in after.Queue)
        {
            var wasOpen = before.Queue.FirstOrDefault(i => i.Order == item.Order) is { } prior
                && AutopilotItemStages.IsOpen(prior.Stage);
            var justClosed = wasOpen && !AutopilotItemStages.IsOpen(item.Stage);
            var stillOpenAtRunEnd = runEnded && AutopilotItemStages.IsOpen(item.Stage);
            if (!justClosed && !stillOpenAtRunEnd)
            {
                continue;
            }

            try
            {
                // EndItemAsync RETURNS a refusal's own words (a dirty worktree, say) rather than throwing
                // one (plan @126 point 8, "never silently swallowed"): both land in the same failures list,
                // the thrown kind named here, the returned kind already self-naming the item.
                if (await _builder.EndItemAsync(context, item.Item, ct).ConfigureAwait(false) is { Length: > 0 } refusal)
                {
                    failures.Add(refusal);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add($"item {item.Item.Value}: {ex.Message}");
            }
        }

        return failures.Count == 0 ? null : string.Join("; ", failures);
    }

    /// <summary>Folds an <see cref="EndItemsAsync"/> failure note onto the run, appending rather than overwriting.</summary>
    private static AutopilotRun WithEndItemNote(AutopilotRun run, string? note) =>
        note is { Length: > 0 }
            ? run with { WorkspaceReturnNote = AppendNote(run.WorkspaceReturnNote, note) }
            : run;

    private static string AppendNote(string? existing, string addition) =>
        existing is { Length: > 0 } ? existing + "; " + addition : addition;

    /// <summary>
    /// One item's own step of a tick, never throwing: a seam exception is caught and carried as
    /// <see cref="ItemTickOutcome.Failure"/> instead, so <see cref="TickAsync"/> can fold several items' own
    /// outcomes one at a time without one item's exception aborting the others, which already ran concurrently
    /// with it and cannot be un-run.
    /// </summary>
    private async Task<ItemTickOutcome> TickOneItemAsync(AutopilotRun run, AutopilotRunItem item, CancellationToken ct)
    {
        if (item.Stage == AutopilotItemStage.Queued)
        {
            var started = item with { Stage = AutopilotItemStage.Prompting, StartedUtc = _clock.GetUtcNow() };
            return new ItemTickOutcome(started, false, null, null);
        }

        try
        {
            var (updatedItem, progressed, waitStop) = await RunStageAsync(run, item, ct).ConfigureAwait(false);
            return new ItemTickOutcome(updatedItem, progressed, waitStop, null);
        }
        catch (SeamFailureException ex)
        {
            return new ItemTickOutcome(ex.ItemSoFar, false, null, (ex.Seam, ex.InnerException!));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A seam that raises an exception instead of returning an answer is exactly the "malformed or
            // missing answer" ADR 0031 decision 10 calls a stop, never a guess: without this, the item stays
            // in its stage and the very next tick calls the same seam again, which is the duplicate (a second
            // commit, a second review, a second push) the restart rule exists to prevent.
            return new ItemTickOutcome(item, false, null, (item.Stage.ToString(), ex));
        }
    }

    private sealed record ItemTickOutcome(
        AutopilotRunItem Item, bool Progressed, AutopilotStop? WaitStop, (string Seam, Exception Error)? Failure);

    /// <summary>
    /// ADR 0031 decision 3's correction (#310): a run that just ended (<paramref name="before"/> was
    /// <see cref="AutopilotRunState.Running"/>, <paramref name="after"/> is not) returns its workspace to the
    /// branch its first attempt was made from, through <see cref="_workspaceReturn"/>, when one was recorded
    /// on the run and that seam is wired. A run with no recorded <see cref="AutopilotRun.BaseBranch"/> (every
    /// run dispatched before this slice) is left exactly as <see cref="Settle"/> or <see cref="ApplySeamFailure"/>
    /// already decided. Nothing here forces a checkout over work: the seam itself refuses when the working tree
    /// is not clean and says so through <see cref="AutopilotRun.WorkspaceReturnNote"/>. The run has already
    /// ended, so a failure here must never undo that or stop it a second time, but it must still say so (item
    /// #310 point 2): an exception from the seam itself records the same kind of note the seam's own refusal
    /// would, naming the exception rather than leaving the record silent about where the folder was left.
    /// <para>
    /// <b>Every item's live building session is stopped and WAITED ON first, before the checkout</b> (commit
    /// review #310): the building session stays alive between turns, and a Parked item's own session keeps
    /// its pending approval open, so once the folder is back on the base branch a person answering that
    /// approval later would have the agent's attempt_commit land there, not on the item's own attempt branch.
    /// <see cref="IAutopilotBuilder.StopAsync"/> itself waits for the process to actually end (killing one is
    /// not instant), so by the time it returns the session genuinely cannot write any more (fifth pass: the
    /// first version of this only asked the registry to stop, which returns at once and proves nothing by
    /// itself). <see cref="StopNowAsync"/> already stops every item's session for its own reason (ADR decision
    /// 13), so on that path this is a harmless second call to the same documented no-op; on every OTHER ending
    /// (completion, a limit, a seam failure, "stop after this item" and cannot-resume) nothing else does, so
    /// skipping this here would leave exactly the gap this paragraph describes. It runs INSIDE the same try the
    /// seam call does (commit review #310, third pass): a stop that cannot succeed (or times out) must still
    /// leave the run ended with a note, never propagate out of the tick and leave the stored row Running for
    /// the next tick to repeat the very seam call ADR decision 10 forbids repeating.
    /// </para>
    /// </summary>
    private async Task<AutopilotRun> MaybeReturnWorkspaceAsync(AutopilotRun before, AutopilotRun after, CancellationToken ct)
    {
        if (before.State != AutopilotRunState.Running
            || after.State == AutopilotRunState.Running
            || _workspaceReturn is null
            || after.BaseBranch is not { Length: > 0 } baseBranch
            || after.BaseWorkspaceId is not { Length: > 0 } workspaceId)
        {
            return after;
        }

        try
        {
            // A known, accepted gap (#305): a StopAsync that throws (the process never reports ending, the
            // same shape StopNowAsync's own loop guards against with its own try per item) aborts this
            // foreach, so items AFTER the one that threw are never asked to stop here. AgentAutopilotBuilder
            // .BuildAsync's own cross-run check (a later run resuming the SAME item stops a leftover session
            // for that item specifically before starting fresh) is what keeps a resumed item safe even when
            // this loop left its old session running; this foreach itself is not hardened against it.
            foreach (var queued in after.Queue)
            {
                await _builder.StopAsync(queued.Item, ct).ConfigureAwait(false);
            }

            // ADR 0031 decision 15 (#308, commit review's finding): a PARALLEL run (effective cap above 1)
            // never switches the person's own folder at all - every item got its own worktree - so there is
            // nothing to return here, and checking the base branch out over whatever the person has since
            // done with that folder would touch a checkout decision 15 says is never touched. EndItemAsync
            // already handles every item's own worktree; this seam is scoped to the cap-1 shared-workspace
            // case alone.
            if (after.Settings.EffectiveConcurrencyCap > 1)
            {
                return after;
            }

            var outcome = await _workspaceReturn.ReturnAsync(workspaceId, baseBranch, ct).ConfigureAwait(false);
            return outcome.Note is { Length: > 0 } note ? WithEndItemNote(after, note) : after;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return WithEndItemNote(after, $"not returned to {baseBranch}: {ex.Message}");
        }
    }

    /// <summary>
    /// Ticks the run with this id and saves the result. Does nothing when there is no such run.
    /// <para>
    /// The seam call inside <see cref="TickAsync"/> (<see cref="IAutopilotBuilder.BuildAsync"/> can run for up
    /// to its own turn bound) runs OUTSIDE this run's lock, so a slow build never holds a person's "stop now"
    /// or "stop after this item" waiting on it. Only the re-read-then-save tail, after the seam call returns,
    /// is taken under the lock <see cref="StopNowAsync"/> and <see cref="StopAfterCurrentItemAsync"/> also take
    /// around their own read-modify-write of the SAME row: without a shared lock, a stop landing between this
    /// tail's own read and its save (or the reverse ordering) is a lost update, not merely a stale read a
    /// re-read could catch on its own, and this tick's result would silently overwrite a stop that already
    /// landed, which is exactly what ADR 0031 decision 13's "nothing half-written" forbids. Under the lock: a
    /// stop that already ended the run wins outright (this tick's own result is discarded, and the
    /// already-stopped row is returned instead), and a stop-after-this-item flag this tick could not have seen
    /// when it started is carried forward so <see cref="Settle"/> still sees it on whichever later tick next
    /// closes the item.
    /// </para>
    /// <para>
    /// A known, accepted gap: when a stop wins, this tick's own <see cref="AutopilotBuildResult.CostUsd"/> (or
    /// any other seam cost spent before the discard) is dropped from the run's recorded spend. Folding a
    /// discarded tick's cost into a stop that overrides it would need the engine to re-run its OWN limit and
    /// progress bookkeeping against a row it no longer trusts the shape of; left unresolved rather than solved
    /// by surprise here, since a stop already ends the run and the next dispatch starts the figure fresh.
    /// </para>
    /// </summary>
    public async Task<AutopilotRun?> TickAndSaveAsync(Guid runId, CancellationToken ct = default)
    {
        if (await _store.GetAsync(runId, ct).ConfigureAwait(false) is not { } run)
        {
            return null;
        }

        var ticked = await TickAsync(run, ct).ConfigureAwait(false);

        // Set only by the "stop after this item landed too late" branch below, which closes a run TickAsync's
        // own Settle never saw closing (see that branch's own comment): it is the one case in this method that
        // needs its OWN workspace-return call (#310), since every other save here reuses ticked as TickAsync
        // already returned it, with any workspace return TickAsync's own Settle/Conclude call already applied.
        AutopilotRun? before = null;
        AutopilotRun result;

        var gate = LockFor(runId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = await _store.GetAsync(runId, ct).ConfigureAwait(false);
            if (current is null || current.State != AutopilotRunState.Running)
            {
                return current;
            }

            // The flag landed too late for THIS tick's own Settle to have seen it (current.StopAfterCurrentItem
            // reads true here, but ticked.State is still Running, which Settle would never have left it as had
            // it seen the flag itself): if this very tick is what closed the item Settle would otherwise have
            // opened the next item for, stopping here, now, with the next item left exactly as it was, is the
            // only way "stop after this item" still means the NEXT item never runs. Comparing CurrentItem
            // before and after is what tells "the tick closed the current item" apart from "the item is still
            // open" (a retry, or progress that has not closed it yet), which must only carry the flag forward.
            // Scoped to a cap-1 run (#308): there, CurrentItem IS the only active item, so "it changed" can only
            // mean "the old one closed and a new one opened", exactly the case this branch exists for. Above
            // cap 1 this comparison cannot tell "a sibling that was already active became the first ACTIVE
            // entry" apart from "a Queued item was genuinely let in", so it is skipped there: the flag is
            // still forwarded below, and Settle's own fixed condition (every non-Queued item closed) catches
            // the real stop honestly on whichever later tick actually closes the last one, at the cost of a
            // narrow, documented race (a Queued sibling that left Queued on this exact tick, before the flag's
            // own effect reaches TickAsync's own active-item selection on the next one, still gets its single
            // zero-cost Prompting move and is then treated as "running" like any other, per the acceptance's
            // "lets every running item finish").
            if (ticked.State == AutopilotRunState.Running
                && current.StopAfterCurrentItem
                && current.Settings.EffectiveConcurrencyCap <= 1
                && current.CurrentItem is { } previousCurrent
                && ticked.CurrentItem is { } nextCurrent
                && nextCurrent.Item != previousCurrent.Item)
            {
                var stop = new AutopilotStop(
                    AutopilotStopKind.StoppedByPerson,
                    AutopilotStopScope.Run,
                    "stop after this item",
                    "a person asked the run to stop once the current item closed",
                    _clock.GetUtcNow(),
                    null,
                    "The current item reached its end, and a person had asked the run to stop once it did; "
                        + "the next item was about to open and was left exactly as it was.");
                before = current;
                result = await _store.SaveAsync(
                    ticked.WithStop(stop) with { State = AutopilotRunState.Stopped }, ct).ConfigureAwait(false);
            }
            else
            {
                if (current.StopAfterCurrentItem && !ticked.StopAfterCurrentItem)
                {
                    ticked = ticked with { StopAfterCurrentItem = true };
                }

                result = await _store.SaveAsync(ticked, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }

        if (before is null)
        {
            return result;
        }

        // Outside the lock, the same reasoning StopNowAsync's own seam call already follows: the Stopped row
        // is already saved, so nothing here needs the lock any more.
        var withReturn = await MaybeReturnWorkspaceAsync(before, result, ct).ConfigureAwait(false);
        return ReferenceEquals(withReturn, result) ? result : await _store.SaveAsync(withReturn, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Called once at process start: every run still <see cref="AutopilotRunState.Running"/> is resumed or stopped
    /// honestly (<see cref="AutopilotDiscipline.OnRestart"/>), and the result is saved either way.
    /// </summary>
    public async Task<IReadOnlyList<AutopilotRun>> ResumeAllAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var open = await _store.OpenRunsAsync(ct).ConfigureAwait(false);
        var resumed = new List<AutopilotRun>(open.Count);
        foreach (var run in open)
        {
            // Every run OpenRunsAsync names is still Running, so a restart that could not resume honestly
            // (CannotResume, #310) also returns the workspace to its base branch here. OnRestart reads the
            // cap off the run itself (#308): a cap-1 run restarts exactly as it always did (OnRestart's own
            // sequential rule), never the item-scoped one a parallel run's own worktrees make safe.
            var restarted = AutopilotDiscipline.OnRestart(run, now);
            var endNote = await EndItemsAsync(run, restarted, ct).ConfigureAwait(false);
            var next = await MaybeReturnWorkspaceAsync(run, WithEndItemNote(restarted, endNote), ct).ConfigureAwait(false);
            await _store.SaveAsync(next, ct).ConfigureAwait(false);
            resumed.Add(next);
        }

        return resumed;
    }

    /// <summary>
    /// "Stop now" (ADR 0031 decision 13): ends every item's live agent process that could still have one
    /// (<see cref="IAutopilotBuilder.StopAsync"/>, a no-op when there is none), stops the current item with a
    /// <see cref="AutopilotStopKind.StoppedByPerson"/> record, and stops the run. Saved either way, under this
    /// run's lock (see <see cref="TickAndSaveAsync"/>). Does nothing, and returns the run unchanged, for a run
    /// that is not <see cref="AutopilotRunState.Running"/> or has nothing open.
    /// <para>
    /// The building session stays alive between turns (<c>LiveSessionRegistry</c> keeps one running session
    /// per workspace, and a finished turn does not end the process), so an item's own session can still be
    /// running while the item itself has moved on to Reviewing, Delivering or Verifying, while it sits Parked
    /// (the builder never stops a session just because one of its calls is waiting on a person), or even while
    /// it is Stopped (an earlier per-item limit ends the ITEM, never the session; only the next item's own
    /// fresh start does that, in the ordinary course of things). So <see cref="IAutopilotBuilder.StopAsync"/>
    /// is called for EVERY item in the queue, not only the current one, trusting that call's own documented
    /// no-op for an item that never had a session: naming which stages might still have one is exactly the
    /// kind of guess that already cost two review passes to get right, and "every item" cannot be wrong the
    /// same way.
    /// </para>
    /// </summary>
    public async Task<AutopilotRun?> StopNowAsync(Guid runId, CancellationToken ct = default)
    {
        AutopilotRun before;
        AutopilotRun? stopped;
        var gate = LockFor(runId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await _store.GetAsync(runId, ct).ConfigureAwait(false) is not { } run)
            {
                return null;
            }

            var active = run.State == AutopilotRunState.Running ? run.ActiveItems : [];
            if (active.Count == 0)
            {
                return run;
            }

            before = run;
            var now = _clock.GetUtcNow();
            // Every ACTIVE item is stopped, not only the first (#308: more than one can be active at once),
            // each with its own record naming it, the same shape a single-item run always produced.
            var withStop = run;
            foreach (var current in active)
            {
                var stop = new AutopilotStop(
                    AutopilotStopKind.StoppedByPerson,
                    AutopilotStopScope.Run,
                    "stop now",
                    "a person stopped the run",
                    now,
                    current.Item,
                    "A person asked the run to stop at once; the item stays exactly where it was, with its branch "
                        + "and its pull request, if any, untouched.");
                // WaitingSinceUtc is cleared here too (the same reasoning Settle's own item-stop branch uses):
                // "stop now" can land on an item that is mid-wait on a person, and Stopped is terminal.
                withStop = withStop.With(current with { Stage = AutopilotItemStage.Stopped, WaitingSinceUtc = null })
                    .WithStop(stop);
            }

            stopped = await _store.SaveAsync(withStop with { State = AutopilotRunState.Stopped }, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        // Outside the lock, exactly like TickAndSaveAsync's own seam call: the row is already saved Stopped,
        // so nothing here needs the lock any more, and a slow process kill must never hold it. StopAsync now
        // waits for the process to actually end (#310, commit review's fifth pass) and can time out: the run
        // is already Stopped regardless, so one item's stop failing must not stop "stop now" from at least
        // trying every other item too.
        foreach (var queued in stopped.Queue)
        {
            try
            {
                await _builder.StopAsync(queued.Item, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }
        }

        var endNote = await EndItemsAsync(before, stopped, ct).ConfigureAwait(false);
        stopped = WithEndItemNote(stopped, endNote);

        // Also outside the lock, and for the same reason (#310): the run is already saved Stopped, and a
        // slow git call to return the workspace must never hold a person's next "stop now" waiting on it.
        // The row was already saved (inside the lock, above) BEFORE this note existed, so a save here is not
        // optional just because MaybeReturnWorkspaceAsync itself changed nothing: endNote alone means this
        // object differs from what the store already has.
        var withReturn = await MaybeReturnWorkspaceAsync(before, stopped, ct).ConfigureAwait(false);
        return endNote is null && ReferenceEquals(withReturn, stopped)
            ? stopped
            : await _store.SaveAsync(withReturn, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// "Stop after this item" (ADR 0031 decision 13): sets <see cref="AutopilotRun.StopAfterCurrentItem"/>,
    /// which <see cref="Settle"/> reads on every later tick to stop the run once the current item closes,
    /// rather than opening the next one. Saved under this run's lock (see <see cref="TickAndSaveAsync"/>).
    /// Does nothing, and returns the run unchanged, for a run that is not <see cref="AutopilotRunState.Running"/>.
    /// </summary>
    public async Task<AutopilotRun?> StopAfterCurrentItemAsync(Guid runId, CancellationToken ct = default)
    {
        var gate = LockFor(runId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await _store.GetAsync(runId, ct).ConfigureAwait(false) is not { } run)
            {
                return null;
            }

            if (run.State != AutopilotRunState.Running || run.StopAfterCurrentItem)
            {
                return run;
            }

            return await _store.SaveAsync(run with { StopAfterCurrentItem = true }, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    // ---- limits, checked after every action ------------------------------------------------------------------

    /// <summary>
    /// Applies whichever limit fires, item-scoped before run-scoped, overriding the stage transition the action
    /// just produced. The run limit is checked AFTER an item stop is applied, against the run that stop just
    /// produced, rather than skipped: the item's own spending just landed on the run's total, and a run ceiling
    /// that crosses on the very same tick stops the run on that tick too, not one tick late with the next item
    /// already started (ADR 0031 decision 6, "whichever fires first").
    /// </summary>
    private static AutopilotRun Settle(AutopilotRun run, AutopilotRunItem item, DateTimeOffset now)
    {
        if (run.State != AutopilotRunState.Running)
        {
            // Already settled by an earlier item's own fold within this SAME tick (#308, more than one item
            // can be active at once): that item's own update is already applied by the caller before this is
            // reached, and re-checking a run-scoped limit that already fired would only append a duplicate
            // stop record. The single-item engine this was before #308 never reached this branch, because it
            // only ever called Settle once per tick, against a run that was always still Running.
            return run;
        }

        if (AutopilotItemStages.IsOpen(item.Stage) && AutopilotDiscipline.CheckItemLimits(run, item, now) is { } itemStop)
        {
            // WaitingSinceUtc is cleared here too: an item limit (money, time, failed reviews) can fire while
            // the item is mid-wait on a person (still in its own stage, per ApplyPersonWait), and Stopped is
            // terminal, so nothing downstream must read a stale wait marker off a Stopped item.
            run = run.With(item with { Stage = AutopilotItemStage.Stopped, WaitingSinceUtc = null }).WithStop(itemStop);
        }

        if (AutopilotDiscipline.CheckRunLimits(run, now) is { } runStop)
        {
            // The run limit can fire on the SAME tick a builder's pre-approval cost was folded into an item
            // that is still mid-wait (ApplyPersonWait keeps a waiting item in its own open stage, so the item
            // stop branch above never touched it): cleared here too, the same reasoning that branch and
            // OnRestart's CannotResume use, because the run is terminal now and nothing will ever read a
            // stale wait marker off an item inside it again.
            var current = run.Queue.FirstOrDefault(i => i.Order == item.Order);
            var stopped = current is { WaitingSinceUtc: not null }
                ? run.With(current with { WaitingSinceUtc = null })
                : run;
            return stopped.WithStop(runStop) with { State = AutopilotRunState.Stopped };
        }

        // ADR 0031 decision 13, "stop after this item": a person asked for this before the item closed, and
        // it has now closed (Done, Stopped by one of the checks above, or Parked). Above cap 1 (#308), "the
        // current item" is every item that had already left Queued, not only this one: the run stops only
        // once NONE of them is still open, so a sibling still Building is let finish rather than cut off; a
        // still-Queued item is never counted as "running" and is simply never promoted (TickAsync's own
        // active-item selection excludes every Queued item once this flag is set).
        var othersStillRunning = run.Queue.Any(i =>
            i.Order != item.Order && AutopilotItemStages.IsOpen(i.Stage) && i.Stage != AutopilotItemStage.Queued);
        if (run.StopAfterCurrentItem
            && run.Queue.FirstOrDefault(i => i.Order == item.Order) is { } settled
            && !AutopilotItemStages.IsOpen(settled.Stage)
            && !othersStillRunning)
        {
            var stop = new AutopilotStop(
                AutopilotStopKind.StoppedByPerson,
                AutopilotStopScope.Run,
                "stop after this item",
                "a person asked the run to stop once the current item closed",
                now,
                null,
                "The current item reached its end, and a person had asked the run to stop once it did.");
            return run.WithStop(stop) with { State = AutopilotRunState.Stopped };
        }

        return AutopilotDiscipline.Conclude(run, now);
    }

    /// <summary>
    /// What a seam exception becomes: the item stops with a <see cref="AutopilotStopKind.SeamFailed"/> record
    /// naming WHICH call failed and the exception, against the item exactly as it stood when that call was
    /// made (so money already spent by an earlier call in the same tick, e.g. the builder's cost ahead of a
    /// throwing advice call, is never dropped). The run limit is still checked against that (same shape as
    /// <see cref="Settle"/>), and <see cref="AutopilotDiscipline.Conclude"/> decides whether the run itself is
    /// done.
    /// </summary>
    private static AutopilotRun ApplySeamFailure(AutopilotRun run, AutopilotRunItem item, string seam, Exception ex, DateTimeOffset now)
    {
        var stop = new AutopilotStop(
            AutopilotStopKind.SeamFailed,
            AutopilotStopScope.Item,
            "seam failed",
            $"{seam} threw {ex.GetType().Name}: {ex.Message}",
            now,
            item.Item,
            "A call into a seam raised an exception rather than returning an answer; repeating it risks the "
                + "exact duplicate the restart rule exists to prevent, so the item stops here instead of being "
                + "guessed at.");
        // WaitingSinceUtc is cleared here too (the same reasoning Settle's own item-stop branch uses): a seam
        // can throw while an item is mid-wait on a person, and Stopped is terminal.
        var updatedRun = run.With(item with { Stage = AutopilotItemStage.Stopped, WaitingSinceUtc = null }).WithStop(stop);

        if (run.State != AutopilotRunState.Running)
        {
            // The same idempotency guard Settle now carries (#308): a run another item's own fold already
            // stopped THIS same tick must not have a run-scoped limit re-checked and re-recorded a second time.
            return updatedRun;
        }

        if (AutopilotDiscipline.CheckRunLimits(updatedRun, now) is { } runStop)
        {
            return updatedRun.WithStop(runStop) with { State = AutopilotRunState.Stopped };
        }

        return AutopilotDiscipline.Conclude(updatedRun, now);
    }

    /// <summary>
    /// Carries the item as it stood AFTER an earlier seam call in the same tick already updated it (its cost
    /// folded in), for a later seam call in that same tick that then throws. Without this, the outer catch in
    /// <see cref="TickAsync"/> would record the failure against the item as it stood before either call ran,
    /// dropping the earlier call's cost from the run's total.
    /// </summary>
    private sealed class SeamFailureException(AutopilotRunItem itemSoFar, string seam, Exception cause)
        : Exception(cause.Message, cause)
    {
        public AutopilotRunItem ItemSoFar { get; } = itemSoFar;

        public string Seam { get; } = seam;
    }

    // ---- one seam call per stage -------------------------------------------------------------------------------

    private Task<(AutopilotRunItem Item, bool Progressed, AutopilotStop? WaitStop)> RunStageAsync(
        AutopilotRun run, AutopilotRunItem item, CancellationToken ct) =>
        item.Stage switch
        {
            AutopilotItemStage.Prompting => RunPromptAsync(run, item, ct),
            AutopilotItemStage.Building => RunBuildAsync(run, item, ct),
            AutopilotItemStage.Reviewing => RunReviewAsync(item, ct),
            AutopilotItemStage.Delivering => RunDeliverAsync(run, item, ct),
            AutopilotItemStage.Verifying => RunVerifyAsync(run, item, ct),
            // Done, Stopped and Parked are terminal for a tick; CurrentItem never returns one of them.
            _ => Task.FromResult<(AutopilotRunItem, bool, AutopilotStop?)>((item, false, null)),
        };

    private async Task<(AutopilotRunItem, bool, AutopilotStop?)> RunPromptAsync(AutopilotRun run, AutopilotRunItem item, CancellationToken ct)
    {
        AutopilotPrompt prompt;
        try
        {
            prompt = await _operator.WritePromptAsync(AutopilotOperatorContext.From(run), item.Item, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Named explicitly ("the operator's prompt"), not left to the stage name: without this, the stop
            // record names the operator only when the exception's own message happens to, which an exception
            // this slice's real implementation did not throw (a tracker fault, an unexpected runner failure)
            // would not.
            throw new SeamFailureException(item, "the operator's prompt", ex);
        }

        return (item with
        {
            Stage = AutopilotItemStage.Building,
            SpentUsd = item.SpentUsd + prompt.CostUsd,
            Prompt = prompt.Text,
        }, false, null);
    }

    /// <summary>
    /// ADR 0031 decision 12 (#305), "waiting and parking": only this method's own <c>result.NeedsPerson</c>
    /// branch ever waits before parking, never the other four <see cref="Park"/> call sites in this class
    /// (the operator declining a retry below, an unresolved review verdict, a delivery that could not proceed,
    /// a verification that needs a person), which still park AT ONCE exactly as before this decision landed.
    /// The reason is deliberate, not an oversight: <c>NeedsPerson</c> names a pending approval or an open
    /// question sitting in the SAME live session (<c>AgentAutopilotBuilder</c>'s own doc comment), which a
    /// person can answer while the run keeps ticking, and re-asking the builder seam to find that out is
    /// reading that session's own state, never a fresh turn or a fresh charge. None of the other four has
    /// anything comparable sitting open for a person to resolve mid-run: the agent's own turn has already
    /// ENDED in every one of them, there is nothing left to poll, and re-running the step every tick for up
    /// to an hour (another build turn and advice call, another review, another push, another verification)
    /// would spend real money and possibly contradict the operator's own advice, so those four are left
    /// exactly as #297 built them.
    /// </summary>
    private async Task<(AutopilotRunItem, bool, AutopilotStop?)> RunBuildAsync(AutopilotRun run, AutopilotRunItem item, CancellationToken ct)
    {
        var result = await _builder
            .BuildAsync(AutopilotBuilderContext.From(run), item.Item, item.Prompt ?? string.Empty, ct)
            .ConfigureAwait(false);

        if (result.WaitingForCapacity)
        {
            // Not a failure of the item (#308 host note point 4, "spends no limit"): the concurrency gate
            // refused a FRESH session start, so nothing ran and nothing is spent, including TIME: the fold
            // loop in TickAsync rebases this item's own StartedUtc to "now" whenever WaitingForCapacityReason
            // is set, so TimePerItem never counts a wait against it, the same way OnRestart already rebases a
            // Queued or Prompting item's clock across a studio outage. It also counts as progress for the
            // RUN's own no-progress window (the same exemption #305 already gives a person-wait). The wait
            // itself ends only when a slot frees (the item is retried and the gate allows it) or a person
            // stops the run; it is never parked (there is no person to wait on) and never counted against
            // PersonWaitTimeout (there is nothing for a person to answer).
            return (item with { WaitingForCapacityReason = result.Problem }, true, null);
        }

        if (result.Refused)
        {
            // Nothing started, for one of two reasons the builder tells apart in result.Problem: #315, the
            // item's workspace could not be put back on the run's base branch (one item's leftovers, say); or
            // #328, its worktree's or workspace's contained copy never became ready, or its start had already
            // failed. Parks at once either way, the same way #310 refuses a dirty dispatch: there is no
            // attempt for the operator to advise on, and building on top of leftovers, or a copy that is not
            // there, is what this prevents.
            var refused = item with { SpentUsd = item.SpentUsd + result.CostUsd, WaitingSinceUtc = null, WaitingForCapacityReason = null };
            return (Park(refused, result.Problem ?? "The item's workspace could not be put back on the run's base branch."), false, null);
        }

        if (result.NeedsPerson)
        {
            // FailedReviewCount is folded in HERE, before Park/ApplyPersonWait (#321): a Failed review
            // captured earlier in this same turn, before whatever is making the turn wait on a person now,
            // must count against the item's ceiling WHILE it waits, not only once the wait eventually
            // settles. Settle's own check runs against whatever item this call returns, open stage or not
            // (ADR 0031 decision 6, "whichever limit fires first": money and time already fire mid-wait the
            // same way), so a count that reaches the ceiling here stops the item instead of parking it.
            var spentWaiting = item with
            {
                SpentUsd = item.SpentUsd + result.CostUsd,
                WaitingForCapacityReason = null,
                FailedReviews = item.FailedReviews + result.FailedReviewCount,
            };
            var parked = Park(spentWaiting, result.Problem ?? "The building agent asked for a person.");
            var (waited, stop) = AutopilotDiscipline.ApplyPersonWait(run, item, parked, _clock.GetUtcNow());

            // Still waiting (stop is null) counts as progress: the shipped no-progress window is shorter than
            // the shipped person-wait timeout, and without this the run-level no-progress stop would always
            // win first and end the whole run during a wait ADR decision 12 explicitly allows up to an hour
            // for. The tick that actually times out and parks does NOT count (stop is not null), the same way
            // reaching Stopped via any other limit never does either.
            return (waited, stop is null, stop);
        }

        // WaitingSinceUtc and WaitingForCapacityReason are cleared here, on every path below, whether or not
        // either was ever set: an item that asked for a person, or waited for a slot, on an earlier tick and
        // is answered or started on this one must not carry a stale marker into Reviewing, a retried
        // Building, or anywhere else.
        var spent = item with { SpentUsd = item.SpentUsd + result.CostUsd, WaitingSinceUtc = null, WaitingForCapacityReason = null };

        if (result.CommitMade)
        {
            // Progress is the branch moving, not the builder's word for it: a result that reports a commit but
            // names the SAME head the item already held is not a new commit landing, so it does not reset the
            // no-progress window (ADR 0031 decision 6, "a new commit on the item's branch"). FailedReviewCount
            // is added here too (#321): an EARLIER attempt_commit in this same turn can have failed review
            // before a LATER one landed the commit, and that earlier failure still owes its own count, since
            // the Reviewing stage that follows only ever reads the ONE review for the commit that just landed.
            return (spent with
            {
                Stage = AutopilotItemStage.Reviewing,
                BranchHeadSha = result.BranchHeadSha,
                FailedReviews = spent.FailedReviews + result.FailedReviewCount,
            }, result.BranchHeadSha is { Length: > 0 } sha && sha != item.BranchHeadSha, null);
        }

        if (result.ReviewFailed)
        {
            // The commit reviewer FAILS a commit by refusing it before the tool itself ever runs (backlog 243),
            // so this attempt never reaches the Reviewing stage the way a passed one does: the only place ADR
            // 0031 decision 6's per-item review ceiling can be counted is here. The item stays in Building (the
            // next tick tries again), bounded by Settle's own check of FailedReviews against the limit, exactly
            // as a passed-then-failing Reviewing stage would bound it. FailedReviewCount can be more than one
            // (#321): a single turn can retry attempt_commit more than once before it ends with nothing
            // committed, each attempt reviewed on its own, and every failed one counts.
            return (spent with { FailedReviews = spent.FailedReviews + Math.Max(1, result.FailedReviewCount) }, false, null);
        }

        // A person's "stop now" can land while the builder's own call above was still running (it can run for
        // up to its whole turn bound), and TickAndSaveAsync's tail already discards this tick's result once
        // that has happened. Asking the operator for advice anyway would still spend real money on a call
        // nobody will ever read the answer to, so it is skipped here rather than only wasted later.
        //
        // Only an EXPLICIT "no longer running" answer skips the call: TickAsync is also called directly,
        // without ever being saved to the store first (every test that exercises it alone does exactly
        // that), and a missing row must read as "unknown", never as "stopped", or this would silently skip
        // advice for every one of those. A plain, unlocked read: a stop landing in the few instructions
        // between this check and the tail's own re-read under the lock is not worth guarding further, since
        // the tail is what actually decides whether this tick's result is kept; this only stops the MOST
        // common and most wasteful case, a stop that already landed well before the builder's call returned.
        if (await _store.GetAsync(run.Id, ct).ConfigureAwait(false) is { State: not AutopilotRunState.Running })
        {
            return (spent, false, null);
        }

        // No commit and no person asked for: the operator advises whether another attempt is warranted (ADR
        // decision 10, advice only). A retry stays in Building, bounded by the limits Settle checks next;
        // anything else parks for a person AT ONCE (see this method's own doc comment: nothing is left open
        // in the session for a person to resolve, so there is nothing to wait on).
        AutopilotAdvice advice;
        try
        {
            advice = await _operator
                .AdviseOnFailureAsync(AutopilotOperatorContext.From(run), item.Item, result.Problem ?? "The build made no commit.", ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The builder's own cost (already folded into "spent") must not be dropped just because the
            // FOLLOW-UP advice call is the one that throws.
            throw new SeamFailureException(spent, "the operator's advice", ex);
        }

        // The advice call costs money too (#300), and it must land on the item before the money limits are
        // checked, the same reasoning as the builder's own cost above.
        var advised = spent with { SpentUsd = spent.SpentUsd + advice.CostUsd };
        return advice.RetryBuild
            ? (advised, false, null)
            : (Park(advised, advice.Reason ?? result.Problem ?? "The build failed."), false, null);
    }

    private async Task<(AutopilotRunItem, bool, AutopilotStop?)> RunReviewAsync(AutopilotRunItem item, CancellationToken ct)
    {
        var review = await _reviews.ReviewAsync(item.Item, ct).ConfigureAwait(false);
        var spent = item with { SpentUsd = item.SpentUsd + review.CostUsd };

        return review.Outcome switch
        {
            CommitReviewOutcome.Passed => (spent with { Stage = AutopilotItemStage.Delivering }, false, null),
            CommitReviewOutcome.Failed => (spent with
            {
                Stage = AutopilotItemStage.Building,
                FailedReviews = spent.FailedReviews + 1,
            }, false, null),
            // Parks at once (RunBuildAsync's own doc comment explains why this is not one of the wait sites):
            // the review already ran and ended inconclusively, so there is nothing left open to re-poll.
            _ => (Park(spent, review.Problem ?? "The commit reviewer gave no verdict."), false, null),
        };
    }

    private async Task<(AutopilotRunItem, bool, AutopilotStop?)> RunDeliverAsync(AutopilotRun run, AutopilotRunItem item, CancellationToken ct)
    {
        if (item.BranchHeadSha is not { Length: > 0 } sha)
        {
            return (Park(item, "There is no branch to deliver."), false, null);
        }

        var result = await _delivery.DeliverAsync(AutopilotDeliveryContext.From(run), item.Item, sha, ct).ConfigureAwait(false);
        return result.Delivered
            ? (item with { Stage = AutopilotItemStage.Verifying }, false, null)
            // Parks at once: the delivery attempt has already finished (successfully or not), so there is
            // nothing pending to re-check by asking again.
            : (Park(item, result.Problem ?? "Delivery did not complete."), false, null);
    }

    private async Task<(AutopilotRunItem, bool, AutopilotStop?)> RunVerifyAsync(AutopilotRun run, AutopilotRunItem item, CancellationToken ct)
    {
        if (item.BranchHeadSha is not { Length: > 0 } sha)
        {
            return (Park(item, "There is no branch to verify."), false, null);
        }

        var result = await _delivery.VerifyAsync(AutopilotDeliveryContext.From(run), item.Item, sha, ct).ConfigureAwait(false);
        if (result.Passed)
        {
            return (item with { Stage = AutopilotItemStage.Done }, true, null);
        }

        if (result.Pending)
        {
            // CI or the scenario has not settled yet: neither progress nor a failure, so the item stays in
            // Verifying and the next tick checks again, bounded by the item's own time and no-progress limits.
            return (item, false, null);
        }

        if (result.NeedsPerson)
        {
            // Parks at once: verification itself could not be run, which is the delivery seam reporting a
            // problem, not a pending approval anywhere a person could answer while the run keeps ticking.
            return (Park(item, result.Problem ?? "Verification needs a person."), false, null);
        }

        // Red CI or a failed scenario: the operator reads the failure (ADR decision 10) to decide whether
        // another attempt is warranted; the building agent then reads the actual CI log or verifier report
        // itself, with its own tool access, and fixes it on the same branch (ADR decision 5), bounded by the
        // item's own limits, which Settle checks next.
        AutopilotAdvice advice;
        try
        {
            advice = await _operator
                .AdviseOnFailureAsync(AutopilotOperatorContext.From(run), item.Item, result.Problem ?? "Verification failed.", ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The item is unchanged by VerifyAsync itself, so there is no earlier cost in this tick to carry
            // forward; still routed through SeamFailureException so the stop names "the operator's advice"
            // rather than the stage name alone, the same reasoning RunBuildAsync's own wrap uses.
            throw new SeamFailureException(item, "the operator's advice", ex);
        }

        var advised = item with { SpentUsd = item.SpentUsd + advice.CostUsd };
        return advice.RetryBuild
            ? (advised with { Stage = AutopilotItemStage.Building }, false, null)
            : (Park(advised, advice.Reason ?? result.Problem ?? "Verification failed."), false, null);
    }

    private static AutopilotRunItem Park(AutopilotRunItem item, string reason) =>
        item with { Stage = AutopilotItemStage.Parked, ParkedReason = reason, WaitingSinceUtc = null };
}
