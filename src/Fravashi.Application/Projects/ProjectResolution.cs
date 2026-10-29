using Fravashi.Application.Persistence;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Projects;

namespace Fravashi.Application.Projects;

/// <summary>
/// The project the surface in front of a person is showing (backlog 121, <c>docs/plans/project-selection.md</c>).
/// The project FOLLOWS THE WORKSPACE: a window is bound to one workspace for its whole life, a workspace belongs
/// to at most one project, and so every page in that window follows it. Read at each use rather than cached,
/// because binding a workspace elsewhere is a thing that can happen while a page is open.
/// </summary>
public interface ICurrentProject
{
    /// <summary>
    /// The project of this window's folder, or null when the folder is NOT SET UP (item #373,
    /// <c>docs/plans/first-open-setup.md</c>). Null is never quietly turned into the default project: a page that
    /// reads a project's data shows the "not set up" state instead.
    /// </summary>
    ProjectId? Id { get; }
}

/// <summary>Thrown by <see cref="CurrentProjects.Require"/> when a handler runs for a folder that is not set up.</summary>
public sealed class FolderNotSetUpException()
    : InvalidOperationException("This folder is not set up yet, so it has no project to act on. Set it up first; nothing was changed.");

/// <summary>Helpers over <see cref="ICurrentProject"/>.</summary>
public static class CurrentProjects
{
    /// <summary>
    /// The project, for a handler that only runs once the page has already decided the folder is set up. It throws
    /// <see cref="FolderNotSetUpException"/> rather than hand anything the default, which is what a null-forgiving
    /// operator would silently risk.
    /// </summary>
    public static ProjectId Require(this ICurrentProject current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current.Id ?? throw new FolderNotSetUpException();
    }

    /// <summary>True when the window's folder is in a project.</summary>
    public static bool IsSetUp(this ICurrentProject current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current.Id is not null;
    }
}

/// <summary>Where an agent session, or a folder, stands with respect to a project (item #373).</summary>
public enum SessionPlacement
{
    /// <summary>The studio does not know the session, or its folder has no record.</summary>
    Unknown,

    /// <summary>The folder is known and not set up: it belongs to no project.</summary>
    FolderNotSetUp,

    /// <summary>The session acts in its folder's project.</summary>
    InProject,
}

/// <summary>
/// The project an AGENT SESSION acts on: the project of the workspace the session runs in. Resolved per call
/// and never held by a singleton, because the tool server, the governance and the git work are each one object
/// for the whole process, and a project cached on one of them is exactly how a session's tools end up pointed
/// at a graph nobody pointed them at.
/// </summary>
public interface ISessionProjects
{
    /// <summary>
    /// The session's project, or null when the studio does not know the session or its folder is not set up. Null
    /// is never quietly turned into the default project by a caller: an unknown session acting on SOME graph is the
    /// failure this exists to prevent, so the caller refuses in words.
    /// </summary>
    ProjectId? For(AgentSessionId session);

    /// <summary>
    /// Why <see cref="For"/> answered what it did, so a refusal can say "this folder is not set up" rather than
    /// "the studio does not know this session". An implementation that cannot tell the two apart says Unknown.
    /// </summary>
    SessionPlacement Place(AgentSessionId session) =>
        For(session) is null ? SessionPlacement.Unknown : SessionPlacement.InProject;
}

/// <summary>
/// Where the default project is DECIDED, and with <see cref="ProjectId.Default"/> itself and the schema's seeded
/// row, the only code allowed to name it (a source-reading test holds that). Since item #373 a FOLDER is never put
/// in it by default: a folder nobody set up belongs to no project. What is left is the seeded row's id, used by a
/// host with no workspace records at all (<see cref="SingleProject"/>), the framework seed, and the dev fallback of a
/// studio run with no bridge (<c>NoFolderFallsIntoTheDefaultProjectTests</c> holds that list).
/// </summary>
public static class DefaultProject
{
    /// <summary>The seeded project's id.</summary>
    public static ProjectId Id => ProjectId.Default;

    /// <summary>
    /// True when <paramref name="id"/> names the seeded shared project. The setup (item #373) asks this to say that a
    /// folder used to be in it, and to name it as the shared project earlier versions used; it never resolves a folder
    /// into it.
    /// </summary>
    public static bool Is(string? id) => string.Equals(id, ProjectId.Default.Value, StringComparison.Ordinal);
}

/// <summary>
/// The project a workspace ACTS in (backlog 319). An ordinary folder's is its own record's. An autopilot worktree has
/// none of its own: it is RESOLVED from its repository folder's record at each use, the way its trust is (#312,
/// <see cref="RepositoryFolder"/>), so a worktree row written in the wrong project, or a folder moved to another
/// project after its worktrees were made, never needs correcting. The worktree row's own project column is not read.
/// </summary>
public static class WorkspaceProjects
{
    /// <summary>The project a folder record names, or null when there is no record or the folder is not set up.</summary>
    public static ProjectId? Of(WorkspaceRecord? workspace) =>
        workspace is { ProjectId: { } id } && !string.IsNullOrWhiteSpace(id) ? new ProjectId(id) : null;

