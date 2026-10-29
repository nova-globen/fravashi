using Fravashi.Application.Sessions;

namespace Fravashi.Infrastructure.Bridge;

/// <summary>
/// The host CLI's conversation history for the circuit's active workspace, over the bridge (backlog 43).
/// Outside bridge mode there is no host to ask, so it lists nothing: a conversation history is never
/// looked for in the container, where the user never ran the CLI.
/// </summary>
public sealed class BridgeCliConversationSource(BridgeClient client, BridgeWorkspaceContext context) : ICliConversationSource
{
    public async Task<IReadOnlyList<CliConversationFile>> ListAsync(CancellationToken cancellationToken)
    {
        if (!context.IsBridgeActive)
        {
            return [];
        }

        var listed = await client.ListCliConversationsAsync(cancellationToken).ConfigureAwait(false);
        return [.. listed.Select(c => new CliConversationFile(c.Name, c.Size, c.ModifiedUtc))];
    }

    public Task<CliConversationContent> ReadAsync(string name, CancellationToken cancellationToken) =>
        context.IsBridgeActive
            ? client.ReadCliConversationAsync(name, cancellationToken)
            : Task.FromResult(CliConversationContent.Missing);
}
