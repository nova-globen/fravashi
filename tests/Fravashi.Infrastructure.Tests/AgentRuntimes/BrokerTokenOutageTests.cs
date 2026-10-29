using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.AgentRuntimes.Broker;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Infrastructure.AgentRuntimes.Broker;
using Fravashi.Testing.Network;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Infrastructure.Tests.AgentRuntimes;

/// <summary>
/// Backlog 235 (plan @60): the broker token under an unstable network, on slice A's scripted network and fake clock.
/// The mint client and the background refresher are the production ones; only the network and the clock are faked.
/// Every token the scripted broker mints carries <see cref="Secret"/>, and every test ends by checking that it reached no
/// answer, no status, no Settings words and no log line.
/// </summary>
public sealed class BrokerTokenOutageTests
{
    private const string BaseUrl = "https://broker.example";
    private const string Secret = "SECRET-bearer-value";

    private readonly FakeNetworkClock _clock = new();
    private readonly FakeSecretStore _secrets = new();
    private readonly CapturingLogger<BrokerTokenService> _log = new();
    private readonly CapturingLogger<BrokerTokenRefreshHostedService> _refresherLog = new();
    private readonly BrokerTokenHealth _health;
    private int _minted;

    public BrokerTokenOutageTests()
    {
        _health = new BrokerTokenHealth(_clock);
        SeedIdentity("idp-access-token-1");
    }

    private static BrokerOptions Active() => new() { Enabled = true, BaseUrl = BaseUrl };

    private void SeedIdentity(string proof) =>
        _secrets.Set(BrokerReferences.IdentitySecretRef,
            CredentialEnvelope.Serialize(new StoredCredential(proof, null, null, AuthMethods.Device)));

    private void SeedToken(string tokenId, TimeSpan expiresIn)
    {
        _secrets.Set(BrokerOptions.DefaultUserTokenSecretRef, $"tok-{Secret}-{tokenId}");
        _secrets.Set(BrokerReferences.TokenMetadataSecretRef,
            new BrokerTokenMetadata("oidc:abc", tokenId, _clock.GetUtcNow(), _clock.GetUtcNow() + expiresIn).Serialize());
    }

    /// <summary>The scripted broker: on a healthy call it mints a new token every time, an hour long.</summary>
    private HttpResponseMessage Mints(HttpRequestMessage request)
    {
        var n = Interlocked.Increment(ref _minted);
        var body = JsonSerializer.Serialize(new
        {
            token = $"tok-{Secret}-{n}",
            tokenId = $"jti-{n}",
            expiresAt = _clock.GetUtcNow().AddHours(1),
            subject = "oidc:abc",
        });
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> Answers(HttpStatusCode status, string body) =>
        _ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private BrokerTokenService Service(ScriptedHttpHandler handler) =>
        new(_secrets, Active(), new HttpClient(handler), _clock, _log, _health);

    private BrokerTokenRefreshHostedService Refresher(BrokerTokenService service)
    {
        var provider = new ServiceCollection().AddSingleton<IBrokerTokenService>(service).BuildServiceProvider();
        return new BrokerTokenRefreshHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(), Active(), _refresherLog, _health, _clock);
    }

    /// <summary>Runs the refresher's own loop on the fake clock until a token is minted (or the pass limit), recording each wait.</summary>
    private async Task<List<TimeSpan>> RunUntilMintedAsync(BrokerTokenRefreshHostedService refresher, int passes = 20)
    {
        var waits = new List<TimeSpan>();
        for (var i = 0; i < passes && _minted == 0; i++)
        {
            var wait = await refresher.StepAsync(CancellationToken.None);
            waits.Add(wait);
            if (_minted == 0)
            {
                _clock.Advance(wait);
            }
        }

        return waits;
    }

