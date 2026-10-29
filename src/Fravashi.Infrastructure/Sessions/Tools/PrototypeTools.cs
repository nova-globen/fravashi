using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fravashi.Application.Mockups;
using Fravashi.Application.Prototypes;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Application.Verification;
using Fravashi.Application.Wiki;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Wiki;

namespace Fravashi.Infrastructure.Sessions.Tools;

/// <summary>
/// What the two prototype tools share (backlog 215, plan @40): reading a prototype by number in THIS call's project,
/// and what a render tells an agent. The project is <see cref="StudioToolCall.Project"/>, asked at every call through
/// <see cref="WikiToolBase.WikiFor"/>, and never held: the tools are one object for the whole process.
/// </summary>
public abstract class PrototypeToolBase(IWikiLibraries wikis, ITrackerResolver trackers) : WikiToolBase(wikis, trackers)
{
    /// <summary>The sentence both descriptions end with: what an overlay is for, and where structure goes instead.</summary>
    internal const string StructureGoesToTheDesign =
        "The overlay is for how the generated page LOOKS: words, styles, a class, an attribute, a small fragment of "
        + "HTML. Anything structural (adding or removing a field, a screen, a dialog or a behaviour, changing a layout) "
        + "belongs in the DESIGN, never in the overlay: change the design with wiki_write (get the format from "
        + "mockup_schema and check it with mockup_check), then call prototype_render with the prototype's number to "
        + "move the prototype onto the design's new version.";

    /// <summary>A prototype in this call's project and its document, or the refusal that says why there is none.</summary>
    protected async Task<(WikiItem? Item, PrototypeDocument? Document, StudioToolResult? Refusal)> PrototypeAsync(
        StudioToolCall call, int number, CancellationToken cancellationToken)
    {
        var item = await WikiFor(call).GetByNumberAsync(number, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return (null, null, Rejected("item_not_found", "number", $"There is no wiki document numbered {number}."));
        }

        if (item.Type != WikiItemType.Prototype)
        {
            return (null, null, Rejected(
                "not_a_prototype",
                "number",
                $"@{item.Number} is a {WikiNames.ToName(item.Type)}, not a prototype; only a prototype holds an overlay."));
        }

        var body = await PrototypeBody.ReadAsync(item.Body, WikiFor(call), cancellationToken).ConfigureAwait(false);
        if (body.Document is not { } document || !body.IsValid)
        {
            return (null, null, Invalid("prototype_invalid", "number", body.Errors));
        }

        return (item, document, null);
    }

    /// <summary>A refusal carrying a reader's errors verbatim, each with its path.</summary>
    protected static StudioToolResult Invalid(string code, string field, IEnumerable<PrototypeError> errors) =>
        StudioToolResult.Failure(new JsonObject
        {
            ["refused"] = code,
            ["errors"] = new JsonArray(
            [
                .. errors.Select(e => (JsonNode)new JsonObject
                {
                    ["code"] = code,
                    ["field"] = field,
                    ["path"] = e.Path,
                    ["message"] = e.Message,
                }),
            ]),
        }.ToJsonString());

    /// <summary>The refusal for a version that is not the one stored: read again, never overwrite.</summary>
    protected static StudioToolResult Stale(WikiItem item, long read) =>
        Rejected(
            WikiCodes.Conflict,
            "version",
            $"@{item.Number} is at version {item.Version} and you read version {read}: somebody changed it since. "
            + "Read it again (wiki_read or prototype_render with its number) and work from what it says now.");

