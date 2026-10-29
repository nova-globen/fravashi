using Fravashi.Domain.AgentSessions;

namespace Fravashi.Application.Sessions;

/// <summary>
/// The branch a session's attempt must be made FROM, when something other than the folder's current branch
/// decides it (#315). An autopilot run records its base branch at dispatch (#310), and every item's attempt
/// starts from that branch, never from whatever the folder has checked out: in a sequential run that was the
/// previous item's attempt branch, so the second item's recorded base became the first item's branch and
/// commit-to-main delivery moved THAT branch instead of the base. A session with nothing set here (a person's
/// own conversation) branches from the current branch, as it always has.
/// </summary>
public interface ISessionAttemptBases
{
    /// <summary>The branch this session's attempt is made from, or null to use the current branch.</summary>
    string? For(AgentSessionId session);

    /// <summary>Sets the branch this session's attempt is made from.</summary>
    void Set(AgentSessionId session, string baseBranch);
}
