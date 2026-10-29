namespace Fravashi.Domain.WorkItems;

/// <summary>
/// What sort of work an item is, orthogonal to its level (plan section 1.2): Spike and Bug are types, not
/// levels.
/// </summary>
public enum WorkItemKind
{
    /// <summary>Ordinary work, at any level.</summary>
    Standard,

    /// <summary>A research question, optionally timeboxed.</summary>
    Spike,

    /// <summary>A defect; its repro steps are its description and its scenario.</summary>
    Bug,
}

/// <summary>Which levels a kind may be authored at.</summary>
public static class WorkItemKinds
{
    private static readonly IReadOnlySet<WorkItemLevel> EveryLevel = new HashSet<WorkItemLevel>(WorkItemLevels.All);

    private static readonly IReadOnlySet<WorkItemLevel> IncrementOrTask =
        new HashSet<WorkItemLevel> { WorkItemLevel.Increment, WorkItemLevel.Task };

    /// <summary>Every kind.</summary>
    public static IReadOnlyList<WorkItemKind> All { get; } = [WorkItemKind.Standard, WorkItemKind.Spike, WorkItemKind.Bug];

    /// <summary>
    /// The levels Fravashi may AUTHOR this kind at. This is an authoring rule, not an invariant: a mirrored
    /// provider Bug at a higher backlog level is stored as the provider said (plan sections 1.2 and 1.8).
    /// </summary>
    public static IReadOnlySet<WorkItemLevel> AuthoredLevels(this WorkItemKind kind) => kind switch
    {
        WorkItemKind.Standard => EveryLevel,
        WorkItemKind.Spike or WorkItemKind.Bug => IncrementOrTask,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a work item kind."),
    };

    /// <summary>True when Fravashi may author this kind at the level.</summary>
    public static bool IsAuthorableAt(this WorkItemKind kind, WorkItemLevel level) => kind.AuthoredLevels().Contains(level);
}
