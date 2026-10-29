using Fravashi.Application.Mirroring;
using Fravashi.Application.Network;
using Fravashi.Domain.Wiki;

namespace Fravashi.Application.Wiki;

/// <summary>
/// A wiki page gateway whose every answer is reported to <see cref="ProviderOutages"/> (backlog 232), the publishing
/// twin of <see cref="ObservedProviderGateway"/>. Calls and results pass through unchanged.
/// </summary>
public sealed class ObservedWikiPageGateway(IWikiPageGateway inner, ProviderOutages outages) : IWikiPageGateway
{
    /// <inheritdoc />
    public string Provider => inner.Provider;

    /// <inheritdoc />
    public bool IsConfigured => inner.IsConfigured;

    /// <inheritdoc />
    public bool VersionsAreReadable => inner.VersionsAreReadable;

    /// <inheritdoc />
    public string? DefaultSpace => inner.DefaultSpace;

    /// <inheritdoc />
    public ConversionResult ToSite(string markdown) => inner.ToSite(markdown);

    /// <inheritdoc />
    public ConversionResult FromSite(string body) => inner.FromSite(body);

    /// <inheritdoc />
    public Task<ProviderResult<SitePage>> CreateAsync(
        string space, string title, string body, string? parentPageId = null, CancellationToken ct = default) =>
        SeenAsync(inner.CreateAsync(space, title, body, parentPageId, ct));

    /// <inheritdoc />
    public Task<ProviderResult<SitePage>> UpdateAsync(string pageId, string title, string body, int newVersion, CancellationToken ct = default) =>
        SeenAsync(inner.UpdateAsync(pageId, title, body, newVersion, ct));

    /// <inheritdoc />
    public Task<ProviderResult<SitePage>> GetAsync(string pageId, CancellationToken ct = default) =>
        SeenAsync(inner.GetAsync(pageId, ct));

    /// <inheritdoc />
    public Task<ProviderResult<SitePage?>> FindByTitleAsync(string space, string title, CancellationToken ct = default) =>
        SeenAsync(inner.FindByTitleAsync(space, title, ct));

    private async Task<ProviderResult<T>> SeenAsync<T>(Task<ProviderResult<T>> call)
    {
        var result = await call.ConfigureAwait(false);
        outages.Observe(inner.Provider, result.Network);
        return result;
    }
}
