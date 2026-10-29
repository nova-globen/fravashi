using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Fravashi.Application.Mirroring;
using Fravashi.Application.Onboarding;
using Fravashi.Application.Persistence;
using Fravashi.Infrastructure.Wiki;
using NSubstitute;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Wiki;

/// <summary>
/// Backlog 144: an Azure DevOps project wiki behind the publishing port, over recorded HTTP. Held: a page lands under its
/// parent's path, the markdown crosses unconverted, a write refuses when the page moved since it was published, the
/// change itself carries the ETag it read, and a page is always read in the wiki it was created in.
/// </summary>
public sealed class AzureDevOpsWikiPageGatewayTests
{
    private const string Project = "https://dev.azure.com/acme/Studio";
    private const string WikiId = "11111111-2222-3333-4444-555555555555";

    private const string Wikis = """{"value":[{"id":"99999999-0000-0000-0000-000000000000","name":"Code","type":"codeWiki"},{"id":"11111111-2222-3333-4444-555555555555","name":"Studio.wiki","type":"projectWiki"}]}""";

    private static string Page(int id, string path, string content = "") =>
        JsonSerializer.Serialize(new { id, path, content, remoteUrl = $"https://dev.azure.com/acme/Studio/_wiki/wikis/Studio.wiki/{id}" });

    private static HttpResponseMessage Json(string body, string? etag = null, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (etag is not null)
        {
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"" + etag + "\"");
        }

