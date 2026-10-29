using Fravashi.Application.Integrity;
using Fravashi.Domain;

namespace Fravashi.Infrastructure.Integrity;

/// <summary>
/// The pure F3 decision function (ADR 0007): given the launcher-written record, the injected HMAC key, and an
/// observed <see cref="IRuntimeShapeProbe"/>, it decides the enforcement mode and runs every shape check, returning
/// an <see cref="IntegrityStatus"/>. It has no side effects (no termination, no I/O) so BOTH directions are
/// unit-tested exhaustively - a legitimate shape returns all-pass and never fail-closed, a tampered shape returns a
/// failing check under enforcement. The checks iterate the record's <c>Components</c>, so T9 adds the VPN sidecar
/// as another component with a <c>sidecar</c> kind and its checks slot in without touching this control flow.
/// </summary>
public static class IntegrityEvaluator
{
    /// <summary>Env var marking that the app runs inside the studio container (image-baked).</summary>
    public const string InContainerEnv = "FRAVASHI_IN_CONTAINER";

    /// <summary>The sidecar component kind (mirrors the launcher's <c>IntegrityComponentKinds.Sidecar</c>).</summary>
    public const string SidecarKind = "sidecar";

    /// <summary>
    /// Evaluates integrity. <paramref name="record"/>/<paramref name="key"/> are null when the launcher did not
    /// provision them (⇒ report-only). <paramref name="isDevelopment"/> forces report-only regardless.
    /// </summary>
    public static IntegrityStatus Evaluate(
        IntegrityRecordModel? record,
        byte[]? key,
        IRuntimeShapeProbe probe,
        bool isDevelopment,
        DateTimeOffset evaluatedUtc,
        bool? vpnConnected = null)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var inContainer = string.Equals(probe.GetEnv(InContainerEnv), "1", StringComparison.Ordinal);
        var provisioned = record is not null && key is { Length: > 0 };

        var (mode, reason) = DetermineMode(isDevelopment, inContainer, provisioned);

        if (record is null || key is null)
        {
            return new IntegrityStatus(mode, reason, Provisioned: false, Checks: [], evaluatedUtc, Runtime: null);
        }

        var checks = new List<IntegrityCheck>();

        // The record must be intact before any shape check means anything: a broken HMAC ⇒ the record cannot be
        // trusted, so it is the one failing check and the rest are moot.
        if (!IntegrityCanonical.Verify(record, key))
        {
            checks.Add(new IntegrityCheck(
                "studio", "Record signature", IntegrityCheckStatus.Fail,
                Expected: "valid HMAC", Actual: "invalid HMAC",
                Detail: "The integrity record's HMAC did not verify against the launcher's injected key - the " +
                    "expected-shape record has been altered (or signed with a different key)."));
            return new IntegrityStatus(mode, reason, Provisioned: true, checks, evaluatedUtc, record.Runtime);
        }

        checks.Add(new IntegrityCheck(
            "studio", "Record signature", IntegrityCheckStatus.Pass,
            Expected: "valid HMAC", Actual: "valid HMAC",
            Detail: "The expected-shape record's HMAC verified against the launcher's injected key."));

        foreach (var component in record.Components)
        {
            if (string.Equals(component.Kind, SidecarKind, StringComparison.Ordinal))
            {
                AppendSidecarChecks(checks, component, vpnConnected);
            }
            else
            {
                AppendComponentChecks(checks, component, probe);
            }
        }

