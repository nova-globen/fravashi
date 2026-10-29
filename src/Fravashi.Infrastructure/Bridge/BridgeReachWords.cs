using System.Globalization;
using System.Net;
using Fravashi.Domain;

namespace Fravashi.Infrastructure.Bridge;

/// <summary>
/// What the studio says when it cannot reach fravashi.exe's host bridge from inside its container (backlog 252), in one
/// place so the studio log, Settings &gt; General &gt; Overview and <c>fravashi status</c> (which relays the same text through
/// <c>bridge-reach.txt</c>) all say the same. A bridge that answers is said as nothing at all.
/// <para>
/// Each cause is worded only as far as it was measured. A connection that TIMED OUT to the address fravashi.exe listens
/// on for Docker and Podman (the WSL gateway, an IP address) can mean either of two things that look identical from
/// inside the container: fravashi.exe not running (closed, restarted or updated), or Windows Firewall, where an inbound
/// Block rule left by an unanswered firewall prompt drops the connection (what the test VM showed on 2026-10-08,
/// with two such rules named fravashi.exe). Backlog 278 measured four timeouts on that VM, under rootless Podman, each
/// at a moment fravashi.exe had been stopped; it did not measure why a closed port times out rather than refusing (the
/// likely reason is Windows' own default stealth behaviour, which drops rather than refuses a connection to a
/// closed port, but that is inferred, not measured, and Docker was not measured either). Backlog 278 found the
/// words naming the less likely cause first: fravashi.exe not running is named first now, and
/// the firewall's detailed advice (the missing rule, the one command) is kept for a bridge that STAYS unreachable
/// past <see cref="FirewallAdviceAfter"/>, using the duration the monitor already tracks rather than asking the
/// host anything new; that threshold is a choice, not a measured restart time, made so an ordinary check does not
/// reach the firewall advice while a genuinely blocked fravashi.exe, which never answers, always does. A REFUSED
/// connection means nothing listens, which is fravashi.exe not running, and is never worded as the firewall. On wslc the
/// container reaches fravashi.exe through a forwarded host NAME on loopback, where the firewall is not in the way, so a
/// timeout there is not blamed on it either.
/// </para>
/// </summary>
public static class BridgeReachWords
{
    /// <summary>
    /// Where the per-user installer puts fravashi.exe (the earlier executable lived in a former folder before the rename), written so
    /// PowerShell expands it on the person's machine.
    /// </summary>
    public const string InstalledDesktopPath = "$env:LOCALAPPDATA\\Programs\\Fravashi\\fravashi.exe";

    /// <summary>
    /// How long a timed-out connection must have lasted before the words give the firewall's detailed advice (the
    /// missing rule, the one command) rather than just naming it as the less likely of the two possibilities. A
    /// choice, not a measured restart or update time: picked so that it does not fire on an ordinary check, while a
    /// genuinely blocked fravashi.exe, which never answers, always stays unreachable past it.
    /// </summary>
    public static readonly TimeSpan FirewallAdviceAfter = TimeSpan.FromMinutes(2);

    private const string WithoutIt =
        " Until it answers, no workspace folder can be opened or synced, and an agent conversation has no folder to work in.";

