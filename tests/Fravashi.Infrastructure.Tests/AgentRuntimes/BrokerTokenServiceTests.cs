using System.Net;
using System.Text;
using System.Text.Json;
using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.AgentRuntimes.Broker;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Infrastructure.AgentRuntimes.Broker;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.AgentRuntimes;

public sealed class BrokerTokenServiceTests
{
    private const string BaseUrl = "https://broker.example";
    private const string ProofToken = "idp-access-token";

    private static BrokerOptions Active() => new() { Enabled = true, BaseUrl = BaseUrl };

    private static void SeedIdentity(FakeSecretStore secrets, string proof = ProofToken) =>
        secrets.Set(BrokerReferences.IdentitySecretRef,
            CredentialEnvelope.Serialize(new StoredCredential(proof, null, null, AuthMethods.Device)));

    // ─────────────────────────────── Mint ───────────────────────────────

    [Fact]
    public async Task Mint_calls_the_broker_and_stores_the_token_in_the_ref_the_cli_reads()
    {
        var secrets = new FakeSecretStore();
        SeedIdentity(secrets);
        var expires = DateTimeOffset.UtcNow.AddHours(1);
        var handler = new BrokerStubHandler
        {
            OnMint = req => (HttpStatusCode.OK, MintJson("tok-xyz", "jti-1", expires), req.Body),
        };
        var service = new BrokerTokenService(secrets, Active(), new HttpClient(handler), new MutableClock(DateTimeOffset.UtcNow));

        var result = await service.MintAsync();

        result.Success.Should().BeTrue();
        // The raw token lands under the exact ref B1 reads as the CLI bearer.
        secrets.Get(BrokerOptions.DefaultUserTokenSecretRef).Should().Be("tok-xyz");
        // The proof the client sent is the stored IdP access token.
        handler.LastMintBody.Should().Contain(ProofToken).And.Contain("\"scope\":\"messages\"");
        // Metadata records expiry for refresh + status.
        BrokerTokenMetadata.Parse(secrets.Get(BrokerReferences.TokenMetadataSecretRef))!.TokenId.Should().Be("jti-1");
    }

    [Fact]
    public async Task Mint_records_the_models_the_broker_says_the_token_covers()
    {
        // The mint answer advertises them, in the shape a test broker answered on 2026-10-29.
        var secrets = new FakeSecretStore();
        SeedIdentity(secrets);
        var expires = DateTimeOffset.UtcNow.AddHours(1);
        var body = JsonSerializer.Serialize(new
        {
            token = "tok-xyz", tokenId = "jti-1", expiresAt = expires, subject = "oidc:subject", allowedModels = new[] { "claude-haiku-5-5*" },
        });
        var handler = new BrokerStubHandler { OnMint = req => (HttpStatusCode.OK, body, req.Body) };
        var service = new BrokerTokenService(secrets, Active(), new HttpClient(handler), new MutableClock(DateTimeOffset.UtcNow));

        (await service.MintAsync()).Success.Should().BeTrue();

        BrokerTokenMetadata.Parse(secrets.Get(BrokerReferences.TokenMetadataSecretRef))!.AllowedModels
            .Should().Equal("claude-haiku-5-5*");
    }

    [Fact]
    public async Task Mint_fails_and_stores_nothing_when_not_signed_in()
    {
        var secrets = new FakeSecretStore();
        var handler = new BrokerStubHandler();
        var service = new BrokerTokenService(secrets, Active(), new HttpClient(handler), new MutableClock(DateTimeOffset.UtcNow));

        var result = await service.MintAsync();

        result.Success.Should().BeFalse();
        handler.MintCalls.Should().Be(0, "there is no identity proof to mint from");
        secrets.Get(BrokerOptions.DefaultUserTokenSecretRef).Should().BeNull();
    }

