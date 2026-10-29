using System.Globalization;
using System.Text.Json;
using Fravashi.Application.Mirroring;
using Fravashi.Domain.Mirroring;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Fravashi.Infrastructure.Mirroring;

/// <summary>
/// The provider mirroring sidecar in the studio's own database (schema step 22, backlog 82): what the
/// provider last said about each item, and the field disagreements waiting for a person.
/// <para>
/// The two counts on <see cref="MirrorState"/> are COUNTED from the outbox and conflict tables on every
/// read rather than kept as columns. A denormalised counter drifts the first time a write path forgets to
/// adjust it, and a freshness indicator that says "nothing is waiting" while something is waiting is worse
/// than showing no indicator at all.
/// </para>
/// </summary>
public sealed class SqliteMirrorStore(ISqliteConnectionFactory factory) : IMirrorStore
{
    /// <summary>
    /// The sidecar row joined to its two derived counts. The correlated subqueries are what keep the
    /// counts honest; at the scale of one person's board this costs nothing worth optimising away.
    /// </summary>
    private const string Select = """
        SELECT m.item_id, m.connection, m.provider_key, m.last_confirmed_utc, m.provider_version,
               m.local_version, m.tombstoned_utc, m.tombstone_reason,
               (SELECT COUNT(*) FROM work_item_outbox o
                 WHERE o.project_id = m.project_id AND o.item_id = m.item_id
                   AND o.state IN ('Pending', 'InFlight')) AS pending,
               (SELECT COUNT(*) FROM work_item_outbox o
                 WHERE o.project_id = m.project_id AND o.item_id = m.item_id
                   AND o.state IN ('Unresolved', 'Failed')) AS stuck,
               (SELECT COUNT(*) FROM work_item_conflicts c
                 WHERE c.project_id = m.project_id AND c.item_id = m.item_id
                   AND c.resolved_utc IS NULL) AS conflicts
          FROM work_item_mirror m
        """;

    /// <inheritdoc />
    public async Task<MirrorState?> GetAsync(ProjectId project, WorkItemId item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(item);

        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE m.project_id = $p AND m.item_id = $item;";
        command.Parameters.AddWithValue("$p", project.Value);
        command.Parameters.AddWithValue("$item", item.Value);

        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadState(reader) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<WorkItemId, MirrorState>> ForProjectAsync(ProjectId project, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE m.project_id = $p;";
        command.Parameters.AddWithValue("$p", project.Value);

        var states = new Dictionary<WorkItemId, MirrorState>();
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var state = ReadState(reader);
            states[state.Item] = state;
        }

        return states;
    }

