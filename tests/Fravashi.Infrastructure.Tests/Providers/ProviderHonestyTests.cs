using System.Net;
using AwesomeAssertions;
using Fravashi.Application.Git;
using Fravashi.Infrastructure.Providers;
using Fravashi.Infrastructure.Tracker.Atlassian;
using Fravashi.Infrastructure.Wiki;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Providers;

/// <summary>
/// Backlog 76. A provider that violates the support contract (an expired credential, a renamed or deleted
/// project, a schema change) used to surface as a bare status code or a .NET default message, which reads
/// like a Fravashi bug. These tests hold the failure to naming the PROVIDER and the CONDITION, on the
/// adapters the user actually meets: the wiki-sync Confluence reader and the GitHub-wiki clone.
/// </summary>
public sealed class ProviderHonestyTests
{
    [Fact]
    public void An_expired_credential_names_the_provider_and_says_the_token_is_the_problem()
    {
        var message = ProviderFailure.Describe("Jira", "the authenticated user", HttpStatusCode.Unauthorized);

        message.Should().StartWith("Jira").And.Contain("401");
        message.Should().Contain("expired").And.Contain("revoked");
    }

    [Fact]
    public void A_renamed_or_deleted_target_is_named_as_such_and_not_as_a_generic_failure()
    {
        var message = ProviderFailure.Describe("GitHub", "acme/widget on github.com", HttpStatusCode.NotFound);

        message.Should().Contain("GitHub does not have acme/widget on github.com (404)");
        message.Should().Contain("renamed, moved or deleted");
    }

    [Fact]
    public void A_retired_api_generation_says_the_build_needs_updating_rather_than_a_retry()
    {
        ProviderFailure.Describe("Jira", "the issue search", HttpStatusCode.Gone)
            .Should().Contain("retired").And.Contain("Fravashi needs an update");
    }

    [Fact]
    public void A_provider_side_outage_is_attributed_to_the_provider()
    {
        ProviderFailure.Describe("Azure DevOps", "the pull-request list", HttpStatusCode.ServiceUnavailable)
            .Should().Contain("failed on its own side").And.Contain("this is the provider, not Fravashi");
    }

    [Fact]
    public void The_providers_own_error_text_rides_along_but_never_unbounded()
    {
        var said = ProviderFailure.Describe("GitHub", "acme/widget", HttpStatusCode.Forbidden, """{"message":"scope missing"}""");
        said.Should().Contain("GitHub said:").And.Contain("scope missing");

        var flood = ProviderFailure.Describe("GitHub", "acme/widget", HttpStatusCode.Forbidden, new string('x', 5000));
        flood.Length.Should().BeLessThan(600);
    }

    [Fact]
    public async Task Confluence_rejecting_an_expired_token_names_Confluence_and_the_token()
    {
        var reader = ConfluenceReader((HttpStatusCode.Unauthorized, """{"message":"Current user not permitted"}"""));

        var act = async () => await reader.ListSpacesAsync();

        var thrown = await act.Should().ThrowAsync<HttpRequestException>();
        thrown.Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        // The wiki-sync card shows this message verbatim, so it must not read like our own crash.
        thrown.Which.Message.Should().StartWith("Confluence rejected the credential");
        thrown.Which.Message.Should().NotContain("Response status code does not indicate success");
    }

    [Fact]
    public async Task A_deleted_Confluence_space_is_reported_as_gone_from_Confluence()
    {
        var reader = ConfluenceReader((HttpStatusCode.NotFound, "{}"));

        var act = async () => await reader.ListPagesAsync("DOCS");

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.Message.Should().Contain("Confluence does not have the space 'DOCS'")
            .And.Contain("renamed, moved or deleted");
    }

    [Fact]
    public async Task A_Confluence_body_that_is_not_the_expected_json_is_called_a_schema_change_not_a_crash()
    {
        // What a corporate proxy or a captive sign-in page actually returns on a 200.
        var reader = ConfluenceReader((HttpStatusCode.OK, "<html><body>Sign in to continue</body></html>"));

        var act = async () => await reader.ListSpacesAsync();

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.Message.Should().Contain("Confluence answered")
            .And.Contain("API shape has changed")
            .And.Contain("sign-in page");
    }

    [Fact]
    public async Task A_GitHub_wiki_clone_refused_for_credentials_blames_the_token_not_Fravashi()
    {
        var source = new GitHubWikiSource(
            "https://github.com/acme/widget",
            "Authorization: Bearer x",
            new FailingGit("remote: Invalid username or token. Access denied.\nfatal: Authentication failed"));

        var act = async () => await source.FetchAllAsync(progress: null);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().StartWith("GitHub rejected the credential")
            .And.Contain("expired, revoked");
    }

    [Fact]
    public async Task A_GitHub_wiki_that_does_not_exist_yet_says_so_instead_of_showing_fatal_text()
    {
        var source = new GitHubWikiSource(
            "https://github.com/acme/widget",
            null,
            new FailingGit("remote: Repository not found.\nfatal: repository not found"));

        var act = async () => await source.FetchAllAsync(progress: null);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("GitHub has no wiki repository here")
            .And.Contain("first page");
    }

    private static AtlassianConfluenceReader ConfluenceReader((HttpStatusCode Status, string Body) answer)
    {
        var http = new HttpClient(new StubHttpMessageHandler(_ => answer));
        var options = Substitute.For<IOptionsMonitor<AtlassianConfig>>();
        options.CurrentValue.Returns(new AtlassianConfig
        {
            JiraKey = "token",
            JiraDomain = "acme.atlassian.net",
            JiraEmail = "docs@acme.com",
        });
        return new AtlassianConfluenceReader(http, options);
    }

    /// <summary>A git that always fails the clone with the given stderr, so no git binary is involved.</summary>
    private sealed class FailingGit(string stderr) : IGitProcessRunner
    {
        public Task<GitProcessResult> RunAsync(
            string workingDirectory,
            IReadOnlyDictionary<string, string> environment,
            Action<string>? onStderrLine,
            CancellationToken cancellationToken,
            params string[] arguments) =>
            Task.FromResult(new GitProcessResult(128, string.Empty, stderr));
    }
}
