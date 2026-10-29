using Fravashi.Launcher.Vpn;

namespace Fravashi.Launcher;

/// <summary>
/// What the container that exists NOW was created carrying, in the comparable form launcher.json records
/// (backlog 135, 138, 87, 157, 270). Environment and network are fixed when a container is created, so a start compares
/// these against what <see cref="StudioContainer"/> would bake today and recreates the container when they differ.
/// None of them is a secret: a token is present only as a fingerprint.
/// </summary>
/// <param name="BridgeUrl">The host-bridge URL, or empty for no bridge.</param>
/// <param name="BridgeTokenFingerprint">A truncated hash of the bridge token, or empty for no bridge.</param>
/// <param name="BrokerSignature">The credential broker's endpoint and token reference, or empty for none.</param>
/// <param name="AppRunnerSignature">The app runner's URL and its token's fingerprint, or empty for none.</param>
/// <param name="UpgradeSignature">The upgrade operation's URL and its token's fingerprint, or empty for none (157).</param>
/// <param name="StudioNetwork">The user-defined network the studio runs on, or empty for the engine's default (270).</param>
/// <param name="DataDir">The host data directory bind-mounted at <c>/data</c> (the move to Fravashi changes it).</param>
public readonly record struct BakedContainerSettings(
    string BridgeUrl,
    string BridgeTokenFingerprint,
    string BrokerSignature,
    string AppRunnerSignature,
    string UpgradeSignature,
    string StudioNetwork,
    string DataDir);

/// <summary>
/// The ONE place that turns a <see cref="LauncherConfig"/> into a running studio container and records what it
/// baked into it (backlog 160). Both a start (<c>LauncherApp.EnsureContainer</c>) and a product update
/// (<c>ProductUpdater.ApplyAsync</c>) go through <see cref="Create"/>.
/// <para>
/// It exists because those two used to build a <see cref="ContainerRunSpec"/> each. Every setting baked into the
/// container's environment then had to be added to both, and the update path was already missing the host
/// bridge, the app runner and the VPN netns: an updated studio had no bridge, so no workspace synced and no
/// agent session could run, and in the <c>corp-vpn</c> variant it had lost its structural kill switch. It could
/// not repair itself either, because the updater saved the OLD bridge and app-runner records, so the next start
/// compared those, saw nothing stale, and started the broken container.
/// </para>
/// <para>
/// The record and the comparison are the same object here (<see cref="Baked"/>), which is the part that makes a
/// new baked setting (the network was the sixth, backlog 270) a one-place change rather than a thing to remember in two.
/// </para>
/// </summary>
public sealed class StudioContainer
{
    private readonly IContainerRuntime _runtime;
    private readonly LauncherConfigStore _configStore;
    private readonly BridgeLaunch? _bridge;

    /// <param name="bridge">
    /// The host bridge fravashi.exe started, or null when this launcher holds none (a console verb, or a host with
    /// no resolvable container-facing address). Null is recorded as "no bridge baked", so the next start that
    /// DOES hold one sees the container as stale and recreates it.
    /// </param>
    public StudioContainer(IContainerRuntime runtime, LauncherConfigStore configStore, BridgeLaunch? bridge = null)
    {
        _runtime = runtime;
        _configStore = configStore;
        _bridge = bridge;
        _runsRootless = new Lazy<bool?>(runtime.RunsRootless);
    }

    private readonly Lazy<bool?> _runsRootless;