    /// <summary>
    /// The project of <paramref name="workspaceId"/>, or null when the folder is not set up, has no record, is a
    /// worktree whose repository folder has no record, or the id is blank. The default is never substituted.
    /// </summary>
    public static ProjectId? For(IWorkspaceStore workspaces, string? workspaceId)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return null;
        }

        // Read once: the record is the answer for an ordinary folder, and only a worktree's leads to another.
        var record = workspaces.Get(workspaceId);
        if (!RepositoryFolder.IsWorktree(record))
        {
            return Of(record);
        }

        return RepositoryFolder.RepositoryOf(workspaces, record!) is { } repository ? Of(repository) : null;
    }

    /// <summary>
    /// Where <paramref name="workspaceId"/> stands: in a project, known and not set up, or unknown (no record, or a
    /// worktree whose repository folder has none).
    /// </summary>
    public static SessionPlacement Place(IWorkspaceStore workspaces, string? workspaceId)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        if (string.IsNullOrWhiteSpace(workspaceId) || workspaces.Get(workspaceId) is not { } record)
        {
            return SessionPlacement.Unknown;
        }

        var folder = RepositoryFolder.IsWorktree(record) ? RepositoryFolder.RepositoryOf(workspaces, record) : record;
        if (folder is null)
        {
            return SessionPlacement.Unknown;
        }

        return Of(folder) is null ? SessionPlacement.FolderNotSetUp : SessionPlacement.InProject;
    }

    /// <summary>
    /// True when <paramref name="workspaceId"/> is a known folder (or a worktree of one) that is NOT SET UP. A blank id
    /// or an unknown folder is not "not set up": there is no folder to set up, and other guards answer for it.
    /// </summary>
    public static bool IsNotSetUp(IWorkspaceStore? workspaces, string? workspaceId) =>
        workspaces is not null && Place(workspaces, workspaceId) == SessionPlacement.FolderNotSetUp;

    /// <summary>
    /// The most recently opened FOLDER bound to <paramref name="project"/>, never a worktree: a worktree is made from
    /// a run's own repository and is never the project's folder (#308). A folder that is not set up is in no project
    /// and is never found.
    /// </summary>
    public static string? NewestFolderIn(IWorkspaceStore workspaces, ProjectId project)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(project);
        return workspaces.Recent(int.MaxValue)
            .FirstOrDefault(w => !RepositoryFolder.IsWorktree(w) && Of(w) == project)?.Id;
    }
}

/// <summary>
/// <see cref="ISessionProjects"/> over the session index and the workspace records: session, then its workspace,
/// then that workspace's project (its repository folder's, for a worktree).
/// </summary>
public sealed class SessionProjects(ISessionStore sessions, IWorkspaceStore workspaces) : ISessionProjects
{
    /// <inheritdoc />
    public ProjectId? For(AgentSessionId session) =>
        sessions.Find(session) is { } record ? WorkspaceProjects.For(workspaces, record.WorkspaceId) : null;

    /// <inheritdoc />
    public SessionPlacement Place(AgentSessionId session) =>
        sessions.Find(session) is { } record ? WorkspaceProjects.Place(workspaces, record.WorkspaceId) : SessionPlacement.Unknown;
}

/// <summary>
/// A host with one project and no workspace records to ask: every session and every window is in the default
/// project. It is what a component falls back to when nothing was registered (a test that builds one by hand),
/// the same way the tool server falls back to its default grants. The product always registers the real ones.
/// </summary>
public sealed class SingleProject : ISessionProjects, ICurrentProject
{
    /// <summary>The one instance.</summary>
    public static SingleProject Instance { get; } = new();

    /// <inheritdoc />
    public ProjectId? Id => DefaultProject.Id;

    /// <inheritdoc />
    public ProjectId? For(AgentSessionId session) => DefaultProject.Id;
}

/// <summary>
/// What a page says about the project it is showing (backlog 121). Nothing at all while the install has ONE
/// project, so an install that never makes a second sees no change; with more than one, the project's name rides
/// on the page title, because a board that does not say whose it is invites work in the wrong one.
/// </summary>
public static class ProjectTitle
{
    /// <summary>
    /// <paramref name="title"/>, followed by the project's name when there is more than one project; the title alone
    /// for a folder that is not set up (item #373: it shows no project's name, because it shows no project).
    /// </summary>
    public static string With(string title, IProjectStore projects, ProjectId? current)
    {
        ArgumentNullException.ThrowIfNull(projects);

        return current is not null && projects.List().Count > 1 && projects.Get(current) is { } project
            ? $"{title} - {project.Name}"
            : title;
    }
}
