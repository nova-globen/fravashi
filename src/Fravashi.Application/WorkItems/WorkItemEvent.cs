using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;

namespace Fravashi.Application.WorkItems;

/// <summary>What a recorded change did (plan section 5.1, <c>work_item_events.operation</c>).</summary>
public enum WorkItemEventOperation
{
    /// <summary>The item was created.</summary>
    Created,

    /// <summary>Fields were updated.</summary>
    Updated,

    /// <summary>The status changed.</summary>
    Transitioned,

    /// <summary>The item was reordered within its column.</summary>
    Moved,

    /// <summary>A link touching the item was added.</summary>
    Linked,

    /// <summary>A link touching the item was removed.</summary>
    Unlinked,

    /// <summary>Evidence was recorded (increment C2).</summary>
    EvidenceRecorded,

    /// <summary>A release took the item (work item #368): its "Released in" was stamped.</summary>
    Released,
}

/// <summary>The stable storage names of <see cref="WorkItemEventOperation"/>; an unknown name is refused, never defaulted.</summary>
public static class WorkItemEventOperations
{
    private static readonly IReadOnlyDictionary<WorkItemEventOperation, string> Names = new Dictionary<WorkItemEventOperation, string>
    {
        [WorkItemEventOperation.Created] = "created",
        [WorkItemEventOperation.Updated] = "updated",
        [WorkItemEventOperation.Transitioned] = "transitioned",
        [WorkItemEventOperation.Moved] = "moved",
        [WorkItemEventOperation.Linked] = "linked",
        [WorkItemEventOperation.Unlinked] = "unlinked",
        [WorkItemEventOperation.EvidenceRecorded] = "evidence_recorded",
        [WorkItemEventOperation.Released] = "released",
    };

    /// <summary>The stored name.</summary>
    public static string ToName(this WorkItemEventOperation operation) =>
        Names.TryGetValue(operation, out var name)
            ? name
            : throw new ArgumentOutOfRangeException(nameof(operation), operation, "Not an event operation.");

    /// <summary>Parses a stored name exactly; null when it is not one.</summary>
    public static WorkItemEventOperation? Parse(string? name)
    {
        foreach (var (operation, stored) in Names)
        {
            if (string.Equals(stored, name, StringComparison.Ordinal))
            {
                return operation;
            }
        }

        return null;
    }
}

/// <summary>
/// One recorded change of one item (plan section 5.1): which actor made it, under which operation id, the
/// version it produced and the fields it changed. A link writes one event on each item, both with the same
/// operation id.
/// </summary>
/// <param name="Id">The event's sequence id.</param>
/// <param name="Project">The item's project.</param>
/// <param name="Item">The item that changed.</param>
/// <param name="ItemVersion">The version this event produced.</param>
/// <param name="Operation">What kind of change it was.</param>
/// <param name="Actor">Who made it.</param>
/// <param name="OperationId">The client operation id, or null.</param>
/// <param name="ChangesJson">A JSON object <c>{field: {from, to}}</c> over the fields this event changed.</param>
/// <param name="OccurredUtc">When it happened.</param>
public sealed record WorkItemEvent(
    long Id,
    ProjectId Project,
    WorkItemId Item,
    long ItemVersion,
    WorkItemEventOperation Operation,
    WorkItemActor Actor,
    string? OperationId,
    string ChangesJson,
    DateTimeOffset OccurredUtc);
