using Fravashi.Domain.WorkItems;

namespace Fravashi.Domain.Mirroring;

/// <summary>
/// A way the graph can be broken across two stores owned by different vendors (ADR 0018, the "two stores"
/// consequence: "a work item in Jira and a plan in Confluence cannot be created atomically ... design that
/// before the first provider adapter ships, not after a user's graph has holes in it").
/// </summary>
public enum DefectKind
{
    /// <summary>An edge points at an id that is not in the graph at all.</summary>
    LinkTargetMissing,

    /// <summary>An edge points at a tombstoned item. Not broken: this is what tombstones are for.</summary>
    LinkTargetTombstoned,

    /// <summary>An item names a parent that is not in the graph.</summary>
    ParentMissing,

    /// <summary>An item names a parent that is tombstoned, so the hierarchy has a dead rung.</summary>
    ParentTombstoned,

    /// <summary>A wiki document references a work item that is not in the graph.</summary>
    WikiReferenceMissing,

    /// <summary>A work item references a wiki document that is not in the library.</summary>
    DocumentReferenceMissing,
}

/// <summary>
/// How a defect can be put right. Nothing here is applied without being asked for: every repair loses
/// information, and a pass that quietly tidied the graph would destroy exactly the evidence that says what
/// went wrong.
/// </summary>
public enum RepairAction
{
    /// <summary>Nothing can be done automatically; a person has to look.</summary>
    None,

    /// <summary>Remove the edge, which is the only way to clear a target that no longer exists.</summary>
    RemoveLink,

    /// <summary>Detach the parent, leaving the item at the top of its tier.</summary>
    ClearParent,

    /// <summary>Leave it. The reference is honest and the reader should see it as it is.</summary>
    Keep,
}

/// <summary>
/// One thing found wrong with the graph, stated so a person can act on it without opening the database.
/// </summary>
/// <param name="Kind">What sort of break it is.</param>
/// <param name="Subject">The item or document the defect was found on.</param>
/// <param name="Target">What it pointed at.</param>
/// <param name="Suggested">What could be done about it, never what was done.</param>
/// <param name="Sentence">The defect in words.</param>
/// <param name="Edge">
/// The edge this defect is about, when it is about one. Carried as data rather than parsed back out of
/// <paramref name="Subject"/>: that field is prose for a reader, and a repair that had to take it apart
/// again would break the first time somebody reworded the sentence.
/// </param>
/// <param name="Item">The item this defect is about, when it is about one.</param>
public sealed record GraphDefect(
    DefectKind Kind,
    string Subject,
    string Target,
    RepairAction Suggested,
    string Sentence,
    DefectEdge? Edge = null,
    WorkItemId? Item = null)
{
    /// <summary>
    /// True when the graph is actually broken, as opposed to merely pointing at something that has been
    /// tombstoned. The distinction is the whole argument for tombstones: a deletion upstream that leaves a
    /// tombstone behind keeps every edge to it meaningful, and only a deletion that leaves nothing behind
    /// produces an edge nobody can read.
    /// </summary>
    public bool IsBroken => Kind is DefectKind.LinkTargetMissing
        or DefectKind.ParentMissing
        or DefectKind.WikiReferenceMissing
        or DefectKind.DocumentReferenceMissing;
}

/// <summary>The two ends and the type of an edge a defect is about.</summary>
/// <param name="Source">The source of the forward edge.</param>
/// <param name="Type">The forward type.</param>
/// <param name="Target">The target of the forward edge.</param>
public sealed record DefectEdge(WorkItemId Source, WorkItemLinkType Type, WorkItemId Target);

/// <summary>What a reconciliation pass found, and enough of a summary to report without re-counting.</summary>
/// <param name="Defects">Everything found, in the order it was found.</param>
/// <param name="ItemsChecked">How many items the pass looked at, so an empty report can be distinguished from a pass that never ran.</param>
/// <param name="LinksChecked">How many edges the pass looked at.</param>
/// <param name="Complete">
/// False when the pass could not see everything, so "nothing is broken" is not something it can claim. A
/// bounded read that reported a clean graph would be a silent cap wearing the words "all clear", which is
/// the most dangerous kind of report a repair tool can produce.
/// </param>
public sealed record GraphReport(IReadOnlyList<GraphDefect> Defects, int ItemsChecked, int LinksChecked, bool Complete = true)
{
    /// <summary>An empty report from a pass that did run.</summary>
    public static GraphReport Empty { get; } = new([], 0, 0);

    /// <summary>Defects that mean the graph genuinely has a hole in it.</summary>
    public IReadOnlyList<GraphDefect> Broken => [.. Defects.Where(d => d.IsBroken)];

    /// <summary>True when nothing is actually broken, whatever else was noted.</summary>
    public bool IsSound => Broken.Count == 0;

    /// <summary>The finding in one sentence, which is what a dashboard card and a log line both want.</summary>
    public string Summary()
    {
        var finding = Defects.Count == 0
            ? $"{ItemsChecked} items and {LinksChecked} links check out."
            : IsSound
                ? $"{ItemsChecked} items and {LinksChecked} links check out, with {Defects.Count} reference(s) to tombstoned things."
                : $"{Broken.Count} broken reference(s) across {ItemsChecked} items and {LinksChecked} links.";

        return Complete ? finding : finding + " This pass could not see the whole graph, so there may be more.";
    }
}

