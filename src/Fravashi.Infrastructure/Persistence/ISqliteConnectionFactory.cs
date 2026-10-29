using Microsoft.Data.Sqlite;

namespace Fravashi.Infrastructure.Persistence;

/// <summary>Opens ready-to-use connections to the Fravashi SQLite database.</summary>
public interface ISqliteConnectionFactory
{
    /// <summary>
    /// Opens a new connection with the standard pragmas applied (WAL journal, foreign keys on) and, if a
    /// vector extension is configured, loaded. The caller owns disposal.
    /// </summary>
    SqliteConnection Open();
}
