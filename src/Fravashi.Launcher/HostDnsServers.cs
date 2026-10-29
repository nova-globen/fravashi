using System.Net.NetworkInformation;

namespace Fravashi.Launcher;

/// <summary>
/// Resolves the Windows host's current DNS servers (backlog 218), the set wslc copies into a container's
/// <c>/etc/resolv.conf</c> only at container START (ADR 0029's measured behaviour: a VPN connecting or
/// disconnecting after that leaves a running container's resolver pointed at servers that may no longer
/// answer). The enumeration is injectable for tests, the same seam <see cref="WslHostAddress"/> already uses
/// for the WSL gateway address.
/// </summary>
public static class HostDnsServers
{
    /// <summary>
    /// Every distinct DNS server address configured on an up, non-loopback interface, sorted for a stable
    /// comparison. Empty when none can be read (no interface answers, or the probe throws).
    /// </summary>
    public static IReadOnlyList<string> Resolve(
        Func<IEnumerable<(bool IsUp, bool IsLoopback, IReadOnlyList<string> DnsAddresses)>>? interfaces = null)
    {
        var source = interfaces ?? DefaultInterfaces;
        var servers = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (isUp, isLoopback, dnsAddresses) in source())
        {
            if (!isUp || isLoopback)
            {
                continue;
            }

            foreach (var address in dnsAddresses)
            {
                if (!string.IsNullOrWhiteSpace(address))
                {
                    servers.Add(address);
                }
            }
        }

        return [.. servers];
    }

    /// <summary>A comparable, order-independent form of a DNS server set, for recording in <c>launcher.json</c>.</summary>
    public static string Signature(IReadOnlyList<string> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        var sorted = new SortedSet<string>(servers, StringComparer.Ordinal);
        return string.Join(",", sorted);
    }

    /// <summary>The inverse of <see cref="Signature"/>: empty for a null or blank signature.</summary>
    public static IReadOnlyList<string> Parse(string? signature) =>
        string.IsNullOrWhiteSpace(signature)
            ? []
            : signature.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IEnumerable<(bool IsUp, bool IsLoopback, IReadOnlyList<string> DnsAddresses)> DefaultInterfaces()
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
            IPInterfaceProperties properties;
            try
            {
                properties = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            var dns = properties.DnsAddresses.Select(address => address.ToString()).ToList();
            yield return (nic.OperationalStatus == OperationalStatus.Up, nic.NetworkInterfaceType == NetworkInterfaceType.Loopback, dns);
        }
    }
}
