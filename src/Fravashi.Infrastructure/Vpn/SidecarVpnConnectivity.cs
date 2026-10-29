using System.Text.Json;
using Fravashi.Application.Vpn;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Fravashi.Infrastructure.Vpn;

/// <summary>
/// The real F5 VPN-connectivity probe (ADR 0008 - VPN6), replacing the T7 <c>UnconfiguredVpnConnectivity</c>
/// placeholder. The studio joins the VPN sidecar's network namespace, so the sidecar's HTTP control server is
/// reachable over the shared loopback; this probe GETs <c>{control}/v1/publicip/ip</c> (gluetun's control API) and
/// treats a returned public IP as proof the tunnel is up and egress is flowing through the VPN exit. Because the
/// sidecar's default-drop firewall (the structural kill switch) blocks everything except the tunnel, any successful
/// egress is necessarily through the VPN - so a confirmed public IP is a sound "connected" signal.
/// <para>
/// Never claims a connection it cannot prove (assertion-gap rule): any non-200, unreachable control server, or
/// missing IP yields "not connected" with an honest detail. State is refreshed by
/// <see cref="VpnConnectivityRefreshService"/> and read synchronously by the gate + integrity surface.
/// </para>
/// </summary>
public sealed class SidecarVpnConnectivity : IVpnConnectivity
{
    /// <summary>Config key for the sidecar control-server base URL (launcher-injected in VPN mode).</summary>
    public const string ControlUrlKey = "Vpn:ControlServerUrl";

    /// <summary>Default control-server URL on the shared netns loopback (gluetun's default port).</summary>
    public const string DefaultControlUrl = "http://127.0.0.1:8000";

    private readonly Func<HttpClient> _clientFactory;
    private readonly string _baseUrl;
    private readonly ILogger<SidecarVpnConnectivity> _logger;
    private volatile VpnConnectivityState _state = VpnConnectivityState.NotConnected;

    public SidecarVpnConnectivity(IHttpClientFactory factory, IConfiguration configuration, ILogger<SidecarVpnConnectivity> logger)
        : this(() => factory.CreateClient(), Resolve(configuration), logger)
    {
    }

    // Test seam: a fixed client factory + explicit base URL, so the probe can be driven against a stub handler.
    public SidecarVpnConnectivity(Func<HttpClient> clientFactory, string baseUrl, ILogger<SidecarVpnConnectivity> logger)
    {
        _clientFactory = clientFactory;
        _baseUrl = baseUrl.TrimEnd('/');
        _logger = logger;
    }

    public VpnConnectivityState Current => _state;

    private static string Resolve(IConfiguration configuration)
    {
        var configured = configuration[ControlUrlKey];
        return string.IsNullOrWhiteSpace(configured) ? DefaultControlUrl : configured;
    }

    /// <summary>Probes the sidecar control server once and updates <see cref="Current"/>. Never throws.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = _clientFactory();
            if (http.Timeout == default || http.Timeout > TimeSpan.FromSeconds(10))
            {
                http.Timeout = TimeSpan.FromSeconds(5);
            }

            using var response = await http.GetAsync($"{_baseUrl}/v1/publicip/ip", cancellationToken);
            var body = response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync(cancellationToken)
                : string.Empty;
            _state = Evaluate((int)response.StatusCode, body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "VPN control server not reachable at {BaseUrl}", _baseUrl);
            _state = new VpnConnectivityState(
                IsConnected: false,
                "The VPN sidecar's control server is not reachable yet - waiting for the tunnel to come up.");
        }
    }

    /// <summary>Pure decision from an HTTP status + body - the tunnel is up iff a public IP was reported.</summary>
    public static VpnConnectivityState Evaluate(int statusCode, string body)
    {
        if (statusCode is < 200 or >= 300)
        {
            return new VpnConnectivityState(
                IsConnected: false,
                $"The VPN control server replied {statusCode} - the tunnel is not up yet.");
        }

        var ip = ParsePublicIp(body);
        return string.IsNullOrWhiteSpace(ip)
            ? new VpnConnectivityState(false, "The VPN control server has not reported a public IP yet - waiting for the tunnel.")
            : new VpnConnectivityState(true, $"Connected - the studio's egress runs through the VPN exit {ip}.");
    }

    /// <summary>Reads <c>public_ip</c> from gluetun's control-server JSON; null on any parse miss.</summary>
    public static string? ParsePublicIp(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("public_ip", out var ip) &&
                ip.ValueKind == JsonValueKind.String)
            {
                var value = ip.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        catch (JsonException)
        {
            // Not the expected JSON - treat as no IP (honest "not connected").
        }

        return null;
    }
}
