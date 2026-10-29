using Fravashi.Application.Projects;
using Fravashi.Domain.Autopilot;
using Fravashi.Domain.Projects;

namespace Fravashi.Infrastructure.Persistence;

/// <summary>A project's autopilot delivery choice in the studio's database (schema step 40, backlog 298).</summary>
public sealed class SqliteProjectDelivery(ISqliteConnectionFactory factory) : IProjectDelivery
{
    private readonly ISqliteConnectionFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    /// <inheritdoc />
    public AutopilotDeliveryMode For(ProjectId project)
    {
        ArgumentNullException.ThrowIfNull(project);
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT delivery FROM project_delivery WHERE project_id = $p;";
        command.Parameters.AddWithValue("$p", project.Value);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Parse(reader.GetString(0)) : AutopilotDeliveryMode.BranchAndPullRequest;
    }

    /// <inheritdoc />
    public void Set(ProjectId project, AutopilotDeliveryMode mode)
    {
        ArgumentNullException.ThrowIfNull(project);
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO project_delivery (project_id, delivery) VALUES ($p, $d) "
            + "ON CONFLICT(project_id) DO UPDATE SET delivery = excluded.delivery;";
        command.Parameters.AddWithValue("$p", project.Value);
        command.Parameters.AddWithValue("$d", Name(mode));
        command.ExecuteNonQuery();
    }

    private static string Name(AutopilotDeliveryMode mode) =>
        mode == AutopilotDeliveryMode.CommitToMain ? "commit_to_main" : "branch_and_pull_request";

    // A value this build does not know reads as the default, the answer a project gave before it chose.
    private static AutopilotDeliveryMode Parse(string text) =>
        string.Equals(text, "commit_to_main", StringComparison.Ordinal)
            ? AutopilotDeliveryMode.CommitToMain
            : AutopilotDeliveryMode.BranchAndPullRequest;
}
