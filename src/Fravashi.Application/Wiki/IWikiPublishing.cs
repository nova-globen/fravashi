using Fravashi.Application.Mirroring;
using Fravashi.Domain.Wiki;

namespace Fravashi.Application.Wiki;

/// <summary>A page as the site has it.</summary>
/// <param name="Id">The site's id for the page.</param>
/// <param name="Title">Its title there.</param>
/// <param name="Version">The site's own version number, which a new version must exceed by exactly one.</param>
/// <param name="Body">Its body in the site's native format.</param>
/// <param name="WebUrl">Its address for a person, when the site gave one.</param>
public sealed record SitePage(string Id, string Title, int Version, string Body, string? WebUrl);

/// <summary>
/// The narrow port a wiki provider implements so an authored document can reach it (backlog 83). It speaks
/// the same outcomes as the board's <see cref="IProviderGateway"/> on purpose: "never left" and "left with
/// no answer" are different states of knowledge here too, and an adapter that reports a timeout as
/// unreachable because it is simpler has put the duplicate page straight back.
/// </summary>
public interface IWikiPageGateway
{
    /// <summary>The provider's name, as the user sees it.</summary>
    string Provider { get; }

    /// <summary>Whether a write can be attempted at all, from the configuration as it is NOW.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Whether <see cref="SitePage.Version"/> is a number a person would recognise on the site (backlog 149).
    /// Confluence numbers its versions and shows them; Azure DevOps has ETags, which the adapter hashes into a
    /// number the publisher can compare, and a sentence that quotes one means nothing to anybody. Default true.
    /// </summary>
    bool VersionsAreReadable => true;

    /// <summary>
    /// Creates a page in a space, under <paramref name="parentPageId"/> when one is given, so the site's tree is the
    /// wiki's. A title the space already holds is refused by the site.
    /// </summary>
    Task<ProviderResult<SitePage>> CreateAsync(
        string space, string title, string body, string? parentPageId = null, CancellationToken ct = default);

    /// <summary>
    /// Sends a whole new version of a page. <paramref name="newVersion"/> is the version this write claims
    /// to become, and the site answers <see cref="ProviderOutcome.Conflict"/> when somebody got there first.
    /// </summary>
    Task<ProviderResult<SitePage>> UpdateAsync(string pageId, string title, string body, int newVersion, CancellationToken ct = default);

    /// <summary>The page as it is now, or <see cref="ProviderOutcome.Gone"/>.</summary>
    Task<ProviderResult<SitePage>> GetAsync(string pageId, CancellationToken ct = default);

    /// <summary>
    /// The page with exactly this title in the space, or Ok(null) when the site definitely has none. Ok(null)
    /// is a real answer and is what makes a lost creation safe to send again.
    /// </summary>
    Task<ProviderResult<SitePage?>> FindByTitleAsync(string space, string title, CancellationToken ct = default);

    /// <summary>
    /// A document's markdown in the site's own format, with what could not be carried (backlog 144: a Confluence page is
    /// storage format, an Azure DevOps wiki page is markdown already). Confluence's conversion unless a site says otherwise.
    /// </summary>
    ConversionResult ToSite(string markdown) => WikiFormatConversion.ToStorage(markdown);

    /// <summary>A page's body back as markdown, with what could not be carried.</summary>
    ConversionResult FromSite(string body) => WikiFormatConversion.ToMarkdown(body);

    /// <summary>
    /// The space a page goes to when nobody named one, or null when the site has no such thing and one must be named.
    /// A Confluence space has to be chosen; an Azure DevOps project has its own wiki.
    /// </summary>
    string? DefaultSpace => null;
}

/// <summary>Where publication facts are kept. One row per document.</summary>
public interface IWikiPublicationStore
{
    /// <summary>The row for a document, or null when it was never published.</summary>
    Task<WikiPublication?> GetAsync(string itemId, CancellationToken ct = default);

    /// <summary>Writes the row, replacing what was there. Durable when it returns.</summary>
    Task SaveAsync(WikiPublication publication, CancellationToken ct = default);

    /// <summary>
    /// Keeps the row as history and removes it (backlog 145: the project now publishes to another wiki, and the page on
    /// the old one is a fact about the document that is not thrown away).
    /// </summary>
    Task ArchiveAsync(WikiPublication publication, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Where the document was published before its project moved to another wiki, newest first.</summary>
    Task<IReadOnlyList<WikiPublication>> HistoryAsync(string itemId, CancellationToken ct = default);
}

/// <summary>Where published pages go.</summary>
public interface IWikiPublishTarget
{
    /// <summary>The space a new page is created in, or null when nobody has named one.</summary>
    string? Space { get; }
}

/// <summary>What one publishing action did.</summary>
/// <param name="Publication">The row as it stands afterwards, or null when there is none.</param>
/// <param name="Losses">What the format conversion could not carry, reported rather than dropped.</param>
/// <param name="Refusal">Why nothing was attempted, when nothing was.</param>
public sealed record WikiPublishReport(
    WikiPublication? Publication,
    IReadOnlyList<ConversionLoss> Losses,
    string? Refusal)
{
    /// <summary>A report that nothing was attempted, and why.</summary>
    public static WikiPublishReport Refused(WikiPublication? publication, string why) => new(publication, [], why);
}

/// <summary>What publishing a whole set of documents did, each counted once.</summary>
/// <param name="Created">Documents that got a new page.</param>
/// <param name="Updated">Documents whose page got a new version.</param>
/// <param name="Unchanged">Documents whose page already held what they hold.</param>
/// <param name="Refused">Each document that was not published, with why, in words.</param>
public sealed record WikiPublishAllReport(int Created, int Updated, int Unchanged, IReadOnlyList<string> Refused);