        return response;
    }

    private static (AzureDevOpsWikiPageGateway Gateway, AtlassianConfluencePageGatewayTests.Recorder Http) Build(
        Func<HttpRequestMessage, string?, HttpResponseMessage> respond, string provider = OnboardingProviders.AzureDevOpsWiki)
    {
        var connections = Substitute.For<IProviderConnectionStore>();
        connections.Get("wiki").Returns(new ProviderConnection("wiki", ProviderKind.Wiki, Project, "wiki:token", "Verified", DateTimeOffset.UtcNow));
        var settings = Substitute.For<IConfigurationStore>();
        settings.Get("Onboarding:Wiki:Provider").Returns(provider);
        var secrets = Substitute.For<ISecretStore>();
        secrets.Get("wiki:token").Returns("wiki-pat");
        var recorder = new AtlassianConfluencePageGatewayTests.Recorder(respond);
        return (new AzureDevOpsWikiPageGateway(new HttpClient(recorder), connections, settings, secrets), recorder);
    }

    [Fact]
    public async Task A_page_under_a_parent_is_created_at_the_parents_path_in_the_parents_wiki_as_markdown()
    {
        var (gateway, http) = Build((request, _) =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            return request.Method == HttpMethod.Put ? Json(Page(8, "/adr/0027: The verifier - part 1", "# Heading"), "abc123")
                : url.Contains("/pages/4?", StringComparison.Ordinal) ? Json(Page(4, "/adr"), "p1")
                : Json(Wikis);
        });

        var created = await gateway.CreateAsync(AzureDevOpsWikiPageGateway.ProjectWiki, "0027: The verifier / part 1", "# Heading", $"{WikiId}:4");

        created.IsOk.Should().BeTrue(created.Reason);
        var put = http.Requests.Single(r => r.Method == HttpMethod.Put);
        put.Url.Should().Be($"{Project}/_apis/wiki/wikis/{WikiId}/pages?path=%2Fadr%2F0027%3A%20The%20verifier%20-%20part%201&api-version=7.1");
        put.Body.Should().Be("""{"content":"# Heading"}""");
        put.Authorization.Should().Be("Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(":wiki-pat")));
        created.Value!.Id.Should().Be($"{WikiId}:8");
        created.Value.Title.Should().Be("0027: The verifier - part 1");
        created.Value.Version.Should().Be(AzureDevOpsWikiPageGateway.VersionOf("abc123"));
        gateway.ToSite("| a |\n|---|\n").Lossless.Should().BeTrue("the wiki is markdown, so nothing is converted or lost");
    }

    [Fact]
    public async Task With_no_wiki_named_the_projects_own_wiki_is_used()
    {
        var (gateway, http) = Build((request, _) => request.Method == HttpMethod.Put ? Json(Page(1, "/Vision"), "e1") : Json(Wikis));

        await gateway.CreateAsync(AzureDevOpsWikiPageGateway.ProjectWiki, "Vision", "Where we go.");

        http.Requests.Single(r => r.Method == HttpMethod.Put).Url.Should().Contain($"/wikis/{WikiId}/pages?path=%2FVision");
    }

    [Fact]
    public async Task A_change_is_refused_when_the_page_moved_and_otherwise_carries_the_etag_it_read()
    {
        var etag = "v2";
        var (gateway, http) = Build((request, _) => request.Method == HttpMethod.Put
            ? Json(Page(8, "/Vision", "Changed."), "v3")
            : Json(Page(8, "/Vision", "Old."), etag));

        var stale = await gateway.UpdateAsync($"{WikiId}:8", "Vision", "Changed.", AzureDevOpsWikiPageGateway.VersionOf("v1") + 1);
        stale.Outcome.Should().Be(ProviderOutcome.Conflict);
        http.Requests.Should().NotContain(r => r.Method == HttpMethod.Put);

        var sent = await gateway.UpdateAsync($"{WikiId}:8", "Vision", "Changed.", AzureDevOpsWikiPageGateway.VersionOf("v2") + 1);
        sent.IsOk.Should().BeTrue(sent.Reason);
        sent.Value!.Version.Should().Be(AzureDevOpsWikiPageGateway.VersionOf("v3"));
    }

    [Fact]
    public async Task A_page_that_is_gone_says_so_and_one_raced_on_the_site_is_a_conflict()
    {
        var gone = Build((_, _) => Json("""{"message":"not found"}""", status: HttpStatusCode.NotFound));
        var raced = Build((request, _) => request.Method == HttpMethod.Put
            ? Json("""{"message":"changed"}""", status: HttpStatusCode.PreconditionFailed)
            : Json(Page(8, "/Vision"), "v2"));

        (await gone.Gateway.GetAsync($"{WikiId}:8")).Outcome.Should().Be(ProviderOutcome.Gone);
        (await raced.Gateway.UpdateAsync($"{WikiId}:8", "Vision", "x", AzureDevOpsWikiPageGateway.VersionOf("v2") + 1))
            .Outcome.Should().Be(ProviderOutcome.Conflict);
    }

    [Fact]
    public async Task A_lost_creation_is_found_by_its_name_in_the_tree_and_none_is_a_real_no()
    {
        const string tree = """{"id":1,"path":"/","subPages":[{"id":4,"path":"/adr","subPages":[{"id":8,"path":"/adr/0027 The verifier"}]}]}""";
        var (gateway, _) = Build((request, _) =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            return url.Contains("recursionLevel=full", StringComparison.Ordinal) ? Json(tree)
                : url.Contains("/pages/8?", StringComparison.Ordinal) ? Json(Page(8, "/adr/0027 The verifier", "Body."), "e8")
                : Json(Wikis);
        });

        (await gateway.FindByTitleAsync(AzureDevOpsWikiPageGateway.ProjectWiki, "0027 The verifier")).Value!.Id.Should().Be($"{WikiId}:8");
        var none = await gateway.FindByTitleAsync(AzureDevOpsWikiPageGateway.ProjectWiki, "Never written");
        none.IsOk.Should().BeTrue();
        none.Value.Should().BeNull();
    }

    [Fact]
    public async Task With_confluence_chosen_as_the_wiki_this_one_is_not_configured_and_sends_nothing()
    {
        var (gateway, http) = Build((_, _) => Json("{}"), OnboardingProviders.Confluence);

        gateway.IsConfigured.Should().BeFalse();
        (await gateway.GetAsync($"{WikiId}:8")).Outcome.Should().Be(ProviderOutcome.Unreachable);
        http.Requests.Should().BeEmpty();
    }
}
