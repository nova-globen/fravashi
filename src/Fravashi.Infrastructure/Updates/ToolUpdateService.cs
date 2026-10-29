using System.Text.Json;
using System.Text.Json.Serialization;
using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.Notifications;
using Fravashi.Application.Persistence;
using Fravashi.Application.Updates;
using Fravashi.Domain.AgentSessions;
using Microsoft.Extensions.Logging;
using Fravashi.Domain;

namespace Fravashi.Infrastructure.Updates;

/// <summary>
/// Backs the dashboard's tool-update card (Type A / F1, ADR 0006). It reads each embedded CLI's reported
/// version (<c>&lt;cli&gt; --version</c> via <see cref="IAgentRuntimeService"/>) and the latest published
/// version (<see cref="IPackageRegistry"/>), and reports two independent facts against the product's PIN
/// (backlog 61 / ADR 0023): a newer release exists (informational, never installed here) and the installed CLI
/// has drifted off the pin (a repair the user can run). It <b>caches the result in the settings table</b> so
/// the card renders instantly and offline, notifies when a newer release appears, and streams the repair's
/// progress to the card. These never lock the app (D03-2). A singleton: the cache and the live progress are
/// shared across every circuit.
/// </summary>
public sealed class ToolUpdateService : IToolUpdateService
{
    /// <summary>
    /// Settings-table key holding the cached row JSON (the settings table, per T5). Suffixed <c>:v2</c> when
    /// the record gained its pin fields, so a cache written by an older release is ignored rather than
    /// half-deserialized into a row with no pinned version.
    /// </summary>
    public const string CacheKey = "updates:tools:v2";

    /// <summary>Settings-table key holding the UTC timestamp of the last availability check.</summary>
    public const string CheckedAtKey = "updates:checked-at";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IPackageRegistry _registry;
    private readonly IAgentRuntimeService _runtimes;
    private readonly IConfigurationStore _config;
    private readonly INotificationHistory _notifications;
    private readonly ILogger<ToolUpdateService> _logger;

    private readonly object _gate = new();
    private readonly Dictionary<AgentTool, ToolUpdateProgress> _progress = [];
    private IReadOnlyList<ToolUpdateInfo> _available = [];
    private DateTimeOffset? _lastChecked;
    private bool _loaded;
    private bool _isChecking;

    public ToolUpdateService(
        IPackageRegistry registry,
        IAgentRuntimeService runtimes,
        IConfigurationStore config,
        INotificationHistory notifications,
        ILogger<ToolUpdateService> logger)
    {
        _registry = registry;
        _runtimes = runtimes;
        _config = config;
        _notifications = notifications;
        _logger = logger;
    }

    public event Action? Changed;

    public DateTimeOffset? LastCheckedUtc
    {
        get
        {
            EnsureLoaded();
            return _lastChecked;
        }
    }

    public bool IsChecking
    {
        get { lock (_gate) { return _isChecking; } }
    }

    public IReadOnlyList<ToolUpdateInfo> Available
    {
        get
        {
            EnsureLoaded();
            lock (_gate)
            {
                return _available;
            }
        }
    }

    public ToolUpdateProgress? ProgressFor(AgentTool tool)
    {
        lock (_gate)
        {
            return _progress.TryGetValue(tool, out var p) ? p : null;
        }
    }

