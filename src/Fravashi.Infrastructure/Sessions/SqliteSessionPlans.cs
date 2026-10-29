using System.Globalization;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>
/// The plan each session is working under, in the studio's own database (schema step 17, backlog 45).
/// Durable for the same reason the work links are: closing the window must not quietly un-gate or re-gate a
/// conversation. A read that fails answers "no plan", which closes the gate.
/// </summary>
public sealed class SqliteSessionPlans(
    ISqliteConnectionFactory factory,
    TimeProvider clock,
    ILogger<SqliteSessionPlans>? logger = null) : ISessionPlans
{
    private readonly ISqliteConnectionFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly ILogger _logger = logger ?? NullLogger<SqliteSessionPlans>.Instance;

    /// <inheritdoc />
    public event Action<AgentSessionId>? Changed;

    /// <inheritdoc />
    public bool HasPlan(AgentSessionId session)
    {
        try
        {
            using var connection = _factory.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM agent_session_plans WHERE session_id = $s);";
            command.Parameters.AddWithValue("$s", Key(session));
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException)
        {
            _logger.LogWarning(ex, "The studio could not read the plan session {SessionId} works under.", session);
            return false;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<SessionPlan> For(AgentSessionId session)
    {
        try
        {
            using var connection = _factory.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT wiki_item_id, linked_utc, linked_by FROM agent_session_plans "
                + "WHERE session_id = $s ORDER BY linked_utc, rowid;";
            command.Parameters.AddWithValue("$s", Key(session));
            using var reader = command.ExecuteReader();

            var plans = new List<SessionPlan>();
            while (reader.Read())
            {
                plans.Add(new SessionPlan(
                    reader.GetString(0),
                    DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    reader.GetString(2)));
            }

            return plans;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException or FormatException)
        {
            _logger.LogWarning(ex, "The studio could not list the plans session {SessionId} works under.", session);
            return [];
        }
    }

    /// <inheritdoc />
    public void Link(AgentSessionId session, string wikiItemId, string linkedBy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wikiItemId);

        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO agent_session_plans (session_id, wiki_item_id, linked_utc, linked_by) "
            + "VALUES ($s, $i, $at, $by) ON CONFLICT (session_id, wiki_item_id) DO NOTHING;";
        command.Parameters.AddWithValue("$s", Key(session));
        command.Parameters.AddWithValue("$i", wikiItemId);
        command.Parameters.AddWithValue("$at", _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$by", string.IsNullOrWhiteSpace(linkedBy) ? "user" : linkedBy.Trim());
        command.ExecuteNonQuery();

        Changed?.Invoke(session);
    }

    /// <inheritdoc />
    public void Unlink(AgentSessionId session, string wikiItemId)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM agent_session_plans WHERE session_id = $s AND wiki_item_id = $i;";
        command.Parameters.AddWithValue("$s", Key(session));
        command.Parameters.AddWithValue("$i", wikiItemId ?? string.Empty);
        command.ExecuteNonQuery();

        Changed?.Invoke(session);
    }

    /// <inheritdoc />
    public void Forget(AgentSessionId session)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM agent_session_plans WHERE session_id = $s;";
        command.Parameters.AddWithValue("$s", Key(session));
        command.ExecuteNonQuery();

        Changed?.Invoke(session);
    }

    private static string Key(AgentSessionId session) => session.Value.ToString("D");
}