    private void NothingLeaked(params object?[] answers)
    {
        foreach (var line in _log.Lines.Select(l => l.Message).Concat(_refresherLog.Lines.Select(l => l.Message)))
        {
            line.Should().NotContain(Secret, "a log line must never carry the token");
        }

        foreach (var answer in answers)
        {
            (answer?.ToString() ?? string.Empty).Should().NotContain(Secret, "an answer must never carry the token");
        }

        (_health.Words ?? string.Empty).Should().NotContain(Secret);
    }

    // ---- an outage: retried on the backoff, minted once the broker answers ------------------

    public static TheoryData<string, double[], int> Outages() => new()
    {
        // The fault; the refresher's waits until the mint succeeds (seconds); the second the broker answers again.
        // F1: the name fails for 60 s. Attempts at 0, 15 and 45 fail; the one at 105 mints.
        { "F1", [15, 30, 60], 60 },

        // F3: the attempt in flight hangs to the client's 100 s timeout, by which time the 30 s outage is over.
        { "F3", [15], 30 },

        // F4: the hung attempt, then no route until 300 s. Attempts at 115, 145 and 205 fail; the one at 325 mints.
        { "F4", [15, 30, 60, 120], 300 },
    };

    private NetworkScript Script(string fault) => fault switch
    {
        "F1" => NetworkScript.F1(_clock),
        "F3" => NetworkScript.F3(_clock),
        "F4" => NetworkScript.F4(_clock),
        _ => throw new ArgumentOutOfRangeException(nameof(fault)),
    };

    [Theory]
    [MemberData(nameof(Outages))]
    public async Task A_mint_that_cannot_reach_the_broker_is_retried_on_the_backoff_and_mints_once_it_answers(
        string fault, double[] waitsBeforeMint, int backAtSecond)
    {
        // The token is already inside its renewal window (3 minutes left) when the network drops.
        var start = _clock.GetUtcNow();
        SeedToken("jti-old", TimeSpan.FromMinutes(3));
        var handler = new ScriptedHttpHandler(Script(fault), Mints);
        var service = Service(handler);

        var waits = await RunUntilMintedAsync(Refresher(service));

        // 15 s doubling to 2 minutes, never the old 5-minute tick, and each attempt is one call (nothing hammers).
        waits.Take(waits.Count - 1).Select(w => w.TotalSeconds).Should().Equal(waitsBeforeMint);
        handler.Journal.Should().HaveCount(waitsBeforeMint.Length + 1);
        _minted.Should().Be(1);

        // Minted no later than one backoff step after the broker came back.
        var mintedAt = handler.Journal[^1].At;
        (mintedAt - (start + TimeSpan.FromSeconds(backAtSecond))).Should().BeLessThanOrEqualTo(BrokerTokenHealth.MaxRetry);

        var status = service.GetStatus();
        status.TokenValid.Should().BeTrue();
        status.TokenId.Should().Be("jti-1");
        status.Trouble.Should().BeNull("the outage ended with the mint");
        _health.Outage.Should().BeNull();

        // After a good mint the refresher goes back to its ordinary interval.
        waits[^1].Should().Be(BrokerTokenRefreshHostedService.Interval);
        NothingLeaked(status, status.Trouble);
    }

