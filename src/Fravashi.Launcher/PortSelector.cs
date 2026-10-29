using System.Net;
using System.Net.Sockets;

namespace Fravashi.Launcher;

/// <summary>
/// Chooses host ports. Two selections live here because two different things constrain them.
/// <list type="bullet">
/// <item><see cref="SelectFirstFree"/> picks the STUDIO port from a short fixed list
/// (<see cref="LauncherDefaults.StudioPorts"/>). The sign-in page builds its redirect URI from the served
/// base address, so the URI follows whatever port the launcher chose, and the identity provider must have
/// that exact URI registered. Backlog item 7 first decided to register the client as a native application
/// so any loopback port would be accepted, but that allowance is NOT available on the owner's provider, so
/// the launcher stays inside a list the provider can register instead. Measured 2026-09-13 against the live
/// provider (an OpenIddict 7.5.0 server): a plain authorize GET for the studio's
/// client (then registered under its former id) with <c>redirect_uri=http://localhost:9090/auth/callback/idp</c> answered
/// <c>HTTP/1.1 302 Found</c> to the login page, while the same request on 9091, 9092 and 9093 each answered
/// <c>HTTP/1.1 400 Bad Request</c> with <c>invalid_request</c> / "The specified 'redirect_uri' is not valid
/// for this client application." (OpenIddict error ID2043). OpenIddict only relaxes the port of a registered
/// loopback redirect URI for an application whose APPLICATION type is native and whose registered URI carries
/// no port; the provider's application seed has no application-type field at all (it carries a
/// CLIENT type, public or confidential, which is a different axis) and its seeder sets only
/// <c>ClientType</c> on the descriptor, so its clients are never native. So the list is what the provider
/// answers 302 for, and nothing else: the three ports were in it once before the provider knew them, and a
/// launch that used one produced exactly the redirect-mismatch page this item exists to remove. They came
/// back on 2026-09-15 only after the owner registered them and the probe answered 302 for each; see
/// <see cref="LauncherDefaults.StudioPorts"/> for that record and the procedure.</item>
/// <item><see cref="Select"/> keeps the older shape (preferred port, then any free dynamic port) for the host
/// bridge, whose port is a private detail between fravashi.exe and the container and never part of a redirect
/// URI.</item>
/// </list>
/// The availability check is injectable so the selection logic is unit-testable without touching real sockets.
/// </summary>
public static class PortSelector
{
    /// <summary>
    /// Returns the first port in <paramref name="candidates"/> that <paramref name="isAvailable"/> reports
    /// free, in list order. Throws <see cref="InvalidOperationException"/> naming every candidate when none is
    /// free: the studio must not start on a port the identity provider would refuse at sign-in.
    /// </summary>
    public static int SelectFirstFree(IReadOnlyList<int> candidates, Func<int, bool> isAvailable)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(isAvailable);
        if (candidates.Count == 0)
        {
            throw new ArgumentException("At least one candidate port is required.", nameof(candidates));
        }

        foreach (var candidate in candidates)
        {
            if (isAvailable(candidate))
            {
                return candidate;
            }
        }

        // Built from the candidates actually given, so the message cannot claim more registered ports than
        // there are (it did: it named four while the provider had one).
        throw new InvalidOperationException(LauncherDefaults.PortsBusyMessage(candidates));
    }

    /// <summary>
    /// Returns <paramref name="preferred"/> if <paramref name="isAvailable"/> reports it free; otherwise
    /// draws random candidates in the dynamic range until one is free. For ports that are NOT part of a
    /// registered redirect URI (the host bridge); the studio port uses <see cref="SelectFirstFree"/>.
    /// </summary>
    public static int Select(int preferred, Func<int, bool> isAvailable, Func<int>? nextRandom = null)
    {
        ArgumentNullException.ThrowIfNull(isAvailable);

        if (isAvailable(preferred))
        {
            return preferred;
        }

        nextRandom ??= () => Random.Shared.Next(1024, 65536);
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var candidate = nextRandom();
            if (candidate is >= 1024 and <= 65535 && isAvailable(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not find a free host port after 200 attempts.");
    }

    /// <summary>True if a TCP listener can bind the loopback port right now.</summary>
    public static bool IsTcpPortFree(int port)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener?.Stop();
        }
    }
}
