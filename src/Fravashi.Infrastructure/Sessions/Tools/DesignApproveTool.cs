using System.Text.Json.Nodes;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Application.Wiki;
using Fravashi.Application.WorkItems;

namespace Fravashi.Infrastructure.Sessions.Tools;

/// <summary>
/// The design gate as one call (backlog 54). Approving a design makes it the work item's approved-design
/// reference, which is the target the verifier checks the built thing against; a design that names no work
/// cannot be approved, and the refusal says exactly how to fix that.
/// </summary>
public sealed class DesignApproveTool(
    IWikiLibraries wiki, ITrackerResolver trackers, DesignApproval approval) : WikiToolBase(wiki, trackers)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "design_approve";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        "Approves a wiki document as the approved design of a work item: the one thing the verifier checks "
        + "the built result against. The document must already be linked to the work it is a design for, "
        + "in a typed link or in its prose. An item has ONE approved design, so approving replaces any "
        + "earlier one.";

    /// <inheritdoc />
    public override string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "designNumber": { "type": "integer", "description": "The wiki document's number, after the @." },
            "workItemNumber": { "type": "integer", "description": "The work item's number, after the #." }
          },
          "required": ["designNumber", "workItemNumber"],
          "additionalProperties": false
        }
        """;

    /// <inheritdoc />
    public override StudioToolAccess Access => StudioToolAccess.Mutating;

    /// <inheritdoc />
    protected override async Task<StudioToolResult> RunAsync(
        StudioToolCall call, CancellationToken cancellationToken)
    {
        if (Number(call, "designNumber") is not { } design)
        {
            return Rejected("number_required", "designNumber", "Name the design document's number.");
        }

        if (Number(call, "workItemNumber") is not { } item)
        {
            return Rejected("number_required", "workItemNumber", "Name the work item's number.");
        }

        var result = await approval.ApproveAsync(call.Project, design, item, cancellationToken).ConfigureAwait(false);
        if (!result.Approved)
        {
            return Rejected(result.Code ?? "design_refused", "designNumber", result.Message ?? "Refused.");
        }

        return Changed("design_approved", new JsonObject
        {
            ["designNumber"] = design,
            ["workItemNumber"] = item,
        });
    }
}
