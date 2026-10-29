using System.Globalization;
using Fravashi.Application.Persistence;
using Fravashi.Application.Projects;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Projects;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fravashi.Infrastructure.Persistence;

/// <summary>
/// <see cref="IProjectEnvironment"/> over <c>project_variables</c> (schema step 35). A plain value sits in the row;
/// a secret's value sits in the protected secret store under a reference the row carries, so it is encrypted at
/// rest like every other credential here and a backup carries it the same way.
/// </summary>
public sealed class SqliteProjectEnvironment(
    ISqliteConnectionFactory factory,
    ISecretStore secrets,
    TimeProvider clock,
    ILogger<SqliteProjectEnvironment>? logger = null) : IProjectEnvironment
{
    private readonly ILogger _logger = logger ?? NullLogger<SqliteProjectEnvironment>.Instance;

    /// <inheritdoc />
    public event Action<ProjectId>? Changed;

    /// <inheritdoc />
    public IReadOnlyList<ProjectVariable> List(ProjectId project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var rows = new List<ProjectVariable>();
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name, value, is_secret, updated_utc FROM project_variables WHERE project_id = $p ORDER BY name;";
        command.Parameters.AddWithValue("$p", project.Value);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var secret = reader.GetInt64(2) != 0;
            rows.Add(new ProjectVariable(
                reader.GetString(0),
                secret || reader.IsDBNull(1) ? null : reader.GetString(1),
                secret,
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture)));
        }

        return rows;
    }

    /// <inheritdoc />
    public string? Set(ProjectId project, string name, string value, bool secret)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (ProjectVariableNames.Refusal(name) is { } refusal)
        {
            return refusal;
        }

        var trimmed = name.Trim();
        value ??= string.Empty;

        // A name that turns from secret to plain (or the reverse) must not leave its old value behind in the other
        // place, so the previous secret is removed either way and written again only when it still is one.
        secrets.Remove(SecretRef(project, trimmed));
        if (secret)
        {
            secrets.Set(SecretRef(project, trimmed), value);
        }

        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO project_variables (project_id, name, value, is_secret, updated_utc) VALUES ($p, $n, $v, $s, $u) "
            + "ON CONFLICT (project_id, name) DO UPDATE SET value = $v, is_secret = $s, updated_utc = $u;";
        command.Parameters.AddWithValue("$p", project.Value);
        command.Parameters.AddWithValue("$n", trimmed);
        command.Parameters.AddWithValue("$v", secret ? DBNull.Value : value);
        command.Parameters.AddWithValue("$s", secret ? 1 : 0);
        command.Parameters.AddWithValue("$u", clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();

        Changed?.Invoke(project);
        return null;
    }

    /// <inheritdoc />
    public void Remove(ProjectId project, string name)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var trimmed = name.Trim();
        secrets.Remove(SecretRef(project, trimmed));

        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM project_variables WHERE project_id = $p AND name = $n;";
        command.Parameters.AddWithValue("$p", project.Value);
        command.Parameters.AddWithValue("$n", trimmed);
        command.ExecuteNonQuery();

        Changed?.Invoke(project);
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> Resolve(ProjectId project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var (name, plain, secret) in Rows(project))
            {
                // A row stored before its name was refused (backlog 189 refused two) would otherwise override what the
                // studio set, because project variables are applied after Restrict.
                if (ProjectVariableNames.Refusal(name) is not null)
                {
                    _logger.LogWarning(
                        "Project {ProjectId} has a variable {Name} that a project may no longer set; it is not handed to agents.",
                        project.Value,
                        name);
                    continue;
                }

                var value = secret ? secrets.Get(SecretRef(project, name)) : plain;
                if (value is not null)
                {
                    values[name] = value;
                }
            }
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException)
        {
            // A child started with nothing is a child that can still run; the variables it misses are said in the log.
            _logger.LogWarning(ex, "The studio could not read the variables of project {ProjectId}.", project.Value);
        }

        return values;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<AgentHeldCredential> Held()
    {
        var held = new List<AgentHeldCredential>();
        try
        {
            using var connection = factory.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT project_id, name FROM project_variables WHERE is_secret = 1;";
            using var reader = command.ExecuteReader();
            var refs = new List<(string Project, string Name)>();
            while (reader.Read())
            {
                refs.Add((reader.GetString(0), reader.GetString(1)));
            }

            foreach (var (project, name) in refs)
            {
                if (secrets.Get(SecretRef(new ProjectId(project), name)) is { Length: > 0 } value)
                {
                    held.Add(new AgentHeldCredential(name, value));
                }
            }
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException)
        {
            _logger.LogWarning(ex, "The studio could not read the projects' secret variables for the guards.");
        }

        return held;
    }

    private IEnumerable<(string Name, string? Value, bool Secret)> Rows(ProjectId project)
    {
        var rows = new List<(string, string?, bool)>();
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, value, is_secret FROM project_variables WHERE project_id = $p;";
        command.Parameters.AddWithValue("$p", project.Value);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt64(2) != 0));
        }

        return rows;
    }

    /// <summary>Where a secret variable's value lives in the secret store.</summary>
    public static string SecretRef(ProjectId project, string name) => $"project:{project.Value}:env:{name}";
}
