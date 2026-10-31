using System.Globalization;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Insights;
using Fravashi.Infrastructure.Sessions;
using Microsoft.Data.Sqlite;

namespace Fravashi.Infrastructure.Persistence;

/// <summary>
/// <see cref="ISessionStore"/> over the <c>agent_sessions</c> and <c>agent_session_events</c> tables
/// (schema v6, plan 2e Task 5). Stamps use the round-trip "O" format like every other timestamp
/// column here. The transcript is append-only: rows come back in insertion order, which is the order
/// the user watched them arrive in.
/// </summary>
public sealed class SqliteSessionStore(ISqliteConnectionFactory factory) : ISessionStore
{
    private readonly ISqliteConnectionFactory _factory = factory;

    public void Save(SessionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        // custom_title (schema v9, backlog 109) is the one column an update may NOT blank: a record with
        // no custom title means the caller carries no rename, so the stored one stays. That is the
        // contract on ISessionStore.Save, and it holds here rather than in every caller.
        command.CommandText =
            "INSERT INTO agent_sessions " +
            "(id, workspace_id, tool, title, custom_title, provider_session_id, state, started_utc, ended_utc) " +
            "VALUES ($id, $w, $tool, $title, $custom, $provider, $state, $started, $ended) " +
            "ON CONFLICT(id) DO UPDATE SET workspace_id = excluded.workspace_id, tool = excluded.tool, " +
            "title = excluded.title, " +
            "custom_title = COALESCE(excluded.custom_title, agent_sessions.custom_title), " +
            "provider_session_id = excluded.provider_session_id, " +
            "state = excluded.state, started_utc = excluded.started_utc, ended_utc = excluded.ended_utc;";
        command.Parameters.AddWithValue("$id", record.Id.ToString());
        command.Parameters.AddWithValue("$w", record.WorkspaceId);
        command.Parameters.AddWithValue("$tool", record.Tool.ToString());
        command.Parameters.AddWithValue("$title", record.Title);
        command.Parameters.AddWithValue("$custom", (object?)record.CustomTitle ?? DBNull.Value);
        command.Parameters.AddWithValue("$provider", (object?)record.ProviderSessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$state", record.State.ToString());
        command.Parameters.AddWithValue("$started", record.StartedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "$ended",
            (object?)record.EndedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public SessionRecord? Find(AgentSessionId id)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Select + "WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public IReadOnlyList<SessionRecord> ForWorkspace(string workspaceId, int max = 50)
    {
        var records = new List<SessionRecord>();
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Select + "WHERE workspace_id = $w ORDER BY started_utc DESC LIMIT $max;";
        command.Parameters.AddWithValue("$w", workspaceId);
        command.Parameters.AddWithValue("$max", max);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            records.Add(Read(reader));
        }

        return records;
    }

    /// <summary>
    /// Backlog 34. Same ordering as <see cref="ForWorkspace"/> and for the same reason it is safe: every
    /// stamp is written in UTC round-trip ("O") form by one clock, so the column's text order IS its
    /// chronological order. A row written with a non-UTC offset would sort by its local wall clock, and
    /// nothing in the studio writes one.
    /// </summary>
    public IReadOnlyList<SessionRecord> Unfinished()
    {
        var records = new List<SessionRecord>();
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Select + "WHERE state IN ('Pending', 'Running') ORDER BY started_utc DESC;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            records.Add(Read(reader));
        }

        return records;
    }

    public IReadOnlyList<SessionRecord> Recent(int max = 10)
    {
        var records = new List<SessionRecord>();
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Select + "ORDER BY started_utc DESC LIMIT $max;";
        command.Parameters.AddWithValue("$max", max);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            records.Add(Read(reader));
        }

        return records;
    }

