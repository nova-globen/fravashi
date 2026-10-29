using System.Text.Json;
using Fravashi.Domain;

namespace Fravashi.Launcher;

/// <summary>
/// Container runtime backed by a Docker-CLI-compatible engine. Docker and Podman share the same command
/// surface (<c>run</c>, <c>ps</c>, <c>image inspect</c>, <c>--restart</c>, …), so both are this one
/// implementation parameterised by the CLI name. This preserves the exact command shape the launcher shipped
/// with, including the BOUNDED <c>--restart on-failure:N</c> the studio container runs under
/// (see <see cref="LauncherDefaults.MaxContainerRestarts"/>).
/// </summary>
public class DockerCompatibleRuntime : CliContainerRuntime
{
    public DockerCompatibleRuntime(string name, string cli)
    {
        Name = name;
        Cli = cli;
    }

    public sealed override string Name { get; }

    protected sealed override string Cli { get; }

    protected override string[] AvailabilityProbeArgs => ["version", "--format", "{{.Server.Version}}"];

    public override bool ImageExists(string image)
        => Run("image", "inspect", image).ExitCode == 0;

    public override bool ContainerExists(string containerName)
    {
        var (exitCode, stdout, _) = Run("ps", "--all", "--filter", $"name=^/{containerName}$", "--format", "{{.Names}}");
        return exitCode == 0 && stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.Trim() == containerName);
    }

    public override bool ContainerRunning(string containerName)
    {
        var (exitCode, stdout, _) = Run("ps", "--filter", $"name=^/{containerName}$", "--format", "{{.Names}}");
        return exitCode == 0 && stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.Trim() == containerName);
    }

    public override void StartContainer(string containerName)
    {
        try
        {
            RunChecked("start", containerName);
        }
        catch (InvalidOperationException ex) when (EngineFailures.Explain(Cli, containerName, ex.Message) is { } said)
        {
            // Starting an existing container meets the same engine failures as creating one (backlog 252).
            throw new ExplainedEngineFailure(said, ex);
        }
    }

    public override void StopContainer(string containerName)
        => RunChecked("stop", containerName);

    // Force-remove; a missing container exits non-zero, which we ignore (removal is idempotent by intent).
    public override void RemoveContainer(string containerName)
        => Run("rm", "--force", containerName);

    // Deliberately NON-forced: the engine refuses while any container or other tag still references the
    // image, which is exactly the retire guard (never delete something still in use). The refusal, like a
    // missing image, is ignored - a skipped removal only costs disk.
    public override void RemoveImage(string imageRef)
        => Run("rmi", imageRef);

    /// <summary>
    /// What still names the image (backlog 165), from its <c>RepoTags</c>. Docker and podman print a JSON array, or
    /// <c>null</c> for none; anything unreadable, or a failed inspect, is "cannot say".
    /// </summary>
    public override IReadOnlyList<string> ImageNames(string imageRef)
    {
        var (exitCode, stdout, _) = Run("image", "inspect", imageRef, "--format", "{{json .RepoTags}}");
        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            return [];
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(stdout.Trim()) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    public override string? ImageVersion(string image)
    {
        var (exitCode, stdout, _) = Run(
            "image", "inspect", image, "--format", "{{.Id}} (created {{.Created}})");
        var line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return exitCode == 0 && !string.IsNullOrEmpty(line) ? line : null;
    }

    public override string? ResolveImageDigest(string image)
    {
        var (exitCode, stdout, _) = Run("image", "inspect", image, "--format", "{{.Id}}");
        var line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return exitCode == 0 && !string.IsNullOrWhiteSpace(line) ? line : null;
    }

    public override string? ImageLabel(string image, string label)
    {
        // The release version and variant (and the no-setuid stamp) are labels baked into the image (Dockerfile). Reading the
        // version lets `fravashi status` show the running image's version even before launcher.json records one (a fresh
        // install has version:null until installed-release.json is folded in). The labels are read as JSON - a
        // Go-template that indexed the label by a quoted key does not survive the Windows→engine boundary intact
        // (the embedded quotes get mangled crossing into the Podman machine), whereas {{json .Config.Labels}}
        // carries no quotes in the template, so it round-trips and is parsed here.
        var (exitCode, stdout, _) = Run(BuildImageLabelsArgs(image));
        return exitCode == 0 ? ReadLabel(stdout, label) : null;
    }

    /// <summary>The <c>image inspect ... {{json .Config.Labels}}</c> argument list. Exposed for unit tests (pure, no process started).</summary>
    public static string[] BuildImageLabelsArgs(string image) =>
        ["image", "inspect", image, "--format", "{{json .Config.Labels}}"];

    /// <summary>Reads one label from a <c>{{json .Config.Labels}}</c> answer; null when absent, empty or unreadable. Exposed for tests.</summary>
    public static string? ReadLabel(string labelsJson, string label)
    {
        try
        {
            using var document = JsonDocument.Parse(labelsJson.Trim());
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(label, out var found)
                && found.ValueKind == JsonValueKind.String)
            {
                var value = found.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        catch (JsonException)
        {
            // Not JSON (e.g. an error banner or a null-label image) - nothing to report.
        }

        return null;
    }

    public override string GetLogs(string containerName, int tail)
    {
        var (exitCode, stdout, stderr) = Run("logs", "--tail", tail.ToString(), containerName);
        // Docker writes container stderr to the process's stderr; surface both so the tail is complete.
        return exitCode == 0 ? string.Concat(stdout, stderr) : stderr.Trim();
    }

    public override string? ContainerImageId(string containerName)
    {
        var (exitCode, stdout, _) = Run("inspect", containerName, "--format", "{{.Image}}");
        var line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return exitCode == 0 && !string.IsNullOrWhiteSpace(line) ? line : null;
    }

    // No deadline: `run` pulls the image first when it is not local, which is a download, not a hang.
    public override void RunContainer(ContainerRunSpec spec)
    {
        try
        {
            RunCheckedUnbounded(BuildRunArgs(spec));
        }
        catch (InvalidOperationException ex) when (EngineFailures.Explain(Cli, spec.ContainerName, ex.Message) is { } said)
        {
            // A failure the launcher has met before and can explain (backlog 134). The engine's own words ride
            // along as the inner exception, so nothing is hidden from the log.
            throw new ExplainedEngineFailure(said, ex);
        }
    }

    /// <summary>Docker and Podman accept <c>--cap-add</c>/<c>--device</c>, so both can run the VPN sidecar (VPN1).</summary>
    public override bool SupportsVpnSidecar => true;

    public override void RunVpnSidecar(VpnSidecarRunSpec spec) => RunCheckedUnbounded(BuildVpnRunArgs(spec));

    /// <summary>The VPN sidecar <c>run</c> argument list - exposed for unit tests (pure, no process started).</summary>
    public string[] BuildVpnRunArgs(VpnSidecarRunSpec spec) => [.. ContainerRunArgs.BuildVpnSidecar(spec)];

    /// <summary>
    /// The <c>run</c> argument list. Exposed for unit tests so the per-runtime shape (notably the
    /// <c>--restart</c> that wslc cannot express) is verifiable without a real engine.
    /// </summary>
    /// <summary>
    /// Engine-specific run args for the bind-mounted <c>/data</c> volume's ownership. Empty by default -
    /// Docker's default userns already lets the hardened non-root (uid 1000) app write a bind mount, and wslc
    /// has no rootless remap. <see cref="PodmanRuntime"/> overrides it (rootless Podman needs a userns keep-id).
    /// </summary>
    protected virtual IEnumerable<string> BindMountUserNamespaceArgs(ContainerRunSpec spec) => [];

    /// <summary>
    /// Whether the agent-workspaces volume mount needs the Podman <c>:U</c> chown suffix so the
    /// non-root app (uid 1000) can write it. False for Docker (copy-up preserves uid-1000
    /// ownership); <see cref="PodmanRuntime"/> overrides it (rootless named volumes are created
    /// root-owned).
    /// </summary>
    protected virtual bool ChownWorkspacesVolumeToContainerUser => false;

    public string[] BuildRunArgs(ContainerRunSpec spec)
    {
        var args = new List<string> { "run", "--detach", "--name", spec.ContainerName };

        // F4 hardening (ADR 0007): Docker/Podman apply the FULL set - loopback-only publish, non-root user,
        // cap-drop ALL, no-new-privileges, read-only rootfs, a hard pids limit, a memory limit and tmpfs /tmp.
        // The loopback publish also lives here (closing the PortSelector gap: the free-port check is loopback,
        // so the publish must be too).
        ContainerHardening.Plan(Name).AppendArgs(args, spec);

        // Rootless engines (Podman) need a userns remap so the non-root app can write the bind-mounted /data.
        // Empty on Docker/wslc; Podman overrides it - see PodmanRuntime.
        args.AddRange(BindMountUserNamespaceArgs(spec));

        args.AddRange(["--restart", LauncherDefaults.RestartPolicy]);
        args.AddRange(["--volume", $"{spec.DataDir}:/data"]);
        args.AddRange(["--env", "FRAVASHI_DATA_DIR=/data"]);
        ContainerRunArgs.AppendWorkspacesVolume(args, spec, ChownWorkspacesVolumeToContainerUser);
        ContainerRunArgs.AppendIntegrityEnv(args, spec);
        ContainerRunArgs.AppendWindowToken(args, spec);
        ContainerRunArgs.AppendVpnAppEnv(args, spec);
        ContainerRunArgs.AppendBrokerEnv(args, spec);
        ContainerRunArgs.AppendBridgeEnv(args, spec);
        args.Add(spec.Image);
        return [.. args];
    }
}

/// <summary>The Docker runtime (<c>docker</c> CLI + daemon).</summary>
public sealed class DockerRuntime() : DockerCompatibleRuntime(RuntimeNames.Docker, "docker")
{
    /// <summary>
    /// Docker has no supported CLI command that starts its own engine: Docker Desktop is a desktop app and
    /// the Linux daemon is started by the service manager, neither of which is Fravashi's to drive. Saying so
    /// is the honest answer; guessing at an executable path or a service name is not.
    /// </summary>
    public override MachineStartResult TryStartMachine() => MachineStartResult.NotSupported(
        "Docker is installed but its engine is not answering, and Docker offers no CLI command to start it. " +
        $"Start Docker Desktop (or the docker service) yourself, then run `{ProductName.Command}` again.");
}

/// <summary>
/// The Podman runtime (<c>podman</c> CLI). Podman is a drop-in, Docker-compatible CLI - the same
/// <c>run</c>/<c>ps</c>/<c>image inspect</c>/<c>--restart</c> surface - so it reuses the Docker-compatible
/// implementation, swapping the executable name and adding the one rootless-specific run arg (below).
/// </summary>
public sealed class PodmanRuntime() : DockerCompatibleRuntime(RuntimeNames.Podman, "podman")
{
    /// <summary>
    /// Rootless Podman maps container uid 0 to the invoking host user and container uid 1000 to a high subuid,
    /// so the hardened non-root (uid 1000) app cannot, by default, write files on the bind-mounted <c>/data</c>
    /// that the host user owns - on Podman-on-Windows the SQLite DB then opens read-only and schema creation
    /// throws "attempt to write a readonly database", crash-looping the studio. <c>--userns=keep-id:uid=1000,
    /// gid=1000</c> maps the container's uid/gid 1000 straight to that host user, so the app owns (and can write)
    /// the DB + Data Protection keys on <c>/data</c>. It is also forward-safe: if a Podman machine's DrvFs has
    /// metadata enabled, app-written files land owned by the host user rather than an unmapped subuid, so they
    /// stay writable across restarts. Docker's default userns already grants this and wslc has no rootless remap,
    /// so the flag is Podman-only. (The pre-existing wslc-era files that carry baked root ownership metadata are
    /// handled separately, host-side, by <see cref="PodmanDataDirNormalizer"/> - keep-id cannot remap those.)
    /// </summary>
    protected override IEnumerable<string> BindMountUserNamespaceArgs(ContainerRunSpec spec) =>
        ["--userns", $"keep-id:uid={KeepIdUid},gid={KeepIdGid}"];

    /// <summary>
    /// Rootless Podman creates the named agent-workspaces volume owned by the container's root,
    /// so the hardened non-root app (uid 1000) could not write the synced copies. The <c>:U</c>
    /// mount suffix chowns the volume to the run user, consistent with the keep-id remap above
    /// (the exact fix the retired /repos volume proved out). Docker's copy-up already preserves
    /// uid-1000 ownership, so this is Podman-only.
    /// </summary>
    protected override bool ChownWorkspacesVolumeToContainerUser => true;

    /// <summary>
    /// Asks the connection the launcher's containers run under whether it is rootless (backlog 252). One bounded
    /// <c>podman info</c>; its template carries no quotes, which do not survive the trip into the machine (see
    /// <see cref="DockerCompatibleRuntime.ImageLabel"/>). Anything but a plain true or false is "cannot say".
    /// </summary>
    public override bool? RunsRootless()
    {
        try
        {
            var outcome = RunBounded(ProbeTimeout, BuildRootlessArgs());
            return outcome.TimedOut || outcome.ExitCode != 0 ? null : ReadRootless(outcome.StdOut);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The <c>info</c> argument list that asks for rootless mode. Exposed for tests (pure, no process started).</summary>
    public static string[] BuildRootlessArgs() => ["info", "--format", "{{.Host.Security.Rootless}}"];

    /// <summary>Reads <c>podman info</c>'s rootless answer; null for anything but true or false. Exposed for tests.</summary>
    public static bool? ReadRootless(string stdout) => stdout.Trim().ToLowerInvariant() switch
    {
        "true" => true,
        "false" => false,
        _ => null,
    };

    /// <summary>The container uid the app runs as (matches <see cref="ContainerHardening.NonRootUser"/>).</summary>
    private const int KeepIdUid = 1000;

    /// <summary>The container gid the app runs as (matches <see cref="ContainerHardening.NonRootUser"/>).</summary>
    private const int KeepIdGid = 1000;

    /// <summary>
    /// How long a machine start may take before its process tree is killed. A cold WSL/QEMU boot is tens of
    /// seconds; three minutes is well past that, so the deadline only fires on a genuinely stuck start.
    /// </summary>
    private static readonly TimeSpan MachineStartTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Starts the Podman machine, the one runtime where that is both meaningful and scriptable. This is the
    /// after-a-reboot case: the machine does not auto-start, so <c>podman version</c> fails and every launch
    /// used to end at "install a container runtime" for a runtime that was plainly installed.
    /// <para>
    /// It deliberately NEVER runs <c>podman machine init</c>: that downloads a multi-gigabyte image and picks
    /// CPU / memory / disk on the user's behalf, which is the owner's call to make, not the launcher's. With
    /// no machine present it says so and stops.
    /// </para>
    /// </summary>
    public override MachineStartResult TryStartMachine()
    {
        var listed = RunBounded(ProbeTimeout, "machine", "list", "--format", "json");
        if (listed.TimedOut)
        {
            return new MachineStartResult(
                MachineStartStatus.Failed,
                "Podman did not answer `podman machine list` within 20s, so its machine could not be started. " +
                $"The engine looks wedged: try `wsl --shutdown` (or a reboot), then run `{ProductName.Command}` again.");
        }

        if (listed.ExitCode != 0)
        {
            return new MachineStartResult(
                MachineStartStatus.Failed,
                $"Could not list the Podman machines, so none could be started: {Tail(listed.StdErr)}");
        }

        var machines = PodmanMachineList.Parse(listed.StdOut);
        if (machines.Count == 0)
        {
            return new MachineStartResult(
                MachineStartStatus.NoMachine,
                "Podman is installed but has no machine, so there is nothing to start. Create one yourself with " +
                "`podman machine init` (it downloads a multi-gigabyte image and picks CPU, memory and disk, so " +
                $"{ProductName.Short} will not do it for you), then run `{ProductName.Command}` again.");
        }

        if (PodmanMachineList.StartTarget(machines) is not { } machine)
        {
            return new MachineStartResult(
                MachineStartStatus.Failed,
                $"Podman has {machines.Count} machines and none is marked default, so {ProductName.Short} will not guess " +
                $"which one to start. Start the right one with `podman machine start <name>`, then run `{ProductName.Command}` again.");
        }

        if (machine.Running || machine.Starting)
        {
            return new MachineStartResult(
                MachineStartStatus.AlreadyRunning,
                $"The Podman machine '{machine.Name}' reports as {(machine.Running ? "running" : "starting")}, yet " +
                "Podman is not answering, so starting it again would not help. It is most likely wedged: run " +
                $"`podman machine stop {machine.Name}` then `podman machine start {machine.Name}` (or `wsl --shutdown`), " +
                $"and run `{ProductName.Command}` again.");
        }

        var started = RunBounded(MachineStartTimeout, "machine", "start", machine.Name);
        if (started.TimedOut)
        {
            return new MachineStartResult(
                MachineStartStatus.TimedOut,
                $"Starting the Podman machine '{machine.Name}' did not finish within 3 minutes, so it was " +
                "cancelled rather than left to hang the launch. Start it by hand with " +
                $"`podman machine start {machine.Name}` to see where it sticks.");
        }

        if (started.ExitCode != 0)
        {
            return new MachineStartResult(
                MachineStartStatus.Failed,
                $"Could not start the Podman machine '{machine.Name}': {Tail(started.StdErr)}");
        }

        return new MachineStartResult(
            MachineStartStatus.Started,
            $"Started the Podman machine '{machine.Name}'.");
    }

    /// <summary>The last useful line of a CLI error, so a message carries the cause without a wall of text.</summary>
    private static string Tail(string stderr)
    {
        var line = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .LastOrDefault(l => l.Length > 0);
        return string.IsNullOrEmpty(line) ? "the CLI reported no reason" : line;
    }
}
