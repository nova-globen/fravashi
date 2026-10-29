using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using Fravashi.Application.Network;
using Fravashi.Infrastructure.Network;
using Fravashi.Testing.Network;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Network;

/// <summary>Backlog 228: the reachability probe across scripted outages on a fake clock, and once on real sockets.</summary>
public sealed class NetworkReachabilityTests
{
    private static readonly NetworkEndpoint Target = new("api.anthropic.com", 443);

    [Fact]
    public async Task Reports_unreachable_then_reachable_across_an_outage_with_the_documented_backoff()
    {
        var clock = new FakeNetworkClock();
        var start = clock.GetUtcNow();
        var script = new NetworkScript(clock,
            new NetworkPhase(NetworkBehaviour.NameFails, For: TimeSpan.FromSeconds(150)),
            new NetworkPhase(NetworkBehaviour.Healthy));
        var probe = new NetworkReachability(Target, new ScriptedConnector(script), clock);
        var changes = new List<NetworkReachabilityState>();
        probe.Changed += changes.Add;

        probe.Current.Reachable.Should().BeNull();
        var waits = new List<TimeSpan>();
        while (probe.Current.Reachable != true)
        {
            var wait = await probe.StepAsync();
            if (probe.Current.Reachable == false)
            {
                probe.Current.Since.Should().Be(start, "unreachable since the first failure, however many follow");
                probe.Current.LastFailure!.Kind.Should().Be(NetworkFailureKind.NameNotResolved);
                probe.Current.NextProbeAt.Should().Be(clock.GetUtcNow() + wait);
            }

            waits.Add(wait);
            clock.Advance(wait);
        }

        // 5, 10, 20, 40, 60, 60 brings the clock to 195 s, the first probe after 150 s; the success then waits the healthy interval.
        waits.Should().Equal(
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40),
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), NetworkReachability.HealthyInterval);
        changes.Select(c => c.Reachable).Should().Equal(false, true);
        changes[1].Since.Should().Be(start + TimeSpan.FromSeconds(195));
        changes[0].Words.Should().Be("api.anthropic.com unreachable since 12:00 (name not resolved)");
        changes[1].Words.Should().Be("api.anthropic.com reachable since 12:03");
    }

    [Fact]
    public async Task The_backoff_starts_again_from_five_seconds_after_a_success()
    {
        var clock = new FakeNetworkClock();
        var script = new NetworkScript(clock,
            new NetworkPhase(NetworkBehaviour.NoRoute, Calls: 3),
            new NetworkPhase(NetworkBehaviour.Healthy, Calls: 1),
            new NetworkPhase(NetworkBehaviour.Refuses));
        var probe = new NetworkReachability(Target, new ScriptedConnector(script), clock);

        var waits = new List<TimeSpan>();
        for (var i = 0; i < 6; i++)
        {
            waits.Add(await probe.StepAsync());
        }

        waits.Select(w => w.TotalSeconds).Should().Equal(5, 10, 20, 60, 5, 10);
        probe.Current.LastFailure!.Kind.Should().Be(NetworkFailureKind.Refused);
    }

    [Fact]
    public async Task A_link_down_probe_that_hangs_reads_as_timed_out_connecting()
    {
        var clock = new FakeNetworkClock();
        var probe = new NetworkReachability(Target, new ScriptedConnector(NetworkScript.F4(clock)), clock);

        await probe.StepAsync();

        probe.Current.Reachable.Should().BeFalse();
        probe.Current.LastFailure!.Kind.Should().Be(NetworkFailureKind.TimedOutConnecting);
        probe.Current.LastFailure.NeverLeft.Should().BeTrue();
    }

    [Fact]
    public async Task The_socket_connector_reaches_an_open_port_and_is_refused_by_a_closed_one()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var open = ((IPEndPoint)listener.LocalEndpoint).Port;
        int closed;
        using (var spare = new TcpListener(IPAddress.Loopback, 0))
        {
            spare.Start();
            closed = ((IPEndPoint)spare.LocalEndpoint).Port;
        }

        var connector = new SocketNetworkConnector();

        (await connector.ConnectAsync(new("127.0.0.1", open), TimeSpan.FromSeconds(5), CancellationToken.None)).Should().BeNull();
        (await connector.ConnectAsync(new("127.0.0.1", closed), TimeSpan.FromSeconds(5), CancellationToken.None))!
            .Kind.Should().Be(NetworkFailureKind.Refused);
        (await connector.ConnectAsync(new("netfault-228.invalid", 443), TimeSpan.FromSeconds(30), CancellationToken.None))!
            .Kind.Should().Be(NetworkFailureKind.NameNotResolved);
    }
}
