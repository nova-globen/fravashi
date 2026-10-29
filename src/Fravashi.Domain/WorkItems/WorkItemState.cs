using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems.AgentLayer;

namespace Fravashi.Domain.WorkItems;

/// <summary>
/// The raw, unchecked shape of a work item: what a row, a provider payload or a proposed change says. It can
/// hold anything, which is why it is not the aggregate. A <see cref="WorkItem"/> is only ever obtained from a
/// state through <see cref="WorkItem.Restore"/> or one of the checks in <see cref="WorkItemRules"/>, so an
/// aggregate that breaks a model invariant cannot exist (plan section 1.8).
/// </summary>
public sealed record WorkItemState
{
    /// <summary>The item's identity.</summary>
    public required WorkItemId Id { get; init; }

    /// <summary>The project the item belongs to.</summary>
    public required ProjectId Project { get; init; }

    /// <summary>The per-project number, <c>#n</c>.</summary>
    public required int Number { get; init; }

    /// <summary>The containment level.</summary>
    public required WorkItemLevel Level { get; init; }

    /// <summary>Standard, Spike or Bug.</summary>
    public WorkItemKind Kind { get; init; } = WorkItemKind.Standard;

    /// <summary>The title; mandatory on every item from every source.</summary>
    public required string Title { get; init; }

    /// <summary>The problem and outcome (decision D20); mandatory when authored, possibly empty when mirrored.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>The parent item, when placed.</summary>
    public WorkItemId? Parent { get; init; }

    /// <summary>The stored status.</summary>
    public WorkItemStatus Status { get; init; } = WorkItemStatus.Triage;

    /// <summary>Why a terminal item closed; null on every open item.</summary>
    public WorkItemResolution? Resolution { get; init; }

    /// <summary>The Blocked and Deferred flags.</summary>
    public WorkItemFlags Flags { get; init; } = WorkItemFlags.None;

    /// <summary>
    /// Marked for autopilot (ADR 0031 decision 1, backlog 298): settable by a person only while the item is
    /// Ready, and cleared automatically once the item reaches a terminal status. Never mirrored: a provider
    /// has no concept of it.
    /// </summary>
    public bool MarkedForAutopilot { get; init; }

    /// <summary>The priority, or null when not prioritised.</summary>
    public WorkItemPriority? Priority { get; init; }

    /// <summary>Who the item is assigned to, as the source names them.</summary>
    public string? Assignee { get; init; }

    /// <summary>The order within the item's status column.</summary>
    public double Rank { get; init; }

    /// <summary>A Spike's optional timebox.</summary>
    public TimeSpan? SpikeTimebox { get; init; }

    /// <summary>The provider reference of a mirrored item; null on the local board.</summary>
    public ExternalReference? External { get; init; }

    /// <summary>The optimistic-concurrency version, bumped by every mutation that touches the item.</summary>
    public long Version { get; init; } = 1;

    /// <summary>When the item was created.</summary>
    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>When the item last changed.</summary>
    public DateTimeOffset UpdatedUtc { get; init; }

    // ---- the agent layer (vision 5.3, plan increment C1) --------------------------------------
    // Every one of these is a LIST of addressable entries rather than a blob, because something else points
    // at an entry: evidence proves a criterion or a scenario, a readiness box is ticked by id. An empty list
    // is the honest "nobody has said anything about this yet"; null is never meaningful here.

    /// <summary>The done-test, in order. Empty on an item whose acceptance nobody has written yet.</summary>
    public IReadOnlyList<AcceptanceCriterion> AcceptanceCriteria { get; init; } = [];

    /// <summary>The definition-of-ready checklist, in order.</summary>
    public IReadOnlyList<ReadinessItem> Readiness { get; init; } = [];

    /// <summary>How ambiguous the item is thought to be, or null when nobody has assessed it.</summary>
    public AmbiguityAssessment? Ambiguity { get; init; }

    /// <summary>People the work is waiting on, cleared ones included: the history is the point.</summary>
    public IReadOnlyList<PersonBlocker> PersonBlockers { get; init; } = [];

    /// <summary>Context, plan and design pointers, in one list because they differ only by role.</summary>
    public IReadOnlyList<ArtifactRef> Refs { get; init; } = [];

    /// <summary>What one unattended attempt may change.</summary>
    public ChangeBudget ChangeBudget { get; init; } = ChangeBudget.None;

    /// <summary>The areas an unattended session may and may not touch.</summary>
    public IReadOnlyList<ScopePattern> ScopePatterns { get; init; } = [];

    /// <summary>The next concrete thing to do, in a sentence, or null.</summary>
    public string? NextAction { get; init; }

    /// <summary>The verifier's scripts, in order.</summary>
    public IReadOnlyList<TestScenario> Scenarios { get; init; } = [];

    // ---- what to run the work on (requested 2026-10-22, model and effort per work item) -----

    /// <summary>
    /// The model a conversation started from this item starts on, or null for "Default": the conversation is then
    /// started exactly as it was before the field existed. Never mirrored: no provider has a concept of it.
    /// </summary>
    public AgentModelAlias? SuggestedModel { get; init; }

    /// <summary>The effort a conversation started from this item starts with, or null for "Default" (no flag).</summary>
    public AgentEffort? SuggestedEffort { get; init; }

    // ---- which release carried it (work item #368) ---------------------------------------------

    /// <summary>
    /// The version of the release that took this item, or null while no release has (work item #368). Written only by
    /// the release store when a release is saved, never through an update: an item with a value here is archived off
    /// the board's Done column and is still found by its number, its references and the list's "Released in" filter.
    /// </summary>
    public string? ReleasedIn { get; init; }
}
