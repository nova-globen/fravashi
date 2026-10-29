using System.Text.Json.Nodes;
using Fravashi.Application.Mockups;
using Fravashi.Application.Sessions.Tools;

namespace Fravashi.Infrastructure.Sessions.Tools;

/// <summary>
/// Hands an agent the mockup format itself: <see cref="MockupSchema"/>'s JSON Schema, which describes the
/// shape, and <see cref="MockupGuide"/>, the short guide to the behaviour vocabulary (<c>states</c>, <c>on</c>,
/// <c>at</c>, <c>a11y</c>, <c>dialog</c>) that <see cref="MockupCheckTool"/> cannot teach: it can only refuse
/// what is wrong, and leaving behaviour out is never wrong (backlog 208). Needs no project and no wiki, so a
/// project with no copy of @14, which is every user's project, gets exactly the same answer. Read-only and
/// unattended, like <c>mockup_check</c>.
/// </summary>
public sealed class MockupSchemaTool : IStudioTool
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "mockup_schema";

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string Description =>
        "Gives you the mockup UI-tree format: the JSON Schema for its shape, and a short guide to its "
        + "behaviour vocabulary (states, on with go/open/close/set, at, a11y, dialog) that mockup_check's "
        + "errors alone cannot teach, because leaving behaviour out of a tree is never wrong. Call this before "
        + "writing a design so what you save carries the spec's states, actions, dialogs and viewports, not "
        + "only its static shape. The guide also covers the prototype overlay (setText, setCss, setAttribute, "
        + "addClass, pageCss, insertHtml), what prototype_adjust writes. Takes no arguments and changes nothing.";

    /// <inheritdoc />
    public string InputSchemaJson => """{"type":"object","properties":{},"additionalProperties":false}""";

    /// <inheritdoc />
    public StudioToolAccess Access => StudioToolAccess.ReadOnly;

    /// <inheritdoc />
    public Task<StudioToolResult> InvokeAsync(StudioToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        var payload = new JsonObject
        {
            ["schemaId"] = MockupSchema.Id,
            ["schema"] = JsonNode.Parse(MockupSchema.Json),
            ["guide"] = MockupGuide.Text,
        };

        return Task.FromResult(StudioToolResult.Success(payload.ToJsonString()));
    }
}
