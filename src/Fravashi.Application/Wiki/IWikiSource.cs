namespace Fravashi.Application.Wiki;

/// <summary>One page fetched from an online wiki provider, normalized across providers for the sync engine.</summary>
/// <param name="Space">The space / collection grouping.</param>
/// <param name="Title">The page title.</param>
/// <param name="Slug">A stable path / slug, unique within the space.</param>
/// <param name="Body">The page body in the provider's storage format.</param>
/// <param name="Format">The storage-format identifier (<c>storage</c>, <c>markdown</c>, <c>html</c>, …).</param>
/// <param name="SourceRef">The provider's version / revision / ETag, when available.</param>
public sealed record WikiSourcePage(
    string Space,
    string Title,
    string Slug,
    string Body,
    string Format,
    string? SourceRef);

/// <summary>Progress reported while fetching from a wiki source (pages fetched / total, current title).</summary>
/// <param name="Fetched">Pages fetched so far.</param>
/// <param name="Total">Total pages when known, else null (indeterminate).</param>
/// <param name="CurrentTitle">The page currently being fetched, when known.</param>
public sealed record WikiFetchProgress(int Fetched, int? Total, string? CurrentTitle);

/// <summary>
/// The read side of an online wiki provider - the source the local store is synced <b>from</b>. Implementations
/// reuse the provider adapters/probes (Confluence v2, a generic HTTP source, GitHub wiki via git). One-way:
/// there is no write-back here (the site is authoritative on pull; local edits + conflict resolution are a
/// deferred follow-up).
/// </summary>
public interface IWikiSource
{
    /// <summary>A short provider label for the card and logs.</summary>
    string Provider { get; }

    /// <summary>Fetches every page, reporting progress. The site is authoritative; the caller overwrites local.</summary>
    Task<IReadOnlyList<WikiSourcePage>> FetchAllAsync(
        IProgress<WikiFetchProgress>? progress,
        CancellationToken cancellationToken = default);
}

/// <summary>The outcome of resolving the configured wiki into a runnable source.</summary>
public enum WikiSourceStatus
{
    /// <summary>A source is ready to pull from.</summary>
    Ready,

    /// <summary>No online wiki is configured (local-only, O10) - nothing to sync.</summary>
    None,

    /// <summary>A wiki is configured but its read-sync is not implemented yet (honest deferral).</summary>
    Unsupported,
}

/// <summary>The result of <see cref="IWikiSourceResolver.Resolve"/>.</summary>
/// <param name="Status">Whether a source is available, absent, or configured-but-unsupported.</param>
/// <param name="Source">The runnable source when <see cref="WikiSourceStatus.Ready"/>, else null.</param>
/// <param name="Message">A human-readable explanation for the None / Unsupported states.</param>
public sealed record WikiSourceResolution(WikiSourceStatus Status, IWikiSource? Source, string Message);

/// <summary>Resolves the onboarding-configured wiki provider into an <see cref="IWikiSource"/> to pull from.</summary>
public interface IWikiSourceResolver
{
    WikiSourceResolution Resolve();
}
