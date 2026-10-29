using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Fravashi.Domain;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Persistence;

/// <summary>
/// Schema step 49, the bridge release of the internal rename (<c>docs/plans/rename-internal-names.md</c>, phase 2): a
/// store at 48 whose rows carry a former name (the studio assistant's settings keys, a stored hook or setting naming the
/// studio's tools by their former MCP prefix) comes out on the new names, with nothing else in it changed; and the
/// studio opens the database under its former file name while the launcher has not renamed it yet.
/// </summary>
public sealed class FormerNamesSchemaMigrationTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fravashi-former-migration", Guid.NewGuid().ToString("N"));

    public FormerNamesSchemaMigrationTests() => Directory.CreateDirectory(_directory);

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

    private SqliteConnectionFactory At48()
    {
        var factory = new SqliteConnectionFactory(new SqliteStoreOptions(Path.Combine(_directory, "at48.db"), null, Pooling: false));
        using var connection = factory.Open();
        SchemaMigrator.Run(connection, [.. SqliteSchema.Migrations.Where(m => m.Version <= 48)]);
        SchemaMigrator.StoredVersion(connection).Should().Be(48);
        return factory;
    }

    private static void Set(SqliteConnection connection, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO settings (key, value) VALUES ($k, $v);";
        command.Parameters.AddWithValue("$k", key);
        command.Parameters.AddWithValue("$v", value);
        command.ExecuteNonQuery();
    }

    private static string? Get(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $k;";
        command.Parameters.AddWithValue("$k", key);
        return command.ExecuteScalar() as string;
    }

    [Fact]
    public void The_studio_assistant_settings_and_stored_tool_names_take_their_new_names()
    {
        var factory = At48();
        var formerTool = FormerNames.StudioToolPrefix + "work_item_create";
        using (var connection = factory.Open())
        {
            Set(connection, FormerNames.StudioAssistantModelKey, "opus");
            Set(connection, FormerNames.StudioAssistantEffortKey, "high");
            Set(connection, FormerNames.StudioAssistantTopicPrefix + "board", "the board topic");
            Set(connection, "Unrelated:Key", "keeps " + formerTool + " in its value, rewritten");
            Set(connection, "Untouched:Key", "nothing to do here");
            using var artifact = connection.CreateCommand();
            artifact.CommandText = """
                INSERT INTO framework_artifacts (id, kind, name, payload_json, created_utc, updated_utc)
                VALUES ('a1', 'hook', 'guard', $p, '2026-10-01', '2026-10-01');
                """;
            artifact.Parameters.AddWithValue("$p", "{\"matcher\":\"" + formerTool + "|Bash\"}");
            artifact.ExecuteNonQuery();
        }

        SqliteSchema.EnsureCreated(factory);

        using (var connection = factory.Open())
        {
            SchemaMigrator.StoredVersion(connection).Should().Be(SqliteSchema.CurrentVersion);
            Get(connection, AgentDefaultsSetting.StudioAssistantModelKey).Should().Be("opus");
            Get(connection, AgentDefaultsSetting.StudioAssistantEffortKey).Should().Be("high");
            Get(connection, StudioAssistantTopic.Prefix + "board").Should().Be("the board topic");
            Get(connection, FormerNames.StudioAssistantModelKey).Should().BeNull("the former row goes once it has moved");
            Get(connection, FormerNames.StudioAssistantTopicPrefix + "board").Should().BeNull();
            Get(connection, "Unrelated:Key").Should().Be("keeps " + AgentWorkGate.StudioToolPrefix + "work_item_create in its value, rewritten");
            Get(connection, "Untouched:Key").Should().Be("nothing to do here");
            using var read = connection.CreateCommand();
            read.CommandText = "SELECT payload_json FROM framework_artifacts WHERE id = 'a1';";
            ((string)read.ExecuteScalar()!).Should().Be("{\"matcher\":\"" + AgentWorkGate.StudioToolPrefix + "work_item_create|Bash\"}");
        }
    }

    [Fact]
    public void A_value_already_stored_under_the_new_key_wins()
    {
        var factory = At48();
        using (var connection = factory.Open())
        {
            Set(connection, FormerNames.StudioAssistantModelKey, "haiku");
            Set(connection, AgentDefaultsSetting.StudioAssistantModelKey, "sonnet");
        }

        SqliteSchema.EnsureCreated(factory);

        using var after = factory.Open();
        Get(after, AgentDefaultsSetting.StudioAssistantModelKey).Should().Be("sonnet");
        Get(after, FormerNames.StudioAssistantModelKey).Should().BeNull();
    }

    [Fact]
    public void The_launcher_knows_the_schema_the_studio_now_writes()
    {
        SqliteSchema.CurrentVersion.Should().Be(49);
    }

    [Fact]
    public void The_studio_opens_the_database_under_its_former_name_until_it_is_renamed()
    {
        var data = new DataDirectory(Path.Combine(_directory, "data"));
        data.DatabasePath.Should().EndWith(DataDirectory.DatabaseFileName, "a fresh install gets the current name");

        File.WriteAllText(Path.Combine(data.Path, FormerNames.DatabaseFileName), string.Empty);
        data.DatabasePath.Should().EndWith(FormerNames.DatabaseFileName, "never an empty new database beside the real one");

        File.WriteAllText(Path.Combine(data.Path, DataDirectory.DatabaseFileName), string.Empty);
        data.DatabasePath.Should().EndWith(DataDirectory.DatabaseFileName, "once renamed, the current name is the one");
    }
}
