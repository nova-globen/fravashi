using AwesomeAssertions;
using Fravashi.Application.Releases;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Releases;
using Fravashi.Infrastructure.WorkItems;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Persistence;

/// <summary>
/// Schema step 47 (work item #368): a store at 46 gains the release tables and <c>work_items.released_in</c>, keeps
/// every row, reads every item back unreleased (so the board shows exactly what it showed), and can then record a
/// release. The live tier runs the same step on a COPY of a real store (taken with SQLite's own backup, the original
/// opened read-only), set by <see cref="RealStoreVariable"/>.
/// </summary>
public sealed class ReleaseSchemaMigrationTests : IDisposable
{
    /// <summary>The path of a real studio database to copy and migrate; the live test skips without it.</summary>
    public const string RealStoreVariable = "FRAVASHI_REAL_STORE";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fravashi-release-migration", Guid.NewGuid().ToString("N"));

    public ReleaseSchemaMigrationTests() => Directory.CreateDirectory(_directory);

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

    [Fact]
    public async Task A_store_created_at_46_migrates_to_47_keeping_its_items_and_can_record_a_release()
    {
        var factory = Factory("at46.db");
        using (var connection = factory.Open())
        {
            SchemaMigrator.Run(connection, [.. SqliteSchema.Migrations.Where(m => m.Version <= 46)]);
            SchemaMigrator.StoredVersion(connection).Should().Be(46);
        }

        // Items as the build that shipped 46 wrote them, in raw SQL: released_in does not exist yet.
        using (var connection = factory.Open())
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO work_items (id, project_id, number, level, kind, title, description, status, resolution, rank, version, created_utc, updated_utc)
                VALUES ('done-1', 'default', 1, 'task', 'standard', 'Done before the release step', 'd', 'done', 'completed', 1, 3,
                        '2026-10-01T00:00:00.0000000+00:00', '2026-10-01T00:00:00.0000000+00:00'),
                       ('open-2', 'default', 2, 'task', 'standard', 'Open', 'd', 'ready', NULL, 2, 1,
                        '2026-10-01T00:00:00.0000000+00:00', '2026-10-01T00:00:00.0000000+00:00');
                UPDATE projects SET next_item_number = 3 WHERE id = 'default';
                """;
            insert.ExecuteNonQuery();
            Columns(connection, "work_items").Should().NotContain("released_in");
        }

        SqliteSchema.EnsureCreated(factory);

        using (var connection = factory.Open())
        {
            SchemaMigrator.StoredVersion(connection).Should().Be(SqliteSchema.CurrentVersion);
            Columns(connection, "work_items").Should().Contain("released_in");
            Columns(connection, "releases").Should().Contain(["version", "tag", "commit_sha", "notes", "wiki_number"]);
            Columns(connection, "release_items").Should().Contain(["release_id", "item_id"]);
            Scalar(connection, "SELECT COUNT(*) FROM work_items WHERE released_in IS NOT NULL;").Should().Be(0L);
        }

        var board = new LocalTrackerResolver(factory, TimeProvider.System).For(ProjectId.Default);
        (await board.QueryAsync(new WorkItemQuery { Release = ReleaseFilter.NotArchived })).Items.Should().HaveCount(2);
        var kept = await board.GetByNumberAsync(1);
        kept!.Title.Should().Be("Done before the release step");
        kept.Version.Should().Be(3);
        kept.ReleasedIn.Should().BeNull();

        var saved = await new SqliteReleaseStores(factory, TimeProvider.System).For(ProjectId.Default).CreateAsync(
            new NewRelease { Version = "v0.9.97", Tag = "v0.9.97", Notes = "n", Items = [new WorkItemId("done-1")] });
        saved.Saved.Should().BeTrue();
        (await board.QueryAsync(new WorkItemQuery { Release = ReleaseFilter.NotArchived })).Items.Should().ContainSingle();
    }

    [Fact]
    public void The_step_is_idempotent_against_an_inaccurate_version_marker()
    {
        var factory = Factory("replay.db");
        SqliteSchema.EnsureCreated(factory);
        using var connection = factory.Open();
        using (var stamp = connection.CreateCommand())
        {
            stamp.CommandText = "UPDATE settings SET value = '46' WHERE key = $k;";
            stamp.Parameters.AddWithValue("$k", SqliteSchema.SchemaVersionKey);
            stamp.ExecuteNonQuery();
        }

        var replay = () => SchemaMigrator.Run(connection, SqliteSchema.Migrations);

        replay.Should().NotThrow();
        SchemaMigrator.StoredVersion(connection).Should().Be(SqliteSchema.CurrentVersion);
    }

    [RealStoreFact]
    public async Task A_copy_of_a_real_store_migrates_and_every_item_reads_back_unreleased()
    {
        var source = Environment.GetEnvironmentVariable(RealStoreVariable)!;
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
        long items;
        long done;
        long fromVersion;
        using (var connection = factory.Open())
        {
            fromVersion = SchemaMigrator.StoredVersion(connection);
            items = (long)Scalar(connection, "SELECT COUNT(*) FROM work_items;")!;
            done = (long)Scalar(connection, "SELECT COUNT(*) FROM work_items WHERE status = 'done';")!;
        }

        SqliteSchema.EnsureCreated(factory);

        using (var connection = factory.Open())
        {
            SchemaMigrator.StoredVersion(connection).Should().Be(SqliteSchema.CurrentVersion, $"the copy was at {fromVersion}");
            Scalar(connection, "SELECT COUNT(*) FROM work_items;").Should().Be(items);
            Scalar(connection, "SELECT COUNT(*) FROM work_items WHERE released_in IS NOT NULL;")
                .Should().Be(fromVersion >= 47 ? Scalar(connection, "SELECT COUNT(*) FROM release_items;") : 0L);
        }

        // Every project's items read back through the board, and the Done ones are exactly what a release would offer.
        var projects = new List<string>();
        using (var connection = factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT DISTINCT project_id FROM work_items;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                projects.Add(reader.GetString(0));
            }
        }

        var resolver = new LocalTrackerResolver(factory, TimeProvider.System);
        long read = 0;
        long offered = 0;
        foreach (var project in projects)
        {
            var board = resolver.For(new ProjectId(project));
            string? cursor = null;
            do
            {
                var page = await board.QueryAsync(new WorkItemQuery { Limit = WorkItemQuery.MaxLimit, Cursor = cursor });
                read += page.Items.Count;
                cursor = page.NextCursor;
            }
            while (cursor is not null);

            offered += (await new ReleaseRecorder(
                    new SqliteReleaseStores(factory, TimeProvider.System), resolver,
                    new Fravashi.Infrastructure.Wiki.SqliteWikiLibraries(factory, TimeProvider.System), new WorkItemChanges())
                .CandidatesAsync(new ProjectId(project))).Count;
        }

        read.Should().Be(items);
        if (fromVersion < 47)
        {
            offered.Should().Be(done);
        }
    }

    private static List<string> Columns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}');";
        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }
}

/// <summary>A fact that runs only with a real store to copy, set in the constructor because Assert.Skip needs xunit v3.</summary>
internal sealed class RealStoreFactAttribute : FactAttribute
{
    public RealStoreFactAttribute()
    {
        var path = Environment.GetEnvironmentVariable(ReleaseSchemaMigrationTests.RealStoreVariable);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Skip = $"Real store tier: set {ReleaseSchemaMigrationTests.RealStoreVariable} to a studio database file to copy and migrate.";
        }
    }
}
