using AwesomeAssertions;
using Fravashi.Application.Network;
using Fravashi.Testing.Network;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Network;

/// <summary>
/// Backlog 228: the scripted handler plays each of plan @48's four faults through a real <see cref="HttpClient"/> on
/// a fake clock, and what each call meets is classified as the real handler's failure would be.
/// </summary>
public sealed class ScriptedNetworkTests
{
    private static readonly Uri Api = new("https://api.example.test/v1/messages");

    [Fact]
    public async Task F1_names_fail_for_a_minute_then_calls_go_through()
    {
        var clock = new FakeNetworkClock();
        var handler = new ScriptedHttpHandler(NetworkScript.F1(clock));
        using var client = new HttpClient(handler);

        var first = await Call(client);
        clock.Advance(TimeSpan.FromSeconds(59));
        var stillBroken = await Call(client);
        clock.Advance(TimeSpan.FromSeconds(1));
        var back = await Call(client);

        first.Kind.Should().Be(NetworkFailureKind.NameNotResolved);
        first.NeverLeft.Should().BeTrue();
        stillBroken.Kind.Should().Be(NetworkFailureKind.NameNotResolved);
        back.Should().Be(NetworkFailure.None);
        handler.Arrived.Should().Be(1);
    }

    [Fact]
    public async Task F2_resets_the_open_request_after_it_arrived_then_has_no_route_then_no_names_then_recovers()
    {
        var clock = new FakeNetworkClock();
        var handler = new ScriptedHttpHandler(NetworkScript.F2(clock));
        using var client = new HttpClient(handler);

        var reset = await Call(client, "{\"create\":1}");
        var noRoute = await Call(client);
        clock.Advance(TimeSpan.FromSeconds(15));
        var noName = await Call(client);
        clock.Advance(TimeSpan.FromSeconds(29));
        var stillNoName = await Call(client);
        clock.Advance(TimeSpan.FromSeconds(1));
        var back = await Call(client);

        reset.Kind.Should().Be(NetworkFailureKind.LeftWithNoAnswer);
        reset.NeverLeft.Should().BeFalse();
        handler.Journal[0].BodyArrived.Should().Be("{\"create\":1}", "the body was sent before the reset");
        noRoute.Kind.Should().Be(NetworkFailureKind.Unreachable);
        noName.Kind.Should().Be(NetworkFailureKind.NameNotResolved);
        stillNoName.Kind.Should().Be(NetworkFailureKind.NameNotResolved);
        back.IsNetwork.Should().BeFalse();
    }

    [Fact]
    public async Task F3_hangs_the_call_in_flight_past_a_thirty_second_outage()
    {
        var clock = new FakeNetworkClock();
        var start = clock.GetUtcNow();
        var handler = new ScriptedHttpHandler(NetworkScript.F3(clock)) { ClientTimeout = TimeSpan.FromSeconds(100) };
        using var client = new HttpClient(handler);

        var hung = await Call(client);
        var after = await Call(client);

        hung.Kind.Should().Be(NetworkFailureKind.LeftWithNoAnswer, "the client's own timeout is not knowing");
        (clock.GetUtcNow() - start).Should().Be(TimeSpan.FromSeconds(100), "the hang ran out the client's timeout on the fake clock");
        after.IsNetwork.Should().BeFalse("the thirty seconds were over by the time the hung call gave up");
    }

    [Fact]
    public async Task F4_outlasts_the_clients_timeout_and_recovers_after_five_minutes()
    {
        var clock = new FakeNetworkClock();
        var start = clock.GetUtcNow();
        var handler = new ScriptedHttpHandler(NetworkScript.F4(clock));
        using var client = new HttpClient(handler);

        var hung = await Call(client);
        var down = await Call(client);
        clock.Advance(TimeSpan.FromMinutes(5) - (clock.GetUtcNow() - start) - TimeSpan.FromSeconds(1));
        var lastSecond = await Call(client);
        clock.Advance(TimeSpan.FromSeconds(1));
        var back = await Call(client);

        hung.Kind.Should().Be(NetworkFailureKind.LeftWithNoAnswer);
        down.Kind.Should().Be(NetworkFailureKind.Unreachable);
        down.NeverLeft.Should().BeTrue();
        lastSecond.Kind.Should().Be(NetworkFailureKind.Unreachable);
        back.IsNetwork.Should().BeFalse();
        handler.Journal.Select(c => c.Behaviour).Should().Equal(
            NetworkBehaviour.Hangs, NetworkBehaviour.NoRoute, NetworkBehaviour.NoRoute, NetworkBehaviour.Healthy);
    }

    [Fact]
    public async Task A_reset_after_write_is_told_apart_from_a_refused_connect()
    {
        var clock = new FakeNetworkClock();
        var handler = new ScriptedHttpHandler(new NetworkScript(clock,
            new NetworkPhase(NetworkBehaviour.Refuses, Calls: 1),
            new NetworkPhase(NetworkBehaviour.ResetAfterWrite, Calls: 1),
            new NetworkPhase(NetworkBehaviour.Healthy)));
        using var client = new HttpClient(handler);

        var refused = await Call(client, "{\"create\":1}");
        var reset = await Call(client, "{\"create\":1}");

        refused.Kind.Should().Be(NetworkFailureKind.Refused);
        refused.NeverLeft.Should().BeTrue("a refused connect wrote nothing, so sending again is safe");
        reset.Kind.Should().Be(NetworkFailureKind.LeftWithNoAnswer);
        reset.NeverLeft.Should().BeFalse("the request arrived; sending again would make a second one");
        handler.Journal.Select(c => c.Arrived).Should().Equal(false, true);
    }

    private static async Task<NetworkFailure> Call(HttpClient client, string body = "{}")
    {
        try
        {
            using var response = await client.PostAsync(Api, new StringContent(body));
            return NetworkFailure.None;
        }
        catch (Exception ex)
        {
            return NetworkFailure.Classify(ex);
        }
    }
}
