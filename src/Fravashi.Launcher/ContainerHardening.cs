using Fravashi.Domain;

namespace Fravashi.Launcher;

/// <summary>The container-hardening controls (F4 / ADR 0007) the launcher can apply to a <c>run</c> invocation.</summary>
public enum HardeningControlId
{
    /// <summary>Publish the app port on loopback only (<c>127.0.0.1</c>) - never all interfaces.</summary>
    LoopbackPublish,

    /// <summary>Run the container process as a non-root user (uid:gid 1000).</summary>
    NonRootUser,

    /// <summary>Cap the container's memory so a runaway agent cannot exhaust host RAM.</summary>
    MemoryLimit,

    /// <summary>Mount <c>/tmp</c> as a writable tmpfs (nosuid,nodev,noexec) so the rootfs need not be writable for it.</summary>
    TmpfsTmp,

    /// <summary>Drop all Linux capabilities (<c>--cap-drop ALL</c>).</summary>
    DropAllCapabilities,

    /// <summary>Forbid privilege escalation (<c>--security-opt no-new-privileges</c>).</summary>
    NoNewPrivileges,

    /// <summary>Read-only root filesystem, with only the explicit data / repo / tmp mounts writable.</summary>
    ReadOnlyRootfs,

    /// <summary>Hard cap on the number of processes (<c>--pids-limit</c>).</summary>
    PidsLimit,

    /// <summary>An <c>nproc</c> ulimit - the partial process-count cap used where <c>--pids-limit</c> is unavailable.</summary>
    NprocUlimit,

    /// <summary>
    /// The IMAGE carries no setuid/setgid binary and no file capability (ADR 0029, D4), so nothing the non-root process
    /// runs can raise its privileges. It needs no run flag, which is why every engine has it: it is wslc's stand-in for
    /// <c>--cap-drop</c> and <c>no-new-privileges</c>, and a second wall behind them on Docker and Podman.
    /// </summary>
    NoPrivilegeRaisingBinaries,
}

/// <summary>Whether a hardening control is actually applied to the running container, or unavailable on this runtime.</summary>
public enum HardeningStatus
{
    /// <summary>Applied - the flag is passed to the runtime and enforced.</summary>
    Active,

    /// <summary>Not applied - the runtime does not support the flag (never silently claimed as active).</summary>
    Unavailable,
}

/// <summary>
/// One hardening control's status for a given runtime, for the honest posture surface (assertion-gap rule): the
/// app must display which protections are genuinely <see cref="HardeningStatus.Active"/> versus
/// <see cref="HardeningStatus.Unavailable"/> on the detected runtime, never a blanket "hardened".
/// </summary>
/// <param name="Id">Stable control id (also serialized to the posture file).</param>
/// <param name="Title">Short human title.</param>
/// <param name="Description">What the control protects against.</param>
/// <param name="Status">Whether it is applied on this runtime.</param>
/// <param name="Detail">Why it is unavailable (runtime name + remedy) or the applied value, for display.</param>
public sealed record PostureControl(
    string Id,
    string Title,
    string Description,
    HardeningStatus Status,
    string? Detail);

/// <summary>
/// The applied hardening for one runtime: the ordered per-control statuses (for the posture surface) and the
/// <c>run</c>-argument fragments the supported controls contribute. This is the <b>single source of truth</b> the
/// per-runtime <c>BuildRunArgs</c> and the launcher-written posture file both consume, so what the app displays
/// can never diverge from what the launcher actually passed to the engine.
/// </summary>
public sealed class HardeningPlan
{
    internal HardeningPlan(string runtimeName, IReadOnlyList<PostureControl> controls, bool strongerRuntimeRecommended, string? recommendation)
    {
        RuntimeName = runtimeName;
        Controls = controls;
        StrongerRuntimeRecommended = strongerRuntimeRecommended;
        Recommendation = recommendation;
    }

    /// <summary>The runtime this plan was computed for.</summary>
    public string RuntimeName { get; }

    /// <summary>Every control's status on this runtime, in a stable display order.</summary>
    public IReadOnlyList<PostureControl> Controls { get; }

    /// <summary>True when a stronger runtime (Docker/Podman) would enforce protections this one cannot.</summary>
    public bool StrongerRuntimeRecommended { get; }

