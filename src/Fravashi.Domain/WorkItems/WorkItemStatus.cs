namespace Fravashi.Domain.WorkItems;

/// <summary>
/// The seven statuses, in board order (plan section 1.3). Only the status is stored; its
/// <see cref="StatusCategory"/> is derived and can never disagree with it (decision D7).
/// </summary>
public enum WorkItemStatus
{
    /// <summary>Captured, not yet looked at.</summary>
    Triage,

    /// <summary>Accepted as work, not yet ready.</summary>
    Backlog,

    /// <summary>Ready to start.</summary>
    Ready,

    /// <summary>Being worked.</summary>
    InProgress,

    /// <summary>Waiting on review.</summary>
    InReview,

    /// <summary>Finished. Terminal.</summary>
    Done,

    /// <summary>Closed without being finished. Terminal.</summary>
    Cancelled,
}

/// <summary>The derived facts about a status.</summary>
public static class WorkItemStatuses
{
    /// <summary>Every status, in board order.</summary>
    public static IReadOnlyList<WorkItemStatus> All { get; } =
    [
        WorkItemStatus.Triage, WorkItemStatus.Backlog, WorkItemStatus.Ready, WorkItemStatus.InProgress,
        WorkItemStatus.InReview, WorkItemStatus.Done, WorkItemStatus.Cancelled,
    ];

    /// <summary>The category the status belongs to: a pure function, never stored.</summary>
    public static StatusCategory Category(this WorkItemStatus status) => status switch
    {
        WorkItemStatus.Triage or WorkItemStatus.Backlog or WorkItemStatus.Ready => StatusCategory.Proposed,
        WorkItemStatus.InProgress or WorkItemStatus.InReview => StatusCategory.InProgress,
        WorkItemStatus.Done => StatusCategory.Done,
        WorkItemStatus.Cancelled => StatusCategory.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Not a work item status."),
    };

    /// <summary>True for Done and Cancelled.</summary>
    public static bool IsTerminal(this WorkItemStatus status) =>
        status is WorkItemStatus.Done or WorkItemStatus.Cancelled;

    /// <summary>True for every status that is not terminal.</summary>
    public static bool IsOpen(this WorkItemStatus status) => !status.IsTerminal();
}
