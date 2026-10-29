using System.Text.RegularExpressions;

namespace Fravashi.Application.Prototypes;

/// <summary>
/// What an <c>insertHtml</c> overlay operation, and a <c>pageCss</c> rule, are refused for BY NAME (backlog
/// 211's acceptance): a script element, because an overlay is a wireframe adjustment and never a place to run
/// code, and an event-handler attribute (<c>onclick</c> and the rest), for the same reason by another route.
/// Backlog 213 added the elements that load, embed or navigate (<c>iframe</c>, <c>object</c>, <c>base</c>,
/// <c>meta</c>, <c>form</c> and their kin), a <c>javascript:</c> URL, and the renderer's own <c>data-mk-</c>
/// namespace.
/// <para>
/// This is a pattern list and can be passed (an entity-encoded scheme is one way): it is defence in depth
/// only. The boundary is the viewer's sandbox and the page's content security policy together (plan @38),
/// which hold whatever reaches the page. No HTML parser: <see cref="PrototypeRenderer"/> walks the tree it
/// generates, and this only has to recognise what it refuses, never to understand the rest of the fragment.
/// </para>
/// </summary>
public static class PrototypeHtmlGuard
{
    private static readonly Regex ScriptTagPattern = new(
        @"<\s*(/?)\s*script\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// An event handler anywhere in the fragment, not only inside what a <c>&lt;[^&gt;]*&gt;</c> match calls a
    /// tag (a <c>&gt;</c> inside a quoted value ends that match early), after whitespace, a slash or a quote:
    /// a browser reads <c>&lt;img src=x/onerror=...&gt;</c> and <c>alt="a"onerror=...</c> as handlers.
    /// </summary>
    private static readonly Regex EventHandlerAttributePattern = new(
        @"(?:^|[\s/""'])(on[a-zA-Z]+)\s*=", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The elements that load, embed or navigate, which a fragment never carries.</summary>
    private static readonly Regex RefusedElementPattern = new(
        @"<\s*(iframe|frameset|frame|object|embed|applet|portal|base|meta|link|form)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>A script URL scheme, with the whitespace and control characters a browser skips inside it.</summary>
    private static readonly Regex ScriptUrlPattern = new(
        @"(j[\s\x00-\x1f]*a[\s\x00-\x1f]*v[\s\x00-\x1f]*a|v[\s\x00-\x1f]*b)[\s\x00-\x1f]*s[\s\x00-\x1f]*c[\s\x00-\x1f]*r[\s\x00-\x1f]*i[\s\x00-\x1f]*p[\s\x00-\x1f]*t[\s\x00-\x1f]*:",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ReservedAttributeInFragmentPattern = new(
        @"(?:^|[\s/""'])(data-mk-[a-zA-Z0-9\-_:.]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AttributeNamePattern = new(
        @"^[a-zA-Z][a-zA-Z0-9\-_:.]*$", RegexOptions.Compiled);

    private static readonly Regex EventHandlerNamePattern = new(
        @"^on[a-zA-Z]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The breakout a page-level CSS rule is refused for landing inside a literal <c>&lt;style&gt;</c>.</summary>
    private const string StyleBreakout = "</style";

    /// <summary>The attribute <see cref="PrototypeRenderer"/> names every element by; an overlay never sets it.</summary>
    public const string ReservedAttributeName = "data-mk-anchor";

    /// <summary>
    /// The namespace of every attribute the renderer writes for the page's wiring (<c>data-mk-anchor</c>,
    /// <c>data-mk-on</c>, <c>data-mk-screen</c> and the rest): an overlay sets none of them and a fragment
    /// carries none, so what the page does is only ever what the tree says.
    /// </summary>
    public const string ReservedAttributePrefix = "data-mk-";

    /// <summary>True when the fragment carries a <c>&lt;script&gt;</c> (or closing) element, naming it.</summary>
    public static bool TryFindScriptElement(string html, out string tag)
    {
        var match = ScriptTagPattern.Match(html ?? string.Empty);
        tag = match.Success ? (match.Groups[1].Value == "/" ? "</script>" : "<script>") : string.Empty;
        return match.Success;
    }

    /// <summary>True when the fragment carries an event-handler attribute (<c>onclick</c> and the rest), naming it.</summary>
    public static bool TryFindEventHandlerAttribute(string html, out string name)
    {
        var match = EventHandlerAttributePattern.Match(html ?? string.Empty);
        name = match.Success ? match.Groups[1].Value : string.Empty;
        return match.Success;
    }

    /// <summary>True when the fragment carries an element that loads, embeds or navigates, naming it.</summary>
    public static bool TryFindRefusedElement(string html, out string element)
    {
        var match = RefusedElementPattern.Match(html ?? string.Empty);
        element = match.Success ? match.Groups[1].Value.ToLowerInvariant() : string.Empty;
        return match.Success;
    }

    /// <summary>True when the text carries a <c>javascript:</c> or <c>vbscript:</c> URL.</summary>
    public static bool ContainsScriptUrl(string text) => ScriptUrlPattern.IsMatch(text ?? string.Empty);

    /// <summary>True when the fragment carries an attribute in the renderer's <c>data-mk-</c> namespace, naming it.</summary>
    public static bool TryFindReservedAttribute(string html, out string name)
    {
        var match = ReservedAttributeInFragmentPattern.Match(html ?? string.Empty);
        name = match.Success ? match.Groups[1].Value : string.Empty;
        return match.Success;
    }

    /// <summary>
    /// Why an inserted fragment is refused, in a sentence, or null when nothing here refuses it. The one
    /// authority both <see cref="PrototypeReader"/> (which reports it) and <see cref="PrototypeRenderer"/>
    /// (which leaves such an operation unplaced) ask, so the two cannot drift apart.
    /// </summary>
    public static string? FragmentRefusal(string html)
    {
        if (TryFindScriptElement(html, out var tag))
        {
            return $"This fragment contains a \"{tag}\" element, and an inserted fragment can never carry a script.";
        }

        if (TryFindEventHandlerAttribute(html, out var attribute))
        {
            return $"This fragment contains the \"{attribute}\" attribute, an event handler, and an inserted "
                + "fragment can never carry one.";
        }

        if (TryFindRefusedElement(html, out var element))
        {
            return $"This fragment contains a \"<{element}>\" element, which loads, embeds or navigates, and an "
                + "inserted fragment can never carry one.";
        }

        if (ContainsScriptUrl(html))
        {
            return "This fragment contains a javascript: or vbscript: URL, and an inserted fragment can never "
                + "carry one.";
        }

        if (TryFindReservedAttribute(html, out var reserved))
        {
            return $"This fragment contains the \"{reserved}\" attribute; the data-mk- attributes are the "
                + "renderer's own wiring, and a fragment never carries one.";
        }

        return null;
    }

    /// <summary>True when a page-level CSS rule would close the <c>&lt;style&gt;</c> element it lands inside.</summary>
    public static bool ContainsStyleBreakout(string rule) =>
        (rule ?? string.Empty).Contains(StyleBreakout, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The one authority on whether <paramref name="name"/> is safe for a <c>setAttribute</c> operation to
    /// set: an ASCII letter, then letters, digits, <c>-</c>, <c>_</c>, <c>:</c> or <c>.</c>, excluding an
    /// event-handler name (<c>onclick</c> and the rest) and the renderer's own <see cref="ReservedAttributePrefix"/>
    /// namespace (<see cref="ReservedAttributeName"/> among it). <see cref="PrototypeReader"/> refuses a
    /// <c>setAttribute</c> naming anything else, and <see cref="PrototypeRenderer"/> leaves one unplaced rather than
    /// emit it, from this SAME predicate, so a name that slips past a shape check copied twice (for example
    /// <c>x" onclick="alert(1)"</c> or <c>x&gt;&lt;img src=x onerror=alert(1)&gt;</c>, neither of which starts
    /// with "on") can never land as a working attribute either way.
    /// </summary>
    public static bool IsAttributeName(string name) =>
        !string.IsNullOrEmpty(name)
        && AttributeNamePattern.IsMatch(name)
        && !EventHandlerNamePattern.IsMatch(name)
        && !name.StartsWith(ReservedAttributePrefix, StringComparison.OrdinalIgnoreCase);
}
