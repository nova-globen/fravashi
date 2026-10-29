using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;

namespace Fravashi.Application.WorkItems;

/// <summary>
/// An update to every settable field except the status, the resolution, the rank and the links (plan section
/// 3.2): each field is an <see cref="Optional{T}"/>, so "not mentioned" and "set to empty" differ. Changing the
/// status is <see cref="ITracker.TransitionAsync"/>; changing the order is <see cref="ITracker.MoveAsync"/>.
/// </summary>
public sealed record WorkItemPatch
{
    /// <summary>A new title.</summary>
    public Optional<string> Title { get; init; }

    /// <summary>A new description.</summary>
    public Optional<string> Description { get; init; }

    /// <summary>A new level.</summary>
    public Optional<WorkItemLevel> Level { get; init; }

    /// <summary>A new kind.</summary>
    public Optional<WorkItemKind> Kind { get; init; }

    /// <summary>A new parent, or null to unplace the item.</summary>
    public Optional<WorkItemId?> Parent { get; init; }

    /// <summary>A new priority, or null.</summary>
    public Optional<WorkItemPriority?> Priority { get; init; }

    /// <summary>A new assignee, or null.</summary>
    public Optional<string?> Assignee { get; init; }

    /// <summary>A new Spike timebox, or null.</summary>
    public Optional<TimeSpan?> SpikeTimebox { get; init; }

    /// <summary>New Blocked and Deferred flags.</summary>
    public Optional<WorkItemFlags> Flags { get; init; }

    /// <summary>Marks or unmarks the item for autopilot (ADR 0031 decision 1, backlog 298).</summary>
    public Optional<bool> MarkedForAutopilot { get; init; }

    // ---- the agent layer (increment C1) -------------------------------------------------------
    // Each list is REPLACED WHOLE, and the replacement keeps the id of any entry whose id the caller passed
    // back (plan section 5.2), so evidence pointing at an unchanged criterion survives an edit of its
    // neighbours. An entry with no id is new and gets one.

    /// <summary>The whole acceptance list, in order.</summary>
    public Optional<IReadOnlyList<AcceptanceCriterion>> AcceptanceCriteria { get; init; }

    /// <summary>The whole readiness checklist, in order.</summary>
    public Optional<IReadOnlyList<ReadinessItem>> Readiness { get; init; }

    /// <summary>A new ambiguity assessment, or null to say nobody has assessed it.</summary>
    public Optional<AmbiguityAssessment?> Ambiguity { get; init; }

    /// <summary>The whole person-blocker list; clearing one is passing it back with a cleared stamp.</summary>
    public Optional<IReadOnlyList<PersonBlocker>> PersonBlockers { get; init; }

    /// <summary>The whole reference list, every role together.</summary>
    public Optional<IReadOnlyList<ArtifactRef>> Refs { get; init; }

    /// <summary>A new change budget.</summary>
    public Optional<ChangeBudget> ChangeBudget { get; init; }

    /// <summary>The whole scope-pattern list.</summary>
    public Optional<IReadOnlyList<ScopePattern>> ScopePatterns { get; init; }

    /// <summary>A new next action, or null.</summary>
    public Optional<string?> NextAction { get; init; }

    /// <summary>The whole scenario list, in order, each with its steps.</summary>
    public Optional<IReadOnlyList<TestScenario>> Scenarios { get; init; }

    /// <summary>A new suggested model, or null for Default (requested 2026-10-22, model and effort per work item).</summary>
    public Optional<AgentModelAlias?> SuggestedModel { get; init; }

    /// <summary>A new suggested effort, or null for Default (requested 2026-10-22).</summary>
    public Optional<AgentEffort?> SuggestedEffort { get; init; }
}