/// <summary>
/// Finding the defects, as a pure function of what the graph says, so the rule can be stated once and
/// tested without a store. The caller supplies what exists; this decides what that means.
/// </summary>
public static class ReferentialRepair
{
    /// <summary>
    /// Checks a set of items and the edges between them. <paramref name="present"/> is every id the graph
    /// holds, <paramref name="tombstoned"/> the subset of those that no longer exist upstream. An id in
    /// neither is missing outright.
    /// </summary>
    public static GraphReport Check(
        IReadOnlyCollection<WorkItem> items,
        IReadOnlyCollection<WorkItemLink> links,
        IReadOnlySet<WorkItemId> present,
        IReadOnlySet<WorkItemId> tombstoned)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(present);
        ArgumentNullException.ThrowIfNull(tombstoned);

        var defects = new List<GraphDefect>();

        foreach (var item in items)
        {
            if (item.Parent is not { } parent)
            {
                continue;
            }

            if (tombstoned.Contains(parent))
            {
                defects.Add(new GraphDefect(
                    DefectKind.ParentTombstoned,
                    item.Id.Value,
                    parent.Value,
                    RepairAction.Keep,
                    $"Item {item.Number} sits under a parent that no longer exists upstream. The parent is "
                    + "kept here with its history, so the hierarchy still reads.",
                    Item: item.Id));
            }
            else if (!present.Contains(parent))
            {
                defects.Add(new GraphDefect(
                    DefectKind.ParentMissing,
                    item.Id.Value,
                    parent.Value,
                    RepairAction.ClearParent,
                    $"Item {item.Number} names a parent that is not in this graph. Clearing it leaves the "
                    + "item at the top of its tier; nothing else recovers the parent.",
                    Item: item.Id));
            }
        }

        // Both ends are inspected, not just the target: an edge whose SOURCE went missing is exactly as
        // unreadable as one whose target did, and a pass that only followed the forward direction would
        // report half the holes.
        foreach (var link in links)
        {
            InspectEnd(link, link.Source);
            InspectEnd(link, link.Target);
        }

        return new GraphReport(defects, items.Count, links.Count);

        void InspectEnd(WorkItemLink link, WorkItemId end)
        {
            var reading = $"{StableNames.ToName(link.Type)} {link.Source.Value} to {link.Target.Value}";

            if (tombstoned.Contains(end))
            {
                defects.Add(new GraphDefect(
                    DefectKind.LinkTargetTombstoned,
                    reading,
                    end.Value,
                    RepairAction.Keep,
                    "This link points at an item that no longer exists upstream. It is kept, because the "
                    + "tombstone means the link can still be read.",
                    new DefectEdge(link.Source, link.Type, link.Target)));
            }
            else if (!present.Contains(end))
            {
                defects.Add(new GraphDefect(
                    DefectKind.LinkTargetMissing,
                    reading,
                    end.Value,
                    RepairAction.RemoveLink,
                    "This link points at an item that is not in this graph and left no tombstone, so "
                    + "nothing can be read from it. Removing the link is the only repair, and it loses the "
                    + "fact that the two were related.",
                    new DefectEdge(link.Source, link.Type, link.Target)));
            }
        }
    }

    /// <summary>
    /// Checks references between the two stores: what a document points at and what an item points at. Both
    /// directions are needed, because a page can be deleted while its item survives and an item can be
    /// tombstoned while its plan stays.
    /// </summary>
    /// <param name="documentReferences">Each document id with the work item numbers it references.</param>
    /// <param name="knownItemNumbers">Every work item number in the graph.</param>
    /// <param name="itemReferences">Each item id with the document slugs it references.</param>
    /// <param name="knownDocumentSlugs">Every document slug in the library.</param>
    public static IReadOnlyList<GraphDefect> CheckAcrossStores(
        IReadOnlyDictionary<string, IReadOnlyCollection<int>> documentReferences,
        IReadOnlySet<int> knownItemNumbers,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> itemReferences,
        IReadOnlySet<string> knownDocumentSlugs)
    {
        ArgumentNullException.ThrowIfNull(documentReferences);
        ArgumentNullException.ThrowIfNull(knownItemNumbers);
        ArgumentNullException.ThrowIfNull(itemReferences);
        ArgumentNullException.ThrowIfNull(knownDocumentSlugs);

        var defects = new List<GraphDefect>();

        foreach (var (document, numbers) in documentReferences)
        {
            foreach (var number in numbers.Where(n => !knownItemNumbers.Contains(n)))
            {
                defects.Add(new GraphDefect(
                    DefectKind.WikiReferenceMissing,
                    document,
                    $"#{number}",
                    RepairAction.None,
                    $"The document '{document}' refers to work item #{number}, which this graph does not "
                    + "have. It renders as a broken reference rather than as a link."));
            }
        }

        foreach (var (item, slugs) in itemReferences)
        {
            foreach (var slug in slugs.Where(s => !knownDocumentSlugs.Contains(s)))
            {
                defects.Add(new GraphDefect(
                    DefectKind.DocumentReferenceMissing,
                    item,
                    slug,
                    RepairAction.None,
                    $"Item {item} refers to the document '{slug}', which is not in the library. It may have "
                    + "been deleted on the provider, or never synced."));
            }
        }

        return defects;
    }
}
