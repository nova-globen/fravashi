using System.Net;
using System.Text;
using Fravashi.Application.Onboarding;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Persistence;
using Fravashi.Application.Wiki;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Persistence.Configuration;
using Fravashi.Infrastructure.Tracker;
using Fravashi.Infrastructure.Wiki;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Fravashi.Infrastructure.Tests.Persistence;

/// <summary>
/// Driven, both-modes proof that Atlassian SSO is now functional end-to-end at the Confluence reader boundary
/// (the kept wiki-sync source). A 3LO access token does NOT authenticate against <c>{site}.atlassian.net</c>;
/// it authenticates against the api.atlassian.com gateway with a Bearer header. This test seeds a real Atlassian
/// connection (fake token + cloudId) through the REAL configuration pipeline (SQLite settings + secret bridge +
/// coordinate bridge) and the REAL reader DI registration, with the reader's HttpClient pointed at a capturing
/// stub, and shows the before/after:
/// <list type="bullet">
///   <item>PAT connection → the Confluence reader hits the SITE with <c>Basic</c> (unchanged).</item>
///   <item>3LO connection → it hits <c>api.atlassian.com/ex/confluence/{cloudId}/…</c> with <c>Bearer</c>.</item>
/// </list>
/// Pre-fix, a 3LO connection wrongly hit the site with Basic (a guaranteed 401); post-fix it routes through the
/// gateway with Bearer. Fake credentials only - the stub observes the request the reader built and never touches
/// the network.
/// </summary>
public sealed class AtlassianThreeLeggedDrivenTests
{
    private readonly ITestOutputHelper _output;

