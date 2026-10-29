using System.Text.Json.Nodes;
using Fravashi.Application.Mockups;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Application.Wiki;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Wiki;

namespace Fravashi.Infrastructure.Sessions.Tools;

/// <summary>
/// Checks a mockup tree against <see cref="MockupReader"/>, the one validator, before an agent saves it (backlog
/// 193, closing the gap #50 recorded: until this an agent had only the schema text to go by). It takes a tree as
/// inline JSON or a design document by number, and answers valid or the reader's errors VERBATIM, each with its
/// JSON path, so an agent repairs a tree in one round. Read-only: it changes nothing.
/// </summary>
public sealed class MockupCheckTool(IWikiLibraries wiki, ITrackerResolver trackers) : WikiToolBase(wiki, trackers)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "mockup_check";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        "Checks a mockup UI tree against the studio's reader, which is the one validator. Pass the tree as JSON text "
        + "in tree, or the number of a design document in number (its body holds the tree in ONE fenced block whose "
        + "info string is mockup). Answers valid, or every error with its JSON path and a sentence saying what would "
        + "be allowed. This can only refuse what is wrong, so leaving out states, on, at, a11y or dialog is never "
        + "flagged here: get the format itself, including that behaviour vocabulary, from mockup_schema. Check a "
        + "tree before you save it with wiki_write. A valid answer may also carry advice: what the tree leaves out "
        + "(no behaviour, or no heading), the node to change by its JSON path, and the exact JSON to add to it. Add "
        + "it and check again; it never changes valid. Changes nothing.";

    /// <inheritdoc />
    public override string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "tree": { "type": "string", "description": "The tree as JSON text, starting {\"mockup\": 1, ...}." },
            "number": { "type": "integer", "description": "A design document's number, as shown after the @." }
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
                "Pass exactly one of tree (the JSON text) or number (a design document's number).");
        }

        if (tree is not null)
        {
            // The agent's own text: nothing from the graph is in the answer but the reader's verdict on it.
            var read = MockupReader.Read(tree);
            return StudioToolResult.Success(Verdict(read.Errors, read.Document).ToJsonString());
        }

        var item = await WikiFor(call).GetByNumberAsync(number!.Value, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return Rejected("item_not_found", "number", $"There is no wiki document numbered {number}.");
        }

        if (item.Type != WikiItemType.Design)
        {
            return Rejected(
                "not_a_design",
                "number",
                $"@{item.Number} is a {WikiNames.ToName(item.Type)}, not a design; only a design holds a mockup.");
        }

        // A document's errors quote what somebody wrote in it, so the answer is labelled as data.
        var design = DesignMockup.Read(item.Body);
        var verdict = Verdict(design.Errors, design.IsValid ? design.Tree!.Document : null);
        verdict["number"] = item.Number;
        verdict["version"] = item.Version;
        return Retrieved(verdict);
    }

    /// <summary>
    /// Valid or the errors, exactly as before backlog 210; a valid tree that is mute or has no heading also carries
    /// <c>advice</c>, at most two lines with the fix and naming mockup_schema, BESIDE the verdict and never in place
    /// of it.
    /// </summary>
    private static JsonObject Verdict(IReadOnlyList<MockupError> errors, MockupDocument? document) =>
        errors.Count == 0
            ? MockupAdvice.For(document!) is { } advice
                ? new JsonObject { ["valid"] = true, ["advice"] = advice }
                : new JsonObject { ["valid"] = true }
            : new JsonObject
            {
                ["valid"] = false,
                ["errors"] = new JsonArray(
                [
                    .. errors.Select(e => (JsonNode)new JsonObject { ["path"] = e.Path, ["message"] = e.Message }),
                ]),
            };
}
