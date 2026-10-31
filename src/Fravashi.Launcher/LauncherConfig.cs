using Fravashi.Domain;

namespace Fravashi.Launcher;

/// <summary>
/// The launcher's own persisted settings - distinct from the app's config-in-DB. Written once on first
/// run and reused forever after, so the port stays stable across launches (ADR 0003 V3).
/// </summary>
/// <remarks>
/// Backward compatibility (F9): every field carries a default, so an older <c>launcher.json</c> written
/// before a field existed (e.g. the owner's file predating <see cref="WebsiteUrl"/>) deserializes cleanly -
/// the missing property simply keeps its default. Unknown/extra properties are ignored by the deserializer.
/// The file location and shape are unchanged, so an existing launcher.json keeps working as-is.
/// </remarks>
public sealed record LauncherConfig
{
    /// <summary>Host port the container is mapped to. Stable once chosen.</summary>
    public int Port { get; init; }

    /// <summary>Host path mounted into the container at <c>/data</c> - the never-lose data directory.</summary>
    public string DataDir { get; init; } = "";

    /// <summary>Container name (so we find and restart the same container).</summary>
    public string ContainerName { get; init; } = LauncherDefaults.ContainerName;

    /// <summary>Image reference the container runs.</summary>
    public string Image { get; init; } = LauncherDefaults.Image;

    /// <summary>
    /// Product website opened by <c>fravashi website</c> and the tray "Website" item, when an owner sets one. Unset (the
    /// default) is the build's own (<see cref="LauncherDefaults.WebsiteUrl"/>), read through
    /// <see cref="LauncherDefaults.ResolveWebsiteUrl"/> at each use and never written back, so a default is not frozen
    /// into launcher.json, where a later build for another host would keep it (backlog 112's cause).
    /// </summary>
    public string? WebsiteUrl { get; init; }

    /// <summary>
    /// Container runtime to drive (<c>wslc</c>, <c>docker</c>, or <c>podman</c> - see <see cref="RuntimeNames"/>).
    /// Optional (ADR 0009): when absent - as in every launcher.json written before this field existed - the
    /// launcher auto-detects the installed runtime. Set it to pin a specific engine.
    /// </summary>
    public string? Runtime { get; init; }

    /// <summary>
    /// RETIRED (ADR 0017): the studio is window-only; the old <c>browser</c> opt-out no longer exists.
    /// The property stays so a launcher.json written by an earlier version still parses; its value is
    /// ignored.
    /// </summary>
    public string? Window { get; init; }

    /// <summary>
    /// The installed product version (e.g. <c>1.0.0</c>), written by the install/update path from the release
    /// manifest (ADR 0006). Optional and backward-compatible: absent from every launcher.json written before
    /// the release channel existed. The T5 update check compares this against the manifest's version.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// The installed variant name (ruling 3 / D03-3), e.g. <c>full</c>. Optional and backward-compatible
    /// (absent = treated as <see cref="LauncherDefaults.DefaultVariant"/>). Update checks compare only within
    /// this variant, so a <c>full</c> install is never offered a <c>corp-vpn</c> image.
    /// </summary>
    public string? Variant { get; init; }

    /// <summary>
    /// The release-manifest URL the update check fetches (ADR 0006). Optional and backward-compatible: absent
    /// falls back to <see cref="LauncherDefaults.ManifestUrl"/> (the build's update channel). Set it to point a
    /// specific install at a mirror or a staging channel. This is the one owner-facing update knob.
    /// </summary>
    public string? ManifestUrl { get; init; }

    /// <summary>
    /// The pinned image content digest (<c>sha256:…</c>) for the F3 tamper-evidence digest check (ADR 0007 #6).
    /// Absent from every launcher.json written before T8: on the next <c>fravashi</c> run the launcher resolves the
    /// loaded image's digest and pins it here (trust-on-first-use, degrading honestly until the signed releases
    /// repo provides an authoritative digest). Thereafter a resolved digest that differs from this - outside a
    /// <c>fravashi update</c>, which re-pins - makes the launcher refuse to start (loud). Backward-compatible.
    /// </summary>
    public string? PinnedImageDigest { get; init; }

