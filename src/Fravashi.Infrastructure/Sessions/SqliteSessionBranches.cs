using System.Globalization;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>
/// The branch each execution attempt runs on, in the studio's own database (schema step 21, backlog 86).
/// A read that fails answers with NO branch, which makes the caller start one rather than commit onto
/// whatever happens to be checked out.
/// </summary>
public sealed class SqliteSessionBranches(
    ISqliteConnectionFactory factory,
    ILogger<SqliteSessionBranches>? logger = null) : ISessionBranches
{
    private readonly ILogger _logger = logger ?? NullLogger<SqliteSessionBranches>.Instance;

    /// <inheritdoc />
    public SessionBranch? For(AgentSessionId session)
    {
        try
        {
            using var connection = factory.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT branch, base_branch, started_utc FROM agent_session_branches WHERE session_id = $s;";
            command.Parameters.AddWithValue("$s", Key(session));
            using var reader = command.ExecuteReader();

            return reader.Read()
                ? new SessionBranch(
                    reader.GetString(0),
                    reader.GetString(1),
                    DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))
                : null;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException or FormatException)
        {
            _logger.LogWarning(ex, "The studio could not read the branch of session {SessionId}.", session);
            return null;
        }
    }

    /// <inheritdoc />
    public void Record(AgentSessionId session, SessionBranch branch)
    {
        ArgumentNullException.ThrowIfNull(branch);

        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO agent_session_branches (session_id, branch, base_branch, started_utc) "
            + "VALUES ($s, $b, $base, $at) ON CONFLICT (session_id) DO UPDATE SET "
            + "branch = $b, base_branch = $base, started_utc = $at;";
        command.Parameters.AddWithValue("$s", Key(session));
        command.Parameters.AddWithValue("$b", branch.Branch);
        command.Parameters.AddWithValue("$base", branch.BaseBranch);
        command.Parameters.AddWithValue("$at", branch.StartedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public void Forget(AgentSessionId session)
    {
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM agent_session_branches WHERE session_id = $s;";
        command.Parameters.AddWithValue("$s", Key(session));
        command.ExecuteNonQuery();
    }

    private static string Key(AgentSessionId session) => session.Value.ToString("D");
}
