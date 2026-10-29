namespace Fravashi.Domain.WorkItems;

/// <summary>
/// Why a terminal item closed (plan section 1.4). Never a status, and never Deferred (decision D15): Deferred
/// is a flag on an open item. Null on a terminal item means the source did not say.
/// </summary>
public enum WorkItemResolution
{
    /// <summary>The work was done. The only resolution an authored Done carries.</summary>
    Completed,

    /// <summary>A decision was made not to do it.</summary>
    WontDo,

    /// <summary>It duplicates another item, which a <c>duplicates</c> link names.</summary>
    Duplicate,

    /// <summary>A defect nobody could reproduce.</summary>
    CannotReproduce,
}
