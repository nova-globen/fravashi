using Fravashi.Domain.WorkItems;

namespace Fravashi.Application.WorkItems;

/// <summary>A page of query results and the cursor for the next page, null when this is the last.</summary>
/// <param name="Items">The items, in query order.</param>
/// <param name="NextCursor">Pass back as <see cref="WorkItemQuery.Cursor"/>; null on the last page.</param>
public sealed record WorkItemPage(IReadOnlyList<WorkItem> Items, string? NextCursor);
