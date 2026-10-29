using System.Text.Json.Nodes;
using Fravashi.Application.Prototypes;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Application.Wiki;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Wiki;

namespace Fravashi.Infrastructure.Sessions.Tools;

/// <summary>
/// Checks a prototype document against <see cref="PrototypeReader"/>, the one validator, before an agent
/// saves it (backlog 211, mirroring <see cref="MockupCheckTool"/>). It takes the document as inline JSON or
/// a prototype document by number, and answers valid or the reader's errors VERBATIM, each with its JSON
/// path, so an agent repairs a document in one round. Read-only: it changes nothing.
/// </summary>
public sealed class PrototypeCheckTool(IWikiLibraries wiki, ITrackerResolver trackers) : WikiToolBase(wiki, trackers)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "prototype_check";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        "Checks a prototype document against the studio's reader, which is the one validator. Pass the "
        + "document as JSON text in tree, or the number of a prototype document in number (its body holds "
        + "the document in ONE fenced block whose info string is prototype). Answers valid, or every error "
        + "with its JSON path and a sentence saying what would be allowed; this includes checking that the "
        + "design named in \"design\" actually exists, is a design, and is at a version the document has "
        + "reached, and, for a document passed in tree, that every anchor's \"part\" is one its node has in that design. "
        + "Check a document before you save it with wiki_write. Changes nothing.";

    /// <inheritdoc />
    public override string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "tree": { "type": "string", "description": "The document as JSON text, starting {\"prototype\": 1, ...}." },
            "number": { "type": "integer", "description": "A prototype document's number, as shown after the @." }
          },
          "additionalProperties": false
        }
        """;

    /// <inheritdoc />
    public override StudioToolAccess Access => StudioToolAccess.ReadOnly;

    /// <inheritdoc />
    protected override async Task<StudioToolResult> RunAsync(StudioToolCall call, CancellationToken cancellationToken)
    {
        var tree = Text(call, "tree");
        var number = Number(call, "number");

        if ((tree is null) == (number is null))
        {
            return Rejected(
                "tree_or_number",
                "tree",
                "Pass exactly one of tree (the JSON text) or number (a prototype document's number).");
        }

        if (tree is not null)
        {
            // The agent's own text: nothing from the graph is in the answer but the reader's verdict on it,
            // except that the design it names is resolved against THIS project's wiki, the same as a saved
            // document's would be.
            var result = await PrototypeReader.ReadAsync(tree, WikiFor(call), cancellationToken).ConfigureAwait(false);
            if (result.Document is not { } document)
            {
                return StudioToolResult.Success(Verdict(result.Errors).ToJsonString());
            }

            // Backlog 238: a document about to be saved is checked against its pinned design for parts its nodes do
            // not have, the reason named. A design that does not read is the render's problem and not this check's.
            var pinned = await PrototypePinnedDesign.ReadAsync(WikiFor(call), document, cancellationToken).ConfigureAwait(false);
            var parts = pinned.Tree is { } design ? PrototypeReader.CheckParts(document.Overlay, design, "$.overlay") : [];
            return StudioToolResult.Success(Verdict(parts).ToJsonString());
        }

        var item = await WikiFor(call).GetByNumberAsync(number!.Value, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return Rejected("item_not_found", "number", $"There is no wiki document numbered {number}.");
        }

        if (item.Type != WikiItemType.Prototype)
        {
            return Rejected(
                "not_a_prototype",
                "number",
                $"@{item.Number} is a {WikiNames.ToName(item.Type)}, not a prototype; only a prototype holds an overlay.");
        }

        // A document's errors quote what somebody wrote in it, so the answer is labelled as data.
        var body = await PrototypeBody.ReadAsync(item.Body, WikiFor(call), cancellationToken).ConfigureAwait(false);
        var verdict = Verdict(body.Errors);
        verdict["number"] = item.Number;
        verdict["version"] = item.Version;
        return Retrieved(verdict);
    }

    private static JsonObject Verdict(IReadOnlyList<PrototypeError> errors) =>
        errors.Count == 0
            ? new JsonObject { ["valid"] = true }
            : new JsonObject
            {
                ["valid"] = false,
                ["errors"] = new JsonArray(
                [
                    .. errors.Select(e => (JsonNode)new JsonObject { ["path"] = e.Path, ["message"] = e.Message }),
                ]),
            };
}
