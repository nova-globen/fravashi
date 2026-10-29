using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Fravashi.Launcher;

/// <summary>
/// Resolves the Windows host's WSL-facing IPv4 address, the ONE address a container inside the
/// WSL-based podman machine can reach the host on (spike 2026-08-01: <c>host.containers.internal</c>
/// resolves but does not connect on this stack; the vEthernet (WSL) gateway address works with an
/// unprivileged listener). The subnet is reassigned across reboots, so callers must treat the
/// value as per-boot: the launcher records what it injected and recreates the container on drift.
/// </summary>
public static class WslHostAddress
{
    /// <summary>
    /// The first IPv4 of an up interface whose name contains "WSL" (case-insensitive), or null when
    /// none exists (no WSL, or the machine is stopped). The enumeration is injectable for tests.
    /// </summary>
    public static string? Resolve(
        Func<IEnumerable<(string Name, bool IsUp, IReadOnlyList<string> IPv4)>>? interfaces = null)
    {
        var source = interfaces ?? DefaultInterfaces;
        foreach (var (name, isUp, ipv4) in source())
        {
            if (!isUp || !name.Contains("WSL", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ipv4.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)) is { } address)
            {
                return address;
            }
        }

        return null;
    }

    private static IEnumerable<(string Name, bool IsUp, IReadOnlyList<string> IPv4)> DefaultInterfaces()
    {
        NetworkInterface[] all;
        try
        {
            all = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            yield break;
        }

        foreach (var nic in all)
        {
            var addresses = new List<string>();
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    addresses.Add(unicast.Address.ToString());
                }
            }

            yield return (nic.Name, nic.OperationalStatus == OperationalStatus.Up, addresses);
        }
    }
}
