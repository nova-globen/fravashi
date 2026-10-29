using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems.AgentLayer;

namespace Fravashi.Domain.WorkItems;

/// <summary>
/// The work item aggregate (vision section 5, plan section 1): identity, lifecycle, description and version.
/// The agent-layer groups arrive in increment C.
/// <para>
/// <b>It cannot be constructed without its invariants.</b> The constructor is private and every property is
/// get-only, so neither <c>new</c> nor a <c>with</c> expression can produce one; the only ways in are
/// <see cref="Restore"/> (the item-local invariants, for any source) and the checks in
/// <see cref="WorkItemRules"/>, which add the invariants that need a parent or children and, for a change
/// Fravashi authors, the authoring rules (plan section 1.8). To change an item, take <see cref="ToState"/>,
/// change the state, and pass it back through a check.
/// </para>
/// </summary>
public sealed record WorkItem
{
    private WorkItem(WorkItemState state)
    {
        Id = state.Id;
        Project = state.Project;
        Number = state.Number;
        Level = state.Level;
        Kind = state.Kind;
        Title = state.Title;
        Description = state.Description ?? string.Empty;
        Parent = state.Parent;
        Status = state.Status;
        Resolution = state.Resolution;
        Flags = state.Flags ?? WorkItemFlags.None;
        MarkedForAutopilot = state.MarkedForAutopilot;
        Priority = state.Priority;
        Assignee = state.Assignee;
        Rank = state.Rank;
        SpikeTimebox = state.SpikeTimebox;
        External = state.External;
        Version = state.Version;
        CreatedUtc = state.CreatedUtc;
        UpdatedUtc = state.UpdatedUtc;
        AcceptanceCriteria = state.AcceptanceCriteria ?? [];
        Readiness = state.Readiness ?? [];
        Ambiguity = state.Ambiguity;
        PersonBlockers = state.PersonBlockers ?? [];
        Refs = state.Refs ?? [];
        ChangeBudget = state.ChangeBudget ?? ChangeBudget.None;
        ScopePatterns = state.ScopePatterns ?? [];
        NextAction = state.NextAction;
        Scenarios = state.Scenarios ?? [];
        SuggestedModel = state.SuggestedModel;
        SuggestedEffort = state.SuggestedEffort;
        ReleasedIn = state.ReleasedIn;
    }

    /// <summary>The item's identity.</summary>
    public WorkItemId Id { get; }

    /// <summary>The project the item belongs to.</summary>
    public ProjectId Project { get; }

    /// <summary>The per-project number, <c>#n</c>.</summary>
    public int Number { get; }

    /// <summary>The containment level.</summary>
    public WorkItemLevel Level { get; }

    /// <summary>Standard, Spike or Bug.</summary>
    public WorkItemKind Kind { get; }

    /// <summary>The title; never blank.</summary>
    public string Title { get; }

    /// <summary>The problem and outcome. Never null; empty only on an item a provider sent without one.</summary>
    public string Description { get; }

    /// <summary>The parent item, when placed.</summary>
    public WorkItemId? Parent { get; }

    /// <summary>The stored status.</summary>
    public WorkItemStatus Status { get; }

    /// <summary>The category, derived from <see cref="Status"/> and never stored (decision D7).</summary>
    public StatusCategory Category => Status.Category();

    /// <summary>Why a terminal item closed. Always null on an open item.</summary>
    public WorkItemResolution? Resolution { get; }

    /// <summary>The Blocked and Deferred flags.</summary>
    public WorkItemFlags Flags { get; }

    /// <summary>Marked for autopilot (ADR 0031 decision 1, backlog 298); see <see cref="WorkItemState.MarkedForAutopilot"/>.</summary>
    public bool MarkedForAutopilot { get; }

    /// <summary>The priority, or null when not prioritised.</summary>
    public WorkItemPriority? Priority { get; }

    /// <summary>Who the item is assigned to.</summary>
    public string? Assignee { get; }

    /// <summary>The order within the item's status column.</summary>
    public double Rank { get; }

    /// <summary>A Spike's optional timebox.</summary>
    public TimeSpan? SpikeTimebox { get; }

    /// <summary>The provider reference of a mirrored item; null on the local board.</summary>
    public ExternalReference? External { get; }

    /// <summary>The optimistic-concurrency version.</summary>
    public long Version { get; }

    /// <summary>When the item was created.</summary>
    public DateTimeOffset CreatedUtc { get; }

    /// <summary>When the item last changed.</summary>
    public DateTimeOffset UpdatedUtc { get; }

    // ---- the agent layer (vision 5.3) ---------------------------------------------------------

    /// <summary>The done-test, in order.</summary>
    public IReadOnlyList<AcceptanceCriterion> AcceptanceCriteria { get; }

    /// <summary>The definition-of-ready checklist, in order.</summary>
    public IReadOnlyList<ReadinessItem> Readiness { get; }

    /// <summary>How ambiguous the item is thought to be, or null.</summary>
    public AmbiguityAssessment? Ambiguity { get; }

    /// <summary>People the work is waiting on, cleared ones included.</summary>
    public IReadOnlyList<PersonBlocker> PersonBlockers { get; }

    /// <summary>Context, plan and design pointers.</summary>
    public IReadOnlyList<ArtifactRef> Refs { get; }

    /// <summary>What one unattended attempt may change.</summary>
    public ChangeBudget ChangeBudget { get; }

    /// <summary>The areas an unattended session may and may not touch.</summary>
    public IReadOnlyList<ScopePattern> ScopePatterns { get; }

