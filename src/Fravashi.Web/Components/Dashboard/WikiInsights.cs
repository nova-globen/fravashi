using System.Globalization;
using Fravashi.Domain.Wiki;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;

namespace Fravashi.Web.Components.Dashboard;

/// <summary>
/// What Home's Wiki card says about a project's documents (work item #358, replacing #354's duplicated page
/// count): how many documents of each type, how many are drafts, and which open work items have no plan. Pure, over
/// what the wiki library and the tracker already return, so the rule is testable without a page.
/// </summary>
public static class WikiInsights
{
    /// <summary>One type's count, for one bar.</summary>
    /// <param name="Name">The type's stable name, which the wiki's <c>?type=</c> filter reads.</param>
    public sealed record TypeCount(WikiItemType Type, string Name, string Label, int Count);

    /// <summary>The documents by type, largest first and then in the type's own order; types with none are left out.</summary>
    public static IReadOnlyList<TypeCount> ByType(IEnumerable<WikiItem> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        return
        [
            .. documents
                .GroupBy(d => d.Type)
                .Select(g => new TypeCount(g.Key, WikiNames.ToName(g.Key), Label(g.Key), g.Count()))
                .OrderByDescending(t => t.Count)
                .ThenBy(t => t.Type),
        ];
    }

    /// <summary>A type as a person reads it: the wiki page's own words (ADR in capitals, the rest as named).</summary>
    public static string Label(WikiItemType type) => type switch
    {
        WikiItemType.Adr => "ADR",
        _ => type.ToString(),
    };

    /// <summary>
    /// The open work items that no plan covers, in number order. An item is planned when its agent layer names a plan
    /// (a ref with the Plan role, or a wiki ref to a document that is a plan), or when a plan document names the item,
    /// by a typed link or a reference in its body: the same either-way-round reading the board's related documents
    /// and the work gate use for "covers".
    /// </summary>
    public static IReadOnlyList<WorkItem> Unplanned(IEnumerable<WorkItem> items, IEnumerable<WikiItem> documents)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(documents);

        var plans = documents.Where(d => d.Type == WikiItemType.Plan).ToList();
        var planNumbers = plans.Select(p => p.Number).ToHashSet();
        var covered = new HashSet<int>();
        foreach (var plan in plans)
        {
            foreach (var link in plan.Links)
            {
                if (link.Kind == WikiLinkKind.WorkItem && link.Number is { } number)
                {
                    covered.Add(number);
                }
            }

            foreach (var found in WikiReferences.Find(plan.Body))
            {
                if (found.Reference.Kind == WikiReferenceKind.WorkItem)
                {
                    covered.Add(found.Reference.Number);
                }
            }
        }

        return
        [
            .. items
                .Where(i => i.Status.IsOpen() && !covered.Contains(i.Number) && !NamesAPlan(i, planNumbers))
                .OrderBy(i => i.Number),
        ];
    }

    private static bool NamesAPlan(WorkItem item, HashSet<int> planNumbers) =>
        item.Refs.Any(r => r.Role == ArtifactRefRole.Plan
            || (r.Kind is ArtifactRefKind.Wiki or ArtifactRefKind.Design
                && int.TryParse(r.Target.TrimStart('@'), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                && planNumbers.Contains(number)));
}
