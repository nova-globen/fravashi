using System.Text.Json.Nodes;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Application.Updates;
using Fravashi.Domain;

namespace Fravashi.Infrastructure.Sessions.Tools;

/// <summary>
/// The one tool the tool-server foundation ships (backlog 84): which studio build the session is talking to. It
/// is deliberately harmless (no argument, no file, no provider, no content anyone else wrote) so the seam can be
/// exercised end to end before items 41 and 42 put the work-item and wiki tools on it.
/// </summary>
public sealed class StudioVersionTool(IProductInfo product, IStudioUpgrades? upgrades = null) : IStudioTool
{
    /// <summary>The tool's bare name; the CLI presents it as <c>mcp__fravashi__studio_version</c>.</summary>
    public const string ToolName = "studio_version";

    /// <summary>How long the update channel gets to answer before the version is reported without it.</summary>
    private static readonly TimeSpan ChannelWait = TimeSpan.FromSeconds(15);

    private readonly IProductInfo _product = product ?? throw new ArgumentNullException(nameof(product));

    public string Name => ToolName;

    public string Description =>
        $"Reports which {ProductName.Short} studio build this agent session is running against: the product version, the "
        + "release variant and the tool-server protocol version, and what the update channel offers (the version "
        + "studio_upgrade would install). Takes no arguments and changes nothing.";

    public string InputSchemaJson => """{"type":"object","properties":{},"additionalProperties":false}""";

    public StudioToolAccess Access => StudioToolAccess.ReadOnly;

    public async Task<StudioToolResult> InvokeAsync(StudioToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        var payload = new JsonObject
        {
            ["product"] = ProductName.Short,
            ["version"] = _product.Version,
            ["variant"] = _product.Variant,
            ["toolServerProtocol"] = StudioToolServer.SupportedProtocolVersions[0],
        };

        // Backlog 157: what studio_upgrade would install, so an agent asks a person for a version that exists.
        if (upgrades is { Available: true })
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(ChannelWait);
            var offer = await upgrades.CheckAsync(wait.Token).ConfigureAwait(false);
            payload["updateChannel"] = offer is null
                ? "the update channel could not be asked"
                : offer.Offered is { } offered
                    ? $"offers {offered}{(offer.Critical ? " (required)" : string.Empty)}"
                    : offer.Said ?? "offers nothing newer";
        }

        return StudioToolResult.Success(payload.ToJsonString());
    }
}