    /// <summary>
    /// The optional VPN-sidecar settings (F5 / ADR 0008 - VPN1 - VPN6). Absent from every launcher.json written
    /// before T9 (and from a <c>full</c>-variant install), in which case the whole VPN feature is off. When present
    /// and <see cref="VpnConfig.Enabled"/>, the launcher runs the sidecar first and joins the studio to its netns -
    /// the structural kill switch. Requires a runtime that can grant the sidecar <c>--cap-add</c>/<c>--device</c>
    /// (Docker/Podman); wslc cannot (ADR 0009 matrix), so on wslc a VPN-enabled config is refused with guidance.
    /// </summary>
    public VpnConfig? Vpn { get; init; }

    /// <summary>
    /// The host-bridge URL injected into the container at its last (re)creation (plan 2a). The WSL
    /// subnet is reassigned across reboots, so the launcher compares this recorded value with the
    /// freshly resolved one and recreates the container on drift. Null when the container was
    /// created without a bridge. Backward-compatible: absent from older launcher.json files.
    /// </summary>
    public string? BridgeUrl { get; init; }

    /// <summary>
    /// A FINGERPRINT (a truncated SHA-256, never the value) of the host-bridge token injected into the
    /// container at its last (re)creation (backlog 135). The token reaches the container as an environment
    /// value, which cannot change on an existing container, so a rotated token left the studio calling the
    /// bridge with the old one and no repository would open, with nothing looking stale because the URL had
    /// not moved. Null in a launcher.json written before this existed, which is recorded and not punished.
    /// </summary>
    public string? BridgeTokenFingerprint { get; init; }

    /// <summary>
    /// The host port the bridge listens on, chosen once (9091 preferred) and reused so the injected
    /// URL stays stable across launches on the same boot. Backward-compatible: absent means unset.
    /// </summary>
    public int? BridgePort { get; init; }

    /// <summary>
    /// The port the app runner listens on (backlog 87), kept once chosen for the same reason as
    /// <see cref="BridgePort"/>: its URL is baked into the container, so a port that moved on every start would
    /// recreate the container on every start.
    /// </summary>
    public int? AppRunnerPort { get; init; }

    /// <summary>
    /// The port the upgrade operation listens on (backlog 157), kept once chosen like <see cref="AppRunnerPort"/>, and
    /// read by a console <c>fravashi update</c> to hand the update to the running fravashi.exe.
    /// </summary>
    public int? UpgradePort { get; init; }

    /// <summary>
    /// The optional credential-broker endpoint (ADR 0010, the <c>corp-broker</c> tier). Absent from every
    /// launcher.json written before this field existed and from every single-user install, in which case no broker
    /// env is injected and the run args are byte-for-byte the non-brokered shape. When present with a
    /// <see cref="BrokerLaunchConfig.BaseUrl"/>, the launcher injects <c>Broker__BaseUrl</c> (+ <c>Broker__Enabled</c>)
    /// into the studio container so a <c>corp-broker</c> studio started via <c>fravashi</c> reaches its broker instead of
    /// failing closed with "broker endpoint not configured" - closing the gap that previously forced a raw
    /// <c>podman run -e Broker__BaseUrl=…</c>. This carries only the endpoint, never a secret.
    /// </summary>
    public BrokerLaunchConfig? Broker { get; init; }

    /// <summary>
    /// The broker setting as it was BAKED into the container that exists now (backlog 138), in the form
    /// <c>StudioContainer.BrokerSignature</c> gives it. Environment is fixed when a container is created, so
    /// <see cref="Broker"/> says what is wanted and this says what is running; when they differ the container is
    /// recreated. Null means "never recorded", which reads as "no broker baked".
    /// </summary>
    public string? BrokerBaked { get; init; }

