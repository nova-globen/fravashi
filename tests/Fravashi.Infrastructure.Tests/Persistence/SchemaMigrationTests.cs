using AwesomeAssertions;
using Fravashi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Persistence;

/// <summary>
/// The acceptance for backlog 60: a populated database is UPGRADED from every shipped schema version to
/// current, its rows survive, its shape becomes correct, and its version marker is a consequence of the
/// completed steps rather than an unconditional stamp. The fixtures build each old shape with RAW SQL
/// (hand-written historical DDL + representative rows), independent of the production migration chain, so
/// these are genuine old databases rather than the code under test asserted back to itself.
/// </summary>
public sealed class SchemaMigrationTests : IDisposable
{
    private const string FakeKey = "sk-" + "ant-api03-Fake0123456789Fake0123456789Fake0123456789FakeAA";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fravashi-migration-tests", Guid.NewGuid().ToString("N"));

    public SchemaMigrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private SqliteConnectionFactory NewFactory(string name = "test.db") =>
        new(new SqliteStoreOptions(Path.Combine(_directory, name), null, Pooling: false));

    // -------------------------------------------------------------------------------------------------
    // Fresh install and no-op
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void A_fresh_database_migrates_to_the_current_version_with_every_table_present()
    {
        var factory = NewFactory();

        SqliteSchema.EnsureCreated(factory);

        using var connection = factory.Open();
        SchemaMigrator.StoredVersion(connection).Should().Be(SqliteSchema.CurrentVersion);
        foreach (var table in AllCurrentTables)
        {
            TableExists(connection, table).Should().BeTrue($"a fresh install must create '{table}'");
        }

        // The onboarding seed row is part of step 1 and must be present on a fresh install.
        Scalar(connection, "SELECT completed FROM onboarding_state WHERE id = 1;").Should().Be(0L);
    }

    [Fact]
    public void A_database_already_at_the_current_version_runs_no_steps()
    {
        var factory = NewFactory();
        SqliteSchema.EnsureCreated(factory); // now at CurrentVersion

        using var connection = factory.Open();

        // Every step at or below the current version must be gated OUT. A chain of poison steps whose
        // versions are all <= current proves it: if any ran it would throw and fail the test.
        var poison = new List<SchemaMigration>
        {
            new(1, (_, _) => throw new InvalidOperationException("step 1 must not re-run")),
            new(SqliteSchema.CurrentVersion, (_, _) => throw new InvalidOperationException("top step must not re-run")),
        };

        var run = () => SchemaMigrator.Run(connection, poison);
        run.Should().NotThrow("a database already at the top runs none of the steps");
    }

    [Fact]
    public void A_database_that_predates_the_conflict_note_gains_the_column_and_keeps_its_rows()
    {
        // Schema 28 (backlog 47/48). A fresh database gets the column from the CREATE, so the ALTER is only
        // ever taken by a real upgrade, which is the one path a fresh-database test cannot reach.
        var factory = NewFactory();
        using (var seed = factory.Open())
        {
            using var make = seed.CreateCommand();
            make.CommandText = """
                CREATE TABLE work_item_conflicts (
                    project_id     TEXT NOT NULL,
                    conflict_id    TEXT NOT NULL,
                    item_id        TEXT NOT NULL,
                    field          TEXT NOT NULL,
                    base_value     TEXT,
                    local_value    TEXT,
                    provider_value TEXT,
                    detected_utc   TEXT NOT NULL,
                    choice         TEXT,
                    resolved_utc   TEXT,
                    PRIMARY KEY (project_id, conflict_id)
                );
                INSERT INTO work_item_conflicts
                    (project_id, conflict_id, item_id, field, local_value, provider_value, detected_utc)
                VALUES ('default', 'c-1', 'i-1', 'Status', 'triage', 'backlog', '2026-09-22T00:00:00.0000000Z');
                """;
            make.ExecuteNonQuery();
        }

        SqliteSchema.EnsureCreated(factory);

        using var connection = factory.Open();
        SchemaMigrator.StoredVersion(connection).Should().Be(SqliteSchema.CurrentVersion);

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT note, local_value FROM work_item_conflicts WHERE conflict_id = 'c-1';";
        using var reader = read.ExecuteReader();
        reader.Read().Should().BeTrue("the disagreement that was already recorded is still there");
        reader.IsDBNull(0).Should().BeTrue("an older row has no note, which is not the same as an empty one");
        reader.GetString(1).Should().Be("triage");
    }

    [Fact]
    public void A_database_that_predates_the_approved_design_version_gains_the_column_and_keeps_its_rows()
    {
        // Schema 37 (backlog 209). A fresh database gets the column from the CREATE, so the ALTER is only
        // ever taken by a real upgrade, which is the one path a fresh-database test cannot reach.
        var factory = NewFactory();
        using (var seed = factory.Open())
        {
            using var make = seed.CreateCommand();
            make.CommandText = """
                CREATE TABLE work_item_refs (
                    id TEXT PRIMARY KEY, item_id TEXT NOT NULL, role TEXT NOT NULL, kind TEXT NOT NULL,
                    target TEXT NOT NULL, workspace_id TEXT, label TEXT, note TEXT, ordinal INTEGER NOT NULL);
                INSERT INTO work_item_refs (id, item_id, role, kind, target, label, ordinal)
                VALUES ('r-1', 'i-1', 'approved_design', 'design', '12', 'The old welcome page', 0);
                """;
            make.ExecuteNonQuery();
        }

        SqliteSchema.EnsureCreated(factory);

        using var connection = factory.Open();
        SchemaMigrator.StoredVersion(connection).Should().Be(SqliteSchema.CurrentVersion);

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT version, label FROM work_item_refs WHERE id = 'r-1';";
        using var reader = read.ExecuteReader();
        reader.Read().Should().BeTrue("the reference that was already recorded is still there");
        reader.IsDBNull(0).Should().BeTrue("an approval made before pinning existed has nothing to compare against");
        reader.GetString(1).Should().Be("The old welcome page");
    }

    [Fact]
    public void A_database_that_predates_wiki_revisions_gets_a_baseline_snapshot_of_every_document()
    {
        // Schema 36 (backlog 209). The backfill gives a document that already exists one row at the
        // version it happens to be at right now, so approving it right after the upgrade still has
        // something to pin to rather than a gap between "upgraded" and "first write under the new code".
        var factory = NewFactory();
        using (var seed = factory.Open())
        {
            using var make = seed.CreateCommand();
            make.CommandText = """
                CREATE TABLE wiki_items (
                    id TEXT PRIMARY KEY, number INTEGER NOT NULL, type TEXT NOT NULL, title TEXT NOT NULL,
                    slug TEXT NOT NULL, body TEXT NOT NULL, version INTEGER NOT NULL,
                    created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL);
                INSERT INTO wiki_items (id, number, type, title, slug, body, version, created_utc, updated_utc)
                    VALUES ('wiki-1', 1, 'design', 'The welcome page', 'the-welcome-page', 'Nine edits later.', 3,
                            '2026-09-01T00:00:00.0000000+00:00', '2026-09-20T00:00:00.0000000+00:00');
                """;
            make.ExecuteNonQuery();
        }

        SqliteSchema.EnsureCreated(factory);

        using var connection = factory.Open();
        SchemaMigrator.StoredVersion(connection).Should().Be(SqliteSchema.CurrentVersion);

        using var read = connection.CreateCommand();
        read.CommandText =
            "SELECT version, title, body FROM wiki_item_revisions WHERE item_id = 'wiki-1';";
        using var reader = read.ExecuteReader();
        reader.Read().Should().BeTrue("the document that already existed gets a baseline snapshot");
        reader.GetInt64(0).Should().Be(3);
        reader.GetString(1).Should().Be("The welcome page");
        reader.GetString(2).Should().Be("Nine edits later.");
        reader.Read().Should().BeFalse("exactly one baseline row, not one per version nobody recorded");
    }

