using System.Net;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Infrastructure.Browser;

/// <summary>
/// Where the agent's browser may go (backlog 159). The browser runs in the studio's own container, so a page can
/// reach whatever the studio can: the studio itself on loopback, the host bridge and the app runner on the host's
/// address. Every one of those is token-protected and the studio refuses browsers outright (ADR 0017), and still a
/// page has no business there, so every request the browser makes is checked here, subresources included.
/// <para>
/// What it refuses: any scheme but http, https, data and blob; a host that is loopback, unspecified or link-local as
/// written; <c>localhost</c> and its subdomains; wslc's host alias (<see cref="AgentHostAlias"/>, backlog 187), which
/// reaches every service on this PC's loopback; and the exact host and port of the bridge and the app runner. What
/// it cannot see, and says so rather than pretending: a DNS name that RESOLVES to loopback. The endpoints there are
/// token-protected either way; the v2 sibling browser container (docs/v2.md section 6) takes the whole question away.
/// </para>
/// </summary>
public sealed class BrowserUrlPolicy
{
    private readonly HashSet<string> _deniedEndpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _allowLoopback;

    /// <param name="deniedEndpoints">Base URLs whose host and port are refused (the bridge's, the app runner's).</param>
    /// <param name="allowLoopback">True only in a test that serves its pages on loopback.</param>
    public BrowserUrlPolicy(IEnumerable<string?> deniedEndpoints, bool allowLoopback = false)
    {
        _allowLoopback = allowLoopback;
        foreach (var endpoint in deniedEndpoints)
        {
            if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            {
                _deniedEndpoints.Add(uri.Host + ":" + uri.Port);
            }
        }
    }

    /// <summary>Why the agent may not OPEN this URL, or null when it may. Only http and https are opened.</summary>
    public string? RefuseToOpen(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return "That is not an absolute URL. Give one that starts with http:// or https://.";
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return $"The browser opens http and https pages only, not {uri.Scheme}: addresses.";
        }

        return RefuseRequest(uri.AbsoluteUri);
    }

    /// <summary>Why a request the page makes is refused, or null when it may go.</summary>
    public string? RefuseRequest(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "not an absolute URL";
        }

        if (uri.Scheme is "data" or "blob")
        {
            return null;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return $"the {uri.Scheme}: scheme is not allowed";
        }

        var host = uri.Host.Trim('[', ']');
        if (_deniedEndpoints.Contains(uri.Host + ":" + uri.Port))
        {
            return "that address belongs to the studio's own host services";
        }

        // Before the loopback switch: no test setting turns this off (backlog 187).
        if (IsHostAlias(host))
        {
            return $"{AgentHostAlias.Name} ({AgentHostAlias.Address}) is wslc's host alias: {AgentHostAlias.Why}";
        }

        if (_allowLoopback)
        {
            return null;
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return "localhost is the studio's own container";
        }

        if (IPAddress.TryParse(host, out var address))
        {
            if (IPAddress.IsLoopback(address)
                || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
                || (address.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(address.MapToIPv4())))
            {
                return "a loopback address is the studio's own container";
            }

            var bytes = address.GetAddressBytes();
            if (address.IsIPv6LinkLocal || (bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254))
            {
                return "a link-local address is not a web page";
            }
        }

        return null;
    }

    private static bool IsHostAlias(string host)
    {
        if (host.TrimEnd('.').Equals(AgentHostAlias.Name, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var alias = IPAddress.Parse(AgentHostAlias.Address);
        return IPAddress.TryParse(host, out var address)
            && (address.Equals(alias) || (address.IsIPv4MappedToIPv6 && address.MapToIPv4().Equals(alias)));
    }
}
