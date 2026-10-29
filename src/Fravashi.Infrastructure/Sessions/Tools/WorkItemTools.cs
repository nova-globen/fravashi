using System.Text.Json.Nodes;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;
using Fravashi.Domain;

namespace Fravashi.Infrastructure.Sessions.Tools;

/// <summary>
/// Backlog 41: the work-item tools an agent session reaches over the studio's own tool server (ADR 0019).
/// Retrieval: find, read, and what can start now. They are separate types rather than one tool with a verb
/// argument, because the threat model's capability split is enforced per tool, and a single tool carrying both
/// a read and a write verb could not be granted one without the other.
/// </summary>
public sealed class WorkItemFindTool(ITrackerResolver trackers, IWorkItemChanges changes)
    : WorkItemToolBase(trackers, changes)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "work_items_find";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        $"Finds work items in the {ProductName.Short} board. Filter by a substring of the title or description, by status, "
        + "by level, or by parent item number. Returns a short summary of each match, newest board order first; "
        + "read one whole with work_item_read. Changes nothing.";

    /// <inheritdoc />
    public override string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "text": { "type": "string", "description": "A substring of the title or the description." },
            "status": { "type": "string", "enum": ["triage","backlog","ready","in_progress","in_review","done","cancelled"] },
            "level": { "type": "string", "enum": ["tier","feature","increment","task"] },
            "parentNumber": { "type": "integer", "description": "Only the children of this item." },
            "limit": { "type": "integer", "description": "At most this many, 1 to 200. Defaults to 50." }
          },
          "additionalProperties": false
        }
        """;

    /// <inheritdoc />
    public override StudioToolAccess Access => StudioToolAccess.ReadOnly;

    /// <inheritdoc />
    protected override async Task<StudioToolResult> RunAsync(
        StudioToolCall call, CancellationToken cancellationToken)
    {
        var query = new WorkItemQuery { Text = Text(call, "text"), Limit = Math.Clamp(Number(call, "limit") ?? 50, 1, 200) };

        if (Text(call, "status") is { } status)
        {
            var parsed = Enum<WorkItemStatus>(status, "status");
            if (!parsed.IsValid)
            {
                return Rejected(parsed.Errors[0].Code, "status", parsed.Errors[0].Message);
            }

            query = query with { Statuses = [parsed.Value] };
        }

        if (Text(call, "level") is { } level)
        {
            var parsed = Enum<WorkItemLevel>(level, "level");
            if (!parsed.IsValid)
            {
                return Rejected(parsed.Errors[0].Code, "level", parsed.Errors[0].Message);
            }

            query = query with { Levels = [parsed.Value] };
        }

        if (Number(call, "parentNumber") is { } parentNumber)
        {
            var parent = await TrackerFor(call).GetByNumberAsync(parentNumber, cancellationToken).ConfigureAwait(false);
            if (parent is null)
            {
                return Rejected("parent_not_found", "parentNumber", $"This project has no item #{parentNumber}.");
            }

            query = query with { Parent = parent.Id };
        }

        var page = await TrackerFor(call).QueryAsync(query, cancellationToken).ConfigureAwait(false);
        return Retrieved(new JsonObject
        {
            ["matches"] = new JsonArray([.. page.Items.Select(i => (JsonNode)Summary(i))]),
            ["more"] = page.NextCursor is not null,
        });
    }
}

/// <summary>One item whole, with its links, its evidence and whether it can be started.</summary>
public sealed class WorkItemReadTool(ITrackerResolver trackers, IWorkItemChanges changes)
    : WorkItemToolBase(trackers, changes)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "work_item_read";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        $"Reads one {ProductName.Short} work item whole by its number: description, acceptance criteria (each with the id "
        + "work_item_evidence takes), readiness, "
        + "guardrails, test scenarios, the items it links to, the evidence recorded against it, and whether it "
        + "can be started now. Changes nothing.";

    /// <inheritdoc />
    public override string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "number": { "type": "integer", "description": "The item's number, as shown on the board." }
          },
          "required": ["number"],
          "additionalProperties": false
        }
        """;

    /// <inheritdoc />
    public override StudioToolAccess Access => StudioToolAccess.ReadOnly;

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

        var payload = Full(item);

        var links = await TrackerFor(call).LinksAsync(item.Id, cancellationToken).ConfigureAwait(false);
        var named = new JsonArray();
        foreach (var link in links)
        {
            // Read from THIS item, so the agent sees "blocked_by #12" rather than a stored edge it has to turn
            // round itself, and the other end is named by its number, which is the only handle these tools take.
            var other = await TrackerFor(call).GetAsync(link.OtherEnd(item.Id), cancellationToken).ConfigureAwait(false);
            named.Add(new JsonObject
            {
                ["type"] = StableNames.ToName(link.ReadingFrom(item.Id)),
                ["number"] = other?.Number,
                ["title"] = other?.Title,
            });
        }

        payload["links"] = named;

        var evidence = await TrackerFor(call).EvidenceAsync(item.Id, cancellationToken).ConfigureAwait(false);
        payload["evidence"] = new JsonArray(
        [
            .. evidence.Select(e => (JsonNode)new JsonObject
            {
                ["kind"] = StableNames.ToName(e.Kind),
                ["result"] = StableNames.ToName(e.Result),
                ["summary"] = e.Summary,
                ["criterion"] = e.Criterion,
                ["reference"] = e.ArtefactRef,
            }),
        ]);

        var report = await TrackerFor(call).WhatCanStartNowAsync(
            new StartableQuery(Limit: int.MaxValue), cancellationToken).ConfigureAwait(false);
        if (report.Startable.Concat(report.Held).FirstOrDefault(e => e.Item.Id == item.Id) is { } entry)
        {
            payload["startability"] = Verdict(entry.Verdict);
        }

        return Retrieved(payload);
    }
}