    /// <inheritdoc />
    public async Task ConfirmAsync(ProjectId project, WorkItemId item, ProviderConfirmation confirmation, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(confirmation);
        ArgumentException.ThrowIfNullOrEmpty(confirmation.Connection);
        ArgumentException.ThrowIfNullOrEmpty(confirmation.Key);

        using var db = factory.Open();
        using var command = db.CreateCommand();

        // The tombstone is cleared here on purpose. An item coming back on the provider (restored from its
        // trash, or a key reused) is a real thing that happens, and leaving the tombstone would hide a live
        // item behind a deletion notice that is no longer true.
        command.CommandText = """
            INSERT INTO work_item_mirror
                (project_id, item_id, connection, provider_key, last_confirmed_utc, provider_version,
                 local_version, tombstoned_utc, tombstone_reason, base_fields)
            VALUES ($p, $item, $conn, $key, $now, $pv, $lv, NULL, NULL, $base)
            ON CONFLICT (project_id, item_id) DO UPDATE SET
                connection = $conn,
                provider_key = $key,
                last_confirmed_utc = $now,
                provider_version = $pv,
                local_version = $lv,
                tombstoned_utc = NULL,
                tombstone_reason = NULL,
                base_fields = COALESCE($base, base_fields);
            """;
        command.Parameters.AddWithValue("$p", project.Value);
        command.Parameters.AddWithValue("$item", item.Value);
        command.Parameters.AddWithValue("$conn", confirmation.Connection);
        command.Parameters.AddWithValue("$key", confirmation.Key);
        command.Parameters.AddWithValue("$now", Stamp(now));
        command.Parameters.AddWithValue("$pv", (object?)confirmation.ProviderVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$lv", confirmation.LocalVersion);

        // Null leaves whatever base was already there. A write's response says what the provider holds for
        // the fields that write touched, not for all of them, so recording a partial answer as the base
        // would make every field it did not mention look moved on the next comparison.
        command.Parameters.AddWithValue(
            "$base",
            confirmation.BaseFields is null ? DBNull.Value : JsonSerializer.Serialize(confirmation.BaseFields));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string?>> BaseFieldsAsync(ProjectId project, WorkItemId item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(item);

        using var db = factory.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT base_fields FROM work_item_mirror WHERE project_id = $p AND item_id = $item;";
        command.Parameters.AddWithValue("$p", project.Value);
        command.Parameters.AddWithValue("$item", item.Value);

        var stored = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        if (stored is not { Length: > 0 })
        {
            return Empty;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string?>>(stored) ?? Empty;
        }
        catch (JsonException)
        {
            // An unreadable base means "we do not know what it was", which is exactly what no base means.
            // Answering with an empty one makes the next comparison treat both sides as moved, so a
            // difference becomes a question for a person rather than a silent overwrite either way.
            return Empty;
        }
    }

    private static readonly IReadOnlyDictionary<string, string?> Empty =
        new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task TombstoneAsync(ProjectId project, WorkItemId item, string reason, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(item);

        using var db = factory.Open();
        using var command = db.CreateCommand();

        // Nothing local is deleted (ADR 0018 decision 5). The work item, its evidence, its links and its
        // history stay exactly where they were; only this row learns that the provider no longer has it.
        // An item never mirrored still gets a row, because a tombstone with no provenance is still the
        // truthful record that something was found to be gone.
        command.CommandText = """
            INSERT INTO work_item_mirror
                (project_id, item_id, connection, provider_key, last_confirmed_utc, provider_version,
                 local_version, tombstoned_utc, tombstone_reason, base_fields)
            VALUES ($p, $item, 'unknown', 'unknown', NULL, NULL, 0, $now, $why, NULL)
            ON CONFLICT (project_id, item_id) DO UPDATE SET
                tombstoned_utc = $now,
                tombstone_reason = $why;
            """;
        command.Parameters.AddWithValue("$p", project.Value);
        command.Parameters.AddWithValue("$item", item.Value);
        command.Parameters.AddWithValue("$now", Stamp(now));
        command.Parameters.AddWithValue("$why", reason ?? string.Empty);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> RetireAsync(ProjectId project, string connection, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        using var db = factory.Open();
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$p", project.Value);
        command.Parameters.AddWithValue("$c", connection);
        command.Parameters.AddWithValue("$now", now.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        command.CommandText =
            "INSERT INTO work_item_mirror_history (project_id, item_id, connection, provider_key, last_confirmed_utc, tombstoned_utc, retired_utc) "
            + "SELECT project_id, item_id, connection, provider_key, last_confirmed_utc, tombstoned_utc, $now "
            + "FROM work_item_mirror WHERE project_id = $p AND connection = $c; "
            // A question about an item on the old provider has nobody left to answer it there.
            + "UPDATE work_item_conflicts SET resolved_utc = $now, "
            + "note = COALESCE(note || ' ', '') || 'Closed without a choice when the project stopped following ' || $c || '.' "
            + "WHERE project_id = $p AND resolved_utc IS NULL "
            + "AND item_id IN (SELECT item_id FROM work_item_mirror WHERE project_id = $p AND connection = $c); "
            + "DELETE FROM work_item_mirror WHERE project_id = $p AND connection = $c;";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        using var count = db.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = "SELECT changes();";
        var retired = Convert.ToInt32(await count.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        transaction.Commit();
        return retired;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<(string Connection, string Key)>> HistoryAsync(ProjectId project, WorkItemId item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(item);

        using var db = factory.Open();
        using var command = db.CreateCommand();
        command.CommandText =
            "SELECT connection, provider_key FROM work_item_mirror_history WHERE project_id = $p AND item_id = $i "
            + "ORDER BY retired_utc DESC, rowid DESC;";
        command.Parameters.AddWithValue("$p", project.Value);
        command.Parameters.AddWithValue("$i", item.Value);
        var history = new List<(string, string)>();
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            history.Add((reader.GetString(0), reader.GetString(1)));
        }

        return history;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FieldConflict>> OpenConflictsAsync(ProjectId project, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        using var db = factory.Open();
        using var command = db.CreateCommand();
        command.CommandText =
            "SELECT conflict_id, item_id, field, base_value, local_value, provider_value, detected_utc, "
            + "choice, resolved_utc, note FROM work_item_conflicts "
            + "WHERE project_id = $p AND resolved_utc IS NULL ORDER BY detected_utc, conflict_id;";
        command.Parameters.AddWithValue("$p", project.Value);

        var conflicts = new List<FieldConflict>();
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            conflicts.Add(new FieldConflict(
                reader.GetString(0),
                new WorkItemId(reader.GetString(1)),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                Read(reader.GetString(6)),
                reader.IsDBNull(7) ? null : Enum.Parse<ConflictChoice>(reader.GetString(7)),
                reader.IsDBNull(8) ? null : Read(reader.GetString(8)))
            {
                Note = reader.IsDBNull(9) ? null : reader.GetString(9),
            });
        }

        return conflicts;
    }

    /// <inheritdoc />
    public async Task<FieldConflict> RecordConflictAsync(ProjectId project, FieldConflict conflict, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(conflict);

        using var db = factory.Open();

        // An open conflict for the same field is UPDATED rather than joined by a second row. A sync that
        // runs every minute would otherwise turn one disagreement into a hundred, and a conflict surface
        // with a hundred copies of one question is a surface nobody opens twice.
        using var existing = db.CreateCommand();
        existing.CommandText =
            "SELECT conflict_id FROM work_item_conflicts WHERE project_id = $p AND item_id = $item "
            + "AND field = $field AND resolved_utc IS NULL LIMIT 1;";
        existing.Parameters.AddWithValue("$p", project.Value);
        existing.Parameters.AddWithValue("$item", conflict.Item.Value);
        existing.Parameters.AddWithValue("$field", conflict.Field);
        var found = await existing.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;

        var id = found ?? conflict.Id;

        using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO work_item_conflicts
                (project_id, conflict_id, item_id, field, base_value, local_value, provider_value,
                 note, detected_utc, choice, resolved_utc)
            VALUES ($p, $id, $item, $field, $base, $local, $provider, $note, $at, NULL, NULL)
            ON CONFLICT (project_id, conflict_id) DO UPDATE SET
                base_value = $base,
                local_value = $local,
                provider_value = $provider,
                note = $note;
            """;
        command.Parameters.AddWithValue("$p", project.Value);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$item", conflict.Item.Value);
        command.Parameters.AddWithValue("$field", conflict.Field);
        command.Parameters.AddWithValue("$base", (object?)conflict.BaseValue ?? DBNull.Value);
        command.Parameters.AddWithValue("$local", (object?)conflict.LocalValue ?? DBNull.Value);
        command.Parameters.AddWithValue("$provider", (object?)conflict.ProviderValue ?? DBNull.Value);
        command.Parameters.AddWithValue("$note", (object?)conflict.Note ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", Stamp(conflict.DetectedUtc));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return conflict with { Id = id };
    }

    /// <inheritdoc />
    public async Task<bool> ResolveConflictAsync(ProjectId project, string conflictId, ConflictChoice choice, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(conflictId);

        using var db = factory.Open();
        using var transaction = db.BeginTransaction();

        // What is being settled has to be read before it is closed, because carrying the decision out needs
        // the item and the field it was about.
        using var read = db.CreateCommand();
        read.Transaction = transaction;
        read.CommandText =
            "SELECT item_id, field, local_value, provider_value FROM work_item_conflicts "
            + "WHERE project_id = $p AND conflict_id = $id AND resolved_utc IS NULL;";
        read.Parameters.AddWithValue("$p", project.Value);
        read.Parameters.AddWithValue("$id", conflictId);

        string item;
        string field;
        string? agreed;
        using (var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            // Only an OPEN conflict is settled. Re-settling a closed one would rewrite what somebody already
            // decided, and the record of that decision is the only thing that explains the value that holds.
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return false;
            }

            item = reader.GetString(0);
            field = reader.GetString(1);

            // The decision is carried out by REBASING onto the side that was NOT chosen, which is the whole
            // mechanism and is worth saying plainly. Taking the provider's value sets the agreed value to
            // OURS, so the next pass sees a field only the provider moved and writes theirs here. Keeping
            // ours sets it to THEIRS, so the next pass sees a field only this board moved and queues ours to
            // be sent. Nothing new carries a settled conflict out: the ordinary pass does, through the same
            // code that already handles a move the board refuses and a status that cannot be expressed.
            var side = choice == ConflictChoice.Provider ? 2 : 3;
            agreed = reader.IsDBNull(side) ? null : reader.GetString(side);
        }

        using var close = db.CreateCommand();
        close.Transaction = transaction;
        close.CommandText =
            "UPDATE work_item_conflicts SET choice = $choice, resolved_utc = $now "
            + "WHERE project_id = $p AND conflict_id = $id AND resolved_utc IS NULL;";
        close.Parameters.AddWithValue("$choice", choice.ToString());
        close.Parameters.AddWithValue("$now", Stamp(now));
        close.Parameters.AddWithValue("$p", project.Value);
        close.Parameters.AddWithValue("$id", conflictId);
        if (await close.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
        {
            return false;
        }

        await RebaseAsync(db, transaction, project, item, field, agreed, ct).ConfigureAwait(false);
        transaction.Commit();
        return true;
    }

    /// <summary>
    /// Writes ONE field of the agreed value, leaving the rest of it alone. It is the consequence of settling
    /// a disagreement, and it is a read and a write rather than one statement because the agreed value is a
    /// JSON document rather than a column. It rides in the same transaction as the settling, so a decision
    /// and its consequence cannot come apart.
    /// </summary>
    private static async Task RebaseAsync(
        SqliteConnection db,
        SqliteTransaction transaction,
        ProjectId project,
        string item,
        string field,
        string? agreed,
        CancellationToken ct)
    {
        using var read = db.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT base_fields FROM work_item_mirror WHERE project_id = $p AND item_id = $item;";
        read.Parameters.AddWithValue("$p", project.Value);
        read.Parameters.AddWithValue("$item", item);
        var stored = await read.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;

        Dictionary<string, string?> fields;
        try
        {
            fields = stored is { Length: > 0 }
                ? JsonSerializer.Deserialize<Dictionary<string, string?>>(stored) ?? []
                : [];
        }
        catch (JsonException)
        {
            fields = [];
        }

        fields[field] = agreed;

        using var write = db.CreateCommand();
        write.Transaction = transaction;
        write.CommandText =
            "UPDATE work_item_mirror SET base_fields = $fields WHERE project_id = $p AND item_id = $item;";
        write.Parameters.AddWithValue("$fields", JsonSerializer.Serialize(fields));
        write.Parameters.AddWithValue("$p", project.Value);
        write.Parameters.AddWithValue("$item", item);
        await write.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static MirrorState ReadState(SqliteDataReader reader) => new(
        new WorkItemId(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : Read(reader.GetString(3)),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetInt64(5),
        reader.GetInt32(8),
        reader.GetInt32(9),
        reader.GetInt32(10),
        reader.IsDBNull(6) ? null : Read(reader.GetString(6)),
        reader.IsDBNull(7) ? null : reader.GetString(7));

    private static string Stamp(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Read(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
