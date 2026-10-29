using System.Globalization;
using Fravashi.Application.Mirroring;
using Fravashi.Domain.Mirroring;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Fravashi.Domain;

namespace Fravashi.Infrastructure.Mirroring;

/// <summary>
/// The durable write queue in the studio's own database (schema step 22, backlog 82).
/// <para>
/// Unlike most stores here, this one does NOT swallow read failures and answer with a safe default. An
/// unreadable queue that answers "nothing to send" is indistinguishable from a drained one, so the drain
/// would report a healthy pass while the user's changes sat unsent and unmentioned. The exception travels,
/// and the caller decides what to say about it.
/// </para>
/// </summary>
public sealed class SqliteOutboxStore(ISqliteConnectionFactory factory) : IOutboxStore
{
    private const string Columns =
        "project_id, operation_id, item_id, kind, payload, expected_version, state, attempts, "
        + "queued_utc, last_attempt_utc, result_key, failure";

    /// <inheritdoc />
    public async Task<OutboxEntry> QueueAsync(OutboxEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var existing = await FindAsync(entry.Project, entry.Id, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            // Queueing the same operation id twice is one write, which is what makes an enqueue that is
            // itself retried safe. It is the same idempotency the operation ledger gives a local mutation.
            return existing;
        }

        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"INSERT INTO work_item_outbox ({Columns}) "
            + "VALUES ($p, $op, $item, $kind, $payload, $version, $state, $attempts, $queued, $last, $key, $failure);";
        command.Parameters.AddWithValue("$p", entry.Project.Value);
        command.Parameters.AddWithValue("$op", entry.Id);
        command.Parameters.AddWithValue("$item", entry.Item.Value);
        command.Parameters.AddWithValue("$kind", entry.Kind.ToString());
        command.Parameters.AddWithValue("$payload", entry.Payload);
        command.Parameters.AddWithValue("$version", entry.ExpectedVersion);
        command.Parameters.AddWithValue("$state", entry.State.ToString());
        command.Parameters.AddWithValue("$attempts", entry.Attempts);
        command.Parameters.AddWithValue("$queued", Stamp(entry.QueuedUtc));
        command.Parameters.AddWithValue("$last", (object?)Stamp(entry.LastAttemptUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$key", (object?)entry.ResultKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$failure", (object?)entry.Failure ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return entry;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<OutboxEntry>> ForItemAsync(ProjectId project, WorkItemId item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(item);

        return ReadAsync(
            $"SELECT {Columns} FROM work_item_outbox WHERE project_id = $p AND item_id = $item "
            + "ORDER BY queued_utc, rowid;",
            command =>
            {
                command.Parameters.AddWithValue("$p", project.Value);
                command.Parameters.AddWithValue("$item", item.Value);
            },
            ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<OutboxEntry>> OpenAsync(ProjectId project, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        // In the order they were queued. Entries queued in one act can share a stamp, and the tie used to fall to the
        // operation id, which is random: a whole board sent at once reached the provider in no particular order.
        return ReadAsync(
            $"SELECT {Columns} FROM work_item_outbox WHERE project_id = $p "
            + "AND state NOT IN ('Confirmed', 'Abandoned') ORDER BY queued_utc, rowid;",
            command => command.Parameters.AddWithValue("$p", project.Value),
            ct);
    }

    /// <inheritdoc />
    public async Task<int> RecoverInFlightAsync(CancellationToken ct = default)
    {
        // One statement across every project. At the moment this runs nothing can legitimately be in
        // flight: the only process that could have been making the call is the one that just started.
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE work_item_outbox SET state = 'Unresolved', failure = $why WHERE state = 'InFlight';";
        command.Parameters.AddWithValue(
            "$why",
            $"This write was in flight when {ProductName.Short} stopped, so whether the provider acted on it is "
            + "unknown. It will be looked up by its operation id before anything is sent again.");

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> MarkAsync(
        ProjectId project,
        string operationId,
        OutboxState state,
        string? resultKey,
        string? failure,
        bool countAttempt,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(operationId);

        using var connection = factory.Open();
        using var command = connection.CreateCommand();

        // A result key is only ever added, never cleared: it is the provider's own identity for the item
        // and losing it would strand every later write for that item.
        command.CommandText =
            "UPDATE work_item_outbox SET state = $state, failure = $failure, "
            + "result_key = COALESCE($key, result_key)"
            + (countAttempt ? ", attempts = attempts + 1, last_attempt_utc = $now" : string.Empty)
            + " WHERE project_id = $p AND operation_id = $op;";
        command.Parameters.AddWithValue("$state", state.ToString());
        command.Parameters.AddWithValue("$failure", (object?)failure ?? DBNull.Value);
        command.Parameters.AddWithValue("$key", (object?)resultKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$p", project.Value);
        command.Parameters.AddWithValue("$op", operationId);
        if (countAttempt)
        {
            command.Parameters.AddWithValue("$now", Stamp(now));
        }

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    private async Task<OutboxEntry?> FindAsync(ProjectId project, string operationId, CancellationToken ct)
    {
        var rows = await ReadAsync(
            $"SELECT {Columns} FROM work_item_outbox WHERE project_id = $p AND operation_id = $op;",
            command =>
            {
                command.Parameters.AddWithValue("$p", project.Value);
                command.Parameters.AddWithValue("$op", operationId);
            },
            ct).ConfigureAwait(false);

        return rows.Count > 0 ? rows[0] : null;
    }

    private async Task<IReadOnlyList<OutboxEntry>> ReadAsync(string sql, Action<SqliteCommand> bind, CancellationToken ct)
    {
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);

        var entries = new List<OutboxEntry>();
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            entries.Add(new OutboxEntry(
                reader.GetString(1),
                new ProjectId(reader.GetString(0)),
                new WorkItemId(reader.GetString(2)),
                Enum.Parse<OutboxKind>(reader.GetString(3)),
                reader.GetString(4),
                reader.GetInt64(5),
                Enum.Parse<OutboxState>(reader.GetString(6)),
                reader.GetInt32(7),
                Read(reader.GetString(8))!.Value,
                reader.IsDBNull(9) ? null : Read(reader.GetString(9)),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11)));
        }

        return entries;
    }

    private static string? Stamp(DateTimeOffset? at) => at?.ToString("O", CultureInfo.InvariantCulture);

    private static string Stamp(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? Read(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
