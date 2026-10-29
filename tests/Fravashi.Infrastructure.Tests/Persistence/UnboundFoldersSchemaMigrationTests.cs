using AwesomeAssertions;
using Fravashi.Application.Projects;
using Fravashi.Domain.Projects;
using Fravashi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Persistence;

/// <summary>
/// Schema step 48 (item #373, <c>docs/plans/first-open-setup.md</c> section 1.2): NO SHARED BUCKET. A store at 47 whose
/// folders sit in the default project only because that was the column's default comes out with those folders NOT SET
/// UP (a NULL project, the old one kept in <c>former_project_id</c>), every folder bound to another project untouched,
/// and the default project and everything in it still there. The live tier runs it on a COPY of a real store, set by
/// <see cref="ReleaseSchemaMigrationTests.RealStoreVariable"/>.
/// </summary>
public sealed class UnboundFoldersSchemaMigrationTests : IDisposable
{
    private const string Stamp = "2026-10-01T00:00:00.0000000+00:00";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fravashi-unbound-migration", Guid.NewGuid().ToString("N"));

    public UnboundFoldersSchemaMigrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private SqliteConnectionFactory Factory(string name) =>
        new(new SqliteStoreOptions(Path.Combine(_directory, name), null, Pooling: false));

    private SqliteConnectionFactory At47()
    {
        var factory = Factory("at47.db");
        using var connection = factory.Open();
        SchemaMigrator.Run(connection, [.. SqliteSchema.Migrations.Where(m => m.Version <= 47)]);
        SchemaMigrator.StoredVersion(connection).Should().Be(47);
        return factory;
    }

    [Fact]
    public async Task Folders_in_the_default_project_are_unbound_and_every_other_folder_keeps_its_project()
    {
        var factory = At47();

        // Rows as the build that shipped 47 wrote them: Stomme and a proof folder in default by the column's default,
        // Fravashi's own folder bound to its project, and a default-project work item that must survive.
        using (var connection = factory.Open())
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = $"""
                INSERT INTO projects (id, name, next_item_number, created_utc, updated_utc)
                VALUES ('ca5417c7', 'Fravashi', 1, '{Stamp}', '{Stamp}');
                INSERT INTO workspaces (id, name, root_path, last_opened_utc)
                VALUES ('ws-core', 'Stomme', 'C:/Repos/Stomme', '{Stamp}'),
                       ('ws-proof', 'proof', 'C:/Repos/proof', '{Stamp}');
                INSERT INTO workspaces (id, name, root_path, last_opened_utc, project_id)
                VALUES ('ws-studio', 'Fravashi', 'C:/Repos/Fravashi', '{Stamp}', 'ca5417c7');
                INSERT INTO work_items (id, project_id, number, level, kind, title, description, status, resolution, rank, version, created_utc, updated_utc)
                VALUES ('early-1', 'default', 1, 'task', 'standard', 'An early item', 'd', 'ready', NULL, 1, 1, '{Stamp}', '{Stamp}');
                UPDATE projects SET next_item_number = 2 WHERE id = 'default';
                """;
            insert.ExecuteNonQuery();
        }

        SqliteSchema.EnsureCreated(factory);

        using (var connection = factory.Open())
        {
            SchemaMigrator.StoredVersion(connection).Should().Be(SqliteSchema.CurrentVersion);
            Scalar(connection, "SELECT COUNT(*) FROM projects WHERE id = 'default';").Should().Be(1L);
        }

        var workspaces = new SqliteWorkspaceStore(factory);
        var core = workspaces.Get("ws-core")!;
        core.ProjectId.Should().BeNull();
        core.FormerProjectId.Should().Be("default");
        core.Name.Should().Be("Stomme");
        core.RootPath.Should().Be("C:/Repos/Stomme");
        workspaces.Get("ws-proof")!.ProjectId.Should().BeNull();

        var studio = workspaces.Get("ws-studio")!;
        studio.ProjectId.Should().Be("ca5417c7");
        studio.FormerProjectId.Should().BeNull();
        WorkspaceProjects.For(workspaces, "ws-studio").Should().Be(new ProjectId("ca5417c7"));
        WorkspaceProjects.Place(workspaces, "ws-core").Should().Be(SessionPlacement.FolderNotSetUp);

