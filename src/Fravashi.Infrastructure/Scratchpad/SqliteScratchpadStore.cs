using System.Globalization;
using Fravashi.Application.Scratchpad;
using Fravashi.Infrastructure.Persistence;

namespace Fravashi.Infrastructure.Scratchpad;

/// <summary>
/// The scratchpad in the <c>scratchpad</c> table (schema step 29): one row, pinned to id 1.
/// <para>
/// The text goes in and comes out exactly as written. It is the feature itself (copying out gives markdown), so
/// this store trims nothing, converts no line endings and interprets nothing, which is also why a checklist
/// line is not special here: <c>- [x]</c> is text until something renders it.
/// </para>
/// </summary>
public sealed class SqliteScratchpadStore(ISqliteConnectionFactory factory) : IScratchpadStore
{
    private readonly ISqliteConnectionFactory _factory = factory;

    /// <inheritdoc />
    public string Read()
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT markdown FROM scratchpad WHERE id = 1;";
        return command.ExecuteScalar() as string ?? string.Empty;
    }

    /// <inheritdoc />
    public void Save(string? markdown)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();

        // An upsert, so saving never depends on a row having been created first: a fresh install has none.
        command.CommandText =
            "INSERT INTO scratchpad (id, markdown, updated_utc) VALUES (1, $markdown, $now) " +
            "ON CONFLICT(id) DO UPDATE SET markdown = excluded.markdown, updated_utc = excluded.updated_utc;";
        command.Parameters.AddWithValue("$markdown", markdown ?? string.Empty);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }
}
