using System.Text.Json;
using System.Text.Json.Nodes;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Application.Wiki;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Projects;
using Fravashi.Domain.Wiki;
using Fravashi.Domain;

namespace Fravashi.Infrastructure.Sessions.Tools;

/// <summary>
/// What every wiki tool shares (backlog 42, ADR 0019). It answers in the same shapes as the work-item
/// tools and carries the SAME trust sentence, referenced rather than copied: the threat model's point is
/// that retrieved content is labelled as data, and two labels that drift apart would leave one surface
/// quietly unlabelled.
/// </summary>
public abstract class WikiToolBase(IWikiLibraries wikis, ITrackerResolver trackers) : IStudioTool
{
    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract string Description { get; }

    /// <inheritdoc />
    public abstract string InputSchemaJson { get; }

    /// <inheritdoc />
    public abstract StudioToolAccess Access { get; }

    /// <summary>The authored wiki.</summary>
    /// <summary>The wiki of the project THIS CALL acts on (backlog 121); never held by the tool.</summary>
    protected IWikiLibrary WikiFor(StudioToolCall call) => wikis.For(call.Project);

    /// <summary>Documents by id, for a write addressed by the id of a document read in this call's project.</summary>
    protected IWikiDocuments Documents => wikis.Documents;

    /// <summary>The tracker, for the work items a plan names.</summary>
    protected ITracker TrackerFor(StudioToolCall call) => trackers.For(call.Project);

    /// <inheritdoc />
    public Task<StudioToolResult> InvokeAsync(StudioToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        return RunAsync(call, cancellationToken);
    }

    /// <summary>The tool's own work, with the call already checked for null.</summary>
    protected abstract Task<StudioToolResult> RunAsync(StudioToolCall call, CancellationToken cancellationToken);

    /// <summary>A retrieval answer, labelled as other-authored data (threat model requirement 2).</summary>
    protected static StudioToolResult Retrieved(JsonNode payload) =>
        StudioToolResult.Success(new JsonObject
        {
            ["trust"] = "other-authored-content",
            ["note"] = WorkItemToolBase.TrustNote,
            ["data"] = payload,
        }.ToJsonString());

    /// <summary>A mutation answer: what the studio itself just did, so it carries no trust label.</summary>
    protected static StudioToolResult Changed(string what, JsonNode payload) =>
        StudioToolResult.Success(new JsonObject { ["changed"] = what, ["item"] = payload }.ToJsonString());

    /// <summary>The model's refusal, in its own codes and sentences.</summary>
    protected static StudioToolResult Refused<T>(WikiResult<T> result, string what) =>
        StudioToolResult.Failure(new JsonObject
        {
            ["refused"] = what,
            ["errors"] = new JsonArray(
            [
                .. result.Errors.Select(e => (JsonNode)new JsonObject
                {
                    ["code"] = e.Code,
                    ["field"] = e.Field,
                    ["message"] = e.Message,
                }),
            ]),
        }.ToJsonString());

    /// <summary>A refusal this tool made before reaching the model, in the same shape.</summary>
    protected static StudioToolResult Rejected(string code, string field, string message) =>
        StudioToolResult.Failure(new JsonObject
        {
            ["refused"] = code,
            ["errors"] = new JsonArray(new JsonObject
            {
                ["code"] = code,
                ["field"] = field,
                ["message"] = message,
            }),
        }.ToJsonString());

    /// <summary>A string argument, or null when absent, not a string, or blank.</summary>
    protected static string? Text(StudioToolCall call, string name) =>
        call.Arguments.ValueKind == JsonValueKind.Object
        && call.Arguments.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { } text
        && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    /// <summary>An integer argument, or null.</summary>
    protected static int? Number(StudioToolCall call, string name) =>
        call.Arguments.ValueKind == JsonValueKind.Object
        && call.Arguments.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;

    /// <summary>One document as a line in a list: enough to choose between them.</summary>
    protected static JsonObject Summary(WikiItem item) => new()
    {
        ["number"] = item.Number,
        ["reference"] = item.Reference.Text,
        ["type"] = WikiNames.ToName(item.Type),
        ["title"] = item.Title,
        ["slug"] = item.Slug,
        ["version"] = item.Version,
    };

