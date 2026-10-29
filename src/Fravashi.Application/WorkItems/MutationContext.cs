using Fravashi.Domain.WorkItems;

namespace Fravashi.Application.WorkItems;

/// <summary>
/// Who is making a mutation and under which client operation id (plan section 3.2, decision D18). The actor is
/// required, so every change is attributable. A mutation whose operation id is already recorded for the project
/// is a replay: nothing is written and the current state comes back marked replayed. Reusing an id for a
/// different operation or different items is <c>Invalid</c> with <see cref="TrackerCodes.OperationIdReused"/>.
/// Only a successful mutation records its id, so a call refused as Conflict or Invalid may be retried with it.
/// </summary>
/// <param name="Actor">The user, a session by id, or the system.</param>
/// <param name="OperationId">The client-generated idempotency key, or null.</param>
public sealed record MutationContext(WorkItemActor Actor, string? OperationId = null)
{
    /// <summary>The user, with no operation id.</summary>
    public static MutationContext User { get; } = new(WorkItemActor.User);

    /// <summary>
    /// The actor's written reason for the change, or null. Today one rule reads it: a move into Done while an
    /// acceptance criterion has no evidence is allowed only with a reason, which is then stored on the item as
    /// an <see cref="Fravashi.Domain.WorkItems.AgentLayer.EvidenceKind.NoEvidenceReason"/> record (request
    /// 2026-10-22, evidence on Done).
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// True when the change is a provider's, arriving through the mirror (a reconcile adopting the provider's
    /// status), rather than an act of a person or an agent here. The rules that judge an act, such as asking
    /// for evidence on Done, do not refuse it: the provider already holds that state, and refusing would only
    /// leave the two stores disagreeing (ADR 0018).
    /// </summary>
    public bool FromProvider { get; init; }
}
