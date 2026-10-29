using Fravashi.Domain.AgentSessions;

namespace Fravashi.Application.Sessions;

/// <summary>The branch one execution attempt runs on, and what it produced.</summary>
/// <param name="Branch">The branch name the attempt was started on.</param>
/// <param name="BaseBranch">What it was branched from, so a reader can tell where it came from.</param>
/// <param name="StartedUtc">When the attempt started.</param>
public sealed record SessionBranch(string Branch, string BaseBranch, DateTimeOffset StartedUtc);

/// <summary>
/// The branch each session's execution attempt runs on (ADR 0018 decision 9, backlog 86). A supervised
/// session can be branched by hand in the Git page; an unattended one cannot, and an attempt whose branch
/// the studio forgot at the next restart would be an attempt nobody could find afterwards.
/// </summary>
public interface ISessionBranches
{
    /// <summary>The branch this session's attempt is on, or null when it has not started one.</summary>
    SessionBranch? For(AgentSessionId session);

    /// <summary>Records the branch an attempt started on. Starting again replaces it.</summary>
    void Record(AgentSessionId session, SessionBranch branch);

    /// <summary>Drops a session's branch record, when the conversation itself is deleted.</summary>
    void Forget(AgentSessionId session);
}
