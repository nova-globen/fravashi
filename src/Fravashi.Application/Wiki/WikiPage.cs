namespace Fravashi.Application.Wiki;

/// <summary>
/// One page in the DB-backed local wiki store (ADR 0005; vision O9/O10). The local wiki is a mirror of the
/// user's online wiki - the site is authoritative, so a pull overwrites the local copy unless a local edit has
/// diverged (a conflict, surfaced to the user, never silently overwritten). Two hashes make drift detectable:
/// <see cref="SourceHash"/> is the site's content at last pull; <see cref="LocalHash"/> is the local body's
/// current content. They are equal right after a pull; they diverge only once local editing lands (deferred),
/// at which point a changed site hash is a conflict.
/// </summary>
/// <param name="Id">Stable local id.</param>
/// <param name="Space">The space / collection the page belongs to (provider-specific grouping).</param>
/// <param name="Title">The page title.</param>
/// <param name="Slug">The page path / slug, unique within a <paramref name="Space"/>.</param>
/// <param name="Body">The page body, in the provider's storage format.</param>
/// <param name="Format">The storage format identifier (e.g. <c>storage</c>, <c>markdown</c>, <c>html</c>).</param>
/// <param name="SourceRef">The site's version / revision / ETag at last pull (drift signal alongside the hash).</param>
/// <param name="SourceHash">Content hash of the site body at last pull.</param>
/// <param name="LocalHash">Content hash of the current local body (equal to <paramref name="SourceHash"/> until edited).</param>
/// <param name="HasConflict">True when a pull detected a divergence it did not overwrite (awaiting resolution).</param>
/// <param name="UpdatedUtc">When this row was last written.</param>
public sealed record WikiPage(
    string Id,
    string Space,
    string Title,
    string Slug,
    string Body,
    string Format,
    string? SourceRef,
    string SourceHash,
    string LocalHash,
    bool HasConflict,
    DateTimeOffset UpdatedUtc);

/// <summary>
/// The DB-backed local wiki store (the <c>wiki_pages</c> table). Uniqueness is per (<c>space</c>, <c>slug</c>).
/// This is the real store the sync engine pulls into - not the retired file-based sync and not a two-way file
/// projection (both out of scope here).
/// </summary>
public interface IWikiPageStore
{
    /// <summary>All pages, ordered by space then title.</summary>
    IReadOnlyList<WikiPage> List();

    WikiPage? Get(string id);

    /// <summary>The page at (<paramref name="space"/>, <paramref name="slug"/>), or null.</summary>
    WikiPage? GetBySlug(string space, string slug);

    /// <summary>Inserts or updates the page keyed on (space, slug).</summary>
    void Upsert(WikiPage page);

    void Remove(string id);

    /// <summary>The number of stored pages (for the card's summary and the empty-state check).</summary>
    int Count();
}
