using Fravashi.Application.Wiki;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Mirroring;
using Fravashi.Domain.Wiki;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;

namespace Fravashi.Application.Mirroring;

/// <summary>What applying a repair did.</summary>
/// <param name="Repaired">True when the graph changed.</param>
/// <param name="Sentence">What happened, or why nothing did.</param>
public sealed record RepairOutcome(bool Repaired, string Sentence);

/// <summary>
/// Walks the whole graph and reports what is broken across the two stores (ADR 0018's "two stores"
/// consequence). Nothing is repaired by inspecting: every repair loses information, so it is an explicit
/// second call about one named defect.
/// <para>
/// Reading the links costs one query per item rather than one for the whole board. That is deliberate at
/// this scale, where a board is one person's work and the port's link read is per item; a bulk read would
/// be a new port method existing only for this pass, and the honest place to add one is when a board is
/// measured to be too big for this, not before.
/// </para>
/// </summary>
public sealed class GraphRepair(IMirrorStore mirror, IWikiLibraries? wikis = null)
{
    /// <summary>
    /// The most documents one pass reads. It matches the library's own hard clamp, so asking for more would
    /// quietly get this many anyway; naming it here is what lets the pass notice it hit the ceiling.
    /// </summary>
    private const int DocumentCeiling = 1000;

    /// <summary>
    /// Everything wrong with the project's graph. A reference to a TOMBSTONED item is reported and is not
    /// counted as broken, which is the whole argument for tombstoning rather than deleting: the edge can
    /// still be read.
    /// </summary>
    public async Task<GraphReport> InspectAsync(ITracker tracker, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tracker);

        var items = await AllItemsAsync(tracker, ct).ConfigureAwait(false);
        var present = items.Select(i => i.Id).ToHashSet();

        var states = await mirror.ForProjectAsync(tracker.Project, ct).ConfigureAwait(false);
        var tombstoned = states.Values.Where(s => s.IsTombstoned).Select(s => s.Item).ToHashSet();

        var links = new HashSet<WorkItemLink>();
        foreach (var item in items)
        {
            foreach (var link in await tracker.LinksAsync(item.Id, ct).ConfigureAwait(false))
            {
                // A set, because every edge is read from both of its ends and reporting it twice would
                // double every count in the report.
                links.Add(link);
            }
        }

        var report = ReferentialRepair.Check(items, [.. links], present, tombstoned);

        var (across, sawEverything) = await AcrossStoresAsync(tracker.Project, items, ct).ConfigureAwait(false);
        return report with
        {
            Defects = across.Count == 0 ? report.Defects : [.. report.Defects, .. across],
            Complete = sawEverything,
        };
    }

    /// <summary>
    /// Applies the repair a defect suggests, and only that one. A defect whose suggestion is to keep it is
    /// refused rather than quietly succeeding, because "repaired" would then mean nothing.
    /// </summary>
    public async Task<RepairOutcome> ApplyAsync(ITracker tracker, GraphDefect defect, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(defect);

        switch (defect.Suggested)
        {
            case RepairAction.RemoveLink:
            {
                if (defect.Edge is not { } edge)
                {
                    return new RepairOutcome(false, "This defect does not name an edge that can be removed.");
                }

                var result = await tracker.UnlinkAsync(edge.Source, edge.Type, edge.Target, MutationContext.User, ct)
                    .ConfigureAwait(false);

                return result.IsOk && result.Value
                    ? new RepairOutcome(true, $"The link to {defect.Target} was removed. That the two were related is no longer recorded.")
                    : new RepairOutcome(false, "There was no such link left to remove.");
            }

            case RepairAction.ClearParent:
            {
                if (defect.Item is not { } subject)
                {
                    return new RepairOutcome(false, "This defect does not name an item whose parent can be cleared.");
                }

                var item = await tracker.GetAsync(subject, ct).ConfigureAwait(false);
                if (item is null)
                {
                    return new RepairOutcome(false, "That item is no longer in this graph.");
                }

                var result = await tracker.UpdateAsync(
                    item.Id,
                    new WorkItemPatch { Parent = (WorkItemId?)null },
                    item.Version,
                    MutationContext.User,
                    ct).ConfigureAwait(false);

                return result.IsOk
                    ? new RepairOutcome(true, $"Item {item.Number} no longer names a parent and sits at the top of its tier.")
                    : new RepairOutcome(false, "The board refused to detach it: " + string.Join("; ", result.Errors.Select(e => e.Message)));
            }

            default:
                return new RepairOutcome(
                    false,
                    "There is nothing to repair here. The reference is honest and a reader should see it as it is.");
        }
    }

    /// <summary>
    /// Every item in the project, by following the query's own cursor. The whole board is needed because a
    /// pass over part of it would report every link leaving that part as dangling.
    /// </summary>
    private static async Task<IReadOnlyList<WorkItem>> AllItemsAsync(ITracker tracker, CancellationToken ct)
    {
        var items = new List<WorkItem>();
        string? cursor = null;

        do
        {
            var page = await tracker.QueryAsync(
                new WorkItemQuery { Limit = WorkItemQuery.MaxLimit, Cursor = cursor },
                ct).ConfigureAwait(false);

            items.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is { Length: > 0 });

        return items;
    }

    /// <summary>
    /// References that cross from one store to the other, in BOTH directions. Both are needed because a
    /// page can be deleted while its item survives and an item can be tombstoned while its plan stays.
    /// </summary>
    private async Task<(IReadOnlyList<GraphDefect> Defects, bool SawEverything)> AcrossStoresAsync(
        Fravashi.Domain.Projects.ProjectId project,
        IReadOnlyList<WorkItem> items,
        CancellationToken ct)
    {
        // The documents of the SAME project as the board being inspected (backlog 121): a reference is a
        // number, and a number read against another project's items would report defects that are not there.
        var wiki = wikis?.For(project);
        if (wiki is null)
        {
            // No wiki configured is not a defect. There is simply no second store to disagree with, and the
            // pass has still seen everything there was to see.
            return ([], true);
        }

        var documents = await wiki.ListAsync(new WikiItemQuery { Limit = DocumentCeiling }, ct).ConfigureAwait(false);

        // The library read has no cursor, so a library at the ceiling may have more behind it. Saying so is
        // the difference between "nothing is broken" and "nothing I could see is broken".
        var sawEverything = documents.Count < DocumentCeiling;

        var documentReferences = documents.ToDictionary(
            d => d.Slug,
            d => (IReadOnlyCollection<int>)[.. d.BodyReferences
                .Where(r => r.Kind == WikiReferenceKind.WorkItem)
                .Select(r => r.Number)
                .Distinct()],
            StringComparer.Ordinal);

        var itemReferences = items
            .Where(i => i.Refs.Any(r => r.Kind == ArtifactRefKind.Wiki))
            .ToDictionary(
                i => i.Id.Value,
                i => (IReadOnlyCollection<string>)[.. i.Refs
                    .Where(r => r.Kind == ArtifactRefKind.Wiki)
                    .Select(r => r.Target)
                    .Distinct(StringComparer.Ordinal)],
                StringComparer.Ordinal);

        var defects = ReferentialRepair.CheckAcrossStores(
            documentReferences,
            items.Select(i => i.Number).ToHashSet(),
            itemReferences,
            documents.Select(d => d.Slug).ToHashSet(StringComparer.Ordinal));

        return (defects, sawEverything);
    }
}
