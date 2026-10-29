using Fravashi.Application.WorkItems;
using Fravashi.Application.Wiki;
using Fravashi.Domain.Projects;
using Fravashi.Domain.Wiki;

namespace Fravashi.Infrastructure.Wiki;

/// <summary>
/// Works out which references in a body point at something that exists (backlog 39), so the renderer can
/// draw a link for those and mark the rest broken.
/// <para>
/// <b>Work items and wiki items are checked.</b> Both are in the studio's own database, so the check is a
/// cheap local read and a wrong number is caught the moment it is written.
/// </para>
/// <para>
/// <b>Pull requests are not, and that is a recorded decision rather than an omission.</b> Checking one means
/// an authenticated call to a git host, per reference, while a page renders: slow, and it fails offline and
/// on a repository the reader's token cannot see, which would mark a perfectly good reference broken. So a
/// pull-request reference always links, and the pull-request page itself says when there is no such
/// request, which is the surface that can say it accurately.
/// </para>
/// </summary>
public sealed class WikiReferenceResolver(ITrackerResolver trackers, IWikiLibraries wikis)
{
    private readonly ITrackerResolver _trackers = trackers ?? throw new ArgumentNullException(nameof(trackers));
    private readonly IWikiLibraries _wikis = wikis ?? throw new ArgumentNullException(nameof(wikis));

    /// <summary>The map for one body: every reference in it that resolves, with what it is called.</summary>
    /// <remarks>
    /// A reference means something only inside a project: <c>#12</c> is item 12 of the project the document is
    /// being read in (backlog 121), so the caller says which, and it is never assumed.
    /// </remarks>
    public async Task<WikiReferenceMap> ResolveAsync(ProjectId project, string? markdown, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        var map = new WikiReferenceMap();
        var references = WikiReferences.Find(markdown).Select(f => f.Reference).Distinct().ToList();
        if (references.Count == 0)
        {
            return map;
        }

        var tracker = _trackers.For(project);
        var wiki = _wikis.For(project);
        foreach (var reference in references)
        {
            switch (reference.Kind)
            {
                case WikiReferenceKind.WorkItem:
                    if (await tracker.GetByNumberAsync(reference.Number, ct).ConfigureAwait(false) is { } item)
                    {
                        map.Add(reference, item.Title);
                    }

                    break;

                case WikiReferenceKind.WikiItem:
                    if (await wiki.GetByNumberAsync(reference.Number, ct).ConfigureAwait(false) is { } page)
                    {
                        map.Add(reference, page.Title);
                    }

                    break;

                default:
                    // A pull request: linked without a check, for the reason in this type's own summary.
                    map.Add(reference, null);
                    break;
            }
        }

        return map;
    }
}