        // Nothing in the default project went anywhere: it is reachable by joining it, and only so.
        var board = new Fravashi.Infrastructure.WorkItems.LocalTrackerResolver(factory, TimeProvider.System).For(DefaultProject.Id);
        (await board.GetByNumberAsync(1))!.Title.Should().Be("An early item");
        var projects = new SqliteProjectStore(factory, TimeProvider.System);
        projects.WorkspacesOf(DefaultProject.Id).Should().BeEmpty();
        projects.BindWorkspace("ws-core", DefaultProject.Id).Should().BeTrue();
        WorkspaceProjects.For(workspaces, "ws-core").Should().Be(DefaultProject.Id);
    }

    [Fact]
    public void A_folder_opened_after_the_step_is_not_set_up_and_reopening_a_bound_one_keeps_its_project()
    {
        var factory = Factory("fresh.db");
        SqliteSchema.EnsureCreated(factory);
        var workspaces = new SqliteWorkspaceStore(factory);
        var projects = new SqliteProjectStore(factory, TimeProvider.System);

        workspaces.Touch("ws-new", "Pare", "C:/Repos/Pare");
        workspaces.Get("ws-new")!.ProjectId.Should().BeNull();
        workspaces.Recent().Should().ContainSingle(w => w.Id == "ws-new" && w.ProjectId == null);

        var mine = projects.Create("Pare");
        projects.BindWorkspace("ws-new", mine.Id).Should().BeTrue();
        workspaces.Touch("ws-new", "Pare", "C:/Repos/Pare");
        workspaces.Get("ws-new")!.ProjectId.Should().Be(mine.Id.Value);
    }

    [Fact]
    public void The_step_is_idempotent_against_an_inaccurate_version_marker()
    {
        var factory = Factory("replay.db");
        SqliteSchema.EnsureCreated(factory);
        new SqliteWorkspaceStore(factory).Touch("ws-1", "repo", "C:/repo");
        using var connection = factory.Open();
        using (var stamp = connection.CreateCommand())
        {
            stamp.CommandText = "UPDATE settings SET value = '47' WHERE key = $k;";
            stamp.Parameters.AddWithValue("$k", SqliteSchema.SchemaVersionKey);
            stamp.ExecuteNonQuery();
        }

        var replay = () => SchemaMigrator.Run(connection, SqliteSchema.Migrations);

        replay.Should().NotThrow();
        SchemaMigrator.StoredVersion(connection).Should().Be(SqliteSchema.CurrentVersion);
        Scalar(connection, "SELECT COUNT(*) FROM workspaces;").Should().Be(1L);
    }

    [Fact]
    public void A_store_with_no_workspaces_table_gets_one_in_the_new_shape()
    {
        var factory = At47();
        using (var connection = factory.Open())
        using (var drop = connection.CreateCommand())
        {
            drop.CommandText = "DROP TABLE workspaces;";
            drop.ExecuteNonQuery();
        }

        SqliteSchema.EnsureCreated(factory);

        new SqliteWorkspaceStore(factory).Touch("ws-1", "repo", "C:/repo");
        new SqliteWorkspaceStore(factory).Get("ws-1")!.ProjectId.Should().BeNull();
    }

    [RealStoreFact]
    public void A_copy_of_a_real_store_unbinds_only_its_default_folders()
    {
        var source = Environment.GetEnvironmentVariable(ReleaseSchemaMigrationTests.RealStoreVariable)!;
        var copy = Path.Combine(_directory, "real-copy.db");
        using (var original = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = source,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString()))
        using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Pooling = false }.ToString()))
        {
            original.Open();
            target.Open();
            original.BackupDatabase(target);
        }

        var factory = new SqliteConnectionFactory(new SqliteStoreOptions(copy, null, Pooling: false));
        Dictionary<string, string?> before;
        using (var connection = factory.Open())
        {
            before = Bindings(connection);
        }

        SqliteSchema.EnsureCreated(factory);

        using (var connection = factory.Open())
        {
            SchemaMigrator.StoredVersion(connection).Should().Be(SqliteSchema.CurrentVersion);
            var after = Bindings(connection);
            after.Keys.Should().BeEquivalentTo(before.Keys, "no folder row is lost");
            foreach (var (id, project) in before)
            {
                after[id].Should().Be(project == "default" ? null : project, $"folder {id} was in {project ?? "none"}");
            }
        }
    }

    private static Dictionary<string, string?> Bindings(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, project_id FROM workspaces;";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        while (reader.Read())
        {
            result[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        return result;
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }
}
