using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>
/// Which role each session runs under, in the studio's own database (schema step 19, backlog 88).
/// <para>
/// The catalogue is the shipped set for now. Roles are meant to be a team's own, and the table this reads
/// is ready to hold authored ones, but shipping an editor for them before anybody has run under one would
/// be building the second half first. What is fixed either way is the CAPABILITY vocabulary, so a role
/// somebody writes later still says something this studio can enforce.
/// </para>
/// </summary>
public sealed class SqliteSessionRoles(
    ISqliteConnectionFactory factory,
    ILogger<SqliteSessionRoles>? logger = null) : ISessionRoles
{
    private readonly ILogger _logger = logger ?? NullLogger<SqliteSessionRoles>.Instance;

    /// <inheritdoc />
    public event Action<AgentSessionId>? Changed;

    /// <inheritdoc />
    public IReadOnlyList<AgentRole> Catalogue => AgentRoles.All;

    /// <inheritdoc />
    public AgentRole? For(AgentSessionId session)
    {
        try
        {
            using var connection = factory.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT role_id FROM agent_session_roles WHERE session_id = $s;";
            command.Parameters.AddWithValue("$s", Key(session));
            return command.ExecuteScalar() is string id ? Resolve(id) : null;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException)
        {
            // Fails TOWARD the role: "we could not tell" must not read as "anything goes". The most
            // restrictive shipped role is the honest answer to an unreadable one.
            _logger.LogWarning(ex, "The studio could not read the role of session {SessionId}.", session);
            return MostRestrictive;
        }
    }

    /// <inheritdoc />
    public bool Assign(AgentSessionId session, string roleId)
    {
        if (Resolve(roleId) is null)
        {
            return false;
        }

        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO agent_session_roles (session_id, role_id) VALUES ($s, $r) "
            + "ON CONFLICT (session_id) DO UPDATE SET role_id = $r;";
        command.Parameters.AddWithValue("$s", Key(session));
        command.Parameters.AddWithValue("$r", roleId);
        command.ExecuteNonQuery();

        Changed?.Invoke(session);
        return true;
    }

    /// <inheritdoc />
    public void Clear(AgentSessionId session)
    {
        Delete(session);
        Changed?.Invoke(session);
    }

    /// <inheritdoc />
    public void Forget(AgentSessionId session) => Delete(session);

    /// <summary>The role with the fewest capabilities, which is what an unreadable store answers with.</summary>
    private static AgentRole MostRestrictive { get; } =
        AgentRoles.All.OrderBy(r => System.Numerics.BitOperations.PopCount((uint)r.Capabilities)).First();

    private static AgentRole? Resolve(string? id) => AgentRoles.Find(id);

    private void Delete(AgentSessionId session)
    {
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM agent_session_roles WHERE session_id = $s;";
        command.Parameters.AddWithValue("$s", Key(session));
        command.ExecuteNonQuery();
    }

    private static string Key(AgentSessionId session) => session.Value.ToString("D");
}

/// <summary>
/// The studio tool grants a session holds BY ITS ROLE (backlog 88). It replaces the fixed default, so the
/// capability split item 41 built is now driven by something a person chooses rather than by a constant.
/// </summary>
public sealed class RoleStudioToolGrants(ISessionRoles roles)
    : Application.Sessions.Tools.IStudioToolGrants
{
    /// <inheritdoc />
    public Application.Sessions.Tools.StudioToolGrant For(AgentSessionId session) =>
        roles.For(session) is { } role
            ? new Application.Sessions.Tools.StudioToolGrant(
                CanRead: role.Holds(RoleCapabilities.Read),
                CanWrite: role.Holds(RoleCapabilities.WriteGraph))
            : Application.Sessions.Tools.StudioToolGrant.Full;
}