/// <summary>What an agent should ask before picking work up.</summary>
public sealed class WorkItemStartableTool(ITrackerResolver trackers, IWorkItemChanges changes)
    : WorkItemToolBase(trackers, changes)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "work_items_startable";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        $"Lists the {ProductName.Short} work items that can be started right now: Ready, not flagged, every blocking item "
        + "Done, and nobody being waited on. Each held item comes back too, with every reason it is held, so "
        + "you can say why rather than only showing less. Changes nothing.";

    /// <inheritdoc />
    public override string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "parentNumber": { "type": "integer", "description": "Only items under this one." },
            "assignee": { "type": "string", "description": "Only items assigned to exactly this person." },
            "includeHeld": { "type": "boolean", "description": "Include what cannot start, with reasons. Defaults to true." },
            "limit": { "type": "integer", "description": "At most this many startable items. Defaults to 25." }
          },
          "additionalProperties": false
        }
        """;

    /// <inheritdoc />
    public override StudioToolAccess Access => StudioToolAccess.ReadOnly;

    /// <inheritdoc />
    protected override async Task<StudioToolResult> RunAsync(
        StudioToolCall call, CancellationToken cancellationToken)
    {
        WorkItemId? parent = null;
        if (Number(call, "parentNumber") is { } parentNumber)
        {
            var found = await TrackerFor(call).GetByNumberAsync(parentNumber, cancellationToken).ConfigureAwait(false);
            if (found is null)
            {
                return Rejected("parent_not_found", "parentNumber", $"This project has no item #{parentNumber}.");
            }

            parent = found.Id;
        }

        var held = !call.Arguments.TryGetProperty("includeHeld", out var flag)
            || flag.ValueKind != System.Text.Json.JsonValueKind.False;

        var report = await TrackerFor(call).WhatCanStartNowAsync(
            new StartableQuery(parent, Text(call, "assignee"), Limit: Math.Clamp(Number(call, "limit") ?? 25, 1, 200)),
            cancellationToken).ConfigureAwait(false);

        var payload = new JsonObject
        {
            ["startable"] = new JsonArray(
            [
                .. report.Startable.Select(e => (JsonNode)Merge(Summary(e.Item), Verdict(e.Verdict))),
            ]),
        };

        if (held)
        {
            payload["held"] = new JsonArray(
            [
                .. report.Held.Select(e => (JsonNode)Merge(Summary(e.Item), Verdict(e.Verdict))),
            ]);
        }

        return Retrieved(payload);
    }

    private static JsonObject Merge(JsonObject item, JsonObject verdict)
    {
        item["startability"] = verdict;
        return item;
    }
}
