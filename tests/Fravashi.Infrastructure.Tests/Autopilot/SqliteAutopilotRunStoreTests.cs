using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Fravashi.Domain.Autopilot;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Autopilot;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Tests.Persistence;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Autopilot;

/// <summary>
/// The durable half of #297 against a real file-backed database with the production schema (step 39): a run
/// survives a restart only if what comes back out is exactly what was saved.
/// </summary>
public sealed class SqliteAutopilotRunStoreTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);
    private static readonly WorkItemId Item1 = new("item-1");
    private static readonly WorkItemId Item2 = new("item-2");

    private readonly SqliteTestContext _context = new();
    private readonly SqliteAutopilotRunStore _store;

    public SqliteAutopilotRunStoreTests()
    {
        _store = new SqliteAutopilotRunStore(_context.Factory);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private static AutopilotRun SampleRun(Guid id) =>
        new(
            id,
            ProjectId.Default,
            // A cap above the default 1 on purpose (#308): it decides whether a resumed run shares one workspace
            // or gives each item a worktree, so a store that dropped it would flip a run's mode on restart and
            // a sample at the default could never show that.
            AutopilotDispatchSettings.Default with
            {
                Limits = AutopilotLimits.Default with { MoneyPerItemUsd = 12.5m },
                EffectiveConcurrencyCap = 2,
            },
            [
                new AutopilotRunItem(Item1, 0, AutopilotItemStage.Reviewing, 3.40m, 1, Start, "sha-1", null, "the prompt"),
                AutopilotRunItem.Queue(Item2, 1),
            ],
            AutopilotRunState.Running,
            Start,
            Start.AddMinutes(5),
            [new AutopilotStop(AutopilotStopKind.FailedCommitReviews, AutopilotStopScope.Item, "failed commit reviews per item", "1 failed review(s), limit 3", Start.AddMinutes(4), Item1, "a note")]);

    [Fact]
    public async Task A_saved_run_reads_back_exactly_as_it_was_saved()
    {
        var run = SampleRun(Guid.NewGuid());

        await _store.SaveAsync(run);
        var reread = await _store.GetAsync(run.Id);

        // BeEquivalentTo, not Be: the record's Queue and Stops are IReadOnlyList<T>, and the default record
        // equality the compiler generates compares a list property by reference, not by its elements.
        reread.Should().BeEquivalentTo(run);
    }

    [Fact]
    public async Task Saving_a_run_again_replaces_its_queue_and_its_stops_rather_than_accumulating_them()
    {
        var run = SampleRun(Guid.NewGuid());
        await _store.SaveAsync(run);

        var advanced = run with
        {
            Queue = [run.Queue[0] with { Stage = AutopilotItemStage.Done }, run.Queue[1]],
            Stops = [],
        };
        await _store.SaveAsync(advanced);

        var reread = await _store.GetAsync(run.Id);
        reread!.Queue.Should().HaveCount(2);
        reread.Queue[0].Stage.Should().Be(AutopilotItemStage.Done);
        reread.Stops.Should().BeEmpty();
    }

    [Fact]
    public async Task A_run_that_was_never_saved_reads_as_null()
    {
        (await _store.GetAsync(Guid.NewGuid())).Should().BeNull();
    }

    /// <summary>
    /// #303's history: every run for a project, open or stopped alike, newest started first, and never
    /// another project's run.
    /// </summary>
    [Fact]
    public async Task For_project_returns_every_run_for_that_project_newest_first_open_or_finished_alike()
    {
        var older = SampleRun(Guid.NewGuid());
        var newer = SampleRun(Guid.NewGuid()) with { StartedUtc = Start.AddHours(1), State = AutopilotRunState.Stopped };
        var otherProject = SampleRun(Guid.NewGuid()) with { Project = new ProjectId("other") };

        await _store.SaveAsync(older);
        await _store.SaveAsync(newer);
        await _store.SaveAsync(otherProject);

        var history = await _store.ForProjectAsync(ProjectId.Default);

        history.Select(r => r.Id).Should().Equal(newer.Id, older.Id);
    }

    [Fact]
    public async Task Stop_after_current_item_round_trips_true_as_well_as_the_sample_runs_default_false()
    {
        var run = SampleRun(Guid.NewGuid()) with { StopAfterCurrentItem = true };

        await _store.SaveAsync(run);
        var reread = await _store.GetAsync(run.Id);

        reread!.StopAfterCurrentItem.Should().BeTrue();
    }

    /// <summary>
    /// Backlog 310: the base branch, the base workspace id and the workspace-return note round-trip,
    /// including all three null.
    /// </summary>
    [Fact]
    public async Task Base_branch_base_workspace_id_and_workspace_return_note_round_trip()
    {
        var run = SampleRun(Guid.NewGuid()) with
        {
            BaseBranch = "main",
            BaseWorkspaceId = "w1",
            WorkspaceReturnNote = "left on fravashi/item-1-abc, uncommitted changes",
        };

        await _store.SaveAsync(run);
        var reread = await _store.GetAsync(run.Id);

        reread!.BaseBranch.Should().Be("main");
        reread.BaseWorkspaceId.Should().Be("w1");
        reread.WorkspaceReturnNote.Should().Be("left on fravashi/item-1-abc, uncommitted changes");
    }

    [Fact]
    public async Task Base_branch_base_workspace_id_and_workspace_return_note_default_to_null()
    {
        var run = SampleRun(Guid.NewGuid());

        await _store.SaveAsync(run);
        var reread = await _store.GetAsync(run.Id);

        reread!.BaseBranch.Should().BeNull();
        reread.BaseWorkspaceId.Should().BeNull();
        reread.WorkspaceReturnNote.Should().BeNull();
    }

    /// <summary>Backlog 305: an item's WaitingSinceUtc round-trips, including null (every item that never asked).</summary>
    [Fact]
    public async Task Waiting_since_utc_round_trips_including_null()
    {
        var run = SampleRun(Guid.NewGuid()) with
        {
            Queue =
            [
                new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 1m, 0, Start, null, null, "the prompt", Start.AddMinutes(5)),
                AutopilotRunItem.Queue(Item2, 1),
            ],
        };

        await _store.SaveAsync(run);
        var reread = await _store.GetAsync(run.Id);

        reread!.Queue[0].WaitingSinceUtc.Should().Be(Start.AddMinutes(5));
        reread.Queue[1].WaitingSinceUtc.Should().BeNull();
    }

    /// <summary>
    /// Backlog 308, ADR 0031 decision 16: an item's WaitingForCapacityReason round-trips, including null
    /// (every item that was never refused), so the autopilot still shows the reason after a reload, not only
    /// within the same in-memory tick.
    /// </summary>
    [Fact]
    public async Task Waiting_for_capacity_reason_round_trips_including_null()
    {
        var run = SampleRun(Guid.NewGuid()) with
        {
            Queue =
            [
                new AutopilotRunItem(Item1, 0, AutopilotItemStage.Building, 1m, 0, Start, null, null, "the prompt")
                {
                    WaitingForCapacityReason = "The concurrency cap is reached: 1 of 1 session(s) are already running against this repository.",
                },
                AutopilotRunItem.Queue(Item2, 1),
            ],
        };

        await _store.SaveAsync(run);
        var reread = await _store.GetAsync(run.Id);

        reread!.Queue[0].WaitingForCapacityReason.Should().Be(
            "The concurrency cap is reached: 1 of 1 session(s) are already running against this repository.");
        reread.Queue[1].WaitingForCapacityReason.Should().BeNull();
    }

    /// <summary>
    /// A run row written before step 41 (the autopilot_runs table has carried no stop_after_item column since
    /// step 39, and step 40 did not touch it either) migrates forward and reads back false, never null or an
    /// error, the same way #298's marked_for_autopilot column does for a historical work item.
    /// </summary>
    [Fact]
    public async Task A_run_saved_before_stop_after_item_existed_migrates_to_v41_and_reads_back_false()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fravashi-autopilot-v40-migration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var factory = new SqliteConnectionFactory(new SqliteStoreOptions(Path.Combine(directory, "test.db"), Pooling: false));
        var runId = Guid.NewGuid();

        try
        {
            using (var connection = factory.Open())
            {
                using var create = connection.CreateCommand();
                create.CommandText = """
                    CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                    INSERT INTO settings (key, value) VALUES ('__schema_version', '40');

                    CREATE TABLE autopilot_runs (
                        id                TEXT PRIMARY KEY,
                        project_id        TEXT NOT NULL,
                        settings          TEXT NOT NULL,
                        state             TEXT NOT NULL,
                        started_utc       TEXT NOT NULL,
                        last_progress_utc TEXT NOT NULL
                    );

                    CREATE TABLE autopilot_run_items (
                        run_id          TEXT NOT NULL,
                        item_id         TEXT NOT NULL,
                        item_order      INTEGER NOT NULL,
                        stage           TEXT NOT NULL,
                        spent_usd       TEXT NOT NULL,
                        failed_reviews  INTEGER NOT NULL,
                        started_utc     TEXT,
                        branch_head_sha TEXT,
                        parked_reason   TEXT,
                        prompt          TEXT,
                        PRIMARY KEY (run_id, item_order)
                    );

                    CREATE TABLE autopilot_run_stops (
                        id         INTEGER PRIMARY KEY AUTOINCREMENT,
                        run_id     TEXT NOT NULL,
                        kind       TEXT NOT NULL,
                        scope      TEXT NOT NULL,
                        limit_name TEXT NOT NULL,
                        figure     TEXT NOT NULL,
                        at_utc     TEXT NOT NULL,
                        item_id    TEXT,
                        reason     TEXT
                    );
                    """;
                create.ExecuteNonQuery();

                var settingsJson = JsonSerializer.Serialize(
                    AutopilotDispatchSettings.Default, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });
                using var insert = connection.CreateCommand();
                insert.CommandText =
                    "INSERT INTO autopilot_runs (id, project_id, settings, state, started_utc, last_progress_utc) "
                    + "VALUES ($id, 'default', $settings, 'Running', $at, $at);";
                insert.Parameters.AddWithValue("$id", runId.ToString("D"));
                insert.Parameters.AddWithValue("$settings", settingsJson);
                insert.Parameters.AddWithValue("$at", Start.ToString("O"));
                insert.ExecuteNonQuery();
            }

            SqliteSchema.EnsureCreated(factory);

            using (var connection = factory.Open())
            {
                SchemaMigrator.StoredVersion(connection).Should().Be(SqliteSchema.CurrentVersion);
            }

            var migrated = await new SqliteAutopilotRunStore(factory).GetAsync(runId);
            migrated!.StopAfterCurrentItem.Should().BeFalse();
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// A run row written before step 42 (carrying stop_after_item but none of base_branch,
    /// workspace_return_note or base_workspace_id) migrates forward and reads all three new columns back as
    /// null, the same shape the v40-to-v41 migration test above proves for stop_after_item.
    /// </summary>
    [Fact]
    public async Task A_run_saved_before_workspace_return_existed_migrates_to_v42_and_reads_both_columns_as_null()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fravashi-autopilot-v41-migration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var factory = new SqliteConnectionFactory(new SqliteStoreOptions(Path.Combine(directory, "test.db"), Pooling: false));
        var runId = Guid.NewGuid();

        try
        {
            using (var connection = factory.Open())
            {
                using var create = connection.CreateCommand();
                create.CommandText = """
                    CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                    INSERT INTO settings (key, value) VALUES ('__schema_version', '41');

                    CREATE TABLE autopilot_runs (
                        id                TEXT PRIMARY KEY,
                        project_id        TEXT NOT NULL,
                        settings          TEXT NOT NULL,
                        state             TEXT NOT NULL,
                        started_utc       TEXT NOT NULL,
                        last_progress_utc TEXT NOT NULL,
                        stop_after_item   INTEGER NOT NULL DEFAULT 0
                    );

                    CREATE TABLE autopilot_run_items (
                        run_id          TEXT NOT NULL,
                        item_id         TEXT NOT NULL,
                        item_order      INTEGER NOT NULL,
                        stage           TEXT NOT NULL,
                        spent_usd       TEXT NOT NULL,
                        failed_reviews  INTEGER NOT NULL,
                        started_utc     TEXT,
                        branch_head_sha TEXT,
                        parked_reason   TEXT,
                        prompt          TEXT,
                        PRIMARY KEY (run_id, item_order)
                    );

                    CREATE TABLE autopilot_run_stops (
                        id         INTEGER PRIMARY KEY AUTOINCREMENT,
                        run_id     TEXT NOT NULL,
                        kind       TEXT NOT NULL,
                        scope      TEXT NOT NULL,
                        limit_name TEXT NOT NULL,
                        figure     TEXT NOT NULL,
                        at_utc     TEXT NOT NULL,
                        item_id    TEXT,
                        reason     TEXT
                    );
                    """;
                create.ExecuteNonQuery();

                var settingsJson = JsonSerializer.Serialize(
                    AutopilotDispatchSettings.Default, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });
                using var insert = connection.CreateCommand();
                insert.CommandText =
                    "INSERT INTO autopilot_runs (id, project_id, settings, state, started_utc, last_progress_utc) "
                    + "VALUES ($id, 'default', $settings, 'Running', $at, $at);";
                insert.Parameters.AddWithValue("$id", runId.ToString("D"));
                insert.Parameters.AddWithValue("$settings", settingsJson);
                insert.Parameters.AddWithValue("$at", Start.ToString("O"));
                insert.ExecuteNonQuery();
            }

            SqliteSchema.EnsureCreated(factory);

            using (var connection = factory.Open())
            {
                SchemaMigrator.StoredVersion(connection).Should().Be(SqliteSchema.CurrentVersion);
            }

            var migrated = await new SqliteAutopilotRunStore(factory).GetAsync(runId);
            migrated!.BaseBranch.Should().BeNull();
            migrated.BaseWorkspaceId.Should().BeNull();
            migrated.WorkspaceReturnNote.Should().BeNull();
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// A run row written before step 43 (carrying every column through base_workspace_id, but no
    /// waiting_since_utc on its items) migrates forward and reads that column back as null, the same shape
    /// the v41-to-v42 migration test above proves for base_branch, workspace_return_note and base_workspace_id.
    /// </summary>
    [Fact]
    public async Task A_run_saved_before_person_wait_existed_migrates_to_v43_and_reads_waiting_since_utc_as_null()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fravashi-autopilot-v42-migration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var factory = new SqliteConnectionFactory(new SqliteStoreOptions(Path.Combine(directory, "test.db"), Pooling: false));
        var runId = Guid.NewGuid();

        try
        {
            using (var connection = factory.Open())
            {
                using var create = connection.CreateCommand();
                create.CommandText = """
                    CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                    INSERT INTO settings (key, value) VALUES ('__schema_version', '42');

                    CREATE TABLE autopilot_runs (
                        id                   TEXT PRIMARY KEY,
                        project_id           TEXT NOT NULL,
                        settings             TEXT NOT NULL,
                        state                TEXT NOT NULL,
                        started_utc          TEXT NOT NULL,
                        last_progress_utc    TEXT NOT NULL,
                        stop_after_item      INTEGER NOT NULL DEFAULT 0,
                        base_branch          TEXT,
                        workspace_return_note TEXT,
                        base_workspace_id    TEXT
                    );

                    CREATE TABLE autopilot_run_items (
                        run_id          TEXT NOT NULL,
                        item_id         TEXT NOT NULL,
                        item_order      INTEGER NOT NULL,
                        stage           TEXT NOT NULL,
                        spent_usd       TEXT NOT NULL,
                        failed_reviews  INTEGER NOT NULL,
                        started_utc     TEXT,
                        branch_head_sha TEXT,
                        parked_reason   TEXT,
                        prompt          TEXT,
                        PRIMARY KEY (run_id, item_order)
                    );

                    CREATE TABLE autopilot_run_stops (
                        id         INTEGER PRIMARY KEY AUTOINCREMENT,
                        run_id     TEXT NOT NULL,
                        kind       TEXT NOT NULL,
                        scope      TEXT NOT NULL,
                        limit_name TEXT NOT NULL,
                        figure     TEXT NOT NULL,
                        at_utc     TEXT NOT NULL,
                        item_id    TEXT,
                        reason     TEXT
                    );
                    """;
                create.ExecuteNonQuery();

                var settingsJson = JsonSerializer.Serialize(
                    AutopilotDispatchSettings.Default, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });
                using var insertRun = connection.CreateCommand();
                insertRun.CommandText =
                    "INSERT INTO autopilot_runs (id, project_id, settings, state, started_utc, last_progress_utc) "
                    + "VALUES ($id, 'default', $settings, 'Running', $at, $at);";
                insertRun.Parameters.AddWithValue("$id", runId.ToString("D"));
                insertRun.Parameters.AddWithValue("$settings", settingsJson);
                insertRun.Parameters.AddWithValue("$at", Start.ToString("O"));
                insertRun.ExecuteNonQuery();

                using var insertItem = connection.CreateCommand();
                insertItem.CommandText =
                    "INSERT INTO autopilot_run_items (run_id, item_id, item_order, stage, spent_usd, failed_reviews) "
                    + "VALUES ($run, 'item-1', 0, 'Building', '0', 0);";
                insertItem.Parameters.AddWithValue("$run", runId.ToString("D"));
                insertItem.ExecuteNonQuery();
            }

            SqliteSchema.EnsureCreated(factory);

            using (var connection = factory.Open())
            {
                SchemaMigrator.StoredVersion(connection).Should().Be(SqliteSchema.CurrentVersion);
            }

            var migrated = await new SqliteAutopilotRunStore(factory).GetAsync(runId);
            migrated!.Queue.Single().WaitingSinceUtc.Should().BeNull();
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// A run row written before step 44 (carrying waiting_since_utc, but no waiting_for_capacity_reason on
    /// its items) migrates forward and reads that column back as null, the same shape the v42-to-v43
    /// migration test above proves for waiting_since_utc.
    /// </summary>
    [Fact]
    public async Task A_run_saved_before_waiting_for_capacity_existed_migrates_to_v44_and_reads_it_as_null()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fravashi-autopilot-v43-migration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var factory = new SqliteConnectionFactory(new SqliteStoreOptions(Path.Combine(directory, "test.db"), Pooling: false));
        var runId = Guid.NewGuid();

        try
        {
            using (var connection = factory.Open())
            {
                using var create = connection.CreateCommand();
                create.CommandText = """
                    CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                    INSERT INTO settings (key, value) VALUES ('__schema_version', '43');

                    CREATE TABLE autopilot_runs (
                        id                   TEXT PRIMARY KEY,
                        project_id           TEXT NOT NULL,
                        settings             TEXT NOT NULL,
                        state                TEXT NOT NULL,
                        started_utc          TEXT NOT NULL,
                        last_progress_utc    TEXT NOT NULL,
                        stop_after_item      INTEGER NOT NULL DEFAULT 0,
                        base_branch          TEXT,
                        workspace_return_note TEXT,
                        base_workspace_id    TEXT
                    );

                    CREATE TABLE autopilot_run_items (
                        run_id            TEXT NOT NULL,
                        item_id           TEXT NOT NULL,
                        item_order        INTEGER NOT NULL,
                        stage             TEXT NOT NULL,
                        spent_usd         TEXT NOT NULL,
                        failed_reviews    INTEGER NOT NULL,
                        started_utc       TEXT,
                        branch_head_sha   TEXT,
                        parked_reason     TEXT,
                        prompt            TEXT,
                        waiting_since_utc TEXT,
                        PRIMARY KEY (run_id, item_order)
                    );

                    CREATE TABLE autopilot_run_stops (
                        id         INTEGER PRIMARY KEY AUTOINCREMENT,
                        run_id     TEXT NOT NULL,
                        kind       TEXT NOT NULL,
                        scope      TEXT NOT NULL,
                        limit_name TEXT NOT NULL,
                        figure     TEXT NOT NULL,
                        at_utc     TEXT NOT NULL,
                        item_id    TEXT,
                        reason     TEXT
                    );
                    """;
                create.ExecuteNonQuery();

                var settingsJson = JsonSerializer.Serialize(
                    AutopilotDispatchSettings.Default, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });
                using var insertRun = connection.CreateCommand();
                insertRun.CommandText =
                    "INSERT INTO autopilot_runs (id, project_id, settings, state, started_utc, last_progress_utc) "
                    + "VALUES ($id, 'default', $settings, 'Running', $at, $at);";
                insertRun.Parameters.AddWithValue("$id", runId.ToString("D"));
                insertRun.Parameters.AddWithValue("$settings", settingsJson);
                insertRun.Parameters.AddWithValue("$at", Start.ToString("O"));
                insertRun.ExecuteNonQuery();

                using var insertItem = connection.CreateCommand();
                insertItem.CommandText =
                    "INSERT INTO autopilot_run_items (run_id, item_id, item_order, stage, spent_usd, failed_reviews) "
                    + "VALUES ($run, 'item-1', 0, 'Building', '0', 0);";
                insertItem.Parameters.AddWithValue("$run", runId.ToString("D"));
                insertItem.ExecuteNonQuery();
            }

            SqliteSchema.EnsureCreated(factory);

            using (var connection = factory.Open())
            {
                SchemaMigrator.StoredVersion(connection).Should().Be(SqliteSchema.CurrentVersion);
            }

            var migrated = await new SqliteAutopilotRunStore(factory).GetAsync(runId);
            migrated!.Queue.Single().WaitingForCapacityReason.Should().BeNull();
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task Open_runs_names_only_the_ones_still_running()
    {
        var running = SampleRun(Guid.NewGuid());
        var stopped = SampleRun(Guid.NewGuid()) with { State = AutopilotRunState.Stopped };
        var completed = SampleRun(Guid.NewGuid()) with { State = AutopilotRunState.Completed };
        await _store.SaveAsync(running);
        await _store.SaveAsync(stopped);
        await _store.SaveAsync(completed);

        var open = await _store.OpenRunsAsync();

        open.Select(r => r.Id).Should().BeEquivalentTo([running.Id]);
    }

    [Fact]
    public async Task A_run_whose_stored_settings_cannot_be_read_throws_rather_than_substituting_the_shipped_defaults()
    {
        // The limits ARE the run's safety boundary (CLAUDE.md two-stores rule 4, nothing silently resolved): a
        // run saved with tighter limits than the defaults must never read back as if it had the defaults.
        var id = await InsertUnreadableRunAsync();

        var act = async () => await _store.GetAsync(id);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*unreadable dispatch settings*");
    }

    [Fact]
    public async Task One_runs_unreadable_settings_does_not_stop_another_run_from_being_offered_to_resume()
    {
        // GetAsync throws by design (the test above), but OpenRunsAsync drives resume-at-start for EVERY run:
        // one bad row must not abort it for every run, forever, and must not leave the bad row Running either,
        // or every later start would hit the same throw again.
        var unreadable = await InsertUnreadableRunAsync();
        var readable = SampleRun(Guid.NewGuid());
        await _store.SaveAsync(readable);

        var open = await _store.OpenRunsAsync();

        open.Select(r => r.Id).Should().BeEquivalentTo([readable.Id]);

        // Its row moved off Running (a direct GetAsync still cannot read its settings and still throws; that is
        // GetAsync's own contract from the test above, unchanged), so a second resume pass does not see it again.
        // The readable run is still legitimately Running (nothing here applies a restart to it) and keeps
        // appearing; only the unreadable one must not reappear.
        var stillUnreadable = async () => await _store.GetAsync(unreadable);
        await stillUnreadable.Should().ThrowAsync<InvalidOperationException>();
        (await _store.OpenRunsAsync()).Select(r => r.Id).Should().BeEquivalentTo([readable.Id]);
    }

    [Fact]
    public async Task One_runs_malformed_settings_json_does_not_stop_another_run_from_being_offered_to_resume()
    {
        // Null is one way settings fail to read; malformed JSON is a different exception entirely (JsonException,
        // not the "deserialised to null" InvalidOperationException), and must take the same per-run path.
        var unreadable = await InsertUnreadableRunAsync("{not json");
        var readable = SampleRun(Guid.NewGuid());
        await _store.SaveAsync(readable);

        var open = await _store.OpenRunsAsync();

        open.Select(r => r.Id).Should().BeEquivalentTo([readable.Id]);
        (await _store.OpenRunsAsync()).Select(r => r.Id).Should().BeEquivalentTo([readable.Id]);
    }

    [Fact]
    public async Task One_runs_settings_naming_an_unknown_runtime_does_not_stop_another_run_from_being_offered_to_resume()
    {
        // Valid JSON naming an enum value this build does not know (a runtime renamed or removed, or simply
        // never shipped) fails on a different read path than malformed JSON (the enum converter rather than
        // the tokenizer), but raises the same JsonException; kept as its own test because it proves the catch
        // set covers a syntactically valid blob this build still cannot interpret, not a different exception.
        const string settings =
            """
            {"Limits":{"MoneyPerItemUsd":15,"MoneyPerRunUsd":100,"FailedCommitReviewsPerItem":3,
            "TimePerItem":"02:00:00","NoProgressWindow":"00:30:00"},"MissingScenario":"Write",
            "PersonWaitTimeout":"01:00:00","Builder":{"Runtime":"Gemini","Model":"x"},
            "Operator":{"Runtime":"Claude","Model":"claude-sonnet-5"}}
            """;
        var unreadable = await InsertUnreadableRunAsync(settings);
        var readable = SampleRun(Guid.NewGuid());
        await _store.SaveAsync(readable);

        var open = await _store.OpenRunsAsync();

        open.Select(r => r.Id).Should().BeEquivalentTo([readable.Id]);
        (await _store.OpenRunsAsync()).Select(r => r.Id).Should().BeEquivalentTo([readable.Id]);
    }

    [Fact]
    public async Task Settings_saved_before_the_concurrency_cap_existed_read_back_as_a_sequential_run()
    {
        // A run dispatched by a build before #308 has no EffectiveConcurrencyCap in its stored settings. It
        // was started sharing one workspace, so it must resume that way: a cap of 1, never the Settings cap.
        const string settingsBefore308 =
            """
            {"Limits":{"MoneyPerItemUsd":15,"MoneyPerRunUsd":100,"FailedCommitReviewsPerItem":3,
            "TimePerItem":"02:00:00","NoProgressWindow":"00:30:00"},"MissingScenario":"Write",
            "PersonWaitTimeout":"01:00:00","Builder":{"Runtime":"Claude","Model":"claude-opus-5-5"},
            "Operator":{"Runtime":"Claude","Model":"claude-sonnet-5"}}
            """;
        var id = Guid.NewGuid();
        var stamp = Start.ToString("O", CultureInfo.InvariantCulture);
        using (var connection = _context.Factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "INSERT INTO autopilot_runs (id, project_id, settings, state, started_utc, last_progress_utc) "
                + "VALUES ($id, $project, $settings, $state, $stamp, $stamp);";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$project", ProjectId.Default.Value);
            command.Parameters.AddWithValue("$settings", settingsBefore308);
            command.Parameters.AddWithValue("$state", AutopilotRunState.Running.ToString());
            command.Parameters.AddWithValue("$stamp", stamp);
            await command.ExecuteNonQueryAsync();
        }

        var reread = await _store.GetAsync(id);

        reread.Should().NotBeNull();
        reread!.Settings.EffectiveConcurrencyCap.Should().Be(1);
    }

    [Fact]
    public async Task A_bad_timestamp_names_its_own_cause_rather_than_blaming_the_settings()
    {
        // The settings here are perfectly valid; what fails to read is the timestamp. The recorded stop must
        // say THAT, not repeat a fixed "unreadable dispatch settings" sentence that would be false here.
        const string validSettings =
            """
            {"Limits":{"MoneyPerItemUsd":15,"MoneyPerRunUsd":100,"FailedCommitReviewsPerItem":3,
            "TimePerItem":"02:00:00","NoProgressWindow":"00:30:00"},"MissingScenario":"Write",
            "PersonWaitTimeout":"01:00:00","Builder":{"Runtime":"Claude","Model":"claude-opus-5-5"},
            "Operator":{"Runtime":"Claude","Model":"claude-sonnet-5"}}
            """;
        var id = Guid.NewGuid();
        using (var connection = _context.Factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "INSERT INTO autopilot_runs (id, project_id, settings, state, started_utc, last_progress_utc) "
                + "VALUES ($id, $project, $settings, $state, 'not-a-date', 'not-a-date');";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$project", ProjectId.Default.Value);
            command.Parameters.AddWithValue("$settings", validSettings);
            command.Parameters.AddWithValue("$state", AutopilotRunState.Running.ToString());
            await command.ExecuteNonQueryAsync();
        }

        await _store.OpenRunsAsync();

        using var readConnection = _context.Factory.Open();
        using var readCommand = readConnection.CreateCommand();
        readCommand.CommandText = "SELECT figure, reason FROM autopilot_run_stops WHERE run_id = $id;";
        readCommand.Parameters.AddWithValue("$id", id.ToString("D"));
        using var reader = await readCommand.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Contain("FormatException").And.NotContain("dispatch settings");
    }

    private async Task<Guid> InsertUnreadableRunAsync(string settings = "null")
    {
        var id = Guid.NewGuid();
        using var connection = _context.Factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO autopilot_runs (id, project_id, settings, state, started_utc, last_progress_utc) "
            + "VALUES ($id, $project, $settings, $state, $started, $started);";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$project", ProjectId.Default.Value);
        command.Parameters.AddWithValue("$settings", settings);
        command.Parameters.AddWithValue("$state", AutopilotRunState.Running.ToString());
        command.Parameters.AddWithValue("$started", Start.ToString("O"));
        await command.ExecuteNonQueryAsync();
        return id;
    }
}