    /// <summary>The next concrete thing to do, or null.</summary>
    public string? NextAction { get; }

    /// <summary>The verifier's scripts, in order.</summary>
    public IReadOnlyList<TestScenario> Scenarios { get; }

    /// <summary>The suggested model (requested 2026-10-22); see <see cref="WorkItemState.SuggestedModel"/>.</summary>
    public AgentModelAlias? SuggestedModel { get; }

    /// <summary>The suggested effort (requested 2026-10-22); see <see cref="WorkItemState.SuggestedEffort"/>.</summary>
    public AgentEffort? SuggestedEffort { get; }

    /// <summary>The release that took the item (work item #368); see <see cref="WorkItemState.ReleasedIn"/>.</summary>
    public string? ReleasedIn { get; }

    /// <summary>The people still being waited on, which is what startability reads (increment C2).</summary>
    public IEnumerable<PersonBlocker> ActivePersonBlockers => PersonBlockers.Where(b => b.IsActive);

    /// <summary>The single ref in a role that holds at most one, or null.</summary>
    public ArtifactRef? RefFor(ArtifactRefRole role) => Refs.FirstOrDefault(r => r.Role == role);

    /// <summary>
    /// Builds the aggregate from a state after checking the ITEM-LOCAL model invariants: one defined level,
    /// kind and status, a non-blank title, a null resolution on an open item, and no item as its own parent.
    /// It never applies an authoring rule, so a mirrored shape (a Cancelled item with no resolution, a Blocked
    /// flag with no reason, an empty description) restores. The invariants that need the parent item are
    /// <see cref="WorkItemRules.CheckInvariants"/>'s.
    /// </summary>
    public static DomainResult<WorkItem> Restore(WorkItemState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var errors = WorkItemRules.LocalInvariantErrors(state);
        return errors.Count == 0 ? DomainResult.Ok(new WorkItem(state)) : DomainResult.Refused<WorkItem>(errors);
    }

    /// <summary>
    /// Value equality that reads the agent-layer LISTS by their contents. A record compares its members with
    /// the member's own equality, and a list's own equality is reference equality, so from increment C1 two
    /// items that say exactly the same thing stopped being equal unless they happened to share list instances.
    /// That is not a test detail: the store returns a fresh object on every read, and item 82's reconciliation
    /// will ask "is this the same item as the one I have".
    /// </summary>
    public bool Equals(WorkItem? other) =>
        other is not null
        && Id == other.Id
        && Project == other.Project
        && Number == other.Number
        && Level == other.Level
        && Kind == other.Kind
        && Title == other.Title
        && Description == other.Description
        && Parent == other.Parent
        && Status == other.Status
        && Resolution == other.Resolution
        && Flags == other.Flags
        && MarkedForAutopilot == other.MarkedForAutopilot
        && Priority == other.Priority
        && Assignee == other.Assignee
        && Rank.Equals(other.Rank)
        && SpikeTimebox == other.SpikeTimebox
        && External == other.External
        && Version == other.Version
        && CreatedUtc == other.CreatedUtc
        && UpdatedUtc == other.UpdatedUtc
        && Ambiguity == other.Ambiguity
        && ChangeBudget == other.ChangeBudget
        && NextAction == other.NextAction
        && SuggestedModel == other.SuggestedModel
        && SuggestedEffort == other.SuggestedEffort
        && ReleasedIn == other.ReleasedIn
        && AcceptanceCriteria.SequenceEqual(other.AcceptanceCriteria)
        && Readiness.SequenceEqual(other.Readiness)
        && PersonBlockers.SequenceEqual(other.PersonBlockers)
        && Refs.SequenceEqual(other.Refs)
        && ScopePatterns.SequenceEqual(other.ScopePatterns)
        && Scenarios.Count == other.Scenarios.Count
        && Scenarios.Zip(other.Scenarios).All(pair =>
            pair.First.Id == pair.Second.Id
            && pair.First.Title == pair.Second.Title
            && pair.First.Steps.SequenceEqual(pair.Second.Steps));

    /// <summary>Hashed on what identifies a version of an item, which the contents cannot contradict.</summary>
    public override int GetHashCode() => HashCode.Combine(Id, Version, Status, Title, UpdatedUtc);

    /// <summary>The item as a plain state, to change and pass back through a check.</summary>
    public WorkItemState ToState() => new()
    {
        Id = Id,
        Project = Project,
        Number = Number,
        Level = Level,
        Kind = Kind,
        Title = Title,
        Description = Description,
        Parent = Parent,
        Status = Status,
        Resolution = Resolution,
        Flags = Flags,
        MarkedForAutopilot = MarkedForAutopilot,
        Priority = Priority,
        Assignee = Assignee,
        Rank = Rank,
        SpikeTimebox = SpikeTimebox,
        External = External,
        Version = Version,
        CreatedUtc = CreatedUtc,
        UpdatedUtc = UpdatedUtc,
        AcceptanceCriteria = AcceptanceCriteria,
        Readiness = Readiness,
        Ambiguity = Ambiguity,
        PersonBlockers = PersonBlockers,
        Refs = Refs,
        ChangeBudget = ChangeBudget,
        ScopePatterns = ScopePatterns,
        NextAction = NextAction,
        Scenarios = Scenarios,
        SuggestedModel = SuggestedModel,
        SuggestedEffort = SuggestedEffort,
        ReleasedIn = ReleasedIn,
    };
}
