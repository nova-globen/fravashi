using Fravashi.Application.AgentSync;
using Fravashi.Application.Autopilot;
using Fravashi.Application.Git;
using Fravashi.Domain.Projects;

namespace Fravashi.Infrastructure.Autopilot;

/// <summary>
/// <see cref="IAutopilotWorkspaceStatus"/> and <see cref="IAutopilotWorkspaceReturn"/> over
/// <see cref="IWorkspaceGit"/> (#310). <see cref="CheckAsync"/>, at dispatch, resolves a project's workspace
/// through the SAME "most recently opened workspace bound to the project" lookup <c>AgentAutopilotBuilder</c>
/// and <c>AgentAutopilotOperator</c> already use, and hands back the resolved id in
/// <see cref="AutopilotWorkspaceState.WorkspaceId"/>. <see cref="ReturnAsync"/> takes that id directly rather
/// than re-resolving by project (commit review #310, third pass): the lookup answers "most recently opened",
/// which can change while a run is open if a person opens a different folder bound to the same project, and
/// re-resolving at return time would then check out a folder the run never touched onto the branch the
/// DISPATCHED folder was on. Never forces a checkout over work: a dirty working tree refuses, in words, rather
/// than being stashed, reset or swept aside.
/// </summary>
public sealed class GitAutopilotWorkspaceReturn(Func<ProjectId, string?> workspaceIdFor, IWorkspaceGit git, IWorkspaceAgentSync sync)
    : IAutopilotWorkspaceStatus, IAutopilotWorkspaceReturn, IAutopilotItemStart
{
    /// <summary>
    /// How long this waits for the session's own edits to reach the host before reading the tree (#310,
    /// commit review): an agent's write-back lands a debounce after the edit, and "stop now" can kill the
    /// building session while its write-backs are still queued, so a status read taken too early sees a clean
    /// tree, checks out the base branch, and the write-back then lands on it. Matches
    /// <see cref="Fravashi.Infrastructure.Git.SessionGitWork.SettleTimeout"/>, the commit path's own bound for
    /// the identical wait.
    /// </summary>
    public static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(15);

    /// <inheritdoc />
    public async Task<AutopilotWorkspaceState> CheckAsync(ProjectId project, CancellationToken ct = default)
    {
        if (workspaceIdFor(project) is not { Length: > 0 } workspaceId
            || git.For(workspaceId) is not { Git.IsAvailable: true } services)
        {
            return new AutopilotWorkspaceState(string.Empty, 0, Reachable: false);
        }

        // Neither AutopilotDispatcher nor the dispatch form catches anything from this call (commit review
        // #310): a bridge fault reading the branch or the status must answer Reachable: false, the same honest
        // refusal an unresolvable workspace already gets, rather than crash the dispatch's click handler.
        try
        {
            var branch = await services.Git.GetCurrentBranchAsync(ct).ConfigureAwait(false);
            var status = await services.Tree.GetStatusAsync(ct).ConfigureAwait(false);

            // GitWorkingTreeService.GetStatusAsync answers GitWorkingStatus.Empty (an empty Branch, among
            // other things) when the runner is unavailable or git status itself exited nonzero (commit review
            // #310): a real read always carries its own branch.head line, even for a clean tree with nothing
            // to report, so an empty Branch here means the read failed, never that the tree happens to be
            // clean. Reading that as DirtyCount 0 would let a dispatch proceed on a count this seam never
            // actually measured.
            return status.Branch.Length == 0
                ? new AutopilotWorkspaceState(string.Empty, 0, Reachable: false)
                : new AutopilotWorkspaceState(
                    branch, status.StagedEntries.Count + status.UnstagedEntries.Count, WorkspaceId: workspaceId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AutopilotWorkspaceState(string.Empty, 0, Reachable: false);
        }
    }

    /// <inheritdoc />
    public async Task<AutopilotWorkspaceReturnResult> ReturnAsync(
        string workspaceId, string baseBranch, CancellationToken ct = default)
    {
        if (git.For(workspaceId) is not { Git.IsAvailable: true } services)
        {
            return new AutopilotWorkspaceReturnResult(
                Returned: false, Note: $"not returned to {baseBranch}: the workspace could not be reached");
        }

        var current = await services.Git.GetCurrentBranchAsync(ct).ConfigureAwait(false);
        if (string.Equals(current, baseBranch, StringComparison.Ordinal))
        {
            return new AutopilotWorkspaceReturnResult(Returned: true, Note: null);
        }

        if (await NotCleanAsync(workspaceId, services, current, baseBranch, ct).ConfigureAwait(false) is { } refusal)
        {
            return new AutopilotWorkspaceReturnResult(Returned: false, Note: refusal);
        }

        await services.Git.CheckoutAsync(baseBranch, ct).ConfigureAwait(false);
        return new AutopilotWorkspaceReturnResult(Returned: true, Note: null);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Unlike <see cref="ReturnAsync"/>, the tree is read even when the folder is already on the base branch:
    /// between two items of one run (#315), uncommitted work on the base is the previous item's leftovers just
    /// as much as uncommitted work on its attempt branch, and the next item must never start on top of it.
    /// </remarks>
    public async Task<string?> PrepareAsync(string workspaceId, string baseBranch, CancellationToken ct = default)
    {
        if (git.For(workspaceId) is not { Git.IsAvailable: true } services)
        {
            return $"not returned to {baseBranch}: the workspace could not be reached";
        }

        var current = await services.Git.GetCurrentBranchAsync(ct).ConfigureAwait(false);
        if (await NotCleanAsync(workspaceId, services, current, baseBranch, ct).ConfigureAwait(false) is { } refusal)
        {
            return refusal;
        }

        if (!string.Equals(current, baseBranch, StringComparison.Ordinal))
        {
            await services.Git.CheckoutAsync(baseBranch, ct).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>
    /// Why the folder may not be switched, or null when it is clean: edits still on their way from the agent's
    /// copy, a working tree that could not be read, or uncommitted changes. Shared by the return and the
    /// between-items preparation so both read a folder the same way.
    /// </summary>
    private async Task<string?> NotCleanAsync(
        string workspaceId, WorkspaceGitServices services, string current, string baseBranch, CancellationToken ct)
    {
        // False means the edits genuinely have not all arrived yet; null means no copy is synced for this
        // workspace (a dev host, or a test) and there is nothing to wait for, so this proceeds straight to the
        // tree read below, exactly as SessionGitWork.CommitAsync treats the same two answers.
        if (await sync.SettleAsync(workspaceId, SettleTimeout, ct).ConfigureAwait(false) == false)
        {
            return $"left on {current}, edits still reaching the folder";
        }

        var status = await services.Tree.GetStatusAsync(ct).ConfigureAwait(false);

        // The same GitWorkingStatus.Empty ambiguity CheckAsync guards against (see its own remarks): a failed
        // read must never be read as "nothing staged or unstaged", or a checkout would land over whatever
        // work is really there with this seam never having actually looked.
        if (status.Branch.Length == 0)
        {
            return $"not returned to {baseBranch}: the working tree could not be read";
        }

        return status.StagedEntries.Count + status.UnstagedEntries.Count > 0
            ? $"left on {current}, uncommitted changes"
            : null;
    }
}
