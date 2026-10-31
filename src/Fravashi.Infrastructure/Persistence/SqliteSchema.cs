using System.Text.Json;
using System.Text.Json.Nodes;
using Fravashi.Infrastructure.Sessions;
using Microsoft.Data.Sqlite;

namespace Fravashi.Infrastructure.Persistence;

/// <summary>
/// The Fravashi database schema, expressed as an ordered, append-only migration chain and run on every
/// open (backlog 60). Each shipped version is one <see cref="SchemaMigration"/> step: the delta from the
/// previous version. A fresh database replays the whole chain and lands at <see cref="CurrentVersion"/>;
/// an existing database runs only the steps above its recorded version; a database already at the top
/// runs none. The version marker is stamped inside each step's own transaction, so it is a consequence of
/// a completed step rather than an unconditional write, so an older database is migrated, not stamped.
/// <para>
/// Every table-creating step guards with <c>IF NOT EXISTS</c>, which is what silently kept pre-marker
/// databases alive before this framework existed and is kept here as a belt-and-braces baseline: a step
/// may replay against a database whose recorded version is inaccurate. When the schema evolves, ADD a step
/// with the next version number; never edit a shipped step's SQL (databases in the field have run it) and
/// never bump <see cref="CurrentVersion"/> without the matching step. <see cref="SchemaMigrator"/> does
/// column additions and data transforms the old bulk <c>CREATE TABLE IF NOT EXISTS</c> could not.
/// </para>
/// </summary>
public static class SqliteSchema
{
    /// <summary>The settings-table key under which the applied schema version is recorded.</summary>
    public const string SchemaVersionKey = SchemaMigrator.SchemaVersionKey;

    /// <summary>
    /// The current schema version: the target version of the last step in <see cref="Migrations"/>. v7 is
    /// not a table change: it marks the one-time transcript redaction scrub (backlog 102) as done, so a
    /// database opened at v7 or later is never scrubbed again. v8 (backlog 19) adds per-session usage
    /// summary columns to <c>agent_sessions</c> and backfills them from each session's last stats event,
    /// so cross-session cost and token totals are a cheap aggregate that reconciles with the sessions. v9
    /// (backlog 109) adds a nullable <c>custom_title</c> column to <c>agent_sessions</c> for a title the
    /// owner chose; null on every existing row, which is what keeps the derived title as the fallback.
    /// v10 (backlog 74) adds the <c>product_events</c> table for locally recorded product events; it is
    /// created empty on every database, because recording is opt-in and off until someone switches it on.
    /// v11 (backlog 37) adds projects with the seeded <c>default</c> project, <c>workspaces.project_id</c>
    /// (every existing workspace lands in <c>default</c>), and the work item tables: <c>work_items</c>,
    /// <c>work_item_links</c>, <c>work_item_events</c> and the <c>work_item_operations</c> idempotency ledger,
    /// all created empty. v12 (backlog 62, resume in place) adds <c>agent_session_usage_days</c>: each
    /// session's spending split by the UTC day it was INCURRED on, backfilled from the stats events' own
    /// stamps. A resumed conversation keeps its original start, so bucketing a whole session by
    /// <c>started_utc</c> hid today's spending on yesterday's conversation from the day and month ceilings.
    /// v13 (backlog 37, increment C1) adds the agent layer: five columns on <c>work_items</c> (the ambiguity
    /// assessment, the change budget's file ceiling and the next action) and the tables behind the lists that
    /// something else points at, <c>work_item_criteria</c>, <c>work_item_readiness</c>,
    /// <c>work_item_person_blockers</c>, <c>work_item_refs</c>, <c>work_item_scope_patterns</c>,
    /// <c>work_item_scenarios</c> and <c>work_item_scenario_steps</c>, all created empty. v14 (backlog 37,
    /// increment C2) adds <c>work_item_evidence</c>, the append-only record of what was checked, also empty.
    /// </summary>
    public const int CurrentVersion = 50;

    // --- Per-version deltas -------------------------------------------------------------------------
    // Each constant is exactly the DDL/DML that a single shipped schema bump added. They are split out of
    // the former single bulk create-script so that the fresh-install path and the upgrade path are one and
    // the same ordered chain. Do not edit a shipped fragment; add a new one and a new step.

    private const string V1BaseTables = """
        CREATE TABLE IF NOT EXISTS settings (
            key   TEXT PRIMARY KEY,
            value TEXT
        );

        CREATE TABLE IF NOT EXISTS provider_connections (
            id             TEXT PRIMARY KEY,
            kind           TEXT NOT NULL,
            base_url       TEXT,
            credential_ref TEXT,
            status         TEXT NOT NULL,
            updated_utc    TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS secrets (
            reference       TEXT PRIMARY KEY,
            protected_value TEXT NOT NULL,
            updated_utc     TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS onboarding_state (
            id            INTEGER PRIMARY KEY CHECK (id = 1),
            completed     INTEGER NOT NULL DEFAULT 0,
            current_step  TEXT,
            started_utc   TEXT,
            completed_utc TEXT
        );

        INSERT OR IGNORE INTO onboarding_state (id, completed) VALUES (1, 0);
        """;

    private const string V2FrameworkStore = """
        CREATE TABLE IF NOT EXISTS framework_artifacts (
            id           TEXT PRIMARY KEY,
            kind         TEXT NOT NULL,
            name         TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            created_utc  TEXT NOT NULL,
            updated_utc  TEXT NOT NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_framework_artifacts_kind_name
            ON framework_artifacts (kind, name);

        CREATE TABLE IF NOT EXISTS framework_resources (
            id           TEXT PRIMARY KEY,
            artifact_id  TEXT NOT NULL,
            file_name    TEXT NOT NULL,
            content_type TEXT,
            bytes        BLOB NOT NULL,
            size_bytes   INTEGER NOT NULL,
            created_utc  TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_framework_resources_artifact
            ON framework_resources (artifact_id);
        """;

    private const string V3WikiStore = """
        CREATE TABLE IF NOT EXISTS wiki_pages (
            id           TEXT PRIMARY KEY,
            space        TEXT NOT NULL,
            title        TEXT NOT NULL,
            slug         TEXT NOT NULL,
            body         TEXT NOT NULL,
            format       TEXT NOT NULL,
            source_ref   TEXT,
            source_hash  TEXT NOT NULL,
            local_hash   TEXT NOT NULL,
            has_conflict INTEGER NOT NULL DEFAULT 0,
            updated_utc  TEXT NOT NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_wiki_pages_space_slug
            ON wiki_pages (space, slug);
        """;

    private const string V4Workspaces = """
        CREATE TABLE IF NOT EXISTS workspaces (
            id              TEXT PRIMARY KEY,
            name            TEXT NOT NULL,
            root_path       TEXT NOT NULL,
            last_opened_utc TEXT NOT NULL
        );
        """;

    private const string V5GitHosts = """
        CREATE TABLE IF NOT EXISTS git_hosts (
            host           TEXT PRIMARY KEY,
            provider       TEXT NOT NULL,
            credential_ref TEXT NOT NULL,
            updated_utc    TEXT NOT NULL
        );
        """;

    private const string V6Sessions = """
        CREATE TABLE IF NOT EXISTS agent_sessions (
            id                  TEXT PRIMARY KEY,
            workspace_id        TEXT NOT NULL,
            tool                TEXT NOT NULL,
            title               TEXT NOT NULL,
            provider_session_id TEXT,
            state               TEXT NOT NULL,
            started_utc         TEXT NOT NULL,
            ended_utc           TEXT
        );

        CREATE INDEX IF NOT EXISTS ix_agent_sessions_workspace
            ON agent_sessions (workspace_id, started_utc DESC);

        CREATE TABLE IF NOT EXISTS agent_session_events (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            session_id  TEXT NOT NULL,
            kind        TEXT NOT NULL,
            payload     TEXT NOT NULL,
            created_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_agent_session_events_session
            ON agent_session_events (session_id, id);
        """;

    /// <summary>
    /// Schema step 10 (backlog 74): locally recorded product events. The columns ARE the payload schema
    /// (<see cref="Fravashi.Domain.Telemetry.ProductEvent"/>) and there is deliberately no text column
    /// among them: kind and outcome are enum names from closed sets, the workspace is a one-way hash,
    /// and the rest are numbers. A prompt, a path or a transcript has nowhere to go in this table even
    /// if a future writer tried to put one there.
    /// </summary>
    private const string V10ProductEvents = """
        CREATE TABLE IF NOT EXISTS product_events (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            kind           TEXT NOT NULL,
            occurred_utc   TEXT NOT NULL,
            outcome        TEXT NOT NULL,
            workspace_hash TEXT,
            duration_ms    INTEGER,
            count          INTEGER
        );

        CREATE INDEX IF NOT EXISTS ix_product_events_kind
            ON product_events (kind, occurred_utc);
        """;

