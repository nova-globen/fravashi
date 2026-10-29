using System.Globalization;
using Fravashi.Application.Persistence;
using Fravashi.Application.Projects;
using Fravashi.Domain.Projects;
using Microsoft.Data.Sqlite;

namespace Fravashi.Infrastructure.Persistence;

/// <summary>
/// <see cref="IProjectStore"/> over the <c>projects</c> table and <c>workspaces.project_id</c> (schema v11,
/// canonical artifact model plan section 2). A workspace has exactly one project column, so binding moves it.
/// </summary>
public sealed class SqliteProjectStore(ISqliteConnectionFactory factory, TimeProvider clock) : IProjectStore
{
    private readonly ISqliteConnectionFactory _factory = factory;
    private readonly TimeProvider _clock = clock;

    public IReadOnlyList<ProjectRecord> List()
    {
        var result = new List<ProjectRecord>();
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, created_utc, updated_utc FROM projects ORDER BY name, id;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadProject(reader));
        }

        return result;
    }

    public ProjectRecord? Get(ProjectId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, created_utc, updated_utc FROM projects WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.Value);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadProject(reader) : null;
    }

    public ProjectRecord Create(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A project needs a name.", nameof(name));
        }

        var now = _clock.GetUtcNow();
        var record = new ProjectRecord(new ProjectId(Guid.NewGuid().ToString("N")), name.Trim(), now, now);
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO projects (id, name, next_item_number, created_utc, updated_utc) VALUES ($id, $n, 1, $t, $t);";
        command.Parameters.AddWithValue("$id", record.Id.Value);
        command.Parameters.AddWithValue("$n", record.Name);
        command.Parameters.AddWithValue("$t", Stamp(now));
        command.ExecuteNonQuery();
        return record;
    }

    public bool Rename(ProjectId id, string name)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A project needs a name.", nameof(name));
        }

        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE projects SET name = $n, updated_utc = $t WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.Value);
        command.Parameters.AddWithValue("$n", name.Trim());
        command.Parameters.AddWithValue("$t", Stamp(_clock.GetUtcNow()));
        return command.ExecuteNonQuery() == 1;
    }

    public bool BindWorkspace(string workspaceId, ProjectId project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentNullException.ThrowIfNull(project);
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE workspaces SET project_id = $p WHERE id = $id AND EXISTS (SELECT 1 FROM projects WHERE id = $p);";
        command.Parameters.AddWithValue("$id", workspaceId);
        command.Parameters.AddWithValue("$p", project.Value);
        return command.ExecuteNonQuery() == 1;
    }

    public IReadOnlyList<WorkspaceRecord> WorkspacesOf(ProjectId project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var result = new List<WorkspaceRecord>();
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, name, root_path, last_opened_utc, project_id, former_project_id FROM workspaces WHERE project_id = $p " +
            "ORDER BY last_opened_utc DESC;";
        command.Parameters.AddWithValue("$p", project.Value);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(SqliteWorkspaceStore.Read(reader));
        }

        return result;
    }

    private static ProjectRecord ReadProject(SqliteDataReader reader) => new(
        new ProjectId(reader.GetString(0)),
        reader.GetString(1),
        ParseStamp(reader.GetString(2)),
        ParseStamp(reader.GetString(3)));

    private static string Stamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseStamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
