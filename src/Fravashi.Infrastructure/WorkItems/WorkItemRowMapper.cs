using System.Globalization;
using System.Text.Json.Nodes;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;
using Microsoft.Data.Sqlite;

namespace Fravashi.Infrastructure.WorkItems;

/// <summary>
/// The mapping between a <c>work_items</c> row and the aggregate (schema v11, canonical artifact model plan
/// section 5.1). A row is read into a <see cref="WorkItemState"/> and built with <see cref="WorkItem.Restore"/>,
/// so a stored item is always held to the item-local invariants on the way out, and never to the authoring
/// rules (a mirrored shape reads back as it was written). An unknown stored name or a row that breaks an
/// invariant is a corrupt database, a genuine fault, and throws.
/// </summary>
internal static class WorkItemRowMapper
{
    /// <summary>The columns every item read selects, in the order <see cref="Read"/> expects.</summary>
    public const string Columns =
        "id, project_id, number, level, kind, title, description, parent_id, status, resolution, " +
        "blocked, blocked_reason, deferred, deferred_reason, marked_for_autopilot, priority, assignee, rank, spike_timebox_minutes, " +
        "external_connection, external_key, external_status, version, created_utc, updated_utc, "
        + AgentLayerRows.ScalarColumns + ", suggested_model, suggested_effort";

    /// <summary>
    /// What a READ selects: <see cref="Columns"/> and then the columns the board never writes through an insert or an
    /// update. <c>released_in</c> (schema step 47, work item #368) is stamped only by the release store, so leaving it
    /// out of <see cref="Columns"/> is what keeps an ordinary update from ever clearing it.
    /// </summary>
    public const string ReadColumns = Columns + ", released_in";

    /// <summary>The same columns qualified with a table alias.</summary>
    public static string ColumnsOf(string alias) =>
        string.Join(", ", Columns.Split(", ").Select(c => alias + "." + c));

    /// <summary><see cref="ReadColumns"/> qualified with a table alias.</summary>
    public static string ReadColumnsOf(string alias) =>
        string.Join(", ", ReadColumns.Split(", ").Select(c => alias + "." + c));

    /// <summary>Reads the current row, selected with <see cref="ReadColumns"/>.</summary>
    public static WorkItem Read(SqliteDataReader reader)
    {
        var id = reader.GetString(0);
        var state = new WorkItemState
        {
            Id = new WorkItemId(id),
            Project = new ProjectId(reader.GetString(1)),
            Number = checked((int)reader.GetInt64(2)),
            Level = Parse<WorkItemLevel>(reader.GetString(3), "level", id),
            Kind = Parse<WorkItemKind>(reader.GetString(4), "kind", id),
            Title = reader.GetString(5),
            Description = reader.GetString(6),
            Parent = reader.IsDBNull(7) ? null : new WorkItemId(reader.GetString(7)),
            Status = Parse<WorkItemStatus>(reader.GetString(8), "status", id),
            Resolution = reader.IsDBNull(9) ? null : Parse<WorkItemResolution>(reader.GetString(9), "resolution", id),
            Flags = new WorkItemFlags(
                reader.GetInt64(10) != 0,
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.GetInt64(12) != 0,
                reader.IsDBNull(13) ? null : reader.GetString(13)),
            MarkedForAutopilot = reader.GetInt64(14) != 0,
            Priority = reader.IsDBNull(15) ? null : Parse<WorkItemPriority>(reader.GetString(15), "priority", id),
            Assignee = reader.IsDBNull(16) ? null : reader.GetString(16),
            Rank = reader.GetDouble(17),
            SpikeTimebox = reader.IsDBNull(18) ? null : TimeSpan.FromMinutes(reader.GetInt64(18)),
            External = reader.IsDBNull(20)
                ? null
                : new ExternalReference(
                    reader.IsDBNull(19) ? string.Empty : reader.GetString(19),
                    reader.GetString(20),
                    reader.IsDBNull(21) ? null : reader.GetString(21)),
            Version = reader.GetInt64(22),
            CreatedUtc = ParseStamp(reader.GetString(23)),
            UpdatedUtc = ParseStamp(reader.GetString(24)),
        };

        // The agent layer's per-item values ride in the same row (schema step 13); its LISTS are loaded
        // separately, because a join would multiply the row out and a reader cannot be re-entered mid-read.
        state = AgentLayerRows.ReadScalars(state, reader, 25);

        // Schema step 45 (requested 2026-10-22, model and effort per work item): the two suggestions, after the
        // agent layer's five scalars. Null is Default; an unknown stored name is a corrupt row like any other.
        state = state with
        {
            SuggestedModel = reader.IsDBNull(30) ? null : Parse<AgentModelAlias>(reader.GetString(30), "suggested_model", id),
            SuggestedEffort = reader.IsDBNull(31) ? null : Parse<AgentEffort>(reader.GetString(31), "suggested_effort", id),

            // Schema step 47 (work item #368): the release that took the item, read-only here.
            ReleasedIn = reader.IsDBNull(32) ? null : reader.GetString(32),
        };

        var restored = WorkItem.Restore(state);
        return restored.IsValid
            ? restored.Value
            : throw new InvalidDataException(
                $"Stored work item {id} breaks a model invariant: " + string.Join("; ", restored.Errors.Select(e => e.Code)));
    }