        return new IntegrityStatus(mode, reason, Provisioned: true, checks, evaluatedUtc, record.Runtime);
    }

    /// <summary>
    /// VPN4: the sidecar's app-side checks. The studio cannot inspect another container's image from inside its own
    /// process, so the sidecar's <b>identity</b> (image digest) is verified host-side by the launcher - which pins it
    /// and REFUSES to start on a swapped sidecar (fail-closed) - and recorded here for provenance. What the studio
    /// CAN observe, because it shares the sidecar's netns, is the live tunnel: a confirmed public IP means egress is
    /// flowing through the VPN. This tunnel check is <b>report-only</b> (never fail-closes): tunnel state is the
    /// gate's job (the studio is held at <c>/secure-connection</c> until connected, and the structural kill switch
    /// means a dropped/killed sidecar leaves the studio with no network at all) - so a transient reconnect surfaces
    /// as "waiting" rather than terminating and breaking auto-recovery.
    /// </summary>
    private static void AppendSidecarChecks(List<IntegrityCheck> checks, IntegrityComponentModel c, bool? vpnConnected)
    {
        checks.Add(new IntegrityCheck(
            c.Id, "Sidecar image digest", IntegrityCheckStatus.Skipped,
            Expected: c.ImageDigest, Actual: null,
            Detail: $"The VPN sidecar image ({c.ImageRef}) digest is pinned and verified host-side by the launcher - " +
                "which refuses to start on a swapped sidecar - and recorded here for provenance. It is not observable " +
                "from inside the studio container, so it is shown, not re-checked."));

        var (status, detail) = vpnConnected switch
        {
            true => (IntegrityCheckStatus.Pass,
                "The studio's egress is confirmed through the VPN sidecar tunnel (a public IP was reported over the shared netns)."),
            false => (IntegrityCheckStatus.Skipped,
                "The VPN tunnel is not currently confirmed - the studio is held at the waiting-for-connection gate, and " +
                "the sidecar's default-drop firewall means no traffic leaves without it. Not fail-closed here (connectivity " +
                "is the gate's job); a stopped/killed sidecar shows up as this unconfirmed tunnel."),
            null => (IntegrityCheckStatus.Skipped,
                "The VPN tunnel state has not been probed yet."),
        };

        checks.Add(new IntegrityCheck(
            c.Id, "VPN tunnel", status,
            Expected: "egress via the VPN tunnel",
            Actual: vpnConnected is null ? null : vpnConnected.Value ? "connected" : "not connected",
            Detail: detail));
    }

    private static (IntegrityMode Mode, string Reason) DetermineMode(bool isDevelopment, bool inContainer, bool provisioned)
    {
        if (isDevelopment)
        {
            return (IntegrityMode.ReportOnly, "Development environment - integrity is report-only (never fail-closed).");
        }

        if (!inContainer)
        {
            return (IntegrityMode.ReportOnly,
                "Running outside a launcher-managed container - integrity is report-only (never fail-closed).");
        }

        if (!provisioned)
        {
            return (IntegrityMode.ReportOnly,
                "This container was not provisioned with an integrity record + key (it predates tamper-evidence, or " +
                $"was started outside the launcher) - report-only. Relaunch with `{ProductName.Command}` to enable enforcement.");
        }

        return (IntegrityMode.Enforced, "A genuine mismatch will fail the app closed and stop the container.");
    }

    private static void AppendComponentChecks(List<IntegrityCheck> checks, IntegrityComponentModel c, IRuntimeShapeProbe probe)
    {
        // Image digest cross-check: the HMAC-verified record's digest vs the digest the launcher injected as env.
        var injectedDigest = probe.GetEnv(IntegrityCanonical.DigestEnvVar);
        checks.Add(string.IsNullOrWhiteSpace(injectedDigest)
            ? Skipped(c.Id, "Image digest", c.ImageDigest, "no digest was injected into this container")
            : Compare(c.Id, "Image digest", c.ImageDigest, injectedDigest,
                "the pinned image digest the launcher injected matches the signed record"));

        // Environment shape: every env the launcher recorded is compared to what the app actually observes.
        foreach (var kv in c.Env.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            checks.Add(Compare(c.Id, $"env {kv.Key}", kv.Value, probe.GetEnv(kv.Key),
                $"the image-baked/launcher-set {kv.Key} matches the recorded value"));
        }

        // Non-root uid from /proc/self/status.
        var uid = probe.TryGetEffectiveUid();
        checks.Add(uid is null
            ? Skipped(c.Id, "Non-root uid", c.NonRootUid.ToString(), "the effective uid could not be read (/proc unavailable)")
            : Compare(c.Id, "Non-root uid", c.NonRootUid.ToString(), uid.Value.ToString(),
                "the process runs as the recorded non-root uid"));

        // Writable mounts.
        foreach (var mount in c.Mounts)
        {
            checks.Add(EvaluateMount(c.Id, mount, probe.ObserveMount(mount.Path)));
        }

        // Container port (observable via ASPNETCORE_HTTP_PORTS); loopback is launcher-verified, not in-container observable.
        var ports = probe.GetEnv("ASPNETCORE_HTTP_PORTS");
        checks.Add(string.IsNullOrWhiteSpace(ports)
            ? Skipped(c.Id, "Container port", c.ContainerPort.ToString(), "ASPNETCORE_HTTP_PORTS is unset")
            : Compare(c.Id, "Container port", c.ContainerPort.ToString(), ports,
                "the app serves on the recorded container port"));

        checks.Add(new IntegrityCheck(
            c.Id, "Loopback publish", IntegrityCheckStatus.Skipped,
            Expected: c.Loopback ? "127.0.0.1" : "any",
            Actual: null,
            Detail: "Loopback-only publish is verified host-side by the launcher and recorded here; it is not " +
                "observable from inside the container, so it is shown for provenance, not re-checked."));
    }

    private static IntegrityCheck EvaluateMount(string componentId, IntegrityMountModel mount, MountObservation obs)
    {
        if (!obs.Exists)
        {
            return new IntegrityCheck(componentId, $"mount {mount.Path}", IntegrityCheckStatus.Fail,
                Expected: $"{mount.Kind}, writable", Actual: "missing",
                Detail: $"The expected {mount.Kind} mount {mount.Path} is not present.");
        }

        if (!obs.Writable)
        {
            return new IntegrityCheck(componentId, $"mount {mount.Path}", IntegrityCheckStatus.Fail,
                Expected: $"{mount.Kind}, writable", Actual: "not writable",
                Detail: $"The expected {mount.Kind} mount {mount.Path} exists but is not writable.");
        }

        // fstype is a genuine comparison only when readable (tmpfs on /tmp). An unknown fstype (/proc unreadable)
        // is not treated as a failure - do-no-harm.
        if (string.Equals(mount.Kind, "tmpfs", StringComparison.Ordinal) &&
            obs.FsType is { } fs && !string.Equals(fs, "tmpfs", StringComparison.Ordinal))
        {
            return new IntegrityCheck(componentId, $"mount {mount.Path}", IntegrityCheckStatus.Fail,
                Expected: "tmpfs", Actual: fs,
                Detail: $"{mount.Path} was expected to be a tmpfs but is {fs}.");
        }

        var detail = obs.FsType is { } known
            ? $"{mount.Path} is present, writable and of type {known}."
            : $"{mount.Path} is present and writable.";
        return new IntegrityCheck(componentId, $"mount {mount.Path}", IntegrityCheckStatus.Pass,
            Expected: $"{mount.Kind}, writable", Actual: obs.FsType ?? "writable", detail);
    }

    private static IntegrityCheck Compare(string componentId, string name, string expected, string? actual, string passDetail) =>
        string.Equals(expected, actual, StringComparison.Ordinal)
            ? new IntegrityCheck(componentId, name, IntegrityCheckStatus.Pass, expected, actual, $"Matched - {passDetail}.")
            : new IntegrityCheck(componentId, name, IntegrityCheckStatus.Fail, expected, actual ?? "(unset)",
                $"Mismatch - expected '{expected}' but observed '{actual ?? "(unset)"}'.");

    private static IntegrityCheck Skipped(string componentId, string name, string expected, string why) =>
        new(componentId, name, IntegrityCheckStatus.Skipped, expected, null, $"Skipped - {why}.");
}
