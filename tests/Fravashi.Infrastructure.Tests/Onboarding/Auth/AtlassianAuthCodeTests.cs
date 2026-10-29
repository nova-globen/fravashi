using System.Net;
using System.Text;
using Fravashi.Application.Onboarding;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Persistence;
using Fravashi.Infrastructure.Onboarding.Auth;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Tests.Persistence;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Onboarding.Auth;

/// <summary>
/// The Atlassian 3LO authenticator (authorize URL + code exchange), the CSRF state ledger, and the callback
/// orchestration. Every HTTP call is answered in-process - no test contacts Atlassian.
/// </summary>
public sealed class AtlassianAuthCodeTests
{
    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static AuthConfigStore ConfiguredStore(SqliteTestContext ctx, out ISecretStore secrets, out IConfigurationStore settings)
    {
        settings = new SqliteConfigurationStore(ctx.Factory);
        secrets = new SqliteSecretStore(ctx.Factory, new ReversibleTestProtector());
        settings.Set(AuthSettings.AtlassianClientId, "atl-client");
        settings.Set(AuthSettings.AtlassianAuthorizeUrl, "https://fake-idp/authorize");
        settings.Set(AuthSettings.AtlassianTokenUrl, "https://fake-idp/token");
        settings.Set(AuthSettings.AtlassianResourcesUrl, "https://fake-idp/resources");
        secrets.Set(AuthSettings.AtlassianClientSecretRef, "atl-secret");
        return new AuthConfigStore(settings, secrets);
    }

    [Fact]
    public void BeginAuthorization_builds_a_url_with_state_and_the_required_params()
    {
        using var ctx = new SqliteTestContext();
        var store = ConfiguredStore(ctx, out _, out _);
        var auth = new AtlassianAuthCodeAuthenticator(new HttpClient(new RouteHandler(_ => Json(HttpStatusCode.OK, "{}"))), store);

        var challenge = auth.BeginAuthorization("http://localhost:5090/auth/callback/atlassian");

        challenge.AuthorizeUrl.Should().StartWith("https://fake-idp/authorize?");
        challenge.AuthorizeUrl.Should().Contain("client_id=atl-client");
        challenge.AuthorizeUrl.Should().Contain("response_type=code");
        challenge.AuthorizeUrl.Should().Contain("prompt=consent");
        challenge.AuthorizeUrl.Should().Contain("audience=api.atlassian.com");
        challenge.AuthorizeUrl.Should().Contain($"state={challenge.State}");
        challenge.AuthorizeUrl.Should().Contain(Uri.EscapeDataString("http://localhost:5090/auth/callback/atlassian"));
        challenge.State.Should().NotBeNullOrEmpty();

        // A second call issues a distinct state (CSRF values must be unpredictable and unique).
        auth.BeginAuthorization("http://localhost:5090/auth/callback/atlassian").State.Should().NotBe(challenge.State);
    }

    [Fact]
    public async Task ExchangeCodeAsync_returns_an_authcode_credential_on_success()
    {
        using var ctx = new SqliteTestContext();
        var store = ConfiguredStore(ctx, out _, out _);
        var auth = new AtlassianAuthCodeAuthenticator(new HttpClient(new RouteHandler(r =>
            r.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, """{"access_token":"atl-access","refresh_token":"atl-refresh","expires_in":3600,"scope":"read:jira-work"}""")
                : Json(HttpStatusCode.NotFound, "{}"))), store);

        var result = await auth.ExchangeCodeAsync("the-code", "http://localhost:5090/auth/callback/atlassian");

