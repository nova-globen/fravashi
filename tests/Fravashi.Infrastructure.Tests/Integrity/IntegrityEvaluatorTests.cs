using Fravashi.Application.Integrity;
using Fravashi.Infrastructure.Integrity;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Integrity;

/// <summary>
/// The pure F3 decision function (ADR 0007) - BOTH directions, exhaustively. A legitimate shape returns all-pass
/// and is NOT fail-closed; a tampered shape (variant, digest, uid, mount, or a broken HMAC) fails, and only under
/// enforcement is it fail-closed. Report-only downgrades (outside a container, Development, unprovisioned) never
/// fail closed no matter how wrong the shape is - the do-no-harm guardrail.
/// </summary>
public sealed class IntegrityEvaluatorTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static IntegrityRecordModel Record() => new()
    {
        SchemaVersion = 1,
        Runtime = "wslc",
        RecordedUtc = Now,
        Components =
        [
            new IntegrityComponentModel
            {
                Id = "studio",
                Kind = "app",
                ImageRef = "fravashi:latest",
                ImageDigest = "sha256:good",
                Variant = "full",
                Version = "1.2.0",
                ContainerName = "fravashi",
                HostPort = 9090,
                ContainerPort = 8080,
                Loopback = true,
                NonRootUid = 1000,
                Mounts =
                [
                    new IntegrityMountModel("/data", "bind"),
                    new IntegrityMountModel("/repos", "volume"),
                    new IntegrityMountModel("/tmp", "tmpfs"),
                ],
                Env = new Dictionary<string, string>
                {
                    ["FRAVASHI_DATA_DIR"] = "/data",
                    ["FRAVASHI_REPO_ROOT"] = "/repos",
                    ["FRAVASHI_IN_CONTAINER"] = "1",
                    ["FRAVASHI_VARIANT"] = "full",
                    ["FRAVASHI_VERSION"] = "1.2.0",
                },
            },
        ],
    };

    private static IntegrityRecordModel Signed(IntegrityRecordModel r) => r with { Hmac = IntegrityCanonical.Sign(r, Key) };

    /// <summary>A probe whose observed shape exactly matches <see cref="Record"/> (the legitimate container).</summary>
    private static FakeShapeProbe LegitProbe() => new()
    {
        Env =
        {
            ["FRAVASHI_IN_CONTAINER"] = "1",
            ["FRAVASHI_INTEGRITY_KEY"] = Convert.ToBase64String(Key),
            ["FRAVASHI_IMAGE_DIGEST"] = "sha256:good",
            ["FRAVASHI_DATA_DIR"] = "/data",
            ["FRAVASHI_REPO_ROOT"] = "/repos",
            ["FRAVASHI_VARIANT"] = "full",
            ["FRAVASHI_VERSION"] = "1.2.0",
            ["ASPNETCORE_HTTP_PORTS"] = "8080",
        },
        Uid = 1000,
        Mounts =
        {
            ["/data"] = new MountObservation(true, true, "ext4"),
            ["/repos"] = new MountObservation(true, true, "ext4"),
            ["/tmp"] = new MountObservation(true, true, "tmpfs"),
        },
    };

    [Fact]
    public void Legit_shape_in_container_passes_every_check_and_is_not_fail_closed()
    {
        var status = IntegrityEvaluator.Evaluate(Signed(Record()), Key, LegitProbe(), isDevelopment: false, Now);

        status.Mode.Should().Be(IntegrityMode.Enforced);
        status.FailCount.Should().Be(0);
        status.IsFailClosed.Should().BeFalse();
        status.PassCount.Should().BeGreaterThan(5);
        status.Checks.Should().Contain(c => c.Name == "Record signature" && c.Status == IntegrityCheckStatus.Pass);

        // Backlog 277: the card prefixes this with its own "Enforced" label, so the reason itself must not
        // repeat the word (it used to start "Enforced - Enforced - ...").
        status.ModeReason.Should().NotContain("Enforced");
        status.ModeReason.Should().MatchRegex("^[A-Z].*\\.$");
    }

    [Fact]
    public void A_wrong_variant_fails_and_is_fail_closed_under_enforcement()
    {
        var probe = LegitProbe();
        probe.Env["FRAVASHI_VARIANT"] = "corp-vpn";

        var status = IntegrityEvaluator.Evaluate(Signed(Record()), Key, probe, isDevelopment: false, Now);

        status.IsFailClosed.Should().BeTrue();
        status.Checks.Should().Contain(c => c.Name == "env FRAVASHI_VARIANT" && c.Status == IntegrityCheckStatus.Fail);
    }

    [Fact]
    public void A_wrong_digest_fails()
    {
        var probe = LegitProbe();
        probe.Env["FRAVASHI_IMAGE_DIGEST"] = "sha256:swapped";

        var status = IntegrityEvaluator.Evaluate(Signed(Record()), Key, probe, isDevelopment: false, Now);

        status.Checks.Should().Contain(c => c.Name == "Image digest" && c.Status == IntegrityCheckStatus.Fail);
        status.IsFailClosed.Should().BeTrue();
    }

    [Fact]
    public void A_root_uid_fails_the_non_root_check()
    {
        var probe = LegitProbe();
        probe.Uid = 0;

        var status = IntegrityEvaluator.Evaluate(Signed(Record()), Key, probe, isDevelopment: false, Now);

        status.Checks.Should().Contain(c => c.Name == "Non-root uid" && c.Status == IntegrityCheckStatus.Fail);
        status.IsFailClosed.Should().BeTrue();
    }

    [Fact]
    public void An_unreadable_uid_is_skipped_not_failed()
    {
        var probe = LegitProbe();
        probe.Uid = null;

        var status = IntegrityEvaluator.Evaluate(Signed(Record()), Key, probe, isDevelopment: false, Now);

        status.Checks.Should().Contain(c => c.Name == "Non-root uid" && c.Status == IntegrityCheckStatus.Skipped);
        status.IsFailClosed.Should().BeFalse();
    }

    [Fact]
    public void A_missing_data_mount_fails()
    {
        var probe = LegitProbe();
        probe.Mounts["/data"] = new MountObservation(false, false, null);

        var status = IntegrityEvaluator.Evaluate(Signed(Record()), Key, probe, isDevelopment: false, Now);

        status.Checks.Should().Contain(c => c.Name == "mount /data" && c.Status == IntegrityCheckStatus.Fail);
        status.IsFailClosed.Should().BeTrue();
    }

    [Fact]
    public void A_non_writable_repos_mount_fails()
    {
        var probe = LegitProbe();
        probe.Mounts["/repos"] = new MountObservation(true, false, "ext4");

        var status = IntegrityEvaluator.Evaluate(Signed(Record()), Key, probe, isDevelopment: false, Now);

        status.Checks.Should().Contain(c => c.Name == "mount /repos" && c.Status == IntegrityCheckStatus.Fail);
    }

    [Fact]
    public void A_tmp_that_is_not_tmpfs_fails_when_the_fstype_is_known()
    {
        var probe = LegitProbe();
        probe.Mounts["/tmp"] = new MountObservation(true, true, "ext4");

        var status = IntegrityEvaluator.Evaluate(Signed(Record()), Key, probe, isDevelopment: false, Now);

        status.Checks.Should().Contain(c => c.Name == "mount /tmp" && c.Status == IntegrityCheckStatus.Fail);
    }

    [Fact]
    public void A_tmp_with_unknown_fstype_still_passes_on_writable()
    {
        var probe = LegitProbe();
        probe.Mounts["/tmp"] = new MountObservation(true, true, null);

        var status = IntegrityEvaluator.Evaluate(Signed(Record()), Key, probe, isDevelopment: false, Now);

        status.Checks.Should().Contain(c => c.Name == "mount /tmp" && c.Status == IntegrityCheckStatus.Pass);
    }

    [Fact]
    public void A_broken_hmac_fails_as_the_single_signature_check()
    {
        var tampered = Signed(Record()) with
        {
            Components = [Record().Components[0] with { Variant = "evil" }],
        };

        var status = IntegrityEvaluator.Evaluate(tampered, Key, LegitProbe(), isDevelopment: false, Now);

        status.Checks.Should().ContainSingle();
        status.Checks[0].Name.Should().Be("Record signature");
        status.Checks[0].Status.Should().Be(IntegrityCheckStatus.Fail);
        status.IsFailClosed.Should().BeTrue();
    }

    [Fact]
    public void Development_is_report_only_even_with_a_wrong_shape()
    {
        var probe = LegitProbe();
        probe.Uid = 0; // a real mismatch

        var status = IntegrityEvaluator.Evaluate(Signed(Record()), Key, probe, isDevelopment: true, Now);

        status.Mode.Should().Be(IntegrityMode.ReportOnly);
        status.HasFailure.Should().BeTrue();
        status.IsFailClosed.Should().BeFalse("report-only never fails closed");
    }

    [Fact]
    public void Outside_a_container_is_report_only()
    {
        var probe = LegitProbe();
        probe.Env["FRAVASHI_IN_CONTAINER"] = "0";

        var status = IntegrityEvaluator.Evaluate(Signed(Record()), Key, probe, isDevelopment: false, Now);

        status.Mode.Should().Be(IntegrityMode.ReportOnly);
        status.ModeReason.Should().Contain("outside");
    }

    [Fact]
    public void An_unprovisioned_container_no_record_is_report_only_and_locks_nothing()
    {
        var probe = LegitProbe(); // in-container but...
        var status = IntegrityEvaluator.Evaluate(record: null, key: null, probe, isDevelopment: false, Now);

        status.Mode.Should().Be(IntegrityMode.ReportOnly);
        status.Provisioned.Should().BeFalse();
        status.IsFailClosed.Should().BeFalse();
        status.Checks.Should().BeEmpty();
    }

    // ---- F5 VPN sidecar component (VPN4) ----

    private static IntegrityRecordModel WithSidecar(IntegrityRecordModel r) => r with
    {
        Components =
        [
            .. r.Components,
            new IntegrityComponentModel
            {
                Id = "vpn-sidecar",
                Kind = "sidecar",
                ImageRef = "qmcgaw/gluetun:latest",
                ImageDigest = "sha256:vpn0000",
                ContainerName = "fravashi-vpn",
            },
        ],
    };

    [Fact]
    public void A_sidecar_component_adds_its_rows_and_the_studio_still_passes()
    {
        var record = Signed(WithSidecar(Record()));

        var status = IntegrityEvaluator.Evaluate(record, Key, LegitProbe(), isDevelopment: false, Now, vpnConnected: true);

        // The integrity surface now INCLUDES the sidecar component (VPN4).
        status.Checks.Should().Contain(c => c.ComponentId == "vpn-sidecar");
        status.Checks.Should().Contain(c => c.ComponentId == "vpn-sidecar" && c.Name == "Sidecar image digest");
        // The studio's own checks are unaffected and it is not fail-closed.
        status.IsFailClosed.Should().BeFalse();
        status.Checks.Should().Contain(c => c.ComponentId == "studio" && c.Name == "Record signature" && c.Status == IntegrityCheckStatus.Pass);
    }

    [Fact]
    public void A_connected_tunnel_passes_the_sidecar_tunnel_check()
    {
        var status = IntegrityEvaluator.Evaluate(Signed(WithSidecar(Record())), Key, LegitProbe(), isDevelopment: false, Now, vpnConnected: true);

        status.Checks.Should().Contain(c => c.ComponentId == "vpn-sidecar" && c.Name == "VPN tunnel" && c.Status == IntegrityCheckStatus.Pass);
    }

    [Fact]
    public void A_down_or_killed_sidecar_tunnel_is_flagged_but_report_only_so_the_gate_can_recover()
    {
        // A killed/stopped sidecar surfaces as an unconfirmed tunnel. It is FLAGGED (not Pass) but does NOT fail the
        // app closed - connectivity is the gate's job (the studio is held at /secure-connection), and the structural
        // kill switch already removed all egress. Failing closed here would break auto-recovery on reconnect.
        var status = IntegrityEvaluator.Evaluate(Signed(WithSidecar(Record())), Key, LegitProbe(), isDevelopment: false, Now, vpnConnected: false);

        var tunnel = status.Checks.Single(c => c.ComponentId == "vpn-sidecar" && c.Name == "VPN tunnel");
        tunnel.Status.Should().Be(IntegrityCheckStatus.Skipped);
        tunnel.Actual.Should().Be("not connected");
        status.IsFailClosed.Should().BeFalse();
    }

    /// <summary>A programmable observed shape.</summary>
    private sealed class FakeShapeProbe : IRuntimeShapeProbe
    {
        public Dictionary<string, string> Env { get; } = new(StringComparer.Ordinal);
        public int? Uid { get; set; }
        public Dictionary<string, MountObservation> Mounts { get; } = new(StringComparer.Ordinal);

        public string? GetEnv(string name) => Env.TryGetValue(name, out var v) ? v : null;
        public int? TryGetEffectiveUid() => Uid;
        public MountObservation ObserveMount(string path) =>
            Mounts.TryGetValue(path, out var m) ? m : new MountObservation(false, false, null);
    }
}