    public async Task<IReadOnlyList<ToolUpdateInfo>> CheckNowAsync(CancellationToken cancellationToken = default)
    {
        EnsureLoaded();
        lock (_gate)
        {
            _isChecking = true;
        }

        Raise();

        var previous = Available.Where(i => i.NewerPublished).Select(i => i.Tool).ToHashSet();
        var results = new List<ToolUpdateInfo>();
        try
        {
            foreach (var descriptor in AgentRuntimeCatalog.All)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(await CheckOneAsync(descriptor, cancellationToken));
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate) { _isChecking = false; }
            Raise();
            throw;
        }

        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            _available = results;
            _lastChecked = now;
            _isChecking = false;
        }

        Persist(results, now);
        Raise();

        NotifyNewlyAvailable(results, previous);
        return results;
    }

    public async Task UpdateAsync(AgentTool tool, CancellationToken cancellationToken = default)
    {
        var descriptor = AgentRuntimeCatalog.For(tool);
        var log = new List<string> { $"Installing the pinned version of {descriptor.DisplayName}…" };
        SetProgress(tool, ToolUpdatePhase.Running, log);

        log.Add($"Running npm install -g {descriptor.PinnedSpecifier}");
        SetProgress(tool, ToolUpdatePhase.Running, log);

        try
        {
            var result = await _runtimes.InstallOrUpdateAsync(tool, cancellationToken);
            if (result.Outcome == AgentRuntimeUpdateOutcome.Succeeded)
            {
                log.Add(VersionChangeLine(descriptor.DisplayName, result.FromVersion, result.ToVersion));
                SetProgress(tool, ToolUpdatePhase.Succeeded, log);
                _notifications.Add(
                    $"{descriptor.DisplayName} is at the pinned version {result.ToVersion ?? descriptor.PinnedVersion}.",
                    NotificationSeverity.Success);
            }
            else
            {
                var reason = result.Message ?? "The update did not succeed.";
                log.Add($"Update failed: {reason}");
                SetProgress(tool, ToolUpdatePhase.Failed, log);
                _notifications.Add($"{descriptor.DisplayName} update failed: {reason}", NotificationSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            // An update never throws at the card: record the real error and move on.
            _logger.LogWarning(ex, "Tool update for {Tool} threw.", tool);
            log.Add($"Update failed: {ex.Message}");
            SetProgress(tool, ToolUpdatePhase.Failed, log);
            _notifications.Add($"{descriptor.DisplayName} update failed: {ex.Message}", NotificationSeverity.Error);
        }

        // Refresh this tool's availability so the card reflects the new installed version.
        try
        {
            var refreshed = await CheckOneAsync(descriptor, cancellationToken);
            ReplaceOne(refreshed);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Post-update availability refresh for {Tool} failed.", tool);
        }
    }

    private async Task<ToolUpdateInfo> CheckOneAsync(AgentRuntimeDescriptor descriptor, CancellationToken cancellationToken)
    {
        var status = await _runtimes.GetStatusAsync(descriptor.Tool, cancellationToken);
        var installed = status.Installed ? status.Version : null;
        var latest = await _registry.GetLatestVersionAsync(descriptor.NpmPackage, cancellationToken);

        // The registry is compared against the PIN, not against whatever happens to be installed: the
        // question this answers is "has the vendor published past the version we pin", which is exactly
        // the fact a human needs in order to decide to promote (ADR 0023). Not "past what we verified":
        // those are separate recorded facts, and a runtime may be pinned with nothing verified at all.
        var newerPublished = SemVer.IsNewer(latest, descriptor.PinnedVersion);
        var offPin = descriptor.IsOffPin(installed);
        var note = BuildNote(status.Installed, installed, latest, descriptor.PinnedVersion, newerPublished, offPin);
        return new ToolUpdateInfo(
            descriptor.Tool, descriptor.DisplayName, descriptor.NpmPackage, descriptor.PinnedVersion,
            installed, latest, newerPublished, offPin, note);
    }

    private static string? BuildNote(
        bool installed, string? installedVersion, string? latest, string pinned, bool newerPublished, bool offPin)
    {
        if (!installed)
        {
            return "Not installed in this environment.";
        }

        if (installedVersion is null)
        {
            return "Installed, but its version could not be read.";
        }

        if (offPin)
        {
            return $"Installed {installedVersion}, but {ProductName.Short} pins {pinned}.";
        }

        if (latest is null)
        {
            return "At the pinned version. Could not reach the registry to check what has been published.";
        }

        return newerPublished
            ? $"At the pinned version {pinned}. {latest} has been published; promoting it is a product change, not a click."
            : "At the pinned version.";
    }

    private static string VersionChangeLine(string name, string? from, string? to)
    {
        if (!string.IsNullOrWhiteSpace(from) && !string.IsNullOrWhiteSpace(to) &&
            !string.Equals(from, to, StringComparison.Ordinal))
        {
            return $"Reinstalled {name}: {from} → {to}.";
        }

        return to is { Length: > 0 }
            ? $"{name} is at {to} (already the pinned version)."
            : $"{name} install completed.";
    }

    private void NotifyNewlyAvailable(IReadOnlyList<ToolUpdateInfo> results, HashSet<AgentTool> previouslyAvailable)
    {
        var newly = results.Where(i => i.NewerPublished && !previouslyAvailable.Contains(i.Tool)).ToList();
        if (newly.Count == 0)
        {
            return;
        }

        // Deliberately not "an update is available": nothing here will be installed. The product stays on
        // its pin until someone promotes the release through ADR 0023's procedure.
        var names = string.Join(", ", newly.Select(i => $"{i.DisplayName} ({i.PinnedVersion} → {i.LatestVersion})"));
        var what = newly.Count == 1 ? "A newer coding-agent CLI has" : $"{newly.Count} newer coding-agent CLIs have";
        _notifications.Add(
            $"{what} been published: {names}. {ProductName.Short} stays on its pinned version until the release is promoted.",
            NotificationSeverity.Info);
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

        // Read the cache outside the lock (a DB read), then adopt it under the lock.
        IReadOnlyList<ToolUpdateInfo> cached = [];
        DateTimeOffset? checkedAt = null;
        try
        {
            var json = _config.Get(CacheKey);
            if (!string.IsNullOrWhiteSpace(json))
            {
                cached = JsonSerializer.Deserialize<List<ToolUpdateInfo>>(json, Json) ?? [];
            }

            if (DateTimeOffset.TryParse(_config.Get(CheckedAtKey), out var parsed))
            {
                checkedAt = parsed;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the cached tool-update availability.");
            cached = [];
        }

        lock (_gate)
        {
            _available = cached;
            _lastChecked = checkedAt;
        }
    }

    private void Persist(IReadOnlyList<ToolUpdateInfo> results, DateTimeOffset now)
    {
        try
        {
            _config.Set(CacheKey, JsonSerializer.Serialize(results, Json));
            _config.Set(CheckedAtKey, now.ToString("O"));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not cache the tool-update availability.");
        }
    }

    private void SetProgress(AgentTool tool, ToolUpdatePhase phase, List<string> log)
    {
        lock (_gate)
        {
            _progress[tool] = new ToolUpdateProgress(tool, phase, [.. log], DateTimeOffset.UtcNow);
        }

        Raise();
    }

    private void ReplaceOne(ToolUpdateInfo refreshed)
    {
        lock (_gate)
        {
            var list = _available.ToList();
            var index = list.FindIndex(i => i.Tool == refreshed.Tool);
            if (index >= 0)
            {
                list[index] = refreshed;
            }
            else
            {
                list.Add(refreshed);
            }

            _available = list;
            Persist(_available, _lastChecked ?? DateTimeOffset.UtcNow);
        }

        Raise();
    }

    private void Raise() => Changed?.Invoke();
}
