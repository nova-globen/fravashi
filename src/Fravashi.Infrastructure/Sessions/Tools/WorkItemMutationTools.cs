using System.Text.Json.Nodes;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;
using Fravashi.Domain;

namespace Fravashi.Infrastructure.Sessions.Tools;

/// <summary>
/// Backlog 41's mutating tools. Every one of them is <see cref="StudioToolAccess.Mutating"/>, which means three
/// things at once and all three are deliberate: the tool server refuses it without an operation key, the
/// session's grant has to cover writing, and the CLI's PreToolUse hook asks a person, because the studio's
/// auto-allow list holds read-only FILE tools only and nothing here will ever join it (threat model
/// requirement 4).
/// <para>
/// A refusal comes back in the domain's own codes and sentences rather than reworded, so an agent that is told
/// "description_required" can act on it, and the words a model reads are the words the board shows.
/// </para>
/// </summary>
public sealed class WorkItemCreateTool(ITrackerResolver trackers, IWorkItemChanges changes)
    : WorkItemToolBase(trackers, changes)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "work_item_create";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        $"Creates a {ProductName.Short} work item. A description is mandatory: say what the problem is and what the outcome "
        + "should be. A parent must be exactly one level up (a task under an increment, an increment under a "
        + "feature, a feature under a tier). Acceptance criteria written here become the item's done-test.";

    /// <inheritdoc />
    public override string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string" },
            "description": { "type": "string", "description": "The problem and the outcome. Mandatory." },
            "level": { "type": "string", "enum": ["tier","feature","increment","task"] },
            "kind": { "type": "string", "enum": ["standard","spike","bug"] },
            "status": { "type": "string", "enum": ["triage","backlog","ready"] },
            "parentNumber": { "type": "integer", "description": "The parent item's number, one level up." },
            "assignee": { "type": "string" },
            "nextAction": { "type": "string", "description": "The next concrete thing to do." },
            "acceptanceCriteria": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["title", "description", "level"],
          "additionalProperties": false
        }
        """;

    /// <inheritdoc />
    public override StudioToolAccess Access => StudioToolAccess.Mutating;

    /// <inheritdoc />
    protected override async Task<StudioToolResult> RunAsync(
        StudioToolCall call, CancellationToken cancellationToken)
    {
        var level = Enum<WorkItemLevel>(Text(call, "level"), "level");
        if (!level.IsValid)
        {
            return Rejected(level.Errors[0].Code, "level", level.Errors[0].Message);
        }

        var kind = WorkItemKind.Standard;
        if (Text(call, "kind") is { } kindName)
        {
            var parsed = Enum<WorkItemKind>(kindName, "kind");
            if (!parsed.IsValid)
            {
                return Rejected(parsed.Errors[0].Code, "kind", parsed.Errors[0].Message);
            }

            kind = parsed.Value;
        }

        var status = WorkItemStatus.Triage;
        if (Text(call, "status") is { } statusName)
        {
            var parsed = Enum<WorkItemStatus>(statusName, "status");
            if (!parsed.IsValid)
            {
                return Rejected(parsed.Errors[0].Code, "status", parsed.Errors[0].Message);
            }

            status = parsed.Value;
        }

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

        var created = await TrackerFor(call).CreateAsync(
            new NewWorkItem
            {
                Level = level.Value,
                Kind = kind,
                Title = Text(call, "title") ?? string.Empty,
                Description = Text(call, "description") ?? string.Empty,
                Status = status,
                Parent = parent,
                Assignee = Text(call, "assignee"),
                NextAction = Text(call, "nextAction"),
                AcceptanceCriteria =
                [
                    .. Strings(call, "acceptanceCriteria").Select(t => new AcceptanceCriterion(string.Empty, t)),
                ],
            },
            ContextFor(call),
            cancellationToken).ConfigureAwait(false);

        if (!created.IsOk)
        {
            return Refused(created, "create");
        }

        Announce(call);
        return Changed("created", Full(created.Value));
    }
}

/// <summary>Changes an item's fields. The status is not one of them; that is a transition.</summary>
public sealed class WorkItemUpdateTool(ITrackerResolver trackers, IWorkItemChanges changes)
    : WorkItemToolBase(trackers, changes)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "work_item_update";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        $"Changes a {ProductName.Short} work item's fields: title, description, next action, assignee, its acceptance "
        + "criteria, or the model and effort a conversation started from it should use. A field you do not mention "
        + "is left alone, and an empty string clears one that can be empty (\"default\" does too for the suggested "
        + "model and effort); acceptanceCriteria replaces the whole list. "
        + "Moving an item between statuses is work_item_transition, not this. Read the item first and pass the "
        + "version you read, so a change somebody else made in between is refused rather than overwritten.";

    /// <inheritdoc />
    public override string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "number": { "type": "integer" },
            "version": { "type": "integer", "description": "The version you read. Refused if it has moved since." },
            "title": { "type": "string" },
            "description": { "type": "string" },
            "nextAction": { "type": "string", "description": "Pass an empty string to clear it." },
            "assignee": { "type": "string", "description": "Pass an empty string to clear it." },
            "acceptanceCriteria": { "type": "array", "items": { "type": "string" } },
            "suggestedModel": { "type": "string", "enum": ["default","haiku","sonnet","opus"], "description": "The model a conversation started from this item uses; default leaves the usual choice." },
            "suggestedEffort": { "type": "string", "enum": ["default","low","medium","high","xhigh","max"], "description": "The effort a conversation started from this item uses; default passes none." }
          },
          "required": ["number", "version"],
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

        if (Number(call, "version") is not { } version)
        {
            return Rejected(
                "version_required",
                "version",
                "Read the item first and pass the version you read, so a change made in between is refused.");
        }

        var item = await TrackerFor(call).GetByNumberAsync(number, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return Rejected("item_not_found", "number", $"This project has no item #{number}.");
        }

        var patch = new WorkItemPatch();
        if (Text(call, "title") is { } title)
        {
            patch = patch with { Title = Optional<string>.Of(title) };
        }

        if (Text(call, "description") is { } description)
        {
            patch = patch with { Description = Optional<string>.Of(description) };
        }

        // Mentioned with a null clears it; not mentioned leaves it, which is what Optional is for.
        if (Mentions(call, "nextAction"))
        {
            patch = patch with { NextAction = Optional<string?>.Of(Text(call, "nextAction")) };
        }

        if (Mentions(call, "assignee"))
        {
            patch = patch with { Assignee = Optional<string?>.Of(Text(call, "assignee")) };
        }

        // Requested 2026-10-22 (model and effort per work item): "default" or an empty string is Default (null),
        // anything else must be one of the stable names, refused rather than guessed, because it becomes a CLI flag.
        if (Mentions(call, "suggestedModel"))
        {
            var model = Suggestion<Fravashi.Domain.AgentSessions.AgentModelAlias>(call, "suggestedModel");
            if (!model.IsValid)
            {
                return Rejected(model.Errors[0].Code, "suggestedModel", model.Errors[0].Message);
            }

            patch = patch with { SuggestedModel = Optional<Fravashi.Domain.AgentSessions.AgentModelAlias?>.Of(model.Value) };
        }

        if (Mentions(call, "suggestedEffort"))
        {
            var effort = Suggestion<Fravashi.Domain.AgentSessions.AgentEffort>(call, "suggestedEffort");
            if (!effort.IsValid)
            {
                return Rejected(effort.Errors[0].Code, "suggestedEffort", effort.Errors[0].Message);
            }

            patch = patch with { SuggestedEffort = Optional<Fravashi.Domain.AgentSessions.AgentEffort?>.Of(effort.Value) };
        }

        if (Mentions(call, "acceptanceCriteria"))
        {
            // The list is replaced whole, and an entry whose text is unchanged keeps its place in order, but
            // NOT its id: these tools take text, so evidence pointing at a criterion is only safe while the
            // list is untouched. The description says so. Evidence itself is recorded with its own tool,
            // work_item_evidence (follow-up filed 2026-10-22, record evidence against a criterion).
            patch = patch with
            {
                AcceptanceCriteria = Optional<IReadOnlyList<AcceptanceCriterion>>.Of(
                [
                    .. Strings(call, "acceptanceCriteria")
                        .Select((text, index) => new AcceptanceCriterion(
                            index < item.AcceptanceCriteria.Count ? item.AcceptanceCriteria[index].Id : string.Empty,
                            text)),
                ]),
            };
        }

        var updated = await TrackerFor(call).UpdateAsync(
            item.Id, patch, version, ContextFor(call), cancellationToken).ConfigureAwait(false);

        if (!updated.IsOk)
        {
            return Refused(updated, "update");
        }

        Announce(call);
        return Changed("updated", Full(updated.Value));
    }
}

/// <summary>Moves an item between the canonical statuses.</summary>
public sealed class WorkItemTransitionTool(ITrackerResolver trackers, IWorkItemChanges changes)
    : WorkItemToolBase(trackers, changes)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "work_item_transition";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        $"Moves a {ProductName.Short} work item to another status. Closing one needs a resolution: done is completed, and "
        + "cancelled is wont_do, duplicate or cannot_reproduce. Pass the version you read, so a change somebody "
        + "else made in between is refused rather than overwritten. Moving an item with acceptance criteria to done "
        + "needs evidence recorded on each criterion; when some has none, the move is refused naming them, and "
        + "you may pass reason: a sentence saying why the missing evidence does not apply, stored on the item.";

    /// <inheritdoc />
    public override string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "number": { "type": "integer" },
            "version": { "type": "integer", "description": "The version you read." },
            "status": { "type": "string", "enum": ["triage","backlog","ready","in_progress","in_review","done","cancelled"] },
            "resolution": { "type": "string", "enum": ["completed","wont_do","duplicate","cannot_reproduce"] },
            "reason": { "type": "string", "description": "Only for a move to done: why the acceptance criteria without evidence do not need any. Stored on the item." }
          },
          "required": ["number", "version", "status"],
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

        if (Number(call, "version") is not { } version)
        {
            return Rejected("version_required", "version", "Pass the version you read.");
        }

        var status = Enum<WorkItemStatus>(Text(call, "status"), "status");
        if (!status.IsValid)
        {
            return Rejected(status.Errors[0].Code, "status", status.Errors[0].Message);
        }

        WorkItemResolution? resolution = null;
        if (Text(call, "resolution") is { } resolutionName)
        {
            var parsed = Enum<WorkItemResolution>(resolutionName, "resolution");
            if (!parsed.IsValid)
            {
                return Rejected(parsed.Errors[0].Code, "resolution", parsed.Errors[0].Message);
            }

            resolution = parsed.Value;
        }

        var item = await TrackerFor(call).GetByNumberAsync(number, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return Rejected("item_not_found", "number", $"This project has no item #{number}.");
        }

        // Requested 2026-10-22 (evidence on Done): the reason rides on the context, and the tracker decides
        // whether it is needed; a blank one is no reason, so the refusal comes back exactly as without it.
        var context = ContextFor(call) with { Reason = Text(call, "reason") };
        var moved = await TrackerFor(call).TransitionAsync(
            item.Id, status.Value, resolution, null, null, version, context, cancellationToken)
            .ConfigureAwait(false);

        if (!moved.IsOk)
        {
            return Refused(moved, "transition");
        }

        Announce(call);
        return Changed("transitioned", Full(moved.Value));
    }
}

/// <summary>
/// Records evidence against one of an item's acceptance criteria (follow-up filed 2026-10-22, record evidence
/// against a criterion). Before it existed nothing could, so the evidence-on-Done rule (<see cref="DoneEvidence"/>)
/// could only ever be met with a reason.
/// <para>
/// It asks a person, like <see cref="WorkItemUpdateTool"/> and <see cref="WorkItemTransitionTool"/>: it is not on
/// <c>AgentGraphTools</c>' unattended list. Evidence is append-only and replaces nothing, but it is exactly what
/// lets an item pass the Done gate, and the Done move's own card shows no evidence; recording it unseen would let an
/// agent clear that gate with a claim no person ever read. The approval card is where the summary is read.
/// </para>
/// </summary>
public sealed class WorkItemEvidenceTool(ITrackerResolver trackers, IWorkItemChanges changes)
    : WorkItemToolBase(trackers, changes)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "work_item_evidence";

    /// <summary>The refusal when the criterion text names more than one of the item's criteria.</summary>
    public const string CriterionAmbiguous = "evidence_criterion_ambiguous";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        $"Records evidence against one acceptance criterion of a {ProductName.Short} work item: that it was checked, what "
        + "happened (pass or fail), a summary sentence saying how it was checked, and optionally a reference such as "
        + "a commit sha, a docs/evidences path or a test name. Name the criterion by the id work_item_read shows "
        + "for it, or by its exact text. Evidence is append-only: a wrong record is followed by a newer one. Moving "
        + "an item with acceptance criteria to done needs evidence on each criterion, or a reason.";

    /// <inheritdoc />
    public override string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "number": { "type": "integer", "description": "The item's number." },
            "criterion": { "type": "string", "description": "The acceptance criterion's id as work_item_read shows it, or its exact text." },
            "result": { "type": "string", "enum": ["pass","fail"] },
            "summary": { "type": "string", "description": "How it was checked and what was seen. Mandatory." },
            "reference": { "type": "string", "description": "Where to look: a commit sha, a docs/evidences path, a test name." },
            "kind": { "type": "string", "enum": ["test_run","screenshot","log","build","review","other"], "description": "What the evidence is. Defaults to other." }
          },
          "required": ["number", "criterion", "result", "summary"],
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

        if (Text(call, "criterion") is not { } named)
        {
            return Rejected("criterion_required", "criterion", "Name the acceptance criterion by its id or its exact text.");
        }

        // Pass and fail only: partial is a real outcome elsewhere, but evidence for one criterion held or it did not.
        var result = Enum<EvidenceResult>(Text(call, "result"), "result");
        if (!result.IsValid || result.Value == EvidenceResult.Partial)
        {
            return Rejected("result_invalid", "result", "The result is pass or fail.");
        }

        if (Text(call, "summary") is not { } summary)
        {
            return Rejected(
                TrackerCodes.EvidenceSummaryRequired,
                "summary",
                "Evidence needs a sentence someone can read later; without one the record proves nothing.");
        }

        var kind = EvidenceKind.Other;
        if (Text(call, "kind") is { } kindName)
        {
            var parsed = Enum<EvidenceKind>(kindName, "kind");

            // A reason for moving to Done is written by that move, never offered here as evidence.
            if (!parsed.IsValid || parsed.Value == EvidenceKind.NoEvidenceReason)
            {
                return Rejected("kind_invalid", "kind", "The kind is test_run, screenshot, log, build, review or other.");
            }

            kind = parsed.Value;
        }

        var item = await TrackerFor(call).GetByNumberAsync(number, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return Rejected("item_not_found", "number", $"This project has no item #{number}.");
        }

        var matches = Resolve(item, named);
        if (matches.Count == 0)
        {
            return Rejected(
                TrackerCodes.EvidenceCriterionNotFound,
                "criterion",
                $"Item #{item.Number} has no acceptance criterion with the id or the exact text '{named}'. "
                + "Read the item with work_item_read for its criteria and their ids.");
        }

        if (matches.Count > 1)
        {
            return Rejected(
                CriterionAmbiguous,
                "criterion",
                $"Item #{item.Number} has {matches.Count} acceptance criteria with the text '{named}'. "
                + "Name the one you mean by its id: " + string.Join(", ", matches.Select(c => c.Id)) + ".");
        }

        var criterion = matches[0];
        var recorded = await TrackerFor(call).RecordEvidenceAsync(
            item.Id,
            new NewEvidence
            {
                Kind = kind,
                Result = result.Value,
                Summary = summary,
                ArtefactRef = Text(call, "reference"),
                Criterion = criterion.Id,
            },
            ContextFor(call),
            cancellationToken).ConfigureAwait(false);

        if (!recorded.IsOk)
        {
            return Refused(recorded, "evidence");
        }

        Announce(call);
        var record = recorded.Value;
        return Changed("evidence_recorded", new JsonObject
        {
            ["number"] = item.Number,
            ["criterion"] = new JsonObject { ["id"] = criterion.Id, ["text"] = criterion.Text },
            ["kind"] = StableNames.ToName(record.Kind),
            ["result"] = StableNames.ToName(record.Result),
            ["summary"] = record.Summary,
            ["reference"] = record.ArtefactRef,
        });
    }

    /// <summary>
    /// The criteria <paramref name="named"/> means: the one whose id it is, else every one whose text it is exactly
    /// (ordinal, both sides trimmed). An id wins over text, so a criterion is always reachable by its id.
    /// </summary>
    internal static IReadOnlyList<AcceptanceCriterion> Resolve(WorkItem item, string named)
    {
        var byId = item.AcceptanceCriteria.Where(c => string.Equals(c.Id, named, StringComparison.Ordinal)).ToList();
        if (byId.Count > 0)
        {
            return byId;
        }

        var text = named.Trim();
        return [.. item.AcceptanceCriteria.Where(c => string.Equals(c.Text.Trim(), text, StringComparison.Ordinal))];
    }
}

/// <summary>Links two items, or removes a link, in either reading.</summary>
public sealed class WorkItemLinkTool(ITrackerResolver trackers, IWorkItemChanges changes)
    : WorkItemToolBase(trackers, changes)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "work_item_link";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        $"Links two {ProductName.Short} work items, stated in either reading: \"12 blocks 15\" and \"15 blocked_by 12\" are "
        + "the same edge and only one is stored. Set remove to true to take the link away. Linking an edge that "
        + "already exists changes nothing and is not an error.";

    /// <inheritdoc />
    public override string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "fromNumber": { "type": "integer" },
            "type": { "type": "string", "enum": ["blocks","blocked_by","relates","duplicates","duplicated_by","supersedes","superseded_by"] },
            "toNumber": { "type": "integer" },
            "remove": { "type": "boolean", "description": "Remove this link instead of adding it." }
          },
          "required": ["fromNumber", "type", "toNumber"],
          "additionalProperties": false
        }
        """;

    /// <inheritdoc />
    public override StudioToolAccess Access => StudioToolAccess.Mutating;

    /// <inheritdoc />
    protected override async Task<StudioToolResult> RunAsync(
        StudioToolCall call, CancellationToken cancellationToken)
    {
        if (Number(call, "fromNumber") is not { } fromNumber || Number(call, "toNumber") is not { } toNumber)
        {
            return Rejected("numbers_required", "fromNumber", "Name both items by their numbers.");
        }

        var type = Enum<WorkItemLinkType>(Text(call, "type"), "type");
        if (!type.IsValid)
        {
            return Rejected(type.Errors[0].Code, "type", type.Errors[0].Message);
        }

        var from = await TrackerFor(call).GetByNumberAsync(fromNumber, cancellationToken).ConfigureAwait(false);
        if (from is null)
        {
            return Rejected("item_not_found", "fromNumber", $"This project has no item #{fromNumber}.");
        }

        var to = await TrackerFor(call).GetByNumberAsync(toNumber, cancellationToken).ConfigureAwait(false);
        if (to is null)
        {
            return Rejected("item_not_found", "toNumber", $"This project has no item #{toNumber}.");
        }

        var remove = call.Arguments.TryGetProperty("remove", out var flag)
            && flag.ValueKind == System.Text.Json.JsonValueKind.True;

        if (remove)
        {
            var unlinked = await TrackerFor(call).UnlinkAsync(
                from.Id, type.Value, to.Id, ContextFor(call), cancellationToken).ConfigureAwait(false);

            if (!unlinked.IsOk)
            {
                return Refused(unlinked, "unlink");
            }

            Announce(call);
            return Changed("unlinked", new JsonObject
            {
                ["from"] = from.Number,
                ["type"] = StableNames.ToName(type.Value),
                ["to"] = to.Number,
                ["removedAnEdge"] = unlinked.Value,
            });
        }

        var linked = await TrackerFor(call).LinkAsync(
            from.Id, type.Value, to.Id, ContextFor(call), cancellationToken).ConfigureAwait(false);

        if (!linked.IsOk)
        {
            return Refused(linked, "link");
        }

        Announce(call);
        return Changed("linked", new JsonObject
        {
            ["from"] = from.Number,
            ["type"] = StableNames.ToName(type.Value),
            ["to"] = to.Number,
        });
    }
}
