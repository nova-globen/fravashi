using Fravashi.Domain.Wiki;

namespace Fravashi.Application.Wiki;

/// <summary>What a search by meaning found.</summary>
/// <param name="Items">The documents, most relevant first. Empty is a real answer: nothing was relevant.</param>
/// <param name="NotYetIndexed">Documents that exist and could not be considered because they are not indexed yet.</param>
public sealed record WikiMeaningResult(IReadOnlyList<WikiItem> Items, int NotYetIndexed);

/// <summary>
/// Finds authored documents by what they MEAN (backlog 40), which is what item 42's live run showed the
/// word search cannot do: asked about "unattended change", it found nothing, because that phrase appears
/// nowhere verbatim, while the document sat in the store.
/// </summary>
public interface IWikiMeaningSearch
{
    /// <summary>Whether searching by meaning is possible at all on this install.</summary>
    bool IsAvailable { get; }

    /// <summary>The relevant documents, or null when searching by meaning is not available.</summary>
    /// <remarks>
    /// Scoped to <paramref name="project"/> (backlog 121). The index itself is per document and knows no
    /// project, but what is RANKED is only that project's documents: a search that ranked every project's wiki
    /// would hand one project's documents to another project's agent.
    /// </remarks>
    Task<WikiMeaningResult?> FindAsync(
        Fravashi.Domain.Projects.ProjectId project,
        string text,
        IReadOnlyList<WikiItemType>? types,
        int limit,
        CancellationToken ct = default);

    /// <summary>Indexes what is new or changed, for at most this long. Returns how many are still waiting.</summary>
    Task<int> CatchUpAsync(TimeSpan budget, CancellationToken ct = default);
}