    /// <summary>
    /// A truncated SHA-256 of a token, which is what launcher.json records (backlog 135). The tokens are 256
    /// random bits, so a hash gives nothing away, and the file never holds the value. Empty for no token, so
    /// "none then, none now" compares equal.
    /// </summary>
    public static string Fingerprint(string? token) =>
        string.IsNullOrEmpty(token)
            ? string.Empty
            : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)))[..16];

    /// <summary>
    /// The broker setting as one comparable string: the endpoint and the token reference, the two things
    /// <see cref="ContainerRunArgs.AppendBrokerEnv"/> bakes. Empty for no broker. Neither part is a secret.
    /// </summary>
    public static string BrokerSignature(LauncherConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return string.IsNullOrWhiteSpace(config.Broker?.BaseUrl)
            ? string.Empty
            : $"{config.Broker.BaseUrl.Trim()}|{config.Broker.UserTokenSecretRef?.Trim()}";
    }

    /// <summary>What this launcher would bake into a container created right now, for <paramref name="config"/>.</summary>
    public BakedContainerSettings Baked(LauncherConfig config) => new(
        _bridge?.Url ?? string.Empty,
        Fingerprint(_bridge?.Token),
        BrokerSignature(config),
        EndpointSignature(_bridge?.AppRunnerUrl, _bridge?.AppRunnerToken),
        EndpointSignature(_bridge?.UpgradeUrl, _bridge?.UpgradeToken),
        Network(config) ?? string.Empty,
        config.DataDir);

    /// <summary>
    /// The user-defined network the studio runs on, or null for the engine's default (backlog 270), as
    /// <see cref="ContainerHardening.StudioNetworkFor"/> decides it for this runtime. The rootless answer is asked of
    /// the engine once per instance: a start compares and then creates, and the mode cannot change in between.
    /// </summary>
    public string? Network(LauncherConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        // A Podman that cannot say this time (a slow `podman info`) keeps what the container was created with, so one
        // unanswered probe never moves the studio back onto pasta, where no run can be joined.
        var rootless = _runsRootless.Value ?? (string.IsNullOrEmpty(config.StudioNetworkBaked) ? null : true);
        return ContainerHardening.StudioNetworkFor(_runtime.Name, rootless, NetnsContainer(config) is not null);
    }

    /// <summary>A host endpoint baked beside the bridge, as its URL and its token's fingerprint; empty for none.</summary>
    private static string EndpointSignature(string? url, string? token) =>
        string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token) ? string.Empty : $"{url}|{Fingerprint(token)}";

    /// <summary>
    /// The VPN sidecar container the studio joins the network namespace of (F5 / ADR 0008), or null when VPN is
    /// off. Derived from the configuration rather than passed in, so no caller can create a container that
    /// escapes the tunnel by forgetting it.
    /// </summary>
    public static string? NetnsContainer(LauncherConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Vpn is { Enabled: true } vpn ? vpn.ContainerName : null;
    }

    /// <summary>
    /// Creates and starts the studio container from <paramref name="config"/>, then SAVES and returns the
    /// configuration carrying what was baked into it. The save is unconditional: creating a container is rare,
    /// and writing the record in the same act is what stops a caller shipping a container nothing knows the
    /// shape of.
    /// </summary>
    /// <param name="integrityKey">The base64 HMAC key for the F3 record, or null for report-only.</param>
    /// <param name="imageDigest">The pinned image digest, or null when none could be resolved.</param>
    public LauncherConfig Create(LauncherConfig config, string? integrityKey, string? imageDigest)
    {
        ArgumentNullException.ThrowIfNull(config);

        // Plan 2d (decision 2): the contained agents' synced workspace copies live on a NAMED volume
        // (fast IO; disposable, everything re-syncs from the host folders).
        _runtime.EnsureVolume(LauncherDefaults.WorkspacesVolume);
        var spec = BuildSpec(config, integrityKey, imageDigest);
        if (spec.Network is { Length: > 0 } network)
        {
            // Backlog 270: made once and kept, like the volume; the engine refuses a run on a network that is missing.
            _runtime.EnsureNetwork(network);
        }

        _runtime.RunContainer(spec);

        var baked = Baked(config);
        var recorded = config with
        {
            BridgeUrl = _bridge?.Url,
            BridgeTokenFingerprint = baked.BridgeTokenFingerprint,
            BrokerBaked = baked.BrokerSignature,
            AppRunnerBaked = baked.AppRunnerSignature,
            UpgradeBaked = baked.UpgradeSignature,
            StudioNetworkBaked = baked.StudioNetwork,
            DataDirBaked = baked.DataDir,
            // Backlog 218: a freshly created container gets the host's DNS right now (wslc copies it at
            // start), so this is the true baseline StudioDnsRecovery compares against while it keeps running.
            DnsServersBaked = HostDnsServers.Signature(HostDnsServers.Resolve()),
        };
        _configStore.Save(recorded);
        return recorded;
    }

    /// <summary>
    /// The run spec for <paramref name="config"/>. Internal so the tests can read what would be baked without
    /// a runtime; nothing outside this class constructs a <see cref="ContainerRunSpec"/> for the studio.
    /// </summary>
    internal ContainerRunSpec BuildSpec(LauncherConfig config, string? integrityKey, string? imageDigest) =>
        new(
            config.ContainerName,
            config.Image,
            config.Port,
            LauncherDefaults.ContainerPort,
            config.DataDir,
            integrityKey,
            imageDigest,
            // F5: in VPN mode the studio joins the sidecar's netns instead of publishing its own port, and is
            // told where the sidecar's control server lives so its connectivity probe can reach it.
            NetnsContainer(config),
            config.Vpn is { Enabled: true } vpn ? VpnSidecarPlan.ControlServerUrl(vpn) : null,
            config.Broker?.BaseUrl,
            config.Broker?.UserTokenSecretRef,
            // ADR 0017: only studio windows carrying this token are served by the studio.
            WindowAccessToken.GetOrCreate(_configStore.ConfigDirectory),
            // Plan 2a: how the studio reaches the host bridge (null when no address resolved).
            _bridge?.Url,
            _bridge?.Token,
            LauncherDefaults.WorkspacesVolume,
            LauncherDefaults.WorkspacesMountPath,
            // Backlog 87: the app runner beside the bridge, with a token of its own.
            _bridge?.AppRunnerUrl,
            _bridge?.AppRunnerToken,
            // Backlog 157: the upgrade operation, likewise.
            _bridge?.UpgradeUrl,
            _bridge?.UpgradeToken,
            // Backlog 270: a network of its own where the engine's default cannot be joined to a run.
            Network(config));
}