    [Fact]
    public async Task Mint_is_a_no_op_when_the_broker_tier_is_inactive()
    {
        var secrets = new FakeSecretStore();
        SeedIdentity(secrets);
        var handler = new BrokerStubHandler();
        var service = new BrokerTokenService(secrets, BrokerOptions.Disabled, new HttpClient(handler), new MutableClock(DateTimeOffset.UtcNow));

        var result = await service.MintAsync();

        result.Success.Should().BeFalse();
        handler.MintCalls.Should().Be(0, "the single-user default never mints");
    }

    // ─────────────────────────────── Refresh ───────────────────────────────

    [Fact]
    public async Task RefreshIfNeeded_remints_when_the_token_is_near_expiry()
    {
        var secrets = new FakeSecretStore();
        SeedIdentity(secrets);
        var clock = new MutableClock(DateTimeOffset.UtcNow);

        // A token that expires in 2 minutes - inside the 5-minute skew, so a refresh is due.
        secrets.Set(BrokerOptions.DefaultUserTokenSecretRef, "old-token");
        secrets.Set(BrokerReferences.TokenMetadataSecretRef,
            new BrokerTokenMetadata("github:1", "jti-old", clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(2)).Serialize());

        var newExpiry = clock.GetUtcNow().AddHours(1);
        var handler = new BrokerStubHandler { OnMint = req => (HttpStatusCode.OK, MintJson("fresh-token", "jti-new", newExpiry), req.Body) };
        var service = new BrokerTokenService(secrets, Active(), new HttpClient(handler), clock);

        var result = await service.RefreshIfNeededAsync();

        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        secrets.Get(BrokerOptions.DefaultUserTokenSecretRef).Should().Be("fresh-token");
    }

    [Fact]
    public async Task RefreshIfNeeded_does_nothing_when_the_token_is_still_fresh()
    {
        var secrets = new FakeSecretStore();
        SeedIdentity(secrets);
        var clock = new MutableClock(DateTimeOffset.UtcNow);

        secrets.Set(BrokerOptions.DefaultUserTokenSecretRef, "still-good");
        secrets.Set(BrokerReferences.TokenMetadataSecretRef,
            new BrokerTokenMetadata("github:1", "jti", clock.GetUtcNow(), clock.GetUtcNow().AddHours(1)).Serialize());

        var handler = new BrokerStubHandler();
        var service = new BrokerTokenService(secrets, Active(), new HttpClient(handler), clock);

        var result = await service.RefreshIfNeededAsync();

        result.Should().BeNull();
        handler.MintCalls.Should().Be(0);
        secrets.Get(BrokerOptions.DefaultUserTokenSecretRef).Should().Be("still-good");
    }

    // ─────────────────────────────── Revoke ───────────────────────────────

    [Fact]
    public async Task Revoke_calls_the_broker_with_the_bearer_and_clears_the_token()
    {
        var secrets = new FakeSecretStore();
        secrets.Set(BrokerOptions.DefaultUserTokenSecretRef, "tok-to-revoke");
        secrets.Set(BrokerReferences.TokenMetadataSecretRef,
            new BrokerTokenMetadata("github:1", "jti", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)).Serialize());
        var handler = new BrokerStubHandler { OnRevoke = req => (HttpStatusCode.OK, "{\"revoked\":true}", req.Bearer) };
        var service = new BrokerTokenService(secrets, Active(), new HttpClient(handler), new MutableClock(DateTimeOffset.UtcNow));

        var result = await service.RevokeAsync();

