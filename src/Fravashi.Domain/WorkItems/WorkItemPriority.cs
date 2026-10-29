namespace Fravashi.Domain.WorkItems;

/// <summary>
/// A four-value priority (decision D22). Nullable on an item: null honestly means "not prioritised".
/// </summary>
public enum WorkItemPriority
{
    /// <summary>Drop everything.</summary>
    Critical,

    /// <summary>Next.</summary>
    High,

    /// <summary>Ordinary.</summary>
    Medium,

    /// <summary>When there is room.</summary>
    Low,
}
