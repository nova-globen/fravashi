using Fravashi.Domain;

namespace Fravashi.Launcher;

/// <summary>Spec for creating and running the studio container.</summary>
/// <param name="ContainerName">Name to assign the container.</param>
/// <param name="Image">Image reference to run.</param>
/// <param name="HostPort">Host port to publish.</param>
/// <param name="ContainerPort">Container-internal port to map from.</param>
/// <param name="DataDir">Host directory bind-mounted at <c>/data</c> (the never-lose DB volume).</param>
/// <param name="IntegrityKey">
/// Base64 HMAC key injected as <c>FRAVASHI_INTEGRITY_KEY</c> so the app can verify the launcher-written integrity
/// record (F3 / ADR 0007). Null/empty omits it (older behaviour / pre-T8 containers run report-only).
/// </param>
/// <param name="ImageDigest">
/// The pinned image digest injected as <c>FRAVASHI_IMAGE_DIGEST</c> so the app can cross-check it against the
/// HMAC-signed record (F3). Null/empty omits it.
/// </param>
/// <param name="NetworkContainer">
/// When set (F5 / ADR 0008 - VPN mode), the studio joins this container's network namespace
/// (<c>--network container:&lt;name&gt;</c>, the gluetun pattern) instead of publishing its own port - its egress
/// then flows only through that sidecar's tunnel (the structural kill switch). The app port is published on the
/// sidecar instead. Null/empty is the normal, non-VPN shape (the studio publishes its own loopback port).
/// </param>
/// <param name="VpnControlServerUrl">
/// When set (VPN mode), injected as <c>Vpn__ControlServerUrl</c> so the app's VPN-connectivity probe knows where the
/// sidecar's control server lives on the shared netns (e.g. <c>http://127.0.0.1:8000</c>). Null/empty omits it.
/// </param>
/// <param name="BrokerBaseUrl">
/// The credential-broker gateway URL (ADR 0010, the <c>corp-broker</c> tier). When set, injected as
/// <c>Broker__BaseUrl</c> (plus <c>Broker__Enabled=true</c>) so a brokered studio reaches its broker instead of
/// failing closed with "broker endpoint not configured". Null/empty is the single-user default - nothing broker
/// related is emitted, so the run args are byte-for-byte the non-brokered shape. This carries only the endpoint,
/// never a secret: the org key lives on the broker and the per-user bearer is minted at run time (ADR 0011).
/// </param>
/// <param name="BrokerUserTokenSecretRef">
/// Optional override for the secret-store reference the per-user bearer token is read from
/// (<c>Broker__UserTokenSecretRef</c>). Null/empty keeps the app's default (<c>broker:user-token</c>). A reference,
/// never the token value.
/// </param>
public sealed record ContainerRunSpec(
    string ContainerName,
    string Image,
    int HostPort,
    int ContainerPort,
    string DataDir,
    string? IntegrityKey = null,
    string? ImageDigest = null,
    string? NetworkContainer = null,
    string? VpnControlServerUrl = null,
    string? BrokerBaseUrl = null,
    string? BrokerUserTokenSecretRef = null,
    string? WindowToken = null,
    string? BridgeUrl = null,
    string? BridgeToken = null,
    // Plan 2d: the named volume the contained agents' synced workspace copies live on.
    string? WorkspacesVolume = null,
    string? WorkspacesMountPath = null,
    // Backlog 87: how the studio reaches the host's app runner, with its own token (never the bridge's).
    string? AppRunnerUrl = null,
    string? AppRunnerToken = null,
    // Backlog 157: how the studio asks fravashi.exe to install the release the update channel offers, with a token
    // of its own (neither the bridge's nor the app runner's).
    string? UpgradeUrl = null,
    string? UpgradeToken = null,
    // Backlog 270: the user-defined network the studio runs on, or null for the engine's default. Decided by
    // ContainerHardening.StudioNetworkFor and never set while NetworkContainer is.
    string? Network = null);

