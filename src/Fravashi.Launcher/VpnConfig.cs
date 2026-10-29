namespace Fravashi.Launcher;

/// <summary>
/// The optional VPN-sidecar settings for a VPN-required install (F5 / ADR 0008 - VPN1 - VPN6). Absent from every
/// launcher.json written before T9, so the field is nullable and the whole feature is off unless configured
/// (backward-compatible, F9). When <see cref="Enabled"/> the launcher runs a tiny VPN sidecar FIRST and starts the
/// studio joined to its network namespace (the structural kill switch), so the studio has no network path unless
/// the tunnel is up.
/// <para>
/// <b>Secrets are never stored here.</b> The VPN credential (a NordVPN token, OpenVPN service username/password, or
/// a WireGuard private key) is supplied to the launcher's <b>process environment</b> and forwarded to the sidecar
/// by NAME only (<c>--env NAME</c> passthrough) - the value never appears in launcher.json, in an argument list, in
/// a log, or in an image layer. <see cref="SecretEnv"/> lists only the variable NAMES to forward.
/// </para>
/// </summary>
public sealed record VpnConfig
{
    /// <summary>When true the launcher runs the VPN sidecar and joins the studio to its netns. Default off.</summary>
    public bool Enabled { get; init; }

    /// <summary>The sidecar image (default the tiny <c>gluetun</c> - VPN1). See ADR 0008 / the T9 decision record.</summary>
    public string Image { get; init; } = LauncherDefaults.VpnImage;

    /// <summary>The sidecar container name (distinct from the studio container).</summary>
    public string ContainerName { get; init; } = LauncherDefaults.VpnContainerName;

    /// <summary>gluetun's <c>VPN_SERVICE_PROVIDER</c> (default <c>nordvpn</c> - the ADR 0008 test vehicle).</summary>
    public string Provider { get; init; } = "nordvpn";

    /// <summary>gluetun's <c>VPN_TYPE</c> - <c>openvpn</c> (service credentials) or <c>wireguard</c> (private key).</summary>
    public string Type { get; init; } = "openvpn";

    /// <summary>Optional gluetun <c>SERVER_COUNTRIES</c> (e.g. <c>Sweden</c>); empty lets the provider choose.</summary>
    public string? ServerCountries { get; init; }

    /// <summary>The sidecar's memory ceiling - a tiny footprint (VPN1). Default 128M.</summary>
    public string MemoryLimit { get; init; } = LauncherDefaults.VpnMemoryLimit;

    /// <summary>gluetun's HTTP control-server port (inside the shared netns) the studio probes for tunnel status.</summary>
    public int ControlServerPort { get; init; } = LauncherDefaults.VpnControlServerPort;

    /// <summary>
    /// gluetun's env-only control-server auth default role (<c>HTTP_CONTROL_SERVER_AUTH_DEFAULT_ROLE</c>, VPN6). Since
    /// gluetun v3.39.1 the status route <c>/v1/publicip/ip</c> is auth-gated by default (returns 401), which would
    /// keep the studio's connectivity probe reading a live tunnel as "not connected". The default
    /// <c>{"auth":"none"}</c> opens the control server that is reachable only inside the sidecar's own netns on
    /// loopback (its port is never published to the host), so the probe clears the gate without weakening the
    /// tunnel/kill-switch. Set to another gluetun role JSON (e.g. <c>{"auth":"apikey","apikey":"…"}</c>) or empty to
    /// leave gluetun's default untouched. Not a secret.
    /// </summary>
    public string ControlServerAuthDefaultRole { get; init; } = LauncherDefaults.VpnControlServerAuthDefaultRole;

    /// <summary>
    /// The NAMES (never values) of the process-environment variables the launcher forwards to the sidecar as
    /// <c>--env NAME</c> passthrough. For gluetun+NordVPN OpenVPN these are <c>OPENVPN_USER</c>/<c>OPENVPN_PASSWORD</c>;
    /// for WireGuard, <c>WIREGUARD_PRIVATE_KEY</c>. The launcher reads the values from its own environment at run
    /// time and hands them to the runtime by name - they are never persisted or logged.
    /// </summary>
    public IReadOnlyList<string> SecretEnv { get; init; } = ["OPENVPN_USER", "OPENVPN_PASSWORD"];

    /// <summary>
    /// The pinned sidecar image digest (<c>sha256:…</c>) for the F3 tamper-evidence check (VPN4). Trust-on-first-use:
    /// pinned on the first VPN launch; a later resolved digest that differs makes the launcher refuse to start the
    /// studio (the sidecar the app trusts its netns to has been swapped). Absent/backward-compatible.
    /// </summary>
    public string? PinnedImageDigest { get; init; }
}
