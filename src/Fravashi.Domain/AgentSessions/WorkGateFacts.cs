namespace Fravashi.Domain.AgentSessions;

/// <summary>
/// Everything the governance gates need to know about a session and the change it is attempting (backlog
/// 85). It is a record rather than a row of boolean parameters because the list grew past the point where
/// a call site could be read, and because a caller that forgets one now fails to compile instead of
/// silently passing <c>true</c>.
/// <para>
/// Every field is worded so that <c>false</c> is the CLOSED answer. A caller that cannot work something
/// out passes false and the gate holds, which is the behaviour every one of these has to have.
/// </para>
/// </summary>
/// <param name="HasWork">The session names at least one work item (gate G1).</param>
/// <param name="WorkIsWorkable">
/// At least one named item is still open. Binding to a Cancelled or Done item is what item 85 calls out:
/// a gate that only checks a link exists is cleared by pointing at work nobody is doing.
/// </param>
/// <param name="HasPlan">The session names at least one plan (gate G2).</param>
/// <param name="PlanHasContent">
/// At least one named plan says something. An empty document satisfies the letter of "there is a plan" and
/// none of its point.
/// </param>
/// <param name="PlanCoversWork">
/// At least one named plan names at least one of the session's work items, in its links or in its prose.
/// A plan for other work is not a plan for this.
/// </param>
/// <param name="InDeclaredScope">
/// The attempted change is inside what the work declares it may touch. True when nothing declares a scope:
/// an item that has not said where the work happens has not forbidden anywhere either.
/// </param>
/// <param name="WithinChangeBudget">
/// Changing this file keeps the session inside the smallest change budget its work declares. True when
/// nothing declares one.
/// </param>
public sealed record WorkGateFacts(
    bool HasWork = false,
    bool WorkIsWorkable = false,
    bool HasPlan = false,
    bool PlanHasContent = false,
    bool PlanCoversWork = false,
    bool InDeclaredScope = false,
    bool WithinChangeBudget = false)
{
    /// <summary>Everything a session needs, for a caller that is testing something else.</summary>
    public static WorkGateFacts Cleared { get; } = new(true, true, true, true, true, true, true);

    /// <summary>Nothing at all, which is what an unbound session has.</summary>
    public static WorkGateFacts None { get; } = new();
}
