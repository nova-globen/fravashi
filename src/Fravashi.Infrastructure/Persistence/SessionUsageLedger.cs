using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Fravashi.Infrastructure.Persistence;

/// <summary>
/// Turns one session's CUMULATIVE usage snapshots into what was spent on each UTC day (schema v11, backlog 62).
/// A snapshot is the running total so far, so what a snapshot adds is its increase over the previous one, and
/// that increase belongs to the day the snapshot was taken. The day and month ceilings read these days, which is
/// what keeps a conversation resumed today from counting today's spending on the day it first started.
/// <para>
/// A snapshot LOWER than the previous one is a process that started counting from zero again (a conversation
/// resumed before its usage was carried forward), so the whole of it is new spending rather than a negative.
/// </para>
/// <para>
/// Free-tier units (backlog 377) are credited the same way: a brokered turn costs nothing, and the units are what it used
/// of the day's allowance.
/// </para>
/// </summary>
internal sealed class SessionUsageLedger(decimal costUsd, long inputTokens, long outputTokens, long freeTierUnits = 0)
{
    private readonly Dictionary<string, (decimal Cost, long Input, long Output, long Units)> _days = new(StringComparer.Ordinal);
    private decimal _cost = costUsd;
    private long _input = inputTokens;
    private long _output = outputTokens;
    private long _units = freeTierUnits;

    /// <summary>The UTC calendar day a stamp falls on, in the text form the table keys by.</summary>
    public static string DayKey(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string DayKey(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public void Observe(DateTimeOffset at, decimal costUsd, long inputTokens, long outputTokens, long freeTierUnits = 0)
    {
        var restarted = costUsd < _cost || inputTokens < _input || outputTokens < _output || freeTierUnits < _units;
        var cost = restarted ? costUsd : costUsd - _cost;
        var input = restarted ? inputTokens : inputTokens - _input;
        var output = restarted ? outputTokens : outputTokens - _output;
        var units = restarted ? freeTierUnits : freeTierUnits - _units;
        _cost = costUsd;
        _input = inputTokens;
        _output = outputTokens;
        _units = freeTierUnits;

        if (cost <= 0m && input <= 0 && output <= 0 && units <= 0)
        {
            return;
        }

        var key = DayKey(at);
        var running = _days.GetValueOrDefault(key);
        _days[key] = (running.Cost + Math.Max(cost, 0m), running.Input + Math.Max(input, 0),
            running.Output + Math.Max(output, 0), running.Units + Math.Max(units, 0));
    }

    /// <summary>Adds what was observed to the session's day rows, inside the caller's transaction.</summary>
    public void WriteDays(SqliteConnection connection, SqliteTransaction transaction, string sessionId)
    {
        foreach (var (day, spent) in _days)
        {
            decimal existingCost = 0m;
            long existingInput = 0, existingOutput = 0;
            using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText =
                    "SELECT cost_usd, input_tokens, output_tokens FROM agent_session_usage_days " +
                    "WHERE session_id = $s AND day_utc = $d;";
                read.Parameters.AddWithValue("$s", sessionId);
                read.Parameters.AddWithValue("$d", day);
                using var reader = read.ExecuteReader();
                if (reader.Read())
                {
                    existingCost = decimal.TryParse(
                        reader.GetString(0), NumberStyles.Number, CultureInfo.InvariantCulture, out var c) ? c : 0m;
                    existingInput = reader.GetInt64(1);
                    existingOutput = reader.GetInt64(2);
                }
            }

            using var write = connection.CreateCommand();
            write.Transaction = transaction;
            write.CommandText =
                "INSERT INTO agent_session_usage_days (session_id, day_utc, cost_usd, input_tokens, output_tokens) " +
                "VALUES ($s, $d, $cost, $in, $out) " +
                "ON CONFLICT(session_id, day_utc) DO UPDATE SET cost_usd = excluded.cost_usd, " +
                "input_tokens = excluded.input_tokens, output_tokens = excluded.output_tokens;";
            write.Parameters.AddWithValue("$s", sessionId);
            write.Parameters.AddWithValue("$d", day);
            write.Parameters.AddWithValue("$cost", (existingCost + spent.Cost).ToString(CultureInfo.InvariantCulture));
            write.Parameters.AddWithValue("$in", existingInput + spent.Input);
            write.Parameters.AddWithValue("$out", existingOutput + spent.Output);
            write.ExecuteNonQuery();

            // Schema step 50's column. Written only when there is something to add: step 12 replays this ledger on a
            // database that does not have the column yet, and the turns it replays never carried units.
            if (spent.Units > 0)
            {
                using var units = connection.CreateCommand();
                units.Transaction = transaction;
                units.CommandText =
                    "UPDATE agent_session_usage_days SET free_tier_units = free_tier_units + $units " +
                    "WHERE session_id = $s AND day_utc = $d;";
                units.Parameters.AddWithValue("$units", spent.Units);
                units.Parameters.AddWithValue("$s", sessionId);
                units.Parameters.AddWithValue("$d", day);
                units.ExecuteNonQuery();
            }
        }
    }
}