        result.Success.Should().BeTrue();
        handler.LastRevokeBearer.Should().Be("tok-to-revoke");
        secrets.Get(BrokerOptions.DefaultUserTokenSecretRef).Should().BeNull("the CLI must not keep using a revoked token");
        secrets.Get(BrokerReferences.TokenMetadataSecretRef).Should().BeNull();
    }

    [Fact]
    public async Task Revoke_clears_the_token_locally_even_when_the_broker_is_unreachable()
    {
        var secrets = new FakeSecretStore();
        secrets.Set(BrokerOptions.DefaultUserTokenSecretRef, "tok");
        var handler = new BrokerStubHandler { OnRevoke = _ => throw new HttpRequestException("down") };
        var service = new BrokerTokenService(secrets, Active(), new HttpClient(handler), new MutableClock(DateTimeOffset.UtcNow));

        var result = await service.RevokeAsync();

        result.Success.Should().BeFalse();
        secrets.Get(BrokerOptions.DefaultUserTokenSecretRef).Should().BeNull();
    }

    // ─────────────────────────────── Status ───────────────────────────────

    [Fact]
    public void Status_is_inactive_in_the_single_user_default()
    {
        var service = new BrokerTokenService(new FakeSecretStore(), BrokerOptions.Disabled, new HttpClient(new BrokerStubHandler()));

        service.GetStatus().Should().Be(BrokerConnectionStatus.Inactive);
    }

    [Fact]
    public void Status_reports_valid_only_from_a_real_stored_token_with_a_future_expiry()
    {
        var secrets = new FakeSecretStore();
        SeedIdentity(secrets);
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        secrets.Set(BrokerOptions.DefaultUserTokenSecretRef, "tok");
        secrets.Set(BrokerReferences.TokenMetadataSecretRef,
            new BrokerTokenMetadata("github:99", "jti", clock.GetUtcNow(), clock.GetUtcNow().AddHours(1)).Serialize());

        var status = new BrokerTokenService(secrets, Active(), new HttpClient(new BrokerStubHandler()), clock).GetStatus();

        status.Active.Should().BeTrue();
        status.SignedIn.Should().BeTrue();
        status.Identity.Should().Be("github:99");
        status.TokenValid.Should().BeTrue();
    }

    [Fact]
    public void Status_reports_invalid_once_the_recorded_expiry_has_passed()
    {
        var secrets = new FakeSecretStore();
        SeedIdentity(secrets);
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        secrets.Set(BrokerOptions.DefaultUserTokenSecretRef, "tok");
        secrets.Set(BrokerReferences.TokenMetadataSecretRef,
            new BrokerTokenMetadata("github:99", "jti", clock.GetUtcNow().AddHours(-2), clock.GetUtcNow().AddHours(-1)).Serialize());

        new BrokerTokenService(secrets, Active(), new HttpClient(new BrokerStubHandler()), clock)
            .GetStatus().TokenValid.Should().BeFalse();
    }

    // ─────────────────────────────── helpers ───────────────────────────────

    private static string MintJson(string token, string tokenId, DateTimeOffset expiresAt) =>
        JsonSerializer.Serialize(new { token, tokenId, expiresAt });

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Routes the broker's mint/revoke calls to scripted responses and records the last request.</summary>
    private sealed class BrokerStubHandler : HttpMessageHandler
    {
        public Func<CapturedRequest, (HttpStatusCode Status, string Body, string? Echo)>? OnMint { get; set; }
        public Func<CapturedRequest, (HttpStatusCode Status, string Body, string? Echo)>? OnRevoke { get; set; }

        public int MintCalls { get; private set; }
        public string? LastMintBody { get; private set; }
        public string? LastRevokeBearer { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var bearer = request.Headers.Authorization?.Parameter;
            var captured = new CapturedRequest(body, bearer);
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/broker/token", StringComparison.Ordinal))
            {
                MintCalls++;
                LastMintBody = body;
                var (status, respBody, _) = (OnMint ?? (_ => (HttpStatusCode.OK, "{}", null)))(captured);
                return Json(status, respBody);
            }

            if (path.EndsWith("/broker/revoke", StringComparison.Ordinal))
            {
                LastRevokeBearer = bearer;
                var (status, respBody, _) = (OnRevoke ?? (_ => (HttpStatusCode.OK, "{}", null)))(captured);
                return Json(status, respBody);
            }

            return Json(HttpStatusCode.NotFound, "{}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed record CapturedRequest(string Body, string? Bearer);
}