    /// <summary>An honest recommendation when the runtime is weaker, else null.</summary>
    public string? Recommendation { get; }

    /// <summary>
    /// Appends the <c>run</c>-argument fragments for every <see cref="HardeningStatus.Active"/> control to
    /// <paramref name="args"/>, using <paramref name="spec"/> for the publish mapping. Unavailable controls
    /// contribute nothing (their flags are never passed - that is what makes them unavailable, not silently
    /// dropped). Deterministic order so the arg list is testable.
    /// </summary>
    public void AppendArgs(List<string> args, ContainerRunSpec spec)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(spec);

        foreach (var control in Controls)
        {
            if (control.Status != HardeningStatus.Active)
            {
                continue;
            }

            args.AddRange(ContainerHardening.ArgsFor(ContainerHardening.ParseId(control.Id), spec));
        }
    }
}

/// <summary>
/// Encodes the per-runtime container-hardening matrix as <b>data</b> (ADR 0007 posture, ADR 0009 capability
/// matrix) rather than scattered <c>if (runtime == …)</c> branches. Docker and Podman support the full set;
/// <c>wslc</c> supports the subset R1 measured (<c>--cap-drop</c>, <c>--security-opt no-new-privileges</c>,
/// <c>--read-only</c> and <c>--pids-limit</c> are rejected by wslc, so they are marked Unavailable and their
/// flags are never emitted) plus an <c>nproc</c> ulimit as a partial process cap. The launcher applies the
/// maximum its runtime supports and records the honest result for the app to display.
/// </summary>
public static class ContainerHardening
{
    /// <summary>Non-root uid:gid the container runs as (matches the Dockerfile's baked user).</summary>
    public const string NonRootUser = "1000:1000";

    /// <summary>
    /// Memory ceiling - a runaway-containment guard, not a reservation (only bites if exceeded). The unit is
    /// UPPERCASE deliberately: wslc rejects a lowercase-unit value (<c>8g</c>) and accepts <c>8G</c>; Docker/Podman
    /// accept either, so uppercase is the portable form.
    /// </summary>
    public const string MemoryLimit = "8G";

    /// <summary>
    /// Hard process cap under Docker/Podman. It counts THREADS, and since backlog 151 the container is also the
    /// agents' build runner: measured in the official SDK image on four CPUs, building this solution peaked at 173
    /// and its test runs at 196, beside a studio that sits at about 31 and an agent CLI. 256 was the previous
    /// value and would have been hit; 512 was measured to hold with room on four CPUs. It did not hold on sixteen
    /// (2026-10-05, wslc on the dev host): MSBuild started fifteen worker nodes, the user stood at 348 threads with the
    /// studio and an agent CLI beside them, a thread burst stopped about 165 short of the cap, and the compilers that
    /// could not create threads HUNG rather than failed, so an agent's build sat still for twenty minutes. MSBuild
    /// starts a node per CPU, so the need grows with the machine: 4096 holds a 32-thread host's build several times
    /// over and still stops a fork bomb in well under a second.
    /// </summary>
    public const string PidsLimit = "4096";

    /// <summary>The user-defined network the studio runs on where the engine's default cannot be joined (backlog 270).</summary>
    public const string StudioNetwork = "fravashi-studio";

    /// <summary>
    /// The network the studio container runs on, or null for the engine's default (backlog 270). Rootless Podman's
    /// default is pasta, and Podman refuses to join a pasta container to any other network (exit 125, measured on the
    /// VM), so the app runner could never attach the studio to a run's edge network. A user-defined network can be
    /// joined, and a rootless container on one still reaches fravashi.exe's bridge and the internet (measured on the VM with
    /// a throwaway container; the studio itself is #225's live check after a release). Docker and rootful
    /// Podman keep their default bridge, where the join already works; a Podman that cannot say keeps it too, which is
    /// what it ran on before. wslc has no app runner. The VPN sidecar owning the network always wins, because its
    /// netns is the structural kill switch.
    /// </summary>
    public static string? StudioNetworkFor(string runtimeName, bool? runsRootless, bool vpnOwnsTheNetwork) =>
        runtimeName == RuntimeNames.Podman && runsRootless == true && !vpnOwnsTheNetwork ? StudioNetwork : null;

