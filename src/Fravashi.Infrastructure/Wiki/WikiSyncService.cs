using System.Security.Cryptography;
using System.Text;
using Fravashi.Application.Network;
using Fravashi.Application.Notifications;
using Fravashi.Application.Persistence;
using Fravashi.Application.Wiki;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fravashi.Infrastructure.Wiki;

/// <summary>
/// <see cref="IWikiSyncService"/> - the initial, one-way pull that populates the DB-backed local wiki store from
/// the configured online wiki (O9). The site is authoritative: each fetched page overwrites its local copy and
/// records the site's content hash. Drift is detectable via the two hashes on <see cref="WikiPage"/> - when a
/// local edit has diverged (<c>LocalHash != SourceHash</c>) <b>and</b> the site has also changed, the page is
/// flagged as a conflict and left for the user rather than silently overwritten (the resolution UI is a declared
/// follow-up). No online wiki ⇒ the local store is the wiki and nothing is pulled (O10). A singleton, so the
/// progress is shared across every circuit.
/// </summary>
public sealed class WikiSyncService : IWikiSyncService
{
    /// <summary>Settings key: UTC of the last successful sync (drives the auto-trigger and survives restarts).</summary>
    public const string LastSyncKey = "Wiki:LastSyncUtc";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWikiPageStore _store;
    private readonly IConfigurationStore _config;
    private readonly INotificationHistory _notifications;
    private readonly ILogger<WikiSyncService> _logger;
    private readonly ProviderOutages? _outages;

    private readonly object _gate = new();
    private WikiSyncState _state = WikiSyncState.Empty;
    private bool _loaded;
    private bool _running;

    public WikiSyncService(
        IServiceScopeFactory scopeFactory,
        IWikiPageStore store,
        IConfigurationStore config,
        INotificationHistory notifications,
        ILogger<WikiSyncService> logger,
        ProviderOutages? outages = null)
    {
        _outages = outages;
        _scopeFactory = scopeFactory;
        _store = store;
        _config = config;
        _notifications = notifications;
        _logger = logger;
    }

    public event Action? Changed;

