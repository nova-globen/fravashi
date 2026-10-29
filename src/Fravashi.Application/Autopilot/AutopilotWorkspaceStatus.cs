using Fravashi.Domain.Projects;

namespace Fravashi.Application.Autopilot;

/// <summary>The project's workspace, as #310's dispatch validation needs it.</summary>
/// <param name="Branch">The branch currently checked out, or empty when the workspace could not be reached.</param>
/// <param name="DirtyCount">
/// Tracked changes plus untracked files the host's git does not already ignore (what <c>git status</c> itself
/// already excludes). Meaningless (always 0) when <see cref="Reachable"/> is false.
/// </param>
/// <param name="Reachable">
/// False when the workspace could not be resolved or reached at all (commit review #310: this must refuse the
/// dispatch rather than read as a clean, branchless workspace, since neither the dirty check nor a base branch
/// to return to would mean anything then).
/// </param>
/// <param name="WorkspaceId">
/// The resolved workspace's own id, or null when <see cref="Reachable"/> is false. Recorded on the run at
/// dispatch (commit review #310, third pass) so the eventual return targets this EXACT folder: the
/// project-to-workspace lookup answers "the most recently opened workspace", which can change while a run is
/// open if a person opens a different folder bound to the same project, and re-resolving it at return time
/// would then check out a folder the run never touched.
/// </param>
public sealed record AutopilotWorkspaceState(string Branch, int DirtyCount, bool Reachable = true, string? WorkspaceId = null);

/// <summary>
/// Whether a dispatch's own workspace is clean enough to dispatch from (#310, ADR 0031 decision 3's
/// correction): an attempt branch is made WITH a checkout, so a dirty tree at dispatch would carry a person's
/// own edits into the run. Resolved by project, through the SAME project-to-workspace lookup the builder and
/// the return seam use (commit review #310): a project can have more than one registered workspace, and
/// checking the dispatch form's own circuit workspace would check one folder while the run actually builds in
/// whichever one that lookup resolves to.
/// </summary>
public interface IAutopilotWorkspaceStatus
{
    Task<AutopilotWorkspaceState> CheckAsync(ProjectId project, CancellationToken ct = default);
}