    /// <summary>Soft:hard <c>nproc</c> ulimit under wslc (the partial <c>--pids-limit</c> substitute; it also counts threads).</summary>
    public const string NprocUlimit = "4096:4096";

    // The full ordered control set. Each runtime's plan is this list, with the runtime's supported flags Active.
    private static readonly IReadOnlyList<(HardeningControlId Id, string Title, string Description)> Catalog =
    [
        (HardeningControlId.LoopbackPublish, "Loopback-only port",
            "The app port is published on 127.0.0.1 only, so nothing on the local network can reach it."),
        (HardeningControlId.NonRootUser, "Non-root user",
            "The container process runs as an unprivileged user (uid 1000), not root."),
        (HardeningControlId.DropAllCapabilities, "Dropped Linux capabilities",
            "All Linux kernel capabilities are dropped, so the process cannot perform privileged operations."),
        (HardeningControlId.NoNewPrivileges, "No privilege escalation",
            "no-new-privileges stops the process (or any child) from gaining more privileges than it started with."),
        (HardeningControlId.NoPrivilegeRaisingBinaries, "No setuid binaries",
            "The image carries no setuid or setgid binary and no file capability, so nothing the process runs can raise its privileges. Checked when the image is built."),
        (HardeningControlId.ReadOnlyRootfs, "Read-only root filesystem",
            "The container filesystem is read-only; only the data mount and /tmp are writable."),
        (HardeningControlId.PidsLimit, "Process-count cap",
            "A hard limit on the number of processes contains fork bombs and runaway agents."),
        (HardeningControlId.NprocUlimit, "Process ulimit",
            "An nproc ulimit caps the process count (a partial substitute where a hard pids limit is unavailable)."),
        (HardeningControlId.MemoryLimit, "Memory limit",
            "A memory ceiling stops a runaway process from exhausting host RAM."),
        (HardeningControlId.TmpfsTmp, "tmpfs /tmp",
            "/tmp is an in-memory tmpfs mounted nosuid,nodev,noexec - writable without a writable rootfs."),
    ];

    // Which controls each runtime supports. Docker/Podman: the full set except the nproc-ulimit substitute
    // (they have the stronger --pids-limit). wslc: the R1-measured subset + the nproc ulimit.
    private static readonly IReadOnlySet<HardeningControlId> DockerControls = new HashSet<HardeningControlId>
    {
        HardeningControlId.LoopbackPublish,
        HardeningControlId.NonRootUser,
        HardeningControlId.DropAllCapabilities,
        HardeningControlId.NoNewPrivileges,
        HardeningControlId.ReadOnlyRootfs,
        HardeningControlId.PidsLimit,
        HardeningControlId.MemoryLimit,
        HardeningControlId.TmpfsTmp,
    };

    // NoPrivilegeRaisingBinaries is in neither set: it is the image's, decided by its stamp in Plan.
    private static readonly IReadOnlySet<HardeningControlId> WslcControls = new HashSet<HardeningControlId>
    {
        HardeningControlId.LoopbackPublish,
        HardeningControlId.NonRootUser,
        HardeningControlId.NprocUlimit,
        HardeningControlId.MemoryLimit,
        HardeningControlId.TmpfsTmp,
    };

