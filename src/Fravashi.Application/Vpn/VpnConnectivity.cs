namespace Fravashi.Application.Vpn;

/// <summary>The app's view of the secure-tunnel state its egress is meant to flow through (F5 / ADR 0008).</summary>
/// <param name="IsConnected">True when the app's traffic is confirmed to egress through the VPN tunnel.</param>
/// <param name="Detail">A short human status ("waiting for the tunnel", "connected via …") for the gate page.</param>
public sealed record VpnConnectivityState(bool IsConnected, string Detail)
{
    /// <summary>The state before any tunnel is confirmed - the app must wait (used by the VPN-required gate).</summary>
    public static readonly VpnConnectivityState NotConnected =
        new(IsConnected: false, "Waiting for the secure VPN connection…");
}

/// <summary>
/// Reports whether the app's network egress is confirmed to run through the required VPN tunnel (F5 / ADR 0008).
/// In the <c>corp-vpn</c> variant (VPN_REQUIRED on) the startup gate blocks the app until this reports connected.
/// <para>
/// T7 wires the <b>gate</b> (driven by the <c>VpnRequired</c> feature flag) and ships a default implementation
/// that honestly reports "not connected" because there is no sidecar yet. T9 replaces the default with a real
/// probe of the VPN sidecar's shared network namespace (the structural kill switch). Keeping the seam here now
/// means the gate is real and testable today, and T9 only swaps the connectivity source.
/// </para>
/// </summary>
public interface IVpnConnectivity
{
    /// <summary>The current tunnel state.</summary>
    VpnConnectivityState Current { get; }
}

/// <summary>
/// The T7 placeholder connectivity source: always "not connected", because the VPN sidecar (T9) does not exist
/// yet. In the <c>full</c> variant VPN_REQUIRED is off so the gate never consults this; in <c>corp-vpn</c> it
/// makes the gate honestly show the waiting state until T9 provides a real tunnel probe. Never claims a
/// connection it cannot prove (assertion-gap rule).
/// </summary>
public sealed class UnconfiguredVpnConnectivity : IVpnConnectivity
{
    public VpnConnectivityState Current { get; } =
        new(IsConnected: false,
            "The VPN sidecar is not provisioned in this build yet (arriving in the VPN increment). This " +
            "locked-down variant requires a confirmed tunnel before the studio will start.");
}
