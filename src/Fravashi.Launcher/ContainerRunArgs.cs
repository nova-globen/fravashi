namespace Fravashi.Launcher;

/// <summary>
/// Shared run-argument fragments so every runtime expresses the same feature the same way; both
/// <c>BuildRunArgs</c> implementations call these, keeping their parity (they differ only by
/// <c>--restart</c>). The pre-2c git working-copy volume fragment retired with the clone model.
/// </summary>
public static class ContainerRunArgs
{
    /// <summary>
    /// Appends the window access token env (ADR 0017): the studio's window-only gate requires the
    /// studio window shell to present this token, so only windows the launcher opens are admitted.
    /// A no-op when the spec carries none (the gate then accepts any studio window by its marker).
    /// </summary>
    public static void AppendWindowToken(List<string> args, ContainerRunSpec spec)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(spec);

        if (string.IsNullOrWhiteSpace(spec.WindowToken))
        {
            return;
        }

        args.Add("--env");
        args.Add($"{WindowAccessToken.EnvironmentVariable}={spec.WindowToken}");
    }

    /// <summary>Container env var naming the agent-workspaces volume mount point (plan 2d).</summary>
    public const string WorkspacesRootEnv = "FRAVASHI_WORKSPACES_ROOT";

    /// <summary>
    /// Appends the named agent-workspaces volume mount and its <c>FRAVASHI_WORKSPACES_ROOT</c> env
    /// var when the spec requests one (plan 2d, decision 2). A no-op when not configured.
    /// <para>
    /// With <paramref name="chownVolumeToContainerUser"/> the mount carries the Podman <c>:U</c>
    /// suffix: rootless Podman creates a fresh named volume root-owned, so the hardened uid-1000
    /// app could not write the copies without it (the same fix the retired /repos volume needed).
    /// Docker's copy-up preserves uid-1000 ownership; wslc runs writable.
    /// </para>
    /// </summary>
    public static void AppendWorkspacesVolume(List<string> args, ContainerRunSpec spec, bool chownVolumeToContainerUser = false)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(spec);

        if (string.IsNullOrWhiteSpace(spec.WorkspacesVolume) || string.IsNullOrWhiteSpace(spec.WorkspacesMountPath))
        {
            return;
        }

        var mount = $"{spec.WorkspacesVolume}:{spec.WorkspacesMountPath}";
        if (chownVolumeToContainerUser)
        {
            mount += ":U";
        }

        args.Add("--volume");
        args.Add(mount);
        args.Add("--env");
        args.Add($"{WorkspacesRootEnv}={spec.WorkspacesMountPath}");
    }

    /// <summary>
    /// Appends the F3 tamper-evidence env (ADR 0007): the HMAC key the app uses to verify the integrity record and
    /// the pinned image digest it cross-checks. Both are part of the runtime shape only the launcher creates. A
    /// no-op when either is absent (a pre-T8 container, or a runtime that could not resolve a digest), which keeps
    /// the app in report-only mode rather than failing closed - do-no-harm on an install that predates T8.
    /// </summary>
    public static void AppendIntegrityEnv(List<string> args, ContainerRunSpec spec)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(spec);

        if (!string.IsNullOrWhiteSpace(spec.IntegrityKey))
        {
            args.Add("--env");
            args.Add($"{Integrity.IntegrityEnv.KeyVar}={spec.IntegrityKey}");
        }

        if (!string.IsNullOrWhiteSpace(spec.ImageDigest))
        {
            args.Add("--env");
            args.Add($"{Integrity.IntegrityEnv.DigestVar}={spec.ImageDigest}");
        }
    }

    /// <summary>
    /// Appends the app-side VPN env (F5 / ADR 0008): the sidecar control-server URL the studio's connectivity probe
    /// queries over the shared netns, mapped to the app's <c>Vpn:ControlServerUrl</c> config via the ASP.NET
    /// double-underscore convention. A no-op when not in VPN mode. This is not a secret.
    /// </summary>
    public static void AppendVpnAppEnv(List<string> args, ContainerRunSpec spec)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(spec);

        if (string.IsNullOrWhiteSpace(spec.VpnControlServerUrl))
        {
            return;
        }

        args.Add("--env");
        args.Add($"Vpn__ControlServerUrl={spec.VpnControlServerUrl}");
        args.Add("--env");
        args.Add("Vpn__Enabled=true");
    }

    /// <summary>
    /// Appends the credential-broker endpoint env (ADR 0010, the <c>corp-broker</c> tier). When the spec carries a
    /// <see cref="ContainerRunSpec.BrokerBaseUrl"/>, emits <c>Broker__BaseUrl=&lt;url&gt;</c> and
    /// <c>Broker__Enabled=true</c> so the studio's credential layer points the CLI at the gateway instead of failing
    /// closed with "broker endpoint not configured" - closing the gap where a <c>corp-broker</c> studio launched via
    /// <c>fravashi</c> had no way to receive its broker URL (it previously needed a raw <c>podman run -e</c>). When a
    /// <see cref="ContainerRunSpec.BrokerUserTokenSecretRef"/> is set it is emitted as
    /// <c>Broker__UserTokenSecretRef</c>. A no-op when no broker URL is configured, so the single-user default run
    /// args are unchanged. Only the endpoint/reference cross the boundary here - never a secret value: the org key
    /// stays on the broker and the per-user bearer is minted at run time (ADR 0011).
    /// </summary>
    public static void AppendBrokerEnv(List<string> args, ContainerRunSpec spec)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(spec);

        if (string.IsNullOrWhiteSpace(spec.BrokerBaseUrl))
        {
            return;
        }

        args.Add("--env");
        args.Add($"Broker__BaseUrl={spec.BrokerBaseUrl}");
        args.Add("--env");
        args.Add("Broker__Enabled=true");

        if (!string.IsNullOrWhiteSpace(spec.BrokerUserTokenSecretRef))
        {
            args.Add("--env");
            args.Add($"Broker__UserTokenSecretRef={spec.BrokerUserTokenSecretRef}");
        }
    }

    /// <summary>
    /// Appends the host-bridge endpoint env (plan 2a): the URL and token the studio uses to reach
    /// fravashi.exe's file/git/search/watch bridge from inside the container. Env names are literals
    /// here because the Launcher library does not reference the Bridge library; they must match
    /// <c>Fravashi.Bridge.BridgeAccessToken.UrlEnvironmentVariable</c> / <c>TokenEnvironmentVariable</c>.
    /// A no-op unless BOTH are present, so a bridge-less launch keeps the previous arg shape.
    /// </summary>
    public static void AppendBridgeEnv(List<string> args, ContainerRunSpec spec)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(spec);

        if (string.IsNullOrWhiteSpace(spec.BridgeUrl) || string.IsNullOrWhiteSpace(spec.BridgeToken))
        {
            return;
        }

        args.Add("--env");
        args.Add($"FRAVASHI_BRIDGE_URL={spec.BridgeUrl}");
        args.Add("--env");
        args.Add($"FRAVASHI_BRIDGE_TOKEN={spec.BridgeToken}");

        // Backlog 87: the app runner sits beside the bridge and is reached the same way, with a token of its own
        // (must match Fravashi.AppRunner.AppRunnerToken's variable names). Only when both are present.
        if (!string.IsNullOrWhiteSpace(spec.AppRunnerUrl) && !string.IsNullOrWhiteSpace(spec.AppRunnerToken))
        {
            args.Add("--env");
            args.Add($"FRAVASHI_APPRUNNER_URL={spec.AppRunnerUrl}");
            args.Add("--env");
            args.Add($"FRAVASHI_APPRUNNER_TOKEN={spec.AppRunnerToken}");
        }

        // Backlog 157: the upgrade operation, the same way, with a token of its own (must match
        // Fravashi.Bridge.StudioUpgradeToken's variable names). Only when both are present.
        if (!string.IsNullOrWhiteSpace(spec.UpgradeUrl) && !string.IsNullOrWhiteSpace(spec.UpgradeToken))
        {
            args.Add("--env");
            args.Add($"FRAVASHI_UPGRADE_URL={spec.UpgradeUrl}");
            args.Add("--env");
            args.Add($"FRAVASHI_UPGRADE_TOKEN={spec.UpgradeToken}");
        }
    }

    /// <summary>
    /// Builds the VPN sidecar's <c>run</c> argument list (F5 / ADR 0008 - VPN1 - VPN3). Shared by Docker and Podman so
    /// the shape is identical and unit-testable without a real engine. Emits the capabilities and devices a VPN
    /// client needs, the tiny memory cap, the STUDIO's loopback port publish (published here because the studio
    /// joins this sidecar's netns), the non-secret provider config env (sorted for determinism), and - crucially -
    /// the secret credential vars by NAME only (<c>--env NAME</c> passthrough), so no secret value ever appears in
    /// the argument list, a log, or a process listing.
    /// </summary>
    public static List<string> BuildVpnSidecar(VpnSidecarRunSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var args = new List<string> { "run", "--detach", "--name", spec.ContainerName };

        foreach (var cap in spec.Capabilities)
        {
            args.Add("--cap-add");
            args.Add(cap);
        }

        foreach (var device in spec.Devices)
        {
            args.Add("--device");
            args.Add(device);
        }

        args.Add("--memory");
        args.Add(spec.MemoryLimit);

        // The studio joins this container's netns, so the studio's port is published HERE, loopback-only.
        args.Add("--publish");
        args.Add($"127.0.0.1:{spec.HostPort}:{spec.ContainerPort}");

        foreach (var kv in spec.ConfigEnv.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            args.Add("--env");
            args.Add($"{kv.Key}={kv.Value}");
        }

        // Secrets by NAME only - the runtime reads the value from the launcher's own environment. Never a value here.
        foreach (var name in spec.SecretEnvNames)
        {
            args.Add("--env");
            args.Add(name);
        }

        args.Add(spec.Image);
        return args;
    }
}
