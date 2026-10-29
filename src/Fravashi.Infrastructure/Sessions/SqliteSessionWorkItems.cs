using System.Globalization;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>
/// What each session is working on, in the studio's own database (schema step 15, backlog 44). Durable
/// because a session outlives the window: closing the studio and reopening it must not quietly un-gate a
/// conversation, in either direction.
/// <para>
/// <b>Every failure denies.</b> A read that throws is logged and answered as "no work", which closes the
/// gate; a write that throws is raised, because a link the user asked for and did not get must not look like
/// it worked. That asymmetry is the whole of item 44's "every failure mode denies".
/// </para>
/// </summary>
public sealed class SqliteSessionWorkItems(
    ISqliteConnectionFactory factory,
    TimeProvider clock,
    ILogger<SqliteSessionWorkItems>? logger = null) : ISessionWorkItems
{
    private readonly ISqliteConnectionFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly ILogger _logger = logger ?? NullLogger<SqliteSessionWorkItems>.Instance;

    /// <inheritdoc />
    public event Action<AgentSessionId>? Changed;

    /// <inheritdoc />
    public bool HasWork(AgentSessionId session)
    {
        try
        {
            using var connection = _factory.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM agent_session_work_items WHERE session_id = $s);";
            command.Parameters.AddWithValue("$s", Key(session));
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException)
        {
            // Fail closed: an unanswerable question is not permission.
            _logger.LogWarning(ex, "The studio could not read what session {SessionId} is working on.", session);
            return false;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<SessionWorkItem> For(AgentSessionId session)
    {
        try
        {
            using var connection = _factory.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT item_id, project_id, linked_utc, linked_by FROM agent_session_work_items "
                + "WHERE session_id = $s ORDER BY linked_utc, rowid;";
            command.Parameters.AddWithValue("$s", Key(session));
            using var reader = command.ExecuteReader();

            var links = new List<SessionWorkItem>();
            while (reader.Read())
            {
                links.Add(new SessionWorkItem(
                    new WorkItemId(reader.GetString(0)),
                    new ProjectId(reader.GetString(1)),
                    DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    reader.GetString(3)));
            }

            return links;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException or FormatException)
        {
            _logger.LogWarning(ex, "The studio could not list what session {SessionId} is working on.", session);
            return [];
        }
    }

    /// <inheritdoc />
    public void Link(AgentSessionId session, WorkItemId item, ProjectId project, string linkedBy)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(project);

        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO agent_session_work_items (session_id, item_id, project_id, linked_utc, linked_by) "
            + "VALUES ($s, $i, $p, $at, $by) ON CONFLICT (session_id, item_id) DO NOTHING;";
        command.Parameters.AddWithValue("$s", Key(session));
        command.Parameters.AddWithValue("$i", item.Value);
        command.Parameters.AddWithValue("$p", project.Value);
        command.Parameters.AddWithValue("$at", _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$by", string.IsNullOrWhiteSpace(linkedBy) ? "user" : linkedBy.Trim());
        command.ExecuteNonQuery();

        Changed?.Invoke(session);
    }

    /// <inheritdoc />
    public void Unlink(AgentSessionId session, WorkItemId item)
    {
        ArgumentNullException.ThrowIfNull(item);

        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM agent_session_work_items WHERE session_id = $s AND item_id = $i;";
        command.Parameters.AddWithValue("$s", Key(session));
        command.Parameters.AddWithValue("$i", item.Value);
        command.ExecuteNonQuery();

        Changed?.Invoke(session);
    }

    /// <inheritdoc />
    public void Forget(AgentSessionId session)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM agent_session_work_items WHERE session_id = $s;";
        command.Parameters.AddWithValue("$s", Key(session));
        command.ExecuteNonQuery();

        Changed?.Invoke(session);
    }

    private static string Key(AgentSessionId session) => session.Value.ToString("D");
}