    /// <summary>One document whole, body and links included.</summary>
    protected static JsonObject Full(WikiItem item)
    {
        var payload = Summary(item);
        payload["id"] = item.Id;
        payload["body"] = item.Body;
        payload["links"] = new JsonArray(
        [
            .. item.Links.Select(l => (JsonNode)new JsonObject
            {
                ["kind"] = WikiNames.ToName(l.Kind),
                ["target"] = l.Target,
                ["label"] = l.Label,
            }),
        ]);
        payload["references"] = new JsonArray(
        [
            .. item.BodyReferences.Select(r => (JsonNode)JsonValue.Create(r.Text)),
        ]);
        return payload;
    }
}

/// <summary>Finds documents. Retrieval, so it changes nothing and its result is labelled as data.</summary>
public sealed class WikiFindTool(
    IWikiLibraries wiki, ITrackerResolver trackers, IWikiMeaningSearch? meaning = null, IWikiSummaryStore? summaries = null)
    : WikiToolBase(wiki, trackers)
{
    // Backlog 40: the ranking underneath this tool. Null, or not available, on an install with no embedding
    // model, and then the tool matches words as it always did and SAYS that it did. The contract an agent
    // calls did not change, which is what item 42 wrote it for.
    private readonly IWikiMeaningSearch? _meaning = meaning;

    // Backlog 40, the other half: what each match is FOR, so an agent can choose which one to open without
    // opening them all. Only documents somebody asked a summary for have one; the rest simply carry none.
    private readonly IWikiSummaryStore? _summaries = summaries;

    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "wiki_find";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        $"Finds documents in the {ProductName.Short} wiki: plans, ADRs, reports, drafts, evidence, architecture, "
        + "governance, feature notes, legal notes, references, designs (mockups), prototypes and the project's "
        + "branding. Ask in your own words: documents are found by what they MEAN when the install can do that, and by the words in "
        + "their title or body as well. The answer says which it was in ranked_by. A match may carry a summary "
        + "of what the document is for; summary_is_behind means it was written for an earlier version. Filter by type. Read one "
        + "whole with wiki_read. Changes nothing.";

    /// <inheritdoc />
    public override string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "text": { "type": "string", "description": "What you are looking for, as a question or as words." },
            "type": { "type": "string", "enum": ["plan","adr","report","draft","evidence","architecture","governance","feature","legal","reference","branding","design","prototype","release"] },
            "limit": { "type": "integer", "description": "At most this many, 1 to 100. Defaults to 20." }
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
        WikiItemType? type = null;
        if (Text(call, "type") is { } name)
        {
            try
            {
                type = WikiNames.Parse<WikiItemType>(name);
            }
            catch (InvalidDataException)
            {
                return Rejected("type_undefined", "type", $"'{name}' is not a wiki item type.");
            }
        }

        var limit = Math.Clamp(Number(call, "limit") ?? 20, 1, 100);
        IReadOnlyList<WikiItemType>? types = type is { } only ? [only] : null;
        var text = Text(call, "text");

        var byWords = await WikiFor(call).ListAsync(
            new WikiItemQuery { Text = text, Types = types, Limit = limit },
            cancellationToken).ConfigureAwait(false);

        // By meaning first, because that is the order of relevance; then whatever matched the WORDS and was
        // not already there, because an exact identifier ("ADR 0023") is found by words and may mean little
        // to a sentence model. With no text there is nothing to rank, and the listing is just the listing.
        var byMeaning = text is { Length: > 0 } && _meaning is not null
            ? await _meaning.FindAsync(call.Project, text, types, limit, cancellationToken).ConfigureAwait(false)
            : null;

        var found = byMeaning is null
            ? byWords
            : [.. byMeaning.Items.Concat(byWords.Where(w => byMeaning.Items.All(m => m.Id != w.Id))).Take(limit)];

        var payload = new JsonObject
        {
            ["matches"] = await MatchesAsync(found, cancellationToken).ConfigureAwait(false),
            ["ranked_by"] = byMeaning is null ? "words" : "meaning_then_words",
        };

        if (byMeaning is { NotYetIndexed: > 0 })
        {
            // Said, because an agent told "no such document" while forty were still waiting to be indexed
            // has been told something false.
            payload["not_yet_searchable_by_meaning"] = byMeaning.NotYetIndexed;
        }

        return Retrieved(payload);
    }

    private async Task<JsonArray> MatchesAsync(IReadOnlyList<WikiItem> found, CancellationToken cancellationToken)
    {
        var matches = new JsonArray();
        foreach (var item in found)
        {
            var match = Summary(item);
            if (_summaries is not null
                && await _summaries.GetAsync(item.Id, cancellationToken).ConfigureAwait(false) is { } summary)
            {
                match["summary"] = summary.Text;
                if (summary.IsBehind(item))
                {
                    // Said rather than withheld: an older summary still says roughly what the document is
                    // for, and an agent told it is behind can weigh it. Silently current would be the lie.
                    match["summary_is_behind"] = true;
                }
            }

            matches.Add(match);
        }

        return matches;
    }
}

