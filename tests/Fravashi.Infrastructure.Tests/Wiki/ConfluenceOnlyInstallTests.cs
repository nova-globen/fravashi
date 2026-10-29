using System.Net;
using System.Text;
using Fravashi.Application.Git;
using Fravashi.Application.Onboarding;
using Fravashi.Application.Persistence;
using Fravashi.Application.Wiki;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Persistence.Configuration;
using Fravashi.Infrastructure.Tests.Persistence;
using Fravashi.Infrastructure.Tracker;
using Fravashi.Infrastructure.Wiki;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Wiki;

/// <summary>
/// The Confluence-only install (backlog 12): a wiki is configured and no board is. Nothing in that shape ever
/// produces a Jira PROJECT key, which is a Jira coordinate and means nothing to Confluence, yet the Atlassian
/// adapter's single validity flag demanded one, so the reader reported not-configured and every read returned
/// an empty list. These tests drive the real chain the product uses after onboarding: the SQLite stores the
/// wizard writes, the real secret and coordinate bridges, the real reader DI registration, then the wiki-source
/// resolver and the Confluence source that wiki sync actually runs. Only the network is a stub, and it answers
/// with real Confluence v2 shapes, so "returns real pages" is measured rather than asserted about a mock.
/// </summary>
public sealed class ConfluenceOnlyInstallTests
{
    private const string SpaceId = "111";
    private const string PageId = "9001";

    /// <summary>Answers the three Confluence Cloud v2 reads the source performs, and records every request.</summary>
    private sealed class ConfluenceStub : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        public List<string> AuthHeaders { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Urls.Add(url);
            AuthHeaders.Add(request.Headers.Authorization?.ToString() ?? string.Empty);

