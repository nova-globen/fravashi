namespace Fravashi.Application.Git;

/// <summary>
/// The git seams pinned to ONE workspace's host folder (backlog 150): the command runner and the three parsed
/// services built over it, all answering about that folder whatever circuit or request is current.
/// </summary>
public sealed record WorkspaceGitServices(
    string WorkspaceId,
    IGitCommandRunner Runner,
    IGitService Git,
    IGitWorkingTreeService Tree,
    IGitRemoteService Remote);

/// <summary>
/// Git for a NAMED workspace, for callers that are not a circuit (backlog 150). The scoped
/// <see cref="IGitService"/> family follows the circuit's window, which is right for a page and wrong for an
/// agent's tool call: that call arrives on the tool server's own request, which carries no window at all, so
/// resolving the scoped seams there fell through to a fallback nothing populates and answered "no git work
/// tree here" in the installed product. An agent session names its workspace; this answers for that name.
/// </summary>
public interface IWorkspaceGit
{
    /// <summary>
    /// The seams for that workspace, or null when the studio has no way to reach git for it (no host bridge is
    /// configured, or the id is blank). Availability of an actual work tree is the returned services' own
    /// answer (<see cref="IGitService.IsAvailable"/>), asked of the folder itself.
    /// </summary>
    WorkspaceGitServices? For(string workspaceId);
}
