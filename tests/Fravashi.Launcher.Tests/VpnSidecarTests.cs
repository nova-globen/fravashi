using Fravashi.Launcher;
using Fravashi.Launcher.Vpn;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests;

/// <summary>
/// F5 / ADR 0008 (VPN1 - VPN4): the VPN sidecar run-arg construction (capabilities, device, tiny memory cap, the
/// studio port published on the sidecar, provider config, and - critically - the credential passed by NAME only so
/// no secret value ever reaches an argument list), the studio's netns-join in VPN mode, the per-runtime support
/// flags (Docker/Podman yes, wslc no - the measured T9 capability gap), and the plan that composes it all.
/// </summary>
public sealed class VpnSidecarTests
{
    private static readonly VpnConfig Vpn = new()
    {
        Enabled = true,
        Image = "qmcgaw/gluetun:latest",
        ContainerName = "fravashi-vpn",
        Provider = "nordvpn",
        Type = "openvpn",
        ServerCountries = "Sweden",
        MemoryLimit = "128M",
        SecretEnv = ["OPENVPN_USER", "OPENVPN_PASSWORD"],
    };

    // ---- sidecar run args (VPN1 - VPN3) ----

    [Fact]
    public void SidecarArgs_GrantTheCapabilitiesAndDevice_ACapVpnClientNeeds()
    {
        var args = new DockerRuntime().BuildVpnRunArgs(VpnSidecarPlan.BuildRunSpec(Vpn, 9090, 8080));

        args.Should().ContainInConsecutiveOrder("--cap-add", "NET_ADMIN");
        args.Should().ContainInConsecutiveOrder("--device", "/dev/net/tun");
        args.Should().ContainInConsecutiveOrder("--memory", "128M");
        args.Should().EndWith("qmcgaw/gluetun:latest");
    }

    [Fact]
    public void SidecarArgs_PublishTheStudioPortLoopbackOnly_BecauseTheStudioJoinsThisNetns()
    {
        var args = new DockerRuntime().BuildVpnRunArgs(VpnSidecarPlan.BuildRunSpec(Vpn, 9090, 8080));

        // The studio's host:container port is published HERE (loopback), not on the studio (it shares this netns).
        args.Should().ContainInConsecutiveOrder("--publish", "127.0.0.1:9090:8080");
    }

    [Fact]
    public void SidecarArgs_TurnOnTheFirewallKillSwitch_AndTheControlServerTheStudioProbes()
    {
        var args = new DockerRuntime().BuildVpnRunArgs(VpnSidecarPlan.BuildRunSpec(Vpn, 9090, 8080));

        // VPN2/VPN3: gluetun's built-in default-drop firewall = the structural kill switch.
        args.Should().ContainInConsecutiveOrder("--env", "FIREWALL=on");
        args.Should().ContainInConsecutiveOrder("--env", "VPN_SERVICE_PROVIDER=nordvpn");
        args.Should().ContainInConsecutiveOrder("--env", "VPN_TYPE=openvpn");
        args.Should().ContainInConsecutiveOrder("--env", "SERVER_COUNTRIES=Sweden");
        args.Should().ContainInConsecutiveOrder("--env", "HTTP_CONTROL_SERVER_ADDRESS=:8000");
    }

    [Fact]
    public void SidecarArgs_OpenTheControlServerRouteTheProbeReads_SoTheGateClears()
    {
        var args = new DockerRuntime().BuildVpnRunArgs(VpnSidecarPlan.BuildRunSpec(Vpn, 9090, 8080));

        // VPN6: gluetun v3.39.1+ gates /v1/publicip/ip behind auth (401) by default, which would keep the studio's
        // probe reading a live tunnel as "not connected". The env-only default role makes that route answer again.
        args.Should().ContainInConsecutiveOrder("--env", """HTTP_CONTROL_SERVER_AUTH_DEFAULT_ROLE={"auth":"none"}""");
    }

    [Fact]
    public void SidecarArgs_OmitTheAuthRole_WhenTheOwnerClearsIt_LeavingGluetunsDefault()
    {
        var spec = VpnSidecarPlan.BuildRunSpec(Vpn with { ControlServerAuthDefaultRole = "" }, 9090, 8080);
        var args = new DockerRuntime().BuildVpnRunArgs(spec);

        args.Should().NotContain(a => a.StartsWith("HTTP_CONTROL_SERVER_AUTH_DEFAULT_ROLE", StringComparison.Ordinal));
    }

