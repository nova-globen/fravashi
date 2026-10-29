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
/// The token refresher: an expiring OAuth credential is renewed via its refresh token, the new envelope is
/// written back, and a reload is triggered so the bridge re-projects the fresh access token. PATs and
/// non-expiring credentials are no-ops. No live IdP is contacted.
/// </summary>
public sealed class ProviderTokenRefresherTests
{
    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CountingReloader : IConfigurationReloader
    {
        public int Count { get; private set; }
        public void Reload() => Count++;
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static ProviderTokenRefresher Build(
        SqliteTestContext ctx, HttpMessageHandler handler, CountingReloader reloader,
        out ISecretStore secrets, out IConfigurationStore settings, out IProviderConnectionStore connections)
    {
        settings = new SqliteConfigurationStore(ctx.Factory);
        secrets = new SqliteSecretStore(ctx.Factory, new ReversibleTestProtector());
        connections = new SqliteProviderConnectionStore(ctx.Factory);
        var config = new AuthConfigStore(settings, secrets);
        return new ProviderTokenRefresher(connections, secrets, settings, reloader, config, new HttpClient(handler),
            new FixedTimeProvider(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Entra_expiring_token_is_refreshed_and_the_new_envelope_is_stored()
    {
        using var ctx = new SqliteTestContext();
        var reloader = new CountingReloader();
        var refresher = Build(ctx, new RouteHandler(_ =>
            Json(HttpStatusCode.OK, """{"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600}""")),
            reloader, out var secrets, out var settings, out var connections);

        settings.Set(AuthSettings.AzureDevOpsClientId, "entra-client");
        settings.Set(AuthSettings.AzureDevOpsTokenUrl, "https://fake-idp/token");
        settings.Set("Onboarding:Board:Provider", OnboardingProviders.AzureDevOps);
        // An already-expired device credential with a refresh token.
        secrets.Set("board:token", CredentialEnvelope.Serialize(new StoredCredential("old-access", "old-refresh", DateTimeOffset.UtcNow.AddMinutes(-5), AuthMethods.Device)));
        connections.Upsert(new ProviderConnection("board", ProviderKind.Board, "https://dev.azure.com/o", "board:token", "Verified", DateTimeOffset.UtcNow));

        var refreshed = await refresher.RefreshIfNeededAsync("board");

        refreshed.Should().BeTrue();
        var stored = CredentialEnvelope.Parse(secrets.Get("board:token"));
        stored!.AccessToken.Should().Be("new-access");
        stored.RefreshToken.Should().Be("new-refresh");
        stored.Method.Should().Be(AuthMethods.Device);
        reloader.Count.Should().Be(1);
    }

    [Fact]
    public async Task Atlassian_expiring_token_is_refreshed()
    {
        using var ctx = new SqliteTestContext();
        var reloader = new CountingReloader();
        var refresher = Build(ctx, new RouteHandler(_ =>
            Json(HttpStatusCode.OK, """{"access_token":"atl-new","refresh_token":"atl-rot","expires_in":3600}""")),
            reloader, out var secrets, out var settings, out var connections);

        settings.Set(AuthSettings.AtlassianClientId, "atl-client");
        settings.Set(AuthSettings.AtlassianTokenUrl, "https://fake-idp/token");
        secrets.Set(AuthSettings.AtlassianClientSecretRef, "atl-secret");
        settings.Set("Onboarding:Board:Provider", OnboardingProviders.Jira);
        secrets.Set("board:token", CredentialEnvelope.Serialize(new StoredCredential("atl-old", "atl-old-refresh", DateTimeOffset.UtcNow.AddMinutes(-1), AuthMethods.AuthCode)));
        connections.Upsert(new ProviderConnection("board", ProviderKind.Board, "https://acme.atlassian.net", "board:token", "Signed in", DateTimeOffset.UtcNow));

        (await refresher.RefreshIfNeededAsync("board")).Should().BeTrue();
        CredentialEnvelope.Parse(secrets.Get("board:token"))!.AccessToken.Should().Be("atl-new");
    }

    [Fact]
    public async Task A_pat_is_never_refreshed()
    {
        using var ctx = new SqliteTestContext();
        var reloader = new CountingReloader();
        var refresher = Build(ctx, new RouteHandler(_ => Json(HttpStatusCode.OK, "{}")),
            reloader, out var secrets, out var settings, out var connections);

        settings.Set("Onboarding:Board:Provider", OnboardingProviders.AzureDevOps);
        secrets.Set("board:token", "raw-pat");   // legacy bare PAT
        connections.Upsert(new ProviderConnection("board", ProviderKind.Board, "https://dev.azure.com/o", "board:token", "Verified", DateTimeOffset.UtcNow));

        (await refresher.RefreshIfNeededAsync("board")).Should().BeFalse();
        reloader.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_credential_that_is_not_expiring_is_left_alone()
    {
        using var ctx = new SqliteTestContext();
        var reloader = new CountingReloader();
        var refresher = Build(ctx, new RouteHandler(_ => Json(HttpStatusCode.OK, "{}")),
            reloader, out var secrets, out var settings, out var connections);

        settings.Set("Onboarding:Board:Provider", OnboardingProviders.AzureDevOps);
        secrets.Set("board:token", CredentialEnvelope.Serialize(new StoredCredential("a", "r", DateTimeOffset.UtcNow.AddHours(1), AuthMethods.Device)));
        connections.Upsert(new ProviderConnection("board", ProviderKind.Board, "https://dev.azure.com/o", "board:token", "Verified", DateTimeOffset.UtcNow));

        (await refresher.RefreshIfNeededAsync("board")).Should().BeFalse();
    }
}
