using System.Linq;
using Fravashi.Domain.Autopilot;

namespace Fravashi.Application.Persistence;

/// <summary>
/// A known host workspace (plan 2b-ii): the welcome page's recents source. <paramref name="ProjectId"/> is the
/// project the workspace belongs to (canonical artifact model plan section 2), or null while the folder is NOT SET
/// UP (item #373, <c>docs/plans/first-open-setup.md</c>): a folder nobody put in a project belongs to none, and is
/// never quietly counted in the default one.
/// </summary>
/// <param name="FormerProjectId">
/// The project the folder was in before schema step 48 unbound it (only ever <c>default</c>), so the setup can say
/// that project's work is still there; null for every other folder.
/// </param>
public sealed record WorkspaceRecord(
    string Id,
    string Name,
    string RootPath,
    DateTimeOffset LastOpenedUtc,
    string? ProjectId = null,
    string? FormerProjectId = null);

/// <summary>
/// Persisted workspace records in the single studio DB (decided 2026-08-01: one DB,
/// workspace-scoped rows). Ids are the bridge's stable workspace ids (a hash of the canonical
/// host path), so recents survive restarts and re-registrations.
/// </summary>
public interface IWorkspaceStore
{
    /// <summary>Newest-first recents.</summary>
    IReadOnlyList<WorkspaceRecord> Recent(int max = 10);

    WorkspaceRecord? Get(string id);

    /// <summary>Upserts the record and stamps <see cref="WorkspaceRecord.LastOpenedUtc"/> now.</summary>
    void Touch(string id, string name, string rootPath);

    void Remove(string id);

    /// <summary>
    /// The record whose RootPath matches, compared the same way a repository's identity is (case-insensitive,
    /// slash-normalized host paths, <see cref="AutopilotRepositoryIdentity.Normalize"/>), or null when none is
    /// registered (backlog 312: a worktree's trust is read from its repository's OWN record, found this way).
    /// Default-implemented by scanning every record, so an existing implementation keeps compiling unchanged;
    /// a store backed by a real index may answer with a direct lookup instead.
    /// </summary>
    WorkspaceRecord? FindByRootPath(string rootPath)
    {
        var normalized = AutopilotRepositoryIdentity.Normalize(rootPath);
        return Recent(int.MaxValue).FirstOrDefault(w =>
            string.Equals(AutopilotRepositoryIdentity.Normalize(w.RootPath), normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Newest-first recents a PERSON opened: <see cref="Recent"/> without the autopilot's worktrees (backlog 319). A
    /// worktree is registered so the studio can group and govern it, never so it is offered back as a folder to open.
    /// </summary>
    IReadOnlyList<WorkspaceRecord> RecentFolders(int max = 10) =>
        [.. Recent(int.MaxValue).Where(w => !RepositoryFolder.IsWorktree(w)).Take(max)];
}

/// <summary>
/// Which folder's record governs a workspace (backlogs 312 and 319): the workspace itself for an ordinary folder,
/// and for an autopilot worktree the repository folder it was made from (<see cref="AutopilotRepositoryIdentity"/>).
/// A worktree has no trust and no project of its own; both are read from the folder this answers, so the two can
/// never disagree about which record is meant.
/// </summary>
public static class RepositoryFolder
{
    /// <summary>Whether a record is an autopilot worktree rather than a folder a person opened.</summary>
    public static bool IsWorktree(WorkspaceRecord? workspace) =>
        workspace?.RootPath is { Length: > 0 } root && AutopilotRepositoryIdentity.IsWorktree(root);

    /// <summary>
    /// The id of the record that governs <paramref name="workspaceId"/>: itself for an ordinary folder or one with no
    /// record to read a RootPath from; its repository folder's id for a worktree. Null when the id is blank, or when a
    /// worktree's repository has no record: there is nothing to read, and never the worktree's own row.
    /// </summary>
    public static string? IdFor(IWorkspaceStore workspaces, string? workspaceId)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return null;
        }

        var record = workspaces.Get(workspaceId);
        return IsWorktree(record) ? RepositoryOf(workspaces, record!)?.Id : workspaceId;
    }

    /// <summary>The repository folder's record for a worktree's record, or null when that folder has none.</summary>
    public static WorkspaceRecord? RepositoryOf(IWorkspaceStore workspaces, WorkspaceRecord worktree)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(worktree);
        return workspaces.FindByRootPath(AutopilotRepositoryIdentity.For(worktree.RootPath));
    }

    /// <summary>The worktree records made from <paramref name="repository"/>, by the same identity.</summary>
    public static IReadOnlyList<WorkspaceRecord> WorktreesOf(IWorkspaceStore workspaces, WorkspaceRecord repository)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(repository);
        if (repository.RootPath is not { Length: > 0 } || IsWorktree(repository))
        {
            return [];
        }

        var root = AutopilotRepositoryIdentity.Normalize(repository.RootPath);
        return
        [
            .. workspaces.Recent(int.MaxValue).Where(w =>
                IsWorktree(w)
                && string.Equals(AutopilotRepositoryIdentity.For(w.RootPath), root, StringComparison.OrdinalIgnoreCase)),
        ];
    }
}
