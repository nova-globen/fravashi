using Microsoft.Data.Sqlite;

namespace Fravashi.Infrastructure.Persistence;

/// <summary>
/// Creates connections to the SQLite database at <see cref="SqliteStoreOptions.DatabasePath"/>. The file
/// is created on first open (<see cref="SqliteOpenMode.ReadWriteCreate"/>). WAL journaling keeps the
/// single-user app responsive; <c>busy_timeout</c> absorbs brief contention between the config provider
/// and the stores.
/// </summary>
public sealed class SqliteConnectionFactory : ISqliteConnectionFactory
{
    private readonly string _connectionString;
    private readonly string? _vectorExtensionPath;

    public SqliteConnectionFactory(SqliteStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = options.Pooling,
        }.ToString();

        _vectorExtensionPath = options.VectorExtensionPath;
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText =
                "PRAGMA journal_mode=WAL;" +
                "PRAGMA foreign_keys=ON;" +
                "PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
        }

        // Vector seam (ADR 0003 V6): a loadable vector extension such as sqlite-vec can be enabled here
        // without touching any store or schema code. Not wired to a package now - only the load point is
        // reserved. When adopted, set SqliteStoreOptions.VectorExtensionPath (and ship the native lib).
        if (!string.IsNullOrWhiteSpace(_vectorExtensionPath))
        {
            connection.EnableExtensions(true);
            connection.LoadExtension(_vectorExtensionPath);
        }

        return connection;
    }
}