    // -------------------------------------------------------------------------------------------------
    // Populated upgrade from every shipped version
    // -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0)] // pre-marker: no tables, no version row at all
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    public void A_populated_database_upgrades_from_every_shipped_version(int fromVersion)
    {
        var factory = NewFactory();
        BuildHistoricalDatabase(factory, fromVersion);

        SqliteSchema.EnsureCreated(factory);

        using var connection = factory.Open();

        // (d) the marker is now current.
        SchemaMigrator.StoredVersion(connection).Should().Be(SqliteSchema.CurrentVersion);

        // (a) every table that exists at current is now present.
        foreach (var table in AllCurrentTables)
        {
            TableExists(connection, table).Should().BeTrue(
                $"upgrading from v{fromVersion} must leave '{table}' present");
        }

        // v11 (backlog 37): the default project exists exactly once and no work item was invented on the way up.
        Scalar(connection, "SELECT COUNT(*) FROM projects WHERE id = 'default';").Should().Be(1L);
        Scalar(connection, "SELECT COUNT(*) FROM work_items;").Should().Be(fromVersion >= 11 ? 1L : 0L);

        if (fromVersion >= 11)
        {
            // v13 (backlog 37 increment C1): an item that predates the agent layer keeps every value it had,
            // reads back with the new columns null, and gains no rows in the new tables.
            Scalar(connection, "SELECT title FROM work_items WHERE id = 'item-1';")
                .Should().Be("An item that predates the agent layer");
            Scalar(connection, "SELECT version FROM work_items WHERE id = 'item-1';").Should().Be(3L);
            Scalar(connection, "SELECT COUNT(*) FROM work_item_events WHERE item_id = 'item-1';").Should().Be(1L);
            Scalar(connection, "SELECT ambiguity_score FROM work_items WHERE id = 'item-1';").Should().BeNull();
            Scalar(connection, "SELECT change_budget_max_files FROM work_items WHERE id = 'item-1';").Should().BeNull();
            Scalar(connection, "SELECT next_action FROM work_items WHERE id = 'item-1';").Should().BeNull();

            // v40 (backlog 298): an item that predates the autopilot mark reads back unmarked, never null
            // (the column defaults to 0, unlike the nullable agent-layer columns above).
            Scalar(connection, "SELECT marked_for_autopilot FROM work_items WHERE id = 'item-1';").Should().Be(0L);

            // v45 (requested 2026-10-22, model and effort per work item): an item that predates the suggestions
            // reads back with both null, which is Default: its conversation starts exactly as it did before.
            Scalar(connection, "SELECT suggested_model FROM work_items WHERE id = 'item-1';").Should().BeNull();
            Scalar(connection, "SELECT suggested_effort FROM work_items WHERE id = 'item-1';").Should().BeNull();

            // v46 (work item #359): an item that predates attachments carries none, and the table arrives empty.
            Scalar(connection, "SELECT COUNT(*) FROM work_item_attachments;").Should().Be(0L);
            Scalar(connection, "SELECT COUNT(*) FROM work_item_criteria;")
                .Should().Be(fromVersion >= 13 ? 1L : 0L);
            Scalar(connection, "SELECT COUNT(*) FROM work_item_scenarios;").Should().Be(0L);

            // v14 (increment C2): the evidence table arrives empty on an older build, and a record an
            // already-migrated build wrote survives the v15 step with its pointer intact.
            Scalar(connection, "SELECT COUNT(*) FROM work_item_evidence;")
                .Should().Be(fromVersion >= 14 ? 1L : 0L);
            if (fromVersion >= 14)
            {
                Scalar(connection, "SELECT criterion_id FROM work_item_evidence WHERE id = 'ev-1';")
                    .Should().Be("crit-1");
            }

            // v15 (backlog 44): the session/work-item join arrives empty on an older build, and a link an
            // already-migrated build wrote survives the v16 step.
            Scalar(connection, "SELECT COUNT(*) FROM agent_session_work_items;")
                .Should().Be(fromVersion >= 15 ? 1L : 0L);

            // v16 (backlog 39): the authored wiki arrives empty on an older build, with its number
            // allocator ready at 1, and a document an already-migrated build wrote survives the v17 step.
            // The v3 wiki_pages MIRROR is untouched either way: two different things that share a word.
            Scalar(connection, "SELECT COUNT(*) FROM wiki_items;").Should().Be(fromVersion >= 16 ? 1L : 0L);
            Scalar(connection, "SELECT next_number FROM wiki_state WHERE id = 1;")
                .Should().Be(fromVersion >= 16 ? 2L : 1L);
            if (fromVersion >= 16)
            {
                Scalar(connection, "SELECT title FROM wiki_items WHERE id = 'wiki-1';")
                    .Should().Be("An older plan");
            }

            // v17 (backlog 45): the session/plan binding arrives empty on an older build, and a binding an
            // already-migrated build wrote survives the v18 step.
            Scalar(connection, "SELECT COUNT(*) FROM agent_session_plans;")
                .Should().Be(fromVersion >= 17 ? 1L : 0L);

            // v18 (backlog 85): the approved-change rows arrive empty on an older build, and a row an
            // already-migrated build wrote survives the v19 step.
            Scalar(connection, "SELECT COUNT(*) FROM agent_session_changed_files;")
                .Should().Be(fromVersion >= 18 ? 1L : 0L);

            // v19 (backlog 88): the session/role table arrives empty on an older build, which means every
            // conversation that predates roles keeps running unrestricted rather than being re-scoped, and
            // a role an already-migrated build wrote survives the v20 step.
            Scalar(connection, "SELECT COUNT(*) FROM agent_session_roles;")
                .Should().Be(fromVersion >= 19 ? 1L : 0L);

            // v20 (backlog 55): the step's runnable command is a new COLUMN on a table v13 created, and the
            // step probes for it, so a build that already has it upgrades without trying to add it twice.
            Scalar(connection, "SELECT COUNT(*) FROM work_item_scenario_steps;").Should().Be(0L);

            // v21 (backlog 86): the attempt branch arrives, and it arrives empty.
            Scalar(connection, "SELECT COUNT(*) FROM agent_session_branches;").Should().Be(0L);

            // v22 (backlog 82): the provider mirroring sidecar, the durable write outbox and the conflict
            // surface all arrive EMPTY, which is the correct state for a database that has never had a
            // provider configured. An outbox that arrived with anything in it would mean an upgrade had
            // invented writes nobody made.
            Scalar(connection, "SELECT COUNT(*) FROM work_item_mirror;").Should().Be(0L);
            Scalar(connection, "SELECT COUNT(*) FROM work_item_outbox;").Should().Be(0L);
            Scalar(connection, "SELECT COUNT(*) FROM work_item_conflicts;").Should().Be(0L);
            if (fromVersion >= 13)
            {
                Scalar(connection, "SELECT text FROM work_item_criteria WHERE id = 'crit-1';")
                    .Should().Be("It works.");
            }
        }

        // (b) the rows planted at the old shape survived intact.
        if (fromVersion >= 1)
        {
            Scalar(connection, "SELECT value FROM settings WHERE key = 'Tracker:Provider';")
                .Should().Be("github");
            Scalar(connection, "SELECT kind FROM provider_connections WHERE id = 'pc1';")
                .Should().Be("github");
            Scalar(connection, "SELECT protected_value FROM secrets WHERE reference = 'ref1';")
                .Should().Be("enc:abc");
            Scalar(connection, "SELECT completed FROM onboarding_state WHERE id = 1;").Should().Be(1L);
        }

        if (fromVersion >= 2)
        {
            Scalar(connection, "SELECT name FROM framework_artifacts WHERE id = 'fa1';").Should().Be("skill-x");
            Scalar(connection, "SELECT file_name FROM framework_resources WHERE id = 'fr1';").Should().Be("a.txt");
        }

        if (fromVersion >= 3)
        {
            Scalar(connection, "SELECT title FROM wiki_pages WHERE id = 'wp1';").Should().Be("Home");
        }

        if (fromVersion >= 4)
        {
            Scalar(connection, "SELECT name FROM workspaces WHERE id = 'ws1';").Should().Be("alpha");

            // v11 (backlog 37) put every workspace that existed in the seeded default project, and v48 (item #373,
            // no shared bucket) takes it out again: the folder is not set up, and the project it was in is remembered.
            Scalar(connection, "SELECT project_id FROM workspaces WHERE id = 'ws1';").Should().BeNull();
            Scalar(connection, "SELECT former_project_id FROM workspaces WHERE id = 'ws1';").Should().Be("default");
        }

        if (fromVersion >= 5)
        {
            Scalar(connection, "SELECT provider FROM git_hosts WHERE host = 'github.com';").Should().Be("github");
        }

        if (fromVersion >= 6)
        {
            Scalar(connection, "SELECT title FROM agent_sessions WHERE id = 's1';").Should().Be("first session");

            var secretPayload = (string)Scalar(connection,
                "SELECT payload FROM agent_session_events WHERE session_id = 's1' AND kind = 'tool_completed';")!;

            // The scrub is step 7. Keyed on THAT version, not on CurrentVersion: a later bump (v8 added the
            // usage summary) must not turn this into a claim that step 8 redacts, which it does not.
            const int scrubVersion = 7;
            if (fromVersion < scrubVersion)
            {
                // (c) the v7 data transform ran on an upgrade that crossed v7 while carrying session rows:
                // the secret an agent printed before the redactor existed is gone.
                secretPayload.Should().NotContain(FakeKey);
                secretPayload.Should().Contain("[redacted:ANTHROPIC_API_KEY]");
            }
            else
            {
                // Already past the scrub: it is a completed step and must NOT re-run, so a raw row (only
                // plantable by writing SQL directly, as the fixture does) stays raw.
                secretPayload.Should().Contain(FakeKey);
            }

            // The ordinary row is untouched either way.
            var ordinaryPayload = (string)Scalar(connection,
                "SELECT payload FROM agent_session_events WHERE session_id = 's1' AND kind = 'text';")!;
            ordinaryPayload.Should().Be("{\"detail\":\"just a normal line\"}");

            // v9 (backlog 109): the row was never renamed, so its custom title is null and the derived
            // title is what it shows.
            Scalar(connection, "SELECT custom_title FROM agent_sessions WHERE id = 's1';").Should().BeNull();
        }
    }

