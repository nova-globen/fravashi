using Fravashi.Application.Persistence;
using Fravashi.Application.Projects;
using Fravashi.Domain.Projects;

namespace Fravashi.Web.Services;

/// <summary>
/// Whose a row on a cross-project surface is (item #373). Home's attention card, live sessions and recent conversations
/// read every folder of every project by design, so a row that does not name its project shows a first-time user work
/// that is not theirs as if it were (the v0.9.100 check: Default's #23 and #24 under an unbound Stomme, with nothing
/// saying so). Every such row names its project through here, on every Home.
/// </summary>
public static class ProjectLabels
{
    /// <summary>
    /// The project a folder is in and the folder's own name, as one label: "Default · Pare"; just "Fravashi" when the
    /// project is named as its folder is; "Stomme (not set up)" for a folder in no project. Null for a folder the store
    /// no longer knows (its recents row was dropped), of which nothing can be said.
    /// </summary>
    public static string? ForFolder(IWorkspaceStore workspaces, IProjectStore? projects, string workspaceId)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(workspaceId);
        if (workspaces.Get(workspaceId) is not { } record)
        {
            return null;
        }

        var folder = record.Name is { Length: > 0 } name ? name : workspaceId;
        var project = WorkspaceProjects.For(workspaces, workspaceId);
        return Join(project is null ? null : NameOf(projects, project), folder);
    }

    /// <summary>The label for a project and a folder name, by the rules of <see cref="ForFolder"/>.</summary>
    public static string Join(string? project, string folder) =>
        project is null ? $"{folder} (not set up)"
        : string.Equals(project, folder, StringComparison.OrdinalIgnoreCase) ? folder
        : $"{project} {Separator} {folder}";

    /// <summary>A project's name, or its id when the project is not known any more.</summary>
    public static string NameOf(IProjectStore? projects, ProjectId project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return projects?.Get(project)?.Name is { Length: > 0 } name ? name : project.Value;
    }

    /// <summary>The middle dot the studio's meta lines separate their parts with.</summary>
    public const string Separator = "·";
}
