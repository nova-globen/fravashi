using Fravashi.Application.Persistence;

namespace Fravashi.Infrastructure.Persistence;

/// <summary>
/// <see cref="IGitHostStore"/> over the <c>git_hosts</c> table (plan 2c, decision 8). Hosts are
/// normalized to lowercase on write AND lookup so the store never holds case-duplicates.
/// </summary>
public sealed class SqliteGitHostStore : IGitHostStore
{
    private readonly ISqliteConnectionFactory _factory;

    public SqliteGitHostStore(ISqliteConnectionFactory factory) => _factory = factory;

    public IReadOnlyList<GitHostRecord> GetAll()
    {
        var result = new List<GitHostRecord>();
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT host, provider, credential_ref, updated_utc FROM git_hosts ORDER BY host;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(Read(reader));
        }

        return result;
    }

    public GitHostRecord? Get(string host)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT host, provider, credential_ref, updated_utc FROM git_hosts WHERE host = $h;";
        command.Parameters.AddWithValue("$h", Normalize(host));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public void Upsert(GitHostRecord record)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO git_hosts (host, provider, credential_ref, updated_utc) VALUES ($h, $p, $r, $t) " +
            "ON CONFLICT(host) DO UPDATE SET provider = excluded.provider, " +
            "credential_ref = excluded.credential_ref, updated_utc = excluded.updated_utc;";
        command.Parameters.AddWithValue("$h", Normalize(record.Host));
        command.Parameters.AddWithValue("$p", record.Provider);
        command.Parameters.AddWithValue("$r", record.CredentialRef);
        command.Parameters.AddWithValue("$t", record.UpdatedUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void Remove(string host)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM git_hosts WHERE host = $h;";
        command.Parameters.AddWithValue("$h", Normalize(host));
        command.ExecuteNonQuery();
    }

    private static string Normalize(string host) => host.Trim().ToLowerInvariant();

    private static GitHostRecord Read(Microsoft.Data.Sqlite.SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        DateTimeOffset.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind));
}
