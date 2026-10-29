using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;

namespace Fravashi.Application.WorkItems;

/// <summary>
/// What a caller supplies to create a work item (plan section 3.2). The id, number, rank, version and
/// timestamps are the tracker's to assign. A terminal initial status follows the authoring rules: Done gets
/// resolution Completed, Cancelled needs one.
/// </summary>
public sealed record NewWorkItem
{
    /// <summary>The containment level.</summary>
    public required WorkItemLevel Level { get; init; }

    /// <summary>Standard, Spike or Bug.</summary>
    public WorkItemKind Kind { get; init; } = WorkItemKind.Standard;

    /// <summary>The title; mandatory.</summary>
    public required string Title { get; init; }

    /// <summary>The problem and outcome; mandatory when authored.</summary>
    public required string Description { get; init; }

    /// <summary>The parent, exactly one level up, or null.</summary>
    public WorkItemId? Parent { get; init; }

    /// <summary>The initial status; Triage by default.</summary>
    public WorkItemStatus Status { get; init; } = WorkItemStatus.Triage;

    /// <summary>The resolution of a terminal initial status.</summary>
    public WorkItemResolution? Resolution { get; init; }

    /// <summary>The priority, or null when not prioritised.</summary>
    public WorkItemPriority? Priority { get; init; }

    /// <summary>Who the item is assigned to.</summary>
    public string? Assignee { get; init; }

    /// <summary>A Spike's optional timebox, stored in whole minutes.</summary>
    public TimeSpan? SpikeTimebox { get; init; }

    /// <summary>The initial Blocked and Deferred flags.</summary>
    public WorkItemFlags Flags { get; init; } = WorkItemFlags.None;

    // ---- the agent layer (increment C1) -------------------------------------------------------
    // An item can be authored with its acceptance and its guardrails already in place, which is what lets a
    // caller create something an agent could pick up without a second round trip. Entry ids are the store's
    // to mint here: a caller creating an item has no ids to pass back yet.

    /// <summary>The done-test, in order.</summary>
    public IReadOnlyList<AcceptanceCriterion> AcceptanceCriteria { get; init; } = [];

    /// <summary>The definition-of-ready checklist, in order.</summary>
    public IReadOnlyList<ReadinessItem> Readiness { get; init; } = [];

    /// <summary>How ambiguous the item is thought to be.</summary>
    public AmbiguityAssessment? Ambiguity { get; init; }

    /// <summary>People the work is waiting on.</summary>
    public IReadOnlyList<PersonBlocker> PersonBlockers { get; init; } = [];

    /// <summary>Context, plan and design references.</summary>
    public IReadOnlyList<ArtifactRef> Refs { get; init; } = [];

    /// <summary>What one unattended attempt may change.</summary>
    public ChangeBudget ChangeBudget { get; init; } = ChangeBudget.None;

    /// <summary>The areas an unattended session may and may not touch.</summary>
    public IReadOnlyList<ScopePattern> ScopePatterns { get; init; } = [];

    /// <summary>The next concrete thing to do.</summary>
    public string? NextAction { get; init; }

    /// <summary>The verifier's scripts.</summary>
    public IReadOnlyList<TestScenario> Scenarios { get; init; } = [];

    /// <summary>The suggested model, or null for Default (requested 2026-10-22, model and effort per work item).</summary>
    public Fravashi.Domain.AgentSessions.AgentModelAlias? SuggestedModel { get; init; }

    /// <summary>The suggested effort, or null for Default (requested 2026-10-22).</summary>
    public Fravashi.Domain.AgentSessions.AgentEffort? SuggestedEffort { get; init; }
}
