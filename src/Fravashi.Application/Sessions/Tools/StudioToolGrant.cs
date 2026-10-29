using Fravashi.Domain.AgentSessions;

namespace Fravashi.Application.Sessions.Tools;

/// <summary>
/// What a session is allowed to do with the studio's own tools (backlog 41, threat model requirement 1:
/// "retrieval tools and mutation tools are distinct capabilities, so a session, a role, or an autopilot policy
/// can hold one without the other").
/// <para>
/// The distinction already in <see cref="StudioToolAccess"/> is a POSTURE: it says what a tool does. This is a
/// GRANT: it says what one session may reach. They are separate on purpose, because the threat model's point is
/// that a reader-agent must be unable to write even when the writing tool exists and is registered. The server
/// enforces it in two places, listing and calling, so a session that never sees a tool still cannot call it by
/// guessing its name.
/// </para>
/// </summary>
/// <param name="CanRead">Whether the session may call retrieval tools.</param>
/// <param name="CanWrite">Whether the session may call mutation tools.</param>
public readonly record struct StudioToolGrant(bool CanRead, bool CanWrite)
{
    /// <summary>Retrieval and mutation, which is what an ordinary interactive session holds.</summary>
    public static StudioToolGrant Full { get; } = new(true, true);

    /// <summary>Retrieval only: the reader-agent the threat model asks to be expressible.</summary>
    public static StudioToolGrant ReadOnly { get; } = new(true, false);

    /// <summary>Nothing at all, for a session that must not reach the graph.</summary>
    public static StudioToolGrant None { get; } = new(false, false);

    /// <summary>Whether this grant covers a tool with that posture.</summary>
    public bool Covers(StudioToolAccess access) =>
        access == StudioToolAccess.ReadOnly ? CanRead : CanWrite;
}

/// <summary>
/// What each session holds. It is a port rather than a field on the session so that the policy can come from a
/// role (item 88) or from an autopilot run (item 58) later without the tool server learning about either.
/// </summary>
public interface IStudioToolGrants
{
    /// <summary>The grant for a session. A session nobody has said anything about gets the default.</summary>
    StudioToolGrant For(AgentSessionId session);
}

/// <summary>
/// The grant every session holds until something says otherwise: retrieval and mutation, because a person is
/// reading each mutation's approval prompt. It is a class rather than a constant so that item 58's autopilot
/// can replace it with a policy without touching the server.
/// </summary>
public sealed class DefaultStudioToolGrants(StudioToolGrant grant) : IStudioToolGrants
{
    /// <summary>Grants <see cref="StudioToolGrant.Full"/> to every session.</summary>
    public DefaultStudioToolGrants()
        : this(StudioToolGrant.Full)
    {
    }

    /// <inheritdoc />
    public StudioToolGrant For(AgentSessionId session) => grant;
}
