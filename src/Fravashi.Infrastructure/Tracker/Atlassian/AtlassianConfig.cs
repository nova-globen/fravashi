using System.Net.Http.Headers;
using System.Text;

namespace Fravashi.Infrastructure.Tracker.Atlassian;

public class AtlassianConfig
{
    public string JiraKey     { get; set; } = string.Empty;
    public string JiraDomain  { get; set; } = string.Empty;
    public string JiraEmail   { get; set; } = string.Empty;
    public string JiraProject { get; set; } = string.Empty;

    /// <summary>
    /// How to authenticate. A PAT / API token is sent as HTTP <c>Basic</c> (<c>base64(email:token)</c>) against
    /// the site base URL <c>https://{JiraDomain}</c> - the default and the legacy behaviour. An Atlassian OAuth
    /// 2.0 (3LO) access token is sent as <c>Bearer</c> against the Atlassian gateway
    /// <c>https://api.atlassian.com/ex/{jira|confluence}/{CloudId}</c>. A 3LO token does NOT authenticate against
    /// <c>{site}.atlassian.net</c>, so the scheme, token, base URL and cloudId must always agree - the
    /// secret/coordinate bridges set them together (secret bridge: scheme + token from the credential's method;
    /// coordinate bridge: cloudId).
    /// </summary>
    public string AuthScheme { get; set; } = "Basic";

    /// <summary>
    /// The Atlassian cloudId captured at 3LO sign-in (from <c>accessible-resources</c>). Required in Bearer mode
    /// to address the gateway; empty for a PAT (which addresses the site directly).
    /// </summary>
    public string CloudId { get; set; } = string.Empty;

    private bool UsesBearer => string.Equals(AuthScheme, "Bearer", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Everything BOTH Atlassian surfaces need to address the site and authenticate against it: the token, the
    /// site domain, and (per scheme) the account email (Basic (PAT) mode is byte-for-byte the legacy contract
    /// and needs it for <c>base64(email:token)</c>) or the cloudId (Bearer (3LO) mode addresses the gateway by
    /// cloudId instead, so an older sign-in that never captured one reports not-configured rather than silently
    /// 401-ing against a URL it cannot build).
    /// </summary>
    private bool HasSiteAccess =>
        !string.IsNullOrWhiteSpace(JiraKey)    &&
        !string.IsNullOrWhiteSpace(JiraDomain) &&
        (UsesBearer
            ? !string.IsNullOrWhiteSpace(CloudId)
            : !string.IsNullOrWhiteSpace(JiraEmail));

    /// <summary>
    /// Whether the CONFLUENCE surface is fully wired. Site access is all it needs: <see cref="JiraProject"/> is a
    /// Jira coordinate and means nothing to Confluence, which addresses spaces by key. It is deliberately NOT
    /// required here, because it is only ever populated from the onboarding BOARD path: a single validity flag
    /// that demanded it left every wiki-only install permanently not-configured, with the reader silently
    /// returning empty lists (backlog 12).
    /// </summary>
    public bool IsConfluenceValid => HasSiteAccess;

    /// <summary>
    /// Whether the JIRA surface is fully wired: site access plus the project key every Jira query is scoped by.
    /// This is the half of the old single flag that genuinely needs the project.
    /// </summary>
    public bool IsJiraValid => HasSiteAccess && !string.IsNullOrWhiteSpace(JiraProject);

    // In Bearer (3LO) mode the gateway prefix replaces the site origin; the REST path suffix is identical to the
    // site API (verified against developer.atlassian.com - the gateway is an origin substitution for
    // https://{site}.atlassian.net, and the Confluence /wiki context path is retained).
    public string JiraBaseUrl => UsesBearer
        ? $"https://api.atlassian.com/ex/jira/{CloudId}/rest/api/3"
        : $"https://{JiraDomain}/rest/api/3";

    public string ConfluenceBaseUrl => UsesBearer
        ? $"https://api.atlassian.com/ex/confluence/{CloudId}/wiki/api/v2"
        : $"https://{JiraDomain}/wiki/api/v2";

    public string BasicAuthToken => Convert.ToBase64String(
        Encoding.ASCII.GetBytes($"{JiraEmail}:{JiraKey}"));

    /// <summary>The Authorization header for the configured token - <c>Bearer &lt;token&gt;</c> (3LO) or
    /// <c>Basic &lt;base64(email:token)&gt;</c> (PAT).</summary>
    public AuthenticationHeaderValue AuthenticationHeader => UsesBearer
        ? new AuthenticationHeaderValue("Bearer", JiraKey)
        : new AuthenticationHeaderValue("Basic", BasicAuthToken);
}
