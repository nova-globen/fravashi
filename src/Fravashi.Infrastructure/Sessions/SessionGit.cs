using Fravashi.Application.AgentSync;
using Fravashi.Application.Git;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.AgentSync;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>
/// Which repository a session's git runs in, and whether that repository has seen everything the session
/// wrote (backlog 150). A session is bound to one workspace for its life (backlog 121), the workspace is a host
/// folder, and the folder's git is reached through the bridge pinned to that workspace id: this is the one
/// place a session id becomes those services, so no tool works it out for itself.
/// </summary>
public sealed class SessionGit(ISessionStore sessions, IWorkspaceGit git, IWorkspaceAgentSync? sync = null)
{
    private readonly ISessionStore _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    private readonly IWorkspaceGit _git = git ?? throw new ArgumentNullException(nameof(git));

    /// <summary>The workspace the session runs in, or null when the studio has no record of the session.</summary>
    public string? WorkspaceIdFor(AgentSessionId session) =>
        _sessions.Find(session)?.WorkspaceId is { Length: > 0 } id ? id : null;

    /// <summary>The git seams for the session's workspace, or null when there is no git to reach for it.</summary>
    public WorkspaceGitServices? For(AgentSessionId session) =>
        WorkspaceIdFor(session) is { } id ? _git.For(id) : null;

    /// <summary>
    /// The session's contained copy path (Linux, where a file's owner-executable bit is real), or null when
    /// no copy is synced for its workspace: a test, a dev host, or the local-clone fallback, none of which
    /// separate an agent's copy from the host's own working tree (#327).
    /// </summary>
    public string? CopyPathFor(AgentSessionId session) =>
        sync is not null && WorkspaceIdFor(session) is { } id ? sync.CopyPathFor(id) : null;

    /// <summary>
    /// Waits until the session's edits have all reached the host folder. Null when no copy is synced for the
    /// session's workspace (a test, or a dev host), which means there is nothing between the agent's writes
    /// and git and no reason to wait.
    /// </summary>
    public Task<bool?> SettleAsync(AgentSessionId session, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        sync is null || WorkspaceIdFor(session) is not { } id
            ? Task.FromResult<bool?>(null)
            : sync.SettleAsync(id, timeout, cancellationToken);

    /// <summary>How many of the session's writes are still on their way to the host, as far as the engine knows.</summary>
    public int PendingWriteBacks(AgentSessionId session) =>
        sync is not null && WorkspaceIdFor(session) is { } id && sync.StatusFor(id) is WorkspaceSyncStatus status
            ? status.PendingWriteBacks
            : 0;
}
