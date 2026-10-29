using Fravashi.Domain.WorkItems;

namespace Fravashi.Domain.Autopilot;

/// <summary>
/// Which items a dispatch takes, and in what order (ADR 0031 decision 1: "only Ready items a person has
/// MARKED for autopilot, one at a time in board order. Nothing it was not handed."). Pure: no store, no
/// project, just a list of items a caller already read.
/// </summary>
public static class AutopilotSelection
{
    /// <summary>
    /// Every item that is both marked and Ready, in board order (rank, then number: the same order
    /// <c>WorkItemsPage.Filtered</c> shows a column in). A marked item that has moved past Ready (say, once
    /// the engine actually starts building it) is not reselected by a NEW dispatch; it is already somebody's
    /// queue entry. An item that is Ready but not marked is skipped, and a marked item that is not Ready
    /// (impossible to author, but a mirrored shape could in principle carry it) is skipped too. This alone
    /// does NOT stop a marked item still in an open run's queue (Queued, or Prompting: the engine has not
    /// moved its status off Ready yet) from being picked up again by a second dispatch; the dispatcher
    /// guards that by refusing a second dispatch while a run for the project is still open.
    /// </summary>
    public static IReadOnlyList<WorkItem> MarkedReadyInBoardOrder(IEnumerable<WorkItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return
        [
            .. items
                .Where(i => i.MarkedForAutopilot && i.Status == WorkItemStatus.Ready)
                .OrderBy(i => i.Rank)
                .ThenBy(i => i.Number),
        ];
    }

    /// <summary>
    /// Every item a project's most recent run left Parked, OR left Queued without ever having started it,
    /// that is STILL marked for autopilot and still <see cref="WorkItemStatus.InProgress"/>, in board order
    /// (ADR 0031 decision 12, #305: "a parked item can be resumed in a later run"). The second shape is what
    /// a RESUMED item becomes when ITS OWN resuming run stops (a run limit, "stop now", or a restart that
    /// cannot resume) before ever reaching it: an earlier SIBLING item in that same queue was still open and
    /// current when the stop fired, so this item never left <see cref="AutopilotItemStage.Queued"/> with
    /// <see cref="AutopilotRunItem.StartedUtc"/> still null, and without reading that shape too it would be
    /// marked, In Progress, and permanently invisible to every later dispatch: no longer named Parked by any
    /// run this reads, and not Ready either, so <see cref="MarkedReadyInBoardOrder"/> never picks it up.
    /// What ties either shape to autopilot is being named by the run's own queue at all: the mark and the
    /// InProgress status alone do not prove that (the authoring rule in #298 only gates turning the mark ON,
    /// which requires Ready at that moment, and nothing stops a person marking an item and then moving it to
    /// In Progress by hand afterward, outside autopilot). An item a person UNMARKED, or moved to a different
    /// status, is left out either way. A parked item still READY when it parked (nothing guarantees the
    /// building agent transitioned it before its first approval) is not named here, but needs no special
    /// handling either: <see cref="MarkedReadyInBoardOrder"/> already resumes it as an ordinary fresh pick,
    /// since it is still marked and still Ready. <paramref name="lastRunQueue"/> is null for a project that
    /// has never run, which selects nothing.
    /// </summary>
    public static IReadOnlyList<WorkItem> ParkedInBoardOrder(
        IEnumerable<WorkItem> items, IEnumerable<AutopilotRunItem>? lastRunQueue)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (lastRunQueue is null)
        {
            return [];
        }

        var stillWaiting = lastRunQueue
            .Where(i => i.Stage == AutopilotItemStage.Parked
                || (i.Stage == AutopilotItemStage.Queued && i.StartedUtc is null))
            .Select(i => i.Item)
            .ToHashSet();

        return
        [
            .. items
                .Where(i => stillWaiting.Contains(i.Id) && i.MarkedForAutopilot && i.Status == WorkItemStatus.InProgress)
                .OrderBy(i => i.Rank)
                .ThenBy(i => i.Number),
        ];
    }
}
