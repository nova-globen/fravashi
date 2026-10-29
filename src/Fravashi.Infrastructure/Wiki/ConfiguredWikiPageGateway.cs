using Fravashi.Application.Mirroring;
using Fravashi.Application.Onboarding;
using Fravashi.Application.Persistence;
using Fravashi.Application.Wiki;
using Fravashi.Domain.Wiki;

namespace Fravashi.Infrastructure.Wiki;

/// <summary>
/// The wiki this install publishes to, decided at each call from the wiki connection onboarding saved (backlog 144):
/// an Azure DevOps wiki when that is the wiki step's choice, Confluence otherwise. Read at each use, like every adapter
/// here, so a wiki changed in a running studio is the one the next publish goes to.
/// </summary>
public sealed class ConfiguredWikiPageGateway(
    AtlassianConfluencePageGateway confluence, AzureDevOpsWikiPageGateway azureDevOps, IConfigurationStore settings) : IWikiPageGateway
{
    private IWikiPageGateway Current =>
        string.Equals(settings.Get("Onboarding:Wiki:Provider"), OnboardingProviders.AzureDevOpsWiki, StringComparison.Ordinal)
            ? azureDevOps
            : confluence;

    /// <inheritdoc />
    public string Provider => Current.Provider;

    public bool VersionsAreReadable => Current.VersionsAreReadable;

    /// <inheritdoc />
    public bool IsConfigured => Current.IsConfigured;

    /// <inheritdoc />
    public string? DefaultSpace => Current.DefaultSpace;

    /// <inheritdoc />
    public ConversionResult ToSite(string markdown) => Current.ToSite(markdown);

    /// <inheritdoc />
    public ConversionResult FromSite(string body) => Current.FromSite(body);

    /// <inheritdoc />
    public Task<ProviderResult<SitePage>> CreateAsync(
        string space, string title, string body, string? parentPageId = null, CancellationToken ct = default) =>
        Current.CreateAsync(space, title, body, parentPageId, ct);

    /// <inheritdoc />
    public Task<ProviderResult<SitePage>> UpdateAsync(string pageId, string title, string body, int newVersion, CancellationToken ct = default) =>
        Current.UpdateAsync(pageId, title, body, newVersion, ct);

    /// <inheritdoc />
    public Task<ProviderResult<SitePage>> GetAsync(string pageId, CancellationToken ct = default) => Current.GetAsync(pageId, ct);

    /// <inheritdoc />
    public Task<ProviderResult<SitePage?>> FindByTitleAsync(string space, string title, CancellationToken ct = default) =>
        Current.FindByTitleAsync(space, title, ct);
}
