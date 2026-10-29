using Fravashi.Application.WorkItems;
using Fravashi.Domain.WorkItems;

namespace Fravashi.Web.Components.Dashboard;

/// <summary>
/// How Home's cards read a whole tracker: the same paged query the board makes (WorkItemsPage.LoadAsync),
/// following the cursor at the largest page the port allows, so a card and the board never disagree about what is
/// there. Shared by the Progress, Wiki and Autopilot cards (work item #358).
/// </summary>
internal static class TrackerReads
{
    /// <summary>Every item of one tracker.</summary>
    public static async Task<IReadOnlyList<WorkItem>> AllAsync(ITracker tracker, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        var items = new List<WorkItem>();
        string? cursor = null;
        do
        {
            var page = await tracker.QueryAsync(new WorkItemQuery { Limit = WorkItemQuery.MaxLimit, Cursor = cursor }, ct);
            items.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return items;
    }
}
