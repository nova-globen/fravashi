namespace Fravashi.Launcher.Integrity;

/// <summary>
/// Builds the expected-shape <see cref="IntegrityRecord"/> from what the launcher is about to create (F3 / ADR
/// 0007) and provisions it: it loads the host-side HMAC key, composes the <c>studio</c> app component from the
/// config + the resolved image digest + the hardening plan that produced the run args, and writes the signed
/// record into <c>/data</c>. Shared by the normal launch path and the product-update swap so both produce a record
/// the app can verify, keeping the two run paths from drifting. The component list is the extension point for T9:
/// the VPN sidecar is added as a second <see cref="IntegrityComponent"/> with kind
/// <see cref="IntegrityComponentKinds.Sidecar"/>, and the app-side evaluator already iterates components generically.
/// </summary>
public static class IntegrityProvisioner
{
    /// <summary>
    /// Composes the expected record for the app container. <paramref name="digest"/> is the resolved+pinned image
    /// digest; <paramref name="runtimeName"/> the driven runtime. Env mirrors exactly what the image bakes and the
    /// launcher injects, so the app compares like-for-like.
    /// </summary>
    public static IntegrityRecord BuildRecord(
        LauncherConfig config, string runtimeName, string digest, IntegrityComponent? sidecar = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        var variant = string.IsNullOrWhiteSpace(config.Variant) ? LauncherDefaults.DefaultVariant : config.Variant!;
        var version = string.IsNullOrWhiteSpace(config.Version) ? "" : config.Version!;

        // The launcher always mounts /data (bind) and the named agent-workspaces volume (plan 2d);
        // /tmp tmpfs is applied by the hardening plan on every runtime (wslc + docker/podman both
        // accept --tmpfs). The /repos working-copy volume retired with the clone model (plan 2c):
        // the launcher rewrites this record on EVERY start, so an existing install's record
        // converges the first time the new exe runs.
        var mounts = new List<IntegrityMount>
        {
            new("/data", IntegrityMountKinds.Bind),
            new(LauncherDefaults.WorkspacesMountPath, IntegrityMountKinds.Volume),
        };
        var plan = ContainerHardening.Plan(runtimeName);
        if (plan.Controls.Any(c => c.Id == nameof(HardeningControlId.TmpfsTmp) && c.Status == HardeningStatus.Active))
        {
            mounts.Add(new("/tmp", IntegrityMountKinds.Tmpfs));
        }

        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FRAVASHI_DATA_DIR"] = "/data",
            [ContainerRunArgs.WorkspacesRootEnv] = LauncherDefaults.WorkspacesMountPath,
            ["FRAVASHI_IN_CONTAINER"] = "1",
            ["FRAVASHI_VARIANT"] = variant,
        };
        if (!string.IsNullOrEmpty(version))
        {
            env["FRAVASHI_VERSION"] = version;
        }

        var component = new IntegrityComponent
        {
            Id = "studio",
            Kind = IntegrityComponentKinds.App,
            ImageRef = config.Image,
            ImageDigest = digest,
            Variant = variant,
            Version = version,
            ContainerName = config.ContainerName,
            HostPort = config.Port,
            ContainerPort = LauncherDefaults.ContainerPort,
            Loopback = true,
            NonRootUid = 1000,
            Mounts = mounts,
            Env = env,
        };

        // VPN4: when a VPN sidecar was started, record it as a second component. The app-side evaluator iterates
        // components generically and applies sidecar-appropriate checks (digest provenance + the live tunnel).
        IReadOnlyList<IntegrityComponent> components = sidecar is null ? [component] : [component, sidecar];

        return new IntegrityRecord
        {
            SchemaVersion = 1,
            Runtime = runtimeName,
            RecordedUtc = DateTimeOffset.UtcNow,
            Components = components,
        };
    }
}
