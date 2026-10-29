using Fravashi.Application.Persistence;
using Fravashi.Domain.Projects;

namespace Fravashi.Application.Projects;

/// <summary>
/// Projects and the workspace binding (plan section 2, decisions D1 and D3). Every install has the seeded
/// <see cref="ProjectId.Default"/> project, and a workspace belongs to exactly one project, so binding a
/// workspace MOVES it rather than adding a second membership. The surfaces for this are backlog item 38's.
/// </summary>
public interface IProjectStore
{
    /// <summary>Every project, by name.</summary>
    IReadOnlyList<ProjectRecord> List();

    /// <summary>The project, or null.</summary>
    ProjectRecord? Get(ProjectId id);

    /// <summary>Creates a project with a fresh id. A blank name is refused with an <see cref="ArgumentException"/>.</summary>
    ProjectRecord Create(string name);

    /// <summary>Renames a project; false when it does not exist.</summary>
    bool Rename(ProjectId id, string name);

    /// <summary>Moves a known workspace into a project; false when either does not exist.</summary>
    bool BindWorkspace(string workspaceId, ProjectId project);

    /// <summary>The workspaces bound to a project, most recently opened first.</summary>
    IReadOnlyList<WorkspaceRecord> WorkspacesOf(ProjectId project);
}