    /// <summary>
    /// Schema step 11 (backlog 37, canonical artifact model plan section 5.1, increment B): projects, the
    /// workspace binding, work items, their links, the append-only event log and the operation ledger. The
    /// seeded <c>default</c> project is <c>INSERT OR IGNORE</c>, so a replay adds nothing twice; the
    /// <c>workspaces.project_id</c> column is added by <see cref="AddProjectsAndWorkItems"/> after a probe, with a
    /// default that places every existing workspace in <c>default</c>. Relations are enforced in application code
    /// with no declared FOREIGN KEY, and every enum column holds a stable lower-snake name, never an ordinal.
    /// </summary>
    private const string V11WorkItems = """
        CREATE TABLE IF NOT EXISTS projects (
            id               TEXT PRIMARY KEY,
            name             TEXT NOT NULL,
            next_item_number INTEGER NOT NULL DEFAULT 1,
            created_utc      TEXT NOT NULL,
            updated_utc      TEXT NOT NULL
        );

        INSERT OR IGNORE INTO projects (id, name, next_item_number, created_utc, updated_utc)
        VALUES ('default', 'Default', 1, '1970-01-01T00:00:00Z', '1970-01-01T00:00:00Z');

        CREATE TABLE IF NOT EXISTS work_items (
            id                    TEXT PRIMARY KEY,
            project_id            TEXT NOT NULL,
            number                INTEGER NOT NULL,
            level                 TEXT NOT NULL,
            kind                  TEXT NOT NULL,
            title                 TEXT NOT NULL,
            description           TEXT NOT NULL,
            parent_id             TEXT,
            status                TEXT NOT NULL,
            resolution            TEXT,
            blocked               INTEGER NOT NULL DEFAULT 0,
            blocked_reason        TEXT,
            deferred              INTEGER NOT NULL DEFAULT 0,
            deferred_reason       TEXT,
            priority              TEXT,
            assignee              TEXT,
            rank                  REAL NOT NULL,
            spike_timebox_minutes INTEGER,
            external_connection   TEXT,
            external_key          TEXT,
            external_status       TEXT,
            version               INTEGER NOT NULL DEFAULT 1,
            created_utc           TEXT NOT NULL,
            updated_utc           TEXT NOT NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_work_items_project_number ON work_items (project_id, number);
        CREATE INDEX IF NOT EXISTS ix_work_items_board ON work_items (project_id, status, rank);
        CREATE INDEX IF NOT EXISTS ix_work_items_parent ON work_items (parent_id);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_work_items_external
            ON work_items (project_id, external_connection, external_key) WHERE external_key IS NOT NULL;

        CREATE TABLE IF NOT EXISTS work_item_links (
            project_id  TEXT NOT NULL,
            source_id   TEXT NOT NULL,
            type        TEXT NOT NULL,
            target_id   TEXT NOT NULL,
            created_utc TEXT NOT NULL,
            created_by  TEXT NOT NULL,
            PRIMARY KEY (source_id, type, target_id)
        );
        CREATE INDEX IF NOT EXISTS ix_work_item_links_target ON work_item_links (target_id, type);

        CREATE TABLE IF NOT EXISTS work_item_events (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            project_id   TEXT NOT NULL,
            item_id      TEXT NOT NULL,
            item_version INTEGER NOT NULL,
            operation    TEXT NOT NULL,
            actor_kind   TEXT NOT NULL,
            actor_id     TEXT,
            operation_id TEXT,
            changes_json TEXT NOT NULL,
            occurred_utc TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_work_item_events_item ON work_item_events (item_id, id);
        CREATE INDEX IF NOT EXISTS ix_work_item_events_operation
            ON work_item_events (project_id, operation_id) WHERE operation_id IS NOT NULL;

        CREATE TABLE IF NOT EXISTS work_item_operations (
            project_id   TEXT NOT NULL,
            operation_id TEXT NOT NULL,
            operation    TEXT NOT NULL,
            item_id      TEXT NOT NULL,
            related_id   TEXT,
            result_ref   TEXT,
            recorded_utc TEXT NOT NULL,
            PRIMARY KEY (project_id, operation_id)
        );
        """;

    /// <summary>
    /// The ordered, append-only migration chain. Step N takes the database from v(N-1) to vN. Steps 1..6
    /// are additive table creations (each idempotent under <c>IF NOT EXISTS</c>); step 7 is a data
    /// transform (the transcript redaction scrub), which is why the chain, not a bulk create, is what a
    /// migrated database needs.
    /// </summary>
    public static IReadOnlyList<SchemaMigration> Migrations { get; } =
    [
        new(1, (c, t) => SchemaMigrator.Execute(c, t, V1BaseTables)),
        new(2, (c, t) => SchemaMigrator.Execute(c, t, V2FrameworkStore)),
        new(3, (c, t) => SchemaMigrator.Execute(c, t, V3WikiStore)),
        new(4, (c, t) => SchemaMigrator.Execute(c, t, V4Workspaces)),
        new(5, (c, t) => SchemaMigrator.Execute(c, t, V5GitHosts)),
        new(6, (c, t) => SchemaMigrator.Execute(c, t, V6Sessions)),
        new(7, (c, t) => ScrubTranscriptSecrets(c, t)),
        new(8, (c, t) => AddSessionUsageSummary(c, t)),
        new(9, (c, t) => AddSessionCustomTitle(c, t)),
        new(10, (c, t) => SchemaMigrator.Execute(c, t, V10ProductEvents)),
        new(11, (c, t) => AddProjectsAndWorkItems(c, t)),
        new(12, (c, t) => AddSessionUsageDays(c, t)),
        new(13, (c, t) => AddWorkItemAgentLayer(c, t)),
        new(14, (c, t) => SchemaMigrator.Execute(c, t, V14Evidence)),
        new(15, (c, t) => SchemaMigrator.Execute(c, t, V15SessionWorkItems)),
        new(16, (c, t) => SchemaMigrator.Execute(c, t, V16WikiItems)),
        new(17, (c, t) => SchemaMigrator.Execute(c, t, V17SessionPlans)),
        new(18, (c, t) => SchemaMigrator.Execute(c, t, V18SessionChangedFiles)),
        new(19, (c, t) => SchemaMigrator.Execute(c, t, V19SessionRoles)),
        new(20, (c, t) => AddScenarioStepCommand(c, t)),
        new(21, (c, t) => SchemaMigrator.Execute(c, t, V21SessionBranches)),
        new(22, (c, t) => SchemaMigrator.Execute(c, t, V22Mirroring)),
        new(23, (c, t) => AddMirrorBaseFields(c, t)),
        new(24, (c, t) => SchemaMigrator.Execute(c, t, V24WikiPublications)),
        new(25, (c, t) => SchemaMigrator.Execute(c, t, V25WikiEmbeddings)),
        new(26, (c, t) => SchemaMigrator.Execute(c, t, V26WikiSummaries)),
        new(27, (c, t) => ScopeWikiToProjects(c, t)),
        new(28, (c, t) => AddConflictNote(c, t)),
        new(29, (c, t) => SchemaMigrator.Execute(c, t, V29Scratchpad)),
        new(30, (c, t) => AddWikiParent(c, t)),
        new(31, (c, t) => SchemaMigrator.Execute(c, t, V31ProjectProviders)),
        new(32, (c, t) => SchemaMigrator.Execute(c, t, V32MirrorHistory)),
        new(33, (c, t) => SchemaMigrator.Execute(c, t, V33PublicationHistory)),
        new(34, (c, t) => SchemaMigrator.Execute(c, t, V34WorkspaceTrust)),
        new(35, (c, t) => SchemaMigrator.Execute(c, t, V35ProjectVariables)),
        new(36, (c, t) => SchemaMigrator.Execute(c, t, V36WikiItemRevisions)),
        new(37, (c, t) => AddApprovedDesignVersion(c, t)),
        new(38, (c, t) => AddSessionAttention(c, t)),
        new(39, (c, t) => SchemaMigrator.Execute(c, t, V39Autopilot)),
        new(40, (c, t) => AddAutopilotMarkAndDelivery(c, t)),
        new(41, (c, t) => AddAutopilotStopAfterItem(c, t)),
        new(42, (c, t) => AddAutopilotWorkspaceReturn(c, t)),
        new(43, (c, t) => AddAutopilotPersonWait(c, t)),
        new(44, (c, t) => AddAutopilotWaitingForCapacity(c, t)),
        new(45, (c, t) => AddSuggestedModelAndEffort(c, t)),
        new(46, (c, t) => SchemaMigrator.Execute(c, t, V46WorkItemAttachments)),
        new(47, (c, t) => AddReleases(c, t)),
        new(48, (c, t) => UnbindDefaultFolders(c, t)),
        new(49, (c, t) => RenameFormerStoredNames(c, t)),
        new(50, (c, t) => AddCacheWritesAndFreeTierUnits(c, t)),
    ];

