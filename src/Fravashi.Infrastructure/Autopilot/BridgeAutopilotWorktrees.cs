using System.Globalization;
using System.Net;
using Fravashi.Application.Autopilot;
using Fravashi.Infrastructure.Bridge;
using Microsoft.Extensions.Configuration;

namespace Fravashi.Infrastructure.Autopilot;

/// <summary>
/// The real <see cref="IAutopilotWorktrees"/> (ADR 0031 decisions 15 and 16, #308): calls the host bridge's
/// own already-tested primitive (#306, <c>POST</c>/<c>DELETE /bridge/ws/{id}/worktrees</c>) through a
/// dedicated <see cref="BridgeClient"/>, built from raw configuration the same way
/// <c>WorkspaceAgentSync.ClientFor</c> already does: this is a process-wide seam the engine's own background
/// host calls, never a circuit's own <c>BridgeWorkspaceContext</c>, which only ever names ONE active
/// workspace for ONE browser connection.
/// </summary>
public sealed class BridgeAutopilotWorktrees(IConfiguration configuration) : IAutopilotWorktrees
{
    private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

    /// <inheritdoc />
    public async Task<AutopilotWorktree> CreateAsync(
        string repositoryWorkspaceId, int itemNumber, string baseBranch, CancellationToken ct = default)
    {
        var client = ClientFor(repositoryWorkspaceId);
        var created = await client.CreateWorktreeAsync(itemNumber, baseBranch, ct).ConfigureAwait(false);
        return new AutopilotWorktree(
            created.Workspace.Id, created.Workspace.Name, created.Workspace.RootPath, created.Branch, created.BaseBranch);
    }

    /// <inheritdoc />
    public async Task<string?> PruneAsync(string repositoryWorkspaceId, int itemNumber, CancellationToken ct = default)
    {
        var client = ClientFor(repositoryWorkspaceId);
        var (status, detail) = await client.PruneWorktreeAsync(itemNumber, ct).ConfigureAwait(false);
        return status switch
        {
            HttpStatusCode.NoContent or HttpStatusCode.NotFound => null,
            _ => detail is { Length: > 0 }
                ? detail
                : $"The worktree for item {itemNumber.ToString(CultureInfo.InvariantCulture)} could not be pruned ({(int)status}).",
        };
    }

    private BridgeClient ClientFor(string workspaceId)
    {
        var context = new BridgeWorkspaceContext(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [BridgeWorkspaceContext.UrlKey] = _configuration[BridgeWorkspaceContext.UrlKey],
                [BridgeWorkspaceContext.TokenKey] = _configuration[BridgeWorkspaceContext.TokenKey],
                [BridgeWorkspaceContext.ActiveIdKey] = workspaceId,
            })
            .Build());
        return new BridgeClient(new HttpClient(), context);
    }
}
