using Bunit;
using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.AgentRuntimes.Broker;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Persistence;
using Fravashi.Infrastructure.AgentRuntimes.Broker;
using Fravashi.Web.Components.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// The Settings broker card (plan 04 B3). Proves the honest status rendering: the "not enabled" state in the
/// single-user default, and - when active - that "authorized" shows only from a real valid token, with the
/// signed-in identity surfaced (attribution) and a revoke action available.
/// </summary>
public class BrokerConnectionCardTests
{
    private static BunitContext CardContext(BrokerOptions options, IBrokerTokenService broker)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<ISecretStore>(new FakeSecretStore());
        ctx.Services.AddSingleton<IConfigurationStore>(new FakeConfigurationStore());
        ctx.Services.AddProviderSignInForTests();
        // Registered last so the stub wins over the real typed-client IBrokerTokenService from the helper.
        ctx.Services.AddSingleton(options);
        ctx.Services.AddSingleton(broker);
        return ctx;
    }

    [Fact]
    public void Renders_the_not_enabled_state_in_the_single_user_default()
    {
        using var ctx = CardContext(BrokerOptions.Disabled, new StubBrokerTokenService(BrokerConnectionStatus.Inactive));

        var cut = ctx.Render<BrokerConnectionCard>();

        Assert.Contains("not enabled", cut.Find("[data-testid=broker-inactive]").TextContent);
    }

    [Fact]
    public void Shows_authorized_and_the_identity_only_when_a_valid_token_is_present()
    {
        var status = new BrokerConnectionStatus(
            Active: true, SignedIn: true, Identity: "github:4242", TokenValid: true,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1), LastMintUtc: DateTimeOffset.UtcNow);
        using var ctx = CardContext(new BrokerOptions { Enabled = true, BaseUrl = "https://broker.example" },
            new StubBrokerTokenService(status));

        var cut = ctx.Render<BrokerConnectionCard>();

        Assert.Contains("github:4242", cut.Find("[data-testid=broker-identity]").TextContent);
        Assert.Contains("Authorized", cut.Find("[data-testid=broker-token-state]").TextContent);
        cut.Find("[data-testid=broker-revoke]"); // a revoke control is offered (throws if absent)
    }

    [Fact]
    public void Does_not_claim_authorized_when_there_is_no_valid_token()
    {
        var status = new BrokerConnectionStatus(
            Active: true, SignedIn: false, Identity: null, TokenValid: false, ExpiresAt: null, LastMintUtc: null);
        using var ctx = CardContext(new BrokerOptions { Enabled = true, BaseUrl = "https://broker.example" },
            new StubBrokerTokenService(status));

        var cut = ctx.Render<BrokerConnectionCard>();

        Assert.Contains("no valid token", cut.Find("[data-testid=broker-token-state]").TextContent);
        Assert.NotEmpty(cut.FindAll("[data-testid=broker-signin-start]")); // an un-signed-in active tier offers sign-in
    }

    [Fact]
    public void Corp_broker_active_but_no_token_says_agent_runs_are_blocked_until_sign_in()
    {
        // corp-broker composed options: brokering on + local-key forbidden, with a broker endpoint. No valid token
        // yet → the card is honest that agent runs are blocked (mandatory sign-in), and surfaces the tier posture.
        var options = new BrokerOptions { BaseUrl = "https://broker.corp.internal" }.ForVariant(brokerRequired: true);
        var status = new BrokerConnectionStatus(
            Active: true, SignedIn: false, Identity: null, TokenValid: false, ExpiresAt: null, LastMintUtc: null);
        using var ctx = CardContext(options, new StubBrokerTokenService(status));

        var cut = ctx.Render<BrokerConnectionCard>();

        Assert.Contains("never reaches this device", cut.Find("[data-testid=broker-tier]").TextContent);
        Assert.Contains("blocked", cut.Find("[data-testid=broker-runs-blocked]").TextContent);
    }

    [Fact]
    public void Corp_broker_with_no_endpoint_is_honest_that_it_fails_closed()
    {
        // corp-broker image where the deploy never set Broker:BaseUrl: brokering is forced on + local key forbidden,
        // but the broker is inert (IsActive false → GetStatus Inactive). The card must NOT say "not enabled" (that is
        // the single-user copy) - it must say the tier is on and runs are blocked until the endpoint is configured.
        var options = new BrokerOptions { BaseUrl = null }.ForVariant(brokerRequired: true);
        using var ctx = CardContext(options, new StubBrokerTokenService(BrokerConnectionStatus.Inactive));

        var cut = ctx.Render<BrokerConnectionCard>();

        Assert.Empty(cut.FindAll("[data-testid=broker-inactive]"));
        Assert.Contains("not configured", cut.Find("[data-testid=broker-misconfigured]").TextContent);
    }

    [Theory]
    [InlineData("Europe/Stockholm", "14:00")]
    [InlineData("America/Los_Angeles", "05:00")]
    [InlineData("Asia/Kolkata", "17:30")]
    public void The_broker_outage_says_since_and_the_next_attempt_in_the_persons_zone(string zone, string hour)
    {
        // Backlog 246: the card renders the health's parts, so the person reads their own clock, not the container's.
        var noon = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var health = new BrokerTokenHealth(new FixedClock(noon));
        health.Unreachable("could not reach broker.example: name not resolved");
        var status = new BrokerConnectionStatus(
            Active: true, SignedIn: true, Identity: "github:4242", TokenValid: true,
            ExpiresAt: noon.AddHours(1), LastMintUtc: noon)
        {
            Trouble = health.Words,
            TroubleWords = health.Sentence,
        };
        using var ctx = CardContext(new BrokerOptions { Enabled = true, BaseUrl = "https://broker.example" },
            new StubBrokerTokenService(status));
        ctx.Services.AddSingleton(PersonZones.Fixed(zone));
        ctx.Services.AddSingleton<TimeProvider>(new FixedClock(noon.AddMinutes(1)));

        var cut = ctx.Render<BrokerConnectionCard>();

        Assert.Equal(
            $"Broker unreachable since {hour} (could not reach broker.example: name not resolved), retrying; next attempt at {hour}:15.",
            cut.Find("[data-testid=broker-trouble]").TextContent);
        Assert.Equal($"2026-10-07 {hour}", cut.Find("[data-testid=broker-last-mint]").TextContent);
    }

    // Backlog 269: the card follows the process's BrokerTokenHealth while it is rendered. These run over the real
    // BrokerTokenService (its GetStatus reads the secret store and the health; it never calls the HttpClient), so what
    // the card shows is what the refresher's mint really stores.
    private static readonly DateTimeOffset Noon = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly BrokerOptions Active = new() { Enabled = true, BaseUrl = "https://broker.example" };

    private static (BunitContext Ctx, FakeSecretStore Secrets, BrokerTokenHealth Health, CountingTokens Tokens) LiveCard()
    {
        var clock = new FixedClock(Noon);
        var secrets = new FakeSecretStore();
        var health = new BrokerTokenHealth(clock);
        secrets.Set(BrokerReferences.IdentitySecretRef,
            CredentialEnvelope.Serialize(
                StoredCredential.ForPat("idp-access")));
        Stored(secrets, "t1", minted: Noon.AddHours(-1), expires: Noon.AddHours(1));
        var tokens = new CountingTokens(new BrokerTokenService(
            secrets, Active, new HttpClient(), clock, health: health));

        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<ISecretStore>(secrets);
        ctx.Services.AddSingleton<IConfigurationStore>(new FakeConfigurationStore());
        ctx.Services.AddProviderSignInForTests();
        ctx.Services.AddSingleton(Active);
        ctx.Services.AddSingleton<IBrokerTokenService>(tokens);
        ctx.Services.AddSingleton(health);
        ctx.Services.AddSingleton(PersonZones.Fixed("Europe/Stockholm"));
        ctx.Services.AddSingleton<TimeProvider>(clock);
        return (ctx, secrets, health, tokens);
    }

    // What the refresher's mint stores before it raises Minted (BrokerTokenService.MintAsync).
    private static void Stored(FakeSecretStore secrets, string id, DateTimeOffset minted, DateTimeOffset expires)
    {
        secrets.Set(Active.UserTokenSecretRef, "token-" + id);
        secrets.Set(BrokerReferences.TokenMetadataSecretRef,
            new BrokerTokenMetadata("github:4242", id, minted, expires).Serialize());
    }

    [Fact]
    public async Task A_background_re_mint_updates_the_shown_expiry_and_last_mint()
    {
        var (ctx, secrets, health, _) = LiveCard();
        using var _ctx = ctx;
        var cut = ctx.Render<BrokerConnectionCard>();
        Assert.Equal("2026-10-07 15:00", cut.Find("[data-testid=broker-expires]").TextContent);
        Assert.Equal("2026-10-07 13:00", cut.Find("[data-testid=broker-last-mint]").TextContent);

        // The refresher re-mints at 11:57 UTC for an hour, from another thread, with nothing re-rendering the card. The
        // card shows Stockholm time (UTC+2 on this date): last mint 13:57, valid until 14:57.
        Stored(secrets, "t2", minted: Noon.AddMinutes(-3), expires: Noon.AddMinutes(57));
        await Task.Run(() => health.Minted("t2"));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("2026-10-07 14:57", cut.Find("[data-testid=broker-expires]").TextContent);
            Assert.Equal("2026-10-07 13:57", cut.Find("[data-testid=broker-last-mint]").TextContent);
        });
    }

    [Fact]
    public async Task An_outage_starting_shows_its_words_a_retry_moves_the_next_attempt_and_its_end_clears_them()
    {
        var (ctx, _, health, _) = LiveCard();
        using var _ctx = ctx;
        var cut = ctx.Render<BrokerConnectionCard>();
        Assert.Empty(cut.FindAll("[data-testid=broker-trouble]"));

        await Task.Run(() => health.Unreachable("could not reach broker.example: name not resolved"));
        cut.WaitForAssertion(() => Assert.Equal(
            "Broker unreachable since 14:00 (could not reach broker.example: name not resolved), retrying; next attempt at 14:00:15.",
            cut.Find("[data-testid=broker-trouble]").TextContent));

        // The next failed attempt puts the one after it back on the doubled backoff (30 s from the fixed clock).
        await Task.Run(() => health.Unreachable("could not reach broker.example: connection refused"));
        cut.WaitForAssertion(() => Assert.Equal(
            "Broker unreachable since 14:00 (could not reach broker.example: connection refused), retrying; next attempt at 14:00:30.",
            cut.Find("[data-testid=broker-trouble]").TextContent));

        await Task.Run(health.Answered);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=broker-trouble]")));
    }

    [Fact]
    public async Task A_refusal_shows_its_words_while_the_card_is_rendered()
    {
        var (ctx, _, health, _) = LiveCard();
        using var _ctx = ctx;
        var cut = ctx.Render<BrokerConnectionCard>();

        var words = BrokerTokenService.RefusalWords(403, "not_allowlisted", "github:4242");
        await Task.Run(() => health.Refuse(words, proofFingerprint: null));

        cut.WaitForAssertion(() => Assert.Equal(words, cut.Find("[data-testid=broker-trouble]").TextContent));
    }

    [Fact]
    public async Task A_disposed_card_no_longer_reads_the_status_when_the_health_changes()
    {
        var (ctx, secrets, health, tokens) = LiveCard();
        using var _ctx = ctx;
        ctx.Render<BrokerConnectionCard>();
        await ctx.DisposeComponentsAsync();
        var reads = tokens.Reads;

        Stored(secrets, "t2", minted: Noon, expires: Noon.AddHours(1));
        health.Minted("t2");
        health.Unreachable("could not reach broker.example: name not resolved");

        Assert.Equal(reads, tokens.Reads);
    }

    private sealed class CountingTokens(IBrokerTokenService inner) : IBrokerTokenService
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public Task<BrokerMintResult> MintAsync(CancellationToken cancellationToken = default) => inner.MintAsync(cancellationToken);
        public Task<BrokerMintResult?> RefreshIfNeededAsync(CancellationToken cancellationToken = default) =>
            inner.RefreshIfNeededAsync(cancellationToken);
        public Task<BrokerRevokeResult> RevokeAsync(CancellationToken cancellationToken = default) => inner.RevokeAsync(cancellationToken);

        public BrokerConnectionStatus GetStatus()
        {
            Interlocked.Increment(ref _reads);
            return inner.GetStatus();
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StubBrokerTokenService(BrokerConnectionStatus status) : IBrokerTokenService
    {
        public Task<BrokerMintResult> MintAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(BrokerMintResult.Fail("stub"));
        public Task<BrokerMintResult?> RefreshIfNeededAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<BrokerMintResult?>(null);
        public Task<BrokerRevokeResult> RevokeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(BrokerRevokeResult.Ok());
        public BrokerConnectionStatus GetStatus() => status;
    }
}
