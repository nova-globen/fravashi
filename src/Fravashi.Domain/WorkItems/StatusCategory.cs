namespace Fravashi.Domain.WorkItems;

/// <summary>The four categories the seven statuses fold into (vision 5.2, plan section 1.3).</summary>
public enum StatusCategory
{
    /// <summary>Not started: Triage, Backlog, Ready.</summary>
    Proposed,

    /// <summary>Being worked: In Progress, In Review.</summary>
    InProgress,

    /// <summary>Finished.</summary>
    Done,

    /// <summary>Closed without being finished.</summary>
    Cancelled,
}

/// <summary>What a category falls back to.</summary>
public static class StatusCategories
{
    /// <summary>Every category.</summary>
    public static IReadOnlyList<StatusCategory> All { get; } =
        [StatusCategory.Proposed, StatusCategory.InProgress, StatusCategory.Done, StatusCategory.Cancelled];

    /// <summary>
    /// The status used when a native provider status maps only to a category (the archived report's adapter
    /// strategy step 3, used by item 47).
    /// </summary>
    public static WorkItemStatus DefaultStatus(this StatusCategory category) => category switch
    {
        StatusCategory.Proposed => WorkItemStatus.Backlog,
        StatusCategory.InProgress => WorkItemStatus.InProgress,
        StatusCategory.Done => WorkItemStatus.Done,
        StatusCategory.Cancelled => WorkItemStatus.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Not a status category."),
    };
}