    public void Append(AgentSessionId id, IEnumerable<AgentEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO agent_session_events (session_id, kind, payload, created_utc) " +
            "VALUES ($s, $kind, $payload, $created);";
        var session = command.Parameters.Add("$s", SqliteType.Text);
        var kind = command.Parameters.Add("$kind", SqliteType.Text);
        var payload = command.Parameters.Add("$payload", SqliteType.Text);
        var created = command.Parameters.Add("$created", SqliteType.Text);
        session.Value = id.ToString();

        // The last usage reading in this batch, kept to fold into the session's summary columns. The CLI
        // reports usage cumulatively (each StatsUpdated snapshot is the running total), so the newest one
        // is the whole of the session so far and the earlier ones in a batch are already inside it.
        AgentStatsSnapshot? latestUsage = null;

        // Schema v11: what each snapshot ADDS is credited to the UTC day it was taken on, starting from the
        // session's summary as it stood before this batch. Only for a session that has a row to summarise.
        SessionUsageLedger? ledger = null;

        foreach (var e in events)
        {
            if (e is StatsUpdated stats)
            {
                latestUsage = stats.Stats;
                ledger ??= LedgerFor(connection, transaction, id);
                ledger?.Observe(
                    stats.Timestamp, stats.Stats.CostUsd, stats.Stats.InputTokens, stats.Stats.OutputTokens, stats.Stats.FreeTierUnits);
            }

            if (AgentEventJson.KindOf(e) is not { } eventKind)
            {
                continue; // transient (the message still being typed)
            }

            kind.Value = eventKind;
            payload.Value = AgentEventJson.Serialize(e);
            created.Value = e.Timestamp.ToString("O", CultureInfo.InvariantCulture);
            command.ExecuteNonQuery();
        }

        if (latestUsage is { } usage)
        {
            ledger?.WriteDays(connection, transaction, id.ToString());
            WriteUsageSummary(connection, transaction, id, usage);
        }

        transaction.Commit();
    }

    /// <summary>A ledger seeded with the session's stored summary, or null when the session has no row.</summary>
    private static SessionUsageLedger? LedgerFor(SqliteConnection connection, SqliteTransaction transaction, AgentSessionId id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT cost_usd, input_tokens, output_tokens, free_tier_units FROM agent_sessions WHERE id = $s;";
        command.Parameters.AddWithValue("$s", id.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new SessionUsageLedger(ParseCost(reader, 0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3))
            : null;
    }

    /// <summary>
    /// Folds a session's latest cumulative usage into its summary columns (schema v8, backlog 19), so the
    /// cross-session totals are a cheap aggregate over <c>agent_sessions</c> rather than a scan of every
    /// transcript event. Cost is stored as invariant text to keep the exact <see cref="decimal"/>; the
    /// snapshot is cumulative, so this overwrites rather than adds.
    /// </summary>
    private static void WriteUsageSummary(
        SqliteConnection connection, SqliteTransaction transaction, AgentSessionId id, AgentStatsSnapshot usage)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "UPDATE agent_sessions SET cost_usd = $cost, input_tokens = $in, output_tokens = $out, " +
            "cache_read_tokens = $cache, cache_write_tokens = $write, free_tier_units = $units, tool_calls = $tools " +
            "WHERE id = $s;";
        command.Parameters.AddWithValue("$cost", usage.CostUsd.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$in", usage.InputTokens);
        command.Parameters.AddWithValue("$out", usage.OutputTokens);
        command.Parameters.AddWithValue("$cache", usage.CacheReadTokens);
        command.Parameters.AddWithValue("$write", usage.CacheWriteTokens);
        command.Parameters.AddWithValue("$units", usage.FreeTierUnits);
        command.Parameters.AddWithValue("$tools", usage.ToolCalls);
        command.Parameters.AddWithValue("$s", id.ToString());
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Cost and billed tokens for today, this month and all time. All time is the per-session summary, as it
    /// always was. Today and this month are what was SPENT in those windows (schema v11): each session's day
    /// rows, credited on the UTC day each usage snapshot was taken, so a conversation resumed today counts today
    /// even though it started yesterday. A day row belongs to a window when its day begins at or after the
    /// window's start, which is exact for the UTC midnights the registry passes.
    /// <para>
    /// A window's session count is every session that STARTED in it or SPENT in it: a saved session that never
    /// reported usage still counts where it started, and a resumed one counts where it spent.
    /// </para>
    /// </summary>
    public SessionUsageTotals ReadUsageTotals(DateTimeOffset dayStartUtc, DateTimeOffset monthStartUtc)
    {
        using var connection = _factory.Open();

        decimal dayCost = 0m, monthCost = 0m, allCost = 0m;
        long dayTokens = 0, monthTokens = 0, allTokens = 0;
        long dayUnits = 0, monthUnits = 0, allUnits = 0;
        var allSessions = 0;
        var daySessions = new HashSet<string>(StringComparer.Ordinal);
        var monthSessions = new HashSet<string>(StringComparer.Ordinal);

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT id, started_utc, cost_usd, input_tokens, output_tokens, free_tier_units FROM agent_sessions;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var started = ParseStamp(reader.GetString(1));
                allCost += ParseCost(reader, 2);
                // Billed tokens moved in and out; cache reads are counted separately and are not billed.
                allTokens += (reader.IsDBNull(3) ? 0L : reader.GetInt64(3)) + (reader.IsDBNull(4) ? 0L : reader.GetInt64(4));
                allUnits += reader.GetInt64(5);
                allSessions++;

                // Backlog 62: independent comparisons, not nested. A caller may pass a day outside the month.
                if (started >= monthStartUtc)
                {
                    monthSessions.Add(reader.GetString(0));
                }

                if (started >= dayStartUtc)
                {
                    daySessions.Add(reader.GetString(0));
                }
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT d.session_id, d.day_utc, d.cost_usd, d.input_tokens, d.output_tokens, d.free_tier_units " +
                "FROM agent_session_usage_days d JOIN agent_sessions s ON s.id = d.session_id;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var dayStart = DayStart(reader.GetString(1));
                var cost = ParseCost(reader, 2);
                var tokens = reader.GetInt64(3) + reader.GetInt64(4);
                var units = reader.GetInt64(5);

                if (dayStart >= monthStartUtc)
                {
                    monthCost += cost;
                    monthTokens += tokens;
                    monthUnits += units;
                    monthSessions.Add(reader.GetString(0));
                }

                if (dayStart >= dayStartUtc)
                {
                    dayCost += cost;
                    dayTokens += tokens;
                    dayUnits += units;
                    daySessions.Add(reader.GetString(0));
                }
            }
        }

        return new SessionUsageTotals(
            new UsageTotal(dayCost, dayTokens, daySessions.Count, dayUnits),
            new UsageTotal(monthCost, monthTokens, monthSessions.Count, monthUnits),
            new UsageTotal(allCost, allTokens, allSessions, allUnits));
    }

