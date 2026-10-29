using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fravashi.Application.Autopilot;
using Fravashi.Domain.Autopilot;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Fravashi.Infrastructure.Autopilot;

/// <summary>
/// The durable store for an autopilot run (schema step 39, #297), in the studio's own database. One row per run,
/// one row per queued item, and one row per stop, so a stop is always a stored record and never only a log line.
/// </summary>
public sealed class SqliteAutopilotRunStore(ISqliteConnectionFactory factory) : IAutopilotRunStore
{
    private static readonly JsonSerializerOptions SettingsJson = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <inheritdoc />
    public async Task<AutopilotRun> SaveAsync(AutopilotRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        using var connection = factory.Open();
        using var transaction = connection.BeginTransaction();

        using (var upsertRun = connection.CreateCommand())
        {
            upsertRun.Transaction = transaction;
            upsertRun.CommandText =
                "INSERT INTO autopilot_runs (id, project_id, settings, state, started_utc, last_progress_utc, "
                + "stop_after_item, base_branch, workspace_return_note, base_workspace_id) "
                + "VALUES ($id, $project, $settings, $state, $started, $progress, $stopafter, $base, $note, $wsid) "
                + "ON CONFLICT (id) DO UPDATE SET "
                + "project_id = $project, settings = $settings, state = $state, "
                + "started_utc = $started, last_progress_utc = $progress, stop_after_item = $stopafter, "
                + "base_branch = $base, workspace_return_note = $note, base_workspace_id = $wsid;";
            upsertRun.Parameters.AddWithValue("$id", run.Id.ToString("D"));
            upsertRun.Parameters.AddWithValue("$project", run.Project.Value);
            upsertRun.Parameters.AddWithValue("$settings", JsonSerializer.Serialize(run.Settings, SettingsJson));
            upsertRun.Parameters.AddWithValue("$state", run.State.ToString());
            upsertRun.Parameters.AddWithValue("$started", Stamp(run.StartedUtc));
            upsertRun.Parameters.AddWithValue("$progress", Stamp(run.LastProgressUtc));
            upsertRun.Parameters.AddWithValue("$stopafter", run.StopAfterCurrentItem ? 1 : 0);
            upsertRun.Parameters.AddWithValue("$base", (object?)run.BaseBranch ?? DBNull.Value);
            upsertRun.Parameters.AddWithValue("$note", (object?)run.WorkspaceReturnNote ?? DBNull.Value);
            upsertRun.Parameters.AddWithValue("$wsid", (object?)run.BaseWorkspaceId ?? DBNull.Value);
            await upsertRun.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        using (var clearItems = connection.CreateCommand())
        {
            clearItems.Transaction = transaction;
            clearItems.CommandText = "DELETE FROM autopilot_run_items WHERE run_id = $id;";
            clearItems.Parameters.AddWithValue("$id", run.Id.ToString("D"));
            await clearItems.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var item in run.Queue)
        {
            using var insertItem = connection.CreateCommand();
            insertItem.Transaction = transaction;
            insertItem.CommandText =
                "INSERT INTO autopilot_run_items "
                + "(run_id, item_id, item_order, stage, spent_usd, failed_reviews, started_utc, branch_head_sha, "
                + "parked_reason, prompt, waiting_since_utc, waiting_for_capacity_reason) "
                + "VALUES ($run, $item, $order, $stage, $spent, $failed, $started, $sha, $parked, $prompt, $waiting, $capacity);";
            insertItem.Parameters.AddWithValue("$run", run.Id.ToString("D"));
            insertItem.Parameters.AddWithValue("$item", item.Item.Value);
            insertItem.Parameters.AddWithValue("$order", item.Order);
            insertItem.Parameters.AddWithValue("$stage", item.Stage.ToString());
            insertItem.Parameters.AddWithValue("$spent", Money(item.SpentUsd));
            insertItem.Parameters.AddWithValue("$failed", item.FailedReviews);
            insertItem.Parameters.AddWithValue("$started", (object?)Stamp(item.StartedUtc) ?? DBNull.Value);
            insertItem.Parameters.AddWithValue("$sha", (object?)item.BranchHeadSha ?? DBNull.Value);
            insertItem.Parameters.AddWithValue("$parked", (object?)item.ParkedReason ?? DBNull.Value);
            insertItem.Parameters.AddWithValue("$prompt", (object?)item.Prompt ?? DBNull.Value);
            insertItem.Parameters.AddWithValue("$waiting", (object?)Stamp(item.WaitingSinceUtc) ?? DBNull.Value);
            insertItem.Parameters.AddWithValue("$capacity", (object?)item.WaitingForCapacityReason ?? DBNull.Value);
            await insertItem.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        using (var clearStops = connection.CreateCommand())
        {
            clearStops.Transaction = transaction;
            clearStops.CommandText = "DELETE FROM autopilot_run_stops WHERE run_id = $id;";
            clearStops.Parameters.AddWithValue("$id", run.Id.ToString("D"));
            await clearStops.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var stop in run.Stops)
        {
            using var insertStop = connection.CreateCommand();
            insertStop.Transaction = transaction;
            insertStop.CommandText =
                "INSERT INTO autopilot_run_stops (run_id, kind, scope, limit_name, figure, at_utc, item_id, reason) "
                + "VALUES ($run, $kind, $scope, $name, $figure, $at, $item, $reason);";
            insertStop.Parameters.AddWithValue("$run", run.Id.ToString("D"));
            insertStop.Parameters.AddWithValue("$kind", stop.Kind.ToString());
            insertStop.Parameters.AddWithValue("$scope", stop.Scope.ToString());
            insertStop.Parameters.AddWithValue("$name", stop.LimitName);
            insertStop.Parameters.AddWithValue("$figure", stop.Figure);
            insertStop.Parameters.AddWithValue("$at", Stamp(stop.AtUtc));
            insertStop.Parameters.AddWithValue("$item", (object?)stop.Item?.Value ?? DBNull.Value);
            insertStop.Parameters.AddWithValue("$reason", (object?)stop.Reason ?? DBNull.Value);
            await insertStop.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return run;
    }

    /// <inheritdoc />
    public async Task<AutopilotRun?> GetAsync(Guid id, CancellationToken ct = default)
    {
        using var connection = factory.Open();

        AutopilotDispatchSettings settings;
        AutopilotRunState state;
        ProjectId project;
        DateTimeOffset started;
        DateTimeOffset progress;
        bool stopAfterItem;
        string? baseBranch;
        string? workspaceReturnNote;
        string? baseWorkspaceId;

        using (var readRun = connection.CreateCommand())
        {
            readRun.CommandText =
                "SELECT project_id, settings, state, started_utc, last_progress_utc, stop_after_item, "
                + "base_branch, workspace_return_note, base_workspace_id FROM autopilot_runs WHERE id = $id;";
            readRun.Parameters.AddWithValue("$id", id.ToString("D"));
            using var reader = await readRun.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            project = new ProjectId(reader.GetString(0));

            // The limits ARE the run's safety boundary: a run that shipped with a tighter ceiling than the
            // shipped defaults must never read back with the defaults instead, so unreadable settings throw
            // rather than silently substitute (CLAUDE.md's two-stores rule 4, "nothing is silently resolved").
            settings = JsonSerializer.Deserialize<AutopilotDispatchSettings>(reader.GetString(1), SettingsJson)
                ?? throw new InvalidOperationException(
                    $"The run {id:D} has unreadable dispatch settings. Its limits cannot be assumed to be the "
                        + "shipped defaults, so this is refused rather than silently substituted.");
            state = Enum.Parse<AutopilotRunState>(reader.GetString(2));
            started = Parse(reader.GetString(3));
            progress = Parse(reader.GetString(4));
            stopAfterItem = reader.GetInt64(5) != 0;
            baseBranch = reader.IsDBNull(6) ? null : reader.GetString(6);
            workspaceReturnNote = reader.IsDBNull(7) ? null : reader.GetString(7);
            baseWorkspaceId = reader.IsDBNull(8) ? null : reader.GetString(8);
        }

        var queue = await ReadItemsAsync(connection, id, ct).ConfigureAwait(false);
        var stops = await ReadStopsAsync(connection, id, ct).ConfigureAwait(false);

        return new AutopilotRun(
            id, project, settings, queue, state, started, progress, stops, stopAfterItem, baseBranch,
            workspaceReturnNote, baseWorkspaceId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AutopilotRun>> OpenRunsAsync(CancellationToken ct = default)
    {
        using var connection = factory.Open();
        using var readIds = connection.CreateCommand();
        readIds.CommandText =
            "SELECT id FROM autopilot_runs WHERE state = $running;";
        readIds.Parameters.AddWithValue("$running", AutopilotRunState.Running.ToString());

        // The id column itself is not inside the per-row isolation below: nothing but this store ever writes a
        // row here, so a primary key that does not parse as a Guid is a different and more severe problem
        // (the table itself is corrupted) than one run's settings failing to read, and is not guessed past.
        var ids = new List<Guid>();
        using (var reader = await readIds.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                ids.Add(Guid.Parse(reader.GetString(0)));
            }
        }

        var runs = new List<AutopilotRun>(ids.Count);
        foreach (var id in ids)
        {
            AutopilotRun? run;
            try
            {
                run = await GetAsync(id, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException or FormatException or ArgumentException)
            {
                // One run's unreadable row (null settings, malformed JSON, an enum name this build does not
                // know, a bad state, a bad timestamp) must not stop every OTHER run from resuming, and must
                // not leave this one Running forever either (every later start would hit the same throw):
                // stop it with a CannotResume row, directly, without needing the very data that cannot be
                // read. This is the store's own catch set, wider than SqliteMirrorStore's (lines 149 and 420
                // there catch JsonException only, the one failure its own opaque payload read can have).
                // Deliberately NOT caught here: SqliteException, because a transient read failure (a locked
                // database) is not evidence the row itself is bad, and permanently stopping the run over it
                // would be its own kind of wrong answer; it propagates so the next start simply tries again.
                await MarkCannotResumeAsync(connection, id, ex, ct).ConfigureAwait(false);
                continue;
            }

            if (run is { } opened)
            {
                runs.Add(opened);
            }
        }

        return runs;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AutopilotRun>> ForProjectAsync(ProjectId project, CancellationToken ct = default)
    {
        using var connection = factory.Open();
        using var readIds = connection.CreateCommand();
        readIds.CommandText =
            "SELECT id FROM autopilot_runs WHERE project_id = $project ORDER BY started_utc DESC;";
        readIds.Parameters.AddWithValue("$project", project.Value);

        var ids = new List<Guid>();
        using (var reader = await readIds.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                ids.Add(Guid.Parse(reader.GetString(0)));
            }
        }

        var runs = new List<AutopilotRun>(ids.Count);
        foreach (var id in ids)
        {
            try
            {
                if (await GetAsync(id, ct).ConfigureAwait(false) is { } run)
                {
                    runs.Add(run);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException or FormatException or ArgumentException)
            {
                // This is a display read for a person's history, never the resume path OpenRunsAsync is: a
                // row this cannot make sense of is simply left out, rather than mutated (unlike
                // OpenRunsAsync's own MarkCannotResumeAsync) or allowed to fail every other run's history too.
            }
        }

        return runs;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AutopilotRun>> LatestPerProjectAsync(CancellationToken ct = default)
    {
        using var connection = factory.Open();
        using var readIds = connection.CreateCommand();

        // Ordered newest first per project, so the first row this reads for each project_id is its latest
        // run: simpler and just as correct as a MAX(started_utc) aggregate, and it is what ForProjectAsync's
        // own query already orders by.
        readIds.CommandText = "SELECT id, project_id FROM autopilot_runs ORDER BY project_id, started_utc DESC;";

        var latestIdByProject = new Dictionary<string, Guid>();
        using (var reader = await readIds.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var project = reader.GetString(1);
                if (!latestIdByProject.ContainsKey(project))
                {
                    latestIdByProject[project] = Guid.Parse(reader.GetString(0));
                }
            }
        }

        var runs = new List<AutopilotRun>(latestIdByProject.Count);
        foreach (var id in latestIdByProject.Values)
        {
            try
            {
                if (await GetAsync(id, ct).ConfigureAwait(false) is { } run)
                {
                    runs.Add(run);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException or FormatException or ArgumentException)
            {
                // Same display-read posture as ForProjectAsync: a row this cannot make sense of is left out
                // rather than failing every other project's own latest run.
            }
        }

        return runs;
    }

    private static async Task MarkCannotResumeAsync(SqliteConnection connection, Guid id, Exception cause, CancellationToken ct)
    {
        using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow;

        using (var markStopped = connection.CreateCommand())
        {
            markStopped.Transaction = transaction;
            markStopped.CommandText = "UPDATE autopilot_runs SET state = $state WHERE id = $id;";
            markStopped.Parameters.AddWithValue("$state", AutopilotRunState.Stopped.ToString());
            markStopped.Parameters.AddWithValue("$id", id.ToString("D"));
            await markStopped.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        using (var insertStop = connection.CreateCommand())
        {
            insertStop.Transaction = transaction;
            insertStop.CommandText =
                "INSERT INTO autopilot_run_stops (run_id, kind, scope, limit_name, figure, at_utc, item_id, reason) "
                + "VALUES ($run, $kind, $scope, $name, $figure, $at, NULL, $reason);";
            insertStop.Parameters.AddWithValue("$run", id.ToString("D"));
            insertStop.Parameters.AddWithValue("$kind", AutopilotStopKind.CannotResume.ToString());
            insertStop.Parameters.AddWithValue("$scope", AutopilotStopScope.Run.ToString());
            insertStop.Parameters.AddWithValue("$name", "resume");
            // Named from the exception actually caught, never a fixed "unreadable dispatch settings": the
            // catch set also covers a bad state, a bad stage, a bad stop enum and a bad timestamp, and
            // claiming the settings specifically would be false for every one of those.
            insertStop.Parameters.AddWithValue("$figure", $"{cause.GetType().Name}: {cause.Message}");
            insertStop.Parameters.AddWithValue("$at", Stamp(now));
            insertStop.Parameters.AddWithValue(
                "$reason",
                "This run's stored row could not be read, and its state cannot be assumed to be safe to resume "
                    + "from, so it stops rather than guess.");
            await insertStop.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<AutopilotRunItem>> ReadItemsAsync(
        SqliteConnection connection, Guid runId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT item_id, item_order, stage, spent_usd, failed_reviews, started_utc, branch_head_sha, "
            + "parked_reason, prompt, waiting_since_utc, waiting_for_capacity_reason FROM autopilot_run_items "
            + "WHERE run_id = $run ORDER BY item_order;";
        command.Parameters.AddWithValue("$run", runId.ToString("D"));

        var items = new List<AutopilotRunItem>();
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(new AutopilotRunItem(
                new WorkItemId(reader.GetString(0)),
                reader.GetInt32(1),
                Enum.Parse<AutopilotItemStage>(reader.GetString(2)),
                decimal.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                reader.GetInt32(4),
                reader.IsDBNull(5) ? null : Parse(reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : Parse(reader.GetString(9)),
                reader.IsDBNull(10) ? null : reader.GetString(10)));
        }

        return items;
    }

    private static async Task<IReadOnlyList<AutopilotStop>> ReadStopsAsync(
        SqliteConnection connection, Guid runId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT kind, scope, limit_name, figure, at_utc, item_id, reason "
            + "FROM autopilot_run_stops WHERE run_id = $run ORDER BY id;";
        command.Parameters.AddWithValue("$run", runId.ToString("D"));

        var stops = new List<AutopilotStop>();
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            stops.Add(new AutopilotStop(
                Enum.Parse<AutopilotStopKind>(reader.GetString(0)),
                Enum.Parse<AutopilotStopScope>(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                Parse(reader.GetString(4)),
                reader.IsDBNull(5) ? null : new WorkItemId(reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return stops;
    }

    private static string Money(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static string? Stamp(DateTimeOffset? at) => at?.ToString("O", CultureInfo.InvariantCulture);

    private static string Stamp(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
