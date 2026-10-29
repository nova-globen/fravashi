using System.Globalization;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;
using Microsoft.Data.Sqlite;

namespace Fravashi.Infrastructure.WorkItems;

/// <summary>
/// The agent layer's rows (schema step 12, plan section 5.2): acceptance criteria, the readiness checklist,
/// person blockers, references, scope patterns, scenarios and their steps. Everything here is a LIST of
/// addressable entries rather than a JSON blob, because something else points at an entry: evidence proves a
/// criterion or a scenario, a readiness box is ticked by id.
/// <para>
/// A list is REPLACED WHOLE on update, and the replacement keeps the id of every entry whose id the caller
/// passed back, so a stable id survives an edit of its neighbours and evidence written against it does not
/// dangle. An entry the caller supplies with no id is new and is given one here, which is what lets a caller
/// that has never read the item author a list.
/// </para>
/// <para>
/// The ambiguity score, its assessor and stamp, the change budget's file ceiling and the next action live on
/// <c>work_items</c> itself rather than in a table: each is one value per item, not a list.
/// </para>
/// </summary>
internal static class AgentLayerRows
{
    /// <summary>The columns step 12 added to <c>work_items</c>, in the order <see cref="ReadScalars"/> expects.</summary>
    public const string ScalarColumns =
        "ambiguity_score, ambiguity_assessed_by, ambiguity_assessed_utc, change_budget_max_files, next_action";

    /// <summary>Reads the per-item scalars onto a state, given a reader positioned on a row that selected them.</summary>
    public static WorkItemState ReadScalars(WorkItemState state, SqliteDataReader reader, int offset) => state with
    {
        Ambiguity = reader.IsDBNull(offset)
            ? null
            : new AmbiguityAssessment(
                checked((int)reader.GetInt64(offset)),
                reader.IsDBNull(offset + 1) ? null : reader.GetString(offset + 1),
                reader.IsDBNull(offset + 2) ? null : WorkItemRowMapper.ParseStamp(reader.GetString(offset + 2))),
        ChangeBudget = reader.IsDBNull(offset + 3)
            ? ChangeBudget.None
            : new ChangeBudget(checked((int)reader.GetInt64(offset + 3))),
        NextAction = reader.IsDBNull(offset + 4) ? null : reader.GetString(offset + 4),
    };

    /// <summary>Binds the per-item scalars for an insert or an update.</summary>
    public static void BindScalars(SqliteCommand command, WorkItemState state)
    {
        command.Parameters.AddWithValue("$ambiguity_score", (object?)state.Ambiguity?.Score ?? DBNull.Value);
        command.Parameters.AddWithValue("$ambiguity_assessed_by", (object?)state.Ambiguity?.AssessedBy ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$ambiguity_assessed_utc",
            state.Ambiguity?.AssessedUtc is { } at ? WorkItemRowMapper.Stamp(at) : DBNull.Value);
        command.Parameters.AddWithValue(
            "$change_budget_max_files",
            (object?)(state.ChangeBudget ?? ChangeBudget.None).MaxFiles ?? DBNull.Value);
        command.Parameters.AddWithValue("$next_action", (object?)state.NextAction ?? DBNull.Value);
    }