    /// <summary>
    /// What app-runner endpoint the current container was created with (backlog 87): its URL and a fingerprint of
    /// its token, as <c>StudioContainer.Baked</c> gives it. Baked like the bridge's, so compared on every
    /// start like the bridge's, or a moved port or a rotated token would silently never arrive.
    /// </summary>
    public string? AppRunnerBaked { get; init; }

    /// <summary>
    /// What upgrade endpoint the current container was created with (backlog 157): its URL and a fingerprint of its
    /// token, as <c>StudioContainer.Baked</c> gives it, compared on every start like the app runner's.
    /// </summary>
    public string? UpgradeBaked { get; init; }

    /// <summary>
    /// The user-defined network the current container was created on (backlog 270), as <c>StudioContainer.Baked</c>
    /// gives it, or null/empty for the engine's default. A container's network is fixed when it is created, so this is
    /// compared on every start: a rootless Podman container from before it existed recorded nothing, runs in pasta
    /// mode where the app runner cannot join it to a run, and is recreated once.
    /// </summary>
    public string? StudioNetworkBaked { get; init; }

    /// <summary>
    /// The host's DNS servers as last observed when the container was actually (re)started (backlog 218), from
    /// <see cref="HostDnsServers.Signature"/>. Unlike the other <c>*Baked</c> fields this is not compared at the
    /// NEXT start to decide whether to recreate - a start always refreshes the container's resolver for free on
    /// wslc - it is compared WHILE THE CONTAINER IS RUNNING by <see cref="StudioDnsRecovery"/>, so a host network
    /// change is noticed without a person restarting anything. Null means "never recorded" (an older
    /// launcher.json, or wslc not in use), which <see cref="StudioDnsRecovery"/> treats as an unknown baseline to
    /// adopt rather than evidence of staleness.
    /// </summary>
    public string? DnsServersBaked { get; init; }

    /// <summary>
    /// The host data directory the current container bind-mounts at <c>/data</c>, recorded when it is created. A
    /// mount is fixed at creation like the environment, so a start compares this with <see cref="DataDir"/> and
    /// recreates the container when they differ. The move to <c>%LOCALAPPDATA%\Fravashi</c> (<see cref="DataFolderMove"/>)
    /// is what makes them differ: it rewrites DataDir and records here the old path the container still mounts. Null in
    /// a launcher.json from before it existed, which is not evidence of a move.
    /// </summary>
    public string? DataDirBaked { get; init; }

    /// <summary>
    /// True once a healthy start found nothing of the former names left on the container engine: no former container or
    /// VPN sidecar, and the former image tag gone (backlog 384). From then on <see cref="FormerInstallMigration"/> asks
    /// the engine nothing at a start, which each start used to do for names that were long gone. Null in every
    /// launcher.json from before it, which is not evidence of anything and runs the migration as before.
    /// </summary>
    public bool? FormerNamesRetired { get; init; }
}

/// <summary>
/// The launcher-side credential-broker settings (ADR 0010). Deliberately minimal: the endpoint the studio's agent
/// CLIs are pointed at, and an optional secret-store reference for the per-user bearer token. It holds <b>no</b>
/// secret - the organization key lives only on the broker (ADR 0011) and the per-user bearer is minted at run time.
/// </summary>
public sealed record BrokerLaunchConfig
{
    /// <summary>
    /// The broker gateway URL the studio is pointed at (becomes the container's <c>Broker__BaseUrl</c>, which the app
    /// binds to <c>BrokerOptions.BaseUrl</c> → the CLI's <c>ANTHROPIC_BASE_URL</c>). Empty/absent leaves the broker
    /// inert (no env injected).
    /// </summary>
    public string? BaseUrl { get; init; }

    /// <summary>
    /// Optional override for the secret-store reference the per-user bearer token is read from (the container's
    /// <c>Broker__UserTokenSecretRef</c>). Absent keeps the app default (<c>broker:user-token</c>). A reference, never
    /// the token value.
    /// </summary>
    public string? UserTokenSecretRef { get; init; }
}