    public AtlassianThreeLeggedDrivenTests(ITestOutputHelper output) => _output = output;

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> AuthHeaders { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            AuthHeaders.Add(request.Headers.Authorization?.ToString() ?? string.Empty);
            var path = request.RequestUri!.AbsolutePath;
            var body = path.Contains("/spaces", StringComparison.Ordinal) ? "{\"results\":[]}" : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static IConfigurationRoot BuildConfiguration(
        SqliteTestContext context,
        SqliteProviderConnectionStore connections,
        SqliteSecretStore secrets,
        SqliteConfigurationStore settings)
    {
        var builder = new ConfigurationBuilder().AddFravashiDatabase(context.DatabasePath);
        var secretBridge = builder.AddFravashiSecretBridge();
        var coordinateBridge = builder.AddFravashiCoordinateBridge();
        var configuration = builder.Build();
        secretBridge.Provider.Attach(connections, secrets, settings);
        coordinateBridge.Provider.Attach(connections, settings);
        configuration.Reload();
        return configuration;
    }

    private static ServiceProvider BuildReader(IConfiguration configuration, CapturingHandler stub)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTrackerIntegration(configuration);
        // Point the reader's typed HttpClient at the stub so we observe the exact requests it builds.
        services.AddHttpClient<AtlassianConfluenceReader>().ConfigurePrimaryHttpMessageHandler(() => stub);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task ThreeLeggedConnection_RoutesConfluenceThroughGatewayWithBearer()
    {
        using var context = new SqliteTestContext();
        var connections = new SqliteProviderConnectionStore(context.Factory);
        var secrets = new SqliteSecretStore(context.Factory, new ReversibleTestProtector());
        var settings = new SqliteConfigurationStore(context.Factory);

        // A 3LO Atlassian sign-in as the auth-code callback leaves it: an OAuth access token (authcode → Bearer),
        // the cloudId captured under the lowercase area slug, a project, and NO account email. The same token +
        // cloudId serve Confluence (the default 3LO scope grants it). Fake creds only.
        const string cloudId = "cloud-3lo-777";
        settings.Set("Onboarding:Board:Provider", OnboardingProviders.Jira);
        settings.Set("Onboarding:Board:Project", "ENG");
        settings.Set("Onboarding:board:CloudId", cloudId);
        settings.Set(AuthSettings.AuthMethodKey("board"), AuthMethods.AuthCode);
        secrets.Set("board:token", CredentialEnvelope.Serialize(
            new StoredCredential("fake_3lo_access_token", "fake_refresh", DateTimeOffset.UtcNow.AddHours(1), AuthMethods.AuthCode)));
        connections.Upsert(new ProviderConnection(
            "board", ProviderKind.Board, "https://acme.atlassian.net", "board:token", "Signed in", DateTimeOffset.UtcNow));

        var configuration = BuildConfiguration(context, connections, secrets, settings);
        var stub = new CapturingHandler();
        using var provider = BuildReader(configuration, stub);

        var reader = provider.GetRequiredService<IConfluenceReader>();
        reader.IsConfigured.Should().BeTrue("token + domain + cloudId are all projected (Bearer mode)");

        await reader.ListSpacesAsync();

        var confluence = stub.Requests.First(r => r.RequestUri!.AbsolutePath.Contains("/spaces", StringComparison.Ordinal));
        confluence.RequestUri!.ToString().Should().StartWith(
            $"https://api.atlassian.com/ex/confluence/{cloudId}/wiki/api/v2/spaces");
        stub.AuthHeaders[stub.Requests.IndexOf(confluence)].Should().Be("Bearer fake_3lo_access_token");

        WriteEvidence("three-legged-gateway", reader.IsConfigured, stub);
    }

    [Fact]
    public async Task PatConnection_RoutesConfluenceToSiteWithBasic_Unchanged()
    {
        using var context = new SqliteTestContext();
        var connections = new SqliteProviderConnectionStore(context.Factory);
        var secrets = new SqliteSecretStore(context.Factory, new ReversibleTestProtector());
        var settings = new SqliteConfigurationStore(context.Factory);

        // A PAT connection as onboarding leaves it: a bare-string token (legacy PAT → Basic), an account email,
        // a project, and NO cloudId. This is the unchanged, byte-for-byte legacy path.
        settings.Set("Onboarding:Board:Provider", OnboardingProviders.Jira);
        settings.Set("Onboarding:Board:Email", "dev@acme.com");
        settings.Set("Onboarding:Board:Project", "ENG");
        secrets.Set("board:token", "fake_pat_token");
        connections.Upsert(new ProviderConnection(
            "board", ProviderKind.Board, "https://acme.atlassian.net", "board:token", "Verified", DateTimeOffset.UtcNow));

        var configuration = BuildConfiguration(context, connections, secrets, settings);
        var stub = new CapturingHandler();
        using var provider = BuildReader(configuration, stub);

        var reader = provider.GetRequiredService<IConfluenceReader>();
        reader.IsConfigured.Should().BeTrue();

        await reader.ListSpacesAsync();

        var expectedBasic = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("dev@acme.com:fake_pat_token"));

        var confluence = stub.Requests.First(r => r.RequestUri!.AbsolutePath.Contains("/spaces", StringComparison.Ordinal));
        confluence.RequestUri!.ToString().Should().StartWith("https://acme.atlassian.net/wiki/api/v2/spaces");
        stub.AuthHeaders[stub.Requests.IndexOf(confluence)].Should().Be(expectedBasic);

        WriteEvidence("pat-site", reader.IsConfigured, stub);
    }

    private void WriteEvidence(string name, bool readerConfigured, CapturingHandler stub)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== Atlassian SSO driven proof: {name} ===");
        sb.AppendLine();
        sb.AppendLine($"  reader.IsConfigured  = {readerConfigured}");
        sb.AppendLine($"  requests issued to the stub = {stub.Requests.Count}");
        for (var i = 0; i < stub.Requests.Count; i++)
        {
            sb.AppendLine($"    [{i}] {stub.Requests[i].Method} {stub.Requests[i].RequestUri}");
            sb.AppendLine($"        Authorization: {stub.AuthHeaders[i]}");
        }

        var evidence = sb.ToString();
        _output.WriteLine(evidence);

        var dir = Environment.GetEnvironmentVariable("ATLASSIAN_3LO_SHOTS");
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"{name}-driven.txt"), evidence);
        }
    }
}
