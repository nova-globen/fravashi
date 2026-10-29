namespace Fravashi.Domain.WorkItems;

/// <summary>
/// A link type as it is READ from one end (plan section 4.1). Four forward types are stored
/// (<see cref="Blocks"/>, <see cref="Relates"/>, <see cref="Duplicates"/>, <see cref="Supersedes"/>); the
/// other three are the inverse readings of the same edge. Parent and child is not a link type: it is the
/// hierarchy.
/// </summary>
public enum WorkItemLinkType
{
    /// <summary>The target cannot start until the source is Done. Forward.</summary>
    Blocks,

    /// <summary>The inverse reading of <see cref="Blocks"/>.</summary>
    BlockedBy,

    /// <summary>A stated association with no scheduling effect. Symmetric.</summary>
    Relates,

    /// <summary>The source is a duplicate of the target. Forward.</summary>
    Duplicates,

    /// <summary>The inverse reading of <see cref="Duplicates"/>.</summary>
    DuplicatedBy,

    /// <summary>The source replaces the target (ADR 0020). Forward.</summary>
    Supersedes,

    /// <summary>The inverse reading of <see cref="Supersedes"/>.</summary>
    SupersededBy,
}

/// <summary>The readings of a link type.</summary>
public static class WorkItemLinkTypes
{
    /// <summary>Every reading.</summary>
    public static IReadOnlyList<WorkItemLinkType> All { get; } =
    [
        WorkItemLinkType.Blocks, WorkItemLinkType.BlockedBy, WorkItemLinkType.Relates, WorkItemLinkType.Duplicates,
        WorkItemLinkType.DuplicatedBy, WorkItemLinkType.Supersedes, WorkItemLinkType.SupersededBy,
    ];

    /// <summary>The four types an edge is stored under.</summary>
    public static IReadOnlyList<WorkItemLinkType> Forward { get; } =
        [WorkItemLinkType.Blocks, WorkItemLinkType.Relates, WorkItemLinkType.Duplicates, WorkItemLinkType.Supersedes];

    /// <summary>True for a stored (forward) type; <see cref="WorkItemLinkType.Relates"/> is its own inverse and forward.</summary>
    public static bool IsForward(this WorkItemLinkType type) => type.ToForward() == type;

    /// <summary>True for <see cref="WorkItemLinkType.Relates"/>, the only symmetric type.</summary>
    public static bool IsSymmetric(this WorkItemLinkType type) => type == WorkItemLinkType.Relates;

    /// <summary>The forward type a reading is stored under.</summary>
    public static WorkItemLinkType ToForward(this WorkItemLinkType type) => type switch
    {
        WorkItemLinkType.Blocks or WorkItemLinkType.BlockedBy => WorkItemLinkType.Blocks,
        WorkItemLinkType.Relates => WorkItemLinkType.Relates,
        WorkItemLinkType.Duplicates or WorkItemLinkType.DuplicatedBy => WorkItemLinkType.Duplicates,
        WorkItemLinkType.Supersedes or WorkItemLinkType.SupersededBy => WorkItemLinkType.Supersedes,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not a link type."),
    };

    /// <summary>How the same edge reads from its other end.</summary>
    public static WorkItemLinkType Inverse(this WorkItemLinkType type) => type switch
    {
        WorkItemLinkType.Blocks => WorkItemLinkType.BlockedBy,
        WorkItemLinkType.BlockedBy => WorkItemLinkType.Blocks,
        WorkItemLinkType.Relates => WorkItemLinkType.Relates,
        WorkItemLinkType.Duplicates => WorkItemLinkType.DuplicatedBy,
        WorkItemLinkType.DuplicatedBy => WorkItemLinkType.Duplicates,
        WorkItemLinkType.Supersedes => WorkItemLinkType.SupersededBy,
        WorkItemLinkType.SupersededBy => WorkItemLinkType.Supersedes,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not a link type."),
    };
}
