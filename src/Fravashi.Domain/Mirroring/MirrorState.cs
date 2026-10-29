using Fravashi.Domain.WorkItems;

namespace Fravashi.Domain.Mirroring;

/// <summary>
/// What a reader is being shown when they look at a mirrored item, most important thing first (ADR 0018
/// decision 8: the local store is a working copy with a VISIBLE freshness indicator).
/// <para>
/// The order of this enum is the precedence order and that is deliberate. A tombstoned item outranks
/// everything because the thing being looked at no longer exists where it came from, and any other
/// sentence would be a smaller truth told over a larger one. A conflict outranks a queued write because a
/// queued write resolves itself and a conflict waits for a person. Never confirmed outranks stale because
/// "we have not spoken to the provider about this yet" and "we spoke a while ago" are different claims,
/// and collapsing them would let an item that was never mirrored read as merely old.
/// </para>
/// </summary>
public enum MirrorLevel
{
    /// <summary>No provider is configured for this item, so the local store is not a copy of anything.</summary>
    LocalOnly,

    /// <summary>The provider confirms this item recently enough to be shown without a caveat.</summary>
    Fresh,

    /// <summary>The provider was last heard from longer ago than the staleness window.</summary>
    Stale,

    /// <summary>The item is mirrored but the provider has never confirmed it.</summary>
    NeverConfirmed,

    /// <summary>Local changes are queued in the outbox and have not been accepted by the provider yet.</summary>
    Queued,

    /// <summary>A provider-owned field changed on both sides and a person has to say which one holds.</summary>
    Conflicted,

    /// <summary>
    /// A queued write will not go without a person: it was refused, or it was in flight when Fravashi
    /// stopped and the provider cannot be asked whether it landed. This outranks a conflict because a
    /// conflict has both values safely in hand, while a write in this state may already have changed the
    /// provider in a way nothing here has recorded.
    /// </summary>
    WriteStuck,

    /// <summary>The provider no longer has this item. It is kept here with its history rather than deleted.</summary>
    Tombstoned,
}

/// <summary>
/// The mirroring facts about ONE work item: which provider it came from, when that provider last confirmed
/// it, how many local writes are still queued for it, and whether it is in conflict or tombstoned.
/// <para>
/// This is a SIDECAR rather than fields on <see cref="WorkItem"/>, and that is a decision worth keeping.
/// The work item is the canonical artifact and every one of its properties is classified in
/// <c>FieldOwnership</c>; putting "when did we last hear from the provider" there would make a provider
/// concern part of the model the local board also uses, where it would be permanently null and permanently
/// meaningless. Keyed beside the item, the local board stays a local board and the mirroring layer
/// composes over it.
/// </para>
/// </summary>
/// <param name="Item">The item these facts are about.</param>
/// <param name="Connection">The provider connection it is mirrored through.</param>
/// <param name="Key">The provider's own key for it, which is what makes a repeated write idempotent.</param>
/// <param name="LastConfirmedUtc">When the provider last told us about this item, or null if it never has.</param>
/// <param name="ProviderVersion">The provider's own version token (etag, updated stamp) kept verbatim.</param>
/// <param name="LocalVersionAtSync">Our own version at the moment of the last reconciliation.</param>
/// <param name="PendingWrites">Outbox entries for this item that the provider has not accepted yet.</param>
/// <param name="StuckWrites">Outbox entries for this item that will not go without a person.</param>
/// <param name="ConflictCount">Fields currently in conflict and waiting for a person.</param>
/// <param name="TombstonedUtc">When the provider was found to no longer have this item.</param>
/// <param name="TombstoneReason">What was observed, in words, so the tombstone explains itself later.</param>
public sealed record MirrorState(
    WorkItemId Item,
    string Connection,
    string Key,
    DateTimeOffset? LastConfirmedUtc,
    string? ProviderVersion,
    long LocalVersionAtSync,
    int PendingWrites,
    int StuckWrites,
    int ConflictCount,
    DateTimeOffset? TombstonedUtc,
    string? TombstoneReason)
{
    /// <summary>True once the provider has been found to no longer hold this item.</summary>
    public bool IsTombstoned => TombstonedUtc is not null;
}

