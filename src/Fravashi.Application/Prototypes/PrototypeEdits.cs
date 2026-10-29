namespace Fravashi.Application.Prototypes;

/// <summary>An edit the panel asked for: the one operation it becomes, or why it becomes none.</summary>
/// <param name="Operation">The operation, when the edit is allowed.</param>
/// <param name="Refusal">Why not, in words, when it is not.</param>
public sealed record PrototypeEditResult(PrototypeOperation? Operation, string? Refusal)
{
    internal static PrototypeEditResult Of(PrototypeOperation operation) => new(operation, null);

    internal static PrototypeEditResult Refused(string why) => new(null, why);
}

/// <summary>
/// The direct edits the prototype's page offers (backlog 214, plan @39), each made into exactly ONE overlay operation
/// here and nowhere else: a node's text, one of a few styles, or a page-level rule. Whatever the reader would accept
/// from an agent, a person's edit is held to a narrower shape, so the panel cannot write a declaration that turns into
/// several or reaches outside the page.
/// </summary>
public static class PrototypeEdits
{
    /// <summary>The longest style value the panel writes.</summary>
    public const int MaxStyleValue = 200;

    /// <summary>The longest page-level rule the panel writes.</summary>
    public const int MaxPageRule = 2000;

    /// <summary>The styles the panel edits, in the order it shows them (the item's list).</summary>
    public static IReadOnlyList<string> Styles { get; } =
        ["color", "background-color", "font-size", "font-weight", "padding", "margin", "text-align"];

    /// <summary>A <c>setText</c>. The renderer encodes text, so any text is allowed, the empty text included.</summary>
    public static PrototypeEditResult SetText(PrototypeAnchor anchor, string text)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(text);
        return PrototypeEditResult.Of(new PrototypeSetTextOperation(anchor, text));
    }

    /// <summary>A <c>setCss</c> of ONE of <see cref="Styles"/>, with a value that is one plain declaration value.</summary>
    public static PrototypeEditResult SetStyle(PrototypeAnchor anchor, string property, string value)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(value);

        if (!Styles.Contains(property, StringComparer.Ordinal))
        {
            return PrototypeEditResult.Refused(
                $"\"{property}\" is not a style the page edits; it edits {string.Join(", ", Styles)}.");
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return PrototypeEditResult.Refused($"A value for {property} is needed; an edit already made is taken back with Undo.");
        }

        if (trimmed.Length > MaxStyleValue)
        {
            return PrototypeEditResult.Refused($"A value for {property} is at most {MaxStyleValue} characters.");
        }

        if (trimmed.IndexOfAny([';', '{', '}', '<', '>', '\\', '\n', '\r']) >= 0)
        {
            return PrototypeEditResult.Refused(
                $"A value for {property} is one plain value: it cannot hold ; {{ }} < > \\ or a line break.");
        }

        if (trimmed.Contains("url(", StringComparison.OrdinalIgnoreCase))
        {
            return PrototypeEditResult.Refused($"A value for {property} cannot name a url(...): the page loads nothing.");
        }

        return PrototypeEditResult.Of(new PrototypeSetCssOperation(
            anchor, new Dictionary<string, string>(StringComparer.Ordinal) { [property] = trimmed }));
    }

    /// <summary>A <c>pageCss</c>: one rule added to the page's own stylesheet.</summary>
    public static PrototypeEditResult PageRule(string rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var trimmed = rule.Trim();
        if (trimmed.Length == 0)
        {
            return PrototypeEditResult.Refused("A page rule needs some CSS, for example .mk-button { border-radius: 1rem; }.");
        }

        if (trimmed.Length > MaxPageRule)
        {
            return PrototypeEditResult.Refused($"A page rule is at most {MaxPageRule} characters.");
        }

        if (PrototypeHtmlGuard.ContainsStyleBreakout(trimmed))
        {
            return PrototypeEditResult.Refused(
                "This rule contains \"</style\", which would close the style element it lands inside.");
        }

        return PrototypeEditResult.Of(new PrototypePageCssOperation(trimmed));
    }
}