    // -------------------------------------------------------------------------------------------------
    // v9: a nullable custom title on every session row (backlog 109)
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void Upgrading_a_populated_v8_database_adds_a_null_custom_title_and_keeps_every_row_readable()
    {
        // A v8 database (agent_sessions with the usage-summary shape) carrying rows. The v9 step adds the
        // column only; the rows come back through the production store with their derived titles and no
        // custom title, and the usage figures the v8 shape held are untouched.
        var factory = NewFactory();
        BuildV8Sessions(factory);

        SqliteSchema.EnsureCreated(factory);

        using var connection = factory.Open();
        SchemaMigrator.StoredVersion(connection).Should().Be(SqliteSchema.CurrentVersion);
        ColumnExists(connection, "agent_sessions", "custom_title").Should().BeTrue();
        Scalar(connection, $"SELECT custom_title FROM agent_sessions WHERE id = '{V8SessionA}';").Should().BeNull();
        Scalar(connection, $"SELECT cost_usd FROM agent_sessions WHERE id = '{V8SessionA}';").Should().Be("0.05");

        var store = new SqliteSessionStore(factory);
        var rows = store.ForWorkspace("ws1");
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => r.CustomTitle == null);
        rows.Select(r => r.DisplayTitle).Should().BeEquivalentTo("A", "C");

