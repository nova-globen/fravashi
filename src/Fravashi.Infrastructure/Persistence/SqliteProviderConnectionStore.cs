using Fravashi.Application.Persistence;
using Microsoft.Data.Sqlite;

namespace Fravashi.Infrastructure.Persistence;

/// <summary><see cref="IProviderConnectionStore"/> over the <c>provider_connections</c> table.</summary>
public sealed class SqliteProviderConnectionStore : IProviderConnectionStore
{
    private const string SelectColumns = "id, kind, base_url, credential_ref, status, updated_utc";

    private readonly ISqliteConnectionFactory _factory;

    public SqliteProviderConnectionStore(ISqliteConnectionFactory factory) => _factory = factory;

    public IReadOnlyList<ProviderConnection> GetAll()
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM provider_connections ORDER BY id;";
        return Read(command);
    }

    public IReadOnlyList<ProviderConnection> GetByKind(ProviderKind kind)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM provider_connections WHERE kind = $kind ORDER BY id;";
        command.Parameters.AddWithValue("$kind", kind.ToString());
        return Read(command);
    }

    public ProviderConnection? Get(string id)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM provider_connections WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        var rows = Read(command);
        return rows.Count > 0 ? rows[0] : null;
    }

    public void Upsert(ProviderConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var db = _factory.Open();
        using var command = db.CreateCommand();
        command.CommandText =
            "INSERT INTO provider_connections (id, kind, base_url, credential_ref, status, updated_utc) " +
            "VALUES ($id, $kind, $base_url, $credential_ref, $status, $updated_utc) " +
            "ON CONFLICT(id) DO UPDATE SET " +
            "kind = excluded.kind, base_url = excluded.base_url, credential_ref = excluded.credential_ref, " +
            "status = excluded.status, updated_utc = excluded.updated_utc;";
        command.Parameters.AddWithValue("$id", connection.Id);
        command.Parameters.AddWithValue("$kind", connection.Kind.ToString());
        command.Parameters.AddWithValue("$base_url", (object?)connection.BaseUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$credential_ref", (object?)connection.CredentialRef ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", connection.Status);
        command.Parameters.AddWithValue("$updated_utc", connection.UpdatedUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void Remove(string id)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM provider_connections WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static List<ProviderConnection> Read(SqliteCommand command)
    {
        var result = new List<ProviderConnection>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ProviderConnection(
                Id: reader.GetString(0),
                Kind: Enum.Parse<ProviderKind>(reader.GetString(1)),
                BaseUrl: reader.IsDBNull(2) ? null : reader.GetString(2),
                CredentialRef: reader.IsDBNull(3) ? null : reader.GetString(3),
                Status: reader.GetString(4),
                UpdatedUtc: DateTimeOffset.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        }

        return result;
    }
}