/// <summary>Launcher-wide constants.</summary>
public static class LauncherDefaults
{
    /// <summary>Uncommon default host port (ADR 0003 V3); the first free entry of <see cref="StudioPorts"/> is used.</summary>
    public const int PreferredPort = 9090;

    /// <summary>
    /// The only host ports the studio may serve on, tried in this order (backlog item 7). The sign-in page
    /// builds its redirect URI from the served base address, so the URI follows whatever port the launcher
    /// chose, and the identity provider matches it exactly, port included. Every entry here must ALREADY be
    /// registered at the provider as <c>http://localhost:PORT/auth/callback/idp</c>: a port the provider does
    /// not know is not a fallback, it is a sign-in the user cannot complete and cannot fix from inside the app.
    /// <para>
    /// Measured against the live provider on 2026-09-13 (an authorize GET for the studio's client, then registered under its former id,
    /// carrying a real S256 challenge, so the redirect-URI check is reached): 9090 answers
    /// <c>HTTP/1.1 302 Found</c> to the login page, while 9091, 9092 and 9093 each answer
    /// <c>HTTP/1.1 400 Bad Request</c> with "The specified 'redirect_uri' is not valid for this client
    /// application." The first pass at item 7 listed all four, which moved a launch off a busy 9090 onto a port
    /// sign-in is refused on, so the list briefly held 9090 alone.
    /// </para>
    /// <para>
    /// Widened 2026-09-15 by exactly the procedure below. The owner added
    /// <c>http://localhost:9091..9093/auth/callback/idp</c> to the studio's client on the test provider (then the
    /// default issuer), and the same probe then answered 302 to the login page for localhost 9090, 9091, 9092
    /// and 9093 and still 400 for 9094. Only the <c>localhost</c> spelling is registered for the extra ports,
    /// which is the one the studio sends (<see cref="LauncherUrls.Base"/>). The production provider was probed the
    /// same day and already answered 302 for 9091 and 9093. Since F1 the client is <c>fravashi-studio</c>, registered with
    /// all four ports on both issuers.
    /// </para>
    /// <para>
    /// Widening it is three steps IN THIS ORDER, never the last one alone: register the extra redirect URIs on
    /// the <c>fravashi-studio</c> application at the provider (an admin action at the provider, not a
    /// change in this repository), re-run the probe above and see a 302, and only then add the port here. Item
    /// 7's acceptance (sign-in still succeeds after the launcher has moved off 9090) needs that provider half;
    /// until it lands, a busy 9090 is an honest refusal naming the port instead of a broken sign-in.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<int> StudioPorts = [PreferredPort, 9091, 9092, 9093];

    /// <summary>
    /// <see cref="StudioPorts"/> as a sentence fragment: "9090", "9090 or 9091", "9090, 9091 or 9092". Every
    /// user-facing string about these ports is built from the list through here and
    /// <see cref="SignInPortsSentence"/>, because hand-written copies went false the moment the list changed:
    /// three of them kept naming 9091 to 9093 as registered while the provider refused all three.
    /// </summary>
    public static string StudioPortsPhrase { get; } = BuildPortsPhrase(StudioPorts);

    /// <summary>Where sign-in works, in one sentence that stays true for a list of any length.</summary>
    public static string SignInPortsSentence { get; } = StudioPorts.Count == 1
        ? $"Sign-in works only on port {StudioPortsPhrase}, because that is the only port registered at the identity provider."
        : $"Sign-in works only on ports {StudioPortsPhrase}, because they are the ports registered at the identity provider.";