    // Schema step 48 (item #373, docs/plans/first-open-setup.md): NO SHARED BUCKET. A folder nobody put in a project
    // belongs to none, so workspaces.project_id becomes nullable, NULL being "not set up", and every folder that was in
    // the default project only because that was the column's default is unbound. SQLite cannot drop a NOT NULL, so the
    // table is rebuilt in this step's transaction. former_project_id keeps the one fact the rebuild would otherwise
    // lose, so the setup can say that the default project's work is still there. A folder bound to any other project
    // is copied as it is, and nothing outside this table changes: the default project and everything in it stay.
    private const string V48WorkspacesShape = """
        CREATE TABLE IF NOT EXISTS {0} (
            id                TEXT PRIMARY KEY,
            name              TEXT NOT NULL,
            root_path         TEXT NOT NULL,
            last_opened_utc   TEXT NOT NULL,
            project_id        TEXT,
            former_project_id TEXT
        );
        """;

    private const string V48Rebuild = """
        INSERT INTO workspaces_v48 (id, name, root_path, last_opened_utc, project_id, former_project_id)
            SELECT id, name, root_path, last_opened_utc,
                   CASE WHEN project_id = 'default' THEN NULL ELSE project_id END,
                   CASE WHEN project_id = 'default' THEN 'default' ELSE NULL END
            FROM workspaces;
        DROP TABLE workspaces;
        ALTER TABLE workspaces_v48 RENAME TO workspaces;
        """;

    /// <summary>
    /// Schema step 50 (backlog 377): the usage summary counts cache writes and the credential broker's free-tier units.
    /// Cache writes are most of a first turn's cost and were not recorded; a brokered turn costs the person nothing and is
    /// counted in the broker's units instead, by the day it was used, so the day's allowance can be shown. Every existing
    /// row recorded neither, so both start at zero and nothing is backfilled. Each column is probed before it is added,
    /// and a table this database does not have (a partial schema a migration test builds) is left alone.
    /// </summary>
    private static void AddCacheWritesAndFreeTierUnits(SqliteConnection connection, SqliteTransaction transaction)
    {
        void AddColumn(string table, string name)
        {
            if (TableExists(connection, table) && !ColumnExists(connection, table, name))
            {
                SchemaMigrator.Execute(connection, transaction, $"ALTER TABLE {table} ADD COLUMN {name} INTEGER NOT NULL DEFAULT 0;");
            }
        }

        AddColumn("agent_sessions", "cache_write_tokens");
        AddColumn("agent_sessions", "free_tier_units");
        AddColumn("agent_session_usage_days", "free_tier_units");
    }

    /// <summary>
    /// Schema step 49 (the bridge release of the internal rename, <c>docs/plans/rename-internal-names.md</c> phase 2): the
    /// stored values that carried a former name, read from <c>FormerNames</c>. Not a table change; a data step, so it runs
    /// once, in its own transaction, and an older database is migrated rather than stamped.
    /// <list type="bullet">
    ///   <item>The studio assistant's settings keys (model, effort and the topic keys) take their new names. A value already
    ///     stored under the new name wins and the former row goes; nothing else in the row changes.</item>
    ///   <item>The studio's MCP tools are named <c>mcp__&lt;server&gt;__</c> in the agent CLI and the server was renamed, so a
    ///     stored hook, role or setting naming a tool by its former prefix is rewritten to the new one (framework artifacts'
    ///     payloads and settings values); without it a stored rule would silently stop matching.</item>
    /// </list>
    /// Transcripts, wiki text and work items are left as they were written: they are history, not configuration.
    /// </summary>
    private static void RenameFormerStoredNames(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (TableExists(connection, "settings"))
        {
            foreach (var (former, current) in new[]
            {
                (Fravashi.Domain.FormerNames.StudioAssistantModelKey, Fravashi.Application.Sessions.AgentDefaultsSetting.StudioAssistantModelKey),
                (Fravashi.Domain.FormerNames.StudioAssistantEffortKey, Fravashi.Application.Sessions.AgentDefaultsSetting.StudioAssistantEffortKey),
            })
            {
                MoveSetting(connection, transaction, former, current);
            }

            var topicKeys = new List<string>();
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = "SELECT key FROM settings WHERE substr(key, 1, length($p)) = $p;";
                select.Parameters.AddWithValue("$p", Fravashi.Domain.FormerNames.StudioAssistantTopicPrefix);
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    topicKeys.Add(reader.GetString(0));
                }
            }

            foreach (var key in topicKeys)
            {
                MoveSetting(
                    connection,
                    transaction,
                    key,
                    Fravashi.Application.Sessions.StudioAssistantTopic.Prefix + key[Fravashi.Domain.FormerNames.StudioAssistantTopicPrefix.Length..]);
            }

