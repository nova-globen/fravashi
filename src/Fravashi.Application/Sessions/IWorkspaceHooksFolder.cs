using Fravashi.Domain.AgentSessions;

namespace Fravashi.Application.Sessions;

/// <summary>
/// The folder the HOST's git runs a workspace's hooks from, in the contained copy's coordinates (backlog 288). Asked of
/// git itself through the bridge, at each call that would otherwise run trusted, so a changed <c>core.hooksPath</c>
/// applies to the next edit. It fails toward asking: a lookup that cannot be made answers
/// <see cref="WorkspaceHooksFolder.Unknown"/>, never <see cref="WorkspaceHooksFolder.None"/>, and never throws.
/// </summary>
public interface IWorkspaceHooksFolder
{
    Task<WorkspaceHooksFolder> ForAsync(string workspaceId, CancellationToken cancellationToken = default);
}