/// <summary>
/// Spec for the VPN sidecar (F5 / ADR 0008 - VPN1 - VPN3). A tiny container that establishes the tunnel and, with its
/// built-in default-drop firewall, is the network kill switch the studio joins. Requires a runtime that grants
/// <c>--cap-add</c> + <c>--device</c> (Docker/Podman); wslc rejects both (ADR 0009 matrix), so it cannot run this.
/// </summary>
/// <param name="ContainerName">Name for the sidecar container.</param>
/// <param name="Image">The sidecar image (gluetun by default).</param>
/// <param name="HostPort">The STUDIO's host port - published HERE because the studio joins this sidecar's netns.</param>
/// <param name="ContainerPort">The studio's container-internal port to publish from.</param>
/// <param name="MemoryLimit">The sidecar's memory ceiling (tiny - VPN1).</param>
/// <param name="Capabilities">Linux capabilities to add (e.g. <c>NET_ADMIN</c>) - a VPN client needs these.</param>
/// <param name="Devices">Devices to expose (e.g. <c>/dev/net/tun</c>).</param>
/// <param name="ConfigEnv">Non-secret provider config env (<c>VPN_SERVICE_PROVIDER</c>, <c>FIREWALL</c>, …).</param>
/// <param name="SecretEnvNames">
/// Names ONLY of process-env vars to forward by <c>--env NAME</c> passthrough (the runtime reads the value from the
/// launcher's own environment). The secret value never appears in the argument list, so it cannot leak to a log.
/// </param>
public sealed record VpnSidecarRunSpec(
    string ContainerName,
    string Image,
    int HostPort,
    int ContainerPort,
    string MemoryLimit,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> Devices,
    IReadOnlyDictionary<string, string> ConfigEnv,
    IReadOnlyList<string> SecretEnvNames);

/// <summary>
/// The launcher's window onto a container runtime, behind an interface so the orchestration is testable
/// without a real engine. The product is runtime-agnostic (ADR 0009): it detects and drives whichever
/// runtime is installed - <see cref="WslcRuntime"/> (Windows/WSL), <see cref="DockerRuntime"/>, or
/// <see cref="PodmanRuntime"/>. Each implementation composes the CLI's own argument syntax; the launcher
/// never hard-codes a specific engine.
/// </summary>
public interface IContainerRuntime
{
    /// <summary>User-facing runtime name (<c>wslc</c>, <c>docker</c>, <c>podman</c>) for messages and status.</summary>
    string Name { get; }

    /// <summary>True if the runtime's CLI is installed and its engine responds.</summary>
    bool IsAvailable();

    /// <summary>
    /// The same probe as <see cref="IsAvailable"/>, but reporting WHICH kind of unavailable it found: a
    /// missing CLI is a different situation from an installed engine whose machine is down, and the launcher
    /// acts differently on each (item 6). Defaulted so test doubles need not implement it: an unavailable
    /// double reads as a missing CLI, which is the conservative answer (nothing is started).
    /// </summary>
    RuntimeAvailability Probe() =>
        IsAvailable() ? RuntimeAvailability.Available : RuntimeAvailability.CliMissing;

    /// <summary>
    /// Best-effort attempt to start this runtime's backing machine, for the case the owner hits after every
    /// reboot: Podman is installed, its machine does not auto-start, and the engine therefore does not answer.
    /// Bounded internally and never throws. Defaulted to "nothing to start here", which is the honest answer
    /// for Docker (no supported CLI starts its engine) and wslc (its distro starts on demand).
    /// </summary>
    MachineStartResult TryStartMachine() => MachineStartResult.NotSupported(
        $"The '{Name}' runtime has no machine {ProductName.Short} can start; start its engine yourself and run `{ProductName.Command}` again.");

    bool ImageExists(string image);

    /// <summary>
    /// Loads an image from a <c>save</c> tarball and returns the names the engine says it loaded (its
    /// <c>Loaded image: ...</c> lines, read by <see cref="LoadedImages.Parse"/>), empty when it printed none. Backlog
    /// 190: the release tarball is named <c>localhost/fravashi:latest</c>, which on wslc and Docker is NOT the name the
    /// launcher creates from, so what the load really named has to be known to be bridged.
    /// </summary>
    IReadOnlyList<string> LoadImage(string tarPath);

    /// <summary>
    /// Gives the image <paramref name="source"/> names the extra name <paramref name="target"/> (the engine's
    /// <c>tag</c>). Returns false when the engine refused or the runtime cannot tag, which is the default so test
    /// doubles need not implement it; <see cref="LoadedImageName"/> then finds the name unmoved and says so.
    /// </summary>
    bool TagImage(string source, string target) => false;

    /// <summary>True if a container with this name exists (running or stopped).</summary>
    bool ContainerExists(string containerName);

    bool ContainerRunning(string containerName);

    /// <summary>Starts an existing, stopped container.</summary>
    void StartContainer(string containerName);

    /// <summary>Stops a running container (no-op-safe if already stopped). Used by <c>fravashi stop</c>.</summary>
    void StopContainer(string containerName);

