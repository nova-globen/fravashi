using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fravashi.Application.AgentSync;
using Fravashi.Application.Autopilot;
using Fravashi.Application.Sessions;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Autopilot;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;
using Microsoft.Extensions.DependencyInjection;
using Fravashi.Domain;

namespace Fravashi.Infrastructure.Sessions.Tools;

/// <summary>
/// The first of the studio assistant's two own tools (work item #359): starts an ordinary BUILDING conversation for a
/// named work item in the assistant's own folder, linked to the item (and to its plan, when it has one), on the
/// item's suggested model and effort or else the new-conversation default. It ALWAYS asks a person first: the
/// broker refuses it to every conversation that is not the assistant and takes it straight to a card for the
/// assistant (<c>AgentAssistantTools.AlwaysAsk</c>), and this tool checks the assistant hold once more itself, so a
/// call that somehow reached it from anything else is refused here too. One editing conversation runs per folder, so
/// when one is already running it starts nothing and says so; it never stops the running one.
/// </summary>
public sealed class SessionStartBuildingTool(
    ITrackerResolver trackers,
    IWorkItemChanges changes,
    IAgentApprovalBroker broker,
    ISessionStore sessions,
    Func<LiveSessionRegistry> registry,
    IWorkspaceAgentSync? sync,
    ISessionWorkItems work,
    ISessionPlans plans,
    Func<AgentDefault> newConversationDefaults)
    : WorkItemToolBase(trackers, changes)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "session_start_building";

    /// <summary>Who the session links say made them.</summary>
    public const string LinkedBy = "studio-assistant";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        $"{ProductName.Short} assistant only. Starts a building conversation in this folder for one work item, linked to it, on the "
        + "item's suggested model and effort. Always asks the person first, so call it only after they said yes to "
        + "starting the work. Starts nothing when an editing conversation already runs in the folder, and says so.";

    /// <inheritdoc />
    public override string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "number": { "type": "integer", "description": "The work item to build." }
          },
          "required": ["number"],
          "additionalProperties": false
        }
        """;

    /// <inheritdoc />
    public override StudioToolAccess Access => StudioToolAccess.Mutating;

    /// <inheritdoc />
    protected override async Task<StudioToolResult> RunAsync(StudioToolCall call, CancellationToken cancellationToken)
    {
        if (!broker.IsAssistant(call.SessionId))
        {
            return Rejected("not_assistant", "session", AgentAssistantTools.NotAnAssistant(ToolName));
        }

        if (Number(call, "number") is not { } number || number <= 0)
        {
            return Rejected("number_invalid", "number", "Name the work item to build by its number.");
        }

        var item = await TrackerFor(call).GetByNumberAsync(number, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return Rejected("item_not_found", "number", $"This project has no item #{Num(number)}.");
        }

        if (item.Status is WorkItemStatus.Done or WorkItemStatus.Cancelled)
        {
            return Rejected(
                "item_closed", "number",
                $"#{Num(number)} is {StudioToolDescriptions.StatusWords(item.Status)}, so there is nothing to build. "
                + "Reopen it first if the work is wanted again.");
        }

        if (sessions.Find(call.SessionId) is not { } self)
        {
            return Rejected("session_unknown", "session", "The studio does not know this conversation's folder.");
        }

        var live = registry();
        if (live.RunningFor(self.WorkspaceId) is { } running)
        {
            return Rejected(
                "editing_running", "number",
                "An editing conversation is already running in this folder (\""
                + ConversationTitles.Display(running.CustomTitle, running.DerivedTitle)
                + "\"), and one folder runs one at a time, so nothing was started and the running one was left alone. "
                + "Tell the person; they can finish or stop it, then ask again.");
        }

        if (sync?.CopyPathFor(self.WorkspaceId) is not { Length: > 0 } copy)
        {
            return Rejected("no_copy", "session", "This folder's working copy is not ready, so nothing was started.");
        }

        var defaults = newConversationDefaults();
        var model = item.SuggestedModel is { } suggested ? suggested.CliId() : defaults.Model;
        var effort = item.SuggestedEffort ?? defaults.Effort;

        LiveAgentSession started;
        try
        {
            started = live.Start(new SessionStartRequest(
                self.WorkspaceId,
                copy,
                BuildingPrompt(item),
                Model: model,
                Title: $"#{Num(item.Number)} {item.Title}",
                FromStudio: true,
                Effort: effort));
        }
        catch (InvalidOperationException ex)
        {
            // The registry's own words: a conversation that started in between, a spending ceiling.
            return Rejected("not_started", "number", ex.Message);
        }

        work.Link(started.Id, item.Id, call.Project, LinkedBy);
        if (item.RefFor(ArtifactRefRole.Plan) is { } plan)
        {
            plans.Link(started.Id, plan.Target, LinkedBy);
        }

        return StudioToolResult.Success(new JsonObject
        {
            ["started"] = started.Id.Value.ToString("D"),
            ["number"] = item.Number,
            ["title"] = item.Title,
            ["model"] = model ?? "default",
            ["effort"] = effort is { } e ? e.CliValue() : "default",
            ["note"] = "The building conversation is running in this folder and linked to the item. Tell the person "
                + "it started; they follow it in the Sessions area.",
        }.ToJsonString());
    }

    /// <summary>What the building conversation is told first: the item, and that its own gates still apply.</summary>
    public static string BuildingPrompt(WorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return $"Build work item #{Num(item.Number)}: {item.Title}. The person agreed with the {ProductName.Short} assistant on Home "
            + "to start it, and this conversation is already linked to it. Read it with work_item_read, then find or "
            + "write its plan and do the work, meeting its acceptance criteria.";
    }

    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// The second of the studio assistant's two own tools (work item #359): dispatches the autopilot for named work items,
/// through the existing <see cref="AutopilotDispatcher"/>, with the project's last run's settings (or the defaults when
/// it has never run). It ALWAYS asks a person first, like <see cref="SessionStartBuildingTool"/>. The card names the
/// items; so that the card is the whole truth, the tool dispatches exactly those: every named item must be Ready, any
/// OTHER item still marked for the autopilot (which the dispatcher would also take) makes it refuse and name them, and
/// the named items are marked first. When the dispatcher refuses, the marks this call added are taken off again.
/// </summary>
public sealed class AutopilotDispatchTool(
    ITrackerResolver trackers,
    IWorkItemChanges changes,
    IAgentApprovalBroker broker,
    IServiceScopeFactory scopes)
    : WorkItemToolBase(trackers, changes)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "autopilot_dispatch";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        $"{ProductName.Short} assistant only. Dispatches the autopilot for the named Ready work items, with the project's last run's "
        + "settings. Always asks the person first, so call it only after they said yes. Dispatches exactly the named "
        + "items: refused when another item is still marked for the autopilot.";

    /// <inheritdoc />
    public override string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "numbers": { "type": "array", "items": { "type": "integer" }, "description": "The Ready work items to dispatch." }
          },
          "required": ["numbers"],
          "additionalProperties": false
        }
        """;

    /// <inheritdoc />
    public override StudioToolAccess Access => StudioToolAccess.Mutating;

    /// <inheritdoc />
    protected override async Task<StudioToolResult> RunAsync(StudioToolCall call, CancellationToken cancellationToken)
    {
        if (!broker.IsAssistant(call.SessionId))
        {
            return Rejected("not_assistant", "session", AgentAssistantTools.NotAnAssistant(ToolName));
        }

        var numbers = Numbers(call, "numbers");
        if (numbers.Count == 0)
        {
            return Rejected("numbers_invalid", "numbers", "Name at least one Ready work item by its number.");
        }

        using var scope = scopes.CreateScope();
        if (scope.ServiceProvider.GetService<AutopilotDispatcher>() is not { } dispatcher
            || scope.ServiceProvider.GetService<IAutopilotRunStore>() is not { } runs)
        {
            return Rejected("autopilot_unavailable", "numbers", "The autopilot is not available in this studio.");
        }

        var tracker = TrackerFor(call);
        var items = await AllItemsAsync(tracker, cancellationToken).ConfigureAwait(false);
        var named = new List<WorkItem>();
        foreach (var number in numbers)
        {
            if (items.FirstOrDefault(i => i.Number == number) is not { } item)
            {
                return Rejected("item_not_found", "numbers", $"This project has no item #{Num(number)}.");
            }

            if (item.Status != WorkItemStatus.Ready)
            {
                return Rejected(
                    "item_not_ready", "numbers",
                    $"#{Num(number)} is {StudioToolDescriptions.StatusWords(item.Status)}; only Ready items can be dispatched.");
            }

            named.Add(item);
        }

        var others = items
            .Where(i => i.MarkedForAutopilot
                && i.Status is WorkItemStatus.Ready or WorkItemStatus.InProgress
                && !numbers.Contains(i.Number))
            .Select(i => "#" + Num(i.Number))
            .ToList();
        if (others.Count > 0)
        {
            return Rejected(
                "others_marked", "numbers",
                $"{string.Join(", ", others)} {(others.Count == 1 ? "is" : "are")} also marked for the autopilot and would be "
                + "dispatched too, which the person was not asked about. Name them as well, or ask the person to unmark them.");
        }

        var marked = new List<WorkItem>();
        foreach (var item in named.Where(i => !i.MarkedForAutopilot))
        {
            var result = await tracker.UpdateAsync(
                item.Id,
                new WorkItemPatch { MarkedForAutopilot = Optional<bool>.Of(true) },
                item.Version,
                ContextFor(call) with { OperationId = null },
                cancellationToken).ConfigureAwait(false);
            if (!result.IsOk)
            {
                await UnmarkAsync(tracker, marked, call, cancellationToken).ConfigureAwait(false);
                return Refused(result, "mark_for_autopilot");
            }

            marked.Add(result.Value);
        }

        var history = await runs.ForProjectAsync(call.Project, cancellationToken).ConfigureAwait(false);
        var last = history.FirstOrDefault();
        var settings = last?.Settings ?? AutopilotDispatchSettings.Default;
        var outcome = await dispatcher.DispatchAsync(call.Project, settings, cancellationToken).ConfigureAwait(false);
        if (outcome.Run is not { } run)
        {
            await UnmarkAsync(tracker, marked, call, cancellationToken).ConfigureAwait(false);
            Announce(call);
            return Rejected("not_dispatched", "numbers", "Not dispatched: " + (outcome.Refusal ?? "the dispatch was refused."));
        }

        Announce(call);
        return StudioToolResult.Success(new JsonObject
        {
            ["dispatched"] = run.Id.ToString("D"),
            ["numbers"] = new JsonArray([.. named.Select(i => (JsonNode)i.Number)]),
            ["settings"] = last is null ? "the defaults (this project has not run the autopilot before)" : "the last run's",
            ["note"] = "The autopilot run started. Tell the person; Home's Autopilot card shows its progress.",
        }.ToJsonString());
    }

    private static async Task UnmarkAsync(
        ITracker tracker, IReadOnlyList<WorkItem> marked, StudioToolCall call, CancellationToken cancellationToken)
    {
        foreach (var item in marked)
        {
            await tracker.UpdateAsync(
                item.Id,
                new WorkItemPatch { MarkedForAutopilot = Optional<bool>.Of(false) },
                item.Version,
                ContextFor(call) with { OperationId = null },
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The distinct positive integers of an array argument, in order.</summary>
    private static List<int> Numbers(StudioToolCall call, string name)
    {
        var numbers = new List<int>();
        if (call.Arguments.ValueKind != JsonValueKind.Object
            || !call.Arguments.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return numbers;
        }

        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var n) && n > 0 && !numbers.Contains(n))
            {
                numbers.Add(n);
            }
        }

        return numbers;
    }

    private static async Task<IReadOnlyList<WorkItem>> AllItemsAsync(ITracker tracker, CancellationToken ct)
    {
        var items = new List<WorkItem>();
        string? cursor = null;
        do
        {
            var page = await tracker.QueryAsync(
                new WorkItemQuery { Limit = WorkItemQuery.MaxLimit, Cursor = cursor }, ct).ConfigureAwait(false);
            items.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return items;
    }

    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Attaches one of THIS conversation's own images to a work item (work item #359): what a person pasted into a turn,
/// numbered across the whole conversation in the order they were attached, which is how Home's composer labels them
/// ("[Image 2]"). The agent never passes bytes or a path, so it can only attach an image a person put into this very
/// conversation. It asks a person like any change to an item that already exists (it is not on the unattended list),
/// rather than happening on its own when the assistant files an item, because a screenshot can carry more than the
/// person meant to file and the card shows which image goes onto which item.
/// </summary>
public sealed class WorkItemAttachImageTool(
    ITrackerResolver trackers,
    IWorkItemChanges changes,
    ISessionStore sessions,
    IWorkItemAttachments attachments)
    : WorkItemToolBase(trackers, changes)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "work_item_attach_image";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        "Attaches one of this conversation's images (as numbered in the conversation: [Image 1] is the first image the "
        + "person attached) to a work item, so the item carries the screenshot of what was reported. Asks the person.";

    /// <inheritdoc />
    public override string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "number": { "type": "integer", "description": "The work item to attach to." },
            "image": { "type": "integer", "description": "Which of this conversation's images, 1 for [Image 1]." },
            "name": { "type": "string", "description": "A short name for it, shown on the item." }
          },
          "required": ["number", "image"],
          "additionalProperties": false
        }
        """;

    /// <inheritdoc />
    public override StudioToolAccess Access => StudioToolAccess.Mutating;

    /// <inheritdoc />
    protected override async Task<StudioToolResult> RunAsync(StudioToolCall call, CancellationToken cancellationToken)
    {
        if (Number(call, "number") is not { } number || number <= 0)
        {
            return Rejected("number_invalid", "number", "Name the work item by its number.");
        }

        var images = ConversationImages(sessions.Transcript(call.SessionId));
        if (Number(call, "image") is not { } index || index < 1 || index > images.Count)
        {
            return Rejected(
                "image_invalid", "image",
                images.Count == 0
                    ? "This conversation has no images to attach."
                    : $"Name one of this conversation's {Num(images.Count)} images, 1 to {Num(images.Count)}.");
        }

        var item = await TrackerFor(call).GetByNumberAsync(number, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return Rejected("item_not_found", "number", $"This project has no item #{Num(number)}.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(images[index - 1].Base64);
        }
        catch (FormatException)
        {
            return Rejected("image_unreadable", "image", "That image could not be read back from the conversation.");
        }

        var name = Text(call, "name") ?? $"Image {Num(index)}";
        var result = attachments.AddImage(item.Id, name, bytes, "session:" + call.SessionId.Value.ToString("N"));
        if (result.Attachment is not { } stored)
        {
            return Rejected("not_attached", "image", result.Refusal ?? "The image was not attached.");
        }

        Announce(call);
        return StudioToolResult.Success(new JsonObject
        {
            ["attached"] = stored.Id,
            ["number"] = item.Number,
            ["name"] = stored.FileName,
            ["mediaType"] = stored.MediaType,
            ["bytes"] = stored.SizeBytes,
        }.ToJsonString());
    }

    /// <summary>Every image persons attached in a transcript, in order: what "[Image N]" counts.</summary>
    public static IReadOnlyList<AgentImageAttachment> ConversationImages(IEnumerable<AgentEvent> transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        return [.. transcript.OfType<UserImagesAttached>().SelectMany(e => e.Images)];
    }

    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);
}
