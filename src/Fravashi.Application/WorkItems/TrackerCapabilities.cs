namespace Fravashi.Application.WorkItems;

/// <summary>
/// What a tracker can actually do, one flag per operation (plan section 3.2). A provider adapter reports what
/// its credential was PROBED to do, never what a non-empty token suggests; the local board reports everything
/// it implements. <see cref="RecordEvidence"/> arrives with increment C2.
/// </summary>
public sealed record TrackerCapabilities(bool Create, bool Update, bool Transition, bool Move, bool Link, bool RecordEvidence)
{
    /// <summary>A read-only tracker.</summary>
    public static TrackerCapabilities ReadOnly { get; } = new(false, false, false, false, false, false);
}
