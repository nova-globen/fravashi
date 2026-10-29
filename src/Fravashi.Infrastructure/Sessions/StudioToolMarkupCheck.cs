using System.Text.Json;
using System.Text.RegularExpressions;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>
/// Whether a call to one of the studio's OWN tools (<c>mcp__fravashi__*</c>) carries a string argument that ends in
/// leaked tool-call markup, such as a closing <c>parameter</c> or <c>invoke</c> tag (work item #356). Seen 2026-10-23:
/// a conversation wrote that markup into a work item's description and retried the call 19 times, each one a card a
/// person had to answer.
/// <para>
/// Like <see cref="StudioToolArgumentCheck"/>, beside which the broker calls it, this only ever produces a REFUSAL
/// sentence: it never says a call is fine, so nothing it answers can widen what the broker allows. Built-in tools are
/// never looked at (a Write of a file that really ends in such a tag is a person's business).
/// </para>
/// </summary>
public static partial class StudioToolMarkupCheck
{
    /// <summary>
    /// The refusal sentence when any string argument (at any depth, for an argument sent as JSON) ends in leaked
    /// markup after trimming whitespace; null otherwise, and null for every tool that is not a studio tool.
    /// </summary>
    public static string? Check(string? toolName, AgentToolInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrEmpty(toolName)
            || !toolName.StartsWith(AgentWorkGate.StudioToolPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var fields = new List<string>();
        foreach (var (name, value) in input)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            var leaked = input.KindOf(name) == AgentToolValueKind.Text
                ? EndsInMarkup(value)
                : AnyStringEndsInMarkup(value);
            if (leaked)
            {
                fields.Add(name);
            }
        }

        if (fields.Count == 0)
        {
            return null;
        }

        var bare = toolName[AgentWorkGate.StudioToolPrefix.Length..];
        return $"The studio refused this {bare} call without asking anyone: {string.Join(", ", fields)} "
            + "ends in leaked tool-call markup (a closing parameter or invoke tag). Remove that trailing markup from "
            + "the value and send the call again.";
    }

    /// <summary>True when the text, trimmed, ends in one or more closing tool-call tags.</summary>
    public static bool EndsInMarkup(string? text) =>
        !string.IsNullOrEmpty(text) && TrailingMarkup().IsMatch(text.TrimEnd());

    // A raw JSON value (an array of criteria, an object): every string leaf is looked at. Unparsable text is read as is.
    private static bool AnyStringEndsInMarkup(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            return Walk(document.RootElement);
        }
        catch (JsonException)
        {
            return EndsInMarkup(raw);
        }
    }

    private static bool Walk(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => EndsInMarkup(element.GetString()),
        JsonValueKind.Array => element.EnumerateArray().Any(Walk),
        JsonValueKind.Object => element.EnumerateObject().Any(p => Walk(p.Value)),
        _ => false,
    };

    // A closing parameter, invoke or function_calls tag, with or without a namespace prefix, at the very end.
    [GeneratedRegex(@"</(?:[A-Za-z_]+:)?(?:parameter|invoke|function_calls)\s*>$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingMarkup();
}