    /// <summary>Builds the hardening plan for a runtime by name (<c>docker</c>, <c>podman</c>, <c>wslc</c>).</summary>
    /// <param name="runtimeName">The engine.</param>
    /// <param name="imageStripped">
    /// Whether the image carries <see cref="ImageLabels.NoPrivilegeRaisingBinaries"/>. Only the posture needs it; the run
    /// arguments do not depend on it, because that control contributes no flag. False unless the image says so, so an
    /// older image is never credited with a protection it does not have.
    /// </param>
    public static HardeningPlan Plan(string runtimeName, bool imageStripped = false)
    {
        var supported = SupportedFor(runtimeName);
        var isWeaker = !SupportsFullSet(runtimeName);

        var controls = new List<PostureControl>(Catalog.Count);
        foreach (var (id, title, description) in Catalog)
        {
            // The nproc-ulimit control is only meaningful on runtimes that lack --pids-limit; on runtimes that
            // have the real pids limit it is neither active nor a "gap", so it is omitted from their posture.
            if (id == HardeningControlId.NprocUlimit && supported.Contains(HardeningControlId.PidsLimit))
            {
                continue;
            }

            if (id == HardeningControlId.NoPrivilegeRaisingBinaries)
            {
                // A property of the IMAGE, not the engine: credited only when the image's own stamp says so.
                controls.Add(new PostureControl(
                    id.ToString(),
                    title,
                    description,
                    imageStripped ? HardeningStatus.Active : HardeningStatus.Unavailable,
                    imageStripped
                        ? AppliedDetail(id)
                        : "Not confirmed for this image: it predates the build step that strips and checks them (v0.9.50)."));
                continue;
            }

            var active = supported.Contains(id);
            controls.Add(new PostureControl(
                id.ToString(),
                title,
                description,
                active ? HardeningStatus.Active : HardeningStatus.Unavailable,
                active ? AppliedDetail(id) : $"Not supported by the {runtimeName} runtime - use Docker or Podman for this protection."));
        }

        var recommendation = isWeaker
            ? $"The {runtimeName} runtime cannot enforce every protection (capability drop, no-new-privileges, " +
              $"read-only rootfs and a hard pids limit are unavailable). For the strongest isolation, run {ProductName.Short} " +
              "under Docker or Podman."
            : null;

        return new HardeningPlan(runtimeName, controls, isWeaker, recommendation);
    }

    /// <summary>The applied-value detail shown for an Active control.</summary>
    private static string? AppliedDetail(HardeningControlId id) => id switch
    {
        HardeningControlId.NonRootUser => $"uid {NonRootUser}",
        HardeningControlId.MemoryLimit => MemoryLimit,
        HardeningControlId.PidsLimit => PidsLimit,
        HardeningControlId.NprocUlimit => $"nproc={NprocUlimit}",
        HardeningControlId.LoopbackPublish => "127.0.0.1",
        HardeningControlId.NoPrivilegeRaisingBinaries => "provided by the image",
        _ => null,
    };

    /// <summary>The <c>run</c>-argument fragment a control contributes, or empty for a marker-only control.</summary>
    internal static IEnumerable<string> ArgsFor(HardeningControlId id, ContainerRunSpec spec) => id switch
    {
        // In VPN mode the studio joins the sidecar's network namespace instead of publishing its own port (a
        // container that shares another's netns cannot also publish - the port is published on the sidecar). The
        // loopback-publish protection still holds (the port IS published loopback-only, on the sidecar), so the
        // control stays Active and only its argument relocates to the netns join - the structural kill switch.
        HardeningControlId.LoopbackPublish when !string.IsNullOrWhiteSpace(spec.NetworkContainer) =>
            ["--network", $"container:{spec.NetworkContainer}"],
        // Backlog 270: on a network of its own (rootless Podman) the port is still published on loopback only.
        HardeningControlId.LoopbackPublish when !string.IsNullOrWhiteSpace(spec.Network) =>
            ["--network", spec.Network, "--publish", $"127.0.0.1:{spec.HostPort}:{spec.ContainerPort}"],
        HardeningControlId.LoopbackPublish => ["--publish", $"127.0.0.1:{spec.HostPort}:{spec.ContainerPort}"],
        HardeningControlId.NonRootUser => ["--user", NonRootUser],
        HardeningControlId.MemoryLimit => ["--memory", MemoryLimit],
        HardeningControlId.TmpfsTmp => ["--tmpfs", "/tmp"],
        HardeningControlId.DropAllCapabilities => ["--cap-drop", "ALL"],
        HardeningControlId.NoNewPrivileges => ["--security-opt", "no-new-privileges"],
        HardeningControlId.ReadOnlyRootfs => ["--read-only"],
        HardeningControlId.PidsLimit => ["--pids-limit", PidsLimit],
        HardeningControlId.NprocUlimit => ["--ulimit", $"nproc={NprocUlimit}"],
        _ => [],
    };

    internal static HardeningControlId ParseId(string id) => Enum.Parse<HardeningControlId>(id);

    private static IReadOnlySet<HardeningControlId> SupportedFor(string runtimeName) => runtimeName switch
    {
        RuntimeNames.Docker or RuntimeNames.Podman => DockerControls,
        _ => WslcControls,
    };

    private static bool SupportsFullSet(string runtimeName) =>
        runtimeName is RuntimeNames.Docker or RuntimeNames.Podman;
}
