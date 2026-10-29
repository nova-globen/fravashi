using System.Collections.Concurrent;
using System.Globalization;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>
/// The operator's attention per conversation, in the studio's own database beside the transcript (schema step 38,
/// backlog 244). Each row is closed-set names, a wait and a time; the table has no column a tool argument, a reason
/// or a prompt could go into.
/// <para>
/// A conversation's counts are cached after the first read and dropped on its next write, because the session pane
/// renders on every streamed fragment and must not query the database each time.
/// </para>
/// </summary>
public sealed class SqliteSessionAttention(
    ISqliteConnectionFactory factory,
    ILogger<SqliteSessionAttention>? logger = null) : ISessionAttention
{
    private readonly ISqliteConnectionFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    private readonly ILogger _logger = logger ?? NullLogger<SqliteSessionAttention>.Instance;
    private readonly ConcurrentDictionary<AgentSessionId, SessionAttention> _cache = new();

    /// <inheritdoc />
    public event Action<AgentSessionId>? Changed;

    /// <inheritdoc />
    public void Record(AttentionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            using var connection = _factory.Open();
            Insert(connection, null, entry);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException)
        {
            _logger.LogWarning(ex, "The studio could not count an attention entry for session {SessionId}.", entry.Session);
        }

        _cache.TryRemove(entry.Session, out _);
        Changed?.Invoke(entry.Session);
    }

    /// <inheritdoc />
    public SessionAttention For(AgentSessionId session)
    {
        if (_cache.TryGetValue(session, out var cached))
        {
            return cached;
        }

        try
        {
            using var connection = _factory.Open();
            var counted = SessionAttention.From(Read(connection, session));
            _cache[session] = counted;
            return counted;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException or FormatException)
        {
            _logger.LogWarning(ex, "The studio could not read the attention counts of session {SessionId}.", session);
            return SessionAttention.Empty;
        }
    }

    /// <inheritdoc />
    public WorkItemAttention ForItem(WorkItemId item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var sessions = new List<AgentSessionId>();
        try
        {
            using var connection = _factory.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT session_id FROM agent_session_work_items WHERE item_id = $i ORDER BY linked_utc, rowid;";
            command.Parameters.AddWithValue("$i", item.Value);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (Guid.TryParse(reader.GetString(0), out var id))
                {
                    sessions.Add(new AgentSessionId(id));
                }
            }
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException)
        {
            _logger.LogWarning(ex, "The studio could not list the conversations linked to work item {ItemId}.", item.Value);
            return new WorkItemAttention([]);
        }

        return new WorkItemAttention([.. sessions.Distinct().Select(s => new LinkedSessionAttention(s, For(s)))]);
    }

    /// <inheritdoc />
    public void Forget(AgentSessionId session)
    {
        using (var connection = _factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM agent_session_attention WHERE session_id = $s;";
            command.Parameters.AddWithValue("$s", Key(session));
            command.ExecuteNonQuery();
        }

        _cache.TryRemove(session, out _);
        Changed?.Invoke(session);
    }

    /// <summary>Writes one row, inside <paramref name="transaction"/> when there is one (the schema step's backfill).</summary>
    internal static void Insert(SqliteConnection connection, SqliteTransaction? transaction, AttentionEntry entry)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO agent_session_attention (session_id, entry, tool_kind, outcome, review, waited_ms, occurred_utc, reconstructed) "
            + "VALUES ($s, $e, $k, $o, $r, $w, $at, $rc);";
        command.Parameters.AddWithValue("$s", Key(entry.Session));
        command.Parameters.AddWithValue("$e", AttentionNames.Of(entry.Entry));
        command.Parameters.AddWithValue("$k", entry.Kind is { } kind ? AttentionNames.Of(kind) : DBNull.Value);
        command.Parameters.AddWithValue("$o", entry.Outcome is { } outcome ? AttentionNames.Of(outcome) : DBNull.Value);
        command.Parameters.AddWithValue("$r", entry.Review is { } review ? AttentionNames.Of(review) : DBNull.Value);
        command.Parameters.AddWithValue("$w", (long)Math.Round(entry.Waited.TotalMilliseconds));
        command.Parameters.AddWithValue("$at", entry.OccurredUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$rc", entry.Reconstructed ? 1 : 0);
        command.ExecuteNonQuery();
    }

    private static List<AttentionEntry> Read(SqliteConnection connection, AgentSessionId session)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT entry, tool_kind, outcome, review, waited_ms, occurred_utc, reconstructed FROM agent_session_attention "
            + "WHERE session_id = $s ORDER BY occurred_utc, id;";
        command.Parameters.AddWithValue("$s", Key(session));
        using var reader = command.ExecuteReader();

        var entries = new List<AttentionEntry>();
        while (reader.Read())
        {
            // A name this version does not know (written by a newer one) is skipped rather than miscounted.
            if (AttentionNames.EntryFrom(reader.GetString(0)) is not { } entry)
            {
                continue;
            }

            entries.Add(new AttentionEntry(
                session,
                entry,
                reader.IsDBNull(1) ? null : AttentionNames.KindFrom(reader.GetString(1)) ?? AttentionToolKind.Other,
                reader.IsDBNull(2) ? null : AttentionNames.OutcomeFrom(reader.GetString(2)),
                reader.IsDBNull(3) ? null : AttentionNames.ReviewFrom(reader.GetString(3)),
                TimeSpan.FromMilliseconds(reader.GetInt64(4)),
                DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetInt64(6) != 0));
        }

        return entries;
    }

    private static string Key(AgentSessionId session) => session.Value.ToString("D");
}