    /// <summary>
    /// Removes a container (force). Tolerant: a missing container is not an error. The product-update path
    /// (T5) removes the old container after loading a new image so a fresh container is created from it.
    /// </summary>
    void RemoveContainer(string containerName);

    /// <summary>
    /// Retires a superseded image by id or reference: a NON-forced remove, whose refusal is swallowed rather
    /// than surfaced. Called after the launcher recreates the container from a freshly loaded image
    /// (local-bundle reinstall or product update) so an install never accumulates one orphaned
    /// <c>&lt;none&gt;</c> image per update, and only through <see cref="SupersededImage.Retire"/>, which asks
    /// <see cref="ImageNames"/> first: podman refuses a remove by id while TWO names reference the image but
    /// removes it with ONE, so "the engine refuses while another tag references it" was not true (backlog 165).
    /// Defaulted to a no-op so runtimes and test doubles without the capability keep the previous behaviour
    /// (skipping the removal is always safe: the image merely stays on disk).
    /// </summary>
    void RemoveImage(string imageRef)
    {
    }

    /// <summary>
    /// The names (repository:tag) an image still carries, empty when it carries none or the runtime cannot say
    /// (backlog 165). A superseded image that still has a name was named by somebody on purpose, a backup tag
    /// for instance, and is kept.
    /// </summary>
    IReadOnlyList<string> ImageNames(string imageRef) => [];

    /// <summary>
    /// Ensures a named volume exists, creating it if absent (idempotent). Always a <b>named</b> volume, never an
    /// anonymous one (anonymous volumes were the earlier cleanup-incident risk). Creating it explicitly means the
    /// same volume is reused across container re-creations. (The /repos working-copy volume retired in plan 2c;
    /// this stays for the agent-sync volume, plan 2d.)
    /// </summary>
    void EnsureVolume(string volumeName);

    /// <summary>
    /// Ensures a user-defined network exists, creating it if absent (idempotent), for the studio to run on (backlog 270).
    /// Only asked for when <see cref="ContainerHardening.StudioNetworkFor"/> names one, which today is rootless Podman.
    /// Defaulted to a no-op so test doubles and runtimes that are never asked need not implement it.
    /// </summary>
    void EnsureNetwork(string networkName)
    {
    }

    /// <summary>
    /// Best-effort removal of a user-defined network: true when the engine removed it, false when it does not exist, the
    /// engine refuses (a container still sits on it) or the runtime has no networks. Asked only for the former studio
    /// network of an install from before the internal rename (FormerInstallMigration). Defaulted to "nothing removed".
    /// </summary>
    bool RemoveNetwork(string networkName) => false;

    /// <summary>
    /// Best-effort removal of a named volume. Returns true when the engine removed it; false when it does not
    /// exist or is still referenced by a container (the engine refuses, which is the safe answer). Used to retire
    /// the obsolete <c>fravashi-repo</c> volume after the clone model's retirement (plan 2c, image-housekeeping
    /// directive 2026-07-28: targeted removal only, never a broad prune).
    /// </summary>
    bool RemoveVolume(string volumeName);

    /// <summary>Creates and starts a new container per <paramref name="spec"/>.</summary>
    void RunContainer(ContainerRunSpec spec);

    /// <summary>
    /// True when this runtime can run a VPN sidecar - i.e. it accepts <c>--cap-add</c> and <c>--device</c>. Docker
    /// and Podman do; <c>wslc</c> does NOT (its <c>run</c> has no such flags - ADR 0009 matrix / measured T9), so a
    /// VPN-required install requires Docker or Podman. Defaulted here so test doubles need not implement it.
    /// </summary>
    bool SupportsVpnSidecar => false;

    /// <summary>
    /// Creates and starts the VPN sidecar per <paramref name="spec"/> (F5 / ADR 0008). Throws
    /// <see cref="NotSupportedException"/> on a runtime that cannot grant the required capabilities/devices - the
    /// launcher checks <see cref="SupportsVpnSidecar"/> first and refuses with guidance rather than reaching here.
    /// </summary>
    void RunVpnSidecar(VpnSidecarRunSpec spec) =>
        throw new NotSupportedException(
            $"The '{Name}' runtime cannot run a VPN sidecar (it does not accept --cap-add/--device). " +
            "Use Docker or Podman for the VPN-required variant.");

    /// <summary>
    /// The image's short id / created stamp for <c>fravashi status</c>, or <c>null</c> if the image is absent.
    /// Best-effort: a display string, not a parsed value.
    /// </summary>
    string? ImageVersion(string image);

