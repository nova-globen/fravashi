using Fravashi.Domain.Mirroring;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;

namespace Fravashi.Application.Mirroring;

/// <summary>
/// The durable write queue (ADR 0018 decision 8). Durable is the whole point: an in-memory queue loses the
/// user's changes on a crash, which is the outcome an outbox exists to prevent, and it cannot record the
/// in-flight window that makes a crashed creation recoverable.
/// </summary>
public interface IOutboxStore
{
    /// <summary>
    /// Records a write to be made. Queueing an operation id that is already recorded returns the existing
    /// entry unchanged, so a caller that retries the enqueue itself does not queue the write twice.
    /// </summary>
    Task<OutboxEntry> QueueAsync(OutboxEntry entry, CancellationToken ct = default);

    /// <summary>Every entry for one item, oldest first, whatever state it is in.</summary>
    Task<IReadOnlyList<OutboxEntry>> ForItemAsync(ProjectId project, WorkItemId item, CancellationToken ct = default);

    /// <summary>Every entry in the project that is not yet a settled success or an abandonment.</summary>
    Task<IReadOnlyList<OutboxEntry>> OpenAsync(ProjectId project, CancellationToken ct = default);

    /// <summary>
    /// Moves every entry left in flight to unresolved, and answers how many there were. Called once when
    /// the store opens: at that moment nothing can legitimately be in flight, because the only process that
    /// could have been making the call is the one that just started.
    /// </summary>
    Task<int> RecoverInFlightAsync(CancellationToken ct = default);

    /// <summary>
    /// Records where an entry has got to. <paramref name="countAttempt"/> is true only when the call
    /// actually left, so the attempt count means "times this reached the provider" rather than "times we
    /// looked at it", which is the number a person reading a stuck entry needs.
    /// </summary>
    Task<bool> MarkAsync(
        ProjectId project,
        string operationId,
        OutboxState state,
        string? resultKey,
        string? failure,
        bool countAttempt,
        DateTimeOffset now,
        CancellationToken ct = default);
}

/// <summary>
/// What the provider said about an item when it last confirmed it. Grouped into one record rather than
/// passed as six arguments because the base field values have to be written in the SAME statement as the
/// confirmation stamp: two writes could disagree, and a base that does not match the moment it was taken
/// from is worse than no base at all.
/// </summary>
/// <param name="Connection">The connection it came through.</param>
/// <param name="Key">The provider's key for it.</param>
/// <param name="ProviderVersion">The provider's version token, verbatim.</param>
/// <param name="LocalVersion">Our own version at this moment.</param>
/// <param name="BaseFields">
/// The provider-owned field values as the provider has them now, which become the base the next comparison
/// is made against. Null leaves whatever base was already recorded, for a confirmation that did not read
/// the fields (a write's response, typically).
/// </param>
public sealed record ProviderConfirmation(
    string Connection,
    string Key,
    string? ProviderVersion,
    long LocalVersion,
    IReadOnlyDictionary<string, string?>? BaseFields = null);

/// <summary>
/// The mirroring sidecar: what the provider last said about each item, and the conflicts waiting for a
/// person. The counts on <see cref="MirrorState"/> are DERIVED here from the outbox and the conflict rows
/// rather than stored as columns, because a denormalised counter that drifts produces a freshness
/// indicator that lies, and a lying indicator is worse than none.
/// </summary>
public interface IMirrorStore
{
    /// <summary>The mirroring facts about one item, or null when it is not mirrored at all.</summary>
    Task<MirrorState?> GetAsync(ProjectId project, WorkItemId item, CancellationToken ct = default);

    /// <summary>The mirroring facts for every mirrored item in the project, keyed by item.</summary>
    Task<IReadOnlyDictionary<WorkItemId, MirrorState>> ForProjectAsync(ProjectId project, CancellationToken ct = default);

    /// <summary>
    /// Records that the provider has just confirmed this item. Also CLEARS any tombstone, because an item
    /// reappearing on the provider (restored from its trash, or a key reused) is a real thing that happens
    /// and leaving the tombstone would hide a live item behind a deletion notice.
    /// </summary>
    Task ConfirmAsync(ProjectId project, WorkItemId item, ProviderConfirmation confirmation, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>
    /// The provider-owned field values as they stood at the last reconciliation, empty when there is no
    /// record of them. This is the BASE a comparison is made against, and without it "did this side move?"
    /// could only be answered from the item's version, which counts a change to any field as a change to
    /// every field.
    /// </summary>
    Task<IReadOnlyDictionary<string, string?>> BaseFieldsAsync(ProjectId project, WorkItemId item, CancellationToken ct = default);

    /// <summary>
    /// Records that the provider no longer has this item. Nothing local is deleted (ADR 0018 decision 5:
    /// "a deletion on the provider side tombstones the item locally rather than destroying its history").
    /// </summary>
    Task TombstoneAsync(ProjectId project, WorkItemId item, string reason, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Every conflict in the project still waiting for a person, oldest first.</summary>
    Task<IReadOnlyList<FieldConflict>> OpenConflictsAsync(ProjectId project, CancellationToken ct = default);

    /// <summary>
    /// Records a disagreement. Recording the same field for the same item twice while it is still open
    /// updates the values rather than stacking a second row, so a sync that runs every minute does not turn
    /// one disagreement into a hundred.
    /// </summary>
    Task<FieldConflict> RecordConflictAsync(ProjectId project, FieldConflict conflict, CancellationToken ct = default);

    /// <summary>
    /// Retires every mirror record of this project that belongs to <paramref name="connection"/> (backlog 145: the
    /// project moved to another provider). Each record is kept as history, the conflicts about those items are closed
    /// without a choice, and the items are then not mirrored anywhere, so they can be sent to the new provider. Returns
    /// how many were retired.
    /// </summary>
    Task<int> RetireAsync(ProjectId project, string connection, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Where the item was mirrored before its project moved provider, newest first.</summary>
    Task<IReadOnlyList<(string Connection, string Key)>> HistoryAsync(ProjectId project, WorkItemId item, CancellationToken ct = default);

    /// <summary>Settles one conflict the way a person chose. False when there is no such open conflict.</summary>
    Task<bool> ResolveConflictAsync(ProjectId project, string conflictId, ConflictChoice choice, DateTimeOffset now, CancellationToken ct = default);
}