    /// <summary>The words for one probe's outcome, or null when the bridge answered.</summary>
    /// <param name="outcome">What the probe found.</param>
    /// <param name="bridge">The bridge address the container was given.</param>
    /// <param name="ports">The host ports fravashi.exe listens on for the container (bridge, app runner, upgrade).</param>
    /// <param name="reason">The failure's own words, for an outcome the studio has no explanation for.</param>
    /// <param name="unreachableFor">
    /// How long the bridge has failed to answer, as the monitor already tracks it. Only changes a timed-out
    /// connection's wording: past <see cref="FirewallAdviceAfter"/> it adds the firewall's detailed advice.
    /// </param>
    public static string? For(
        BridgeReachOutcome outcome,
        Uri bridge,
        IReadOnlyList<int> ports,
        string? reason = null,
        TimeSpan unreachableFor = default)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(ports);
        var at = bridge.GetLeftPart(UriPartial.Authority);
        return outcome switch
        {
            BridgeReachOutcome.Answered => null,
            BridgeReachOutcome.Refused =>
                $"{ProductName.Short} cannot reach {ProductName.Executable}'s host bridge at {at}: nothing is listening there, so {ProductName.Executable} is most "
                + $"likely not running (it was exited from the tray, or it stopped). Start {ProductName.Short} again from the Start "
                + "menu." + WithoutIt,
            BridgeReachOutcome.TimedOut when IPAddress.TryParse(bridge.Host, out var address) && !IPAddress.IsLoopback(address) =>
                Firewalled(at, ports, unreachableFor),
            BridgeReachOutcome.TimedOut =>
                $"{ProductName.Short} cannot reach {ProductName.Executable}'s host bridge at {at}: it did not answer in time. Check that {ProductName.Executable} is "
                + $"running (the {ProductName.Short} icon near the clock), and start {ProductName.Short} again if it is not." + WithoutIt,
            _ =>
                $"{ProductName.Short} cannot reach {ProductName.Executable}'s host bridge at {at}: {Sentence(reason)}" + WithoutIt,
        };
    }

    /// <summary>
    /// The one command that adds the missing rule, for a PowerShell opened as administrator. It removes the Block rules
    /// a dismissed prompt leaves first, because a Block rule wins over any allow rule; then it allows fravashi.exe in on
    /// exactly these ports. The measured fix of 2026-10-08, in one line.
    /// </summary>
    public static string FirewallCommand(IReadOnlyList<int> ports)
    {
        ArgumentNullException.ThrowIfNull(ports);
        var list = string.Join(",", ports.Select(p => p.ToString(CultureInfo.InvariantCulture)));
        return $"Get-NetFirewallRule -DisplayName '{ProductName.Executable}' -ErrorAction SilentlyContinue | Where-Object Action -eq 'Block' "
            + $"| Remove-NetFirewallRule; New-NetFirewallRule -DisplayName '{ProductName.Short} {ProductName.Executable}' -Direction Inbound "
            + $"-Action Allow -Protocol TCP -LocalPort {list} -Program \"{InstalledDesktopPath}\" -Profile Any";
    }

    private static string Firewalled(string at, IReadOnlyList<int> ports, TimeSpan unreachableFor)
    {
        var likely =
            $"{ProductName.Short} cannot reach {ProductName.Executable}'s host bridge at {at}: the connection timed out from inside the container. "
            + $"The likely cause is that {ProductName.Executable} is not running right now (it was closed, restarted or updated); start "
            + $"{ProductName.Short} again from the Start menu if it is not already open.";

        if (unreachableFor < FirewallAdviceAfter)
        {
            return likely
                + $" If {ProductName.Executable} is running and this does not clear up within a couple of minutes, Windows Firewall is "
                + $"the other possibility: it may be blocking {ProductName.Executable}. The fix is added once the bridge has stayed "
                + "unreachable long enough that the firewall becomes the more likely cause." + WithoutIt;
        }

        var list = string.Join(", ", ports.Select(p => p.ToString(CultureInfo.InvariantCulture)));
        return likely
            + $" This has gone on long enough that Windows Firewall is now the more likely cause, if {ProductName.Executable} is in fact "
            + $"running: the first time {ProductName.Executable} listened, Windows asked whether to allow it, and a prompt that was "
            + $"dismissed or left unanswered leaves inbound Block rules named {ProductName.Executable}, which win over any allow rule. "
            + $"What is missing is an inbound allow rule for {ProductName.Executable} on TCP port(s) {list}. To add it, run this one "
            + $"command in a PowerShell opened as administrator:   {FirewallCommand(ports)}   {ProductName.Short} notices within "
            + "two minutes; nothing needs restarting." + WithoutIt;
    }

    private static string Sentence(string? reason)
    {
        var text = string.IsNullOrWhiteSpace(reason) ? "the connection failed" : reason.Trim();
        return text.EndsWith('.') ? text : text + ".";
    }
}

/// <summary>What one question to the host bridge found (backlog 252).</summary>
public enum BridgeReachOutcome
{
    /// <summary>The bridge answered, with any status: something is there and the path to it is open.</summary>
    Answered,

    /// <summary>The connection was refused: nothing listens at that address.</summary>
    Refused,

    /// <summary>
    /// No answer before the budget ran out: packets dropped on the way, either fravashi.exe not running (a closed port,
    /// likely dropped rather than refused by Windows' default stealth behaviour) or a firewall Block rule. The two
    /// look the same from inside the container; how long the bridge has stayed unreachable only shifts which the
    /// words call more likely, it never tells them apart for certain.
    /// </summary>
    TimedOut,

    /// <summary>Anything else (a name that does not resolve, an unreachable network), carried with its own words.</summary>
    Failed,
}
