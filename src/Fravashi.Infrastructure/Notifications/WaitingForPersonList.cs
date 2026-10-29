using System.Globalization;
using Fravashi.Application.Autopilot;
using Fravashi.Application.Notifications;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Autopilot;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Sessions;

namespace Fravashi.Infrastructure.Notifications;

/// <summary>
/// The real <see cref="IWaitingForPersonList"/> (backlog 304): every live session waiting on an approval or a
/// question (via <see cref="WaitingSessionReader"/>, cross-workspace, the same reach
/// <c>LiveSessionsCard</c> already has), plus every parked autopilot item from each project's most RECENT
/// run, open or ended alike (ADR 0031 decision 12, #305: a parked item stays listed after its own run has
/// ended, which is the only way a person who was not watching when it parked ever sees it).
/// <para>
/// Listed whenever it is still marked for autopilot (an item a person UNMARKED is dropped: the mark is what
/// says this is still autopilot's business). Status is deliberately NOT part of this list's own filter,
/// unlike <c>AutopilotSelection.ParkedInBoardOrder</c>'s resume predicate, which also requires
/// <see cref="WorkItemStatus.InProgress"/>: nothing in the engine or the building agent's own prompt
/// GUARANTEES that transition happens before the agent's first approval parks it (CLAUDE.md asks the agent
/// to move it, but an agent that parks on its very first tool call may never have reached that line), so an
/// item still Ready when it parked must still be listed, even though a NEW dispatch would already resume it
/// through the ordinary Ready selection rather than needing <c>ParkedInBoardOrder</c> at all. The two reads
/// agree on being marked; they do not need to agree on status for the list to be honest.
/// </para>
/// </summary>
public sealed class WaitingForPersonList(
    LiveSessionRegistry registry,
    IAutopilotRunStore runs,
    ITrackerResolver trackers,
    IParkedNoticeDismissals? dismissals = null) : IWaitingForPersonList
{
    public async Task<IReadOnlyList<WaitingForPersonEntry>> CurrentAsync(CancellationToken ct = default)
    {
        var entries = new List<WaitingForPersonEntry>();

        foreach (var session in registry.Sessions.Where(s => s.IsRunning))
        {
            var snapshot = new WaitingSessionSnapshot(
                session.Id, session.WorkspaceId, session.TurnInFlight, session.Pending, session.Transcript);
            if (WaitingSessionReader.Read(snapshot) is { } entry)
            {
                entries.Add(entry);
            }
        }

        var latest = await runs.LatestPerProjectAsync(ct).ConfigureAwait(false);
        foreach (var run in latest)
        {
            // Genuinely Parked is listed whatever the run's own state (it can sit Parked in a STILL-RUNNING
            // run, since the run moves on to the next item without it, ADR 0031 decision 12). The second
            // shape, Queued with no StartedUtc, is what a RESUMED item becomes when ITS OWN run stops before
            // ever reaching it (an earlier sibling was still open and current): only counted once the run has
            // ENDED, because while a run is still Running, every later item in its queue is simply waiting
            // its ordinary turn, not stuck (AutopilotSelection.ParkedInBoardOrder reads the identical shape
            // for the resume rule, for the identical reason: without it, such an item is marked, In Progress,
            // and permanently invisible to both this list and every later dispatch).
            foreach (var item in run.Queue.Where(i =>
                i.Stage == AutopilotItemStage.Parked
                || (run.State != AutopilotRunState.Running && i.Stage == AutopilotItemStage.Queued && i.StartedUtc is null)))
            {
                // Work item #356: a person dismissed this run's notice for this item; it stays marked and a later
                // dispatch still resumes it, it is just no longer listed.
                if (dismissals?.IsDismissed(run.Id, item.Item) == true)
                {
                    continue;
                }

                var tracker = trackers.For(run.Project);
                var found = await tracker.GetAsync(item.Item, ct).ConfigureAwait(false);
                if (found is not null && !found.MarkedForAutopilot)
                {
                    // A person unmarked it: it is no longer autopilot's business, so it is no longer on
                    // autopilot's own waiting list either.
                    continue;
                }

                entries.Add(ParkedEntry(run, item, found));
            }
        }

        return entries;
    }

    private static WaitingForPersonEntry ParkedEntry(AutopilotRun run, AutopilotRunItem item, WorkItem? found)
    {
        var label = found is null
            ? item.Item.Value
            : string.Create(CultureInfo.InvariantCulture, $"#{found.Number} {found.Title}");
        // Work item #356: a reason stored before tool calls were put into words can still carry a raw mcp__ name.
        var reason = item.ParkedReason is { Length: > 0 } stored
            ? StudioToolDescriptions.Humanize(stored)
            : item.Stage == AutopilotItemStage.Parked
                ? "Parked, waiting on a person"
                : "Still queued; the run that would have resumed it ended before reaching it.";

        // The stored reason is the STAGE's own wording from when it parked (ADR 0031 decision 12, #305:
        // "The building session is waiting on a person to approve ..."), true only while the run that parked
        // it is still Running. Once it has ended, the run can never be ticked again regardless of whether
        // MaybeReturnWorkspaceAsync actually reached that session (it only runs when the workspace-return
        // seam is wired and the run carries a BaseBranch/BaseWorkspaceId, which a run dispatched before #310
        // never does): either way, "dispatch again" is the only real next step, so the sentence names that
        // rather than repeating a live-session claim this code cannot promise is still true.
        var what = run.State == AutopilotRunState.Running
            ? $"{label} is parked: {reason}"
            : $"{label} is parked: {reason} This run has ended; dispatch the project again to resume it.";

        return new WaitingForPersonEntry(
            WaitingForPersonKind.Parked,
            what,
            item.StartedUtc ?? run.StartedUtc,
            run.BaseWorkspaceId ?? string.Empty,
            Item: item.Item,
            Project: run.Project,
            ItemNumber: found?.Number,
            Run: run.Id,
            Reason: reason,
            RunEnded: run.State != AutopilotRunState.Running);
    }
}