    [Fact]
    public void SidecarArgs_PassTheCredentialByNameOnly_NeverAValue_SoItCannotLeak()
    {
        var args = new DockerRuntime().BuildVpnRunArgs(VpnSidecarPlan.BuildRunSpec(Vpn, 9090, 8080));

        // The secret vars are forwarded as `--env NAME` (no `=value`): the runtime reads the value from the
        // launcher's own environment. The name appears; a value must NOT (it never enters the arg list).
        args.Should().ContainInConsecutiveOrder("--env", "OPENVPN_USER");
        args.Should().ContainInConsecutiveOrder("--env", "OPENVPN_PASSWORD");
        args.Should().NotContain(a => a.StartsWith("OPENVPN_USER=", StringComparison.Ordinal));
        args.Should().NotContain(a => a.StartsWith("OPENVPN_PASSWORD=", StringComparison.Ordinal));
    }

    [Fact]
    public void PodmanSidecarArgs_MatchDocker()
    {
        var spec = VpnSidecarPlan.BuildRunSpec(Vpn, 9090, 8080);
        new PodmanRuntime().BuildVpnRunArgs(spec).Should().Equal(new DockerRuntime().BuildVpnRunArgs(spec));
    }

    // ---- per-runtime support (the measured wslc gap) ----

    [Fact]
    public void DockerAndPodman_SupportTheVpnSidecar_ButWslcDoesNot()
    {
        new DockerRuntime().SupportsVpnSidecar.Should().BeTrue();
        new PodmanRuntime().SupportsVpnSidecar.Should().BeTrue();
        // wslc has no --cap-add/--device (ADR 0009 matrix / measured T9), so it cannot run the sidecar.
        new WslcRuntime().SupportsVpnSidecar.Should().BeFalse();
    }

    [Fact]
    public void Wslc_RunVpnSidecar_ThrowsNotSupported_WithGuidanceTowardDockerOrPodman()
    {
        var act = () => new WslcRuntime().RunVpnSidecar(VpnSidecarPlan.BuildRunSpec(Vpn, 9090, 8080));

        act.Should().Throw<NotSupportedException>().WithMessage("*Docker or Podman*");
    }

    // ---- studio netns join in VPN mode ----

    [Fact]
    public void StudioArgs_InVpnMode_JoinTheSidecarNetns_AndDoNotPublishTheirOwnPort()
    {
        var spec = new ContainerRunSpec("fravashi", "fravashi:latest", 9090, 8080, @"C:\data",
            NetworkContainer: "fravashi-vpn", VpnControlServerUrl: "http://127.0.0.1:8000");

        foreach (var args in new[] { new DockerRuntime().BuildRunArgs(spec), new WslcRuntime().BuildRunArgs(spec) })
        {
            args.Should().ContainInConsecutiveOrder("--network", "container:fravashi-vpn");
            // A container that shares another's netns cannot also publish - the publish is on the sidecar.
            args.Should().NotContain("--publish");
            args.Should().ContainInConsecutiveOrder("--env", "Vpn__ControlServerUrl=http://127.0.0.1:8000");
            args.Should().ContainInConsecutiveOrder("--env", "Vpn__Enabled=true");
        }
    }

    [Fact]
    public void StudioArgs_WithoutVpnMode_PublishTheirOwnLoopbackPort_AndAddNoNetnsJoin()
    {
        var spec = new ContainerRunSpec("fravashi", "fravashi:latest", 9090, 8080, @"C:\data");
        var args = new DockerRuntime().BuildRunArgs(spec);

        args.Should().ContainInConsecutiveOrder("--publish", "127.0.0.1:9090:8080");
        args.Should().NotContain(a => a.StartsWith("container:", StringComparison.Ordinal));
        args.Should().NotContain(a => a.StartsWith("Vpn__", StringComparison.Ordinal));
    }

    // ---- plan (the gluetun contract + the integrity component) ----

    [Fact]
    public void Plan_BuildsTheControlServerUrl_OnTheSharedLoopback()
    {
        VpnSidecarPlan.ControlServerUrl(Vpn with { ControlServerPort = 8000 })
            .Should().Be("http://127.0.0.1:8000");
    }

    [Fact]
    public void Plan_IntegrityComponent_IsTheSidecarKind_CarryingTheImageAndDigest()
    {
        var component = VpnSidecarPlan.BuildIntegrityComponent(Vpn, "sha256:deadbeef");

        component.Id.Should().Be("vpn-sidecar");
        component.Kind.Should().Be(Fravashi.Launcher.Integrity.IntegrityComponentKinds.Sidecar);
        component.ImageRef.Should().Be("qmcgaw/gluetun:latest");
        component.ImageDigest.Should().Be("sha256:deadbeef");
        component.ContainerName.Should().Be("fravashi-vpn");
    }
}
