using Fravashi.Domain.AgentSessions;

namespace Fravashi.Application.Sessions;

/// <summary>One plan a session is working under, and when it was linked.</summary>
/// <param name="WikiItemId">The wiki item's identity. It is of type plan; the store that links it checks.</param>
/// <param name="LinkedUtc">When the link was made.</param>
/// <param name="LinkedBy">Who made it: <c>user</c>, or a session id when the agent linked itself.</param>
public sealed record SessionPlan(string WikiItemId, DateTimeOffset LinkedUtc, string LinkedBy);

/// <summary>
/// The plan a session is working under (ADR 0020 gate G2, backlog 45). It is a separate binding from the
/// work items because the two are set at different moments: the work says what is wanted, and the plan says
/// how it will be done, and a session acquires them in that order.
/// <para>
/// <b>It fails closed</b>, exactly as <see cref="ISessionWorkItems"/> does: a store that cannot answer says
/// there is no plan, which closes the gate rather than opening it.
/// </para>
/// </summary>
public interface ISessionPlans
{
    /// <summary>Raised when a session's plan links change.</summary>
    event Action<AgentSessionId>? Changed;

    /// <summary>Whether the session names a plan. This is the gate's whole question.</summary>
    bool HasPlan(AgentSessionId session);

    /// <summary>The plans the session is working under, oldest link first.</summary>
    IReadOnlyList<SessionPlan> For(AgentSessionId session);

    /// <summary>Links a plan. Linking one already linked changes nothing.</summary>
    void Link(AgentSessionId session, string wikiItemId, string linkedBy);

    /// <summary>Unlinks a plan. Unlinking one that is not linked changes nothing.</summary>
    void Unlink(AgentSessionId session, string wikiItemId);

    /// <summary>Drops every plan link of a session, when the conversation itself is deleted.</summary>
    void Forget(AgentSessionId session);
}
