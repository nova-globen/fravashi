using System.Net;
using Fravashi.Infrastructure.Vpn;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Vpn;

/// <summary>
/// The real F5 VPN-connectivity probe (ADR 0008 - VPN6). It reads gluetun's control-server <c>/v1/publicip/ip</c>
/// over the shared netns and treats a returned public IP as proof the tunnel is up - never claiming a connection it
/// cannot prove (assertion-gap rule). Because the sidecar's default-drop firewall blocks everything except the
/// tunnel, any egress is necessarily through the VPN, so a confirmed public IP is a sound "connected" signal.
/// </summary>
public sealed class SidecarVpnConnectivityTests
{
    // ---- pure decision from an HTTP status + body ----

    [Fact]
    public void A_public_ip_from_the_control_server_means_connected_and_names_the_exit()
    {
        var state = SidecarVpnConnectivity.Evaluate(200, """{"public_ip":"203.0.113.7","region":"Sweden"}""");

        state.IsConnected.Should().BeTrue();
        state.Detail.Should().Contain("203.0.113.7");
    }

    [Fact]
    public void No_public_ip_yet_is_not_connected()
    {
        SidecarVpnConnectivity.Evaluate(200, """{"public_ip":""}""").IsConnected.Should().BeFalse();
        SidecarVpnConnectivity.Evaluate(200, "{}").IsConnected.Should().BeFalse();
    }

    [Fact]
    public void A_non_2xx_control_response_is_not_connected()
    {
        SidecarVpnConnectivity.Evaluate(503, "").IsConnected.Should().BeFalse();
    }

    [Fact]
    public void A_401_from_an_auth_gated_control_server_is_not_connected()
    {
        // gluetun v3.39.1+ gates /v1/publicip/ip behind auth by default → 401. The probe honestly reports "not
        // connected" (it cannot prove the tunnel), which is why the launcher now opens that route via the
        // HTTP_CONTROL_SERVER_AUTH_DEFAULT_ROLE env so a live tunnel answers 200 and the gate clears.
        var state = SidecarVpnConnectivity.Evaluate(401, "");

        state.IsConnected.Should().BeFalse();
        state.Detail.Should().Contain("401");
    }

    [Fact]
    public void ParsePublicIp_reads_the_field_and_tolerates_garbage()
    {
        SidecarVpnConnectivity.ParsePublicIp("""{"public_ip":"1.2.3.4"}""").Should().Be("1.2.3.4");
        SidecarVpnConnectivity.ParsePublicIp("not json").Should().BeNull();
        SidecarVpnConnectivity.ParsePublicIp("").Should().BeNull();
    }

    // ---- driven end-to-end against a stub handler ----

    [Fact]
    public async Task RefreshAsync_against_a_live_control_server_reports_connected()
    {
        var probe = new SidecarVpnConnectivity(
            () => new HttpClient(new StubHandler(HttpStatusCode.OK, """{"public_ip":"198.51.100.9"}""")),
            "http://127.0.0.1:8000",
            NullLogger<SidecarVpnConnectivity>.Instance);

        probe.Current.IsConnected.Should().BeFalse(); // never claims a connection before probing.
        await probe.RefreshAsync();

        probe.Current.IsConnected.Should().BeTrue();
        probe.Current.Detail.Should().Contain("198.51.100.9");
    }

    [Fact]
    public async Task RefreshAsync_when_the_control_server_is_unreachable_stays_not_connected()
    {
        var probe = new SidecarVpnConnectivity(
            () => new HttpClient(new ThrowingHandler()),
            "http://127.0.0.1:8000",
            NullLogger<SidecarVpnConnectivity>.Instance);

        await probe.RefreshAsync();

        probe.Current.IsConnected.Should().BeFalse();
        probe.Current.Detail.Should().Contain("not reachable");
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("connection refused");
    }
}
