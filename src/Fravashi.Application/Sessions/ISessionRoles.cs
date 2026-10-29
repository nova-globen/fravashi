using Fravashi.Domain.AgentSessions;

namespace Fravashi.Application.Sessions;

/// <summary>
/// Which role each session runs under (backlog 88). A session that nobody has assigned a role runs
/// unrestricted, which is what an ordinary interactive conversation with a person watching every change
/// has always been; that is NOT the same as a role holding nothing.
/// <para>
/// <b>It fails toward the role.</b> A store that cannot say which role a session holds answers with the
/// most restrictive shipped role rather than with none, because "we could not tell" must not read as
/// "anything goes". This is the opposite direction from the other session stores, and deliberately so:
/// there, absent means unbound and the gate closes; here, absent would mean unrestricted.
/// </para>
/// </summary>
public interface ISessionRoles
{
    /// <summary>Raised when a session's role changes.</summary>
    event Action<AgentSessionId>? Changed;

    /// <summary>Every role that can be chosen, shipped and authored alike.</summary>
    IReadOnlyList<AgentRole> Catalogue { get; }

    /// <summary>The role a session runs under, or null when nobody has chosen one.</summary>
    AgentRole? For(AgentSessionId session);

    /// <summary>Puts a session under a role. An unknown id is refused and returns false.</summary>
    bool Assign(AgentSessionId session, string roleId);

    /// <summary>Takes a session back to unrestricted.</summary>
    void Clear(AgentSessionId session);

    /// <summary>Drops a session's role, when the conversation itself is deleted.</summary>
    void Forget(AgentSessionId session);
}
