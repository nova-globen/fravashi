using Fravashi.Application.Git;
using Fravashi.Application.Onboarding;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Persistence;
using Fravashi.Application.Wiki;
using Fravashi.Infrastructure.Git;

namespace Fravashi.Infrastructure.Wiki;

/// <summary>
/// Resolves the onboarding-configured wiki into a runnable <see cref="IWikiSource"/> (F2 / O6 - O10). Resolution
/// order: an explicit <c>Wiki:SourceBaseUrl</c> override wins (the generic HTTP source - the config-overridable
/// hook that lets the sync be driven against a stand-in server); otherwise the provider recorded under
/// <c>Onboarding:Wiki:Provider</c> selects Confluence (reusing <see cref="IConfluenceReader"/>) or GitHub-wiki
/// (git). Azure DevOps wiki read-sync is a declared follow-up (its pages-API adapter is not built yet), so it
/// resolves as <see cref="WikiSourceStatus.Unsupported"/> rather than pretending to sync. No online wiki (Local
/// or none) resolves as <see cref="WikiSourceStatus.None"/>.
/// </summary>
public sealed class WikiSourceResolver(
    IProviderConnectionStore connections,
    IConfigurationStore config,
    IConfluenceReader confluence,
    ISecretStore secrets,
    IGitProcessRunner git,
    IHttpClientFactory httpClientFactory) : IWikiSourceResolver
{
    /// <summary>Config key: an explicit HTTP wiki base URL, overriding provider selection. Config-overridable.</summary>
    public const string SourceBaseUrlKey = "Wiki:SourceBaseUrl";

    public WikiSourceResolution Resolve()
    {
        var overrideUrl = config.Get(SourceBaseUrlKey);
        if (!string.IsNullOrWhiteSpace(overrideUrl))
        {
            var client = httpClientFactory.CreateClient("wiki-sync");
            return new WikiSourceResolution(
                WikiSourceStatus.Ready, new HttpJsonWikiSource(client, overrideUrl),
                $"Syncing from {overrideUrl}.");
        }

        var connection = connections.GetByKind(ProviderKind.Wiki).FirstOrDefault();
        var provider = config.Get("Onboarding:Wiki:Provider");

        if (connection is null || string.IsNullOrWhiteSpace(provider)
            || string.Equals(provider, OnboardingProviders.Local, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, OnboardingProviders.None, StringComparison.OrdinalIgnoreCase))
        {
            return new WikiSourceResolution(
                WikiSourceStatus.None, null,
                "Using the local wiki - no online wiki was configured, so there is nothing to sync.");
        }

        if (string.Equals(provider, OnboardingProviders.Confluence, StringComparison.OrdinalIgnoreCase))
        {
            return confluence.IsConfigured
                ? new WikiSourceResolution(WikiSourceStatus.Ready, new ConfluenceWikiSource(confluence, NamedSpace()), "Syncing from Confluence.")
                : new WikiSourceResolution(WikiSourceStatus.Unsupported, null,
                    "Confluence is selected but its credentials are not fully wired yet.");
        }

        if (string.Equals(provider, OnboardingProviders.GitHubWiki, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(connection.BaseUrl))
            {
                return new WikiSourceResolution(WikiSourceStatus.Unsupported, null,
                    "A GitHub wiki is selected but no repository URL was recorded.");
            }

            var credential = string.IsNullOrWhiteSpace(connection.CredentialRef)
                ? null
                : CredentialEnvelope.Parse(secrets.Get(connection.CredentialRef));
            var header = GitAuthHeader.Build(credential, azureDevOpsBasicForm: false);
            return new WikiSourceResolution(
                WikiSourceStatus.Ready, new GitHubWikiSource(connection.BaseUrl, header, git),
                "Syncing from the GitHub wiki repository.");
        }

        if (string.Equals(provider, OnboardingProviders.AzureDevOpsWiki, StringComparison.OrdinalIgnoreCase))
        {
            return new WikiSourceResolution(WikiSourceStatus.Unsupported, null,
                "Azure DevOps wiki read-sync is a follow-up (its pages-API read adapter is not built yet). " +
                "The site stays authoritative and is used directly in the meantime.");
        }

        return new WikiSourceResolution(WikiSourceStatus.None, null,
            $"No sync is available for the '{provider}' wiki provider.");
    }

    /// <summary>
    /// The space the wiki connection names, which is the same answer publishing uses
    /// (<see cref="ConfiguredWikiPublishTarget"/>): the explicit setting, otherwise the key the access check ran
    /// against. The sync reads it in addition to the team spaces, whatever kind of space it is (backlog 12).
    /// </summary>
    private string? NamedSpace()
    {
        var chosen = config.Get(ConfiguredWikiPublishTarget.SpaceKey);
        return string.IsNullOrWhiteSpace(chosen) ? config.Get(ConfiguredWikiPublishTarget.CheckedSpaceKey) : chosen;
    }
}
