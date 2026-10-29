using AwesomeAssertions;
using Fravashi.Infrastructure.Persistence;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Backlog 102: rows written BEFORE the redactor existed are rewritten exactly once, on the first open
/// at schema v7. The scrub is keyed on the stored schema version (the same mechanism every schema bump
/// here uses), so it never runs a second time: a raw row planted after the scrub stays raw in the
/// database and is the display path's problem, which is exactly the division of labour the design wants.
/// </summary>
public class TranscriptScrubTests : IDisposable
{
    private const string FakeKey = "sk-" + "ant-api03-Fake0123456789Fake0123456789Fake0123456789FakeAA";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fravashi-scrub-" + Guid.NewGuid().ToString("N"));

    private readonly SqliteConnectionFactory _factory;

    public TranscriptScrubTests()
    {
        Directory.CreateDirectory(_directory);
        _factory = new SqliteConnectionFactory(
            new SqliteStoreOptions(Path.Combine(_directory, "fravashi.db"), Pooling: false));
        SqliteSchema.EnsureCreated(_factory);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        // No ClearAllPools: it is process-global and racing parallel SQLite classes; Pooling: false above
        // makes it unnecessary (backlog 89).
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Opening_a_pre_v7_database_redacts_the_stored_rows_once()
    {
        // A row persisted by a build with no redaction, in a database still stamped v6.
        PlantRawRow("s1", "ANTHROPIC_API_KEY=" + FakeKey);
        StampVersion(6);

        SqliteSchema.EnsureCreated(_factory);

        var payload = ReadPayloads("s1").Single();
        payload.Should().NotContain(FakeKey);
        payload.Should().Contain("[redacted:ANTHROPIC_API_KEY]");
        ReadVersion().Should().Be(SqliteSchema.CurrentVersion.ToString());
    }

    [Fact]
    public void The_scrub_does_not_run_a_second_time()
    {
        StampVersion(6);
        SqliteSchema.EnsureCreated(_factory); // the one-time scrub, stamping the current version

        // A raw row planted AFTER the scrub (only possible by writing SQL directly, as this test does).
        PlantRawRow("s2", "ANTHROPIC_API_KEY=" + FakeKey);
        SqliteSchema.EnsureCreated(_factory);

        ReadPayloads("s2").Single().Should().Contain(FakeKey);
    }

    [Fact]
    public void The_scrub_leaves_ordinary_rows_byte_identical()
    {
        var payload = PlantRawRow("s3", "var TOKEN = ReadToken(); // commit d788bbf3c1a94be00e2f5cf05a1c6c02e6b7a4d9");
        StampVersion(6);

        SqliteSchema.EnsureCreated(_factory);

        ReadPayloads("s3").Single().Should().Be(payload);
    }

    private string PlantRawRow(string sessionId, string detail)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            timestamp = "2026-08-04T18:00:00.0000000+00:00",
            toolUseId = "toolu_1",
            toolName = "Bash",
            success = true,
            detail,
        });

        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO agent_session_events (session_id, kind, payload, created_utc) " +
            "VALUES ($s, 'tool_completed', $p, '2026-08-04T18:00:00.0000000+00:00');";
        command.Parameters.AddWithValue("$s", sessionId);
        command.Parameters.AddWithValue("$p", payload);
        command.ExecuteNonQuery();
        return payload;
    }

    private void StampVersion(int version)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO settings (key, value) VALUES ($k, $v) " +
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("$k", SqliteSchema.SchemaVersionKey);
        command.Parameters.AddWithValue("$v", version.ToString());
        command.ExecuteNonQuery();
    }

    private string? ReadVersion()
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $k;";
        command.Parameters.AddWithValue("$k", SqliteSchema.SchemaVersionKey);
        return command.ExecuteScalar() as string;
    }

    private List<string> ReadPayloads(string sessionId)
    {
        var payloads = new List<string>();
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM agent_session_events WHERE session_id = $s;";
        command.Parameters.AddWithValue("$s", sessionId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            payloads.Add(reader.GetString(0));
        }

        return payloads;
    }
}