    /// <summary>The prototype as an agent needs it back: the item, its pinned design, and what its page renders.</summary>
    protected static JsonObject Answer(WikiItem item, PrototypeDocument document, PrototypeRenderResult? render, string? problem)
    {
        var payload = new JsonObject
        {
            ["number"] = item.Number,
            ["reference"] = item.Reference.Text,
            ["title"] = item.Title,
            ["version"] = item.Version,
            ["design"] = new JsonObject
            {
                ["number"] = document.Design.Number,
                ["version"] = document.Design.Version,
            },
            ["overlayOperations"] = document.Overlay.Count,
        };

        if (render is null)
        {
            payload["renders"] = false;
            payload["problem"] = problem;
            return payload;
        }

        // Backlog 238: each part the page draws for a node (a title, a caption, a tab, a column header) is listed after
        // the nodes with its anchor's part, so an agent can address "the heading" rather than the whole screen.
        payload["renders"] = true;
        payload["anchors"] = new JsonArray(
        [
            .. render.Anchors.Values.Concat(render.Parts.Values).Select(a => (JsonNode)new JsonObject
            {
                ["anchor"] = Anchor(a.Anchor),
                ["type"] = a.Type,
                ["text"] = a.Text,
            }),
        ]);

        // Never dropped: an operation that no longer places is the person's work, and settling it is #216's act.
        payload["unplaced"] = new JsonArray(
            [.. render.Unplaced.Select(op => (JsonNode)JsonValue.Create(PrototypeOverlayDescription.Describe(op)))]);
        payload["unexpressedOverrides"] = render.UnexpressedOverrides.Count;
        return payload;
    }

    /// <summary>An overlay anchor written the way the overlay writes one.</summary>
    protected static JsonObject Anchor(PrototypeAnchor anchor)
    {
        var written = anchor.Id is { } id ? new JsonObject { ["id"] = id } : new JsonObject { ["path"] = anchor.Path };
        if (anchor.Part != DesignPart.Node)
        {
            written["part"] = PrototypeVocabulary.Name(anchor.Part);
            if (anchor.Column is { } column)
            {
                written["column"] = column;
            }
        }

        return written;
    }

    /// <summary>Renders the prototype from its pinned tree, or says why it cannot be rendered.</summary>
    protected async Task<(PrototypeRenderResult? Render, string? Problem)> RenderAsync(
        StudioToolCall call, PrototypeDocument document, CancellationToken cancellationToken)
    {
        var pinned = await PrototypePinnedDesign.ReadAsync(WikiFor(call), document, cancellationToken).ConfigureAwait(false);
        return pinned.Tree is { } tree ? (PrototypeRenderer.Render(tree, document.Overlay), null) : (null, pinned.Problem);
    }
}

