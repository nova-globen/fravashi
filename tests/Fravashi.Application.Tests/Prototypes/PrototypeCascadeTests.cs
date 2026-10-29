using System.Text.RegularExpressions;
using AwesomeAssertions;
using Fravashi.Application.Mockups;
using Fravashi.Application.Prototypes;
using Xunit;

namespace Fravashi.Application.Tests.Prototypes;

/// <summary>
/// Backlog 237 (plan @50): who wins the cascade on a generated page. The renderer's LOOK rules have zero specificity, so
/// any overlay rule beats them whatever selector it uses; its MECHANISM rules and the rules the design wrote for one node
/// keep theirs, so a general page rule cannot undo the prototype's behaviour. Read from the generated stylesheet with a
/// small specificity calculator; the live tier (<c>PrototypeCascadeLiveTests</c>) reads the same in a real browser.
/// </summary>
public class PrototypeCascadeTests
{
    /// <summary>Every base selector that keeps a specificity, by name, so a new base rule has to be classified here.</summary>
    private static readonly string[] Mechanism =
    [
        "[hidden]",
        "dialog:not([open])",
        ".mk-state-hidden",
        ".mk-text-variant",
        ".mk-state-disabled",
        ".mk-state-loading",
        ".mk-state-error",
        ".mk-state-selected",
        ".mk-state-focused",
        "[data-mk-select=\"rows\"] li:not(.mk-caption)",
        "[data-mk-select=\"rows\"] tbody tr",
    ];

    private static MockupDocument EveryFeature()
    {
        var read = MockupReader.Read(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Mockups", "Fixtures", "every-feature.json")));
        read.IsValid.Should().BeTrue(string.Join("\n", read.Errors));
        return read.Document!;
    }

    [Fact]
    public void Every_base_rule_but_the_mechanism_has_zero_specificity()
    {
        var rules = Rules(PrototypeRenderer.Render(EveryFeature(), []).Html);

        // The design's own rules name one node by its anchor (or its text's); everything else is the renderer's base.
        var baseSelectors = rules.Where(r => !r.InMedia).SelectMany(r => r.Selectors).Where(s => !NamesANode(s)).ToList();
        baseSelectors.Should().NotBeEmpty();

        baseSelectors.Where(s => Specificity(s) != (0, 0, 0)).Should().BeEquivalentTo(Mechanism,
            "only the mechanism keeps a specificity; a look rule must lose to any overlay rule");
        baseSelectors.Should().Contain(":where(.mk-button)");
    }

    [Fact]
    public void A_type_only_page_rule_outranks_every_rule_that_paints_a_button()
    {
        var html = PrototypeRenderer.Render(
            EveryFeature(), [new PrototypePageCssOperation("button { background-color: #1d4ed8; color: white; }")]).Html;
        var rules = Rules(html);

        var page = rules.Single(r => r.Selectors.SequenceEqual(["button"]));
        var painting = rules.Where(r => r != page && Regex.IsMatch(r.Declarations, @"(^|[;\s])(background|background-color|color)\s*:"))
            .SelectMany(r => r.Selectors.Select(s => (Rule: r, Selector: s)))
            .Where(p => p.Selector.Contains("button", StringComparison.Ordinal))
            .ToList();
        painting.Should().NotBeEmpty("the base paints buttons, which is what the page rule must beat");

        foreach (var (rule, selector) in painting)
        {
            rule.Important.Should().BeFalse(selector);
            Compare(Specificity(selector), Specificity("button")).Should().BeLessThanOrEqualTo(0, selector);
            rule.Index.Should().BeLessThan(page.Index, "a tie goes to the later rule, and the overlay comes after the base");
        }
    }

    [Fact]
    public void No_general_page_rule_outranks_the_mechanism_or_the_designs_own_rules()
    {
        var rules = Rules(PrototypeRenderer.Render(EveryFeature(), []).Html);

        // A rule written with types alone, however many, never outranks one class or attribute; an important one wins
        // over any rule that is not. So every mechanism rule is safe from `span { display: inline }` and `* { display: block }`.
        foreach (var rule in rules.Where(r => !r.InMedia && r.Selectors.Any(s => Mechanism.Contains(s))))
        {
            foreach (var selector in rule.Selectors)
            {
                (rule.Important || Specificity(selector).B > 0).Should().BeTrue(selector);
            }
        }

        // The design's own per-node rules (layout, viewport bands, state variants, hidden columns) keep theirs.
        var design = rules.SelectMany(r => r.Selectors).Where(NamesANode).ToList();
        design.Should().NotBeEmpty();
        design.Should().AllSatisfy(s => Specificity(s).B.Should().BeGreaterThan(0, s));
    }

    [Fact]
    public void The_page_stays_deterministic_and_the_script_and_policy_unchanged()
    {
        var overlay = new PrototypeOperation[] { new PrototypePageCssOperation("button { background-color: #1d4ed8; }") };
        var first = PrototypeRenderer.Render(EveryFeature(), overlay).Html;

        PrototypeRenderer.Render(EveryFeature(), overlay).Html.Should().Be(first);
        first.Should().Contain($"<script>{PrototypeRenderer.Script}</script>");
        PrototypeRenderer.ContentSecurityPolicy.Should().Contain($"script-src '{PrototypeRenderer.ScriptHash}'");
    }

    // ---- reading the stylesheet --------------------------------------------------------------------------

    private sealed record Rule(int Index, IReadOnlyList<string> Selectors, string Declarations, bool InMedia)
    {
        public bool Important => Declarations.Contains("!important", StringComparison.Ordinal);
    }

    private static bool NamesANode(string selector) =>
        selector.Contains("[data-mk-anchor=", StringComparison.Ordinal) || selector.Contains("[data-mk-text-of=", StringComparison.Ordinal);

    /// <summary>Every rule in the page's one stylesheet, in order, with the rules inside a media block flattened.</summary>
    private static List<Rule> Rules(string html)
    {
        var start = html.IndexOf("<style>", StringComparison.Ordinal) + "<style>".Length;
        var css = html[start..html.IndexOf("</style>", start, StringComparison.Ordinal)];
        var rules = new List<Rule>();
        Read(css, inMedia: false, rules);
        return rules;
    }

    private static void Read(string css, bool inMedia, List<Rule> rules)
    {
        var i = 0;
        while (true)
        {
            var open = IndexOutsideQuotes(css, '{', i);
            if (open < 0)
            {
                return;
            }

            var prelude = css[i..open].Trim();
            var close = MatchingBrace(css, open);
            var inside = css[(open + 1)..close];
            if (prelude.StartsWith('@'))
            {
                Read(inside, inMedia: true, rules);
            }
            else
            {
                rules.Add(new Rule(rules.Count, SplitTopLevel(prelude), inside.Trim(), inMedia));
            }

            i = close + 1;
        }
    }

    private static int IndexOutsideQuotes(string text, char wanted, int from)
    {
        char? quote = null;
        for (var i = from; i < text.Length; i++)
        {
            var c = text[i];
            if (quote is { } q)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == q)
                {
                    quote = null;
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == wanted)
            {
                return i;
            }
        }

        return -1;
    }

