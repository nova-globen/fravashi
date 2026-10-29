namespace Fravashi.Application.Wiki;

/// <summary>The phase of the initial wiki sync, driving the dashboard card's visual state.</summary>
public enum WikiSyncPhase
{
    /// <summary>Nothing attempted this process.</summary>
    Idle,

    /// <summary>A pull is in progress.</summary>
    Running,

    /// <summary>The local store is populated / up to date.</summary>
    Succeeded,

    /// <summary>The last pull failed; <see cref="WikiSyncState.Message"/> carries the reason.</summary>
    Failed,

    /// <summary>No online wiki (local-only, O10) or its read-sync is deferred - nothing pulled.</summary>
    Skipped,
}

/// <summary>A snapshot of the wiki sync's state, rendered by the Wiki dashboard card.</summary>
/// <param name="Phase">The current phase.</param>
/// <param name="Message">A human-readable status or error line.</param>
/// <param name="Fetched">Pages fetched from the site so far.</param>
/// <param name="Total">Total pages when known, else null.</param>
/// <param name="Synced">Pages written into the local store.</param>
/// <param name="Conflicts">Divergences detected and left for the user (site not overwritten).</param>
/// <param name="CurrentTitle">The page currently being processed, when known.</param>
/// <param name="Provider">The source provider label, when known.</param>
/// <param name="StoredPages">Total pages currently in the local store.</param>
/// <param name="LastRunUtc">When the sync last completed, or null.</param>
public sealed record WikiSyncState(
    WikiSyncPhase Phase,
    string Message,
    int Fetched,
    int? Total,
    int Synced,
    int Conflicts,
    string? CurrentTitle,
    string? Provider,
    int StoredPages,
    DateTimeOffset? LastRunUtc)
{
    public static WikiSyncState Empty { get; } =
        new(WikiSyncPhase.Idle, "No sync has run yet.", 0, null, 0, 0, null, null, 0, null);
}

/// <summary>
/// Runs the initial one-way sync that pulls the configured online wiki into the DB-backed local store (O9): the
/// site is authoritative, so a pull overwrites the local copy; a divergent local edit is flagged as a conflict
/// and left for the user (resolution UI deferred). No online wiki ⇒ the local store is the wiki and nothing is
/// pulled (O10). A singleton, so progress is shared across every circuit.
/// </summary>
public interface IWikiSyncService
{
    event Action? Changed;

    WikiSyncState State { get; }

    /// <summary>True when an online wiki that supports read-sync is configured.</summary>
    bool IsConfigured { get; }

    /// <summary>Starts the initial sync once if configured and not already done - the post-onboarding trigger.</summary>
    Task EnsureStartedAsync(CancellationToken cancellationToken = default);

    /// <summary>Pulls now, unconditionally - the card's manual "Sync now". Never throws at the UI.</summary>
    Task SyncNowAsync(CancellationToken cancellationToken = default);
}