            var page = "{\"id\":\"" + PageId + "\",\"status\":\"current\",\"title\":\"Home\",\"spaceId\":\"" + SpaceId + "\"";
            var body = url switch
            {
                var u when u.Contains("/spaces", StringComparison.Ordinal) =>
                    "{\"results\":[{\"id\":\"" + SpaceId + "\",\"key\":\"DOCS\",\"name\":\"Documentation\",\"type\":\"global\",\"status\":\"current\"}]}",
                var u when u.Contains("/pages/" + PageId, StringComparison.Ordinal) =>
                    page + ",\"body\":{\"storage\":{\"value\":\"<p>hello from confluence</p>\",\"representation\":\"storage\"}}}",
                var u when u.Contains("/pages", StringComparison.Ordinal) =>
                    "{\"results\":[" + page + "}]}",
                _ => "{}",
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed record Stores(
        SqliteProviderConnectionStore Connections,
        SqliteSecretStore Secrets,
        SqliteConfigurationStore Settings);

    /// <summary>Exactly what the wizard's wiki step leaves behind for a Confluence PAT, and nothing else: no
    /// board provider, no board connection, and therefore no project key anywhere in the store.</summary>
    private static Stores SeedConfluenceOnlyInstall(SqliteTestContext context)
    {
        var stores = EmptyStores(context);
        ConnectConfluence(stores);
        return stores;
    }

    private static Stores EmptyStores(SqliteTestContext context) => new(
        new SqliteProviderConnectionStore(context.Factory),
        new SqliteSecretStore(context.Factory, new ReversibleTestProtector()),
        new SqliteConfigurationStore(context.Factory));

    private static void ConnectConfluence(Stores stores)
    {
        stores.Settings.Set("Onboarding:Wiki:Provider", OnboardingProviders.Confluence);
        stores.Settings.Set("Onboarding:Wiki:Email", "docs@acme.com");
        stores.Secrets.Set("wiki:token", "fake_confluence_token");
        stores.Connections.Upsert(new ProviderConnection(
            "wiki", ProviderKind.Wiki, "https://acme.atlassian.net", "wiki:token", "Verified", DateTimeOffset.UtcNow));
    }

    private static IConfigurationRoot BuildConfiguration(SqliteTestContext context, Stores stores)
    {
        var builder = new ConfigurationBuilder().AddFravashiDatabase(context.DatabasePath);
        var secret = builder.AddFravashiSecretBridge();
        var coordinate = builder.AddFravashiCoordinateBridge();
        var configuration = builder.Build();
        secret.Provider.Attach(stores.Connections, stores.Secrets, stores.Settings);
        coordinate.Provider.Attach(stores.Connections, stores.Settings);
        configuration.Reload();
        return configuration;
    }

    private static ServiceProvider BuildReader(IConfiguration configuration, ConfluenceStub stub)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTrackerIntegration(configuration);
        services.AddHttpClient<AtlassianConfluenceReader>().ConfigurePrimaryHttpMessageHandler(() => stub);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void ConfluenceOnlyInstall_ProjectsNoJiraProject()
    {
        using var context = new SqliteTestContext();
        var stores = SeedConfluenceOnlyInstall(context);

        var configuration = BuildConfiguration(context, stores);

        // The premise of the whole item: the project key is a BOARD coordinate. A wiki-only install has no
        // board, so nothing populates it and nothing can.
        configuration["Atlassian:JiraProject"].Should().BeNull();
        configuration["Atlassian:JiraDomain"].Should().Be("acme.atlassian.net");
        configuration["Atlassian:JiraEmail"].Should().Be("docs@acme.com");
        configuration["Atlassian:JiraKey"].Should().Be("fake_confluence_token");
    }

    [Fact]
    public void ConfluenceOnlyInstall_ReportsTheWikiConfigured()
    {
        using var context = new SqliteTestContext();
        var stores = SeedConfluenceOnlyInstall(context);
        var configuration = BuildConfiguration(context, stores);
        using var provider = BuildReader(configuration, new ConfluenceStub());

        provider.GetRequiredService<IConfluenceReader>().IsConfigured
            .Should().BeTrue("a Confluence site needs a token, a domain and an account email, not a Jira project");
    }

    [Fact]
    public async Task ConfluenceOnlyInstall_ReturnsRealPages()
    {
        using var context = new SqliteTestContext();
        var stores = SeedConfluenceOnlyInstall(context);
        var configuration = BuildConfiguration(context, stores);
        var stub = new ConfluenceStub();
        using var provider = BuildReader(configuration, stub);
        var reader = provider.GetRequiredService<IConfluenceReader>();

        var spaces = await reader.ListSpacesAsync();
        spaces.Should().ContainSingle().Which.Key.Should().Be("DOCS");

        var pages = await reader.ListPagesAsync("DOCS");
        pages.Should().ContainSingle().Which.Title.Should().Be("Home");

        var page = await reader.GetPageAsync(PageId);
        page!.BodyHtml.Should().Be("<p>hello from confluence</p>");

        // The requests really went to the site with the PAT's Basic header (the legacy contract, unchanged).
        stub.Urls.Should().AllSatisfy(u => u.Should().StartWith("https://acme.atlassian.net/wiki/api/v2/"));
        stub.AuthHeaders.Should().AllSatisfy(h => h.Should().Be(
            "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("docs@acme.com:fake_confluence_token"))));
    }

    /// <summary>
    /// The ordering every test above leaves out, and the one the installed product actually met (2026-09-21): the
    /// studio is ALREADY RUNNING and has already asked the reader once (the Home page's wiki card does, on first
    /// render) when the wiki is connected from Settings. The bridges re-project on the reload, but a consumer
    /// that bound its options once for the life of the process never hears it, so a verified connection kept
    /// reading "not fully wired yet" until the container restarted.
    /// </summary>
    [Fact]
    public void A_wiki_connected_while_the_studio_runs_is_configured_for_the_next_reader_without_a_restart()
    {
        using var context = new SqliteTestContext();
        var stores = EmptyStores(context);
        var configuration = BuildConfiguration(context, stores);
        using var provider = BuildReader(configuration, new ConfluenceStub());

        using (var before = provider.CreateScope())
        {
            before.ServiceProvider.GetRequiredService<IConfluenceReader>().IsConfigured
                .Should().BeFalse("nothing is connected yet, and this first ask is what froze the answer");
        }

        ConnectConfluence(stores);
        configuration.Reload();

        using var after = provider.CreateScope();
        after.ServiceProvider.GetRequiredService<IConfluenceReader>().IsConfigured
            .Should().BeTrue("the connection was saved and the configuration reloaded; a restart must not be needed");
    }

    /// <summary>
    /// The same fact for a reader that is ALREADY HELD. A Blazor circuit is one DI scope for as long as the window
    /// is open, so the reader a page resolved before the connection existed is the reader it keeps. It has to
    /// become configured too, and its requests have to carry the credential that arrived after it was built.
    /// </summary>
    [Fact]
    public async Task A_reader_held_since_before_the_wiki_was_connected_reads_real_pages_after_the_reload()
    {
        using var context = new SqliteTestContext();
        var stores = EmptyStores(context);
        var configuration = BuildConfiguration(context, stores);
        var stub = new ConfluenceStub();
        using var provider = BuildReader(configuration, stub);
        using var circuit = provider.CreateScope();
        var reader = circuit.ServiceProvider.GetRequiredService<IConfluenceReader>();
        reader.IsConfigured.Should().BeFalse();
        (await reader.ListSpacesAsync()).Should().BeEmpty();
        stub.Urls.Should().BeEmpty("an unconfigured reader has no site to ask");

        ConnectConfluence(stores);
        configuration.Reload();

        reader.IsConfigured.Should().BeTrue();
        (await reader.ListSpacesAsync()).Should().ContainSingle().Which.Key.Should().Be("DOCS");
        stub.AuthHeaders.Should().AllSatisfy(h => h.Should().Be(
            "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("docs@acme.com:fake_confluence_token"))));
    }

    /// <summary>
    /// A site shaped like a current Confluence Cloud one, which <see cref="ConfluenceStub"/> is not: it HONOURS the
    /// <c>type</c> filter it is sent, the way the real API does, and none of its team spaces is <c>global</c>.
    /// The v2 contract's space types are global, collaboration, knowledge_base, personal, system, onboarding and
    /// xflow_sample_space, and the parameter takes one value.
    /// </summary>
    private sealed class TypedSpacesStub : HttpMessageHandler
    {
        private static readonly (string Id, string Key, string Type)[] Spaces =
        [
            ("1", "TEAM", "collaboration"),
            ("2", "KB", "knowledge_base"),
            ("3", "~alice", "personal"),
            ("4", "SYS", "system"),
        ];

        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Urls.Add(url);

            var body = "{\"results\":[]}";
            if (url.Contains("/spaces", StringComparison.Ordinal))
            {
                var wanted = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["type"];
                var rows = Spaces
                    .Where(s => wanted is null || s.Type == wanted)
                    .Select(s => "{\"id\":\"" + s.Id + "\",\"key\":\"" + s.Key + "\",\"name\":\"" + s.Key
                        + "\",\"type\":\"" + s.Type + "\",\"status\":\"current\"}");
                body = "{\"results\":[" + string.Join(',', rows) + "]}";
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>
    /// Met in the installed product on 2026-09-21: the wizard's probe resolved a space BY KEY, wrote a page to it
    /// and verified the connection, then the sync listed spaces with <c>type=global</c>, got a 200 with nothing in
    /// it, and reported "Synced 0 page(s)". The filter was there to skip personal spaces and it skipped every team
    /// space a current site creates as well.
    /// </summary>
    [Fact]
    public async Task Team_spaces_of_every_current_type_are_listed_and_personal_and_system_ones_are_not()
    {
        using var context = new SqliteTestContext();
        var stores = SeedConfluenceOnlyInstall(context);
        var configuration = BuildConfiguration(context, stores);
        var stub = new TypedSpacesStub();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTrackerIntegration(configuration);
        services.AddHttpClient<AtlassianConfluenceReader>().ConfigurePrimaryHttpMessageHandler(() => stub);
        using var provider = services.BuildServiceProvider();

        var spaces = await provider.GetRequiredService<IConfluenceReader>().ListSpacesAsync();

        spaces.Select(s => s.Key).Should().BeEquivalentTo(["TEAM", "KB"]);
    }

    [Fact]
    public async Task ConfluenceOnlyInstall_ResolvesAsAReadyWikiSource_ThatFetchesPages()
    {
        using var context = new SqliteTestContext();
        var stores = SeedConfluenceOnlyInstall(context);
        var configuration = BuildConfiguration(context, stores);
        var stub = new ConfluenceStub();
        using var provider = BuildReader(configuration, stub);

        var resolver = new WikiSourceResolver(
            stores.Connections,
            stores.Settings,
            provider.GetRequiredService<IConfluenceReader>(),
            stores.Secrets,
            Substitute.For<IGitProcessRunner>(),
            Substitute.For<IHttpClientFactory>());

        var resolution = resolver.Resolve();
        resolution.Status.Should().Be(WikiSourceStatus.Ready, "the wiki is configured, so sync has a source");
        resolution.Source.Should().NotBeNull();

        var fetched = await resolution.Source!.FetchAllAsync(progress: null);
        fetched.Should().ContainSingle();
        fetched[0].Title.Should().Be("Home");
        fetched[0].Body.Should().Be("<p>hello from confluence</p>");
    }
}
