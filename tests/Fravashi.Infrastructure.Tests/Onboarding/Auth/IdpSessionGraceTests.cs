using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Persistence;
using Fravashi.Infrastructure.Onboarding.Auth;
using Fravashi.Testing.Network;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Onboarding.Auth;

/// <summary>
/// Backlog 234 (plan @64): the sign-in keeps a one-hour grace past its expiry when its refresh fails on the NETWORK,
/// locks at once on a refusal, retries on a backoff and as soon as the issuer's host answers, and never says or logs a
/// token. Everything runs on slice A's scripted network and fake clock.
/// </summary>
public sealed class IdpSessionGraceTests
{
    private const string Issuer = "https://idp.example.test";
    private const string OldAccess = "old-access-SECRET-a1";
    private const string OldRefresh = "old-refresh-SECRET-b2";
    private const string FreshAccess = "fresh-access-SECRET-c3";
    private const string FreshRefresh = "fresh-refresh-SECRET-d4";
    private static readonly string[] Tokens = [OldAccess, OldRefresh, FreshAccess, FreshRefresh];

    private readonly FakeNetworkClock _clock = new();
    private readonly Dictionary<string, string> _secrets = [];
    private readonly IdpSessionGrace _grace;
    private (HttpStatusCode Status, string Body)? _tokenAnswer;

    public IdpSessionGraceTests() => _grace = new IdpSessionGrace(_clock);

    private DateTimeOffset StoreSession(TimeSpan expiresIn)
    {
        var expires = _clock.GetUtcNow() + expiresIn;
        _secrets[AuthSettings.IdpSessionSecretRef] = CredentialEnvelope.Serialize(
            new StoredCredential(OldAccess, OldRefresh, expires, AuthMethods.AuthCode));
        return expires;
    }

    private (IdpAuthService Idp, IdpSessionState State, ScriptedHttpHandler Handler) Build(NetworkScript script)
    {
        var settings = Substitute.For<IConfigurationStore>();
        settings.Get(AuthSettings.IdpIssuer).Returns(Issuer);
        settings.Get(AuthSettings.IdpClientId).Returns("fravashi-studio");

        var secrets = Substitute.For<ISecretStore>();
        secrets.Get(Arg.Any<string>()).Returns(call => _secrets.GetValueOrDefault(call.Arg<string>()));
        secrets.When(s => s.Set(Arg.Any<string>(), Arg.Any<string>()))
            .Do(call => _secrets[call.ArgAt<string>(0)] = call.ArgAt<string>(1));

        var handler = new ScriptedHttpHandler(script, Answer);
        var idp = new IdpAuthService(
            new HttpClient(handler), settings, secrets, new InMemoryAuthCodeStateStore(),
            Substitute.For<IConfigurationReloader>(), _clock, _ => null, _grace);
        var state = new IdpSessionState(settings, secrets, features: null, time: _clock, env: _ => null, grace: _grace);
        return (idp, state, handler);
    }