    /// <summary>
    /// The image's content digest for the F3 digest pin (ADR 0007 #6). Returns the config image <c>Id</c>
    /// (<c>sha256:…</c>) - the pinnable digest for tar-loaded images under every runtime (ADR 0009: <c>RepoDigests</c>
    /// is empty for tar-loaded images, so the config <c>Id</c> is used uniformly). <c>null</c> if the image is absent.
    /// </summary>
    string? ResolveImageDigest(string image);

    /// <summary>Returns the last <paramref name="tail"/> lines of the container's logs (best-effort).</summary>
    string GetLogs(string containerName, int tail);

    /// <summary>
    /// The image id the container was created from, or <c>null</c> when the container is absent or the
    /// runtime cannot report it. Lets the launcher notice a container that predates the currently loaded
    /// image (a local-bundle reinstall swaps the image under the same tag; merely starting the old
    /// container would run the old build) and recreate it. Defaulted to <c>null</c> (no recreation) so
    /// runtimes and test doubles without the capability keep the previous behaviour.
    /// </summary>
    string? ContainerImageId(string containerName) => null;

    /// <summary>
    /// The release version baked into the image (the <c>org.fravashi.version</c> label / <c>FRAVASHI_VERSION</c>),
    /// or <c>null</c> when it cannot be read. Lets <c>fravashi status</c> report the running image's version even
    /// before launcher.json records one. The shipped runtimes read it through <see cref="ImageLabel"/>.
    /// </summary>
    string? ImageReleaseVersion(string image);

    /// <summary>
    /// The release variant baked into the image (the <c>org.fravashi.variant</c> label / <c>FRAVASHI_VARIANT</c>), or
    /// <c>null</c> when it is absent or cannot be read, which callers read as "not known" (backlog 251). Read the same
    /// way as <see cref="ImageReleaseVersion"/>. Defaulted to <c>null</c> so a runtime or test double that cannot read
    /// labels keeps whatever variant was recorded.
    /// </summary>
    string? ImageVariant(string image) => null;

    /// <summary>
    /// One label of the image (<see cref="ImageLabels"/>), or <c>null</c> when it is absent or cannot be read.
    /// Defaulted to <c>null</c>, which every caller treats as "not confirmed".
    /// </summary>
    string? ImageLabel(string image, string label) => null;

    /// <summary>
    /// Whether the RUNNING container can resolve <paramref name="hostname"/> right now, asked of the
    /// container itself rather than inferred from the host (backlog 218): the host's DNS servers can change
    /// again without the container's resolver actually breaking (a VPN reconnecting to a resolver the
    /// container can still reach), so a held drift is confirmed broken before anything is restarted - a
    /// restart on every change, rather than on an actual failure, would cut off a running turn for nothing.
    /// Best-effort and bounded internally: false on any doubt (a missing container, a non-zero exit, a
    /// timeout) is the conservative answer, because it still only leads to a restart, never a missed one.
    /// Defaulted to true ("it resolves fine") so existing test doubles are unaffected; only a check that
    /// specifically means to model a broken resolver overrides it.
    /// </summary>
    bool CanResolve(string containerName, string hostname) => true;

    /// <summary>
    /// Whether the engine runs the launcher's containers ROOTLESS (backlog 252): true or false as the engine reported it
    /// for the connection in use, null when it cannot be asked or did not say. Only Podman answers; a rootful Podman
    /// machine publishes no port Windows can reach. Defaulted to null ("cannot say") for every other runtime and double.
    /// </summary>
    bool? RunsRootless() => null;
}

/// <summary>The labels the studio image carries (Dockerfile) that the launcher reads.</summary>
public static class ImageLabels
{
    /// <summary>The release version, the same value as <c>FRAVASHI_VERSION</c>.</summary>
    public const string Version = "org.fravashi.version";

    /// <summary>The release variant (<c>full</c>, <c>corp-broker</c>, ...), the same value as <c>FRAVASHI_VARIANT</c>.</summary>
    public const string Variant = "org.fravashi.variant";

    /// <summary>
    /// Stamped by the Dockerfile only AFTER its build step stripped every setuid/setgid bit and file capability and
    /// then checked that none remained (ADR 0029, D4). An image without it, such as one built before v0.9.50, is
    /// never reported as having that protection.
    /// </summary>
    public const string NoPrivilegeRaisingBinaries = "org.fravashi.no-setuid";
}
