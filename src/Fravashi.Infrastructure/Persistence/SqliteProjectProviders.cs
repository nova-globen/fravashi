using Fravashi.Application.Projects;
using Fravashi.Domain.Projects;

namespace Fravashi.Infrastructure.Persistence;

/// <summary>A project's provider choices in the studio's database (schema step 31, backlog 142).</summary>
public sealed class SqliteProjectProviders(ISqliteConnectionFactory factory) : IProjectProviders
{
    private readonly ISqliteConnectionFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    /// <inheritdoc />
    public ProjectProviderChoice For(ProjectId project)
    {
        ArgumentNullException.ThrowIfNull(project);
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT board, wiki FROM project_providers WHERE project_id = $p;";
        command.Parameters.AddWithValue("$p", project.Value);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new ProjectProviderChoice(Parse(reader.GetString(0)), Parse(reader.GetString(1)))
            : ProjectProviderChoice.Default;
    }

    /// <inheritdoc />
    public void Set(ProjectId project, ProjectProviderChoice choice)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(choice);
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO project_providers (project_id, board, wiki) VALUES ($p, $b, $w) "
            + "ON CONFLICT(project_id) DO UPDATE SET board = excluded.board, wiki = excluded.wiki;";
        command.Parameters.AddWithValue("$p", project.Value);
        command.Parameters.AddWithValue("$b", Name(choice.Board));
        command.Parameters.AddWithValue("$w", Name(choice.Wiki));
        command.ExecuteNonQuery();
    }

    private static string Name(ProviderChoice choice) => choice == ProviderChoice.None ? "none" : "install";

    // A value this build does not know reads as the install's connection, the answer a project gave before it chose.
    private static ProviderChoice Parse(string text) =>
        string.Equals(text, "none", StringComparison.Ordinal) ? ProviderChoice.None : ProviderChoice.Install;
}
