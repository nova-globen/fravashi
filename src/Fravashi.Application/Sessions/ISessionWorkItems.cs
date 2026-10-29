using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;

namespace Fravashi.Application.Sessions;

/// <summary>One work item a session is doing, and when it was linked.</summary>
/// <param name="Item">The item's identity.</param>
/// <param name="Project">The project it belongs to.</param>
/// <param name="LinkedUtc">When the link was made.</param>
/// <param name="LinkedBy">Who made it: <c>user</c>, or a session id when the agent linked itself.</param>
public sealed record SessionWorkItem(WorkItemId Item, ProjectId Project, DateTimeOffset LinkedUtc, string LinkedBy);

/// <summary>
/// What a session is working on (ADR 0020, backlog 44). Gate G1 asks this one question of every workspace
/// mutation, so the answer has to be cheap, durable, and honest about failing.
/// <para>
/// <b>It fails closed.</b> Every implementation answers <see cref="HasWork"/> with false when it cannot tell,
/// because an unanswerable question must not open the gate. A store that throws is a denied change, never an
/// allowed one; the acceptance for item 44 says every failure mode denies.
/// </para>
/// </summary>
public interface ISessionWorkItems
{
    /// <summary>Raised when a session's links change, so a surface showing them can follow.</summary>
    event Action<AgentSessionId>? Changed;

    /// <summary>
    /// Whether the session names at least one work item. This is the gate's whole question, and it is
    /// separate from <see cref="For"/> so that answering it never depends on reading item rows.
    /// </summary>
    bool HasWork(AgentSessionId session);

    /// <summary>The items the session is doing, oldest link first. Empty when it names none.</summary>
    IReadOnlyList<SessionWorkItem> For(AgentSessionId session);

    /// <summary>Links an item to a session. Linking one that is already linked changes nothing.</summary>
    void Link(AgentSessionId session, WorkItemId item, ProjectId project, string linkedBy);

    /// <summary>Unlinks an item. Unlinking one that is not linked changes nothing.</summary>
    void Unlink(AgentSessionId session, WorkItemId item);

    /// <summary>Drops every link of a session, when the conversation itself is deleted.</summary>
    void Forget(AgentSessionId session);
}
