using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Fravashi.Application.Mockups;

/// <summary>
/// Advice for a VALID tree that is mute or has no heading (backlog 210, plan @42). <c>mockup_check</c> can only
/// refuse what is wrong, and leaving behaviour out is never wrong, so #208's live re-run saved a tree with no
/// heading and no <c>set</c> while its agent never opened the guide that teaches both. This never changes a
/// verdict: it is carried beside one, and an agent that ignores it loses nothing.
/// <para>
/// Round 2 (2026-10-07): a line that only pointed at <c>mockup_schema</c> reached Haiku twice and changed nothing,
/// because that agent would not make a second call. So the advice carries the FIX, built from the tree being
/// checked: the node to change, by its JSON path and its words, and the exact JSON to add to it. It is at most two
/// lines (the mute case lacks two things, each fixed on its own node) and names mockup_schema once, at the end.
/// </para>
/// <para>
/// The heading rule asks for a heading ANYWHERE in the tree, not on every screen: every-feature.json, which uses
/// everything, has a secondary screen with no heading, and a nudge that fires on good trees teaches agents to
/// ignore it. Only at-rest properties count; a heading that exists only inside an override is not the page's title.
/// </para>
/// </summary>
public static class MockupAdvice
{
    /// <summary>How many characters of a node's words a line quotes before it cuts them with "...".</summary>
    public const int QuotedLength = 40;

    /// <summary>The longest id the advice derives from a button's words.</summary>
    public const int DerivedIdLength = 32;

    /// <summary>The ending of the last line, the one place the advice names mockup_schema.</summary>
    public const string Rest = "Call mockup_schema for the rest: labels, at, dialogs, and on with go, open, close and set.";

    private static readonly JsonSerializerOptions Quoting = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The advice for this tree (one or two lines), or null when it has behaviour and a heading.</summary>
    public static string? For(MockupDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var nodes = document.Screens.SelectMany((screen, i) => Walk(screen, $"$.screens[{i}]")).ToList();

        if (!nodes.Any(n => n.Node.States is not null || n.Node.On is not null || n.Node.At is not null || n.Node.A11y is not null))
        {
            return $"{Behaviour(nodes)}\n{Heading(document)}";
        }

        return nodes.Any(n => n.Node.A11y?.Heading is not null) ? null : Heading(document);
    }

    /// <summary>
    /// The heading line: the first text node on the first screen (dialogs on it included, a decorative one
    /// skipped), else that screen itself, and the exact JSON that makes it a level 1 heading.
    /// </summary>
    private static string Heading(MockupDocument document)
    {
        var screen = Walk(document.Screens[0], "$.screens[0]").ToList();
        var text = screen.FindIndex(n => n.Node.Type == MockupComponent.Text && n.Node.A11y?.Decorative != true);
        var target = screen[Math.Max(text, 0)];

        var snippet = target.Node.A11y is null
            ? "by adding \"a11y\": {\"heading\": 1} to it"
            : "by adding \"heading\": 1 inside its \"a11y\"";

        return "Valid, but no node in this tree is a heading (a11y.heading). "
            + $"Make {Describe(target.Node)} at {target.Path} the main heading {snippet}. {Rest}";
    }

    /// <summary>
    /// The behaviour line: a states plus on with set example on the tree's own first button, which sets its own
    /// state (the smallest example the reader accepts), with an id added when the button has none.
    /// </summary>
    private static string Behaviour(IReadOnlyList<(MockupNode Node, string Path)> nodes)
    {
        const string Opening = "Valid, but this tree describes no behaviour (no states, on, at or a11y anywhere).";

        var button = nodes.FirstOrDefault(n => n.Node.Type == MockupComponent.Button);
        if (button.Node is null)
        {
            return $"{Opening} It has no button to build an example on: a node's \"on\" maps an event such as "
                + "\"click\" to {\"go\": a screen id}, {\"open\": a dialog id} or {\"set\": {a node id: one of that node's "
                + "own \"states\"}}.";
        }

        var id = button.Node.Id ?? DerivedId(button.Node.Text, nodes.Select(n => n.Node.Id).OfType<string>().ToHashSet(StringComparer.Ordinal));
        var add = button.Node.Id is null ? $"\"id\": \"{id}\", " : string.Empty;

        return $"{Opening} For example, give {Describe(button.Node)} at {button.Path} "
            + $"{add}\"states\": {{\"loading\": {{}}}}, \"on\": {{\"click\": {{\"set\": {{\"{id}\": \"loading\"}}}}}} "
            + "so a click shows it loading.";
    }

    /// <summary>
    /// An id from a button's words: lower case, every run of anything but an ASCII letter or digit one hyphen, a
    /// leading letter guaranteed, capped, and made unique against the tree's ids with a numeric suffix.
    /// </summary>
    private static string DerivedId(string? text, IReadOnlySet<string> taken)
    {
        var builder = new StringBuilder();
        foreach (var c in (text ?? string.Empty).ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var stem = builder.ToString().Trim('-');
        if (stem.Length == 0 || !char.IsAsciiLetter(stem[0]))
        {
            stem = stem.Length == 0 ? "button" : $"button-{stem}";
        }

        stem = stem[..Math.Min(stem.Length, DerivedIdLength)].TrimEnd('-');

        var id = stem;
        for (var n = 2; taken.Contains(id); n++)
        {
            id = $"{stem}-{n}";
        }

        return id;
    }

    /// <summary>How a line names a node in words: its text, else its id, else its type alone.</summary>
    private static string Describe(MockupNode node)
    {
        var type = MockupVocabulary.Name(node.Type);
        if (!string.IsNullOrEmpty(node.Text))
        {
            var words = node.Text.Length > QuotedLength ? $"{node.Text[..QuotedLength]}..." : node.Text;
            return $"the {type} {JsonSerializer.Serialize(words, Quoting)}";
        }

        return node.Id is { } id ? $"the {type} with the id \"{id}\"" : $"the {type}";
    }

    /// <summary>A node and every child under it, in document order with its JSON path, dialogs included, overrides not.</summary>
    private static IEnumerable<(MockupNode Node, string Path)> Walk(MockupNode node, string path) =>
        new[] { (node, path) }.Concat(
            (node.Children ?? []).SelectMany((child, i) => Walk(child, $"{path}.children[{i}]")));
}
