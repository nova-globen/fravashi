using System.Text.Json.Nodes;
using Fravashi.Application.Sessions;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain;

namespace Fravashi.Infrastructure.Sessions.Tools;

/// <summary>
/// What this session is working on (backlog 44, gate G1). A read: an agent that has just been refused a
/// change needs to be able to see whether it is linked at all before it starts asking the user questions.
/// </summary>
public sealed class SessionWorkReadTool(
    ITrackerResolver trackers, IWorkItemChanges changes, ISessionWorkItems work)
    : WorkItemToolBase(trackers, changes)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "session_work_read";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        $"Lists the {ProductName.Short} work items this session is working on. A session linked to none may read, search "
        + $"and discuss, and may write to the {ProductName.Short} graph, but cannot change files in the workspace until "
        + "it is linked to at least one item. Changes nothing.";

    /// <inheritdoc />
    public override string InputSchemaJson => """{"type":"object","properties":{},"additionalProperties":false}""";

    /// <inheritdoc />
    public override StudioToolAccess Access => StudioToolAccess.ReadOnly;

    /// <inheritdoc />
    protected override async Task<StudioToolResult> RunAsync(
        StudioToolCall call, CancellationToken cancellationToken)
    {
        var linked = new JsonArray();
        foreach (var entry in work.For(call.SessionId))
        {
            var item = await TrackerFor(call).GetAsync(entry.Item, cancellationToken).ConfigureAwait(false);
            linked.Add(new JsonObject
            {
                ["number"] = item?.Number,
                ["title"] = item?.Title,
                ["status"] = item is null ? null : StableNames.ToName(item.Status),
                ["linkedBy"] = entry.LinkedBy,
            });
        }

        return Retrieved(new JsonObject
        {
            ["linked"] = linked,
            ["mayChangeTheWorkspace"] = linked.Count > 0,
        });
    }
}

/// <summary>
/// Links this session to a work item, or unlinks it. This is the tool that CLEARS gate G1, which is why it
/// is a graph tool: ADR 0020's gates govern workspace mutations and never graph ones, so a refused session
/// can still describe the work and then say it is doing it, without a person doing that for them.
/// <para>
/// It is still <see cref="StudioToolAccess.Mutating"/>, so it needs an operation key, the session's grant
/// has to cover writing, and the user is asked before it happens. The gate makes work DESCRIBABLE; it was
/// never the thing authorising the change, and every workspace change still asks a person afterwards.
/// </para>
/// </summary>
public sealed class SessionWorkLinkTool(
    ITrackerResolver trackers, IWorkItemChanges changes, ISessionWorkItems work)
    : WorkItemToolBase(trackers, changes)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "session_work_link";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        $"Says which {ProductName.Short} work item this session is working on, by its number, which is what lets the "
        + "session change files in the workspace. Set remove to true to stop working on it. Create the item "
        + "first with work_item_create if it does not exist yet.";

    /// <inheritdoc />
    public override string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "number": { "type": "integer", "description": "The item's number, as shown on the board." },
            "remove": { "type": "boolean", "description": "Stop working on it instead of starting." }
          },
          "required": ["number"],
          "additionalProperties": false
        }
        """;

    /// <inheritdoc />
    public override StudioToolAccess Access => StudioToolAccess.Mutating;

    /// <inheritdoc />
    protected override async Task<StudioToolResult> RunAsync(
        StudioToolCall call, CancellationToken cancellationToken)
    {
        if (Number(call, "number") is not { } number)
        {
            return Rejected("number_required", "number", "Name the item's number.");
        }

        var item = await TrackerFor(call).GetByNumberAsync(number, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return Rejected("item_not_found", "number", $"This project has no item #{number}.");
        }

        var remove = call.Arguments.TryGetProperty("remove", out var flag)
            && flag.ValueKind == System.Text.Json.JsonValueKind.True;

        if (remove)
        {
            work.Unlink(call.SessionId, item.Id);
        }
        else
        {
            // The session is named as the linker, so a person reading the link later can tell whether they
            // chose this work or the agent did.
            work.Link(call.SessionId, item.Id, call.Project, SessionActor(call.SessionId));
        }

        return Changed(remove ? "stopped" : "started", new JsonObject
        {
            ["number"] = item.Number,
            ["title"] = item.Title,
            ["mayChangeTheWorkspace"] = work.HasWork(call.SessionId),
        });
    }

    private static string SessionActor(AgentSessionId session) => "session:" + session.Value.ToString("N");
}
