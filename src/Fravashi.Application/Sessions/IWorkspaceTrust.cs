using Fravashi.Application.Persistence;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Autopilot;

namespace Fravashi.Application.Sessions;

/// <summary>
/// The trust level a person chose for each workspace (ADR 0028, backlog 152). It fails toward
/// <see cref="WorkspaceTrust.Ask"/>: a store that cannot say what a folder's level is answers with the one
/// that asks, because "we could not tell" must never read as "run it".
/// </summary>
public interface IWorkspaceTrust
{
    /// <summary>Raised with the workspace id when a level changes.</summary>
    event Action<string>? Changed;

    /// <summary>The level for a workspace; Ask when none was ever chosen or the store cannot answer.</summary>
    WorkspaceTrust For(string workspaceId);

    /// <summary>Sets a workspace's level. Choosing Ask again removes the row rather than storing a default.</summary>
    void Set(string workspaceId, WorkspaceTrust trust);
}

/// <summary>
/// Which workspace id a trust level actually governs (backlog 312). A worktree (#306, #308) never has its
/// own level: the studio's approval broker reads it from the repository the worktree was made from, and
/// every OTHER surface that shows or changes a level (the session work strip) must resolve the SAME id this
/// does, or it would show and edit a level nothing reads.
/// </summary>
public static class WorkspaceTrustScope
{
    /// <summary>
    /// The workspace id whose <see cref="IWorkspaceTrust"/> row governs <paramref name="workspaceId"/>: itself,
    /// for an ordinary folder or one with no <see cref="IWorkspaceStore"/> record to read a RootPath from; its
    /// repository folder's own id for a worktree (<see cref="AutopilotRepositoryIdentity"/>, the same identity
    /// #307's concurrency gate already uses). Null only when a worktree's repository has no record in the
    /// workspace store: there is nothing to read or to write, never the worktree's own id, which nothing
    /// governs. With no workspace store wired this answers <paramref name="workspaceId"/> unconditionally, the
    /// rule exactly as it stood before worktrees existed.
    /// </summary>
    public static string? For(IWorkspaceStore? workspaceStore, string? workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return null;
        }

        // Backlog 319: the same answer a worktree's PROJECT is read through, so trust and project name one folder.
        return workspaceStore is null ? workspaceId : RepositoryFolder.IdFor(workspaceStore, workspaceId);
    }
}
