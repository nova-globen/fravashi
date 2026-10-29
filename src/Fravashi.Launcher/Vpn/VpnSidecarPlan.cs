using Fravashi.Launcher.Integrity;

namespace Fravashi.Launcher.Vpn;

/// <summary>
/// Composes the VPN sidecar's run spec and its integrity component from the launcher config (F5 / ADR 0008 -
/// VPN1 - VPN3, VPN4). Kept pure and central so the gluetun contract (provider config, the built-in default-drop
/// firewall = the kill switch, the control server the studio probes) lives in one testable place rather than being
/// scattered through the launcher. The credential is referenced by env-var NAME only; no value flows through here.
/// </summary>
public static class VpnSidecarPlan
{
    /// <summary>
    /// Builds the sidecar run spec. <paramref name="hostPort"/>/<paramref name="containerPort"/> are the STUDIO's
    /// ports - published on the sidecar because the studio joins its netns. The firewall (VPN2/VPN3) is gluetun's
    /// built-in <c>FIREWALL=on</c> default-drop: only the tunnel and its server endpoint are reachable, so a dropped
    /// tunnel leaves the studio with no network path at all - the structural kill switch.
    /// </summary>
    public static VpnSidecarRunSpec BuildRunSpec(VpnConfig vpn, int hostPort, int containerPort)
    {
        ArgumentNullException.ThrowIfNull(vpn);

        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["VPN_SERVICE_PROVIDER"] = vpn.Provider,
            ["VPN_TYPE"] = vpn.Type,
            // VPN2/VPN3: the kill switch + firewall. Default-drop everything except the tunnel; correct by
            // construction, not a reactive watcher.
            ["FIREWALL"] = "on",
            // DNS-over-TLS so DNS also rides the tunnel (no plaintext DNS leak outside it).
            ["DOT"] = "on",
            // The HTTP control server the studio's connectivity probe queries over the shared netns (VPN6).
            ["HTTP_CONTROL_SERVER_ADDRESS"] = $":{vpn.ControlServerPort}",
        };

        // VPN6: gluetun v3.39.1+ makes every control route private by default, so /v1/publicip/ip answers 401 and
        // the studio's probe would read a live tunnel as "not connected". The env-only default role makes the route
        // answer 200 again. Loopback-only inside this netns (the port is never published), so it does not weaken the
        // tunnel or the default-drop firewall/kill-switch. Empty leaves gluetun's default untouched (owner override).
        if (!string.IsNullOrWhiteSpace(vpn.ControlServerAuthDefaultRole))
        {
            env["HTTP_CONTROL_SERVER_AUTH_DEFAULT_ROLE"] = vpn.ControlServerAuthDefaultRole;
        }

        if (!string.IsNullOrWhiteSpace(vpn.ServerCountries))
        {
            env["SERVER_COUNTRIES"] = vpn.ServerCountries!;
        }

        return new VpnSidecarRunSpec(
            ContainerName: vpn.ContainerName,
            Image: vpn.Image,
            HostPort: hostPort,
            ContainerPort: containerPort,
            MemoryLimit: vpn.MemoryLimit,
            Capabilities: ["NET_ADMIN"],
            Devices: ["/dev/net/tun"],
            ConfigEnv: env,
            SecretEnvNames: vpn.SecretEnv);
    }

    /// <summary>
    /// The integrity component (VPN4) recording the sidecar the launcher started: its image, the pinned digest, and
    /// the container name. Kind <c>sidecar</c> - the app-side evaluator runs sidecar-appropriate checks (digest
    /// provenance + the live tunnel), not the studio's own uid/mount/port checks.
    /// </summary>
    public static IntegrityComponent BuildIntegrityComponent(VpnConfig vpn, string digest) =>
        new()
        {
            Id = "vpn-sidecar",
            Kind = IntegrityComponentKinds.Sidecar,
            ImageRef = vpn.Image,
            ImageDigest = digest,
            ContainerName = vpn.ContainerName,
        };

    /// <summary>The <c>Vpn:ControlServerUrl</c> the launcher injects into the studio (shared-netns loopback).</summary>
    public static string ControlServerUrl(VpnConfig vpn) => $"http://127.0.0.1:{vpn.ControlServerPort}";
}
