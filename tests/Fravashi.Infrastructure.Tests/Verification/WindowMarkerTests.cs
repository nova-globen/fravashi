using AwesomeAssertions;
using Fravashi.Application.Verification;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Verification;

/// <summary>
/// Backlog 226 (plan @69): the window marker belongs to ONE run's origin. The route decision a design check makes for
/// every request of a marked context, and the guarantee that nothing that prints a marker prints its token.
/// </summary>
public sealed class WindowMarkerTests
{
    private const string Token = "0123456789" + "abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static WindowMarker Marker() => WindowMarker.For("http://fravashi-run-r1a2b3c4d5e6-relay:8080", Token)!;

    [Theory]
    [InlineData("http://fravashi-run-r1a2b3c4d5e6-relay:8080/")]
    [InlineData("http://fravashi-run-r1a2b3c4d5e6-relay:8080/workitems/1?x=y#z")]
    [InlineData("http://FRAVASHI-RUN-R1A2B3C4D5E6-RELAY:8080/_framework/blazor.web.js")]
    [InlineData("ws://fravashi-run-r1a2b3c4d5e6-relay:8080/_blazor?id=1")]
    public void The_runs_own_origin_carries_the_marker(string url) =>
        Marker().Route(url).Should().Be(WindowMarkerRoute.Carry);

    [Theory]
    [InlineData("https://fravashi-run-r1a2b3c4d5e6-relay:8080/", "another scheme")]
    [InlineData("http://fravashi-run-r1a2b3c4d5e6-relay:8081/", "another port")]
    [InlineData("http://fravashi-run-r1a2b3c4d5e6-relay/", "the default port is another port")]
    [InlineData("http://fravashi-run-r9-relay:8080/", "another run")]
    [InlineData("http://fravashi-run-r1a2b3c4d5e6-relay.evil.example:8080/", "a longer host")]
    [InlineData("https://example.com/", "an external address")]
    [InlineData("http://example.com/?next=http://fravashi-run-r1a2b3c4d5e6-relay:8080/", "the run named in a query")]
    [InlineData("wss://example.com/socket", "an external socket")]
    [InlineData("not a url", "nothing at all")]
    [InlineData(null, "nothing at all")]
    public void Every_other_address_is_refused(string? url, string why) =>
        Marker().Route(url).Should().Be(WindowMarkerRoute.Refuse, why);

    [Theory]
    [InlineData("data:image/png;base64,iVBORw0KGgo=")]
    [InlineData("blob:http://fravashi-run-r1a2b3c4d5e6-relay:8080/1f2e")]
    public void A_request_that_never_leaves_the_browser_is_local(string url) =>
        Marker().Route(url).Should().Be(WindowMarkerRoute.Local);

    [Theory]
    [InlineData(null, Token)]
    [InlineData("http://run:8080", null)]
    [InlineData("http://run:8080", "")]
    [InlineData("http://run:8080", "two words")]
    [InlineData("ftp://run:8080", Token)]
    [InlineData("run:8080", Token)]
    public void No_marker_is_made_without_a_run_address_and_a_token(string? url, string? token) =>
        WindowMarker.For(url, token).Should().BeNull();

    [Fact]
    public void The_user_agent_carries_the_marker_and_the_token_and_nothing_that_prints_it_does()
    {
        var marker = Marker();

        marker.UserAgent("Mozilla/5.0 X").Should().Be($"Mozilla/5.0 X FravashiWindow/1.0 tk/{Token}");
        marker.ToString().Should().NotContain(Token).And.Contain("withheld");
        marker.Refusal.Should().NotContain(Token);
        marker.Withhold($"console error: agent was Mozilla FravashiWindow/1.0 tk/{Token}!").Should()
            .Be("console error: agent was Mozilla FravashiWindow/1.0 tk/(window token withheld)!");
        new DesignCheckRequest(new Fravashi.Application.Mockups.MockupDocument(1, null, [], [], []), []) { Marker = marker }
            .ToString().Should().NotContain(Token);
        new Fravashi.Application.WorkItems.ApplicationRun("r1", "http://fravashi-run-r1a2b3c4d5e6-relay:8080", null) { Marker = marker }
            .ToString().Should().NotContain(Token);
    }
}
