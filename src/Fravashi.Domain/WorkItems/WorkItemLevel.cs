namespace Fravashi.Domain.WorkItems;

/// <summary>
/// Where a work item sits in the containment <c>Tier &gt; Feature &gt; Increment &gt; Task</c> (vision
/// section 5, plan section 1.1). Every item has exactly one level.
/// </summary>
public enum WorkItemLevel
{
    /// <summary>A portfolio grouping; never has a parent.</summary>
    Tier,

    /// <summary>One capability; its specification is a linked wiki document.</summary>
    Feature,

    /// <summary>A shippable, PR-sized slice whose Verify gate is its acceptance criteria.</summary>
    Increment,

    /// <summary>A concrete step; owns no document of its own.</summary>
    Task,
}

/// <summary>The containment arithmetic over <see cref="WorkItemLevel"/>.</summary>
public static class WorkItemLevels
{
    /// <summary>Every level, highest first.</summary>
    public static IReadOnlyList<WorkItemLevel> All { get; } =
        [WorkItemLevel.Tier, WorkItemLevel.Feature, WorkItemLevel.Increment, WorkItemLevel.Task];

    /// <summary>How deep the level sits: 0 for a Tier, 3 for a Task. A smaller depth is a higher level.</summary>
    public static int Depth(this WorkItemLevel level) => level switch
    {
        WorkItemLevel.Tier => 0,
        WorkItemLevel.Feature => 1,
        WorkItemLevel.Increment => 2,
        WorkItemLevel.Task => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Not a work item level."),
    };

    /// <summary>True when <paramref name="level"/> is strictly higher than <paramref name="other"/>.</summary>
    public static bool IsHigherThan(this WorkItemLevel level, WorkItemLevel other) => level.Depth() < other.Depth();

    /// <summary>
    /// The one level an AUTHORED parent must have: exactly one level up (plan decision D5). Null for a Tier,
    /// which never has a parent.
    /// </summary>
    public static WorkItemLevel? AllowedParentLevel(this WorkItemLevel level) => level switch
    {
        WorkItemLevel.Tier => null,
        WorkItemLevel.Feature => WorkItemLevel.Tier,
        WorkItemLevel.Increment => WorkItemLevel.Feature,
        WorkItemLevel.Task => WorkItemLevel.Increment,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Not a work item level."),
    };
}