    private static DateTimeOffset DayStart(string dayKey) =>
        new(DateOnly.ParseExact(dayKey, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    /// <summary>
    /// Backlog 36, with schema v11's attribution: a day's COST is what was spent on it (the day rows), and its
    /// session count is the sessions that STARTED on it, which is what the card labels it. Started days are
    /// grouped in C# because <c>started_utc</c> is round-trip text whose offset is part of the string. A day
    /// appears when a session started on it or anything was spent on it.
    /// </summary>
    public IReadOnlyList<SessionDay> ReadSessionDays(DateOnly fromDayUtc, DateOnly toDayUtc)
    {
        var days = new Dictionary<DateOnly, (decimal Cost, int Sessions)>();
        using var connection = _factory.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT started_utc FROM agent_sessions;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var day = DateOnly.FromDateTime(ParseStamp(reader.GetString(0)).UtcDateTime);
                if (day < fromDayUtc || day > toDayUtc)
                {
                    continue;
                }

                var running = days.TryGetValue(day, out var current) ? current : (0m, 0);
                days[day] = (running.Item1, running.Item2 + 1);
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT d.day_utc, d.cost_usd FROM agent_session_usage_days d " +
                "JOIN agent_sessions s ON s.id = d.session_id WHERE d.day_utc >= $from AND d.day_utc <= $to;";
            command.Parameters.AddWithValue("$from", SessionUsageLedger.DayKey(fromDayUtc));
            command.Parameters.AddWithValue("$to", SessionUsageLedger.DayKey(toDayUtc));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var day = DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture);
                var running = days.TryGetValue(day, out var current) ? current : (0m, 0);
                days[day] = (running.Item1 + ParseCost(reader, 1), running.Item2);
            }
        }

