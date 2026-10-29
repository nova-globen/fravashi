using System.Text.Json.Nodes;
using Fravashi.Application.Sessions;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Application.Wiki;
using Fravashi.Application.WorkItems;

namespace Fravashi.Infrastructure.Sessions.Tools;

/// <summary>
/// The quick-change path as one tool call (backlog 85). It exists so that a typo fix costs a step rather
/// than a conversation, and it is NOT a bypass: it creates the same artifacts the gates ask for, properly
/// linked, and the gates then pass for the ordinary reason.
/// <para>
/// It is mutating, like everything that changes the graph, so it needs an operation key and the session's
/// grant has to cover writing, and the user is asked before it happens. That prompt is the "one confirmed
/// step" item 85's acceptance asks for.
/// </para>
/// </summary>
public sealed class QuickChangeTool(
    IWikiLibraries wiki, ITrackerResolver trackers, QuickChange quick) : WikiToolBase(wiki, trackers)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "quick_change";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        "Starts a small change in one step: creates a work item, a short plan covering it and a done-check, "
        + "and binds this session to both, so the session may then change the workspace. Use it for a fix "
        + "too small to be worth a conversation about scope, such as a typo or a one-line correction. "
        + "Everything it creates is marked as a quick change so it can be reconciled later. For anything "
        + "larger, write the item and the plan properly instead.";

    /// <inheritdoc />
    public override string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "summary": { "type": "string", "description": "One line saying what is being changed and why." },
            "doneCheck": { "type": "string", "description": "How anyone can tell it worked. One sentence." }
          },
          "required": ["summary"],
          "additionalProperties": false
        }
        """;

    /// <inheritdoc />
    public override StudioToolAccess Access => StudioToolAccess.Mutating;

    /// <inheritdoc />
    protected override async Task<StudioToolResult> RunAsync(
        StudioToolCall call, CancellationToken cancellationToken)
    {
        if (Text(call, "summary") is not { } summary)
        {
            return Rejected(
                "summary_required",
                "summary",
                "Say in one line what is being changed and why; that line becomes the work item.");
        }

        var started = await quick.StartAsync(
            call.SessionId,
            call.Project,
            summary,
            Text(call, "doneCheck"),
            "session:" + call.SessionId.Value.ToString("N"),
            cancellationToken).ConfigureAwait(false);

        if (started is null)
        {
            return Rejected(
                "quick_change_refused",
                "summary",
                "The studio could not create the work item and the plan for this quick change.");
        }

        return Changed("quick_change_started", new JsonObject
        {
            ["workItem"] = new JsonObject
            {
                ["number"] = started.Item.Number,
                ["title"] = started.Item.Title,
            },
            ["plan"] = new JsonObject
            {
                ["number"] = started.Plan.Number,
                ["title"] = started.Plan.Title,
            },
            ["mayChangeTheWorkspace"] = true,
            ["note"] = QuickChange.Marker,
        });
    }
}
