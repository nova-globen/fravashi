using System.Collections.Concurrent;
using System.Globalization;
using Fravashi.Application.Autopilot;
using Fravashi.Application.Persistence;
using Fravashi.Domain.Autopilot;
using Fravashi.Infrastructure.Sessions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fravashi.Infrastructure.Autopilot;

/// <summary>
/// The concurrency cap as the settings table holds it (ADR 0031 decision 16, #307), shaped exactly after
/// <c>ConfigurationAgentSpendLimits</c>: read fresh on every call, so saving a lower cap in Settings &gt;
/// Environment &gt; Concurrency takes effect on the next start, not the next restart.
/// </summary>
public sealed class ConfigurationAutopilotConcurrencySettings(IConfiguration configuration) : IAutopilotConcurrencySettings
{
    public const string CapKey = "Autopilot:ConcurrencyCap";

    private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

    public int Cap() => Read(_configuration[CapKey]);

    /// <summary>
    /// A stored value as a cap, forgiving in one direction only: blank, unparseable or non-positive all read
    /// as the shipped default, never as "no cap" and never as a crash.
    /// </summary>
    public static int Read(string? stored) =>
        int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : AutopilotConcurrency.DefaultCap;

    /// <summary>The inverse of <see cref="Read"/>, for the Settings form.</summary>
    public static string Write(int cap) => cap.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Reads memory the way the .NET runtime already computes it for its own GC sizing
/// (<see cref="GC.GetGCMemoryInfo"/>), which is cgroup-aware under a container, so this needs no shell-out, no
/// new dependency and no new privilege. What was actually verified in this session: this container's own
/// cgroup memory limit, read directly (<c>cat /sys/fs/cgroup/memory.max</c>), answered exactly the 8 GiB
/// <c>ContainerHardening.MemoryLimit</c> sets. <see cref="GCMemoryInfo.TotalAvailableMemoryBytes"/> itself was
/// NOT captured and compared against that number: the GC configuration docs default the heap hard limit to 75%
/// of a container's memory limit when none is set explicitly, and the property then reports THAT hard limit,
/// not the raw cgroup figure, so <see cref="AvailableBytes"/>'s "available" figure is a heap-limit-scaled
/// estimate of free memory, not a measurement of free memory on the machine. <see cref="GCMemoryInfo.MemoryLoadBytes"/>
/// is how much of that was in use AS OF THE LAST GARBAGE COLLECTION, not right now, so the gate can also judge
/// against a reading that is stale on a studio that has been idle (no GC) for a while. Both are known limits of
/// this slice's memory check, not fixed here. A GC that has not run yet can leave
/// <c>TotalAvailableMemoryBytes</c> at 0, which <see cref="AvailableBytes"/> already reads as "cannot be read"
/// rather than as zero free memory.
/// </summary>
public sealed class RuntimeMemoryAvailability : IAutopilotMemoryAvailability
{
    public string Source => "estimate";

