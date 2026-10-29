namespace Fravashi.Domain.WorkItems;

/// <summary>Why an item cannot be started, as a stable code a board and an agent can both act on.</summary>
public enum StartBlocker
{
    /// <summary>Its status is not Ready, so nobody has said it is ready to be picked up.</summary>
    NotReady,

    /// <summary>Someone set the Blocked flag and said why.</summary>
    BlockedFlag,

    /// <summary>Someone deferred it.</summary>
    DeferredFlag,

    /// <summary>An item blocking it is not Done. A Cancelled blocker still blocks (decision D9).</summary>
    OpenBlocker,

    /// <summary>A person is still being waited on.</summary>
    PersonBlocker,
}

/// <summary>Something worth knowing about an item that can start anyway (decision D10: advisories never exclude).</summary>
public enum StartAdvisory
{
    /// <summary>Its definition-of-ready checklist still has unticked lines.</summary>
    ReadinessUnchecked,

    /// <summary>Its ambiguity score is above what an unattended session should take on.</summary>
    Ambiguous,

    /// <summary>Nobody has written an acceptance criterion, so there is no done-test to verify against.</summary>
    NoAcceptanceCriteria,
}

/// <summary>
/// The answer for one item: whether it can be started now, every reason it cannot (never only the first), and
/// the advisories that do not stop it. A reason that is an open blocker names the blockers, so a board can
/// say "waiting on #12 and #15" rather than "blocked".
/// </summary>
public sealed record StartabilityVerdict(
    WorkItemId Item,
    bool CanStart,
    IReadOnlyList<StartBlocker> Reasons,
    IReadOnlyList<WorkItemId> OpenBlockers,
    IReadOnlyList<string> WaitingOnPeople,
    IReadOnlyList<StartAdvisory> Advisories)
{
    /// <summary>
    /// Effectively blocked (plan section 4.4): the flag is set, or something blocking it is not Done, or a
    /// person is still being waited on. The STORED flag stays exactly what a person or a provider said; this
    /// is the derived answer a board shows (decision D8).
    /// </summary>
    public bool EffectivelyBlocked =>
        Reasons.Contains(StartBlocker.BlockedFlag)
        || Reasons.Contains(StartBlocker.OpenBlocker)
        || Reasons.Contains(StartBlocker.PersonBlocker);
}

/// <summary>
/// Whether an item can be started now, worked out in the domain from the item and the STATUS of everything
/// blocking it (plan section 4.4). Pure by construction: it reads no store, so the same four rules answer for
/// the local board, for a provider-mirrored project, and for item 44's gate.
/// <para>
/// A parent's blockers are not inherited by its children (decision D11), so the caller passes exactly the
/// items that block THIS one. A mirrored cycle needs no special case: each item in it has a blocker that is
/// not Done, so each is reported with that reason.
/// </para>
/// </summary>
public static class Startability
{
    /// <summary>
    /// The verdict for one item. <paramref name="blockers"/> are the items that block it, whatever their
    /// status; only their status is read, which is what keeps this a pure function of the graph's shape.
    /// </summary>
    public static StartabilityVerdict Evaluate(WorkItem item, IEnumerable<WorkItem> blockers)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(blockers);

        var reasons = new List<StartBlocker>();
        var flags = item.Flags ?? WorkItemFlags.None;

        if (item.Status != WorkItemStatus.Ready)
        {
            reasons.Add(StartBlocker.NotReady);
        }

        if (flags.Blocked)
        {
            reasons.Add(StartBlocker.BlockedFlag);
        }

        if (flags.Deferred)
        {
            reasons.Add(StartBlocker.DeferredFlag);
        }

        // Not Done, which a Cancelled blocker also is not: a dependency that was abandoned did not deliver
        // what this item needs, so it still blocks until someone unlinks it deliberately (decision D9).
        var open = blockers
            .Where(b => b.Status != WorkItemStatus.Done)
            .Select(b => b.Id)
            .Distinct()
            .ToList();
        if (open.Count > 0)
        {
            reasons.Add(StartBlocker.OpenBlocker);
        }

        var people = item.ActivePersonBlockers.Select(b => b.Person).Distinct().ToList();
        if (people.Count > 0)
        {
            reasons.Add(StartBlocker.PersonBlocker);
        }

        var advisories = new List<StartAdvisory>();
        if (item.Readiness.Any(r => !r.Checked))
        {
            advisories.Add(StartAdvisory.ReadinessUnchecked);
        }

        if (item.Ambiguity is { } ambiguity
            && ambiguity.Score > AgentLayer.AmbiguityAssessment.WorkableCeiling)
        {
            advisories.Add(StartAdvisory.Ambiguous);
        }

        if (item.AcceptanceCriteria.Count == 0)
        {
            advisories.Add(StartAdvisory.NoAcceptanceCriteria);
        }

        return new StartabilityVerdict(item.Id, reasons.Count == 0, reasons, open, people, advisories);
    }

    /// <summary>
    /// A sentence for each reason, for a board column or an agent's own reading. Written here rather than in
    /// the UI so the wording cannot drift between the two surfaces that show it.
    /// </summary>
    public static string Describe(StartBlocker reason) => reason switch
    {
        StartBlocker.NotReady => "It is not Ready yet.",
        StartBlocker.BlockedFlag => "It is flagged as blocked.",
        StartBlocker.DeferredFlag => "It is deferred.",
        StartBlocker.OpenBlocker => "Something it is blocked by is not Done.",
        StartBlocker.PersonBlocker => "It is waiting on a person.",
        _ => "It cannot start.",
    };

    /// <summary>A sentence for each advisory: worth knowing, never a reason to hold the item back.</summary>
    public static string Describe(StartAdvisory advisory) => advisory switch
    {
        StartAdvisory.ReadinessUnchecked => "Its readiness checklist still has unticked lines.",
        StartAdvisory.Ambiguous => "Its ambiguity score is high for an unattended session.",
        StartAdvisory.NoAcceptanceCriteria => "It has no acceptance criteria, so there is no done-test.",
        _ => string.Empty,
    };
}
