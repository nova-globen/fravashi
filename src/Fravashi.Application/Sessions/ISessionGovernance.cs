using Fravashi.Domain.AgentSessions;

namespace Fravashi.Application.Sessions;

/// <summary>
/// What the governance gates need to know, assembled from everything the studio holds (backlog 85). It is
/// one port rather than the four stores it reads, so the approval broker asks one question and stays out
/// of the business of what a plan is.
/// <para>
/// <b>It fails closed.</b> An implementation that cannot answer returns <see cref="WorkGateFacts.None"/>,
/// which holds the gate. Nothing here ever guesses in the permissive direction.
/// </para>
/// </summary>
public interface ISessionGovernance
{
    /// <summary>
    /// The facts for one attempted change. <paramref name="attemptedPath"/> is the destination the studio
    /// resolved for the call, or null when the call names no location; a call that names nowhere cannot be
    /// out of scope, because there is nowhere for it to be out of.
    /// </summary>
    WorkGateFacts Facts(AgentSessionId session, string? attemptedPath);

    /// <summary>
    /// Records that a change to this path was APPROVED, so it counts against the change budget. Called
    /// after the decision, never before: a refused change is not a change.
    /// </summary>
    void RecordApprovedChange(AgentSessionId session, string? path);

    /// <summary>Forgets a session's recorded changes, when the conversation itself is deleted.</summary>
    void Forget(AgentSessionId session);
}

/// <summary>Which files a session has already been approved to change (schema step 18).</summary>
public interface ISessionChangedFiles
{
    /// <summary>How many distinct files the session has been approved to change.</summary>
    int Count(AgentSessionId session);

    /// <summary>Whether this exact path is already one of them, so changing it again costs nothing.</summary>
    bool Contains(AgentSessionId session, string path);

    /// <summary>
    /// Every path the session was approved to change, oldest first. Backlog 86 commits exactly these:
    /// committing everything dirty would sweep up whatever else was in the working tree and attribute it
    /// to the agent.
    /// </summary>
    IReadOnlyList<string> Paths(AgentSessionId session);

    /// <summary>Records one approved path. Recording the same path twice changes nothing.</summary>
    void Record(AgentSessionId session, string path);

    /// <summary>Drops a session's rows.</summary>
    void Forget(AgentSessionId session);
}
