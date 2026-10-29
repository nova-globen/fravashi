using System.Globalization;
using Fravashi.Application.Projects;
using Fravashi.Domain.Projects;

namespace Fravashi.Infrastructure.Persistence;

/// <summary>
/// <see cref="IProjectContents"/> as two counts over the project's own rows (item #373): the setup's "Join an existing
/// project" list says how much each project holds without reading any of it.
/// </summary>
public sealed class SqliteProjectContents(ISqliteConnectionFactory factory) : IProjectContents
{
    /// <inheritdoc />
    public ProjectContents Of(ProjectId project)
    {
        ArgumentNullException.ThrowIfNull(project);
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT (SELECT COUNT(*) FROM work_items WHERE project_id = $p), (SELECT COUNT(*) FROM wiki_items WHERE project_id = $p);";
        command.Parameters.AddWithValue("$p", project.Value);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new ProjectContents(
                Convert.ToInt32(reader.GetInt64(0), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetInt64(1), CultureInfo.InvariantCulture))
            : ProjectContents.Empty;
    }
}