    [Fact]
    public async Task A_token_that_expired_during_the_outage_is_minted_again_once_the_broker_answers()
    {
        // F4 against a token with 3 minutes left: it lapses at 180 s, the broker answers at 300 s, and the next
        // attempt (325 s) mints a fresh one rather than waiting for a tick.
        var start = _clock.GetUtcNow();
        SeedToken("jti-old", TimeSpan.FromMinutes(3));
        var service = Service(new ScriptedHttpHandler(NetworkScript.F4(_clock), Mints));
        var refresher = Refresher(service);

        var sawExpired = false;
        for (var i = 0; i < 20 && _minted == 0; i++)
        {
            var wait = await refresher.StepAsync(CancellationToken.None);
            sawExpired |= !service.GetStatus().TokenValid;
            if (_minted == 0)
            {
                // During the outage Settings says so, in words, with no token in them.
                service.GetStatus().Trouble.Should().StartWith("Broker unreachable since").And.Contain("retrying");
                _clock.Advance(wait);
            }
        }

        sawExpired.Should().BeTrue("the outage outlasted the token");
        service.GetStatus().TokenValid.Should().BeTrue();
        (_clock.GetUtcNow() - start).Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(300) + BrokerTokenHealth.MaxRetry);
        NothingLeaked(service.GetStatus());
    }

    [Fact]
    public async Task A_broker_answering_503_is_retried_on_the_backoff_like_the_network()
    {
        SeedToken("jti-old", TimeSpan.FromMinutes(3));
        var handler = new ScriptedHttpHandler(new NetworkScript(_clock, new NetworkPhase(NetworkBehaviour.Healthy)),
            Answers(HttpStatusCode.ServiceUnavailable, "{\"error\":\"upstream\"}"));
        var service = Service(handler);

        var first = await service.RefreshIfNeededAsync();
        first!.Failure.Should().Be(BrokerMintFailure.Unreachable);
        first.Error.Should().Contain("503").And.Contain("retries by itself");

        // Not asked again before the backoff's next attempt...
        _clock.Advance(TimeSpan.FromSeconds(10));
        (await service.RefreshIfNeededAsync())!.Failure.Should().Be(BrokerMintFailure.Unreachable);
        handler.Journal.Should().HaveCount(1);

        // ...and asked again once it is due.
        _clock.Advance(TimeSpan.FromSeconds(5));
        await service.RefreshIfNeededAsync();
        handler.Journal.Should().HaveCount(2);
        NothingLeaked(first, first.Error);
    }

    // ---- renewal before expiry ----------------------------------------------------------

    [Fact]
    public async Task The_token_is_renewed_when_its_window_opens_before_expiry_not_up_to_a_tick_later()
    {
        var start = _clock.GetUtcNow();
        SeedToken("jti-old", TimeSpan.FromMinutes(20));
        var handler = new ScriptedHttpHandler(new NetworkScript(_clock, new NetworkPhase(NetworkBehaviour.Healthy)), Mints);
        var service = Service(handler);

        var waits = await RunUntilMintedAsync(Refresher(service));

        // 5, 5 and 3 minutes: the third pass lands exactly where the window opens, 7 minutes before expiry.
        waits.Take(3).Select(w => w.TotalMinutes).Should().Equal(5, 5, 3);
        var mintedAt = handler.Journal.Single().At;
        mintedAt.Should().Be(start + TimeSpan.FromMinutes(20) - BrokerTokenService.RenewAhead);
        mintedAt.Should().BeBefore(start + TimeSpan.FromMinutes(20));
        NothingLeaked();
    }

    [Fact]
    public void The_refresher_never_waits_past_the_renewal_window_or_spins()
    {
        var now = _clock.GetUtcNow();
        BrokerConnectionStatus Valid(TimeSpan expiresIn) =>
            new(true, true, "oidc:abc", true, now + expiresIn, now) { TokenId = "jti" };

        BrokerTokenRefreshHostedService.NextWait(now, null, false, Valid(TimeSpan.FromHours(1)))
            .Should().Be(BrokerTokenRefreshHostedService.Interval);
        BrokerTokenRefreshHostedService.NextWait(now, null, false, Valid(TimeSpan.FromMinutes(9)))
            .Should().Be(TimeSpan.FromMinutes(2));

        // Inside the window but not renewed (an answer that was no token): looked at again soon, never in a tight loop.
        BrokerTokenRefreshHostedService.NextWait(now, null, false, Valid(TimeSpan.FromMinutes(3)))
            .Should().Be(BrokerTokenHealth.FirstRetry);

        // A refusal is not on the backoff: the plain interval, which only looks for a new sign-in.
        BrokerTokenRefreshHostedService.NextWait(now, null, true, Valid(TimeSpan.FromMinutes(3)))
            .Should().Be(BrokerTokenRefreshHostedService.Interval);
    }

    // ---- a refusal is not the network --------------------------------------------------

    public static TheoryData<HttpStatusCode, string, string, bool> Refusals() => new()
    {
        // The refusal; what it says; whether a RENEWED access token (the identity provider's hourly refresh, which
        // rewrites the mint proof) asks again. A refusal of the person does not lift with it; a refusal of the proof does.
        { HttpStatusCode.Forbidden, "{\"error\":\"not_allowlisted\",\"subject\":\"oidc:abc\"}", "oidc:abc is not on its allowlist (not_allowlisted)", false },
        { HttpStatusCode.Forbidden, "{\"error\":\"not_entitled\",\"subject\":\"oidc:abc\"}", "oidc:abc is not entitled to it (not_entitled)", false },
        { HttpStatusCode.Unauthorized, "{\"error\":\"identity_rejected\"}", "no longer accepts your sign-in (identity_rejected)", true },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_refused_mint_says_why_and_is_not_asked_again_by_itself(
        HttpStatusCode status, string body, string says, bool renewedProofAsksAgain)
    {
        SeedToken("jti-old", TimeSpan.FromMinutes(3));
        var handler = new ScriptedHttpHandler(new NetworkScript(_clock, new NetworkPhase(NetworkBehaviour.Healthy)), Answers(status, body));
        var service = Service(handler);
        var refresher = Refresher(service);

        // Twenty passes of the refresher over an hour and a half: the broker is asked once.
        for (var i = 0; i < 20; i++)
        {
            _clock.Advance(await refresher.StepAsync(CancellationToken.None));
        }

        handler.Journal.Should().HaveCount(1, "a refusal is never retried as if it were the network");
        var result = await service.RefreshIfNeededAsync();
        result!.Failure.Should().Be(BrokerMintFailure.Refused);
        result.Error.Should().Contain(says);
        service.GetStatus().Trouble.Should().Contain(says, "Settings says the same words");
        _health.Outage.Should().BeNull("the broker answered, so it is not unreachable");
        handler.Journal.Should().HaveCount(1);

        // The identity provider renews the access token (IdpAuthService rewrites broker:identity about hourly). That is
        // a renewed PROOF: it asks again after a 401 (where backlog 234's refresh plugs in), and not after a refusal of
        // the person, which no access token changes.
        SeedIdentity("idp-access-token-2");
        for (var i = 0; i < 3; i++)
        {
            _clock.Advance(await refresher.StepAsync(CancellationToken.None));
        }

        var askedAfterRenewal = renewedProofAsksAgain ? 2 : 1;
        handler.Journal.Should().HaveCount(askedAfterRenewal);

        // And the person's own Mint always asks.
        await service.MintAsync();
        handler.Journal.Should().HaveCount(askedAfterRenewal + 1);
        NothingLeaked(result, result.Error);
    }

    [Fact]
    public async Task A_conversation_refused_on_a_still_recorded_token_gets_a_different_one()
    {
        // The broker refused a conversation's token whose recorded expiry has not passed (clock skew, a revocation):
        // asking to replace THAT token mints even though the store would call it fresh.
        SeedToken("jti-old", TimeSpan.FromMinutes(40));
        var handler = new ScriptedHttpHandler(new NetworkScript(_clock, new NetworkPhase(NetworkBehaviour.Healthy)), Mints);
        var service = Service(handler);

        (await service.RefreshIfNeededAsync()).Should().BeNull("nothing is due by expiry");
        var renewed = await service.RenewAsync(replacing: "jti-old", now: false);

        renewed!.Success.Should().BeTrue();
        renewed.TokenId.Should().Be("jti-1");
        service.GetStatus().TokenId.Should().Be("jti-1");
        NothingLeaked(renewed);
    }
}
