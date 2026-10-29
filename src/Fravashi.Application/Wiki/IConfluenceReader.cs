namespace Fravashi.Application.Wiki;

public sealed record ConfluenceSpaceSummary(string Id, string Key, string Name);

public sealed record ConfluencePageSummary(string Id, string Title, string SpaceKey);

public sealed record ConfluencePage(string Id, string Title, string SpaceKey, string BodyHtml);

/// <summary>Read-only access to Confluence spaces/pages. Optional - reports unconfigured when absent.</summary>
public interface IConfluenceReader
{
    bool IsConfigured { get; }

    /// <summary>List the global spaces, for building the docs-sidebar Confluence tree.</summary>
    Task<IReadOnlyList<ConfluenceSpaceSummary>> ListSpacesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConfluencePageSummary>> ListPagesAsync(string spaceKey, CancellationToken cancellationToken = default);

    Task<ConfluencePage?> GetPageAsync(string pageId, CancellationToken cancellationToken = default);
}
