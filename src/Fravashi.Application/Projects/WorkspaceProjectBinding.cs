using Fravashi.Application.Persistence;
using Fravashi.Domain.Projects;

namespace Fravashi.Application.Projects;

/// <summary>What came of asking to move a folder to a project: it moved, or a sentence saying why not.</summary>
/// <param name="Moved">True when the folder is now in the project asked for.</param>
/// <param name="Refusal">Why it was not moved, for a person to read; null when it was.</param>
public sealed record ProjectMove(bool Moved, string? Refusal)
{
    /// <summary>The folder moved.</summary>
    public static ProjectMove Done { get; } = new(true, null);

    /// <summary>The folder did not move, and this is why.</summary>
    public static ProjectMove Refused(string why) => new(false, why);
}

/// <summary>
/// Choosing a project (backlog 121, <c>docs/plans/project-selection.md</c>). The project follows the workspace,
/// so choosing one is an act on the WORKSPACE, made once and persisted, and every window and every agent session
/// on that folder follows it from the next thing they do.
/// <para>
/// The one rule with teeth: a folder with a conversation RUNNING is not moved. The agent's tools resolve their
/// project at each call, so a move made mid-conversation would point a running session at another graph between
/// two tool calls, with its work item and plan links still in the old one. Refusing costs the person ending a
/// conversation first; allowing it is the failure the item was opened for. A conversation that is not running
/// keeps the links it made in the project it made them in.
/// </para>
/// </summary>
/// <param name="projects">The projects, and the binding itself.</param>
/// <param name="workspaces">The known folders.</param>
/// <param name="hasRunningConversation">Whether a folder has a conversation running right now.</param>
public sealed class WorkspaceProjectBinding(
    IProjectStore projects, IWorkspaceStore workspaces, Func<string, bool> hasRunningConversation)
{
    /// <summary>Moves a folder into an existing project.</summary>
    public ProjectMove Move(string workspaceId, ProjectId target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (Blocked(workspaceId) is { } blocked)
        {
            return blocked;
        }

        if (projects.Get(target) is not { } project)
        {
            return ProjectMove.Refused("There is no such project any more. Pick one from the list.");
        }

        // The folder's own column (item #373): a folder that is not set up is in no project, so joining any project,
        // the default one included, is a real move.
        if (WorkspaceProjects.Of(workspaces.Get(workspaceId)) == target)
        {
            return ProjectMove.Refused($"This folder is already in {project.Name}.");
        }

        return projects.BindWorkspace(workspaceId, target)
            ? ProjectMove.Done
            : ProjectMove.Refused("The folder could not be moved. Nothing was changed.");
    }

    /// <summary>Makes a project and moves the folder into it, or does neither.</summary>
    public ProjectMove MoveToNew(string workspaceId, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ProjectMove.Refused("A project needs a name.");
        }

        // Checked BEFORE the project is made, so a refused attempt never leaves an empty project behind.
        if (Blocked(workspaceId) is { } blocked)
        {
            return blocked;
        }

        var created = projects.Create(name.Trim());
        return projects.BindWorkspace(workspaceId, created.Id)
            ? ProjectMove.Done
            : ProjectMove.Refused($"{created.Name} was made, and the folder could not be moved into it.");
    }

    /// <summary>
    /// Why the folder cannot be moved right now (no such folder, a worktree, a conversation running), or null when it
    /// can. The setup (item #373) asks this BEFORE it makes a project, so a refused setup never leaves an empty one.
    /// </summary>
    public ProjectMove? Check(string workspaceId) => Blocked(workspaceId);

    private ProjectMove? Blocked(string workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || workspaces.Get(workspaceId) is not { } folder)
        {
            return ProjectMove.Refused("No folder is open in this window, so there is nothing to move.");
        }

        // Backlog 319: a worktree's project is its repository folder's, read through, so moving the worktree itself
        // would change nothing anybody reads.
        if (RepositoryFolder.IsWorktree(folder))
        {
            return ProjectMove.Refused(
                "This folder is a worktree the autopilot made, and it is always in its repository folder's project. "
                + "Move the repository folder instead.");
        }

        // And the repository folder's worktrees act in ITS project, so a conversation running in any of them is
        // running in this folder's project as far as a move is concerned.
        return hasRunningConversation(workspaceId)
            || RepositoryFolder.WorktreesOf(workspaces, folder).Any(w => hasRunningConversation(w.Id))
            ? ProjectMove.Refused(
                "This folder has a conversation running (in the folder or in one of its worktrees). End it first, "
                + "then move the folder: a move made now would point that conversation's tools at another project "
                + "between two steps.")
            : null;
    }
}