    /// <summary>Binds every column of <paramref name="state"/> as <c>$</c>-prefixed parameters named like the columns.</summary>
    public static void Bind(SqliteCommand command, WorkItemState state)
    {
        var flags = state.Flags ?? WorkItemFlags.None;
        command.Parameters.AddWithValue("$id", state.Id.Value);
        command.Parameters.AddWithValue("$project_id", state.Project.Value);
        command.Parameters.AddWithValue("$number", state.Number);
        command.Parameters.AddWithValue("$level", StableNames.ToName(state.Level));
        command.Parameters.AddWithValue("$kind", StableNames.ToName(state.Kind));
        command.Parameters.AddWithValue("$title", state.Title);
        command.Parameters.AddWithValue("$description", state.Description ?? string.Empty);
        command.Parameters.AddWithValue("$parent_id", (object?)state.Parent?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", StableNames.ToName(state.Status));
        command.Parameters.AddWithValue("$resolution", state.Resolution is { } r ? (object)StableNames.ToName(r) : DBNull.Value);
        command.Parameters.AddWithValue("$blocked", flags.Blocked ? 1 : 0);
        command.Parameters.AddWithValue("$blocked_reason", (object?)flags.BlockedReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$deferred", flags.Deferred ? 1 : 0);
        command.Parameters.AddWithValue("$deferred_reason", (object?)flags.DeferredReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$marked_for_autopilot", state.MarkedForAutopilot ? 1 : 0);
        command.Parameters.AddWithValue("$priority", state.Priority is { } p ? (object)StableNames.ToName(p) : DBNull.Value);
        command.Parameters.AddWithValue("$assignee", (object?)state.Assignee ?? DBNull.Value);
        command.Parameters.AddWithValue("$rank", state.Rank);
        command.Parameters.AddWithValue("$spike_timebox_minutes", state.SpikeTimebox is { } t ? (object)checked((long)t.TotalMinutes) : DBNull.Value);
        command.Parameters.AddWithValue("$external_connection", (object?)state.External?.Connection ?? DBNull.Value);
        command.Parameters.AddWithValue("$external_key", (object?)state.External?.Key ?? DBNull.Value);
        command.Parameters.AddWithValue("$external_status", (object?)state.External?.NativeStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("$version", state.Version);
        command.Parameters.AddWithValue("$created_utc", Stamp(state.CreatedUtc));
        command.Parameters.AddWithValue("$updated_utc", Stamp(state.UpdatedUtc));
        AgentLayerRows.BindScalars(command, state);
        command.Parameters.AddWithValue(
            "$suggested_model", state.SuggestedModel is { } model ? (object)StableNames.ToName(model) : DBNull.Value);
        command.Parameters.AddWithValue(
            "$suggested_effort", state.SuggestedEffort is { } effort ? (object)StableNames.ToName(effort) : DBNull.Value);
    }

    /// <summary>The <c>VALUES</c> list matching <see cref="Columns"/> for <see cref="Bind"/>.</summary>
    public static string Parameters => string.Join(", ", Columns.Split(", ").Select(c => "$" + c));

    /// <summary>
    /// The fields an event diff covers, rendered as JSON values: what a person or an agent can change through the
    /// port. Identity, version and timestamps are not diffed; the event row carries the version.
    /// </summary>
    public static Dictionary<string, JsonNode?> Snapshot(WorkItemState state)
    {
        var flags = state.Flags ?? WorkItemFlags.None;
        return new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            ["level"] = StableNames.ToName(state.Level),
            ["kind"] = StableNames.ToName(state.Kind),
            ["title"] = state.Title,
            ["description"] = state.Description,
            ["parent"] = state.Parent?.Value,
            ["status"] = StableNames.ToName(state.Status),
            ["resolution"] = state.Resolution is { } r ? StableNames.ToName(r) : null,
            ["blocked"] = flags.Blocked,
            ["blocked_reason"] = flags.BlockedReason,
            ["deferred"] = flags.Deferred,
            ["deferred_reason"] = flags.DeferredReason,
            ["marked_for_autopilot"] = state.MarkedForAutopilot,
            ["priority"] = state.Priority is { } p ? StableNames.ToName(p) : null,
            ["assignee"] = state.Assignee,
            ["rank"] = state.Rank,
            ["spike_timebox_minutes"] = state.SpikeTimebox is { } t ? JsonValue.Create((long)t.TotalMinutes) : null,

            // The agent layer, rendered as text rather than structure. The diff only has to ANSWER whether the
            // field changed (a patch that edits nothing must write nothing, and one that edits only the
            // acceptance list must write something); what exactly moved inside a list is the list's own rows.
            ["ambiguity"] = state.Ambiguity is { } a
                ? string.Create(CultureInfo.InvariantCulture, $"{a.Score}|{a.AssessedBy}|{a.AssessedUtc:O}")
                : null,
            ["change_budget_max_files"] = (state.ChangeBudget ?? ChangeBudget.None).MaxFiles,
            ["next_action"] = state.NextAction,
            ["suggested_model"] = state.SuggestedModel is { } model ? StableNames.ToName(model) : null,
            ["suggested_effort"] = state.SuggestedEffort is { } effort ? StableNames.ToName(effort) : null,
            ["acceptance_criteria"] = Rendered(state.AcceptanceCriteria, c => $"{c.Id}:{c.Text}"),
            ["readiness"] = Rendered(state.Readiness, r => $"{r.Id}:{r.Checked}:{r.Text}"),
            ["person_blockers"] = Rendered(
                state.PersonBlockers, b => $"{b.Id}:{b.Person}:{b.Reason}:{b.ClearedUtc:O}"),
            ["refs"] = Rendered(
                state.Refs,
                r => $"{r.Id}:{StableNames.ToName(r.Role)}:{StableNames.ToName(r.Kind)}:{r.Target}:{r.WorkspaceId}:{r.Label}:{r.Note}"),
            ["scope_patterns"] = Rendered(
                state.ScopePatterns, p => $"{p.Id}:{StableNames.ToName(p.Mode)}:{p.Pattern}:{p.WorkspaceId}"),
            ["scenarios"] = Rendered(
                state.Scenarios,
                s => s.Id + ":" + s.Title + ":" + string.Join(
                    "|", s.Steps.Select(step => $"{step.Id}/{step.Action}/{step.Expected}/{step.Data}"))),
        };
    }

    /// <summary>A list as one line, or null when it is empty, so an empty list and an absent one read alike.</summary>
    private static JsonNode? Rendered<T>(IReadOnlyList<T>? entries, Func<T, string> render) =>
        entries is { Count: > 0 } ? JsonValue.Create(string.Join("\n", entries.Select(render))) : null;

    /// <summary>
    /// <c>{field: {from, to}}</c> over the fields that differ between two snapshots; a null
    /// <paramref name="before"/> is a creation, where every field that has a value is recorded from null.
    /// </summary>
    public static JsonObject Diff(WorkItemState? before, WorkItemState after)
    {
        var from = before is null ? null : Snapshot(before);
        var to = Snapshot(after);
        var changes = new JsonObject();
        foreach (var (field, value) in to)
        {
            var previous = from is null ? null : from[field];
            var previousText = previous?.ToJsonString() ?? "null";
            var valueText = value?.ToJsonString() ?? "null";
            if (from is null ? value is null || valueText is "false" : string.Equals(previousText, valueText, StringComparison.Ordinal))
            {
                continue;
            }

            changes[field] = new JsonObject
            {
                ["from"] = previous?.DeepClone(),
                ["to"] = value?.DeepClone(),
            };
        }

        return changes;
    }

    /// <summary>A timestamp as round-trip text.</summary>
    public static string Stamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>A round-trip timestamp.</summary>
    public static DateTimeOffset ParseStamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static TEnum Parse<TEnum>(string name, string field, string id)
        where TEnum : struct, Enum
    {
        var parsed = StableNames.Parse<TEnum>(name, field);
        return parsed.IsValid
            ? parsed.Value
            : throw new InvalidDataException($"Stored work item {id} has an unknown {field} '{name}'.");
    }
}