        return [.. days.OrderBy(pair => pair.Key)
            .Select(pair => new SessionDay(pair.Key, pair.Value.Cost, pair.Value.Sessions))];
    }

    /// <summary>
    /// Backlog 36. Reads the transcript-events table, which is the only place a finished TURN is
    /// recorded, filtered to the one kind that means it. The stored stamp is bounded in SQL first (a
    /// day of slack either side, because the text carries its own offset and the widest real offset is
    /// under fifteen hours) and then tested exactly in C#, so the scan stays small without the string
    /// comparison ever deciding a boundary.
    /// </summary>
    public IReadOnlyList<DayCount> ReadTurnDays(DateOnly fromDayUtc, DateOnly toDayUtc)
    {
        var days = new Dictionary<DateOnly, int>();
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT created_utc FROM agent_session_events " +
            "WHERE kind = 'turn_completed' AND created_utc >= $from AND created_utc <= $to;";
        command.Parameters.AddWithValue("$from", Stamp(fromDayUtc.AddDays(-1)));
        command.Parameters.AddWithValue("$to", Stamp(toDayUtc.AddDays(2)));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var day = DateOnly.FromDateTime(ParseStamp(reader.GetString(0)).UtcDateTime);
            if (day < fromDayUtc || day > toDayUtc)
            {
                continue;
            }

            days[day] = days.TryGetValue(day, out var count) ? count + 1 : 1;
        }

        return [.. days.OrderBy(pair => pair.Key).Select(pair => new DayCount(pair.Key, pair.Value))];
    }

    /// <summary>
    /// Backlog 36. Only sessions that have ENDED carry a duration, so the <c>ended_utc IS NOT NULL</c>
    /// predicate is the measurement's definition rather than an optimisation.
    /// </summary>
    public DurationStats ReadSessionDurations(DateTimeOffset fromUtc)
    {
        var durations = new List<TimeSpan>();
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT started_utc, ended_utc FROM agent_sessions WHERE ended_utc IS NOT NULL;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var started = ParseStamp(reader.GetString(0));
            if (started < fromUtc)
            {
                continue;
            }

            durations.Add(ParseStamp(reader.GetString(1)) - started);
        }

        return DurationStats.From(durations);
    }

    private static string Stamp(DateOnly day) =>
        new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            .ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseStamp(string stored) =>
        DateTimeOffset.Parse(stored, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static decimal ParseCost(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? 0m
            : decimal.TryParse(
                reader.GetString(ordinal), NumberStyles.Number, CultureInfo.InvariantCulture, out var cost)
                ? cost
                : 0m;

    public IReadOnlyList<AgentEvent> Transcript(AgentSessionId id)
    {
        var events = new List<AgentEvent>();
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT kind, payload FROM agent_session_events WHERE session_id = $s ORDER BY id;";
        command.Parameters.AddWithValue("$s", id.ToString());
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (AgentEventJson.Deserialize(reader.GetString(0), reader.GetString(1)) is { } e)
            {
                events.Add(e);
            }
        }

        return events;
    }

    public void Delete(AgentSessionId id)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM agent_session_events WHERE session_id = $s; DELETE FROM agent_session_usage_days WHERE session_id = $s; " +
            "DELETE FROM agent_sessions WHERE id = $s;";
        command.Parameters.AddWithValue("$s", id.ToString());
        command.ExecuteNonQuery();
    }

    // custom_title rides last so the earlier ordinals in Read are unchanged.
    private const string Select =
        "SELECT id, workspace_id, tool, title, provider_session_id, state, started_utc, ended_utc, custom_title " +
        "FROM agent_sessions ";

    private static SessionRecord Read(SqliteDataReader reader) => new(
        new AgentSessionId(Guid.Parse(reader.GetString(0))),
        reader.GetString(1),
        Enum.TryParse<AgentTool>(reader.GetString(2), out var tool) ? tool : AgentTool.Claude,
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        Enum.TryParse<AgentSessionState>(reader.GetString(5), out var state) ? state : AgentSessionState.Completed,
        DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        reader.IsDBNull(7)
            ? null
            : DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        reader.IsDBNull(8) ? null : reader.GetString(8));
}