/// <summary>
/// Creates a prototype of a design, or moves an existing one onto its design's newest version (backlog 215, plan
/// @40). Creating adds to the graph and replaces nothing, so it runs unattended (<see cref="AgentGraphTools"/>, the
/// wiki_write rule: no <c>number</c>); naming a <c>number</c> rewrites a prototype somebody may have edited, so it asks.
/// </summary>
public sealed class PrototypeRenderTool(IWikiLibraries wiki, ITrackerResolver trackers) : PrototypeToolBase(wiki, trackers)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "prototype_render";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        "Renders a design as a clickable prototype page, stored as a wiki document of type prototype. Leave number out "
        + "and name the design to CREATE a prototype of it at the design's current version (this runs without asking). "
        + "Pass number and the version you read to bring an existing prototype onto its design's current version, its "
        + "overlay kept as it is (this asks a person, and writes nothing when it is already current). Either way the "
        + "answer lists every anchor the page emits (by id, or by path when a node has none), with its component type "
        + "and the text it shows, which is what prototype_adjust's operations name, then each PART a node draws (a "
        + "screen's or dialog's title, a caption, a tab, a column header) with the part in its anchor, and any overlay "
        + "operation that no longer finds its node. " + StructureGoesToTheDesign;

    /// <inheritdoc />
    public override string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "design": { "type": "integer", "description": "The design document's number, after the @. Needed to create." },
            "number": { "type": "integer", "description": "An existing prototype's number, to bring it onto its design's current version. Leave out to create." },
            "version": { "type": "integer", "description": "The prototype version you read. Required with number." },
            "title": { "type": "string", "description": "The new prototype's title. Defaults to Prototype: and the design's title." }
          },
          "additionalProperties": false
        }
        """;

    /// <inheritdoc />
    public override StudioToolAccess Access => StudioToolAccess.Mutating;

    /// <inheritdoc />
    protected override async Task<StudioToolResult> RunAsync(StudioToolCall call, CancellationToken cancellationToken)
    {
        var designNumber = Number(call, "design");
        if (Number(call, "number") is { } number)
        {
            return await RefreshAsync(call, number, designNumber, cancellationToken).ConfigureAwait(false);
        }

        if (designNumber is not { } design)
        {
            return Rejected("design_required", "design", "Name the design to create a prototype of, by its number after the @.");
        }

        return await CreateAsync(call, design, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StudioToolResult> CreateAsync(StudioToolCall call, int number, CancellationToken cancellationToken)
    {
        var design = await WikiFor(call).GetByNumberAsync(number, cancellationToken).ConfigureAwait(false);
        if (design is null)
        {
            return Rejected("item_not_found", "design", $"There is no wiki document numbered {number}.");
        }

        if (design.Type != WikiItemType.Design)
        {
            return Rejected(
                "not_a_design",
                "design",
                $"@{design.Number} is a {WikiNames.ToName(design.Type)}, not a design; a prototype is generated from a design.");
        }

        var read = DesignMockup.Read(design.Body);
        if (!read.IsValid)
        {
            // A prototype of a tree that cannot be read could never render, so none is made; mockup_check says the same.
            return StudioToolResult.Failure(new JsonObject
            {
                ["refused"] = "design_invalid",
                ["errors"] = new JsonArray(
                [
                    .. read.Errors.Select(e => (JsonNode)new JsonObject
                    {
                        ["code"] = "design_invalid",
                        ["field"] = "design",
                        ["path"] = e.Path,
                        ["message"] = e.Message,
                    }),
                ]),
            }.ToJsonString());
        }

        var version = checked((int)design.Version);
        var document = new PrototypeDocument(PrototypeVocabulary.Version, new PrototypeDesignRef(design.Number, version), []);
        var created = await WikiFor(call).CreateAsync(
            new NewWikiItem
            {
                Type = WikiItemType.Prototype,
                Title = Text(call, "title") ?? $"Prototype: {design.Title}",
                Body = PrototypeBody.NewBody(document),
            },
            cancellationToken).ConfigureAwait(false);

        if (!created.IsValid)
        {
            return Refused(created, "create");
        }

        var (render, problem) = await RenderAsync(call, document, cancellationToken).ConfigureAwait(false);
        return Changed("created", Answer(created.Value, document, render, problem));
    }

    private async Task<StudioToolResult> RefreshAsync(
        StudioToolCall call, int number, int? designNumber, CancellationToken cancellationToken)
    {
        if (Number(call, "version") is not { } read)
        {
            return Rejected(
                "version_required",
                "version",
                "Read the prototype first and pass the version you read, so a change made in between is refused.");
        }

        var (item, document, refusal) = await PrototypeAsync(call, number, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        if (item!.Version != read)
        {
            return Stale(item, read);
        }

        if (designNumber is { } named && named != document!.Design.Number)
        {
            return Rejected(
                "design_mismatch",
                "design",
                $"@{item.Number} is a prototype of @{document.Design.Number}, not of @{named}; a prototype never changes "
                + "which design it is of. Leave number out to create a prototype of another design.");
        }

        var design = await WikiFor(call).GetByNumberAsync(document!.Design.Number, cancellationToken).ConfigureAwait(false);
        if (design is null)
        {
            return Rejected("item_not_found", "design", $"The design @{document.Design.Number} no longer exists.");
        }

        var saved = await PrototypeSave.RepinAsync(Documents, item, document, checked((int)design.Version), cancellationToken)
            .ConfigureAwait(false);

        switch (saved.Outcome)
        {
            case PrototypeSaveOutcome.Unchanged:
            {
                var (render, problem) = await RenderAsync(call, document, cancellationToken).ConfigureAwait(false);
                return Changed("unchanged", Answer(item, document, render, problem));
            }

            case PrototypeSaveOutcome.Saved:
            {
                var now = document with { Design = document.Design with { Version = checked((int)design.Version) } };
                var (render, problem) = await RenderAsync(call, now, cancellationToken).ConfigureAwait(false);
                return Changed("refreshed", Answer(saved.Item!, now, render, problem));
            }

            case PrototypeSaveOutcome.Stale:
                return Stale(item, read);

            default:
                return Rejected(
                    saved.Errors.FirstOrDefault()?.Code ?? "refused",
                    "number",
                    string.Join(" ", saved.Errors.Select(e => e.Message)));
        }
    }
}

/// <summary>
/// Applies overlay operations to a prototype against the version read (backlog 215, plan @40), through #214's edit
/// path and nothing else: <see cref="PrototypeEditSession"/> appends each operation exactly as a person's direct edit
/// does and <see cref="PrototypeSave"/> writes it, so an agent's adjustment and a person's edit are the same overlay
/// operations written the same way, and Undo on the page takes either back. It always asks, and the approval card
/// lists each operation in words (<see cref="Effects"/>).
/// </summary>
public sealed class PrototypeAdjustTool(IWikiLibraries wiki, ITrackerResolver trackers) : PrototypeToolBase(wiki, trackers)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "prototype_adjust";

    /// <summary>The longest one operation's sentence on the approval card; the raw input is still shown whole.</summary>
    public const int MaxEffectLength = 1000;

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        "Adjusts a prototype by appending overlay operations to it, against the version you read; a newer version is "
        + "refused, never overwritten. Each operation is one object of the prototype overlay (setText, setCss, "
        + "setAttribute, addClass, pageCss, insertHtml; mockup_schema's guide shows each), naming its node by an anchor "
        + "prototype_render listed; to change only a node's title, caption, tab or one column header, add its \"part\" "
        + "(and \"column\") to the anchor as listed. A person approves the call first and sees every operation in words. "
        + "Any operation the overlay reader refuses, whose anchor names no node, or names a part its node does not have, "
        + "refuses the whole call and nothing is written. "
        + StructureGoesToTheDesign;

    /// <inheritdoc />
    public override string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "number": { "type": "integer", "description": "The prototype's number, after the @." },
            "version": { "type": "integer", "description": "The prototype version you read." },
            "operations": {
              "type": "array",
              "description": "Overlay operations, applied in order after the ones the prototype already has.",
              "items": { "type": "object" }
            }
          },
          "required": ["number", "version", "operations"],
          "additionalProperties": false
        }
        """;

    /// <inheritdoc />
    public override StudioToolAccess Access => StudioToolAccess.Mutating;

    /// <inheritdoc />
    protected override async Task<StudioToolResult> RunAsync(StudioToolCall call, CancellationToken cancellationToken)
    {
        if (Number(call, "number") is not { } number)
        {
            return Rejected("number_required", "number", "Name the prototype's number.");
        }

        if (Number(call, "version") is not { } read)
        {
            return Rejected("version_required", "version", "Pass the version you read, so a change made in between is refused.");
        }

        if (OperationsText(call) is not { } text)
        {
            return Rejected("operations_required", "operations", "Pass operations: a list of overlay operations.");
        }

        var operations = PrototypeReader.ReadOperations(text);
        if (operations.Operations is not { } applied)
        {
            return Invalid("operations_invalid", "operations", operations.Errors);
        }

        var (item, document, refusal) = await PrototypeAsync(call, number, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        if (item!.Version != read)
        {
            return Stale(item, read);
        }

        // Every operation must find its node in the page the prototype renders from: a person's edit can only target
        // an anchor the page emitted, and an agent's must not write operations that do nothing either. The new
        // operations are rendered on their own, because a fragment carries no anchor of its own and so cannot make
        // one place that otherwise would not.
        var pinned = await PrototypePinnedDesign.ReadAsync(WikiFor(call), document!, cancellationToken).ConfigureAwait(false);
        if (pinned.Tree is not { } tree)
        {
            return Rejected("prototype_not_renderable", "number", pinned.Problem ?? "The prototype cannot be rendered.");
        }

        // Backlog 238: a part the node's component does not have is refused with its reason, before "no node there".
        var partErrors = PrototypeReader.CheckParts(applied, tree);
        if (partErrors.Count > 0)
        {
            return Invalid("part_not_found", "operations", partErrors);
        }

        var unplaced = PrototypeRenderer.Render(tree, applied).Unplaced;
        if (unplaced.Count > 0)
        {
            return Invalid(
                "anchor_not_found",
                "operations",
                unplaced.Select(op => new PrototypeError(
                    $"{PrototypeReader.OperationsPath}[{IndexOf(applied, op)}].anchor",
                    $"No node of design @{document!.Design.Number} at version {document.Design.Version} is anchored there "
                    + $"({PrototypeOverlayDescription.Describe(op)}). prototype_render lists every anchor the page emits.")));
        }

        // #214's edit path: one appended operation per edit, saved against the version that was read.
        var session = PrototypeEditSession.Open(document!);
        foreach (var operation in applied)
        {
            session.Apply(operation);
        }

        var saved = await PrototypeSave.SaveAsync(Documents, item, session, cancellationToken).ConfigureAwait(false);
        switch (saved.Outcome)
        {
            case PrototypeSaveOutcome.Saved:
            {
                var render = PrototypeRenderer.Render(tree, session.Document.Overlay);
                var answer = Answer(saved.Item!, session.Document, render, null);
                answer["applied"] = new JsonArray(
                    [.. applied.Select(op => (JsonNode)JsonValue.Create(PrototypeOverlayDescription.Describe(op)))]);
                return Changed("adjusted", answer);
            }

            case PrototypeSaveOutcome.Stale:
                return Stale(item, read);

            default:
                return Rejected(
                    saved.Errors.FirstOrDefault()?.Code ?? "refused",
                    "number",
                    saved.Errors.Count == 0 ? "Nothing was written." : string.Join(" ", saved.Errors.Select(e => e.Message)));
        }
    }

    /// <summary>
    /// What the approval card says about a prototype_adjust call (plan @40): which prototype and against which version,
    /// then EACH operation in words, read from the RAW input by the same reader the tool runs. Operations the studio
    /// cannot read are said loudly, never passed over: a card with nothing on it reads as nothing to worry about.
    /// Empty for any other tool.
    /// </summary>
    public static IReadOnlyList<AgentToolEffect> Effects(string? toolName, IReadOnlyDictionary<string, string> input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!string.Equals(toolName, StudioToolServer.CliToolName(ToolName), StringComparison.Ordinal))
        {
            return [];
        }

        var number = input.TryGetValue("number", out var n) ? n.Trim() : "?";
        var version = input.TryGetValue("version", out var v) ? v.Trim() : "?";
        var read = input.TryGetValue("operations", out var raw) && !string.IsNullOrWhiteSpace(raw)
            ? PrototypeReader.ReadOperations(raw)
            : new PrototypeOperationsRead(null, [new PrototypeError(PrototypeReader.OperationsPath, "No operations were sent.")]);

        if (read.Operations is not { } operations)
        {
            var why = string.Join(" ", read.Errors.Take(3).Select(e => e.ToString()));
            return
            [
                new AgentToolEffect(
                    "operations",
                    AgentToolSubject.Shorten(raw, 80),
                    AgentToolSubject.Shorten(
                        $"The studio cannot read these operations for prototype @{number}, so it cannot say what they "
                        + $"change ({why}). The tool refuses them and writes nothing.",
                        MaxEffectLength),
                    AgentEffectWeight.Alarm),
            ];
        }

        var effects = new List<AgentToolEffect>
        {
            new(
                "number",
                number,
                $"Adjusts prototype @{number} against version {version}, appending {operations.Count} "
                + $"{(operations.Count == 1 ? "operation" : "operations")} to its overlay. Each is listed below; Undo on "
                + "the prototype's page takes any of them back.",
                AgentEffectWeight.Note),
        };

        for (var i = 0; i < operations.Count; i++)
        {
            var operation = operations[i];
            var sentence = PrototypeOverlayDescription.Describe(operation);
            if (operation is PrototypeInsertHtmlOperation insert)
            {
                // Describe names only where it lands; the person approving is owed what lands there.
                sentence += $" The fragment: {insert.Html}";
            }

            effects.Add(new AgentToolEffect(
                $"operations[{i.ToString(CultureInfo.InvariantCulture)}]",
                operation.Op,
                AgentToolSubject.Shorten($"{(i + 1).ToString(CultureInfo.InvariantCulture)}. {sentence}", MaxEffectLength),
                AgentEffectWeight.Note));
        }

        return effects;
    }

    /// <summary>The operations as JSON text: an array as sent, or a string holding one.</summary>
    private static string? OperationsText(StudioToolCall call)
    {
        if (call.Arguments.ValueKind != JsonValueKind.Object
            || !call.Arguments.TryGetProperty("operations", out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.GetRawText(),
        };
    }

    private static int IndexOf(IReadOnlyList<PrototypeOperation> operations, PrototypeOperation operation)
    {
        for (var i = 0; i < operations.Count; i++)
        {
            if (ReferenceEquals(operations[i], operation))
            {
                return i;
            }
        }

        return -1;
    }
}
