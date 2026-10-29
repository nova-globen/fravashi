using AwesomeAssertions;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Scratchpad;
using Fravashi.Infrastructure.Tests.Persistence;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Scratchpad;

/// <summary>
/// The scratchpad's storage (backlog 67), against a real file-backed database with the production schema. The
/// acceptance asks that what was written survives a restart and travels in a backup, and the second follows
/// from the first only because it lives in the database, so that is what these hold.
/// </summary>
public sealed class SqliteScratchpadStoreTests : IDisposable
{
    private readonly SqliteTestContext _context = new();

    public void Dispose() => _context.Dispose();

    [Fact]
    public void A_fresh_install_has_an_empty_scratchpad_rather_than_an_error()
    {
        new SqliteScratchpadStore(_context.Factory).Read().Should().BeEmpty();
    }

    /// <summary>
    /// Byte for byte, because the text IS the feature: copying out gives markdown, so anything the store
    /// normalised (line endings, trailing spaces, a checkbox's exact spelling) would change what is copied.
    /// </summary>
    [Fact]
    public void What_was_saved_is_read_back_exactly()
    {
        const string Markdown = "# Today\r\n\r\n- [ ] Ring the bank  \n- [x] **Ship** the *fix*\n1. [a link](https://example.test)\nåäö ✓\n";
        var store = new SqliteScratchpadStore(_context.Factory);

        store.Save(Markdown);

        store.Read().Should().Be(Markdown);
    }

    [Fact]
    public void Saving_again_replaces_rather_than_appends()
    {
        var store = new SqliteScratchpadStore(_context.Factory);

        store.Save("first");
        store.Save("second");

        store.Read().Should().Be("second");
        Count("SELECT COUNT(*) FROM scratchpad;").Should().Be(1, "there is exactly one scratchpad per install");
    }

    [Fact]
    public void It_survives_a_restart()
    {
        new SqliteScratchpadStore(_context.Factory).Save("- [ ] still here after a restart");

        // A new store over the same database is what a restarted studio gets.
        new SqliteScratchpadStore(_context.Factory).Read().Should().Be("- [ ] still here after a restart");
    }

    [Fact]
    public void A_null_is_saved_as_empty_not_ignored()
    {
        var store = new SqliteScratchpadStore(_context.Factory);
        store.Save("something");

        store.Save(null);

        store.Read().Should().BeEmpty("clearing the scratchpad has to clear it");
    }

    [Fact]
    public void The_table_arrives_with_its_own_schema_step()
    {
        SqliteSchema.CurrentVersion.Should().BeGreaterThanOrEqualTo(29);
        Count("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'scratchpad';").Should().Be(1);
    }

    private long Count(string sql)
    {
        using var connection = _context.Factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }
}
