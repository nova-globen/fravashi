using Fravashi.Application.Persistence;
using Fravashi.Application.Projects;
using Fravashi.Domain.Projects;

namespace Fravashi.Infrastructure.Bridge;

/// <summary>
/// <see cref="ICurrentProject"/> for a studio window (backlog 121): the project of the workspace this circuit's
/// window is bound to, read from the workspace's record at each use.
/// <para>
/// Item #373, no shared bucket: a folder that is not set up, a folder with no record, and a worktree whose repository
/// folder has no record all answer NULL, and the page shows the "not set up" state rather than some project's data. So
/// does a window with the bridge configured and no folder (the start dialog renders instead of the shell there, so no
/// page should ask). The one place the default project is still the answer is a studio run with NO BRIDGE at all, the
/// host-side <c>dotnet run</c> on <c>Repository:Root</c>: it has no folder record anyone could set up, and a developer's
/// own studio keeps a board.
/// </para>
/// </summary>
public sealed class WorkspaceCurrentProject(BridgeWorkspaceContext workspace, IWorkspaceStore workspaces) : ICurrentProject
{
    /// <inheritdoc />
    public ProjectId? Id => workspace.ActiveWorkspaceId is { Length: > 0 } id
        ? WorkspaceProjects.For(workspaces, id)
        : workspace.IsBridgeConfigured ? null : DefaultProject.Id;
}
