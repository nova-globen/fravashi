using System.Text.Json.Nodes;
using Fravashi.Application.Sessions;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Application.WorkItems;

namespace Fravashi.Infrastructure.Sessions.Tools;

/// <summary>
/// The operator's attention as counted by the studio (backlog 244): this session's, or a work item's rolled up over the
/// conversations linked to it. It exists so an agent writing an outcome note quotes the product's counts rather than an
/// estimate. A read of the studio's own records, so it runs unattended (<c>AgentGraphTools</c>).
/// </summary>
public sealed class SessionAttentionTool(
    ITrackerResolver trackers, IWorkItemChanges changes, ISessionAttention attention)
    : WorkItemToolBase(trackers, changes)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "session_attention";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        "Counts the operator's attention as the studio recorded it: approvals asked and how each was answered (approved, "
        + "denied, denied with a reason, timed out), how long they waited, calls the studio allowed or refused without "
        + "asking, guidance turns a person typed after the opening prompt, and commit review verdicts. Without number it is "
        + "this session's; with number it is that work item's, rolled up over every conversation linked to it, with each "
        + "conversation's own line. Quote the line as it is. Changes nothing.";

    /// <inheritdoc />
    public override string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "number": { "type": "integer", "description": "A work item's number, for its roll-up. Leave out for this session." }
          },
          "additionalProperties": false
        }
        """;

    /// <inheritdoc />
    public override StudioToolAccess Access => StudioToolAccess.ReadOnly;

    /// <inheritdoc />
    protected override async Task<StudioToolResult> RunAsync(StudioToolCall call, CancellationToken cancellationToken)
    {
        if (!call.Arguments.TryGetProperty("number", out _))
        {
            return Retrieved(Describe(attention.For(call.SessionId)));
        }

        if (Number(call, "number") is not { } number)
        {
            return Rejected("number_invalid", "number", "Name the item by its number.");
        }

        var item = await TrackerFor(call).GetByNumberAsync(number, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return Rejected("item_not_found", "number", $"This project has no item #{number}.");
        }

        var rolled = attention.ForItem(item.Id);
        var sessions = new JsonArray();
        foreach (var linked in rolled.Sessions)
        {
            var one = Describe(linked.Attention);
            one["session"] = linked.Session.Value.ToString("D");
            sessions.Add(one);
        }

        var total = Describe(rolled.Total);
        total["number"] = item.Number;
        total["title"] = item.Title;
        total["sessions"] = sessions;
        total["coverage"] =
            "Every conversation linked to this item now. One that unlinked itself or was deleted is not counted.";
        return Retrieved(total);
    }

    /// <summary>The counts as JSON, with the same line every surface shows.</summary>
    public static JsonObject Describe(SessionAttention counted)
    {
        var outcomes = new JsonObject();
        foreach (var outcome in System.Enum.GetValues<AttentionOutcome>())
        {
            outcomes[AttentionNames.Of(outcome)] = counted.Count(outcome);
        }

        var kinds = new JsonObject();
        foreach (var (kind, n) in counted.AskedByKind.Where(k => k.Value > 0).OrderBy(k => k.Key))
        {
            kinds[AttentionNames.Of(kind)] = n;
        }

        var reviews = new JsonObject();
        foreach (var (review, n) in counted.Reviews.Where(r => r.Value > 0).OrderBy(r => r.Key))
        {
            reviews[AttentionNames.Of(review)] = n;
        }

        var result = new JsonObject
        {
            ["line"] = counted.Line,
            ["asked"] = counted.Asked,
            ["outcomes"] = outcomes,
            ["askedByKind"] = kinds,
            ["waitedSeconds"] = (long)Math.Round(counted.Waited.TotalSeconds),
            ["guidanceTurns"] = counted.GuidanceTurns,
            ["reviews"] = reviews,
        };

        if (counted.Reconstructed)
        {
            result["reconstructed"] = SessionAttention.ReconstructedNote;
        }

        return result;
    }
}
