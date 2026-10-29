namespace Fravashi.Application.AgentSync;

/// <summary>
/// The contained agents' workspace-copy engine (plan 2d, decision 2): one synced copy per opened
/// workspace on the fast volume, maintained by the studio process through the host bridge. Agents
/// only ever see the copies; no host path crosses the sandbox boundary.
/// </summary>
public interface IWorkspaceAgentSync
{
    /// <summary>
    /// Idempotent: ensures the workspace's sync session is running. A no-op (honestly logged
    /// once) when the workspaces volume or the bridge is absent - dev hosts and pre-2d
    /// containers run without agent copies.
    /// </summary>
    void EnsureStarted(string workspaceId);

    /// <summary>The workspace copy's path, or null while no session is running for the id.</summary>
    string? CopyPathFor(string workspaceId);

    /// <summary>The session's status snapshot (a WorkspaceSyncStatus), or null when none runs.</summary>
    object? StatusFor(string workspaceId);

    /// <summary>
    /// Waits until everything the agent wrote into the copy has reached the host folder (backlog 150): the
    /// write-back queue is empty, nothing is in flight, and the copy has been quiet for longer than the
    /// debounce. A commit made through the bridge before this holds records a tree a few hundred
    /// milliseconds stale. True when settled, false when the timeout passed first, null when no copy is
    /// synced for that workspace and there is nothing to wait for.
    /// </summary>
    Task<bool?> SettleAsync(string workspaceId, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        Task.FromResult<bool?>(null);

    /// <summary>
    /// Why the workspace's copy failed to start the last time it was tried (backlog 173), or null when it did
    /// not fail: never started, still starting, or running. A copy that is still starting is never called
    /// failed; only a start that has actually thrown records a reason.
    /// </summary>
    string? StartFailureFor(string workspaceId) => null;

    /// <summary>
    /// Ends this ONE workspace's own sync session, when one is running, without touching any other (#308: a
    /// pruned worktree's own copy must not keep syncing against a host folder the bridge has already removed,
    /// and a later attempt for the same item number must not find a stale session already claiming the id).
    /// A no-op default, for an implementation with nothing per-workspace to stop.
    /// </summary>
    Task StopAsync(string workspaceId) => Task.CompletedTask;

    /// <summary>
    /// Forgets a recorded start failure and starts the copy again. <see cref="EnsureStarted"/> deliberately does
    /// not retry a recorded failure, because a surface calls it on every render and each failure would then
    /// start the next attempt; a person asking to try again is what does.
    /// </summary>
    void Retry(string workspaceId) => EnsureStarted(workspaceId);

    /// <summary>Raised with the workspace id when a copy's start fails or a retry begins.</summary>
    event Action<string>? StartChanged
    {
        add { }
        remove { }
    }
}