    private HttpResponseMessage Answer(HttpRequestMessage request)
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { token_endpoint = $"{Issuer}/connect/token" }));
        }

        if (_tokenAnswer is { } scripted)
        {
            return Json(scripted.Status, scripted.Body);
        }

        return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            access_token = FreshAccess,
            refresh_token = FreshRefresh,
            expires_in = 3600,
        }));
    }

    private void TokenEndpointAnswers(HttpStatusCode status, string body) => _tokenAnswer = (status, body);

    private bool Renewed =>
        CredentialEnvelope.Parse(_secrets[AuthSettings.IdpSessionSecretRef])!.AccessToken == FreshAccess;

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static NetworkScript Down(FakeNetworkClock clock) => new(clock, new NetworkPhase(NetworkBehaviour.NoRoute));

    public static TheoryData<string> NetworkFaults => ["F1", "F3", "F4", "503"];

    [Theory]
    [MemberData(nameof(NetworkFaults))]
    public async Task A_network_fault_or_a_503_is_could_not_reach_and_keeps_the_person_signed_in(string fault)
    {
        var script = fault switch
        {
            "F1" => NetworkScript.F1(_clock),
            "F3" => NetworkScript.F3(_clock),
            "F4" => NetworkScript.F4(_clock),
            _ => new NetworkScript(_clock, new NetworkPhase(NetworkBehaviour.Healthy)),
        };
        if (fault == "503")
        {
            TokenEndpointAnswers(HttpStatusCode.ServiceUnavailable, """{"error":"temporarily_unavailable"}""");
        }

        var expires = StoreSession(TimeSpan.FromSeconds(30));
        var (idp, state, _) = Build(script);

        var result = await idp.RefreshAsync(force: true);

        result.Outcome.Should().Be(IdpRefreshOutcome.Unreachable);
        _grace.Outage.Should().NotBeNull();
        _grace.LocksAt(expires).Should().Be(expires + TimeSpan.FromHours(1));
        state.Usable.Should().BeTrue();
        CredentialEnvelope.Parse(_secrets[AuthSettings.IdpSessionSecretRef])!.AccessToken.Should().Be(OldAccess);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"token old-refresh-SECRET-b2 revoked"}""", "invalid_grant, HTTP 400")]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":"invalid_client"}""", "invalid_client, HTTP 401")]
    public async Task A_refusal_is_refused_and_says_so_without_the_description(HttpStatusCode status, string body, string said)
    {
        TokenEndpointAnswers(status, body);
        StoreSession(TimeSpan.FromSeconds(30));
        var (idp, state, _) = Build(new NetworkScript(_clock, new NetworkPhase(NetworkBehaviour.Healthy)));

        var result = await idp.RefreshAsync(force: true);

        result.Outcome.Should().Be(IdpRefreshOutcome.Refused);
        _grace.Outage.Should().BeNull();
        state.RefusalWords.Should().Contain(said);
        Tokens.Should().AllSatisfy(token => state.RefusalWords.Should().NotContain(token));

        _clock.Advance(TimeSpan.FromSeconds(31));
        state.Usable.Should().BeFalse("a refused session gets no grace past its expiry");
    }

    [Fact]
    public async Task The_grace_holds_for_59_minutes_past_expiry_and_closes_at_60()
    {
        var expires = StoreSession(TimeSpan.FromSeconds(30));
        var (idp, state, _) = Build(Down(_clock));
        await idp.RefreshAsync(force: true);

        _clock.Advance(expires + TimeSpan.FromMinutes(59) - _clock.GetUtcNow());
        state.SignedIn.Should().BeFalse();
        state.Usable.Should().BeTrue();
        (await idp.RefreshAsync(force: true)).Outcome.Should().Be(IdpRefreshOutcome.Unreachable);
        state.Usable.Should().BeTrue("failing again does not stretch the grace, and it is still inside it");

        _clock.Advance(TimeSpan.FromMinutes(1));
        state.Usable.Should().BeFalse();
        state.Grace!.Lapsed.Should().BeTrue();
        state.Grace.LocksAt.Should().Be(expires + TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task A_refusal_inside_the_grace_closes_it_at_once()
    {
        var expires = StoreSession(TimeSpan.FromSeconds(30));
        var script = new NetworkScript(_clock,
            new NetworkPhase(NetworkBehaviour.NameFails, Calls: 1),
            new NetworkPhase(NetworkBehaviour.Healthy));
        var (idp, state, _) = Build(script);
        await idp.RefreshAsync(force: true);
        _clock.Advance(expires + TimeSpan.FromMinutes(10) - _clock.GetUtcNow());
        state.Usable.Should().BeTrue();

        TokenEndpointAnswers(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
        (await idp.RefreshAsync(force: true)).Outcome.Should().Be(IdpRefreshOutcome.Refused);

        state.Usable.Should().BeFalse();
        state.Grace.Should().BeNull();
    }

    [Fact]
    public async Task A_refresh_that_succeeds_during_the_grace_ends_it_silently()
    {
        var expires = StoreSession(TimeSpan.FromSeconds(30));
        var script = new NetworkScript(_clock,
            new NetworkPhase(NetworkBehaviour.NoRoute, For: TimeSpan.FromMinutes(20)),
            new NetworkPhase(NetworkBehaviour.Healthy));
        var (idp, state, _) = Build(script);
        await idp.RefreshAsync(force: true);
        _clock.Advance(expires + TimeSpan.FromMinutes(25) - _clock.GetUtcNow());

        var result = await idp.RefreshAsync(force: true);

        result.Outcome.Should().Be(IdpRefreshOutcome.Renewed);
        _grace.Outage.Should().BeNull();
        state.Grace.Should().BeNull();
        state.SignedIn.Should().BeTrue();
        CredentialEnvelope.Parse(_secrets[AuthSettings.IdpSessionSecretRef])!.AccessToken.Should().Be(FreshAccess);
    }

    [Fact]
    public async Task The_backoff_is_15_seconds_doubling_to_2_minutes_and_the_gate_does_not_call_while_it_holds()
    {
        StoreSession(TimeSpan.FromSeconds(30));
        var (idp, _, handler) = Build(Down(_clock));
        var waits = new List<TimeSpan>();
        for (var i = 0; i < 5; i++)
        {
            await idp.RefreshAsync(force: true);
            var next = _grace.Outage!.NextAttemptAt;
            waits.Add(next - _clock.GetUtcNow());

            var calls = handler.Journal.Count;
            (await idp.RefreshIfNeededAsync()).Should().BeFalse();
            handler.Journal.Count.Should().Be(calls, "the gate's refresh does not ask while the backoff holds");
            _clock.Advance(next - _clock.GetUtcNow());
        }

        waits.Should().Equal(
            TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60),
            TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task The_words_carry_the_first_failure_and_the_lock_instant_and_never_a_token()
    {
        var expires = StoreSession(TimeSpan.FromMinutes(5));
        var (idp, state, _) = Build(Down(_clock));
        var first = _clock.GetUtcNow();
        await idp.RefreshAsync(force: true);
        _clock.Advance(TimeSpan.FromSeconds(15));
        await idp.RefreshAsync(force: true);

        var grace = state.Grace!;
        grace.Since.Should().Be(first);
        grace.LocksAt.Should().Be(expires + TimeSpan.FromHours(1));
        grace.Lapsed.Should().BeFalse();
        grace.Reason.Should().Be("could not reach idp.example.test: no network route");
        Tokens.Should().AllSatisfy(token => grace.Reason.Should().NotContain(token));
    }

    [Fact]
    public async Task The_loop_retries_at_once_when_the_issuers_host_answers_again()
    {
        StoreSession(TimeSpan.FromSeconds(30));
        var down = TimeSpan.FromSeconds(50);
        var (idp, state, handler) = Build(new NetworkScript(_clock,
            new NetworkPhase(NetworkBehaviour.NoRoute, For: down), new NetworkPhase(NetworkBehaviour.Healthy)));
        var connector = new ScriptedConnector(new NetworkScript(_clock,
            new NetworkPhase(NetworkBehaviour.NoRoute, For: down), new NetworkPhase(NetworkBehaviour.Healthy)));
        var logger = new ListLogger<IdpSessionRefreshHostedService>();
        var loop = new IdpSessionRefreshHostedService(Scopes(idp, state), logger, _grace, _clock, connector);

        DateTimeOffset? attemptDue = null;
        for (var step = 0; step < 50 && !Renewed; step++)
        {
            attemptDue = _grace.Outage?.NextAttemptAt;
            var wait = await loop.StepAsync(CancellationToken.None);
            if (!Renewed)
            {
                _clock.Advance(wait);
            }
        }

        Renewed.Should().BeTrue();
        state.Grace.Should().BeNull();
        var renewedAt = handler.Journal[^1].At;
        connector.Calls.Should().NotBeEmpty();
        connector.Calls[^1].At.Should().Be(renewedAt, "the renewal was asked on the step the probe saw the host answer");
        renewedAt.Should().BeBefore(attemptDue!.Value, "the probe beat the backoff's own next attempt");
        connector.Calls[^1].Target.Host.Should().Be("idp.example.test");
        connector.Calls[^1].Target.Port.Should().Be(443);
    }

    [Fact]
    public async Task The_loops_log_lines_never_carry_a_token()
    {
        StoreSession(TimeSpan.FromSeconds(30));
        var (idp, state, _) = Build(new NetworkScript(_clock,
            new NetworkPhase(NetworkBehaviour.NameFails, Calls: 1),
            new NetworkPhase(NetworkBehaviour.Healthy)));
        var logger = new ListLogger<IdpSessionRefreshHostedService>();
        var loop = new IdpSessionRefreshHostedService(
            Scopes(idp, state), logger, _grace, _clock,
            new ScriptedConnector(new NetworkScript(_clock, new NetworkPhase(NetworkBehaviour.NoRoute))));

        await loop.StepAsync(CancellationToken.None);       // could not reach
        _clock.Advance(TimeSpan.FromSeconds(15));
        TokenEndpointAnswers(HttpStatusCode.BadRequest, $$"""{"error":"invalid_grant","error_description":"{{OldRefresh}}"}""");
        await loop.StepAsync(CancellationToken.None);       // refused
        _tokenAnswer = null;
        await loop.StepAsync(CancellationToken.None);       // renewed

        logger.Lines.Should().Contain(line => line.Contains("could not reach idp.example.test", StringComparison.Ordinal));
        logger.Lines.Should().Contain(line => line.Contains("refused", StringComparison.Ordinal));
        logger.Lines.Should().Contain(line => line.Contains("Renewed", StringComparison.Ordinal));
        foreach (var token in Tokens)
        {
            logger.Lines.Should().NotContain(line => line.Contains(token, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_loop_waits_for_the_backoff_or_the_probe_and_never_past_five_minutes()
    {
        var now = _clock.GetUtcNow();
        var outage = new IdpOutage(now, "x", now + TimeSpan.FromSeconds(30));

        IdpSessionRefreshHostedService.NextWait(now, outage, false, null, now + TimeSpan.FromSeconds(10))
            .Should().Be(TimeSpan.FromSeconds(10));
        IdpSessionRefreshHostedService.NextWait(now, outage, false, null, now + TimeSpan.FromSeconds(50))
            .Should().Be(TimeSpan.FromSeconds(30));
        IdpSessionRefreshHostedService.NextWait(now, null, false, now + TimeSpan.FromHours(1), null)
            .Should().Be(IdpSessionRefreshHostedService.Interval);
        IdpSessionRefreshHostedService.NextWait(now, null, false, now + TimeSpan.FromMinutes(9), null)
            .Should().Be(TimeSpan.FromMinutes(2));
        IdpSessionRefreshHostedService.NextWait(now, null, true, now, null)
            .Should().Be(IdpSessionRefreshHostedService.Interval);
    }

    [Fact]
    public void The_grace_is_one_hour()
    {
        IdpSessionGrace.Length.Should().Be(TimeSpan.FromHours(1));
    }

    private static IServiceScopeFactory Scopes(IdpAuthService idp, IdpSessionState state)
    {
        var services = new ServiceCollection();
        services.AddSingleton(idp);
        services.AddSingleton(state);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines => _lines;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _lines.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
    }
}
