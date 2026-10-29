using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;
using Microsoft.Data.Sqlite;

namespace Fravashi.Infrastructure.WorkItems;

/// <summary>
/// The <c>work_item_evidence</c> rows (schema step 14, increment C2). Append-only by construction: this
/// helper offers an insert and a read and nothing else, so the port's promise that a wrong record is followed
/// by a newer one rather than rewritten is a property of the storage layer too, not only of the interface.
/// </summary>
internal static class EvidenceRows
{
    /// <summary>The evidence recorded against an item, oldest first.</summary>
    public static List<EvidenceRecord> Load(
        SqliteConnection connection, SqliteTransaction? transaction, string itemId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT id, kind, gate, result, summary, artefact_ref, criterion_id, scenario_id, details_json, "
            + "recorded_utc, actor_kind, actor_id FROM work_item_evidence WHERE item_id = $i "
            + "ORDER BY recorded_utc, rowid;";
        command.Parameters.AddWithValue("$i", itemId);
        using var reader = command.ExecuteReader();

        var rows = new List<EvidenceRecord>();
        while (reader.Read())
        {
            rows.Add(new EvidenceRecord(
                reader.GetString(0),
                Name<EvidenceKind>(reader.GetString(1), "kind", itemId),
                Name<EvidenceResult>(reader.GetString(3), "result", itemId),
                reader.GetString(4),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                WorkItemRowMapper.ParseStamp(reader.GetString(9)),
                Actor(reader, 10, itemId)));
        }

        return rows;
    }

    /// <summary>Appends one record. There is deliberately no update and no delete beside it.</summary>
    public static void Insert(
        SqliteConnection connection, SqliteTransaction? transaction, string itemId, EvidenceRecord record)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO work_item_evidence (id, item_id, kind, gate, result, summary, artefact_ref, "
            + "criterion_id, scenario_id, details_json, recorded_utc, actor_kind, actor_id) "
            + "VALUES ($id, $item, $kind, $gate, $result, $summary, $artefact, $criterion, $scenario, "
            + "$details, $at, $actor_kind, $actor_id);";
        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$item", itemId);
        command.Parameters.AddWithValue("$kind", StableNames.ToName(record.Kind));
        command.Parameters.AddWithValue("$gate", (object?)record.Gate ?? DBNull.Value);
        command.Parameters.AddWithValue("$result", StableNames.ToName(record.Result));
        command.Parameters.AddWithValue("$summary", record.Summary);
        command.Parameters.AddWithValue("$artefact", (object?)record.ArtefactRef ?? DBNull.Value);
        command.Parameters.AddWithValue("$criterion", (object?)record.Criterion ?? DBNull.Value);
        command.Parameters.AddWithValue("$scenario", (object?)record.Scenario ?? DBNull.Value);
        command.Parameters.AddWithValue("$details", (object?)record.Details ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", WorkItemRowMapper.Stamp(record.RecordedUtc));
        command.Parameters.AddWithValue(
            "$actor_kind", StableNames.ToName((record.Actor ?? WorkItemActor.System).Kind));
        command.Parameters.AddWithValue("$actor_id", (object?)(record.Actor ?? WorkItemActor.System).SessionId ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <summary>Which acceptance criteria and scenarios of an item evidence already points at.</summary>
    public static (HashSet<string> Criteria, HashSet<string> Scenarios) Referenced(
        SqliteConnection connection, SqliteTransaction? transaction, string itemId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT criterion_id, scenario_id FROM work_item_evidence WHERE item_id = $i;";
        command.Parameters.AddWithValue("$i", itemId);
        using var reader = command.ExecuteReader();

        var criteria = new HashSet<string>(StringComparer.Ordinal);
        var scenarios = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                criteria.Add(reader.GetString(0));
            }

            if (!reader.IsDBNull(1))
            {
                scenarios.Add(reader.GetString(1));
            }
        }

        return (criteria, scenarios);
    }

    /// <summary>Removes an item's evidence rows, for the delete the port does not offer yet.</summary>
    public static void Remove(SqliteConnection connection, SqliteTransaction? transaction, string itemId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM work_item_evidence WHERE item_id = $i;";
        command.Parameters.AddWithValue("$i", itemId);
        command.ExecuteNonQuery();
    }

    private static WorkItemActor Actor(SqliteDataReader reader, int offset, string itemId)
    {
        var kind = Name<WorkItemActorKind>(reader.GetString(offset), "actor_kind", itemId);
        var sessionId = reader.IsDBNull(offset + 1) ? null : reader.GetString(offset + 1);
        return kind switch
        {
            WorkItemActorKind.User => WorkItemActor.User,
            WorkItemActorKind.Session => WorkItemActor.Session(sessionId ?? string.Empty),
            _ => WorkItemActor.System,
        };
    }

    /// <summary>A stored enum name, or a genuine fault: a name no value has means a corrupt database.</summary>
    private static TEnum Name<TEnum>(string stored, string field, string itemId)
        where TEnum : struct, Enum
    {
        var parsed = StableNames.Parse<TEnum>(stored, field);
        return parsed.IsValid
            ? parsed.Value
            : throw new InvalidDataException($"Work item {itemId} has an unknown stored {field} '{stored}'.");
    }
}
