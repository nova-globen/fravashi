using System.Globalization;

namespace Fravashi.Domain.Autopilot;

/// <summary>
/// Groups a workspace with every worktree made from it (ADR 0031 decision 16's host addendum, #307): "per
/// workspace" now means "per repository", because #306 registers each concurrent session's worktree as its
/// OWN workspace id, in a sibling folder named <c>&lt;repo&gt;.fravashi-worktrees/&lt;item&gt;</c>. Pure, over a
/// path string, so it needs no bridge round-trip: a worktree's own RootPath already carries everything needed
/// to find its repository's. This is only ever ASKED of a RootPath a caller already has; the caller (the real
/// <c>AutopilotConcurrencyGate</c>) is the one that depends on the studio's workspace store already holding a
/// record for the workspace being asked about, which is not yet true for a worktree until whoever starts one
/// (#308) registers it.
/// </summary>
public static class AutopilotRepositoryIdentity
{
    /// <summary>
    /// Mirrors <c>BridgeWorktreeService.ContainerSuffix</c> by value. The two projects share no reference
    /// (<c>Fravashi.Bridge</c> is a Windows-only library hosted by fravashi.exe; <c>Fravashi.Domain</c> ships inside
    /// the Linux container), so nothing but a test holds them equal: a comment alone would let a rename on
    /// either side silently stop every worktree from being grouped with its repository, with no other test
    /// going red. <c>AutopilotRepositoryIdentityPinTests</c> (Infrastructure.Tests, shaped after
    /// <c>AgentCliPinTests</c>' own habit of reading a sibling project's real source) reads
    /// <c>BridgeWorktreeService.cs</c> from the working tree and fails the build if its literal ever disagrees
    /// with this one.
    /// </summary>
    public const string WorktreeContainerSuffix = ".fravashi-worktrees";

    /// <summary>
    /// A host path in the comparable form every method here uses: forward slashes, no trailing slash. Exposed
    /// so a caller that already has a path can compare it against <see cref="For"/>'s own answer the same way
    /// <see cref="For"/> does internally, rather than inventing a second normalization rule (backlog 312).
    /// </summary>
    public static string Normalize(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        return rootPath.Replace('\\', '/').TrimEnd('/');
    }

    /// <summary>
    /// The repository a workspace's RootPath belongs to: for an ordinary workspace, its own RootPath; for a
    /// worktree (<c>&lt;parent&gt;/&lt;repo&gt;.fravashi-worktrees/&lt;item&gt;</c>), the registered repository's
    /// own RootPath (<c>&lt;parent&gt;/&lt;repo&gt;</c>), derived without ever asking the bridge. Compared
    /// case-insensitively by every caller: these are Windows host paths.
    /// </summary>
    public static string For(string rootPath)
    {
        var normalized = Normalize(rootPath);
        var containerDir = GetParent(normalized);
        if (containerDir is not { Length: > 0 })
        {
            return normalized;
        }

        var containerName = GetName(containerDir);

        // The bridge release: a worktree made before the internal rename sits under the former suffix, and is still its
        // repository's (FormerNames; the cleanup release drops it).
        var suffix = new[] { WorktreeContainerSuffix, FormerNames.WorktreeContainerSuffix }
            .FirstOrDefault(s => containerName.EndsWith(s, StringComparison.Ordinal) && containerName.Length > s.Length);
        if (suffix is null)
        {
            return normalized;
        }

        var repoName = containerName[..^suffix.Length];
        var grandparent = GetParent(containerDir);
        return grandparent is { Length: > 0 }
            ? grandparent + "/" + repoName
            : normalized;
    }

    /// <summary>
    /// Whether a RootPath names a worktree sibling rather than a repository's own folder, i.e. whether
    /// <see cref="For"/> answers something other than the path itself (backlog 312: the same test a worktree's
    /// trust resolution uses to tell its own folder apart from its repository's, so it leaves an ordinary
    /// workspace's trust lookup untouched).
    /// </summary>
    public static bool IsWorktree(string rootPath) =>
        !string.Equals(For(rootPath), Normalize(rootPath), StringComparison.OrdinalIgnoreCase);

    private static string? GetParent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash > 0 ? path[..slash] : null;
    }

    private static string GetName(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }
}

/// <summary>Which limit a concurrency refusal is about (ADR 0031 decision 16).</summary>
public enum AutopilotConcurrencyRefusalKind
{
    /// <summary>As many sessions as the cap allows are already running against this repository.</summary>
    Cap,

