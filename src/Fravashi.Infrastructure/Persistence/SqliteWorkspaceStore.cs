using Fravashi.Application.Persistence;

namespace Fravashi.Infrastructure.Persistence;

/// <summary>
/// <see cref="IWorkspaceStore"/> over the <c>workspaces</c> table (plan 2b-ii): the welcome page's
/// recents. Stamps use the round-trip "O" format like every other timestamp column here. <see cref="Touch"/>
/// never names <c>project_id</c>: a new workspace takes the column default, NULL since schema step 48, which is "not
/// set up" (item #373), and re-opening one never resets the project it was bound to (canonical artifact model plan
/// section 5.1).
/// </summary>
public sealed class SqliteWorkspaceStore : IWorkspaceStore
{
    private readonly ISqliteConnectionFactory _factory;

    public SqliteWorkspaceStore(ISqliteConnectionFactory factory) => _factory = factory;

    public IReadOnlyList<WorkspaceRecord> Recent(int max = 10)
    {
        var result = new List<WorkspaceRecord>();
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, name, root_path, last_opened_utc, project_id, former_project_id FROM workspaces " +
            "ORDER BY last_opened_utc DESC LIMIT $max;";
        command.Parameters.AddWithValue("$max", max);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(Read(reader));
        }

        return result;
    }

    public WorkspaceRecord? Get(string id)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, name, root_path, last_opened_utc, project_id, former_project_id FROM workspaces WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public void Touch(string id, string name, string rootPath)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO workspaces (id, name, root_path, last_opened_utc) VALUES ($id, $n, $p, $t) " +
            "ON CONFLICT(id) DO UPDATE SET name = excluded.name, root_path = excluded.root_path, " +
            "last_opened_utc = excluded.last_opened_utc;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$n", name);
        command.Parameters.AddWithValue("$p", rootPath);
        command.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void Remove(string id)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM workspaces WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// A row as a record, the columns in the order every query here selects them: id, name, root_path,
    /// last_opened_utc, project_id, former_project_id. Shared with <see cref="SqliteProjectStore"/>.
    /// </summary>
    internal static WorkspaceRecord Read(Microsoft.Data.Sqlite.SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        DateTimeOffset.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5));
}
