using System.Text.Json.Nodes;
using Fravashi.Application.Sessions;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Application.Wiki;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Wiki;

namespace Fravashi.Infrastructure.Sessions.Tools;

/// <summary>
/// Says which plan this session is working under, which is the tool that CLEARS gate G2 (backlog 45). It is
/// a graph tool, so the gate it clears does not stop it: that is ADR 0020's rule and the whole reason an
/// agent can get itself out of a refusal rather than stalling on one.
/// <para>
/// The document it names has to BE a plan. A session working under a draft or a report would satisfy the
/// gate's letter and none of its point, and the type is the one thing the studio can check cheaply.
/// </para>
/// </summary>
public sealed class SessionPlanLinkTool(
    IWikiLibraries wiki, ITrackerResolver trackers, ISessionPlans plans) : WikiToolBase(wiki, trackers)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "session_plan_link";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        "Says which plan this session is working under, by the number of a wiki document of type plan, "
        + "which is what lets the session change files in the workspace. Write the plan first with "
        + "wiki_write and link the work items it affects with wiki_link. Set remove to true to stop working "
        + "under it.";

    /// <inheritdoc />
    public override string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "number": { "type": "integer", "description": "The plan document's number, as shown after the @." },
            "remove": { "type": "boolean", "description": "Stop working under it instead of starting." }
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
            return Rejected("number_required", "number", "Name the plan document's number.");
        }

        var item = await WikiFor(call).GetByNumberAsync(number, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return Rejected("item_not_found", "number", $"There is no wiki document numbered {number}.");
        }

        if (item.Type != WikiItemType.Plan)
        {
            return Rejected(
                "not_a_plan",
                "number",
                $"Document @{number} is a {WikiNames.ToName(item.Type)}, not a plan. Write a plan, or change "
                + "that document's type to plan if it is one.");
        }

        var remove = call.Arguments.TryGetProperty("remove", out var flag)
            && flag.ValueKind == System.Text.Json.JsonValueKind.True;

        if (remove)
        {
            plans.Unlink(call.SessionId, item.Id);
        }
        else
        {
            plans.Link(call.SessionId, item.Id, "session:" + call.SessionId.Value.ToString("N"));
        }

        return Changed(remove ? "stopped" : "started", new JsonObject
        {
            ["number"] = item.Number,
            ["title"] = item.Title,
            ["mayChangeTheWorkspace"] = plans.HasPlan(call.SessionId),
        });
    }
}

/// <summary>What this session is bound to, in one read, for an agent that has just been refused.</summary>
public sealed class SessionGovernanceReadTool(
    IWikiLibraries wiki,
    ITrackerResolver trackers,
    ISessionWorkItems work,
    ISessionPlans plans,
    ISessionRoles? roles = null) : WikiToolBase(wiki, trackers)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "session_governance_read";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        "Reports what this session is bound to and what is missing: the role it is running as and what that "
        + "role is for, the work items it is doing, the plan it is doing them under, and whether it may "
        + "currently change files in the workspace. Read this at the start of a session, and after a "
        + "refusal rather than guessing which binding is absent. Changes nothing.";

    /// <inheritdoc />
    public override string InputSchemaJson =>
        """{"type":"object","properties":{},"additionalProperties":false}""";

    /// <inheritdoc />
    public override StudioToolAccess Access => StudioToolAccess.ReadOnly;

    /// <inheritdoc />
    protected override async Task<StudioToolResult> RunAsync(
        StudioToolCall call, CancellationToken cancellationToken)
    {
        var items = new JsonArray();
        foreach (var entry in work.For(call.SessionId))
        {
            var item = await TrackerFor(call).GetAsync(entry.Item, cancellationToken).ConfigureAwait(false);
            items.Add(new JsonObject
            {
                ["number"] = item?.Number,
                ["title"] = item?.Title,
                ["linkedBy"] = entry.LinkedBy,
            });
        }

        var linkedPlans = new JsonArray();
        foreach (var entry in plans.For(call.SessionId))
        {
            var plan = await WikiFor(call).GetAsync(entry.WikiItemId, cancellationToken).ConfigureAwait(false);
            linkedPlans.Add(new JsonObject
            {
                ["number"] = plan?.Number,
                ["title"] = plan?.Title,
                ["linkedBy"] = entry.LinkedBy,
            });
        }

        var hasWork = work.HasWork(call.SessionId);
        var hasPlan = plans.HasPlan(call.SessionId);

        // The verdict is taken from the gate itself against a representative workspace change, rather than
        // recomputed here, so what this tool reports and what the gate does cannot disagree.
        var verdict = AgentWorkGate.Check("Edit", readOnly: false, hasWork, hasPlan);

        // Backlog 88: the role and its instructions ride on this answer, because this is a channel the
        // studio can prove reaches the model. Passing them as a system prompt would mean changing the
        // CLI's command line, which is a PINNED contract (ADR 0023) that may only move by a live run.
        var role = roles?.For(call.SessionId);

        return Retrieved(new JsonObject
        {
            ["role"] = role is null
                ? null
                : new JsonObject
                {
                    ["id"] = role.Id,
                    ["name"] = role.Name,
                    ["instructions"] = role.Instructions,
                    ["mayWriteTheGraph"] = role.Holds(RoleCapabilities.WriteGraph),
                    ["mayChangeFiles"] = role.Holds(RoleCapabilities.ChangeWorkspace),
                    ["mayRunCommands"] = role.Holds(RoleCapabilities.RunCommands),
                },
            ["workItems"] = items,
            ["plans"] = linkedPlans,
            ["mayChangeTheWorkspace"] = !verdict.Refuses(),
            ["missing"] = verdict.SentenceFor(),
        });
    }
}
