namespace Fravashi.Web.Services;

/// <summary>
/// The path policy for the VPN-required startup gate (F5 / I1 / ADR 0008). In a variant that requires a VPN
/// (VPN_REQUIRED on, e.g. <c>corp-vpn</c>), the app will not serve until a secure tunnel is confirmed: every page
/// request is redirected to <c>/secure-connection</c> - except the gate page itself, the liveness probe, the
/// Blazor framework/SignalR endpoints, and the Error page (backlog 281: a thrown gate-page request
/// is re-executed to StudioError.PagePath, which must stay reachable or the person gets redirected straight back
/// to the page that threw), so the gate can render and poll. In the <c>full</c> variant VPN_REQUIRED is off and
/// this gate is inert. Extracted from the Program.cs middleware so the decision is unit-tested. (T7 wires the
/// gate; T9 provides the real tunnel the gate waits on.) A static asset is let through by <see cref="StudioGates"/>,
/// which asks <see cref="StaticAssetRoutes"/> (backlog 283), never by the shape of a path.
/// </summary>
public static class VpnGate
{
    /// <summary>The waiting-for-connection page every non-exempt request is redirected to while the tunnel is down.</summary>
    public const string GatePath = "/secure-connection";

    /// <summary>True when a request path stays reachable even while the VPN gate is closed.</summary>
    public static bool IsExempt(string path) =>
        path.StartsWith(GatePath, StringComparison.OrdinalIgnoreCase) ||
        StudioError.IsPagePath(path) ||
        path.StartsWith("/healthz", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/_", StringComparison.Ordinal);
}