    private static int MatchingBrace(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] is '"' or '\'')
            {
                i = SkipQuoted(text, i);
            }
            else if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}' && --depth == 0)
            {
                return i;
            }
        }

        throw new InvalidOperationException("Unbalanced stylesheet.");
    }

    /// <summary>The index of the quote that closes the one at <paramref name="at"/>.</summary>
    private static int SkipQuoted(string text, int at)
    {
        for (var i = at + 1; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
            }
            else if (text[i] == text[at])
            {
                return i;
            }
        }

        return text.Length - 1;
    }

    /// <summary>Splits a selector list on the commas outside parentheses, brackets and quotes.</summary>
    private static List<string> SplitTopLevel(string list)
    {
        var parts = new List<string>();
        var depth = 0;
        var from = 0;
        for (var i = 0; i < list.Length; i++)
        {
            switch (list[i])
            {
                case '"' or '\'':
                    i = SkipQuoted(list, i);
                    break;
                case '(' or '[':
                    depth++;
                    break;
                case ')' or ']':
                    depth--;
                    break;
                case ',' when depth == 0:
                    parts.Add(list[from..i].Trim());
                    from = i + 1;
                    break;
            }
        }

        parts.Add(list[from..].Trim());
        return parts;
    }

    // ---- specificity (Selectors level 4) -------------------------------------------------------------------

    private static int Compare((int A, int B, int C) x, (int A, int B, int C) y) =>
        x.A != y.A ? x.A.CompareTo(y.A) : x.B != y.B ? x.B.CompareTo(y.B) : x.C.CompareTo(y.C);

    /// <summary>
    /// One complex selector's specificity: ids, then classes, attributes and pseudo-classes, then types and pseudo-elements.
    /// <c>:where()</c> counts nothing, <c>:not()</c>, <c>:is()</c> and <c>:has()</c> count their most specific argument.
    /// </summary>
    private static (int A, int B, int C) Specificity(string selector)
    {
        int a = 0, b = 0, c = 0;
        var i = 0;
        while (i < selector.Length)
        {
            var ch = selector[i];
            if (ch == '#')
            {
                a++;
                i = SkipIdent(selector, i + 1);
            }
            else if (ch == '.')
            {
                b++;
                i = SkipIdent(selector, i + 1);
            }
            else if (ch == '[')
            {
                b++;
                i = ClosingBracket(selector, i) + 1;
            }
            else if (ch == ':' && i + 1 < selector.Length && selector[i + 1] == ':')
            {
                c++;
                i = SkipIdent(selector, i + 2);
            }
            else if (ch == ':')
            {
                var end = SkipIdent(selector, i + 1);
                var name = selector[(i + 1)..end];
                if (end < selector.Length && selector[end] == '(')
                {
                    var close = ClosingParen(selector, end);
                    var argument = selector[(end + 1)..close];
                    if (name is "not" or "is" or "has")
                    {
                        var most = SplitTopLevel(argument).Select(Specificity).Aggregate((0, 0, 0), (m, s) => Compare(s, m) > 0 ? s : m);
                        a += most.Item1;
                        b += most.Item2;
                        c += most.Item3;
                    }
                    else if (name != "where")
                    {
                        b++;
                    }

                    i = close + 1;
                }
                else
                {
                    b++;
                    i = end;
                }
            }
            else if (char.IsLetter(ch))
            {
                c++;
                i = SkipIdent(selector, i);
            }
            else
            {
                i++;
            }
        }

        return (a, b, c);
    }

    private static int SkipIdent(string text, int from)
    {
        var i = from;
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '-' or '_'))
        {
            i++;
        }

        return i;
    }

    private static int ClosingBracket(string text, int open)
    {
        for (var i = open + 1; i < text.Length; i++)
        {
            if (text[i] is '"' or '\'')
            {
                i = SkipQuoted(text, i);
            }
            else if (text[i] == ']')
            {
                return i;
            }
        }

        throw new InvalidOperationException("Unclosed attribute selector.");
    }

    private static int ClosingParen(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] is '"' or '\'')
            {
                i = SkipQuoted(text, i);
            }
            else if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')' && --depth == 0)
            {
                return i;
            }
        }

        throw new InvalidOperationException("Unclosed parenthesis.");
    }
}
