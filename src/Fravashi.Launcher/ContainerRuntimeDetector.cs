namespace Fravashi.Launcher;

/// <summary>Canonical runtime names, shared by the runtimes, the detector, and <c>launcher.json</c>.</summary>
public static class RuntimeNames
{
    public const string Docker = "docker";
    public const string Podman = "podman";
    public const string Wslc = "wslc";
}

/// <summary>
/// Picks the container runtime the launcher will drive (ADR 0009). The product is runtime-agnostic: it runs
/// on whatever engine is installed rather than hard-coding Docker.
/// <para>
/// Precedence: an explicit <c>runtime</c> in <c>launcher.json</c> always wins (so a user can pin one). With
/// no explicit choice, the first <em>available</em> runtime is used, probed in the order <b>docker → podman →
/// wslc</b>. Docker and Podman come first because they support the full container-hardening set
/// (<c>--cap-drop</c>, <c>--read-only</c>, <c>--pids-limit</c>, <c>--restart</c>, …) that T7/T8/T9 depend on;
/// <c>wslc</c> is the WSL-native fallback used when it is the only runtime present - the common case on a
/// Windows host without Docker Desktop.
/// </para>
/// </summary>
public static class ContainerRuntimeDetector
{
    /// <summary>Detects the runtime using the real Docker/Podman/wslc candidates.</summary>
    public static IContainerRuntime Detect(LauncherConfig? config = null)
        => Detect(config, DefaultCandidates());

    /// <summary>
    /// Detects the runtime among the supplied <paramref name="candidates"/> (injected for unit tests). An
    /// explicit, recognised <c>runtime</c> name is honoured when its engine is installed, even if it is not
    /// answering, so the ensuing "runtime not available" message names the engine the user asked for; an
    /// unrecognised name falls back to auto-detection.
    /// <para>
    /// A pin to an engine that is NOT INSTALLED falls back to auto-detection too. That pin is stale, not a choice:
    /// on 2026-10-04 podman was removed from the dev host in favour of wslc, launcher.json still said
    /// <c>"runtime": "podman"</c>, and every start failed on an engine that no longer existed while the one that did
    /// was never tried.
    /// </para>
    /// <para>
    /// When nothing answers, an engine that is INSTALLED but not responding wins over the first candidate
    /// (item 6). On a fresh install with no <c>launcher.json</c>, Podman installed and its machine stopped,
    /// falling through to candidate one told the user to install Docker, which is both wrong and unactionable;
    /// returning the runtime that is actually on the machine is what lets the launcher try to start it and
    /// name it truthfully when it cannot. Only when nothing is even installed does the first candidate stand
    /// in, so the "install a runtime" path still has something to name.
    /// </para>
    /// </summary>
    public static IContainerRuntime Detect(LauncherConfig? config, IReadOnlyList<IContainerRuntime> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
        {
            throw new ArgumentException("At least one runtime candidate is required.", nameof(candidates));
        }

        if (config?.Runtime is { Length: > 0 } pinned)
        {
            var chosen = candidates.FirstOrDefault(c => string.Equals(c.Name, pinned, StringComparison.OrdinalIgnoreCase));
            if (chosen is not null && chosen.Probe() != RuntimeAvailability.CliMissing)
            {
                return chosen;
            }
            // Unknown name, or a pinned engine that is not installed: auto-detect below.
        }

        // Probe each candidate ONCE (every probe is a process start against a possibly wedged engine).
        var probed = candidates.Select(c => (Runtime: c, State: c.Probe())).ToList();
        return Pick(probed, RuntimeAvailability.Available)
            ?? Pick(probed, RuntimeAvailability.EngineNotResponding)
            ?? candidates[0];
    }

    private static IContainerRuntime? Pick(
        IReadOnlyList<(IContainerRuntime Runtime, RuntimeAvailability State)> probed,
        RuntimeAvailability state) =>
        probed.Where(p => p.State == state).Select(p => p.Runtime).FirstOrDefault();

    private static IReadOnlyList<IContainerRuntime> DefaultCandidates() =>
        [new DockerRuntime(), new PodmanRuntime(), new WslcRuntime()];
}
