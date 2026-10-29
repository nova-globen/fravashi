using Fravashi.Application.Persistence;

namespace Fravashi.Infrastructure.Persistence;

/// <summary>
/// <see cref="IConfigurationStore"/> over the <c>settings</c> table. This is the write side of config-in-DB;
/// the read side that feeds <see cref="Microsoft.Extensions.Configuration.IConfiguration"/> is
/// <c>SqliteConfigurationProvider</c>, which reads the same table at startup.
/// </summary>
public sealed class SqliteConfigurationStore : IConfigurationStore
{
    private readonly ISqliteConnectionFactory _factory;

    public SqliteConfigurationStore(ISqliteConnectionFactory factory) => _factory = factory;

    public string? Get(string key)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $k;";
        command.Parameters.AddWithValue("$k", key);
        return command.ExecuteScalar() as string;
    }

    public IReadOnlyDictionary<string, string?> GetAll()
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM settings;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        return result;
    }

    public void Set(string key, string? value)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        Upsert(command, key, value);
        command.ExecuteNonQuery();
    }

    public void SetMany(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return;
        }

        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var (key, value) in values)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            Upsert(command, key, value);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void Remove(string key)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM settings WHERE key = $k;";
        command.Parameters.AddWithValue("$k", key);
        command.ExecuteNonQuery();
    }

    private static void Upsert(Microsoft.Data.Sqlite.SqliteCommand command, string key, string? value)
    {
        command.CommandText =
            "INSERT INTO settings (key, value) VALUES ($k, $v) " +
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("$k", key);
        command.Parameters.AddWithValue("$v", (object?)value ?? DBNull.Value);
    }
}