        result.Success.Should().BeTrue();
        result.Credential!.AccessToken.Should().Be("atl-access");
        result.Credential.RefreshToken.Should().Be("atl-refresh");
        result.Credential.Method.Should().Be(AuthMethods.AuthCode);
    }

    [Fact]
    public async Task ExchangeCodeAsync_fails_when_the_endpoint_returns_an_error()
    {
        using var ctx = new SqliteTestContext();
        var store = ConfiguredStore(ctx, out _, out _);
        var auth = new AtlassianAuthCodeAuthenticator(new HttpClient(new RouteHandler(_ =>
            Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"code expired"}"""))), store);

        var result = await auth.ExchangeCodeAsync("bad", "http://localhost/cb");

        result.Success.Should().BeFalse();
        result.Detail.Should().Contain("code expired");
    }

    // ── CSRF state ledger ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StateStore_returns_an_entry_once_then_never_again()
    {
        var store = new InMemoryAuthCodeStateStore();
        var entry = new AuthCodeStateEntry("st-1", OnboardingProviders.Jira, "board", "board", "board:token", "https://s", "http://localhost/cb", "wizard", DateTimeOffset.UtcNow);
        store.Put(entry);

        store.Consume("st-1").Should().BeSameAs(entry);
        store.Consume("st-1").Should().BeNull();     // single-use - a replay fails
        store.Consume("unknown").Should().BeNull();  // a forged state fails
    }

    [Fact]
    public void StateStore_rejects_an_expired_state()
    {
        var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryAuthCodeStateStore(time);
        store.Put(new AuthCodeStateEntry("st", OnboardingProviders.Confluence, "wiki", "wiki", "wiki:token", null, "http://localhost/cb", "wizard", time.GetUtcNow()));

        time.Now = time.Now.Add(InMemoryAuthCodeStateStore.Lifetime).AddMinutes(1);

        store.Consume("st").Should().BeNull();
    }

    // ── Callback orchestration ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Callback_with_an_unknown_state_is_rejected_as_csrf()
    {
        using var ctx = new SqliteTestContext();
        var store = ConfiguredStore(ctx, out var secrets, out var settings);
        var connections = new SqliteProviderConnectionStore(ctx.Factory);
        var reloader = new CountingReloader();
        var callback = new AuthCodeCallbackService(new InMemoryAuthCodeStateStore(),
            new AtlassianAuthCodeAuthenticator(new HttpClient(new RouteHandler(_ => Json(HttpStatusCode.OK, "{}"))), store),
            secrets, connections, settings, reloader);

        var result = await callback.HandleAsync("code", "forged-state", error: null);

        result.RedirectPath.Should().Be("/onboarding?signin=csrf");
        connections.GetAll().Should().BeEmpty();
    }

    [Fact]
    public async Task Callback_success_stores_the_credential_connection_and_provider_then_reloads()
    {
        using var ctx = new SqliteTestContext();
        var store = ConfiguredStore(ctx, out var secrets, out var settings);
        var connections = new SqliteProviderConnectionStore(ctx.Factory);
        var states = new InMemoryAuthCodeStateStore();
        var reloader = new CountingReloader();

        var auth = new AtlassianAuthCodeAuthenticator(new HttpClient(new RouteHandler(r =>
            r.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, """{"access_token":"atl-access","refresh_token":"atl-refresh","expires_in":3600}""")
                : Json(HttpStatusCode.OK, """[{"id":"cloud-123","name":"Acme","url":"https://acme.atlassian.net"}]"""))), store);

        var callback = new AuthCodeCallbackService(states, auth, secrets, connections, settings, reloader);
        states.Put(new AuthCodeStateEntry("st-ok", OnboardingProviders.Jira, "board", "board", "board:token", "https://acme.atlassian.net", "http://localhost/cb", "wizard", DateTimeOffset.UtcNow));

        var result = await callback.HandleAsync("the-code", "st-ok", error: null);

        result.RedirectPath.Should().Be("/onboarding/board?signin=ok");

        var connection = connections.Get("board");
        connection.Should().NotBeNull();
        connection!.Status.Should().Be("Signed in");
        connection.CredentialRef.Should().Be("board:token");

        // The credential is stored as an envelope carrying the refresh token (never a bare PAT here).
        var stored = CredentialEnvelope.Parse(secrets.Get("board:token"));
        stored!.AccessToken.Should().Be("atl-access");
        stored.RefreshToken.Should().Be("atl-refresh");
        stored.Method.Should().Be(AuthMethods.AuthCode);

        settings.Get("Onboarding:Board:Provider").Should().Be(OnboardingProviders.Jira);
        settings.Get("Tracker:Provider").Should().Be(OnboardingProviders.Jira);
        settings.Get(AuthSettings.AuthMethodKey("board")).Should().Be(AuthMethods.AuthCode);
        settings.Get("Onboarding:board:CloudId").Should().Be("cloud-123");
        reloader.Count.Should().Be(1);
    }

    [Fact]
    public async Task Callback_in_settings_mode_returns_to_the_settings_sidebar_location()
    {
        // T2 OAuth-return fix: a settings-hosted sign-in round-trips back to the Settings Environment section,
        // not the onboarding wizard route (the step renders under /settings/{area}).
        using var ctx = new SqliteTestContext();
        var store = ConfiguredStore(ctx, out var secrets, out var settings);
        var connections = new SqliteProviderConnectionStore(ctx.Factory);
        var states = new InMemoryAuthCodeStateStore();

        var auth = new AtlassianAuthCodeAuthenticator(new HttpClient(new RouteHandler(r =>
            r.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, """{"access_token":"atl-access","refresh_token":"atl-refresh","expires_in":3600}""")
                : Json(HttpStatusCode.OK, """[{"id":"cloud-123","name":"Acme","url":"https://acme.atlassian.net"}]"""))), store);

        var callback = new AuthCodeCallbackService(states, auth, secrets, connections, settings, new CountingReloader());
        states.Put(new AuthCodeStateEntry("st-set", OnboardingProviders.Jira, "board", "board", "board:token",
            "https://acme.atlassian.net", "http://localhost/cb", "settings", DateTimeOffset.UtcNow));

        var result = await callback.HandleAsync("the-code", "st-set", error: null);

        result.RedirectPath.Should().Be("/settings/board?signin=ok");
    }

    [Fact]
    public async Task Callback_with_a_user_denial_redirects_without_storing()
    {
        using var ctx = new SqliteTestContext();
        var store = ConfiguredStore(ctx, out var secrets, out var settings);
        var connections = new SqliteProviderConnectionStore(ctx.Factory);
        var states = new InMemoryAuthCodeStateStore();
        var callback = new AuthCodeCallbackService(states,
            new AtlassianAuthCodeAuthenticator(new HttpClient(new RouteHandler(_ => Json(HttpStatusCode.OK, "{}"))), store),
            secrets, connections, settings, new CountingReloader());
        states.Put(new AuthCodeStateEntry("st-d", OnboardingProviders.Confluence, "wiki", "wiki", "wiki:token", null, "http://localhost/cb", "wizard", DateTimeOffset.UtcNow));

        var result = await callback.HandleAsync(code: null, "st-d", error: "access_denied");

        result.RedirectPath.Should().Be("/onboarding/wiki?signin=denied");
        connections.Get("wiki").Should().BeNull();
    }

    private sealed class CountingReloader : IConfigurationReloader
    {
        public int Count { get; private set; }
        public void Reload() => Count++;
    }
}
