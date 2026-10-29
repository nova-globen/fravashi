using Microsoft.Data.Sqlite;

namespace Fravashi.Infrastructure.Persistence;

/// <summary>
/// The ordered-steps migration runner (backlog 60). It reads the recorded schema version once, then runs
/// every <see cref="SchemaMigration"/> whose target version is above it, in ascending order, each inside
/// its own transaction with the version marker stamped in that SAME transaction. The stamp is therefore a
/// CONSEQUENCE of a completed step, never an unconditional write: the old bootstrap ran the whole
/// create-script and stamped the current version regardless, so a database at an older shape got marked
/// current while stale. Here a fresh database (version 0) replays every step and lands at current; an
/// existing database runs only the steps above its recorded version; a database already at the top runs
/// none.
/// </summary>
public static class SchemaMigrator
{
    /// <summary>The settings-table key under which the applied schema version is recorded.</summary>
    public const string SchemaVersionKey = "__schema_version";

    /// <summary>
    /// Applies every migration above the database's recorded version, in order, each transactionally with
    /// its own version stamp. Safe to call on every open: a database already at the chain's top does no work.
    /// </summary>
    public static void Run(SqliteConnection connection, IReadOnlyList<SchemaMigration> migrations)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(migrations);

        var current = StoredVersion(connection);

        foreach (var migration in migrations.Where(m => m.Version > current).OrderBy(m => m.Version))
        {
            using var transaction = connection.BeginTransaction();
            migration.Apply(connection, transaction);
            Stamp(connection, transaction, migration.Version);
            transaction.Commit();
        }
    }

    /// <summary>
    /// Reads the recorded schema version, returning 0 when the marker is absent or unparseable: a fresh
    /// database, or one that predates the marker being read. The <c>settings</c> table may not exist yet,
    /// which also reads as version 0.
    /// </summary>
    public static int StoredVersion(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $k;";
        command.Parameters.AddWithValue("$k", SchemaVersionKey);

        try
        {
            return command.ExecuteScalar() is string stored && int.TryParse(stored, out var version)
                ? version
                : 0;
        }
        catch (SqliteException)
        {
            // The settings table does not exist yet (it is created by step 1): a brand-new database is
            // version 0. Preparing a SELECT against a missing table throws rather than returning nothing.
            return 0;
        }
    }

    /// <summary>Executes a DDL/DML script on the connection, enrolled in the migration's transaction.</summary>
    public static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Stamp(SqliteConnection connection, SqliteTransaction transaction, int version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO settings (key, value) VALUES ($k, $v) " +
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("$k", SchemaVersionKey);
        command.Parameters.AddWithValue("$v", version.ToString());
        command.ExecuteNonQuery();
    }
}
