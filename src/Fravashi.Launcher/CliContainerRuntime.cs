using System.Diagnostics;
using Fravashi.Domain;

namespace Fravashi.Launcher;

/// <summary>
/// Shared machinery for the CLI-shelling container runtimes: it composes an argument list, starts the
/// runtime's CLI process, and inspects exit codes / stdout. No orchestration logic lives here (that is
/// <see cref="LauncherApp"/>'s); subclasses only translate each operation into their CLI's own syntax.
/// </summary>
public abstract class CliContainerRuntime : IContainerRuntime
{
    /// <summary>User-facing runtime name (<c>wslc</c>, <c>docker</c>, <c>podman</c>).</summary>
    public abstract string Name { get; }

    /// <summary>The CLI executable this runtime shells out to.</summary>
    protected abstract string Cli { get; }

    /// <summary>The CLI call that proves the engine answers (each CLI spells its version verb differently).</summary>
    protected abstract string[] AvailabilityProbeArgs { get; }

    public bool IsAvailable() => Probe() == RuntimeAvailability.Available;

    public virtual RuntimeAvailability Probe() => ProbeAvailability(AvailabilityProbeArgs);

    /// <summary>
    /// No machine to start by default. Only Podman has one this launcher may drive, so
    /// <see cref="PodmanRuntime"/> overrides it and the others say honestly that there is nothing to start.
    /// </summary>
    public virtual MachineStartResult TryStartMachine() => MachineStartResult.NotSupported(
        $"The '{Name}' runtime has no machine {ProductName.Short} can start; start its engine yourself and run `{ProductName.Command}` again.");

    public abstract bool ImageExists(string image);
    // No deadline: a multi-hundred-megabyte tarball on a slow disk legitimately takes minutes. Docker, podman and
    // wslc all spell it `load --input`, and all print the names they loaded (backlog 190).
    public IReadOnlyList<string> LoadImage(string tarPath)
        => LoadedImages.Parse(RunCheckedUnbounded("load", "--input", tarPath));

    /// <summary>
    /// <c>tag source target</c>, the same verb on Docker, podman and wslc (the installer and
    /// <c>tools/release/get-release.sh</c> use it on each). False when the engine refused.
    /// </summary>
    public bool TagImage(string source, string target) => Run("tag", source, target).ExitCode == 0;
    public abstract bool ContainerExists(string containerName);
    public abstract bool ContainerRunning(string containerName);
    public abstract void StartContainer(string containerName);
    public abstract void StopContainer(string containerName);
    public abstract void RemoveContainer(string containerName);
    public abstract void RunContainer(ContainerRunSpec spec);

    /// <summary>
    /// Whether this runtime can run a VPN sidecar (needs <c>--cap-add</c>/<c>--device</c>). False by default -
    /// <see cref="WslcRuntime"/> keeps it (wslc rejects both flags, ADR 0009 matrix); Docker/Podman override to true.
    /// </summary>
    public virtual bool SupportsVpnSidecar => false;

    /// <summary>Runs the VPN sidecar; throws on a runtime that cannot (wslc). Docker/Podman override this.</summary>
    public virtual void RunVpnSidecar(VpnSidecarRunSpec spec) =>
        throw new NotSupportedException(
            $"The '{Name}' runtime cannot run a VPN sidecar (it does not accept --cap-add/--device). " +
            "Use Docker or Podman for the VPN-required variant.");

    public abstract string? ImageVersion(string image);
    public abstract string? ResolveImageDigest(string image);
    public abstract string GetLogs(string containerName, int tail);

    /// <summary>
    /// Docker, podman and wslc all accept <c>exec &lt;container&gt; &lt;cmd...&gt;</c> (wslc models its surface
    /// closely on Docker's - see <see cref="WslcRuntime"/>'s remarks - though this specific verb is assumed
    /// from that, not independently measured on 3.0.1). <c>getent hosts</c> is glibc's own resolver lookup,
    /// present in the studio image's Debian base; a non-glibc image would need a different probe. Bounded by
    /// <see cref="ResolveProbeTimeout"/> rather than the ordinary command timeout - this runs on every poll
    /// while deciding whether to restart, not once at start, so a wedged probe must fail fast.
    /// </summary>
    public virtual bool CanResolve(string containerName, string hostname)
    {
        var outcome = RunBounded(ResolveProbeTimeout, BuildResolveArgs(containerName, hostname));
        return !outcome.TimedOut && outcome.ExitCode == 0;
    }

    /// <summary>The <c>exec ... getent hosts ...</c> argument list. Exposed for unit tests (pure, no process started).</summary>
    public static string[] BuildResolveArgs(string containerName, string hostname) =>
        ["exec", containerName, "getent", "hosts", hostname];

    /// <summary>
    /// Retires a superseded image (non-forced remove, see <see cref="IContainerRuntime.RemoveImage"/>). No-op by
    /// default, because skipping a removal is always safe; every shipped runtime overrides it, each on a surface
    /// measured on its own engine (ADR 0009's rule).
    /// </summary>
    public virtual void RemoveImage(string imageRef)
    {
    }

