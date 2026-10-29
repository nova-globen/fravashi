using System.Net;
using System.Text;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Infrastructure.Onboarding.Auth;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Onboarding.Auth;

/// <summary>
/// The device-code polling state machine, the credential envelope, and the shared OAuth device-flow HTTP
/// engine. Every HTTP call is answered in-process - no test here contacts GitHub or Entra.
/// </summary>
public sealed class DeviceCodeFlowTests
{
    // ── DeviceCodePolling: the pure transition rule ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DevicePollState.Pending, 5, 5, true)]
    [InlineData(DevicePollState.SlowDown, 5, 10, true)]   // +5 seconds, keep going
    [InlineData(DevicePollState.SlowDown, 10, 15, true)]
    [InlineData(DevicePollState.Success, 5, 5, false)]
    [InlineData(DevicePollState.Denied, 5, 5, false)]
    [InlineData(DevicePollState.Expired, 5, 5, false)]
    [InlineData(DevicePollState.Error, 5, 5, false)]
    public void Next_widens_on_slow_down_and_stops_on_terminal(DevicePollState state, int current, int expectedNext, bool expectedContinue)
    {
        var (next, keepGoing) = DeviceCodePolling.Next(current, state);
        next.Should().Be(expectedNext);
        keepGoing.Should().Be(expectedContinue);
    }

    // ── DeviceCodeDriver: drives pending → slow_down → success with no waiting ─────────────────────────────

    [Fact]
    public async Task Driver_polls_through_pending_and_slow_down_to_success()
    {
        var start = new DeviceCodeStart("dev", "USER-CODE", "https://verify", 5, 900, null);
        var credential = new StoredCredential("tok", null, null, AuthMethods.Device);
        var queue = new Queue<DevicePollResult>(
        [
            DevicePollResult.Pending(),
            DevicePollResult.SlowDown(),
            DevicePollResult.Pending(),
            DevicePollResult.Success(credential),
        ]);
        var polls = 0;

        var result = await DeviceCodeDriver.RunAsync(
            start,
            (_, _) => { polls++; return Task.FromResult(queue.Dequeue()); },
            (_, _) => Task.CompletedTask, // no real delay
            DateTimeOffset.UtcNow,
            () => DateTimeOffset.UtcNow,
            cancellationToken: default);

        result.State.Should().Be(DevicePollState.Success);
        result.Credential.Should().BeSameAs(credential);
        polls.Should().Be(4);
    }

    [Fact]
    public async Task Driver_stops_immediately_on_denied()
    {
        var start = new DeviceCodeStart("dev", "U", "https://v", 5, 900, null);
        var result = await DeviceCodeDriver.RunAsync(
            start,
            (_, _) => Task.FromResult(DevicePollResult.Denied("no")),
            (_, _) => Task.CompletedTask,
            DateTimeOffset.UtcNow,
            () => DateTimeOffset.UtcNow);

        result.State.Should().Be(DevicePollState.Denied);
    }

    [Fact]
    public async Task Driver_reports_expired_when_the_deadline_passes()
    {
        var started = DateTimeOffset.UtcNow;
        var start = new DeviceCodeStart("dev", "U", "https://v", 5, 900, null);

        // now() jumps past the deadline, so the loop expires before polling.
        var result = await DeviceCodeDriver.RunAsync(
            start,
            (_, _) => Task.FromResult(DevicePollResult.Pending()),
            (_, _) => Task.CompletedTask,
            started,
            () => started.AddSeconds(1000));

        result.State.Should().Be(DevicePollState.Expired);
    }

    // ── CredentialEnvelope: round-trip + PAT back-compat ───────────────────────────────────────────────────

    [Fact]
    public void Envelope_round_trips_an_oauth_credential()
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(1);
        var credential = new StoredCredential("access-abc", "refresh-xyz", expiry, AuthMethods.AuthCode);

        var parsed = CredentialEnvelope.Parse(CredentialEnvelope.Serialize(credential));

