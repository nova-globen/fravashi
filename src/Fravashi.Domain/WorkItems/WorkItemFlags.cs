namespace Fravashi.Domain.WorkItems;

/// <summary>
/// The Blocked and On-Hold/Deferred flags (plan section 1.4). They decorate an item; they are never statuses.
/// <para>
/// Any combination is REPRESENTABLE, because a provider can say it (ADO's Blocked field is a bare Yes/No, so a
/// mirrored Blocked flag has no reason). What Fravashi may AUTHOR is narrower and is judged by
/// <see cref="WorkItemRules.ValidateFlags"/>: a Blocked reason is mandatory, and no flag is set on a terminal
/// item. A reason without its flag is never kept: the constructor drops it.
/// </para>
/// </summary>
public sealed record WorkItemFlags
{
    /// <summary>Creates a flag set. A reason is kept only while its flag is set.</summary>
    public WorkItemFlags(bool blocked, string? blockedReason, bool deferred, string? deferredReason)
    {
        Blocked = blocked;
        BlockedReason = blocked ? blockedReason : null;
        Deferred = deferred;
        DeferredReason = deferred ? deferredReason : null;
    }

    /// <summary>Neither flag set.</summary>
    public static WorkItemFlags None { get; } = new(false, null, false, null);

    /// <summary>An explicit statement that the item cannot proceed. Never derived from blocked-by (D8).</summary>
    public bool Blocked { get; }

    /// <summary>Why it is blocked; mandatory when authored, nullable when mirrored.</summary>
    public string? BlockedReason { get; }

    /// <summary>Parked: still open and unresolved, so it resurfaces in triage.</summary>
    public bool Deferred { get; }

    /// <summary>Why it is parked; optional.</summary>
    public string? DeferredReason { get; }

    /// <summary>True when either flag is set.</summary>
    public bool Any => Blocked || Deferred;

    /// <summary>The same flags with Blocked set and its reason.</summary>
    public WorkItemFlags WithBlocked(string? reason) => new(true, reason, Deferred, DeferredReason);

    /// <summary>The same flags with Blocked cleared.</summary>
    public WorkItemFlags WithoutBlocked() => new(false, null, Deferred, DeferredReason);

    /// <summary>The same flags with Deferred set and its optional reason.</summary>
    public WorkItemFlags WithDeferred(string? reason) => new(Blocked, BlockedReason, true, reason);

    /// <summary>The same flags with Deferred cleared.</summary>
    public WorkItemFlags WithoutDeferred() => new(Blocked, BlockedReason, false, null);
}