    public long? AvailableBytes()
    {
        var info = GC.GetGCMemoryInfo();
        return info.TotalAvailableMemoryBytes > 0
            ? Math.Max(0, info.TotalAvailableMemoryBytes - info.MemoryLoadBytes)
            : null;
    }
}

/// <summary>
/// Reads the container's own cgroup v2 memory figures fresh on every call (#308 host note point 5):
/// <c>/sys/fs/cgroup/memory.max</c> (the limit; a literal <c>"max"</c> means none is set, and there is then
/// nothing to subtract a usage figure from) and <c>/sys/fs/cgroup/memory.current</c> (usage right now, unlike
/// <see cref="RuntimeMemoryAvailability"/>'s own figure, which is only as fresh as the last garbage
/// collection). Falls back to the GC estimate (#307) when either file is missing, unreadable, or does not
/// parse: a diagnostic that cannot be read must not silently block every autopilot start, the same forgiving
/// direction #307's own fallback-to-"skip the check" already takes one level up. Whether a given host or
/// container actually has these two files, and what figures they answer, is unmeasured by this class itself
/// and recorded, if at all, wherever this was run rather than claimed here.
/// </summary>
public sealed class CgroupMemoryAvailability(
    IAutopilotMemoryAvailability? fallback = null, string? maxPath = null, string? currentPath = null)
    : IAutopilotMemoryAvailability
{
    private const string DefaultMaxPath = "/sys/fs/cgroup/memory.max";
    private const string DefaultCurrentPath = "/sys/fs/cgroup/memory.current";

    private readonly IAutopilotMemoryAvailability _fallback = fallback ?? new RuntimeMemoryAvailability();
    private readonly string _maxPath = maxPath ?? DefaultMaxPath;
    private readonly string _currentPath = currentPath ?? DefaultCurrentPath;

    // Written by AvailableBytes() and read right after by AutopilotConcurrencyGate.Refuse, on the same call:
    // a benign, cosmetic race under concurrent load (two repositories checked at once) could label a refusal
    // with the other call's source, never the figure itself, which each call reads and compares atomically.
    private volatile string _lastSource = "estimate";

    public string Source => _lastSource;

    public long? AvailableBytes()
    {
        if (TryReadCgroup() is { } bytes)
        {
            _lastSource = "cgroup";
            return bytes;
        }

        _lastSource = "estimate";
        return _fallback.AvailableBytes();
    }

    private long? TryReadCgroup()
    {
        try
        {
            if (!File.Exists(_maxPath) || !File.Exists(_currentPath))
            {
                return null;
            }

            var maxText = File.ReadAllText(_maxPath).Trim();
            if (string.Equals(maxText, "max", StringComparison.Ordinal)
                || !long.TryParse(maxText, out var max))
            {
                return null;
            }

            var currentText = File.ReadAllText(_currentPath).Trim();
            if (!long.TryParse(currentText, out var current))
            {
                return null;
            }

            return Math.Max(0, max - current);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// The real <see cref="IAutopilotConcurrencyGate"/> (ADR 0031 decision 16's host addendum, #307): counts every
/// currently RUNNING session across the whole registry whose workspace resolves to the same repository
/// (<see cref="AutopilotRepositoryIdentity"/>) as the one about to start in, regardless of whether it is an
/// autopilot session or a person's own conversation (the cap bounds how many agent CLI processes run against
/// one repository's memory at once, and a person's own conversation is exactly such a process), then judges
/// that count and the free memory with <see cref="AutopilotConcurrencyGuard"/>.
/// <para>
/// Grouping a worktree with its repository depends on <see cref="IWorkspaceStore"/> already holding a record
/// of it (its RootPath is what <see cref="AutopilotRepositoryIdentity.For"/> reads): nothing studio-side
/// writes that record for a worktree today, since nothing studio-side starts one yet. Whoever starts a
/// worktree session (#308) must register it there (the welcome page's own folder-open path already calls
/// <c>Touch</c> for an ordinary workspace) before this gate can group it; until then an unregistered worktree
/// falls back to counting on its own id (<see cref="RepositoryKeyFor"/>), which this logs so the gap is
/// visible rather than silent.
/// </para>
/// </summary>
public sealed class AutopilotConcurrencyGate(
    LiveSessionRegistry registry,
    IWorkspaceStore workspaces,
    IAutopilotConcurrencySettings settings,
    IAutopilotMemoryAvailability memory,
    ILogger<AutopilotConcurrencyGate>? logger = null) : IAutopilotConcurrencyGate
{
    private readonly ILogger _logger = logger ?? NullLogger<AutopilotConcurrencyGate>.Instance;

    // Keyed per repository, the same shape AutopilotEngine's own per-run lock uses, grown and never pruned:
    // a repository gets checked and started rarely enough next to how many could ever exist that the
    // entries this leaves behind for the life of the process are not worth a cleanup pass.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _repositoryLocks = new(StringComparer.OrdinalIgnoreCase);

    public string? Refuse(string workspaceId, int? dispatchCapOverride)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var repository = RepositoryKeyFor(workspaceId);
        var running = registry.Sessions.Count(s => s.IsRunning
            && string.Equals(RepositoryKeyFor(s.WorkspaceId), repository, StringComparison.OrdinalIgnoreCase));
        var cap = AutopilotConcurrency.Effective(settings.Cap(), dispatchCapOverride);

        long? available;
        string? source = null;
        try
        {
            available = memory.AvailableBytes();
            source = memory.Source;
        }
        catch (Exception ex)
        {
            // A diagnostic that cannot be read must not silently block every autopilot start forever: the cap
            // check alone still runs, exactly as a null reading already does below.
            _logger.LogWarning(ex, "Could not read free memory for the autopilot's concurrency gate.");
            available = null;
        }

        return AutopilotConcurrencyGuard.Refuse(running, cap, available, AutopilotConcurrency.MemoryFloorBytes, source)?.Message;
    }

    /// <inheritdoc />
    public async Task<IDisposable> LockAsync(string workspaceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var repository = RepositoryKeyFor(workspaceId);
        var semaphore = _repositoryLocks.GetOrAdd(repository, static _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (!_released)
            {
                _released = true;
                semaphore.Release();
            }
        }
    }

    private string RepositoryKeyFor(string workspaceId)
    {
        if (workspaces.Get(workspaceId)?.RootPath is { Length: > 0 } root)
        {
            return AutopilotRepositoryIdentity.For(root);
        }

        // No record to read a RootPath from, so this counts on its own id: a repository's worktree grouping
        // (the whole point of this gate, per its own remarks) silently stops holding for this workspace.
        // Logged so a studio that starts worktree sessions without registering them first (#308's own job)
        // shows this in its logs rather than only in a cap that quietly failed to bound anything.
        _logger.LogWarning(
            "Workspace {WorkspaceId} has no record in the workspace store, so the concurrency gate counts it "
            + "on its own id rather than grouping it with its repository.", workspaceId);
        return workspaceId;
    }
}
