using Fravashi.Domain.Projects;

namespace Fravashi.Domain.Autopilot;

/// <summary>Where a run stands as a whole.</summary>
public enum AutopilotRunState
{
    /// <summary>Still being driven, tick by tick.</summary>
    Running,

    /// <summary>A run-level limit fired, or a restart could not resume honestly.</summary>
    Stopped,

    /// <summary>Every item reached <see cref="AutopilotItemStage.Done"/>.</summary>
    Completed,
}

/// <summary>
/// A persisted autopilot run (ADR 0031 decisions 1, 6 and 10): a dispatch's settings, its queue of marked items
/// in board order, and every stop it has recorded. The engine is the only thing that changes one; nothing here
/// decides on its own.
/// </summary>
/// <param name="Id">The run's identity.</param>
/// <param name="Project">The project it runs against.</param>
/// <param name="Settings">The dispatch's settings.</param>
/// <param name="Queue">The marked items, in board order.</param>
/// <param name="State">Where the run stands.</param>
/// <param name="StartedUtc">When the run started.</param>
/// <param name="LastProgressUtc">
/// The last time progress was seen: the queue shrinking (an item reached <see cref="AutopilotItemStage.Done"/>)
/// or a new commit landing on the current item's branch. Starts at <see cref="StartedUtc"/>.
/// </param>
/// <param name="Stops">Every stop this run has recorded, oldest first.</param>
/// <param name="StopAfterCurrentItem">
/// A person asked the run to stop once the current item next closes (ADR 0031 decision 13, "stop after this
/// item"). Read by the engine's <c>Settle</c> on every later tick; "stop now" does not use this flag, because
/// it stops the run on the same tick it is asked rather than waiting for the item to close.
/// </param>
/// <param name="BaseBranch">
/// The branch the dispatch's workspace was checked out to when this run was dispatched (#310), read once at
/// dispatch time rather than from any session's own branch record, because a session's record is process-local
/// and never persisted (see <c>AutopilotBuilderSessions</c>), and this must survive a restart. Null for a run
/// dispatched before this field existed, or whose workspace could not be reached at dispatch: there is then
/// nothing to return the workspace to when the run ends.
/// </param>
/// <param name="WorkspaceReturnNote">
/// Set once, when the run ends, whenever the workspace was NOT returned to <see cref="BaseBranch"/> (#310), in
/// words naming why: a dirty tree ("left on &lt;branch&gt;, uncommitted changes"), the session's own edits
/// still on their way from the agent's copy ("left on &lt;branch&gt;, edits still reaching the folder"), a
/// working tree that could not be read, the workspace could not be reached at all ("not returned to
/// &lt;branch&gt;: ..."), or the return seam itself raised an exception ("not returned to &lt;branch&gt;:
/// &lt;the exception's message&gt;"). Null while the run is still open, and null once it has ended if the
/// return succeeded or there was nothing to return.
/// </param>
/// <param name="BaseWorkspaceId">
/// The workspace's own id, resolved once at dispatch time (#310, commit review's third pass) and recorded
/// alongside <see cref="BaseBranch"/>: the project-to-workspace lookup answers "the most recently opened
/// workspace", which can change while a run is open if a person opens a different folder bound to the same
/// project, so the eventual return must act on the id recorded HERE, never re-resolve by project, or it could
/// check out a folder the run never touched. Null exactly when <see cref="BaseBranch"/> is null.
/// </param>
public sealed record AutopilotRun(
    Guid Id,
    ProjectId Project,
    AutopilotDispatchSettings Settings,
    IReadOnlyList<AutopilotRunItem> Queue,
    AutopilotRunState State,
    DateTimeOffset StartedUtc,
    DateTimeOffset LastProgressUtc,
    IReadOnlyList<AutopilotStop> Stops,
    bool StopAfterCurrentItem = false,
    string? BaseBranch = null,
    string? WorkspaceReturnNote = null,
    string? BaseWorkspaceId = null)
{
    /// <summary>A fresh run over a queue of items, in the order given.</summary>
    public static AutopilotRun Start(
        Guid id, ProjectId project, AutopilotDispatchSettings settings, IReadOnlyList<Domain.WorkItems.WorkItemId> items,
        DateTimeOffset now, string? baseBranch = null, string? baseWorkspaceId = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(items);

        var queue = items.Select((item, index) => AutopilotRunItem.Queue(item, index)).ToList();
        return new AutopilotRun(
            id, project, settings, queue, AutopilotRunState.Running, now, now, [],
            BaseBranch: baseBranch, BaseWorkspaceId: baseWorkspaceId);
    }

    /// <summary>What has been spent across the whole run: the sum over the queue, never stored separately.</summary>
    public decimal SpentUsd => Queue.Sum(i => i.SpentUsd);

    /// <summary>
    /// Every item the engine still has work to do in, in board order (#308, "the autopilot runs marked items
    /// in parallel up to the cap"): no new persisted shape, since <see cref="AutopilotRunItem"/> already
    /// carries each item's own stage, spend and branch independently of every other one. A run whose
    /// effective concurrency cap is 1 (today's sequential behaviour, unchanged) has at most one entry here;
    /// a run dispatched with a higher cap can have several, each advanced by its own call into the builder,
    /// bounded in practice by the concurrency gate's own count of live sessions, never by this list's length.
    /// </summary>
    public IReadOnlyList<AutopilotRunItem> ActiveItems =>
        [.. Queue.Where(i => AutopilotItemStages.IsOpen(i.Stage)).OrderBy(i => i.Order)];

    /// <summary>
    /// The first item <see cref="ActiveItems"/> names, for every caller that only ever wanted one (every
    /// per-item limit check, the Settle path and the autopilot's own run-wide figures). Null when nothing is left
    /// to do.
    /// </summary>
    public AutopilotRunItem? CurrentItem => ActiveItems.FirstOrDefault();

    /// <summary>True once every item has reached <see cref="AutopilotItemStage.Done"/>.</summary>
    public bool AllDone => Queue.Count > 0 && Queue.All(i => i.Stage == AutopilotItemStage.Done);

    /// <summary>This run with one item replaced, the queue kept in board order (<see cref="AutopilotRunItem.Order"/>).</summary>
    public AutopilotRun With(AutopilotRunItem item) =>
        this with { Queue = [.. Queue.Where(i => i.Order != item.Order).Append(item).OrderBy(i => i.Order)] };

    /// <summary>This run with a stop appended.</summary>
    public AutopilotRun WithStop(AutopilotStop stop) => this with { Stops = [.. Stops, stop] };
}
