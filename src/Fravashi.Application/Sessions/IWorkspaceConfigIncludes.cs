using Fravashi.Domain.AgentSessions;

namespace Fravashi.Application.Sessions;

/// <summary>
/// The work-tree files the HOST's git config includes, anywhere in the repository's own config chain, in the
/// contained copy's coordinates (backlog 289). Asked of git itself through the bridge, at each call that
/// would otherwise run trusted, so a changed <c>.git/config</c> (or an already-included file) applies to the
/// next edit. It fails toward asking: a lookup that cannot be made answers
/// <see cref="WorkspaceConfigIncludes.Unknown"/>, never <see cref="WorkspaceConfigIncludes.None"/>, and never throws.
/// </summary>
public interface IWorkspaceConfigIncludes
{
    Task<WorkspaceConfigIncludes> ForAsync(string workspaceId, CancellationToken cancellationToken = default);
}