    /// <summary>Free memory is below the floor needed to start another session.</summary>
    Memory,
}

/// <summary>
/// Why the autopilot's next start was refused (ADR 0031 decision 16), carrying the figures rather than a
/// pre-baked sentence so a test and the sentence agree on what was actually measured.
/// </summary>
/// <param name="Source">
/// Which memory reading answered (#308 host note point 5): <c>"cgroup"</c> when the container's own
/// <c>/sys/fs/cgroup/memory.max</c>/<c>memory.current</c> figures were read fresh, <c>"estimate"</c> for the
/// GC-sizing fallback (#307), or null for a <see cref="AutopilotConcurrencyRefusalKind.Cap"/> refusal, which
/// never reads memory at all.
/// </param>
public sealed record AutopilotConcurrencyRefusal(
    AutopilotConcurrencyRefusalKind Kind, int Running, int Cap, long? AvailableBytes, long FloorBytes,
    string? Source = null)
{
    /// <summary>What a person reads: which limit fired, and the figures behind it.</summary>
    public string Message => Kind == AutopilotConcurrencyRefusalKind.Cap
        ? $"The concurrency cap is reached: {Running.ToString(CultureInfo.InvariantCulture)} of "
            + $"{Cap.ToString(CultureInfo.InvariantCulture)} session(s) are already running against this "
            + "repository. Stop one of them, or raise the cap in Settings > Agents > Autopilot concurrency."
        : $"Estimated free memory ({Source ?? "estimate"}) is below the floor needed to start another "
            + $"session: {Megabytes(AvailableBytes)} MB free, {Megabytes(FloorBytes)} MB required.";

    private static string Megabytes(long? bytes) =>
        bytes is { } value
            ? (value / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)
            : "unknown";
}

/// <summary>
/// Whether the autopilot may start another session against a repository right now (ADR 0031 decision 16).
/// Pure: the caller has already counted the running sessions and read the available memory; this only judges
/// what was read. The cap is checked first (an exact count against an exact cap is more informative than a
/// free-memory figure that can fluctuate from one read to the next).
/// </summary>
public static class AutopilotConcurrencyGuard
{
    public static AutopilotConcurrencyRefusal? Refuse(
        int running, int cap, long? availableBytes, long floorBytes, string? source = null)
    {
        var effectiveCap = cap > 0 ? cap : AutopilotConcurrency.DefaultCap;

        if (running >= effectiveCap)
        {
            return new AutopilotConcurrencyRefusal(
                AutopilotConcurrencyRefusalKind.Cap, running, effectiveCap, availableBytes, floorBytes);
        }

        if (availableBytes is { } bytes && bytes < floorBytes)
        {
            return new AutopilotConcurrencyRefusal(
                AutopilotConcurrencyRefusalKind.Memory, running, effectiveCap, availableBytes, floorBytes, source);
        }

        return null;
    }
}

/// <summary>The concurrency cap's shipped defaults and the single place a Settings cap and a dispatch override combine.</summary>
public static class AutopilotConcurrency
{
    /// <summary>The Settings cap's own shipped default (ADR 0031 decision 16).</summary>
    public const int DefaultCap = 2;

    /// <summary>
    /// The floor this slice checks free memory against: one GiB. #57's planning note of 2026-09-26 puts each
    /// agent CLI process at "a few hundred megabytes" against the container's 8 GB limit
    /// (<c>ContainerHardening.MemoryLimit</c>); that figure is stated there, not measured. One GiB free leaves
    /// headroom for one more such process plus the studio's own container and the OS, a host decision
    /// recorded here rather than only in a comment, the same way <c>AutopilotLimits.Default</c>'s own figures
    /// are a host decision recorded in plan @114.
    /// </summary>
    public const long MemoryFloorBytes = 1024L * 1024 * 1024;

    /// <summary>
    /// The cap a dispatch actually enforces: the Settings cap, or the dispatch's own override when it is a
    /// positive number strictly LOWER than it. A dispatch may only lower the cap, never raise it, so an
    /// override that is not positive, or that is not below the Settings cap, is ignored outright rather than
    /// clamped to some other value: this is what keeps even a persisted run whose Settings cap was lowered
    /// after it was dispatched from exceeding the CURRENT Settings cap.
    /// </summary>
    public static int Effective(int settingsCap, int? dispatchOverride) =>
        dispatchOverride is { } o && o > 0 && o < settingsCap ? o : settingsCap;
}