    /// <summary>What still names an image (backlog 165). Empty, meaning "cannot say", by default.</summary>
    public virtual IReadOnlyList<string> ImageNames(string imageRef) => [];

    /// <summary>Whether the engine runs containers rootless (backlog 252). Null, "cannot say", unless Podman.</summary>
    public virtual bool? RunsRootless() => null;

    /// <summary>The image's baked release version, read as its <see cref="ImageLabels.Version"/> label.</summary>
    public virtual string? ImageReleaseVersion(string image) => ImageLabel(image, ImageLabels.Version);

    /// <summary>The image's baked release variant, read as its <see cref="ImageLabels.Variant"/> label (backlog 251).</summary>
    public virtual string? ImageVariant(string image) => ImageLabel(image, ImageLabels.Variant);

    /// <summary>One label of the image. Null by default, which callers read as "not confirmed".</summary>
    public virtual string? ImageLabel(string image, string label) => null;

    /// <summary>The image id a container was created from. Null by default, which means no stale-container detection.</summary>
    public virtual string? ContainerImageId(string containerName) => null;

    /// <summary>
    /// Ensures a named volume exists (idempotent). Docker, Podman, and wslc share the same
    /// <c>volume inspect</c> / <c>volume create</c> surface, so this lives in the shared base. A zero exit from
    /// <c>volume inspect</c> means it already exists; otherwise it is created.
    /// </summary>
    public void EnsureVolume(string volumeName)
    {
        if (Run(BuildVolumeInspectArgs(volumeName)).ExitCode == 0)
        {
            return;
        }

        RunChecked(BuildVolumeCreateArgs(volumeName));
    }

    /// <summary>
    /// Ensures a user-defined network exists (idempotent, backlog 270), the same way as <see cref="EnsureVolume"/>:
    /// Docker and Podman share <c>network inspect</c> / <c>network create</c>. Only rootless Podman is ever asked.
    /// </summary>
    public void EnsureNetwork(string networkName)
    {
        if (Run(BuildNetworkInspectArgs(networkName)).ExitCode == 0)
        {
            return;
        }

        RunChecked(BuildNetworkCreateArgs(networkName));
    }

    /// <summary>
    /// Best-effort network removal, the same way as <see cref="RemoveVolume"/>: false when the network is absent or the
    /// engine refuses (a container still sits on it), true only when it was actually removed.
    /// </summary>
    public bool RemoveNetwork(string networkName)
    {
        if (Run(BuildNetworkInspectArgs(networkName)).ExitCode != 0)
        {
            return false;
        }

        return Run(["network", "rm", networkName]).ExitCode == 0;
    }

    /// <summary>The <c>network inspect</c> argument list. Exposed for unit tests (pure, no process started).</summary>
    public static string[] BuildNetworkInspectArgs(string networkName) => ["network", "inspect", networkName];

    /// <summary>The <c>network create</c> argument list. Exposed for unit tests (pure, no process started).</summary>
    public static string[] BuildNetworkCreateArgs(string networkName) => ["network", "create", networkName];

    /// <summary>
    /// Best-effort volume removal: false when the volume is absent or the engine refuses (still
    /// referenced by a container), true only when it was actually removed. Never throws for those
    /// cases; targeted removal only, never a prune.
    /// </summary>
    public bool RemoveVolume(string volumeName)
    {
        if (Run(BuildVolumeInspectArgs(volumeName)).ExitCode != 0)
        {
            return false; // nothing to remove
        }

        return Run(BuildVolumeRemoveArgs(volumeName)).ExitCode == 0;
    }

    /// <summary>The <c>volume inspect</c> argument list. Exposed for unit tests (pure, no process started).</summary>
    public string[] BuildVolumeInspectArgs(string volumeName) => ["volume", "inspect", volumeName];

    /// <summary>The <c>volume create</c> argument list. Exposed for unit tests (pure, no process started).</summary>
    public string[] BuildVolumeCreateArgs(string volumeName) => ["volume", "create", volumeName];

    /// <summary>The <c>volume rm</c> argument list. Exposed for unit tests (pure, no process started).</summary>
    public string[] BuildVolumeRemoveArgs(string volumeName) => ["volume", "rm", volumeName];

    /// <summary>
    /// Deadline for the availability probe and the other quick, machine-level queries. A wedged engine does
    /// not fail, it stops answering, and every one of these calls used to block forever behind the splash.
    /// Twenty seconds is the figure the Windows installer's own probe already settled on.
    /// </summary>
    protected static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Deadline for ordinary CLI calls (introspection, start/stop, volume bookkeeping). Generous enough that a
    /// merely slow engine is never cut off, short enough that a wedged one surfaces as a failure rather than a
    /// hang. The genuinely long operations (loading an image tarball, creating a container that may pull) are
    /// run through <see cref="RunCheckedUnbounded"/> instead, where a deadline would be the bigger hazard.
    /// </summary>
    protected static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Deadline for <see cref="CanResolve"/>, polled repeatedly while deciding whether to restart (backlog 218).</summary>
    protected static readonly TimeSpan ResolveProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The exit code reported for a call that was killed on its deadline. Negative so it can never collide
    /// with a Windows process exit code, and always paired with <see cref="CliOutcome.TimedOut"/>, which is
    /// what callers that care should test.
    /// </summary>
    protected const int TimedOutExitCode = -1000;