    /// <summary>
    /// The whole "nothing is free" message, built from the candidate list so it cannot overstate what is
    /// registered. Shared by the launcher's refusal and fravashi.exe's message box, which must say the same thing.
    /// </summary>
    public static string PortsBusyMessage(IReadOnlyList<int> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var phrase = BuildPortsPhrase(candidates);
        return candidates.Count == 1
            ? $"The only host port {ProductName.Short} can serve the studio on ({phrase}) is busy. "
                + "Sign-in works only there, because it is the one port registered at the identity provider. "
                + $"Close whatever is holding it and start {ProductName.Short} again."
            : $"None of the host ports {ProductName.Short} can serve the studio on ({phrase}) is free. "
                + "Sign-in works only on these, because they are the ports registered at the identity provider. "
                + $"Free one of them (close whatever is holding it) and start {ProductName.Short} again.";
    }

    private static string BuildPortsPhrase(IReadOnlyList<int> ports)
    {
        var text = ports.Select(p => p.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        return text.Length <= 1
            ? string.Concat(text)
            : $"{string.Join(", ", text[..^1])} or {text[^1]}";
    }

    public const string ContainerName = "fravashi";
    public const string Image = "fravashi:latest";

    /// <summary>
    /// Named volume holding the agents' per-workspace synced copies (plan 2d, decision 2): fast
    /// volume IO for contained agents, kept in sync with the host folders by the studio's sync
    /// engine through the bridge. Disposable: everything on it re-syncs from the host.
    /// </summary>
    public const string WorkspacesVolume = "fravashi-workspaces";

    /// <summary>
    /// How many times the engine restarts a studio container that keeps exiting, before it gives up and
    /// leaves it stopped (backlog 122).
    /// <para>
    /// This replaced <c>--restart unless-stopped</c>, which has no ceiling. Measured on 2026-09-20: v0.9.23
    /// threw while building its service provider, and the container was restarted <b>1501 times in about 35
    /// minutes</b> on the owner's laptop, roughly forty crashes a minute, while the window said it might
    /// still be starting up. Nothing noticed and nothing stopped.
    /// </para>
    /// <para>
    /// <b>The ceiling is the entire fix, and there is no backoff.</b> That is measured, not assumed: a
    /// throwaway container exiting non-zero under <c>on-failure:5</c> was restarted five times and then left
    /// exited permanently, and the whole sequence took <b>2.4 seconds</b>. Podman puts no delay between
    /// attempts, and the roughly 1.5 seconds observed between crashes during the incident was the studio
    /// taking that long to die, not the engine waiting. Item 122's acceptance asked for "bounded with
    /// backoff" and only the bounded half is deliverable through a restart policy; adding a real delay would
    /// mean the launcher supervising the container itself, which is a much larger change for a loop that now
    /// lasts a couple of seconds instead of thirty-five minutes.
    /// </para>
    /// <para>
    /// What this GIVES UP, stated rather than discovered: <c>unless-stopped</c> also brought the container
    /// back after a reboot or an engine restart. That is not load-bearing here, because the studio is
    /// window-only and <c>fravashi</c> starts a stopped container itself on every launch (see
    /// <c>LauncherApp.EnsureContainer</c>). A studio serving in the background with no window is not
    /// something this product wants anyway.
    /// </para>
    /// </summary>
    public const int MaxContainerRestarts = 5;

    /// <summary>The engine's <c>--restart</c> value for the studio container. See <see cref="MaxContainerRestarts"/>.</summary>
    public static string RestartPolicy { get; } =
        "on-failure:" + MaxContainerRestarts.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Container mount point for the agent workspaces volume.</summary>
    public const string WorkspacesMountPath = "/workspaces";

    /// <summary>The default variant name (ruling 3 / D03-3) when a launcher.json records none.</summary>
    public const string DefaultVariant = "full";

    /// <summary>Container-internal port the app serves on (matches the Dockerfile).</summary>
    public const int ContainerPort = 8080;

    /// <summary>File name of the image tarball placed next to the launcher by the installer.</summary>
    public const string ImageTarFileName = "fravashi-image.tar";

    /// <summary>
    /// Default VPN-sidecar image (F5 / ADR 0008): the tiny multi-arch <c>gluetun</c> (Alpine, ~15-45 MB, arm64 +
    /// amd64) - VPN1's "minimum resources". Chosen over NordVPN's own Ubuntu-based client image (see the T9
    /// decision record): built-in default-drop firewall/kill-switch, first-class sidecar netns pattern, an HTTP
    /// control server the studio probes for tunnel status, and pure-env config with no baked secrets.
    /// </summary>
    public const string VpnImage = "qmcgaw/gluetun:latest";

    /// <summary>Default VPN-sidecar container name (distinct from the studio container).</summary>
    public const string VpnContainerName = "fravashi-vpn";

    /// <summary>Default VPN-sidecar memory ceiling - a tiny footprint (VPN1).</summary>
    public const string VpnMemoryLimit = "128M";

    /// <summary>gluetun's default HTTP control-server port (inside the shared netns) the studio probes.</summary>
    public const int VpnControlServerPort = 8000;

    /// <summary>
    /// Default gluetun control-server auth (VPN6). Since gluetun v3.39.1 every control route is private by default,
    /// so <c>GET /v1/publicip/ip</c> returns <b>401</b> unless auth is configured - which left the studio's
    /// <c>SidecarVpnConnectivity</c> probe reading a live tunnel as "not connected" and the <c>/secure-connection</c>
    /// gate stuck. gluetun accepts an env-only default role (<c>HTTP_CONTROL_SERVER_AUTH_DEFAULT_ROLE</c>, a JSON
    /// role applied to all routes) so no file mount is needed. We set it to <c>{"auth":"none"}</c>: the control
    /// server binds only inside the sidecar's network namespace on loopback and its port is <b>never published</b>
    /// to the host, so the only reachable client is the co-located studio - opening it there weakens neither the
    /// tunnel nor the default-drop firewall/kill-switch (those are the real boundary). Overridable per install via
    /// <c>launcher.json</c>'s <c>vpn.ControlServerAuthDefaultRole</c> (e.g. an <c>{"auth":"apikey",…}</c> role, or
    /// empty to leave gluetun's default). See the T9 VPN decision record and <c>docs/vpn-key-configuration.md</c>.
    /// </summary>
    public const string VpnControlServerAuthDefaultRole = "{\"auth\":\"none\"}";

    /// <summary>
    /// Default product website (F8/F10): the product's home on the build's service host. Configurable per install via
    /// <c>launcher.json</c>'s <c>websiteUrl</c>.
    /// </summary>
    public static string WebsiteUrl => ServiceHost.WebsiteUrl;

    /// <summary>The website to open: the configured value, or <see cref="WebsiteUrl"/> when none is set.</summary>
    public static string ResolveWebsiteUrl(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? WebsiteUrl : configured.Trim();

    /// <summary>
    /// Default release-manifest URL (ADR 0006, backlog 157): <c>release-manifest.json</c> on the build's update
    /// channel, which redirects it to the promoted release's asset on the public repository's GitHub Releases. A 404 or
    /// anything unreachable degrades honestly to "could not check". Overridable per install via <c>launcher.json</c>'s
    /// <c>manifestUrl</c>.
    /// </summary>
    public static string ManifestUrl => ServiceHost.UpdateChannelUrl + "/release-manifest.json";
}

/// <summary>
/// Builds the browser URLs the CLI verbs and the tray open. Centralised so <c>fravashi</c> and the tray never
/// drift on the path to the app or the settings re-entry query.
/// </summary>
public static class LauncherUrls
{
    /// <summary>The studio root on the reserved host port.</summary>
    public static string Base(int port) => $"http://localhost:{port}";

    /// <summary>
    /// The onboarding settings re-entry URL (<c>/onboarding?mode=settings</c>) - the wizard opens with saved
    /// choices pre-filled. If onboarding was never completed the app redirects to onboarding on its own, so
    /// this is safe to open in either state.
    /// </summary>
    public static string Onboarding(int port) => $"{Base(port)}/onboarding?mode=settings";
}
