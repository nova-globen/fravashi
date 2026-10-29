using System.Globalization;
using Fravashi.Application.Wiki;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Projects;
using Fravashi.Domain.Releases;
using Fravashi.Domain.Wiki;
using Fravashi.Domain.WorkItems;

namespace Fravashi.Application.Releases;

/// <summary>What saving a release did: the release, and the Release wiki document or why it was not written.</summary>
/// <param name="Result">The store's answer.</param>
/// <param name="WikiNumber">The number of the Release wiki document, when it was written.</param>
/// <param name="WikiProblem">Why the document was not written, when it was not; the release itself still stands.</param>
public sealed record ReleaseSaved(ReleaseResult Result, int? WikiNumber, string? WikiProblem);

/// <summary>
/// Records a release of a project (work item #368): the items a release can take, the version to suggest, and the save
/// itself, which stamps the items through the release store and then writes the notes as a Release wiki document linked
/// to every item. Publishing that document to a provider is NOT done here and never follows from a save: it is the wiki
/// page's own explicit act (ADR 0018). The project is named on every call and never remembered.
/// </summary>
public sealed class ReleaseRecorder(
    IReleaseStores releases,
    ITrackerResolver trackers,
    IWikiLibraries wikis,
    IWorkItemChanges changes)
{
    /// <summary>The version suggested when neither a tag nor a release says anything yet.</summary>
    public const string FirstVersion = "v0.1.0";

    /// <summary>
    /// Every Done item of the project that no release has taken, by number: what a new release offers, all ticked. A
    /// query condition in the store, page by page, never a scan of the whole board here.
    /// </summary>
    public async Task<IReadOnlyList<WorkItem>> CandidatesAsync(ProjectId project, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var tracker = trackers.For(project);
        var items = new List<WorkItem>();
        string? cursor = null;
        do
        {
            var page = await tracker.QueryAsync(
                new WorkItemQuery
                {
                    Statuses = [WorkItemStatus.Done],
                    Release = ReleaseFilter.Unreleased,
                    Limit = WorkItemQuery.MaxLimit,
                    Cursor = cursor,
                },
                ct).ConfigureAwait(false);
            items.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return [.. items.OrderBy(i => i.Number)];
    }

    /// <summary>
    /// The latest version among the repository's tag names and the project's own releases, or null when neither has one
    /// in the release shape. The suggestion is its next patch; minor and major are one click away on the page.
    /// </summary>
    public static ReleaseVersion? Latest(IEnumerable<string> tagNames, IEnumerable<Release> known)
    {
        ArgumentNullException.ThrowIfNull(tagNames);
        ArgumentNullException.ThrowIfNull(known);
        return ReleaseVersion.Latest(tagNames.Concat(known.Select(r => r.Version)));
    }

    /// <summary>The suggested next version: the latest's next patch, or <see cref="FirstVersion"/>.</summary>
    public static ReleaseVersion Suggest(IEnumerable<string> tagNames, IEnumerable<Release> known) =>
        Latest(tagNames, known)?.NextPatch() ?? ReleaseVersion.Parse(FirstVersion)!;

    /// <summary>
    /// Saves the release (stamping and archiving its items in one transaction), then writes the notes as a Release wiki
    /// document linked to every item and records it on the release. A document that could not be written leaves the
    /// release saved and says why, because the items are already stamped and the notes are on the release record.
    /// </summary>
    public async Task<ReleaseSaved> SaveAsync(ProjectId project, NewRelease release, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(release);
        var result = await releases.For(project).CreateAsync(release, ct).ConfigureAwait(false);
        if (result.Value is not { } saved)
        {
            return new ReleaseSaved(result, null, null);
        }

        changes.Announce(project);

        var links = saved.ItemNumbers
            .Select(n => new WikiLink(WikiLinkKind.WorkItem, n.ToString(CultureInfo.InvariantCulture)))
            .ToList();
        var written = await wikis.For(project).CreateAsync(
            new NewWikiItem
            {
                Type = WikiItemType.Release,
                Title = saved.Title,
                Body = saved.Notes,
                Links = links,
            },
            ct).ConfigureAwait(false);
        if (!written.IsValid)
        {
            return new ReleaseSaved(
                result,
                null,
                "The release is saved, but its notes could not be written to the wiki: "
                + string.Join(" ", written.Errors.Select(e => e.Message)));
        }

        await releases.For(project).SetWikiDocumentAsync(saved.Id, written.Value.Number, ct).ConfigureAwait(false);
        return new ReleaseSaved(result with { Value = saved with { WikiNumber = written.Value.Number } }, written.Value.Number, null);
    }
}