            ReplaceIn(connection, transaction, "settings", "value");
        }

        if (TableExists(connection, "framework_artifacts"))
        {
            ReplaceIn(connection, transaction, "framework_artifacts", "payload_json");
        }
    }

    /// <summary>Moves one settings row to a new key; a row already under the new key wins and the former one goes.</summary>
    private static void MoveSetting(SqliteConnection connection, SqliteTransaction transaction, string former, string current)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT OR IGNORE INTO settings (key, value) SELECT $new, value FROM settings WHERE key = $old;" +
            "DELETE FROM settings WHERE key = $old;";
        command.Parameters.AddWithValue("$old", former);
        command.Parameters.AddWithValue("$new", current);
        command.ExecuteNonQuery();
    }

    /// <summary>Rewrites the studio tools' former prefix to the current one in one text column of one table.</summary>
    private static void ReplaceIn(SqliteConnection connection, SqliteTransaction transaction, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"UPDATE {table} SET {column} = replace({column}, $old, $new) WHERE instr({column}, $old) > 0;";
        command.Parameters.AddWithValue("$old", Fravashi.Domain.FormerNames.StudioToolPrefix);
        command.Parameters.AddWithValue("$new", Fravashi.Domain.AgentSessions.AgentWorkGate.StudioToolPrefix);
        command.ExecuteNonQuery();
    }

    private static void UnbindDefaultFolders(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (!TableExists(connection, "workspaces"))
        {
            // A database that never had the table (a hand-built older shape) gets it in the new shape.
            SchemaMigrator.Execute(connection, transaction, string.Format(
                System.Globalization.CultureInfo.InvariantCulture, V48WorkspacesShape, "workspaces"));
            return;
        }

        // Replayed against an inaccurate version marker, the table is already in the new shape: nothing to do.
        if (ColumnExists(connection, "workspaces", "former_project_id"))
        {
            return;
        }

        if (!ColumnExists(connection, "workspaces", "project_id"))
        {
            SchemaMigrator.Execute(connection, transaction, "ALTER TABLE workspaces ADD COLUMN project_id TEXT;");
        }

        SchemaMigrator.Execute(connection, transaction, "DROP TABLE IF EXISTS workspaces_v48;");
        SchemaMigrator.Execute(connection, transaction, string.Format(
            System.Globalization.CultureInfo.InvariantCulture, V48WorkspacesShape, "workspaces_v48"));
        SchemaMigrator.Execute(connection, transaction, V48Rebuild);
    }

    // Schema step 47 (work item #368): a release is a tracker record of one project (version, date, commit, tag,
    // notes, its Release wiki document) and the items it carried, and every work item gains released_in, the version
    // of the release that took it. released_in is null on every existing row, which is "no release yet": the board
    // shows exactly what it showed before. The index makes the board's "not archived" condition and the list's
    // "Released in" filter a lookup rather than a scan. Nothing here is ever deleted: a release has no delete.
    private const string V47ReleaseTables = """
        CREATE TABLE IF NOT EXISTS releases (
            id           TEXT PRIMARY KEY,
            project_id   TEXT NOT NULL,
            version      TEXT NOT NULL,
            tag          TEXT NOT NULL,
            commit_sha   TEXT,
            tag_created  INTEGER NOT NULL DEFAULT 0,
            released_utc TEXT NOT NULL,
            notes        TEXT NOT NULL,
            wiki_number  INTEGER,
            created_by   TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_releases_project_version ON releases (project_id, version);
        CREATE TABLE IF NOT EXISTS release_items (
            release_id TEXT NOT NULL,
            item_id    TEXT NOT NULL,
            PRIMARY KEY (release_id, item_id)
        );
        CREATE INDEX IF NOT EXISTS ix_release_items_item ON release_items (item_id);
        """;

    private static void AddReleases(SqliteConnection connection, SqliteTransaction transaction)
    {
        SchemaMigrator.Execute(connection, transaction, V47ReleaseTables);
        if (TableExists(connection, "work_items") && !ColumnExists(connection, "work_items", "released_in"))
        {
            SchemaMigrator.Execute(connection, transaction, "ALTER TABLE work_items ADD COLUMN released_in TEXT;");
        }

        if (TableExists(connection, "work_items"))
        {
            SchemaMigrator.Execute(
                connection,
                transaction,
                "CREATE INDEX IF NOT EXISTS ix_work_items_released ON work_items (project_id, released_in);");
        }
    }

    // Schema step 46 (work item #359): the files a work item carries, images first. One row per attachment; the
    // bytes themselves live in the data directory under work-item-attachments/<item>/<id>.<ext>, never in the
    // repository and never in the database, so a large screenshot does not bloat every backup's database copy and
    // a row is only ever a pointer the store checks before it serves anything. Keyed by the item's id, not its
    // number, so a renumbered or moved item keeps its files.
    private const string V46WorkItemAttachments = """
        CREATE TABLE IF NOT EXISTS work_item_attachments (
            id          TEXT PRIMARY KEY,
            item_id     TEXT NOT NULL,
            file_name   TEXT NOT NULL,
            media_type  TEXT NOT NULL,
            size_bytes  INTEGER NOT NULL,
            sha256      TEXT NOT NULL,
            added_utc   TEXT NOT NULL,
            added_by    TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_work_item_attachments_item ON work_item_attachments (item_id);
        """;

    // Schema step 45 (requested 2026-10-22, model and effort per work item): the model and the effort a
    // conversation started from the item starts with, each stored as the stable name the agent CLI itself takes.
    // Both nullable, and null on every existing row, which is "Default": an item nobody set starts its conversation
    // exactly as before. Probed before being added like step 40's work_items column.
    private static void AddSuggestedModelAndEffort(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (TableExists(connection, "work_items") && !ColumnExists(connection, "work_items", "suggested_model"))
        {
            SchemaMigrator.Execute(connection, transaction, "ALTER TABLE work_items ADD COLUMN suggested_model TEXT;");
        }

        if (TableExists(connection, "work_items") && !ColumnExists(connection, "work_items", "suggested_effort"))
        {
            SchemaMigrator.Execute(connection, transaction, "ALTER TABLE work_items ADD COLUMN suggested_effort TEXT;");
        }
    }

    // Schema step 44 (backlog 308, ADR 0031 decision 16): the words a person reads on the autopilot page when an item's
    // own fresh build start was refused by the concurrency gate, while it stays in its own stage rather than
    // failing. Nullable, probed before being added like step 43's own column.
    private static void AddAutopilotWaitingForCapacity(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (TableExists(connection, "autopilot_run_items") && !ColumnExists(connection, "autopilot_run_items", "waiting_for_capacity_reason"))
        {
            SchemaMigrator.Execute(
                connection, transaction, "ALTER TABLE autopilot_run_items ADD COLUMN waiting_for_capacity_reason TEXT;");
        }
    }

    // Schema step 43 (backlog 305, ADR 0031 decision 12): when an item first asked for a person and the run
    // started waiting on it, while it is still in its own stage rather than parked. Nullable, probed before
    // being added like step 42's three columns.
    private static void AddAutopilotPersonWait(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (TableExists(connection, "autopilot_run_items") && !ColumnExists(connection, "autopilot_run_items", "waiting_since_utc"))
        {
            SchemaMigrator.Execute(
                connection, transaction, "ALTER TABLE autopilot_run_items ADD COLUMN waiting_since_utc TEXT;");
        }
    }

    // Schema step 42 (backlog 310, ADR 0031 decision 3's correction): the branch a run's workspace was checked
    // out to when it was dispatched, the workspace's own id (resolved once at dispatch, never re-resolved by
    // project at return time, commit review's third pass), and the note recorded when the workspace could not
    // be returned because the working tree was not clean. All three nullable, all probed before being added
    // like step 41's stop_after_item column.
    private static void AddAutopilotWorkspaceReturn(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (TableExists(connection, "autopilot_runs") && !ColumnExists(connection, "autopilot_runs", "base_branch"))
        {
            SchemaMigrator.Execute(connection, transaction, "ALTER TABLE autopilot_runs ADD COLUMN base_branch TEXT;");
        }

        if (TableExists(connection, "autopilot_runs") && !ColumnExists(connection, "autopilot_runs", "workspace_return_note"))
        {
            SchemaMigrator.Execute(
                connection, transaction, "ALTER TABLE autopilot_runs ADD COLUMN workspace_return_note TEXT;");
        }

        if (TableExists(connection, "autopilot_runs") && !ColumnExists(connection, "autopilot_runs", "base_workspace_id"))
        {
            SchemaMigrator.Execute(
                connection, transaction, "ALTER TABLE autopilot_runs ADD COLUMN base_workspace_id TEXT;");
        }
    }

    // Schema step 41 (backlog 309, ADR 0031 decision 13): "stop after this item", a flag on the run itself,
    // probed before it is added like step 40's work_items column.
    private static void AddAutopilotStopAfterItem(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (TableExists(connection, "autopilot_runs") && !ColumnExists(connection, "autopilot_runs", "stop_after_item"))
        {
            SchemaMigrator.Execute(
                connection,
                transaction,
                "ALTER TABLE autopilot_runs ADD COLUMN stop_after_item INTEGER NOT NULL DEFAULT 0;");
        }
    }

    // Schema step 40 (backlog 298, ADR 0031 decisions 1 and 3): the per-item autopilot mark and the
    // per-project delivery choice. The mark is a column on work_items, probed before it is added like
    // step 13's agent-layer columns; delivery is its own table, one row per project that has chosen,
    // shaped exactly like project_providers (step 31): a project that never chose has no row and reads as
    // the default (branch and pull request) rather than needing one inserted.
    private const string V40ProjectDelivery = """
        CREATE TABLE IF NOT EXISTS project_delivery (
            project_id TEXT PRIMARY KEY,
            delivery   TEXT NOT NULL
        );
        """;

    private static void AddAutopilotMarkAndDelivery(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (TableExists(connection, "work_items") && !ColumnExists(connection, "work_items", "marked_for_autopilot"))
        {
            SchemaMigrator.Execute(
                connection,
                transaction,
                "ALTER TABLE work_items ADD COLUMN marked_for_autopilot INTEGER NOT NULL DEFAULT 0;");
        }

        SchemaMigrator.Execute(connection, transaction, V40ProjectDelivery);
    }

    // Schema step 39 (backlog 297): a persisted autopilot run, its queue of marked items with a stage each, and
    // its stop records. One row per run, one per queued item, one per stop, so a stop is a stored row and never a
    // log line only.
    private const string V39Autopilot = """
        CREATE TABLE IF NOT EXISTS autopilot_runs (
            id                TEXT PRIMARY KEY,
            project_id        TEXT NOT NULL,
            settings          TEXT NOT NULL,
            state             TEXT NOT NULL,
            started_utc       TEXT NOT NULL,
            last_progress_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS autopilot_run_items (
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

        CREATE TABLE IF NOT EXISTS autopilot_run_stops (
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

        CREATE INDEX IF NOT EXISTS ix_autopilot_run_items_run ON autopilot_run_items (run_id);
        CREATE INDEX IF NOT EXISTS ix_autopilot_run_stops_run ON autopilot_run_stops (run_id);
        """;

    // Schema step 38 (backlog 244): the operator's attention per conversation, one row per broker decision, person turn
    // and commit review. Like product_events, every column is an id, a closed-set name, a number or a time: nothing a
    // tool call carried has a column to go into.
    private const string V38SessionAttention = """
        CREATE TABLE IF NOT EXISTS agent_session_attention (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            session_id    TEXT NOT NULL,
            entry         TEXT NOT NULL,
            tool_kind     TEXT,
            outcome       TEXT,
            review        TEXT,
            waited_ms     INTEGER NOT NULL DEFAULT 0,
            occurred_utc  TEXT NOT NULL,
            reconstructed INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS ix_agent_session_attention_session ON agent_session_attention (session_id, occurred_utc);
        """;

    /// <summary>
    /// Schema step 38 (backlog 244): creates the attention table, then reconstructs what every stored transcript can say
    /// about the conversations that ran before counting existed (<see cref="AttentionReconstruction"/>), as rows marked
    /// reconstructed. A replay finds rows already there for a conversation and adds none.
    /// </summary>
    private static void AddSessionAttention(SqliteConnection connection, SqliteTransaction transaction)
    {
        SchemaMigrator.Execute(connection, transaction, V38SessionAttention);

        var counted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT DISTINCT session_id FROM agent_session_attention;";
            using var reader = existing.ExecuteReader();
            while (reader.Read())
            {
                counted.Add(reader.GetString(0));
            }
        }

        var transcripts = new Dictionary<Guid, List<Domain.AgentSessions.AgentEvent>>();
        using (var events = connection.CreateCommand())
        {
            events.Transaction = transaction;
            events.CommandText =
                "SELECT session_id, kind, payload FROM agent_session_events "
                + "WHERE kind IN ('approval_requested', 'approval_resolved', 'tool_refused', 'tool_trusted', 'message') "
                + "ORDER BY id;";
            using var reader = events.ExecuteReader();
            while (reader.Read())
            {
                if (!Guid.TryParse(reader.GetString(0), out var id) || counted.Contains(id.ToString("D")))
                {
                    continue;
                }

                Domain.AgentSessions.AgentEvent? e;
                try
                {
                    e = AgentEventJson.Deserialize(reader.GetString(1), reader.GetString(2));
                }
                catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or ArgumentException)
                {
                    // A row this version cannot read is not counted rather than stopping the upgrade.
                    continue;
                }

                if (e is null)
                {
                    continue;
                }

                if (!transcripts.TryGetValue(id, out var list))
                {
                    transcripts[id] = list = [];
                }

                list.Add(e);
            }
        }

        foreach (var (id, transcript) in transcripts)
        {
            var session = new Domain.AgentSessions.AgentSessionId(id);
            foreach (var entry in AttentionReconstruction.From(session, transcript))
            {
                SqliteSessionAttention.Insert(connection, transaction, entry);
            }
        }
    }

    // Schema step 36 (backlog 209): one snapshot of a wiki document's title+body per version it was ever saved
    // at, so a reader can ask for an old one after later edits moved the document on. Written going forward on
    // every CreateAsync/UpdateAsync; the backfill below gives every document that already exists one row at the
    // version it happens to be at right now, so approving one right after this upgrade still has a baseline to
    // pin to rather than a gap between "upgraded" and "first write under the new code".
    private const string V36WikiItemRevisions = """
        CREATE TABLE IF NOT EXISTS wiki_item_revisions (
            id          TEXT PRIMARY KEY,
            item_id     TEXT NOT NULL,
            version     INTEGER NOT NULL,
            title       TEXT NOT NULL,
            body        TEXT NOT NULL,
            created_utc TEXT NOT NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_wiki_item_revisions_item_version
            ON wiki_item_revisions (item_id, version);
        CREATE INDEX IF NOT EXISTS ix_wiki_item_revisions_item ON wiki_item_revisions (item_id);

        INSERT OR IGNORE INTO wiki_item_revisions (id, item_id, version, title, body, created_utc)
            SELECT lower(hex(randomblob(16))), id, version, title, body, updated_utc FROM wiki_items;
        """;

    // Schema step 35 (backlog 154): the environment variables a project hands its agents. A plain value sits in the
    // row; a secret's value sits in the secret store under project:{id}:env:{name} and the row's value is NULL.
    private const string V35ProjectVariables = """
        CREATE TABLE IF NOT EXISTS project_variables (
            project_id  TEXT NOT NULL,
            name        TEXT NOT NULL,
            value       TEXT,
            is_secret   INTEGER NOT NULL DEFAULT 0,
            updated_utc TEXT NOT NULL,
            PRIMARY KEY (project_id, name)
        );
        """;

    // Schema step 34 (ADR 0028, backlog 152): the trust level a person chose per workspace. One row per folder set
    // above Ask; no row is Ask, so a database that predates the step reads as "every change asks" everywhere.
    private const string V34WorkspaceTrust = """
        CREATE TABLE IF NOT EXISTS workspace_trust (
            workspace_id TEXT PRIMARY KEY,
            trust        TEXT NOT NULL
        );
        """;

    // Schema step 33 (backlog 145): where a document WAS published, once its project publishes to another wiki. The
    // live row is keyed by document, one wiki at a time, the same shape as the board's mirror and for the same reason.
    private const string V33PublicationHistory = """
        CREATE TABLE IF NOT EXISTS wiki_item_publication_history (
            item_id      TEXT NOT NULL,
            provider     TEXT NOT NULL,
            space        TEXT NOT NULL,
            page_id      TEXT,
            web_url      TEXT,
            state        TEXT NOT NULL,
            archived_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_wiki_item_publication_history_item ON wiki_item_publication_history (item_id);
        """;

    // Schema step 32 (backlog 145): where an item WAS mirrored, once its project moved to another provider. The live
    // mirror row is keyed by project and item, one provider at a time, so moving would otherwise mean deleting the
    // record that the item was ever on the old one; ADR 0018 decision 5 says history is not destroyed.
    private const string V32MirrorHistory = """
        CREATE TABLE IF NOT EXISTS work_item_mirror_history (
            project_id         TEXT NOT NULL,
            item_id            TEXT NOT NULL,
            connection         TEXT NOT NULL,
            provider_key       TEXT NOT NULL,
            last_confirmed_utc TEXT,
            tombstoned_utc     TEXT,
            retired_utc        TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_work_item_mirror_history_item
            ON work_item_mirror_history (project_id, item_id);
        """;

    // Schema step 31 (backlog 142): which providers a project follows. No row is the install's connections, which
    // is what every project followed before, so nothing changes for an install until somebody chooses.
    private const string V31ProjectProviders = """
        CREATE TABLE IF NOT EXISTS project_providers (
            project_id TEXT PRIMARY KEY,
            board      TEXT NOT NULL DEFAULT 'install',
            wiki       TEXT NOT NULL DEFAULT 'install'
        );
        """;

    /// <summary>
    /// Schema step 30 (backlog 49): a document may sit under another, so the wiki keeps the tree of the folders it
    /// was imported from and a provider that nests pages is given the same tree. Nullable, no foreign key: a
    /// deleted parent's children are moved up by the store in the same transaction.
    /// </summary>
    private static void AddWikiParent(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (!ColumnExists(connection, "wiki_items", "parent_id"))
        {
            SchemaMigrator.Execute(connection, transaction, "ALTER TABLE wiki_items ADD COLUMN parent_id TEXT;");
        }
    }

    /// <summary>
    /// Schema step 37 (backlog 209): the approved-design reference records the design's version at the
    /// moment it was approved, so an edit afterward cannot silently change what the verifier reads. Null on
    /// every row written before this step, which is read as "approved, nothing to compare against" rather
    /// than as a version that does not exist.
    /// </summary>
    private static void AddApprovedDesignVersion(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (!ColumnExists(connection, "work_item_refs", "version"))
        {
            SchemaMigrator.Execute(connection, transaction, "ALTER TABLE work_item_refs ADD COLUMN version INTEGER;");
        }
    }

    /// <summary>
    /// Schema step 28 (backlog 47/48): a conflict may carry a NOTE beside its three values. The values are
    /// what settling rebases the agreed value onto, so a sentence in one of them is not merely untidy.
    /// </summary>
    private static void AddConflictNote(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (!ColumnExists(connection, "work_item_conflicts", "note"))
        {
            SchemaMigrator.Execute(connection, transaction, "ALTER TABLE work_item_conflicts ADD COLUMN note TEXT;");
        }
    }

    /// <summary>
    /// Schema step 27 (backlog 121): the authored wiki becomes project-scoped, the way work items have been
    /// since step 11. It was the one store with no project column at all, and a document's NUMBER and SLUG were
    /// unique across the whole install, so a second project's documents would have sat in the first's library
    /// and <c>@3</c> would have meant one thing everywhere.
    /// <para>
    /// Every existing document joins the default project, which is where every existing workspace already is, so
    /// nothing moves and no reference changes meaning for an install that has one project. The two unique
    /// indexes become per-project. The number counter moves from the single-row <c>wiki_state</c> to one row per
    /// project, and the default project's row STARTS from the old counter: a counter exists at all so that a
    /// deleted document's number is never handed out again (a reference to it must go on reading as broken
    /// rather than quietly pointing at something new), and restarting it would break exactly that.
    /// <c>wiki_state</c> is left in place, unread: dropping a table is the one step here that cannot be undone,
    /// and it costs nothing to keep.
    /// </para>
    /// </summary>
    private static void ScopeWikiToProjects(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (!ColumnExists(connection, "wiki_items", "project_id"))
        {
            SchemaMigrator.Execute(
                connection, transaction, "ALTER TABLE wiki_items ADD COLUMN project_id TEXT NOT NULL DEFAULT 'default';");
        }

        SchemaMigrator.Execute(connection, transaction, """
            DROP INDEX IF EXISTS ux_wiki_items_number;
            DROP INDEX IF EXISTS ux_wiki_items_slug;
            CREATE UNIQUE INDEX IF NOT EXISTS ux_wiki_items_project_number ON wiki_items (project_id, number);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_wiki_items_project_slug ON wiki_items (project_id, slug);

            CREATE TABLE IF NOT EXISTS wiki_numbers (
                project_id  TEXT PRIMARY KEY,
                next_number INTEGER NOT NULL
            );

            INSERT OR IGNORE INTO wiki_numbers (project_id, next_number)
                SELECT 'default', next_number FROM wiki_state WHERE id = 1;
            """);
    }

    /// <summary>
    /// Schema step 23 (backlog 82, ADR 0018 decision 5): the provider-owned field values as they stood at
    /// the last reconciliation.
    /// <para>
    /// Without them, "did this side move?" can only be answered from the item's VERSION, which counts every
    /// change to the item rather than to one field. An edit to the description would then make the title
    /// look moved too, and the conflict surface would fill with questions nobody needs to answer. A surface
    /// that asks too much is one people learn to click through, and then the question that mattered gets
    /// clicked through as well.
    /// </para>
    /// </summary>
    private static void AddMirrorBaseFields(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (ColumnExists(connection, "work_item_mirror", "base_fields"))
        {
            return;
        }

        SchemaMigrator.Execute(connection, transaction, "ALTER TABLE work_item_mirror ADD COLUMN base_fields TEXT;");
    }

    // Schema step 22 (backlog 82, ADR 0018 decisions 5, 7 and 8): the provider mirroring sidecar.
    //
    // Three tables, and the reason each is here rather than a column on work_items:
    //
    // work_item_mirror keeps the provider facts BESIDE the item. The local board has no provider, so
    // every one of these columns would be permanently null on it, and the work item is the canonical
    // artifact whose every field is classified by owner. A sidecar keeps the board a board.
    //
    // work_item_outbox is the durable write queue. Durable is the point: an in-memory queue loses the
    // user's changes on a crash, which is the outcome an outbox exists to prevent, and it could not
    // record the in-flight window that makes a crashed creation recoverable. The operation id is the
    // primary key WITH the project, which is the same key the work_item_operations ledger uses, so one
    // id names one write everywhere.
    //
    // work_item_conflicts holds disagreements rather than resolving them. All three values are kept,
    // the base included, because without the value as it stood at the last sync nobody can tell which
    // side moved, and a conflict nobody can interpret is a conflict nobody will settle.
    //
    // No counter columns anywhere: how many writes are pending and how many fields are in conflict are
    // COUNTED from these tables when the freshness indicator is built. A denormalised counter that
    // drifts produces an indicator that lies, and a lying indicator is worse than none.
    private const string V22Mirroring = """
        CREATE TABLE IF NOT EXISTS work_item_mirror (
            project_id          TEXT NOT NULL,
            item_id             TEXT NOT NULL,
            connection          TEXT NOT NULL,
            provider_key        TEXT NOT NULL,
            last_confirmed_utc  TEXT,
            provider_version    TEXT,
            local_version       INTEGER NOT NULL DEFAULT 0,
            tombstoned_utc      TEXT,
            tombstone_reason    TEXT,
            PRIMARY KEY (project_id, item_id)
        );

        CREATE TABLE IF NOT EXISTS work_item_outbox (
            project_id       TEXT NOT NULL,
            operation_id     TEXT NOT NULL,
            item_id          TEXT NOT NULL,
            kind             TEXT NOT NULL,
            payload          TEXT NOT NULL,
            expected_version INTEGER NOT NULL,
            state            TEXT NOT NULL,
            attempts         INTEGER NOT NULL DEFAULT 0,
            queued_utc       TEXT NOT NULL,
            last_attempt_utc TEXT,
            result_key       TEXT,
            failure          TEXT,
            PRIMARY KEY (project_id, operation_id)
        );

        CREATE INDEX IF NOT EXISTS ix_work_item_outbox_item
            ON work_item_outbox (project_id, item_id, queued_utc);

        CREATE TABLE IF NOT EXISTS work_item_conflicts (
            project_id     TEXT NOT NULL,
            conflict_id    TEXT NOT NULL,
            item_id        TEXT NOT NULL,
            field          TEXT NOT NULL,
            base_value     TEXT,
            local_value    TEXT,
            provider_value TEXT,
            note           TEXT,
            detected_utc   TEXT NOT NULL,
            choice         TEXT,
            resolved_utc   TEXT,
            PRIMARY KEY (project_id, conflict_id)
        );

        CREATE INDEX IF NOT EXISTS ix_work_item_conflicts_open
            ON work_item_conflicts (project_id, item_id, resolved_utc);
        """;

    // Schema step 21 (backlog 86, ADR 0018 decision 9): the branch an execution attempt runs on. Durable
    // because an attempt whose branch the studio forgot at the next restart would be an attempt nobody
    // could find afterwards, which is the opposite of what recording it is for.
    private const string V21SessionBranches = """
        CREATE TABLE IF NOT EXISTS agent_session_branches (
            session_id  TEXT PRIMARY KEY,
            branch      TEXT NOT NULL,
            base_branch TEXT NOT NULL,
            started_utc TEXT NOT NULL
        );
        """;

    /// <summary>
    /// Schema step 20 (backlog 55): what to RUN to check a scenario step. A column rather than something
    /// packed into the step's open data, because whether a step is runnable is a fact the product acts on
    /// and a fact nobody should have to parse out of a blob to find.
    /// </summary>
    private static void AddScenarioStepCommand(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (TableExists(connection, "work_item_scenario_steps")
            && !ColumnExists(connection, "work_item_scenario_steps", "command"))
        {
            SchemaMigrator.Execute(
                connection, transaction, "ALTER TABLE work_item_scenario_steps ADD COLUMN command TEXT;");
        }
    }

    // Schema step 19 (backlog 88): the role a session runs under. One row per session, because a session
    // is one conversation doing one kind of work; a conversation that needs a second role is a second
    // conversation, which is also how a person would think about it.
    private const string V19SessionRoles = """
        CREATE TABLE IF NOT EXISTS agent_session_roles (
            session_id TEXT PRIMARY KEY,
            role_id    TEXT NOT NULL
        );
        """;

    // Schema step 18 (backlog 85): which files a session has already been approved to change. It is what
    // makes a change budget mean something across a restart: a budget the studio forgot at every restart
    // would be a number that only ever held on a quiet afternoon. Paths are stored as the studio resolved
    // them, one row each, so the count is DISTINCT files rather than approvals.
    private const string V18SessionChangedFiles = """
        CREATE TABLE IF NOT EXISTS agent_session_changed_files (
            session_id TEXT NOT NULL,
            path       TEXT NOT NULL,
            first_utc  TEXT NOT NULL,
            PRIMARY KEY (session_id, path)
        );
        """;

    // Schema step 17 (backlog 45, gate G2): the plan a session is working under. Its own table rather than
    // a column beside the work items, because they are two different bindings that are cleared and set at
    // different moments, and because the gate asks each of them the same cheap question separately.
    private const string V17SessionPlans = """
        CREATE TABLE IF NOT EXISTS agent_session_plans (
            session_id   TEXT NOT NULL,
            wiki_item_id TEXT NOT NULL,
            linked_utc   TEXT NOT NULL,
            linked_by    TEXT NOT NULL,
            PRIMARY KEY (session_id, wiki_item_id)
        );

        CREATE INDEX IF NOT EXISTS ix_agent_session_plans_item ON agent_session_plans (wiki_item_id);
        """;

    // Schema step 26 (backlog 40, the other half): what a document is FOR, written by an agent on demand and
    // read by other agents when they search. Beside wiki_items for the reason the publication row is: the
    // document is the person's and this is derived from it. item_version is the version that was READ, so a
    // summary that has fallen behind its document says so instead of passing for current. No foreign key: a
    // row that outlives its document is harmless, and the embeddings pass already removes orphans of its own.
    // Schema step 29 (backlog 67): the person's scratchpad. ONE row, pinned to id 1 by the CHECK, because the
    // owner specified exactly one scratchpad per install; a second row would be a second scratchpad nobody can
    // see. The text is stored as written: it IS the feature (copying out gives markdown), so nothing here may
    // normalise it.
    private const string V29Scratchpad = """
        CREATE TABLE IF NOT EXISTS scratchpad (
            id          INTEGER PRIMARY KEY CHECK (id = 1),
            markdown    TEXT NOT NULL,
            updated_utc TEXT NOT NULL
        );
        """;

    private const string V26WikiSummaries = """
        CREATE TABLE IF NOT EXISTS wiki_item_summaries (
            item_id      TEXT PRIMARY KEY,
            summary      TEXT NOT NULL,
            item_version INTEGER NOT NULL,
            written_by   TEXT NOT NULL,
            written_utc  TEXT NOT NULL
        );
        """;

    // Schema step 25 (backlog 40): the index by meaning over the authored wiki. One unit vector per CHUNK of a
    // document, as a BLOB of little-endian floats, with the document version and the model it was made from,
    // so an edit and a changed model both re-index by themselves. No vector extension: at a few thousand
    // documents a scan in managed code is milliseconds (decided 2026-09-19). It is DERIVED data and
    // can always be rebuilt from wiki_items, so a backup that carries it loses nothing by being restored
    // under a different model.
    private const string V25WikiEmbeddings = """
        CREATE TABLE IF NOT EXISTS wiki_item_embeddings (
            item_id      TEXT NOT NULL,
            chunk        INTEGER NOT NULL,
            model        TEXT NOT NULL,
            item_version INTEGER NOT NULL,
            vector       BLOB NOT NULL,
            PRIMARY KEY (item_id, chunk)
        );
        """;

    // Schema step 24 (backlog 83, ADR 0018 decisions 2, 5, 7 and 8): where an authored document stands
    // against the page it was published as. It sits BESIDE wiki_items for the reason work_item_mirror sits
    // beside work_items: the document has no provider, and a column there would put one on every document
    // of an install that has no wiki at all. One row per document. There is deliberately no foreign key and
    // no cascade: deleting a document here does not delete its page on the site, and a row that outlives
    // its document is the only record that such a page exists.
    //
    // replaced_body is the body as it stood before the site's version was taken over it, so that taking the
    // site's version is reversible in one step.
    private const string V24WikiPublications = """
        CREATE TABLE IF NOT EXISTS wiki_item_publications (
            item_id             TEXT PRIMARY KEY,
            provider            TEXT NOT NULL,
            space               TEXT NOT NULL,
            page_id             TEXT,
            site_version        INTEGER,
            pushed_item_version INTEGER,
            state               TEXT NOT NULL,
            operation_id        TEXT NOT NULL,
            sent_title          TEXT,
            detail              TEXT,
            web_url             TEXT,
            replaced_body       TEXT,
            updated_utc         TEXT NOT NULL
        );
        """;

    // Schema step 16 (backlog 39): the wiki as a document store rather than a mirror. The v3 wiki_pages
    // table stays exactly where it is and keeps doing its job (the pull from Confluence and GitHub wiki):
    // these are authored items with types, numbers and links, which is a different thing that happens to
    // share a word. Item 83 decides what, if anything, flows between them.
    //
    // wiki_item_refs is the DERIVED index of what each body references, rewritten on every save. It exists
    // so "what points at this" is a query rather than a scan of every document, which is what makes a link
    // navigable in both directions (this item's acceptance).
    private const string V16WikiItems = """
        CREATE TABLE IF NOT EXISTS wiki_items (
            id          TEXT PRIMARY KEY,
            number      INTEGER NOT NULL,
            type        TEXT NOT NULL,
            title       TEXT NOT NULL,
            slug        TEXT NOT NULL,
            body        TEXT NOT NULL,
            version     INTEGER NOT NULL,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_wiki_items_number ON wiki_items (number);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_wiki_items_slug ON wiki_items (slug);
        CREATE INDEX IF NOT EXISTS ix_wiki_items_type ON wiki_items (type);

        CREATE TABLE IF NOT EXISTS wiki_item_links (
            id      TEXT PRIMARY KEY,
            item_id TEXT NOT NULL,
            kind    TEXT NOT NULL,
            target  TEXT NOT NULL,
            label   TEXT,
            ordinal INTEGER NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_wiki_item_links_item ON wiki_item_links (item_id, ordinal);
        CREATE INDEX IF NOT EXISTS ix_wiki_item_links_target ON wiki_item_links (kind, target);

        CREATE TABLE IF NOT EXISTS wiki_item_refs (
            item_id TEXT NOT NULL,
            kind    TEXT NOT NULL,
            number  INTEGER NOT NULL,
            PRIMARY KEY (item_id, kind, number)
        );

        CREATE INDEX IF NOT EXISTS ix_wiki_item_refs_target ON wiki_item_refs (kind, number);

        CREATE TABLE IF NOT EXISTS wiki_state (
            id          INTEGER PRIMARY KEY CHECK (id = 1),
            next_number INTEGER NOT NULL
        );

        INSERT OR IGNORE INTO wiki_state (id, next_number) VALUES (1, 1);
        """;

    // Schema step 15 (backlog 44): which work items a session is doing. It is a JOIN table rather than a
    // column on the session, because a session can carry more than one item and because the gate asks only
    // "is there any", which a row count answers without reading the session at all. The work item is held by
    // id rather than number: a number is the board's handle for a person, and an id is what survives.
    private const string V15SessionWorkItems = """
        CREATE TABLE IF NOT EXISTS agent_session_work_items (
            session_id  TEXT NOT NULL,
            item_id     TEXT NOT NULL,
            project_id  TEXT NOT NULL,
            linked_utc  TEXT NOT NULL,
            linked_by   TEXT NOT NULL,
            PRIMARY KEY (session_id, item_id)
        );

        CREATE INDEX IF NOT EXISTS ix_agent_session_work_items_item ON agent_session_work_items (item_id);
        """;

    private const string V14Evidence = """
        CREATE TABLE IF NOT EXISTS work_item_evidence (
            id           TEXT PRIMARY KEY,
            item_id      TEXT NOT NULL,
            kind         TEXT NOT NULL,
            gate         TEXT,
            result       TEXT NOT NULL,
            summary      TEXT NOT NULL,
            artefact_ref TEXT,
            criterion_id TEXT,
            scenario_id  TEXT,
            details_json TEXT,
            recorded_utc TEXT NOT NULL,
            actor_kind   TEXT NOT NULL,
            actor_id     TEXT
        );

        CREATE INDEX IF NOT EXISTS ix_work_item_evidence_item ON work_item_evidence (item_id, recorded_utc);
        """;

    private const string V13AgentLayer = """
        CREATE TABLE IF NOT EXISTS work_item_criteria (
            id      TEXT PRIMARY KEY,
            item_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL,
            text    TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_work_item_criteria_item ON work_item_criteria (item_id, ordinal);

        CREATE TABLE IF NOT EXISTS work_item_readiness (
            id      TEXT PRIMARY KEY,
            item_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL,
            text    TEXT NOT NULL,
            checked INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS ix_work_item_readiness_item ON work_item_readiness (item_id, ordinal);

        CREATE TABLE IF NOT EXISTS work_item_person_blockers (
            id          TEXT PRIMARY KEY,
            item_id     TEXT NOT NULL,
            person      TEXT NOT NULL,
            reason      TEXT NOT NULL,
            created_utc TEXT NOT NULL,
            cleared_utc TEXT
        );

        CREATE INDEX IF NOT EXISTS ix_work_item_person_blockers_item ON work_item_person_blockers (item_id);

        CREATE TABLE IF NOT EXISTS work_item_refs (
            id           TEXT PRIMARY KEY,
            item_id      TEXT NOT NULL,
            role         TEXT NOT NULL,
            kind         TEXT NOT NULL,
            target       TEXT NOT NULL,
            workspace_id TEXT,
            label        TEXT,
            note         TEXT,
            version      INTEGER,
            ordinal      INTEGER NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_work_item_refs_item ON work_item_refs (item_id, role, ordinal);
        CREATE INDEX IF NOT EXISTS ix_work_item_refs_target ON work_item_refs (kind, target);

        CREATE TABLE IF NOT EXISTS work_item_scope_patterns (
            id           TEXT PRIMARY KEY,
            item_id      TEXT NOT NULL,
            mode         TEXT NOT NULL,
            pattern      TEXT NOT NULL,
            workspace_id TEXT
        );

        CREATE INDEX IF NOT EXISTS ix_work_item_scope_patterns_item ON work_item_scope_patterns (item_id);

        CREATE TABLE IF NOT EXISTS work_item_scenarios (
            id      TEXT PRIMARY KEY,
            item_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL,
            title   TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_work_item_scenarios_item ON work_item_scenarios (item_id, ordinal);

        CREATE TABLE IF NOT EXISTS work_item_scenario_steps (
            id          TEXT PRIMARY KEY,
            scenario_id TEXT NOT NULL,
            ordinal     INTEGER NOT NULL,
            action      TEXT NOT NULL,
            expected    TEXT NOT NULL,
            data_json   TEXT
        );

        CREATE INDEX IF NOT EXISTS ix_work_item_scenario_steps_scenario
            ON work_item_scenario_steps (scenario_id, ordinal);
        """;

    private const string V11SessionUsageDays = """
        CREATE TABLE IF NOT EXISTS agent_session_usage_days (
            session_id    TEXT NOT NULL,
            day_utc       TEXT NOT NULL,
            cost_usd      TEXT NOT NULL DEFAULT '0',
            input_tokens  INTEGER NOT NULL DEFAULT 0,
            output_tokens INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (session_id, day_utc)
        );

        CREATE INDEX IF NOT EXISTS ix_agent_session_usage_days_day
            ON agent_session_usage_days (day_utc);
        """;

    /// <summary>
    /// Schema step 11: spending by the day it was incurred. Creates the table, then (only when it is still
    /// empty, so a replay against an inaccurate marker adds nothing twice) replays every session's stats
    /// events in order and credits each one's increase over the previous snapshot to the UTC day of the
    /// event's own stamp, through the same <see cref="SessionUsageLedger"/> the write path uses.
    /// </summary>
    private static void AddSessionUsageDays(SqliteConnection connection, SqliteTransaction transaction)
    {
        SchemaMigrator.Execute(connection, transaction, V11SessionUsageDays);

        using (var probe = connection.CreateCommand())
        {
            probe.Transaction = transaction;
            probe.CommandText = "SELECT 1 FROM agent_session_usage_days LIMIT 1;";
            if (probe.ExecuteScalar() is not null)
            {
                return;
            }
        }

        var ledgers = new Dictionary<string, SessionUsageLedger>(StringComparer.Ordinal);
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                "SELECT session_id, payload, created_utc FROM agent_session_events WHERE kind = 'stats' ORDER BY id;";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                JsonNode? payload;
                try
                {
                    payload = JsonNode.Parse(reader.GetString(1));
                }
                catch (JsonException)
                {
                    continue;
                }

                if (payload is not JsonObject o
                    || !DateTimeOffset.TryParse(
                        reader.GetString(2),
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind,
                        out var at))
                {
                    continue;
                }

                var session = reader.GetString(0);
                if (!ledgers.TryGetValue(session, out var ledger))
                {
                    ledgers[session] = ledger = new SessionUsageLedger(0m, 0, 0);
                }

                ledger.Observe(
                    at,
                    decimal.Parse(MoneyText(o, "costUsd"), System.Globalization.CultureInfo.InvariantCulture),
                    Number(o, "inputTokens"),
                    Number(o, "outputTokens"));
            }
        }

        foreach (var (session, ledger) in ledgers)
        {
            ledger.WriteDays(connection, transaction, session);
        }
    }

    /// <summary>Migrates the database to <see cref="CurrentVersion"/>. Idempotent and safe on every open.</summary>
    public static void EnsureCreated(ISqliteConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        using var connection = factory.Open();
        SchemaMigrator.Run(connection, Migrations);
    }

    /// <summary>
    /// Schema step 7 (backlog 102): rows persisted before the transcript redactor existed are rewritten
    /// exactly once. Each payload goes through the same <see cref="TranscriptRedactor"/> the write path and
    /// the display use; only rows the redactor actually changed are rewritten, so an ordinary transcript
    /// stays byte-identical. A payload that does not parse as JSON is left alone: the redactor cannot reason
    /// about it, and the reader skips it anyway. Because it is a migration step keyed on the stored version,
    /// a database already at v7+ never re-runs it.
    /// </summary>
    private static void ScrubTranscriptSecrets(SqliteConnection connection, SqliteTransaction transaction)
    {
        var rewrites = new List<(long Id, string Payload)>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT id, payload FROM agent_session_events;";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                JsonNode? payload;
                try
                {
                    payload = JsonNode.Parse(reader.GetString(1));
                }
                catch (JsonException)
                {
                    continue;
                }

                if (payload is not null && TranscriptRedactor.RedactInPlace(payload))
                {
                    rewrites.Add((reader.GetInt64(0), payload.ToJsonString()));
                }
            }
        }

        if (rewrites.Count > 0)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE agent_session_events SET payload = $p WHERE id = $id;";
            var payload = update.Parameters.Add("$p", SqliteType.Text);
            var id = update.Parameters.Add("$id", SqliteType.Integer);
            foreach (var (rowId, rewritten) in rewrites)
            {
                payload.Value = rewritten;
                id.Value = rowId;
                update.ExecuteNonQuery();
            }
        }
    }

    /// <summary>
    /// Schema step 8 (backlog 19): the per-session usage summary. Adds cost and token columns to
    /// <c>agent_sessions</c> and backfills each session from its LAST persisted stats event (the CLI's
    /// snapshots are cumulative, so the last one is the whole session). This is what makes the cross-session
    /// totals a one-row-per-session aggregate instead of a scan over every transcript event, and it
    /// reconciles by construction: a session's stored figure is the same snapshot its strip renders. Cost is
    /// TEXT so the exact <see cref="decimal"/> round-trips. Idempotent-safe: each column is probed before it
    /// is added, so a replay against an inaccurate version marker adds nothing twice.
    /// </summary>
    private static void AddSessionUsageSummary(SqliteConnection connection, SqliteTransaction transaction)
    {
        void AddColumn(string name, string type)
        {
            if (!ColumnExists(connection, "agent_sessions", name))
            {
                SchemaMigrator.Execute(connection, transaction, $"ALTER TABLE agent_sessions ADD COLUMN {name} {type};");
            }
        }

        AddColumn("cost_usd", "TEXT NOT NULL DEFAULT '0'");
        AddColumn("input_tokens", "INTEGER NOT NULL DEFAULT 0");
        AddColumn("output_tokens", "INTEGER NOT NULL DEFAULT 0");
        AddColumn("cache_read_tokens", "INTEGER NOT NULL DEFAULT 0");
        AddColumn("tool_calls", "INTEGER NOT NULL DEFAULT 0");

        BackfillSessionUsage(connection, transaction);
    }

    /// <summary>
    /// Writes each session's summary columns from the last <c>stats</c> row it has. Ordered by id ascending
    /// so the last write per session wins, which is the newest cumulative snapshot. A payload that does not
    /// parse is skipped, exactly as the transcript reader skips it.
    /// </summary>
    private static void BackfillSessionUsage(SqliteConnection connection, SqliteTransaction transaction)
    {
        var latest = new Dictionary<string, (string Cost, long Input, long Output, long Cache, long Tools)>(StringComparer.Ordinal);
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                "SELECT session_id, payload FROM agent_session_events WHERE kind = 'stats' ORDER BY id;";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                JsonNode? payload;
                try
                {
                    payload = JsonNode.Parse(reader.GetString(1));
                }
                catch (JsonException)
                {
                    continue;
                }

                if (payload is not JsonObject o)
                {
                    continue;
                }

                latest[reader.GetString(0)] = (
                    Cost: MoneyText(o, "costUsd"),
                    Input: Number(o, "inputTokens"),
                    Output: Number(o, "outputTokens"),
                    Cache: Number(o, "cacheReadTokens"),
                    Tools: Number(o, "toolCalls"));
            }
        }

        if (latest.Count == 0)
        {
            return;
        }

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
            "UPDATE agent_sessions SET cost_usd = $cost, input_tokens = $in, output_tokens = $out, " +
            "cache_read_tokens = $cache, tool_calls = $tools WHERE id = $id;";
        var cost = update.Parameters.Add("$cost", SqliteType.Text);
        var input = update.Parameters.Add("$in", SqliteType.Integer);
        var output = update.Parameters.Add("$out", SqliteType.Integer);
        var cache = update.Parameters.Add("$cache", SqliteType.Integer);
        var tools = update.Parameters.Add("$tools", SqliteType.Integer);
        var id = update.Parameters.Add("$id", SqliteType.Text);
        foreach (var (sessionId, v) in latest)
        {
            cost.Value = v.Cost;
            input.Value = v.Input;
            output.Value = v.Output;
            cache.Value = v.Cache;
            tools.Value = v.Tools;
            id.Value = sessionId;
            update.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Schema step 9 (backlog 109): a title the owner chose. Adds a nullable <c>custom_title</c> column
    /// to <c>agent_sessions</c> and nothing else: every existing row stays null, so a conversation nobody
    /// renamed keeps showing its derived <c>title</c>, which this step never touches. Idempotent-safe the
    /// way v8 is: the column is probed before it is added, so a replay against an inaccurate version
    /// marker adds nothing twice.
    /// </summary>
    private static void AddSessionCustomTitle(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (!ColumnExists(connection, "agent_sessions", "custom_title"))
        {
            SchemaMigrator.Execute(connection, transaction, "ALTER TABLE agent_sessions ADD COLUMN custom_title TEXT;");
        }
    }

    /// <summary>
    /// Schema step 11 (backlog 37): the work item tables of <see cref="V11WorkItems"/>, then
    /// <c>workspaces.project_id</c>, probed before it is added so a replay against an inaccurate version marker
    /// adds nothing twice. The column default puts every existing workspace in the seeded <c>default</c> project.
    /// <para>
    /// The workspaces table itself is probed too, because a step may run against a database that never had one:
    /// a migration test builds the shape of an older version by hand, and an install whose bridge was never used
    /// has no reason to carry it either. A step that assumed the table failed the whole upgrade on
    /// <c>no such table</c>, which would have stranded such a database at version 10 with no way forward.
    /// </para>
    /// </summary>
    private static void AddProjectsAndWorkItems(SqliteConnection connection, SqliteTransaction transaction)
    {
        SchemaMigrator.Execute(connection, transaction, V11WorkItems);
        if (TableExists(connection, "workspaces") && !ColumnExists(connection, "workspaces", "project_id"))
        {
            SchemaMigrator.Execute(
                connection,
                transaction,
                "ALTER TABLE workspaces ADD COLUMN project_id TEXT NOT NULL DEFAULT 'default';");
        }
    }

    /// <summary>
    /// Schema step 13 (backlog 37, increment C1): the agent layer. The five per-item values are columns on
    /// <c>work_items</c>, probed one by one so a replay against an inaccurate version marker adds nothing
    /// twice; everything that is a LIST is its own table, because an entry has an id that evidence and a
    /// readiness tick point at, which JSON would lose (plan section 5.2).
    /// </summary>
    private static void AddWorkItemAgentLayer(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (TableExists(connection, "work_items"))
        {
            foreach (var (column, type) in new[]
            {
                ("ambiguity_score", "INTEGER"),
                ("ambiguity_assessed_by", "TEXT"),
                ("ambiguity_assessed_utc", "TEXT"),
                ("change_budget_max_files", "INTEGER"),
                ("next_action", "TEXT"),
            })
            {
                if (!ColumnExists(connection, "work_items", column))
                {
                    SchemaMigrator.Execute(
                        connection, transaction, $"ALTER TABLE work_items ADD COLUMN {column} {type};");
                }
            }
        }

        SchemaMigrator.Execute(connection, transaction, V13AgentLayer);
    }

    /// <summary>Whether a table is present, so a step can skip work a database has no place for.</summary>
    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $t;";
        command.Parameters.AddWithValue("$t", table);
        return command.ExecuteScalar() is not null;
    }

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = $c;";
        command.Parameters.AddWithValue("$c", column);
        return command.ExecuteScalar() is not null;
    }

    /// <summary>A stats payload's money field as invariant text, matching how the write path stores it.</summary>
    private static string MoneyText(JsonObject payload, string name) =>
        payload.TryGetPropertyValue(name, out var value) && value is not null
            ? value.GetValue<decimal>().ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "0";

    private static long Number(JsonObject payload, string name) =>
        payload.TryGetPropertyValue(name, out var value) && value is not null ? value.GetValue<long>() : 0;
}