    /// <summary>One CLI invocation's result, including whether it was killed on its deadline.</summary>
    /// <param name="ExitCode">The process exit code, or <see cref="TimedOutExitCode"/> when it was killed.</param>
    /// <param name="StdOut">Captured stdout (empty on a timeout).</param>
    /// <param name="StdErr">Captured stderr, or the timeout explanation.</param>
    /// <param name="TimedOut">True when the call was killed because it outlived its deadline.</param>
    protected readonly record struct CliOutcome(int ExitCode, string StdOut, string StdErr, bool TimedOut);

    /// <summary>
    /// Probes availability by running the given verb, distinguishing the three cases the launcher acts on
    /// differently: the CLI is not installed, the CLI is installed but its engine did not answer (a non-zero
    /// exit, or nothing at all before the deadline), or the engine answered.
    /// </summary>
    protected RuntimeAvailability ProbeAvailability(params string[] versionArgs)
    {
        try
        {
            return RunBounded(ProbeTimeout, versionArgs).ExitCode == 0
                ? RuntimeAvailability.Available
                : RuntimeAvailability.EngineNotResponding;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            // The runtime's executable is not on PATH.
            return RuntimeAvailability.CliMissing;
        }
    }

    /// <summary>Runs the CLI with <paramref name="args"/> under the default deadline.</summary>
    protected (int ExitCode, string StdOut, string StdErr) Run(params string[] args)
    {
        var outcome = RunBounded(DefaultCommandTimeout, args);
        return (outcome.ExitCode, outcome.StdOut, outcome.StdErr);
    }

    /// <summary>Runs the CLI and throws with the stderr tail if it exits non-zero (or times out).</summary>
    protected void RunChecked(params string[] args) => Check(args, RunBounded(DefaultCommandTimeout, args));

    /// <summary>
    /// Runs the CLI with NO deadline, for the operations whose honest duration is unbounded: loading a
    /// multi-hundred-megabyte image tarball, and creating a container that may pull its image first. Cutting
    /// those off on a timer would break working installs on slow disks and slow links, which is a worse
    /// failure than the one this file's deadlines exist to prevent. Returns stdout, which is where a load says
    /// what it named.
    /// </summary>
    protected string RunCheckedUnbounded(params string[] args)
    {
        var outcome = RunBounded(timeout: null, args);
        Check(args, outcome);
        return outcome.StdOut;
    }

    /// <summary>
    /// Runs the CLI with <paramref name="args"/>, capturing exit code and streams, and killing the WHOLE
    /// process tree if it outlives <paramref name="timeout"/> (null means no deadline). The tree matters:
    /// a runtime CLI spawns helpers (a machine start spawns a VM provider), and killing only the parent
    /// leaves those running with the pipes still open.
    /// </summary>
    protected CliOutcome RunBounded(TimeSpan? timeout, params string[] args)
    {
        var startInfo = new ProcessStartInfo(Cli)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start the {Cli} process.");

        // Read both pipes concurrently: a sequential ReadToEnd on stdout blocks forever once the OTHER
        // pipe's buffer fills, and that block is not interruptible, so no deadline above it could work.
        var stdoutRead = process.StandardOutput.ReadToEndAsync();
        var stderrRead = process.StandardError.ReadToEndAsync();

        if (timeout is { } deadline && !process.WaitForExit((int)deadline.TotalMilliseconds))
        {
            KillTree(process);
            return new CliOutcome(
                TimedOutExitCode,
                string.Empty,
                $"{EngineCommandText.ForMessages(Cli, args)} did not answer within {Describe(deadline)} and was cancelled.",
                TimedOut: true);
        }

        if (timeout is null)
        {
            process.WaitForExit();
        }

        // The process has exited; its pipes close, so these complete. The bound is belt and braces against a
        // grandchild that inherited a handle and outlived the kill.
        Task.WhenAll(stdoutRead, stderrRead).Wait(TimeSpan.FromSeconds(5));
        return new CliOutcome(
            process.ExitCode,
            stdoutRead.IsCompletedSuccessfully ? stdoutRead.Result : string.Empty,
            stderrRead.IsCompletedSuccessfully ? stderrRead.Result : string.Empty,
            TimedOut: false);
    }

    private void Check(string[] args, CliOutcome outcome)
    {
        if (outcome.ExitCode != 0)
        {
            throw new InvalidOperationException(outcome.TimedOut
                ? outcome.StdErr
                : $"{EngineCommandText.ForMessages(Cli, args)} failed (exit {outcome.ExitCode}): {outcome.StdErr.Trim()}");
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
            or NotSupportedException or AggregateException)
        {
            // It exited between the deadline and the kill, or the OS refused. Nothing more to do here.
        }
    }

    private static string Describe(TimeSpan span) =>
        span.TotalSeconds < 90 ? $"{span.TotalSeconds:0}s" : $"{span.TotalMinutes:0} minutes";
}
