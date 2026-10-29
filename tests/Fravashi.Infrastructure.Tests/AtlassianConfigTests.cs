using System.Text;
using Fravashi.Infrastructure.Tracker.Atlassian;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests;

/// <summary>
/// The Atlassian adapter's two auth modes, at the source: a PAT / API token addresses the SITE with HTTP Basic
/// (the legacy contract, byte-for-byte); a 3LO OAuth token addresses the api.atlassian.com GATEWAY with Bearer.
/// A 3LO access token does not authenticate against <c>{site}.atlassian.net</c>, so the base URL, scheme and
/// cloudId must move together - these tests pin the exact URL + header each mode produces for both Jira and
/// Confluence, verified against developer.atlassian.com (gateway = origin substitution; the REST path suffix,
/// including the Confluence <c>/wiki</c> context path, is unchanged).
/// </summary>
public sealed class AtlassianConfigTests
{
    private static AtlassianConfig Pat() => new()
    {
        JiraKey = "api_token",
        JiraDomain = "acme.atlassian.net",
        JiraEmail = "dev@acme.com",
        JiraProject = "ENG",
        // AuthScheme defaults to Basic; CloudId stays empty - a PAT addresses the site directly.
    };

    private static AtlassianConfig ThreeLegged() => new()
    {
        JiraKey = "oauth_access_token",
        JiraDomain = "acme.atlassian.net",
        JiraProject = "ENG",
        AuthScheme = "Bearer",
        CloudId = "11111111-2222-3333-4444-555555555555",
        // No JiraEmail - a Bearer token carries the identity; the gateway is addressed by cloudId, not email.
    };

    // ── PAT mode (unchanged, byte-for-byte) ───────────────────────────────────────────────────────────────

    [Fact]
    public void PatMode_Jira_UsesSiteUrl_AndBasicAuth()
    {
        var config = Pat();

        config.JiraBaseUrl.Should().Be("https://acme.atlassian.net/rest/api/3");
        config.AuthenticationHeader.Scheme.Should().Be("Basic");
        config.AuthenticationHeader.Parameter.Should().Be(
            Convert.ToBase64String(Encoding.ASCII.GetBytes("dev@acme.com:api_token")));
    }

    [Fact]
    public void PatMode_Confluence_UsesSiteWikiUrl_AndBasicAuth()
    {
        var config = Pat();

        config.ConfluenceBaseUrl.Should().Be("https://acme.atlassian.net/wiki/api/v2");
        config.AuthenticationHeader.Scheme.Should().Be("Basic");
    }

    [Fact]
    public void PatMode_IsValid_RequiresEmail_NotCloudId()
    {
        Pat().IsJiraValid.Should().BeTrue();
        Pat().IsConfluenceValid.Should().BeTrue();

        var noEmail = Pat();
        noEmail.JiraEmail = string.Empty;
        noEmail.IsJiraValid.Should().BeFalse("Basic auth needs base64(email:token)");
        noEmail.IsConfluenceValid.Should().BeFalse("Basic auth needs base64(email:token)");
    }

    // ── 3LO mode (gateway + Bearer) ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void ThreeLeggedMode_Jira_UsesGatewayUrl_AndBearerAuth()
    {
        var config = ThreeLegged();

        config.JiraBaseUrl.Should().Be(
            "https://api.atlassian.com/ex/jira/11111111-2222-3333-4444-555555555555/rest/api/3");
        config.AuthenticationHeader.Scheme.Should().Be("Bearer");
        config.AuthenticationHeader.Parameter.Should().Be("oauth_access_token");
    }

    [Fact]
    public void ThreeLeggedMode_Confluence_UsesGatewayWikiUrl_AndBearerAuth()
    {
        var config = ThreeLegged();

        // The gateway keeps the Confluence /wiki context path (verified against developer.atlassian.com).
        config.ConfluenceBaseUrl.Should().Be(
            "https://api.atlassian.com/ex/confluence/11111111-2222-3333-4444-555555555555/wiki/api/v2");
        config.AuthenticationHeader.Scheme.Should().Be("Bearer");
        config.AuthenticationHeader.Parameter.Should().Be("oauth_access_token");
    }

    [Fact]
    public void ThreeLeggedMode_IsValid_RequiresCloudId_NotEmail()
    {
        // Valid without an email - the Bearer token carries identity.
        ThreeLegged().IsJiraValid.Should().BeTrue();
        ThreeLegged().IsConfluenceValid.Should().BeTrue();

        // But an older sign-in that never captured a cloudId cannot address the gateway → not valid.
        var noCloudId = ThreeLegged();
        noCloudId.CloudId = string.Empty;
        noCloudId.IsJiraValid.Should().BeFalse("the gateway URL cannot be built without a cloudId");
        noCloudId.IsConfluenceValid.Should().BeFalse("the gateway URL cannot be built without a cloudId");
    }

    // ── The project key is a JIRA coordinate, not a site one (backlog 12) ─────────────────────────────────

    [Fact]
    public void ConfluenceValidity_DoesNotRequireTheJiraProject_ButJiraValidityDoes()
    {
        // A wiki-only install: the site, the account email and the token are all there, and no board ever ran,
        // so nothing populates the project key. Confluence addresses spaces by key and never needs it.
        var wikiOnly = Pat();
        wikiOnly.JiraProject = string.Empty;

        wikiOnly.IsConfluenceValid.Should().BeTrue("Confluence needs the site and a credential, not a Jira project");
        wikiOnly.IsJiraValid.Should().BeFalse("every Jira query is scoped by the project key");

        // The same split under 3LO, where the gateway is addressed by cloudId.
        var wikiOnlySso = ThreeLegged();
        wikiOnlySso.JiraProject = string.Empty;

        wikiOnlySso.IsConfluenceValid.Should().BeTrue();
        wikiOnlySso.IsJiraValid.Should().BeFalse();
    }

    [Fact]
    public void SchemeComparison_IsCaseInsensitive()
    {
        var config = ThreeLegged();
        config.AuthScheme = "bearer";
        config.JiraBaseUrl.Should().StartWith("https://api.atlassian.com/ex/jira/");
        config.AuthenticationHeader.Scheme.Should().Be("Bearer");
    }
}