    /// <summary>Loads every list of the agent layer onto a state.</summary>
    public static WorkItemState Load(SqliteConnection connection, SqliteTransaction? transaction, WorkItemState state)
    {
        var id = state.Id.Value;
        return state with
        {
            AcceptanceCriteria = Query(
                connection, transaction, id,
                "SELECT id, text FROM work_item_criteria WHERE item_id = $i ORDER BY ordinal;",
                r => new AcceptanceCriterion(r.GetString(0), r.GetString(1))),
            Readiness = Query(
                connection, transaction, id,
                "SELECT id, text, checked FROM work_item_readiness WHERE item_id = $i ORDER BY ordinal;",
                r => new ReadinessItem(r.GetString(0), r.GetString(1), r.GetInt64(2) != 0)),
            PersonBlockers = Query(
                connection, transaction, id,
                "SELECT id, person, reason, created_utc, cleared_utc FROM work_item_person_blockers "
                + "WHERE item_id = $i ORDER BY created_utc;",
                r => new PersonBlocker(
                    r.GetString(0),
                    r.GetString(1),
                    r.GetString(2),
                    WorkItemRowMapper.ParseStamp(r.GetString(3)),
                    r.IsDBNull(4) ? null : WorkItemRowMapper.ParseStamp(r.GetString(4)))),
            Refs = Query(
                connection, transaction, id,
                "SELECT id, role, kind, target, workspace_id, label, note, version FROM work_item_refs "
                + "WHERE item_id = $i ORDER BY role, ordinal;",
                r => new ArtifactRef(
                    r.GetString(0),
                    Name<ArtifactRefRole>(r.GetString(1), "role", id),
                    Name<ArtifactRefKind>(r.GetString(2), "kind", id),
                    r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4),
                    r.IsDBNull(5) ? null : r.GetString(5),
                    r.IsDBNull(6) ? null : r.GetString(6),
                    r.IsDBNull(7) ? null : checked((int)r.GetInt64(7)))),
            ScopePatterns = Query(
                connection, transaction, id,
                // Insertion order, which is the order the author wrote them in: the table has no ordinal because
                // a pattern list is a SET in meaning, but showing it shuffled would still be a surprise.
                "SELECT id, mode, pattern, workspace_id FROM work_item_scope_patterns WHERE item_id = $i ORDER BY rowid;",
                r => new ScopePattern(
                    r.GetString(0),
                    Name<ScopeMode>(r.GetString(1), "mode", id),
                    r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3))),
            Scenarios = LoadScenarios(connection, transaction, id),
        };
    }

    /// <summary>
    /// Replaces every list for an item, keeping the id of each entry whose id the caller passed back. Delete
    /// then insert, inside the caller's transaction: a list is small, and an entry's ORDER is part of its
    /// meaning, so a diff-and-patch would buy nothing and could leave a half-ordered list behind.
    /// </summary>
    public static WorkItemState Save(SqliteConnection connection, SqliteTransaction? transaction, WorkItemState state)
    {
        var id = state.Id.Value;

        // The steps go FIRST: they are found through their scenario, so deleting the scenarios before them
        // would orphan every step and the next write would collide with the ids still sitting there.
        Execute(
            connection,
            transaction,
            "DELETE FROM work_item_scenario_steps WHERE scenario_id IN "
            + "(SELECT id FROM work_item_scenarios WHERE item_id = $i);",
            id);

        foreach (var table in Tables)
        {
            Execute(connection, transaction, $"DELETE FROM {table} WHERE item_id = $i;", id);
        }

        var criteria = Kept(state.AcceptanceCriteria, c => c.Id, (c, newId) => c with { Id = newId });
        for (var i = 0; i < criteria.Count; i++)
        {
            Write(
                connection, transaction,
                "INSERT INTO work_item_criteria (id, item_id, ordinal, text) VALUES ($id, $i, $o, $t);",
                command =>
                {
                    command.Parameters.AddWithValue("$id", criteria[i].Id);
                    command.Parameters.AddWithValue("$i", id);
                    command.Parameters.AddWithValue("$o", i);
                    command.Parameters.AddWithValue("$t", criteria[i].Text);
                });
        }

        var readiness = Kept(state.Readiness, r => r.Id, (r, newId) => r with { Id = newId });
        for (var i = 0; i < readiness.Count; i++)
        {
            Write(
                connection, transaction,
                "INSERT INTO work_item_readiness (id, item_id, ordinal, text, checked) VALUES ($id, $i, $o, $t, $c);",
                command =>
                {
                    command.Parameters.AddWithValue("$id", readiness[i].Id);
                    command.Parameters.AddWithValue("$i", id);
                    command.Parameters.AddWithValue("$o", i);
                    command.Parameters.AddWithValue("$t", readiness[i].Text);
                    command.Parameters.AddWithValue("$c", readiness[i].Checked ? 1 : 0);
                });
        }

        var blockers = Kept(state.PersonBlockers, b => b.Id, (b, newId) => b with { Id = newId });
        foreach (var blocker in blockers)
        {
            Write(
                connection, transaction,
                "INSERT INTO work_item_person_blockers (id, item_id, person, reason, created_utc, cleared_utc) "
                + "VALUES ($id, $i, $person, $reason, $created, $cleared);",
                command =>
                {
                    command.Parameters.AddWithValue("$id", blocker.Id);
                    command.Parameters.AddWithValue("$i", id);
                    command.Parameters.AddWithValue("$person", blocker.Person);
                    command.Parameters.AddWithValue("$reason", blocker.Reason);
                    command.Parameters.AddWithValue("$created", WorkItemRowMapper.Stamp(blocker.CreatedUtc));
                    command.Parameters.AddWithValue(
                        "$cleared",
                        blocker.ClearedUtc is { } cleared ? WorkItemRowMapper.Stamp(cleared) : DBNull.Value);
                });
        }

        var refs = Kept(state.Refs, r => r.Id, (r, newId) => r with { Id = newId });
        var perRole = new Dictionary<ArtifactRefRole, int>();
        foreach (var reference in refs)
        {
            var ordinal = perRole.TryGetValue(reference.Role, out var seen) ? seen : 0;
            perRole[reference.Role] = ordinal + 1;
            Write(
                connection, transaction,
                "INSERT INTO work_item_refs (id, item_id, role, kind, target, workspace_id, label, note, version, ordinal) "
                + "VALUES ($id, $i, $role, $kind, $target, $ws, $label, $note, $version, $o);",
                command =>
                {
                    command.Parameters.AddWithValue("$id", reference.Id);
                    command.Parameters.AddWithValue("$i", id);
                    command.Parameters.AddWithValue("$role", StableNames.ToName(reference.Role));
                    command.Parameters.AddWithValue("$kind", StableNames.ToName(reference.Kind));
                    command.Parameters.AddWithValue("$target", reference.Target);
                    command.Parameters.AddWithValue("$ws", (object?)reference.WorkspaceId ?? DBNull.Value);
                    command.Parameters.AddWithValue("$label", (object?)reference.Label ?? DBNull.Value);
                    command.Parameters.AddWithValue("$note", (object?)reference.Note ?? DBNull.Value);
                    command.Parameters.AddWithValue("$version", (object?)reference.Version ?? DBNull.Value);
                    command.Parameters.AddWithValue("$o", ordinal);
                });
        }

        var patterns = Kept(state.ScopePatterns, p => p.Id, (p, newId) => p with { Id = newId });
        foreach (var pattern in patterns)
        {
            Write(
                connection, transaction,
                "INSERT INTO work_item_scope_patterns (id, item_id, mode, pattern, workspace_id) "
                + "VALUES ($id, $i, $mode, $pattern, $ws);",
                command =>
                {
                    command.Parameters.AddWithValue("$id", pattern.Id);
                    command.Parameters.AddWithValue("$i", id);
                    command.Parameters.AddWithValue("$mode", StableNames.ToName(pattern.Mode));
                    command.Parameters.AddWithValue("$pattern", pattern.Pattern);
                    command.Parameters.AddWithValue("$ws", (object?)pattern.WorkspaceId ?? DBNull.Value);
                });
        }

        var scenarios = Kept(state.Scenarios, s => s.Id, (s, newId) => s with { Id = newId });
        var written = new List<TestScenario>(scenarios.Count);
        for (var i = 0; i < scenarios.Count; i++)
        {
            var scenario = scenarios[i];
            var ordinal = i;
            Write(
                connection, transaction,
                "INSERT INTO work_item_scenarios (id, item_id, ordinal, title) VALUES ($id, $i, $o, $t);",
                command =>
                {
                    command.Parameters.AddWithValue("$id", scenario.Id);
                    command.Parameters.AddWithValue("$i", id);
                    command.Parameters.AddWithValue("$o", ordinal);
                    command.Parameters.AddWithValue("$t", scenario.Title);
                });

            var steps = Kept(scenario.Steps, s => s.Id, (s, newId) => s with { Id = newId });
            for (var stepIndex = 0; stepIndex < steps.Count; stepIndex++)
            {
                var step = steps[stepIndex];
                var stepOrdinal = stepIndex;
                Write(
                    connection, transaction,
                    "INSERT INTO work_item_scenario_steps "
                    + "(id, scenario_id, ordinal, action, expected, data_json, command) "
                    + "VALUES ($id, $s, $o, $a, $e, $d, $c);",
                    command =>
                    {
                        command.Parameters.AddWithValue("$id", step.Id);
                        command.Parameters.AddWithValue("$s", scenario.Id);
                        command.Parameters.AddWithValue("$o", stepOrdinal);
                        command.Parameters.AddWithValue("$a", step.Action);
                        command.Parameters.AddWithValue("$e", step.Expected);
                        command.Parameters.AddWithValue("$d", (object?)step.Data ?? DBNull.Value);
                        command.Parameters.AddWithValue("$c", (object?)step.Command ?? DBNull.Value);
                    });
            }

            written.Add(scenario with { Steps = steps });
        }

        return state with
        {
            AcceptanceCriteria = criteria,
            Readiness = readiness,
            PersonBlockers = blockers,
            Refs = refs,
            ScopePatterns = patterns,
            Scenarios = written,
        };
    }

    /// <summary>Removes every agent-layer row of an item, for a delete that the port does not yet offer.</summary>
    public static void Remove(SqliteConnection connection, SqliteTransaction? transaction, string itemId)
    {
        Execute(
            connection,
            transaction,
            "DELETE FROM work_item_scenario_steps WHERE scenario_id IN "
            + "(SELECT id FROM work_item_scenarios WHERE item_id = $i);",
            itemId);
        foreach (var table in Tables)
        {
            Execute(connection, transaction, $"DELETE FROM {table} WHERE item_id = $i;", itemId);
        }
    }

    private static readonly string[] Tables =
    [
        "work_item_criteria",
        "work_item_readiness",
        "work_item_person_blockers",
        "work_item_refs",
        "work_item_scope_patterns",
        "work_item_scenarios",
    ];

    /// <summary>The list with every entry's id kept when the caller passed one back, minted when not.</summary>
    private static List<T> Kept<T>(IReadOnlyList<T>? entries, Func<T, string> idOf, Func<T, string, T> withId) =>
        [.. (entries ?? []).Select(entry =>
        {
            var supplied = idOf(entry);
            return string.IsNullOrWhiteSpace(supplied) ? withId(entry, AgentLayerEntry.NewId()) : entry;
        })];

    private static List<TestScenario> LoadScenarios(
        SqliteConnection connection, SqliteTransaction? transaction, string itemId)
    {
        var scenarios = Query(
            connection, transaction, itemId,
            "SELECT id, title FROM work_item_scenarios WHERE item_id = $i ORDER BY ordinal;",
            r => new TestScenario(r.GetString(0), r.GetString(1), []));

        return [.. scenarios.Select(scenario => scenario with
        {
            Steps = Query(
                connection, transaction, scenario.Id,
                "SELECT id, action, expected, data_json, command FROM work_item_scenario_steps "
                + "WHERE scenario_id = $i ORDER BY ordinal;",
                r => new ScenarioStep(
                    r.GetString(0),
                    r.GetString(1),
                    r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4))),
        })];
    }

    private static List<T> Query<T>(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string key,
        string sql,
        Func<SqliteDataReader, T> read)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$i", key);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
        {
            rows.Add(read(reader));
        }

        return rows;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, string key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$i", key);
        command.ExecuteNonQuery();
    }

    private static void Write(
        SqliteConnection connection, SqliteTransaction? transaction, string sql, Action<SqliteCommand> bind)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        bind(command);
        command.ExecuteNonQuery();
    }

    /// <summary>A stored enum name, or a genuine fault: a name no value has means a corrupt database.</summary>
    private static TEnum Name<TEnum>(string stored, string field, string itemId)
        where TEnum : struct, Enum
    {
        var parsed = StableNames.Parse<TEnum>(stored, field);
        return parsed.IsValid
            ? parsed.Value
            : throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Work item {itemId} has an unknown stored {field} '{stored}'."));
    }
}
