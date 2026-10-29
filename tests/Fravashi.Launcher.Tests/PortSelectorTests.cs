using Fravashi.Launcher;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests;

public sealed class PortSelectorTests
{
    [Fact]
    public void Select_ReturnsPreferred_WhenFree()
    {
        var port = PortSelector.Select(9090, isAvailable: _ => true);

        port.Should().Be(9090);
    }

    [Fact]
    public void Select_PicksRandomFree_WhenPreferredTaken()
    {
        var random = new Queue<int>([9090, 40000]); // first candidate also taken, second free

        var port = PortSelector.Select(
            9090,
            isAvailable: p => p == 40000,
            nextRandom: random.Dequeue);

        port.Should().Be(40000);
    }

    [Fact]
    public void Select_Throws_WhenNothingFree()
    {
        var act = () => PortSelector.Select(9090, isAvailable: _ => false, nextRandom: () => 5000);

        act.Should().Throw<InvalidOperationException>();
    }

    // Backlog item 7: the studio may only serve on a port the identity provider has a redirect URI for.

    [Fact]
    public void SelectFirstFree_TakesTheFirstFreePortInListOrder()
    {
        // A candidate list of its own, not LauncherDefaults.StudioPorts: the selector's ordering must hold for
        // a list of any length, while the real list holds only the ports the provider has registered.
        var probed = new List<int>();

        var port = PortSelector.SelectFirstFree(
            [9090, 9091, 9092, 9093],
            isAvailable: p =>
            {
                probed.Add(p);
                return p == 9092;
            });

        port.Should().Be(9092);
        probed.Should().ContainInOrder(9090, 9091, 9092);
        // The list is ordered and the walk stops at the first free port: 9093 is never probed.
        probed.Should().NotContain(9093);
    }

    [Fact]
    public void SelectFirstFree_PrefersThePreferredPort_WhenItIsFree()
    {
        var port = PortSelector.SelectFirstFree(LauncherDefaults.StudioPorts, isAvailable: _ => true);

        port.Should().Be(LauncherDefaults.PreferredPort);
    }

    [Fact]
    public void SelectFirstFree_Throws_NamingEveryPort_WhenAllAreBusy()
    {
        var act = () => PortSelector.SelectFirstFree(LauncherDefaults.StudioPorts, isAvailable: _ => false);

        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        foreach (var candidate in LauncherDefaults.StudioPorts)
        {
            message.Should().Contain(candidate.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public void SelectFirstFree_RejectsAnEmptyCandidateList()
    {
        var act = () => PortSelector.SelectFirstFree([], isAvailable: _ => true);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void StudioPorts_HoldsOnlyPortsTheProviderHasRegistered()
    {
        // The provider matches the redirect URI exactly, port included, so this list is a contract with the
        // identity provider's registration, not a preference. Probed live on 2026-09-13: an authorize GET for
        // fravashi-studio answered 302 on 9090 and 400 "'redirect_uri' is not valid for this client
        // application." on 9091, 9092 and 9093. The owner then registered the three, and on 2026-09-15 the same
        // probe answered 302 on localhost 9090 to 9093 and 400 on 9094. A port is added here only after that
        // probe answers 302 for it, and this list is exactly the ports it did.
        LauncherDefaults.StudioPorts.Should().Equal(9090, 9091, 9092, 9093);
    }

    [Fact]
    public void TheUserFacingPortStrings_NameExactlyTheListedPorts()
    {
        // The regression this guards: three hand-written sentences went on naming 9091 to 9093 as registered
        // after the list had changed. Every such string is built from the list, so none can outlive it.
        var listed = LauncherDefaults.StudioPorts
            .Select(p => p.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        var strings = new[]
        {
            LauncherDefaults.StudioPortsPhrase,
            LauncherDefaults.SignInPortsSentence,
            LauncherDefaults.PortsBusyMessage(LauncherDefaults.StudioPorts),
        };

        foreach (var text in strings)
        {
            foreach (var port in listed)
            {
                text.Should().Contain(port);
            }

            foreach (var unregistered in new[] { "9091", "9092", "9093" }.Except(listed))
            {
                text.Should().NotContain(unregistered);
            }
        }
    }

    [Fact]
    public void PortsBusyMessage_ReadsAsOnePortOrSeveral()
    {
        LauncherDefaults.PortsBusyMessage([9090]).Should().Contain("The only host port")
            .And.Contain("(9090)").And.Contain("it is the one port registered");
        LauncherDefaults.PortsBusyMessage([9090, 9091]).Should().Contain("None of the host ports")
            .And.Contain("(9090 or 9091)").And.Contain("they are the ports registered");
    }

    [Fact]
    public void IsTcpPortFree_ReportsTakenPortAsUnavailable()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var busyPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            PortSelector.IsTcpPortFree(busyPort).Should().BeFalse();
        }
        finally
        {
            listener.Stop();
        }
    }
}
