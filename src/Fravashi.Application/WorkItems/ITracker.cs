using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;

namespace Fravashi.Application.WorkItems;

/// <summary>
/// The application-facing tracker port (canonical artifact model plan section 3.2): create, read, update,
/// transition, move, link and query the work items of ONE project. Every instance is bound to its
/// <see cref="Project"/>, so no call can wander into another project's graph by passing the wrong id.
/// <para>
/// A refusal is a <see cref="TrackerResult{T}"/>, never an exception: <c>Invalid</c> carries the domain's
/// codes, fields and sentences, <c>Conflict</c> the current version, <c>Unsupported</c> the reason a provider
/// cannot act. Exceptions stay for genuine faults, such as an unreadable database. Evidence and "what can start
/// now" arrive with increment C2.
/// </para>
/// </summary>
public interface ITracker
{
    /// <summary>The project this tracker is bound to.</summary>
    ProjectId Project { get; }

    /// <summary>The provider's name; <c>local</c> for the local board.</summary>
    string ProviderName { get; }

    /// <summary>What this tracker can actually do, per operation, never inferred from a token being present.</summary>
    TrackerCapabilities Capabilities { get; }

    /// <summary>Creates an item at the bottom of its initial status column, allocating the next per-project number.</summary>
    Task<TrackerResult<WorkItem>> CreateAsync(NewWorkItem item, MutationContext context, CancellationToken ct = default);

    /// <summary>The item with this id in this project, or null.</summary>
    Task<WorkItem?> GetAsync(WorkItemId id, CancellationToken ct = default);

    /// <summary>The item with this per-project number, or null.</summary>
    Task<WorkItem?> GetByNumberAsync(int number, CancellationToken ct = default);

    /// <summary>A page of items matching the query, ordered by status, then rank, then number.</summary>
    Task<WorkItemPage> QueryAsync(WorkItemQuery query, CancellationToken ct = default);

    /// <summary>Every recorded change of the item, oldest first.</summary>
    Task<IReadOnlyList<WorkItemEvent>> HistoryAsync(WorkItemId id, CancellationToken ct = default);

    /// <summary>
    /// Every stored edge touching the item, in normal form. Read each with <see cref="WorkItemLink.ReadingFrom"/>
    /// to get both readings (<c>blocks</c> from one end, <c>blocked_by</c> from the other).
    /// </summary>
    Task<IReadOnlyList<WorkItemLink>> LinksAsync(WorkItemId id, CancellationToken ct = default);

    /// <summary>Changes every settable field except the status and links; a stale version is a conflict.</summary>
    Task<TrackerResult<WorkItem>> UpdateAsync(WorkItemId id, WorkItemPatch patch, long expectedVersion, MutationContext context, CancellationToken ct = default);

    /// <summary>
    /// Changes the status (and resolution) under the authoring rules. A change of status places the item in the
    /// destination column: immediately after <c>after</c>, else immediately before <c>before</c>, else at the
    /// bottom (plan section 5.3). A transition to the current status is not a placement and ignores the anchors.
    /// A move into Done is also judged by <see cref="DoneEvidence"/>: evidence on every acceptance criterion, or
    /// <see cref="MutationContext.Reason"/>, else <see cref="TrackerCodes.DoneNeedsEvidence"/>.
    /// </summary>
    Task<TrackerResult<WorkItem>> TransitionAsync(WorkItemId id, WorkItemStatus to, WorkItemResolution? resolution, WorkItemId? after, WorkItemId? before, long expectedVersion, MutationContext context, CancellationToken ct = default);

    /// <summary>Reorders the item within its current status column; at least one anchor is required.</summary>
    Task<TrackerResult<WorkItem>> MoveAsync(WorkItemId id, WorkItemId? after, WorkItemId? before, long expectedVersion, MutationContext context, CancellationToken ct = default);

    /// <summary>
    /// Links two items, stated in any reading. Takes no version (decision D27) and bumps both items' versions.
    /// Linking an edge that already exists is an idempotent success that writes nothing.
    /// </summary>
    Task<TrackerResult<WorkItemLink>> LinkAsync(WorkItemId from, WorkItemLinkType type, WorkItemId to, MutationContext context, CancellationToken ct = default);

    /// <summary>
    /// Removes the edge, stated in either reading. The value is true when an edge was removed and false when there
    /// was none to remove (which writes nothing).
    /// </summary>
    Task<TrackerResult<bool>> UnlinkAsync(WorkItemId from, WorkItemLinkType type, WorkItemId to, MutationContext context, CancellationToken ct = default);

    /// <summary>
    /// Records evidence against an item (increment C2). APPEND-ONLY on purpose: there is no update and no
    /// delete here, so a wrong record is followed by a newer one and the history of what was believed when
    /// stays readable. Deleting the CONTENT is item 75's lifecycle rather than an edit.
    /// <para>
    /// It takes no expected version, like a link, because it adds to the item rather than overwriting any
    /// value of it; the item's version is bumped so a reader knows something about it moved. Evidence naming
    /// a criterion or a scenario the item does not have is <c>Invalid</c>, never stored dangling.
    /// </para>
    /// </summary>
    Task<TrackerResult<EvidenceRecord>> RecordEvidenceAsync(WorkItemId id, NewEvidence evidence, MutationContext context, CancellationToken ct = default);

    /// <summary>The evidence recorded against an item, oldest first.</summary>
    Task<IReadOnlyList<EvidenceRecord>> EvidenceAsync(WorkItemId id, CancellationToken ct = default);

    /// <summary>
    /// What can start now (plan section 4.4): every Ready item with neither flag set, every blocker Done and
    /// no uncleared person blocker, in board order. Everything that does not qualify comes back too, with
    /// EVERY failing reason rather than the first, so a board and an agent can both say why.
    /// </summary>
    Task<StartabilityReport> WhatCanStartNowAsync(StartableQuery query, CancellationToken ct = default);
}
