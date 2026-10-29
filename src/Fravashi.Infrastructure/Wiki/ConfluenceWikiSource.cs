using Fravashi.Application.Wiki;

namespace Fravashi.Infrastructure.Wiki;

/// <summary>
/// The Confluence read source - reuses the existing <see cref="IConfluenceReader"/> (Cloud v2: list spaces →
/// list pages → get page storage body). The page id is the stable slug within its space; the storage-format
/// HTML is stored verbatim. One-way: nothing is written back (the site is authoritative on pull).
/// <paramref name="namedSpace"/> is the space key the wiki connection names, when it names one.
/// </summary>
public sealed class ConfluenceWikiSource(IConfluenceReader reader, string? namedSpace = null) : IWikiSource
{
    public string Provider => "Confluence";

    public async Task<IReadOnlyList<WikiSourcePage>> FetchAllAsync(
        IProgress<WikiFetchProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        if (!reader.IsConfigured)
        {
            return [];
        }

        var spaces = await reader.ListSpacesAsync(cancellationToken).ConfigureAwait(false);

        // The team spaces, AND the space the connection names, whatever kind that one is. The list above is
        // deliberately narrowed to team kinds so nobody's personal space is swept up unasked; a space somebody
        // NAMED was asked for. Met in the owner's install on 2026-09-21: the connection named the owner's own space (`~` and a user name), a
        // personal space, the access check wrote a page there and said Verified, and the sync then kept no
        // space at all, asked for no pages, and reported "Synced 0 page(s)" (backlog 12).
        var keys = spaces.Select(s => s.Key).ToList();
        if (!string.IsNullOrWhiteSpace(namedSpace)
            && !keys.Contains(namedSpace.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            keys.Add(namedSpace.Trim());
        }

        // First pass: enumerate page summaries so we can report an honest total.
        var summaries = new List<ConfluencePageSummary>();
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            summaries.AddRange(await reader.ListPagesAsync(key, cancellationToken).ConfigureAwait(false));
        }

        var pages = new List<WikiSourcePage>(summaries.Count);
        var fetched = 0;
        foreach (var summary in summaries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new WikiFetchProgress(fetched, summaries.Count, summary.Title));

            var page = await reader.GetPageAsync(summary.Id, cancellationToken).ConfigureAwait(false);
            if (page is not null)
            {
                pages.Add(new WikiSourcePage(
                    Space: page.SpaceKey,
                    Title: page.Title,
                    Slug: page.Id,
                    Body: page.BodyHtml,
                    Format: "storage",
                    SourceRef: null));
            }

            fetched++;
            progress?.Report(new WikiFetchProgress(fetched, summaries.Count, summary.Title));
        }

        return pages;
    }
}
