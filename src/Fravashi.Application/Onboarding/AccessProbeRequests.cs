namespace Fravashi.Application.Onboarding;

/// <summary>
/// Provider identifiers the onboarding wizard offers. These are vendor names on the adapter side (which
/// naming-conventions permits - an adapter is named for its provider), used to select which concrete probe
/// path runs. <see cref="Local"/> is the in-app fallback (no external system).
/// </summary>
public static class OnboardingProviders
{
    public const string GitHub = "GitHub";
    public const string AzureDevOps = "AzureDevOps";

    /// <summary>Self-hosted Forgejo (locked decision 10): a full source provider with PAT auth.</summary>
    public const string Forgejo = "Forgejo";
    public const string Jira = "Jira";
    public const string Confluence = "Confluence";
    public const string AzureDevOpsWiki = "AzureDevOpsWiki";
    public const string GitHubWiki = "GitHubWiki";
    public const string Local = "Local";
    public const string None = "None";
}

/// <summary>A request to verify access to a git source repository.</summary>
/// <param name="Provider">One of <see cref="OnboardingProviders"/> (GitHub / AzureDevOps).</param>
/// <param name="RepositoryUrl">The clone URL (HTTPS) of the repository.</param>
/// <param name="Token">The personal access token, or null to probe anonymously.</param>
/// <param name="Bearer">
/// True when <paramref name="Token"/> is an OAuth bearer token (an Entra device-code token for Azure DevOps)
/// rather than a PAT. Azure DevOps then requires <c>Authorization: Bearer</c> for REST and a git bearer header
/// for <c>ls-remote</c>; a PAT (the default, false) is sent as Basic / embedded in the URL as before.
/// </param>

/// <summary>A request to verify access to a documentation/wiki provider.</summary>
/// <param name="Provider">One of <see cref="OnboardingProviders"/> (Confluence / AzureDevOpsWiki / GitHubWiki).</param>
/// <param name="BaseUrl">The provider base URL or site, when applicable.</param>
/// <param name="Email">The account email (Confluence basic-auth pairs email with the token), or null.</param>
/// <param name="Token">The personal access token / API token, or null.</param>
/// <param name="SpaceKey">
/// The Confluence space key the write/delete round-trip targets, or null. When null the round-trip is not
/// run and Write/Delete stay honestly "not verified - provide a space key to run the write test". Ignored by
/// providers that do not use a space key.
/// </param>
/// <param name="Bearer">
/// True when <paramref name="Token"/> is an OAuth bearer token (an Entra device-code token for the Azure
/// DevOps wiki) rather than a PAT, so the probe sends <c>Authorization: Bearer</c>. GitHub-wiki tokens embed in
/// the git URL either way; Confluence (Atlassian 3LO) probe integration is a tracked follow-up.
/// </param>
public sealed record WikiAccessRequest(string Provider, string? BaseUrl, string? Email, string? Token, string? SpaceKey = null, bool Bearer = false);

/// <summary>A request to verify access to a work board / tracker.</summary>
/// <param name="Provider">One of <see cref="OnboardingProviders"/> (AzureDevOps / Jira).</param>
/// <param name="BaseUrl">The organization/site base URL.</param>
/// <param name="Project">The project (Azure DevOps) or project key (Jira), when applicable.</param>
/// <param name="Email">The account email (Jira basic-auth pairs email with the token), or null.</param>
/// <param name="Token">The personal access token / API token, or null.</param>
/// <param name="Bearer">
/// True when <paramref name="Token"/> is an OAuth bearer token (an Entra device-code token for Azure DevOps)
/// rather than a PAT, so the probe sends <c>Authorization: Bearer</c> instead of Basic. Ignored by Jira.
/// </param>
public sealed record BoardAccessRequest(string Provider, string? BaseUrl, string? Project, string? Email, string? Token, bool Bearer = false);