        // And the column is live: a rename through the store lands in it.
        var a = rows.Single(r => r.Title == "A");
        store.Save(a with { CustomTitle = "the first one" });
        store.Find(a.Id)!.DisplayTitle.Should().Be("the first one");
    }

    [Fact]
    public void The_custom_title_step_is_idempotent_against_an_inaccurate_version_marker()
    {
        // A database whose column already exists but whose marker says v8 (the belt-and-braces case the
        // v8 step also guards): the step must probe and skip, not fail on a duplicate column.
        var factory = NewFactory();
        BuildV8Sessions(factory);
        using (var seed = factory.Open())
        {
            using var alter = seed.CreateCommand();
            alter.CommandText = "ALTER TABLE agent_sessions ADD COLUMN custom_title TEXT;";
            alter.ExecuteNonQuery();
        }

        var migrate = () => SqliteSchema.EnsureCreated(factory);

        migrate.Should().NotThrow();
        using var connection = factory.Open();
        SchemaMigrator.StoredVersion(connection).Should().Be(SqliteSchema.CurrentVersion);
    }

    // Real GUIDs, because the production store parses ids on the way back out.
    private const string V8SessionA = "0b6a5f2e-1c3d-4e5f-8a9b-000000000001";
    private const string V8SessionB = "0b6a5f2e-1c3d-4e5f-8a9b-000000000002";
    private const string V8SessionC = "0b6a5f2e-1c3d-4e5f-8a9b-000000000003";

    private static void BuildV8Sessions(SqliteConnectionFactory factory)
    {
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $$"""
            CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE agent_sessions (
                id TEXT PRIMARY KEY, workspace_id TEXT NOT NULL, tool TEXT NOT NULL, title TEXT NOT NULL,
                provider_session_id TEXT, state TEXT NOT NULL, started_utc TEXT NOT NULL, ended_utc TEXT,
                cost_usd TEXT NOT NULL DEFAULT '0', input_tokens INTEGER NOT NULL DEFAULT 0,
                output_tokens INTEGER NOT NULL DEFAULT 0, cache_read_tokens INTEGER NOT NULL DEFAULT 0,
                tool_calls INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE agent_session_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL, kind TEXT NOT NULL,
                payload TEXT NOT NULL, created_utc TEXT NOT NULL);

            INSERT INTO agent_sessions (id, workspace_id, tool, title, state, started_utc, cost_usd, input_tokens, output_tokens) VALUES
                ('{{V8SessionA}}', 'ws1', 'Claude', 'A', 'Completed', '2026-08-01T00:00:00+00:00', '0.05', 300, 600),
                ('{{V8SessionB}}', 'ws2', 'Claude', 'B', 'Completed', '2026-08-02T00:00:00+00:00', '0.01', 10, 20),
                ('{{V8SessionC}}', 'ws1', 'Claude', 'C', 'Failed', '2026-08-03T00:00:00+00:00', '0', 0, 0);

            INSERT INTO settings (key, value) VALUES ('__schema_version', '8');
            """;
        command.ExecuteNonQuery();
    }

    // -------------------------------------------------------------------------------------------------
    // v10: locally recorded product events (backlog 74)
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void Upgrading_a_populated_v9_database_adds_an_empty_product_events_table_and_keeps_every_row()
    {
        // A real v9 database carrying rows in every table it had. The v10 step adds one table and nothing
        // else, and it must arrive EMPTY: recording is opt-in and off, so an upgrade that invented events
        // would be inventing consent.
        var factory = NewFactory();
        BuildHistoricalDatabase(factory, 9);

        SqliteSchema.EnsureCreated(factory);

        using var connection = factory.Open();
        SchemaMigrator.StoredVersion(connection).Should().Be(SqliteSchema.CurrentVersion);
        TableExists(connection, "product_events").Should().BeTrue();
        Scalar(connection, "SELECT COUNT(*) FROM product_events;").Should().Be(0L);

        // The rows that were there are untouched.
        Scalar(connection, "SELECT title FROM agent_sessions WHERE id = 's1';").Should().Be("first session");
        Scalar(connection, "SELECT value FROM settings WHERE key = 'Tracker:Provider';").Should().Be("github");
        Scalar(connection, "SELECT name FROM workspaces WHERE id = 'ws1';").Should().Be("alpha");

        // And the new table is live: the production store writes and reads through it.
        var store = new SqliteProductEventStore(factory);
        var recorded = Fravashi.Domain.Telemetry.ProductEvent.OnboardingCompleted(
            new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero), TimeSpan.FromMinutes(7));
        store.Append(recorded);
        store.Recent().Should().ContainSingle().Which.Should().Be(recorded);
    }

    // -------------------------------------------------------------------------------------------------
    // v11: spending by the day it was incurred (backlog 62, resume in place)
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void Upgrading_a_populated_v10_database_credits_each_sessions_spending_to_the_days_it_happened()
    {
        // A real v10 database whose one session spent $0.02 on 14 September and $0.03 more on 15 September
        // (cumulative snapshots 0.02 then 0.05), plus a second session that spent $0.01 on the 15th. The v11
        // step must split the first session across both days from the events' own stamps, not lump it on
        // the day it started.
        var factory = NewFactory();
        BuildHistoricalDatabase(factory, 10);
        using (var seed = factory.Open())
        {
            using var command = seed.CreateCommand();
            command.CommandText = """
                INSERT INTO agent_sessions (id, workspace_id, tool, title, state, started_utc, cost_usd, input_tokens, output_tokens)
                    VALUES ('spent', 'ws1', 'Claude', 'resumed', 'Completed', '2026-09-14T22:00:00.0000000+00:00', '0.05', 30, 60),
                           ('other', 'ws1', 'Claude', 'other', 'Completed', '2026-09-15T08:00:00.0000000+00:00', '0.01', 1, 2);
                INSERT INTO agent_session_events (session_id, kind, payload, created_utc) VALUES
                    ('spent', 'stats', '{"costUsd":0.02,"inputTokens":10,"outputTokens":20}', '2026-09-14T23:00:00.0000000+00:00'),
                    ('spent', 'stats', '{"costUsd":0.05,"inputTokens":30,"outputTokens":60}', '2026-09-15T01:00:00.0000000+00:00'),
                    ('other', 'stats', '{"costUsd":0.01,"inputTokens":1,"outputTokens":2}', '2026-09-15T08:05:00.0000000+00:00');
                """;
            command.ExecuteNonQuery();
        }

        SqliteSchema.EnsureCreated(factory);

        using var connection = factory.Open();
        SchemaMigrator.StoredVersion(connection).Should().Be(SqliteSchema.CurrentVersion);
        Scalar(connection, "SELECT cost_usd FROM agent_session_usage_days WHERE session_id = 'spent' AND day_utc = '2026-09-14';")
            .Should().Be("0.02");
        Scalar(connection, "SELECT cost_usd FROM agent_session_usage_days WHERE session_id = 'spent' AND day_utc = '2026-09-15';")
            .Should().Be("0.03");
        Scalar(connection, "SELECT input_tokens FROM agent_session_usage_days WHERE session_id = 'spent' AND day_utc = '2026-09-15';")
            .Should().Be(20L);

        // The rows that were there are untouched, and the production totals read the new days. The one
        // product event is the one the v10 fixture records, so this asserts the step left that table alone.
        Scalar(connection, "SELECT title FROM agent_sessions WHERE id = 's1';").Should().Be("first session");
        Scalar(connection, "SELECT COUNT(*) FROM product_events;").Should().Be(1L);
        var totals = new SqliteSessionStore(factory).ReadUsageTotals(
            new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        totals.Today.CostUsd.Should().Be(0.04m);
        totals.Month.CostUsd.Should().Be(0.06m);
        totals.AllTime.CostUsd.Should().Be(0.06m);
    }

    [Fact]
    public void The_usage_days_step_adds_nothing_twice_against_an_inaccurate_version_marker()
    {
        var factory = NewFactory();
        SqliteSchema.EnsureCreated(factory);
        var store = new SqliteSessionStore(factory);
        var id = Fravashi.Domain.AgentSessions.AgentSessionId.New();
        var at = new DateTimeOffset(2026, 9, 15, 1, 0, 0, TimeSpan.Zero);
        store.Save(new Fravashi.Application.Sessions.SessionRecord(
            id, "ws1", Fravashi.Domain.AgentSessions.AgentTool.Claude, "t", null,
            Fravashi.Domain.AgentSessions.AgentSessionState.Completed, at, at));
        store.Append(id, [new Fravashi.Domain.AgentSessions.StatsUpdated(
            at, new Fravashi.Domain.AgentSessions.AgentStatsSnapshot(1, 2, 0, 0.25m, 0, 0, 1))]);

        using (var connection = factory.Open())
        {
            using var rewind = connection.CreateCommand();
            rewind.CommandText = $"UPDATE settings SET value = '10' WHERE key = '{SqliteSchema.SchemaVersionKey}';";
            rewind.ExecuteNonQuery();
        }

        SqliteSchema.EnsureCreated(factory);

        var midnight = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        store.ReadUsageTotals(midnight, midnight).Today.CostUsd.Should().Be(0.25m);
    }

    [Fact]
    public void The_product_events_table_has_no_column_free_text_could_be_stored_in()
    {
        // The table IS the payload schema (backlog 74 decision c). A future column called "detail" or
        // "message" would be the way a prompt or a path got into it, so the shape is asserted here as
        // well as in the domain, because a schema step is where such a column would actually arrive.
        var factory = NewFactory();
        SqliteSchema.EnsureCreated(factory);

        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info('product_events') ORDER BY cid;";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(0));
        }

        columns.Should().Equal("id", "kind", "occurred_utc", "outcome", "workspace_hash", "duration_ms", "count");
    }

    // -------------------------------------------------------------------------------------------------
    // v11: projects, the workspace binding and the work item tables (backlog 37)
    // -------------------------------------------------------------------------------------------------

    private static readonly string[] WorkItemTables =
        ["projects", "work_items", "work_item_links", "work_item_events", "work_item_operations"];

    [Fact]
    public async Task Upgrading_a_populated_v10_database_leaves_its_workspaces_in_no_project_and_keeps_every_row()
    {
        // A real v10 database carrying rows in every table it had, two workspaces among them. The v11 step adds
        // the default project, a project column on workspaces and five EMPTY work item tables: an upgrade that
        // invented work items would be inventing the backlog of the owner.
        var factory = NewFactory();
        BuildHistoricalDatabase(factory, 10);
        using (var seed = factory.Open())
        {
            using var insert = seed.CreateCommand();
            insert.CommandText =
                "INSERT INTO workspaces (id, name, root_path, last_opened_utc) VALUES ('ws2', 'beta', '/repos/beta', '2026-02-01T00:00:00Z');";
            insert.ExecuteNonQuery();
        }

        SqliteSchema.EnsureCreated(factory);

        using var connection = factory.Open();
        SchemaMigrator.StoredVersion(connection).Should().Be(SqliteSchema.CurrentVersion);
        foreach (var table in WorkItemTables)
        {
            TableExists(connection, table).Should().BeTrue();
        }

        Scalar(connection, "SELECT COUNT(*) FROM projects;").Should().Be(1L);
        Scalar(connection, "SELECT name FROM projects WHERE id = 'default';").Should().Be("Default");
        Scalar(connection, "SELECT next_item_number FROM projects WHERE id = 'default';").Should().Be(1L);
        foreach (var table in WorkItemTables.Skip(1))
        {
            Scalar(connection, $"SELECT COUNT(*) FROM {table};").Should().Be(0L, $"'{table}' arrives empty");
        }

        ColumnExists(connection, "workspaces", "project_id").Should().BeTrue();
        // v11 put both in the default project; v48 (item #373, no shared bucket) leaves them not set up, remembering it.
        Scalar(connection, "SELECT COUNT(*) FROM workspaces WHERE project_id IS NULL AND former_project_id = 'default';")
            .Should().Be(2L);

        // Every row that was there is untouched.
        Scalar(connection, "SELECT name FROM workspaces WHERE id = 'ws1';").Should().Be("alpha");
        Scalar(connection, "SELECT root_path FROM workspaces WHERE id = 'ws2';").Should().Be("/repos/beta");
        Scalar(connection, "SELECT title FROM agent_sessions WHERE id = 's1';").Should().Be("first session");
        Scalar(connection, "SELECT value FROM settings WHERE key = 'Tracker:Provider';").Should().Be("github");
        Scalar(connection, "SELECT kind FROM product_events WHERE id = 1;").Should().Be("OnboardingCompleted");
        Scalar(connection, "SELECT provider FROM git_hosts WHERE host = 'github.com';").Should().Be("github");

        // And the new shape is live through the production stores.
        new SqliteWorkspaceStore(factory).Get("ws1")!.ProjectId.Should().BeNull();
        new SqliteWorkspaceStore(factory).Get("ws1")!.FormerProjectId.Should().Be("default");
        new SqliteProjectStore(factory, TimeProvider.System).WorkspacesOf(Fravashi.Domain.Projects.ProjectId.Default)
            .Should().BeEmpty();
        var board = new Fravashi.Infrastructure.WorkItems.SqliteLocalBoard(
            Fravashi.Domain.Projects.ProjectId.Default, factory, TimeProvider.System);
        var created = await board.CreateAsync(
            new Fravashi.Application.WorkItems.NewWorkItem
            {
                Level = Fravashi.Domain.WorkItems.WorkItemLevel.Task,
                Title = "First item",
                Description = "The first item on an upgraded install.",
            },
            Fravashi.Application.WorkItems.MutationContext.User);
        created.IsOk.Should().BeTrue();
        created.Value.Number.Should().Be(1);
    }

    [Fact]
    public void The_work_item_step_is_idempotent_against_an_inaccurate_version_marker()
    {
        // A database whose v11 shape is already present (tables, seeded project, workspace column, a work item)
        // but whose marker says v10: the step must create nothing twice, seed nothing twice and add no column twice.
        var factory = NewFactory();
        SqliteSchema.EnsureCreated(factory);
        using (var seed = factory.Open())
        {
            using var command = seed.CreateCommand();
            command.CommandText = """
                INSERT INTO workspaces (id, name, root_path, last_opened_utc, project_id) VALUES ('ws1', 'alpha', '/repos/alpha', '2026-01-01T00:00:00Z', 'p2');
                INSERT INTO projects (id, name, next_item_number, created_utc, updated_utc) VALUES ('p2', 'Second', 4, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
                UPDATE projects SET name = 'Renamed default', next_item_number = 7 WHERE id = 'default';
                INSERT INTO work_items (id, project_id, number, level, kind, title, description, status, rank, created_utc, updated_utc)
                    VALUES ('w1', 'default', 6, 'task', 'standard', 'kept', 'd', 'triage', 1.0, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
                UPDATE settings SET value = '10' WHERE key = '__schema_version';
                """;
            command.ExecuteNonQuery();
        }

        var migrate = () => SqliteSchema.EnsureCreated(factory);

        migrate.Should().NotThrow();
        using var connection = factory.Open();
        SchemaMigrator.StoredVersion(connection).Should().Be(SqliteSchema.CurrentVersion);
        Scalar(connection, "SELECT COUNT(*) FROM projects;").Should().Be(2L);
        Scalar(connection, "SELECT name FROM projects WHERE id = 'default';").Should().Be("Renamed default", "the seed is INSERT OR IGNORE");
        Scalar(connection, "SELECT next_item_number FROM projects WHERE id = 'default';").Should().Be(7L);
        Scalar(connection, "SELECT project_id FROM workspaces WHERE id = 'ws1';").Should().Be("p2", "a replay never resets a binding");
        Scalar(connection, "SELECT title FROM work_items WHERE id = 'w1';").Should().Be("kept");
    }

    [Fact]
    public void Upgrading_an_old_database_migrates_its_shape_rather_than_only_stamping_it()
    {
        // A v1 database that predates every later table. The old bug stamped the version regardless while
        // leaving the shape untouched for anything IF-NOT-EXISTS could not do; here the proof is stronger
        // than "tables appeared" (those are new-table creates the old code also did): a v6-planted secret
        // row is REWRITTEN by the v7 data transform on the way up, which is a migration a stamp is not.
        var factory = NewFactory();
        BuildHistoricalDatabase(factory, 6);

        using (var before = factory.Open())
        {
            SchemaMigrator.StoredVersion(before).Should().Be(6);
            var raw = (string)Scalar(before,
                "SELECT payload FROM agent_session_events WHERE kind = 'tool_completed';")!;
            raw.Should().Contain(FakeKey, "the fixture plants the raw secret so the transform has work to do");
        }

        SqliteSchema.EnsureCreated(factory);

        using var after = factory.Open();
        SchemaMigrator.StoredVersion(after).Should().Be(SqliteSchema.CurrentVersion);
        var migrated = (string)Scalar(after,
            "SELECT payload FROM agent_session_events WHERE kind = 'tool_completed';")!;
        migrated.Should().NotContain(FakeKey);
        migrated.Should().Contain("[redacted:ANTHROPIC_API_KEY]");
    }

    // -------------------------------------------------------------------------------------------------
    // v8: the per-session usage summary is backfilled from the last stats event (backlog 19)
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void Upgrading_backfills_each_sessions_usage_summary_from_its_last_stats_event()
    {
        // A v6 database (agent_sessions with the pre-usage shape) carrying two sessions, each with stats
        // events. The CLI's snapshots are cumulative, so a correct backfill takes the LAST one per session,
        // not the first and not a sum. Stamped v7 so the upgrade runs exactly the v8 step under test.
        var factory = NewFactory();
        BuildSessionsWithStats(factory);

        SqliteSchema.EnsureCreated(factory);

        using var connection = factory.Open();
        SchemaMigrator.StoredVersion(connection).Should().Be(SqliteSchema.CurrentVersion);

        // Session A: last snapshot wins (0.05 over the earlier 0.02; 300 in, 600 out).
        Scalar(connection, "SELECT cost_usd FROM agent_sessions WHERE id = 'a';").Should().Be("0.05");
        Scalar(connection, "SELECT input_tokens FROM agent_sessions WHERE id = 'a';").Should().Be(300L);
        Scalar(connection, "SELECT output_tokens FROM agent_sessions WHERE id = 'a';").Should().Be(600L);

        // Session B: its single snapshot.
        Scalar(connection, "SELECT cost_usd FROM agent_sessions WHERE id = 'b';").Should().Be("0.01");
        Scalar(connection, "SELECT input_tokens FROM agent_sessions WHERE id = 'b';").Should().Be(10L);
        Scalar(connection, "SELECT output_tokens FROM agent_sessions WHERE id = 'b';").Should().Be(20L);

        // A session that never reported usage keeps the column defaults rather than a null.
        Scalar(connection, "SELECT cost_usd FROM agent_sessions WHERE id = 'c';").Should().Be("0");
        Scalar(connection, "SELECT input_tokens FROM agent_sessions WHERE id = 'c';").Should().Be(0L);

        // The backfilled figures reconcile with what a totals read sums (the whole point of the column).
        var store = new SqliteSessionStore(factory);
        var totals = store.ReadUsageTotals(
            new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));
        totals.AllTime.CostUsd.Should().Be(0.06m);
        totals.AllTime.TotalTokens.Should().Be(300 + 600 + 10 + 20);
        totals.AllTime.Sessions.Should().Be(3);
    }

    private static void BuildSessionsWithStats(SqliteConnectionFactory factory)
    {
        using var connection = factory.Open();

        void Exec(string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        // Just enough of the historical chain for agent_sessions/events to exist at their v6 shape.
        Exec("""
            CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE agent_sessions (
                id TEXT PRIMARY KEY, workspace_id TEXT NOT NULL, tool TEXT NOT NULL, title TEXT NOT NULL,
                provider_session_id TEXT, state TEXT NOT NULL, started_utc TEXT NOT NULL, ended_utc TEXT);
            CREATE TABLE agent_session_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL, kind TEXT NOT NULL,
                payload TEXT NOT NULL, created_utc TEXT NOT NULL);

            INSERT INTO agent_sessions (id, workspace_id, tool, title, state, started_utc) VALUES
                ('a', 'ws1', 'claude', 'A', 'ended', '2026-08-01T00:00:00+00:00'),
                ('b', 'ws2', 'claude', 'B', 'ended', '2026-08-02T00:00:00+00:00'),
                ('c', 'ws1', 'claude', 'C', 'ended', '2026-08-03T00:00:00+00:00');

            INSERT INTO agent_session_events (session_id, kind, payload, created_utc) VALUES
                ('a', 'stats', '{"costUsd":0.02,"inputTokens":100,"outputTokens":200,"cacheReadTokens":5,"toolCalls":1}', '2026-08-01T00:01:00+00:00'),
                ('a', 'stats', '{"costUsd":0.05,"inputTokens":300,"outputTokens":600,"cacheReadTokens":9,"toolCalls":3}', '2026-08-01T00:02:00+00:00'),
                ('b', 'stats', '{"costUsd":0.01,"inputTokens":10,"outputTokens":20,"cacheReadTokens":0,"toolCalls":0}', '2026-08-02T00:01:00+00:00'),
                ('a', 'message', '{"detail":"unrelated"}', '2026-08-01T00:03:00+00:00');

            INSERT INTO settings (key, value) VALUES ('__schema_version', '7');
            """);
    }

    // -------------------------------------------------------------------------------------------------
    // Adding a column to an existing populated table
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void A_migration_step_can_add_a_column_to_an_existing_populated_table()
    {
        var factory = NewFactory();
        SqliteSchema.EnsureCreated(factory); // at CurrentVersion, real current tables

        using (var seed = factory.Open())
        {
            using var insert = seed.CreateCommand();
            insert.CommandText =
                "INSERT INTO workspaces (id, name, root_path, last_opened_utc) " +
                "VALUES ('ws1', 'alpha', '/repos/alpha', '2026-08-09T00:00:00Z');";
            insert.ExecuteNonQuery();
        }

        // A hypothetical next shipped version that adds a column to an EXISTING, populated table, exactly
        // what the old bulk-create-then-stamp could never do. The step is idempotent-safe (it probes first).
        var next = SqliteSchema.CurrentVersion + 1;
        var chain = SqliteSchema.Migrations
            .Append(new SchemaMigration(next, AddDescriptionColumnToWorkspaces))
            .ToList();

        using (var connection = factory.Open())
        {
            SchemaMigrator.Run(connection, chain);
        }

        using var check = factory.Open();
        SchemaMigrator.StoredVersion(check).Should().Be(next, "the new step stamps its own version");
        ColumnExists(check, "workspaces", "description").Should().BeTrue("the ALTER TABLE step added the column");

        // The pre-existing row survived the column addition; the new column is NULL for it.
        Scalar(check, "SELECT name FROM workspaces WHERE id = 'ws1';").Should().Be("alpha");
        Scalar(check, "SELECT description FROM workspaces WHERE id = 'ws1';").Should().BeNull();
    }

    private static void AddDescriptionColumnToWorkspaces(SqliteConnection connection, SqliteTransaction transaction)
    {
        // Idempotent-safe: SQLite has no ADD COLUMN IF NOT EXISTS, so probe the table before altering.
        if (ColumnExists(connection, "workspaces", "description"))
        {
            return;
        }

        SchemaMigrator.Execute(connection, transaction, "ALTER TABLE workspaces ADD COLUMN description TEXT;");
    }

    // -------------------------------------------------------------------------------------------------
    // Raw historical fixtures (hand-written, independent of the production chain)
    // -------------------------------------------------------------------------------------------------

    private static readonly string[] AllCurrentTables =
    [
        "settings", "provider_connections", "secrets", "onboarding_state",
        "framework_artifacts", "framework_resources", "wiki_pages",
        "workspaces", "git_hosts", "agent_sessions", "agent_session_events",
        "product_events", "agent_session_usage_days",
        "projects", "work_items", "work_item_links", "work_item_events", "work_item_operations",
        "work_item_criteria", "work_item_readiness", "work_item_person_blockers", "work_item_refs",
        "work_item_scope_patterns", "work_item_scenarios", "work_item_scenario_steps", "work_item_evidence",
        "agent_session_work_items",
        "wiki_items", "wiki_item_links", "wiki_item_refs", "wiki_state", "wiki_item_revisions",
        "agent_session_plans", "agent_session_changed_files", "agent_session_roles",
        "agent_session_branches", "agent_session_attention",
        "work_item_mirror", "work_item_outbox", "work_item_conflicts",
        "autopilot_runs", "autopilot_run_items", "autopilot_run_stops",
        "project_delivery", "work_item_attachments", "releases", "release_items",
    ];

    /// <summary>
    /// Builds a database at exactly the shape and content a given historical version had, using raw SQL:
    /// only the tables that existed at <paramref name="version"/> are created, each gets a representative
    /// row, and the version marker is stamped to match (v0 leaves no marker and no tables at all).
    /// </summary>
    private static void BuildHistoricalDatabase(SqliteConnectionFactory factory, int version)
    {
        using var connection = factory.Open();

        void Exec(string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        if (version == 0)
        {
            return; // a database that predates the very first schema: no tables, no marker.
        }

        // v1
        Exec("""
            CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE provider_connections (
                id TEXT PRIMARY KEY, kind TEXT NOT NULL, base_url TEXT, credential_ref TEXT,
                status TEXT NOT NULL, updated_utc TEXT NOT NULL);
            CREATE TABLE secrets (
                reference TEXT PRIMARY KEY, protected_value TEXT NOT NULL, updated_utc TEXT NOT NULL);
            CREATE TABLE onboarding_state (
                id INTEGER PRIMARY KEY CHECK (id = 1), completed INTEGER NOT NULL DEFAULT 0,
                current_step TEXT, started_utc TEXT, completed_utc TEXT);

            INSERT INTO settings (key, value) VALUES ('Tracker:Provider', 'github');
            INSERT INTO provider_connections (id, kind, status, updated_utc)
                VALUES ('pc1', 'github', 'connected', '2026-01-01T00:00:00Z');
            INSERT INTO secrets (reference, protected_value, updated_utc)
                VALUES ('ref1', 'enc:abc', '2026-01-01T00:00:00Z');
            INSERT INTO onboarding_state (id, completed) VALUES (1, 1);
            """);

        if (version >= 2)
        {
            Exec("""
                CREATE TABLE framework_artifacts (
                    id TEXT PRIMARY KEY, kind TEXT NOT NULL, name TEXT NOT NULL, payload_json TEXT NOT NULL,
                    created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL);
                CREATE UNIQUE INDEX ux_framework_artifacts_kind_name ON framework_artifacts (kind, name);
                CREATE TABLE framework_resources (
                    id TEXT PRIMARY KEY, artifact_id TEXT NOT NULL, file_name TEXT NOT NULL,
                    content_type TEXT, bytes BLOB NOT NULL, size_bytes INTEGER NOT NULL, created_utc TEXT NOT NULL);
                CREATE INDEX ix_framework_resources_artifact ON framework_resources (artifact_id);

                INSERT INTO framework_artifacts (id, kind, name, payload_json, created_utc, updated_utc)
                    VALUES ('fa1', 'skill', 'skill-x', '{}', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
                INSERT INTO framework_resources (id, artifact_id, file_name, bytes, size_bytes, created_utc)
                    VALUES ('fr1', 'fa1', 'a.txt', x'01020304', 4, '2026-01-01T00:00:00Z');
                """);
        }

        if (version >= 3)
        {
            Exec("""
                CREATE TABLE wiki_pages (
                    id TEXT PRIMARY KEY, space TEXT NOT NULL, title TEXT NOT NULL, slug TEXT NOT NULL,
                    body TEXT NOT NULL, format TEXT NOT NULL, source_ref TEXT, source_hash TEXT NOT NULL,
                    local_hash TEXT NOT NULL, has_conflict INTEGER NOT NULL DEFAULT 0, updated_utc TEXT NOT NULL);
                CREATE UNIQUE INDEX ux_wiki_pages_space_slug ON wiki_pages (space, slug);

                INSERT INTO wiki_pages (id, space, title, slug, body, format, source_hash, local_hash, updated_utc)
                    VALUES ('wp1', 'DEV', 'Home', 'home', '# Home', 'markdown', 'h1', 'h1', '2026-01-01T00:00:00Z');
                """);
        }

        if (version >= 4)
        {
            Exec("""
                CREATE TABLE workspaces (
                    id TEXT PRIMARY KEY, name TEXT NOT NULL, root_path TEXT NOT NULL, last_opened_utc TEXT NOT NULL);

                INSERT INTO workspaces (id, name, root_path, last_opened_utc)
                    VALUES ('ws1', 'alpha', '/repos/alpha', '2026-01-01T00:00:00Z');
                """);
        }

        if (version >= 5)
        {
            Exec("""
                CREATE TABLE git_hosts (
                    host TEXT PRIMARY KEY, provider TEXT NOT NULL, credential_ref TEXT NOT NULL, updated_utc TEXT NOT NULL);

                INSERT INTO git_hosts (host, provider, credential_ref, updated_utc)
                    VALUES ('github.com', 'github', 'gh-ref', '2026-01-01T00:00:00Z');
                """);
        }

        if (version >= 6)
        {
            Exec("""
                CREATE TABLE agent_sessions (
                    id TEXT PRIMARY KEY, workspace_id TEXT NOT NULL, tool TEXT NOT NULL, title TEXT NOT NULL,
                    provider_session_id TEXT, state TEXT NOT NULL, started_utc TEXT NOT NULL, ended_utc TEXT);
                CREATE INDEX ix_agent_sessions_workspace ON agent_sessions (workspace_id, started_utc DESC);
                CREATE TABLE agent_session_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL, kind TEXT NOT NULL,
                    payload TEXT NOT NULL, created_utc TEXT NOT NULL);
                CREATE INDEX ix_agent_session_events_session ON agent_session_events (session_id, id);

                INSERT INTO agent_sessions (id, workspace_id, tool, title, state, started_utc)
                    VALUES ('s1', 'ws1', 'claude', 'first session', 'ended', '2026-01-01T00:00:00Z');
                """);

            // A row printed before the redactor existed (exercises the v7 transform on upgrade), plus an
            // ordinary row that must stay byte-identical.
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO agent_session_events (session_id, kind, payload, created_utc) VALUES " +
                "('s1', 'tool_completed', $secret, '2026-01-01T00:00:00Z'), " +
                "('s1', 'text', '{\"detail\":\"just a normal line\"}', '2026-01-01T00:00:00Z');";
            command.Parameters.AddWithValue(
                "$secret",
                "{\"toolName\":\"Bash\",\"detail\":\"ANTHROPIC_API_KEY=" + FakeKey + "\"}");
            command.ExecuteNonQuery();
        }

        if (version >= 8)
        {
            // The v8 usage summary, in the shape that step shipped (backlog 19).
            Exec("""
                ALTER TABLE agent_sessions ADD COLUMN cost_usd TEXT NOT NULL DEFAULT '0';
                ALTER TABLE agent_sessions ADD COLUMN input_tokens INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE agent_sessions ADD COLUMN output_tokens INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE agent_sessions ADD COLUMN cache_read_tokens INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE agent_sessions ADD COLUMN tool_calls INTEGER NOT NULL DEFAULT 0;
                """);
        }

        if (version >= 9)
        {
            // The v9 custom title (backlog 109), in the shape that step shipped.
            Exec("ALTER TABLE agent_sessions ADD COLUMN custom_title TEXT;");
        }

        if (version >= 10)
        {
            // The v10 product events table (backlog 74), in the shape that step shipped, with one recorded event.
            Exec("""
                CREATE TABLE product_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL, occurred_utc TEXT NOT NULL,
                    outcome TEXT NOT NULL, workspace_hash TEXT, duration_ms INTEGER, count INTEGER);
                CREATE INDEX ix_product_events_kind ON product_events (kind, occurred_utc);

                INSERT INTO product_events (kind, occurred_utc, outcome, duration_ms)
                    VALUES ('OnboardingCompleted', '2026-09-13T12:00:00.0000000+00:00', 'Succeeded', 420000);
                """);
        }

        if (version >= 11)
        {
            // The v11 work item tables (backlog 37 increment B), in the shape that step shipped, with one
            // project, one item and one event, so the agent-layer step has real rows to leave alone.
            Exec("""
                CREATE TABLE projects (
                    id TEXT PRIMARY KEY, name TEXT NOT NULL, next_item_number INTEGER NOT NULL DEFAULT 1,
                    created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL);
                INSERT INTO projects (id, name, next_item_number, created_utc, updated_utc)
                    VALUES ('default', 'Default', 2, '1970-01-01T00:00:00Z', '1970-01-01T00:00:00Z');

                CREATE TABLE work_items (
                    id TEXT PRIMARY KEY, project_id TEXT NOT NULL, number INTEGER NOT NULL, level TEXT NOT NULL,
                    kind TEXT NOT NULL, title TEXT NOT NULL, description TEXT NOT NULL, parent_id TEXT,
                    status TEXT NOT NULL, resolution TEXT, blocked INTEGER NOT NULL DEFAULT 0, blocked_reason TEXT,
                    deferred INTEGER NOT NULL DEFAULT 0, deferred_reason TEXT, priority TEXT, assignee TEXT,
                    rank REAL NOT NULL, spike_timebox_minutes INTEGER, external_connection TEXT, external_key TEXT,
                    external_status TEXT, version INTEGER NOT NULL DEFAULT 1, created_utc TEXT NOT NULL,
                    updated_utc TEXT NOT NULL);
                INSERT INTO work_items (id, project_id, number, level, kind, title, description, status, rank,
                    version, created_utc, updated_utc)
                    VALUES ('item-1', 'default', 1, 'increment', 'standard', 'An item that predates the agent layer',
                        'Its rows must survive the upgrade.', 'backlog', 1000.0, 3,
                        '2026-09-15T10:00:00.0000000+00:00', '2026-09-15T11:00:00.0000000+00:00');

                CREATE TABLE work_item_links (
                    project_id TEXT NOT NULL, source_id TEXT NOT NULL, type TEXT NOT NULL, target_id TEXT NOT NULL,
                    created_utc TEXT NOT NULL);

                CREATE TABLE work_item_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, project_id TEXT NOT NULL, item_id TEXT NOT NULL,
                    version INTEGER NOT NULL, operation TEXT NOT NULL, actor_kind TEXT NOT NULL, actor_id TEXT,
                    operation_id TEXT, changes_json TEXT NOT NULL, at_utc TEXT NOT NULL);
                INSERT INTO work_item_events (project_id, item_id, version, operation, actor_kind, changes_json, at_utc)
                    VALUES ('default', 'item-1', 1, 'created', 'user', '{}', '2026-09-15T10:00:00.0000000+00:00');

                CREATE TABLE work_item_operations (
                    project_id TEXT NOT NULL, operation_id TEXT NOT NULL, operation TEXT NOT NULL, item_id TEXT,
                    link_json TEXT, at_utc TEXT NOT NULL);
                """);

            // Step 11 also bound every existing workspace to the seeded project, so a database that really
            // stood at v11 already carries the column.
            if (version >= 4)
            {
                Exec("ALTER TABLE workspaces ADD COLUMN project_id TEXT NOT NULL DEFAULT 'default';");
            }
        }

        if (version >= 12)
        {
            // The v12 usage-days table (backlog 62), in the shape that step shipped.
            Exec("""
                CREATE TABLE agent_session_usage_days (
                    session_id TEXT NOT NULL, day_utc TEXT NOT NULL, cost_usd TEXT NOT NULL,
                    input_tokens INTEGER NOT NULL DEFAULT 0, output_tokens INTEGER NOT NULL DEFAULT 0,
                    cache_read_tokens INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (session_id, day_utc));
                CREATE INDEX ix_agent_session_usage_days_day ON agent_session_usage_days (day_utc);
                """);
        }

        if (version >= 13)
        {
            // The v13 agent layer (backlog 37 increment C1), in the shape that step shipped, with one
            // criterion on the item so the evidence step has a pointer target to leave alone.
            Exec("""
                ALTER TABLE work_items ADD COLUMN ambiguity_score INTEGER;
                ALTER TABLE work_items ADD COLUMN ambiguity_assessed_by TEXT;
                ALTER TABLE work_items ADD COLUMN ambiguity_assessed_utc TEXT;
                ALTER TABLE work_items ADD COLUMN change_budget_max_files INTEGER;
                ALTER TABLE work_items ADD COLUMN next_action TEXT;

                CREATE TABLE work_item_criteria (
                    id TEXT PRIMARY KEY, item_id TEXT NOT NULL, ordinal INTEGER NOT NULL, text TEXT NOT NULL);
                INSERT INTO work_item_criteria (id, item_id, ordinal, text)
                    VALUES ('crit-1', 'item-1', 0, 'It works.');

                CREATE TABLE work_item_readiness (
                    id TEXT PRIMARY KEY, item_id TEXT NOT NULL, ordinal INTEGER NOT NULL, text TEXT NOT NULL,
                    checked INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE work_item_person_blockers (
                    id TEXT PRIMARY KEY, item_id TEXT NOT NULL, person TEXT NOT NULL, reason TEXT NOT NULL,
                    created_utc TEXT NOT NULL, cleared_utc TEXT);
                CREATE TABLE work_item_refs (
                    id TEXT PRIMARY KEY, item_id TEXT NOT NULL, role TEXT NOT NULL, kind TEXT NOT NULL,
                    target TEXT NOT NULL, workspace_id TEXT, label TEXT, note TEXT, ordinal INTEGER NOT NULL);
                CREATE TABLE work_item_scope_patterns (
                    id TEXT PRIMARY KEY, item_id TEXT NOT NULL, mode TEXT NOT NULL, pattern TEXT NOT NULL,
                    workspace_id TEXT);
                CREATE TABLE work_item_scenarios (
                    id TEXT PRIMARY KEY, item_id TEXT NOT NULL, ordinal INTEGER NOT NULL, title TEXT NOT NULL);
                CREATE TABLE work_item_scenario_steps (
                    id TEXT PRIMARY KEY, scenario_id TEXT NOT NULL, ordinal INTEGER NOT NULL,
                    action TEXT NOT NULL, expected TEXT NOT NULL, data_json TEXT);
                """);
        }

        if (version >= 14)
        {
            // The v14 evidence table (backlog 37 increment C2), in the shape that step shipped, with one
            // record pointing at the criterion above so the v15 step has something to leave alone.
            Exec("""
                CREATE TABLE work_item_evidence (
                    id TEXT PRIMARY KEY, item_id TEXT NOT NULL, kind TEXT NOT NULL, gate TEXT,
                    result TEXT NOT NULL, summary TEXT NOT NULL, artefact_ref TEXT, criterion_id TEXT,
                    scenario_id TEXT, details_json TEXT, recorded_utc TEXT NOT NULL,
                    actor_kind TEXT NOT NULL, actor_id TEXT);
                CREATE INDEX ix_work_item_evidence_item ON work_item_evidence (item_id, recorded_utc);
                INSERT INTO work_item_evidence
                    (id, item_id, kind, result, summary, criterion_id, recorded_utc, actor_kind)
                    VALUES ('ev-1', 'item-1', 'test_run', 'pass', 'The suite is green.', 'crit-1',
                            '2026-09-01T00:00:00.0000000+00:00', 'user');
                """);
        }

        if (version >= 15)
        {
            // The v15 session/work-item join (backlog 44), in the shape that step shipped, with one row so
            // the v16 step has something to leave alone.
            Exec("""
                CREATE TABLE agent_session_work_items (
                    session_id TEXT NOT NULL, item_id TEXT NOT NULL, project_id TEXT NOT NULL,
                    linked_utc TEXT NOT NULL, linked_by TEXT NOT NULL,
                    PRIMARY KEY (session_id, item_id));
                CREATE INDEX ix_agent_session_work_items_item ON agent_session_work_items (item_id);
                INSERT INTO agent_session_work_items (session_id, item_id, project_id, linked_utc, linked_by)
                    VALUES ('s-1', 'item-1', 'default', '2026-09-01T00:00:00.0000000+00:00', 'user');
                """);
        }

        if (version >= 16)
        {
            // The v16 authored wiki (backlog 39), in the shape that step shipped, with one document so the
            // v17 step has something to leave alone.
            Exec("""
                CREATE TABLE wiki_items (
                    id TEXT PRIMARY KEY, number INTEGER NOT NULL, type TEXT NOT NULL, title TEXT NOT NULL,
                    slug TEXT NOT NULL, body TEXT NOT NULL, version INTEGER NOT NULL,
                    created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL);
                CREATE UNIQUE INDEX ux_wiki_items_number ON wiki_items (number);
                CREATE UNIQUE INDEX ux_wiki_items_slug ON wiki_items (slug);
                CREATE INDEX ix_wiki_items_type ON wiki_items (type);
                CREATE TABLE wiki_item_links (
                    id TEXT PRIMARY KEY, item_id TEXT NOT NULL, kind TEXT NOT NULL, target TEXT NOT NULL,
                    label TEXT, ordinal INTEGER NOT NULL);
                CREATE INDEX ix_wiki_item_links_item ON wiki_item_links (item_id, ordinal);
                CREATE INDEX ix_wiki_item_links_target ON wiki_item_links (kind, target);
                CREATE TABLE wiki_item_refs (
                    item_id TEXT NOT NULL, kind TEXT NOT NULL, number INTEGER NOT NULL,
                    PRIMARY KEY (item_id, kind, number));
                CREATE INDEX ix_wiki_item_refs_target ON wiki_item_refs (kind, number);
                CREATE TABLE wiki_state (id INTEGER PRIMARY KEY CHECK (id = 1), next_number INTEGER NOT NULL);
                INSERT INTO wiki_state (id, next_number) VALUES (1, 2);
                INSERT INTO wiki_items (id, number, type, title, slug, body, version, created_utc, updated_utc)
                    VALUES ('wiki-1', 1, 'plan', 'An older plan', 'an-older-plan', 'About #1.', 1,
                            '2026-09-01T00:00:00.0000000+00:00', '2026-09-01T00:00:00.0000000+00:00');
                """);
        }

        if (version >= 17)
        {
            // The v17 session/plan binding (backlog 45), in the shape that step shipped.
            Exec("""
                CREATE TABLE agent_session_plans (
                    session_id TEXT NOT NULL, wiki_item_id TEXT NOT NULL, linked_utc TEXT NOT NULL,
                    linked_by TEXT NOT NULL, PRIMARY KEY (session_id, wiki_item_id));
                CREATE INDEX ix_agent_session_plans_item ON agent_session_plans (wiki_item_id);
                INSERT INTO agent_session_plans (session_id, wiki_item_id, linked_utc, linked_by)
                    VALUES ('s-1', 'wiki-1', '2026-09-01T00:00:00.0000000+00:00', 'user');
                """);
        }

        if (version >= 18)
        {
            // The v18 approved-change rows (backlog 85), in the shape that step shipped.
            Exec("""
                CREATE TABLE agent_session_changed_files (
                    session_id TEXT NOT NULL, path TEXT NOT NULL, first_utc TEXT NOT NULL,
                    PRIMARY KEY (session_id, path));
                INSERT INTO agent_session_changed_files (session_id, path, first_utc)
                    VALUES ('s-1', 'src/a.cs', '2026-09-01T00:00:00.0000000+00:00');
                """);
        }

        if (version >= 19)
        {
            // The v19 session/role table (backlog 88), in the shape that step shipped.
            Exec("""
                CREATE TABLE agent_session_roles (session_id TEXT PRIMARY KEY, role_id TEXT NOT NULL);
                INSERT INTO agent_session_roles (session_id, role_id) VALUES ('s-1', 'verifier');
                """);
        }

        if (version >= 20)
        {
            // The v20 column (backlog 55) is added to a table v13 created, so an older build that already
            // has it must not have it added twice; the step probes, and this is what makes that probe count.
            Exec("ALTER TABLE work_item_scenario_steps ADD COLUMN command TEXT;");
        }

        // Stamp the version the way every historical build did: a plain settings row.
        using (var stamp = connection.CreateCommand())
        {
            stamp.CommandText =
                "INSERT INTO settings (key, value) VALUES ($k, $v) " +
                "ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
            stamp.Parameters.AddWithValue("$k", SqliteSchema.SchemaVersionKey);
            stamp.Parameters.AddWithValue("$v", version.ToString());
            stamp.ExecuteNonQuery();
        }
    }

    // -------------------------------------------------------------------------------------------------
    // Small query helpers
    // -------------------------------------------------------------------------------------------------

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $n;";
        command.Parameters.AddWithValue("$n", table);
        return command.ExecuteScalar() is not null;
    }

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = $c;";
        command.Parameters.AddWithValue("$c", column);
        return command.ExecuteScalar() is not null;
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }
}