        parsed.Should().NotBeNull();
        parsed!.AccessToken.Should().Be("access-abc");
        parsed.RefreshToken.Should().Be("refresh-xyz");
        parsed.Method.Should().Be(AuthMethods.AuthCode);
        parsed.ExpiresUtc.Should().BeCloseTo(expiry, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Parse_treats_a_bare_string_as_a_legacy_pat()
    {
        var parsed = CredentialEnvelope.Parse("ghp_rawtokenvalue");

        parsed.Should().NotBeNull();
        parsed!.AccessToken.Should().Be("ghp_rawtokenvalue");
        parsed.Method.Should().Be(AuthMethods.Pat);
        parsed.RefreshToken.Should().BeNull();
        parsed.CanRefresh.Should().BeFalse();
    }

    [Fact]
    public void Parse_returns_null_for_empty()
    {
        CredentialEnvelope.Parse(null).Should().BeNull();
        CredentialEnvelope.Parse("   ").Should().BeNull();
    }

    [Fact]
    public void IsExpiredOrExpiring_uses_the_skew()
    {
        var now = DateTimeOffset.UtcNow;
        var soon = new StoredCredential("a", "r", now.AddMinutes(1), AuthMethods.Device);
        var later = new StoredCredential("a", "r", now.AddHours(1), AuthMethods.Device);

        soon.IsExpiredOrExpiring(now).Should().BeTrue();   // within the 2-min skew
        later.IsExpiredOrExpiring(now).Should().BeFalse();
        soon.CanRefresh.Should().BeTrue();
    }

    // ── OAuthDeviceFlow: start parsing + poll error mapping ────────────────────────────────────────────────

    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static readonly OAuthDeviceFlow.Config Config = new("https://idp/device", "https://idp/token", "client-1", "repo");

    [Fact]
    public async Task StartAsync_parses_the_device_code_response()
    {
        var flow = new OAuthDeviceFlow(new HttpClient(new RouteHandler(_ =>
            Json(HttpStatusCode.OK, """{"device_code":"DEV","user_code":"WXYZ-1234","verification_uri":"https://github.com/login/device","expires_in":900,"interval":5}"""))));

        var start = await flow.StartAsync(Config, default);

        start.DeviceCode.Should().Be("DEV");
        start.UserCode.Should().Be("WXYZ-1234");
        start.VerificationUri.Should().Be("https://github.com/login/device");
        start.IntervalSeconds.Should().Be(5);
    }

    [Theory]
    [InlineData("""{"error":"authorization_pending"}""", DevicePollState.Pending)]
    [InlineData("""{"error":"slow_down"}""", DevicePollState.SlowDown)]
    [InlineData("""{"error":"access_denied"}""", DevicePollState.Denied)]
    [InlineData("""{"error":"authorization_declined"}""", DevicePollState.Denied)]
    [InlineData("""{"error":"expired_token"}""", DevicePollState.Expired)]
    [InlineData("""{"error":"something_else"}""", DevicePollState.Error)]
    public async Task PollOnceAsync_maps_each_error_code(string body, DevicePollState expected)
    {
        var flow = new OAuthDeviceFlow(
            new HttpClient(new RouteHandler(_ => Json(HttpStatusCode.OK, body))));

        var result = await flow.PollOnceAsync(Config, "DEV", default);

        result.State.Should().Be(expected);
    }

    [Fact]
    public async Task PollOnceAsync_returns_success_with_the_token_and_expiry()
    {
        var flow = new OAuthDeviceFlow(new HttpClient(new RouteHandler(_ =>
            Json(HttpStatusCode.OK, """{"access_token":"aad-token","refresh_token":"r1","expires_in":3599,"token_type":"Bearer"}"""))));

        var result = await flow.PollOnceAsync(Config, "DEV", default);

        result.State.Should().Be(DevicePollState.Success);
        result.Credential!.AccessToken.Should().Be("aad-token");
        result.Credential.RefreshToken.Should().Be("r1");
        result.Credential.Method.Should().Be(AuthMethods.Device);
        result.Credential.ExpiresUtc.Should().NotBeNull();
    }
}