/// <summary>
/// What to say about an item's freshness: the level, the age behind it, and one sentence a reader can act
/// on. Computed at read time from <see cref="MirrorState"/> rather than stored, because a stored level
/// would need something to wake up and re-stamp every row as time passed, and an indicator that is only
/// correct while a background job is healthy is worse than no indicator at all.
/// </summary>
/// <param name="Level">Which of the mutually exclusive things is true, most important first.</param>
/// <param name="Age">How long since the provider last confirmed the item, when that is known.</param>
/// <param name="Sentence">The indicator in words, written for a person rather than for a log.</param>
public sealed record Freshness(MirrorLevel Level, TimeSpan? Age, string Sentence)
{
    /// <summary>True when the reader should be shown a caveat alongside the item.</summary>
    public bool NeedsCaveat => Level is not (MirrorLevel.LocalOnly or MirrorLevel.Fresh);
}

/// <summary>Turns mirroring facts into the indicator a board and an agent both read.</summary>
public static class MirrorFreshness
{
    /// <summary>
    /// How long a confirmation stays fresh before the indicator says so. Ten minutes is not a tuned number
    /// and is not meant to be one: it is short enough that a board left open across a meeting stops
    /// claiming currency, and long enough that ordinary use does not carry a caveat on every row.
    /// </summary>
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromMinutes(10);

    /// <summary>The indicator for an item with no provider behind it at all.</summary>
    public static Freshness Local { get; } = new(
        MirrorLevel.LocalOnly,
        null,
        "This board is the source of truth; there is no provider behind it.");

    /// <summary>
    /// Describes the state as of <paramref name="now"/>. A null state means no provider, which is the local
    /// board's ordinary case and not a degraded one.
    /// </summary>
    public static Freshness Describe(MirrorState? state, DateTimeOffset now, TimeSpan? staleAfter = null)
    {
        if (state is null)
        {
            return Local;
        }

        var age = state.LastConfirmedUtc is { } confirmed ? now - confirmed : (TimeSpan?)null;

        // Never negative: a clock that moved backwards would otherwise produce an age that reads as the
        // future, and an indicator nobody can explain is an indicator nobody trusts.
        if (age is { } measured && measured < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (state.IsTombstoned)
        {
            var why = state.TombstoneReason is { Length: > 0 } reason ? reason : "No reason was recorded.";
            return new Freshness(
                MirrorLevel.Tombstoned,
                age,
                $"{state.Connection} no longer has {state.Key}. This copy is kept with its history. {why}");
        }

        if (state.StuckWrites > 0)
        {
            return new Freshness(
                MirrorLevel.WriteStuck,
                age,
                state.StuckWrites == 1
                    ? $"A change to this item did not reach {state.Connection} and will not retry on its own."
                    : $"{state.StuckWrites} changes to this item did not reach {state.Connection} and will not retry on their own.");
        }

        if (state.ConflictCount > 0)
        {
            return new Freshness(
                MirrorLevel.Conflicted,
                age,
                state.ConflictCount == 1
                    ? $"One field changed both here and on {state.Connection}. Choose which one holds."
                    : $"{state.ConflictCount} fields changed both here and on {state.Connection}. Choose which ones hold.");
        }

        if (state.PendingWrites > 0)
        {
            return new Freshness(
                MirrorLevel.Queued,
                age,
                state.PendingWrites == 1
                    ? $"One change is waiting to reach {state.Connection}."
                    : $"{state.PendingWrites} changes are waiting to reach {state.Connection}.");
        }

        if (age is not { } since)
        {
            return new Freshness(
                MirrorLevel.NeverConfirmed,
                null,
                $"{state.Connection} has not confirmed {state.Key} yet.");
        }

        var window = staleAfter ?? DefaultStaleAfter;
        return since <= window
            ? new Freshness(MirrorLevel.Fresh, since, $"Confirmed by {state.Connection} {Spoken(since)}.")
            : new Freshness(MirrorLevel.Stale, since, $"Last confirmed by {state.Connection} {Spoken(since)}.");
    }

    /// <summary>An age as a person would say it, coarse on purpose: nobody acts on seconds here.</summary>
    private static string Spoken(TimeSpan age) => age switch
    {
        { TotalSeconds: < 90 } => "just now",
        { TotalMinutes: < 60 } => $"{(int)age.TotalMinutes} minutes ago",
        { TotalHours: < 24 } => (int)age.TotalHours == 1 ? "an hour ago" : $"{(int)age.TotalHours} hours ago",
        _ => (int)age.TotalDays == 1 ? "a day ago" : $"{(int)age.TotalDays} days ago",
    };
}