/// <summary>Reads one document whole, with what points at it.</summary>
public sealed class WikiReadTool(IWikiLibraries wiki, ITrackerResolver trackers) : WikiToolBase(wiki, trackers)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "wiki_read";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        $"Reads one {ProductName.Short} wiki document whole by its number: its body, its typed links, the references "
        + "written in it, and every document that points at it. Cite what you use by its number, written "
        + "@12, which renders as a link wherever you write it. Changes nothing.";

    /// <inheritdoc />
    public override string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "number": { "type": "integer", "description": "The document's number, as shown after the @." }
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
            return Rejected("number_required", "number", "Name the document's number.");
        }

        var item = await WikiFor(call).GetByNumberAsync(number, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return Rejected("item_not_found", "number", $"There is no wiki document numbered {number}.");
        }

        var payload = Full(item);
        var backlinks = await WikiFor(call).BacklinksAsync(item.Reference, cancellationToken).ConfigureAwait(false);
        payload["referencedBy"] = new JsonArray([.. backlinks.Select(b => (JsonNode)Summary(b))]);

        return Retrieved(payload);
    }
}

/// <summary>Creates or changes a document. Mutating, so it needs a key, a grant, and a person's approval.</summary>
public sealed class WikiWriteTool(IWikiLibraries wiki, ITrackerResolver trackers) : WikiToolBase(wiki, trackers)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "wiki_write";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        $"Writes a {ProductName.Short} wiki document. Leave number out to create one; pass it with the version you read "
        + "to change one. A plan is a document of type plan: write it here, then say which work items it "
        + "affects with wiki_link. A design is a document of type design whose body holds its mockup tree in ONE "
        + "fenced block with the info string mockup; get the format, including its behaviour vocabulary, from "
        + "mockup_schema, and check the tree with mockup_check before you save it. A prototype is a document of "
        + "type prototype whose body holds, in ONE fenced block with the info string prototype, a small JSON "
        + "document naming the design it is a prototype of (pinned to a version) and the overlay of direct "
        + "edits on top of it; check it with prototype_check before you save it. Reference a work item "
        + "in the body with #12, another document with @34, a pull request with !56, and each renders as a link.";

    /// <inheritdoc />
    public override string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "number": { "type": "integer", "description": "Leave out to create; pass it to change one." },
            "version": { "type": "integer", "description": "The version you read. Required when changing." },
            "title": { "type": "string" },
            "type": { "type": "string", "enum": ["plan","adr","report","draft","evidence","architecture","governance","feature","legal","reference","branding","design","prototype","release"] },
            "body": { "type": "string", "description": "Markdown." }
          },
          "additionalProperties": false
        }
        """;

    /// <inheritdoc />
    public override StudioToolAccess Access => StudioToolAccess.Mutating;

    /// <inheritdoc />
    protected override async Task<StudioToolResult> RunAsync(
        StudioToolCall call, CancellationToken cancellationToken)
    {
        WikiItemType? type = null;
        if (Text(call, "type") is { } name)
        {
            try
            {
                type = WikiNames.Parse<WikiItemType>(name);
            }
            catch (InvalidDataException)
            {
                return Rejected("type_undefined", "type", $"'{name}' is not a wiki item type.");
            }
        }

        if (Number(call, "number") is { } number)
        {
            if (Number(call, "version") is not { } version)
            {
                return Rejected(
                    "version_required",
                    "version",
                    "Read the document first and pass the version you read, so a change made in between is refused.");
            }

            var existing = await WikiFor(call).GetByNumberAsync(number, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                return Rejected("item_not_found", "number", $"There is no wiki document numbered {number}.");
            }

            var updated = await WikiFor(call).UpdateAsync(
                existing.Id,
                new WikiItemEdit { Title = Text(call, "title"), Body = Text(call, "body"), Type = type },
                version,
                cancellationToken).ConfigureAwait(false);

            return updated.IsValid ? Changed("updated", Full(updated.Value)) : Refused(updated, "update");
        }

        var created = await WikiFor(call).CreateAsync(
            new NewWikiItem
            {
                Type = type ?? WikiItemType.Draft,
                Title = Text(call, "title") ?? string.Empty,
                Body = Text(call, "body") ?? string.Empty,
            },
            cancellationToken).ConfigureAwait(false);

        return created.IsValid ? Changed("created", Full(created.Value)) : Refused(created, "create");
    }
}

/// <summary>
/// Says which things a document affects, which is how a plan is tied to the work items it covers (backlog
/// 42's acceptance names exactly this). The link is stated with its KIND, so "this plan affects work item
/// 44" is a different fact from "this document refers to that file".
/// </summary>
public sealed class WikiLinkTool(IWikiLibraries wiki, ITrackerResolver trackers) : WikiToolBase(wiki, trackers)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "wiki_link";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        $"Links a {ProductName.Short} wiki document to a work item, another document, a pull request or a file in the "
        + "repository, stating which kind of thing it is. This is how a plan says which work items it "
        + "affects. Set remove to true to take the link away.";

    /// <inheritdoc />
    public override string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "number": { "type": "integer", "description": "The document's number." },
            "version": { "type": "integer", "description": "The version you read." },
            "kind": { "type": "string", "enum": ["work_item","wiki_item","pull_request","repository_file"] },
            "target": { "type": "string", "description": "A number for the first three kinds, a repository-relative path for a file." },
            "label": { "type": "string", "description": "What to show instead of the raw target." },
            "remove": { "type": "boolean", "description": "Remove this link instead of adding it." }
          },
          "required": ["number", "version", "kind", "target"],
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
            return Rejected("number_required", "number", "Name the document's number.");
        }

        if (Number(call, "version") is not { } version)
        {
            return Rejected("version_required", "version", "Pass the version you read.");
        }

        WikiLinkKind kind;
        try
        {
            kind = WikiNames.Parse<WikiLinkKind>(Text(call, "kind"));
        }
        catch (InvalidDataException)
        {
            return Rejected("link_kind_undefined", "kind", "That is not a link kind.");
        }

        if (Text(call, "target") is not { } target)
        {
            return Rejected("link_target_required", "target", "A link needs a target.");
        }

        var item = await WikiFor(call).GetByNumberAsync(number, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return Rejected("item_not_found", "number", $"There is no wiki document numbered {number}.");
        }

        // A link to a work item that does not exist is caught here rather than stored dangling: the wiki
        // model cannot check it (it does not know the tracker), and this tool does.
        if (kind == WikiLinkKind.WorkItem
            && int.TryParse(target, out var itemNumber)
            && await TrackerFor(call).GetByNumberAsync(itemNumber, cancellationToken).ConfigureAwait(false) is null)
        {
            return Rejected("link_target_not_found", "target", $"This project has no work item #{itemNumber}.");
        }

        var remove = call.Arguments.TryGetProperty("remove", out var flag)
            && flag.ValueKind == JsonValueKind.True;

        var links = item.Links
            .Where(l => !(l.Kind == kind && string.Equals(l.Target, target, StringComparison.Ordinal)))
            .ToList();

        if (!remove)
        {
            links.Add(new WikiLink(kind, target, Text(call, "label")));
        }

        var updated = await WikiFor(call).UpdateAsync(
            item.Id, new WikiItemEdit { Links = links }, version, cancellationToken).ConfigureAwait(false);

        return updated.IsValid
            ? Changed(remove ? "unlinked" : "linked", Full(updated.Value))
            : Refused(updated, remove ? "unlink" : "link");
    }
}