    public WikiSyncState State
    {
        get
        {
            EnsureLoaded();
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public bool IsConfigured
    {
        get
        {
            using var scope = _scopeFactory.CreateScope();
            return scope.ServiceProvider.GetRequiredService<IWikiSourceResolver>().Resolve().Status
                == WikiSourceStatus.Ready;
        }
    }

    public async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        EnsureLoaded();
        if (!string.IsNullOrWhiteSpace(_config.Get(LastSyncKey)))
        {
            return; // Initial sync already done; re-sync is manual (incremental resync is a follow-up).
        }

        lock (_gate)
        {
            if (_running)
            {
                return;
            }
        }

        await SyncNowAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SyncNowAsync(CancellationToken cancellationToken = default)
    {
        EnsureLoaded(); // set _loaded before any SetState, so a later State read never clobbers the live state
        lock (_gate)
        {
            if (_running)
            {
                return;
            }

            _running = true;
        }

        try
        {
            await RunSyncAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Wiki sync threw.");
            var message = $"Sync failed: {ex.Message}";

            // Backlog 232: a network failure is said as the outage it is, in the words the board's sync and the wiki's
            // publishing use, and recorded with them when it provably never left.
            var failure = NetworkFailure.Classify(ex, cancellationToken);
            if (failure.IsNetwork && _state.Provider is { Length: > 0 } provider)
            {
                if (failure.NeverLeft)
                {
                    _outages?.Failed(provider, failure);
                }

                var words = _outages?.For(provider)?.Words ?? $"{provider} could not be reached ({failure.Words})";
                message = $"{words}. The local wiki is served as it was last pulled; Sync again once it answers.";
            }

            SetState(_state with
            {
                Phase = WikiSyncPhase.Failed,
                Message = message,
                LastRunUtc = DateTimeOffset.UtcNow,
            });
            _notifications.Add(failure.IsNetwork ? $"Wiki sync: {message}" : $"Wiki sync failed: {ex.Message}", NotificationSeverity.Error);
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
            }
        }
    }

    private async Task RunSyncAsync(CancellationToken cancellationToken)
    {
        // The resolver (and any scoped provider adapter it wraps, e.g. the Confluence reader) live in a scope
        // that must outlast the fetch, so the whole pull runs inside it.
        using var scope = _scopeFactory.CreateScope();
        var resolution = scope.ServiceProvider.GetRequiredService<IWikiSourceResolver>().Resolve();
        if (resolution.Status != WikiSourceStatus.Ready || resolution.Source is null)
        {
            SetState(new WikiSyncState(
                WikiSyncPhase.Skipped, resolution.Message, 0, null, 0, 0, null, null, _store.Count(), _state.LastRunUtc));
            return;
        }

        var source = resolution.Source;
        SetState(new WikiSyncState(
            WikiSyncPhase.Running, "Starting sync…", 0, null, 0, 0, null, source.Provider, _store.Count(), _state.LastRunUtc));

        var progress = new Progress<WikiFetchProgress>(p => OnFetchProgress(source.Provider, p));
        var pages = await source.FetchAllAsync(progress, cancellationToken).ConfigureAwait(false);

        var synced = 0;
        var conflicts = 0;
        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hash = Hash(page.Body);
            var existing = _store.GetBySlug(page.Space, page.Slug);
            var now = DateTimeOffset.UtcNow;

            var localDiverged = existing is not null && existing.LocalHash != existing.SourceHash;
            if (existing is not null && localDiverged && hash != existing.LocalHash)
            {
                // The local copy was edited away from what we last pulled AND the site now wants a different
                // value - a real conflict (O9). Never silently overwrite: keep the local body and flag it for
                // the user (resolution UI is a declared follow-up). We still record the new site ref for drift.
                _store.Upsert(existing with { HasConflict = true, SourceRef = page.SourceRef, UpdatedUtc = now });
                conflicts++;
                continue;
            }

            // Site is authoritative: overwrite local, resetting both hashes to the site's content.
            _store.Upsert(new WikiPage(
                Id: existing?.Id ?? Guid.NewGuid().ToString("n"),
                Space: page.Space,
                Title: page.Title,
                Slug: page.Slug,
                Body: page.Body,
                Format: page.Format,
                SourceRef: page.SourceRef,
                SourceHash: hash,
                LocalHash: hash,
                HasConflict: false,
                UpdatedUtc: now));
            synced++;
        }

        var completed = DateTimeOffset.UtcNow;
        _config.Set(LastSyncKey, completed.ToString("O"));
        _outages?.Answered(source.Provider);

        var conflictNote = conflicts > 0 ? $" {conflicts} conflict(s) left for review." : string.Empty;
        SetState(new WikiSyncState(
            WikiSyncPhase.Succeeded,
            $"Synced {synced} page(s) from {source.Provider}.{conflictNote}",
            pages.Count, pages.Count, synced, conflicts, null, source.Provider, _store.Count(), completed));
        _notifications.Add(
            $"Wiki synced: {synced} page(s) from {source.Provider}.{conflictNote}",
            conflicts > 0 ? NotificationSeverity.Warning : NotificationSeverity.Success);
    }

    private void OnFetchProgress(string provider, WikiFetchProgress p)
    {
        lock (_gate)
        {
            if (_state.Phase != WikiSyncPhase.Running)
            {
                return;
            }

            _state = _state with
            {
                Fetched = p.Fetched,
                Total = p.Total,
                CurrentTitle = p.CurrentTitle,
                Provider = provider,
                Message = p.Total is { } total
                    ? $"Fetching pages ({p.Fetched}/{total})…"
                    : $"Fetching pages ({p.Fetched})…",
            };
        }

        Raise();
    }

    private void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
        }

        DateTimeOffset? lastRun = DateTimeOffset.TryParse(_config.Get(LastSyncKey), out var parsed) ? parsed : null;
        var stored = _store.Count();

        lock (_gate)
        {
            _state = lastRun is not null
                ? new WikiSyncState(WikiSyncPhase.Succeeded, $"{stored} page(s) in the local wiki.", stored, stored, stored, 0, null, null, stored, lastRun)
                : WikiSyncState.Empty with { StoredPages = stored };
        }
    }

    private void SetState(WikiSyncState state)
    {
        lock (_gate)
        {
            _state = state;
        }

        Raise();
    }

    private void Raise() => Changed?.Invoke();

    private static string Hash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content ?? string.Empty));
        return Convert.ToHexString(bytes);
    }
}
