using System.Text.RegularExpressions;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Work item #363. The studio has ONE type scale, one weight set and one radius set, as tokens on <c>:root</c> in
/// studio.css. Before it, the sheet carried twenty font sizes from 7 to 34px (half pixels included), weights up to
/// 800 on whole paragraphs and seven different corner radii, and nothing stopped the next rule from adding one more.
/// So this reads the sheet the studio serves and fails on any literal font-size, font-weight or border-radius
/// outside the token definitions, except the few listed below, each with its reason. bunit has no cascade, so the
/// sheet itself is the only place this can be held.
/// </summary>
public class StudioDesignTokenTests
{
    private static readonly string[] SizeTokens = ["xs", "sm", "md", "lg", "xl", "2xl", "3xl"];

    /// <summary>
    /// The declarations the scale cannot express, as (selector, property, value). Add one only with its reason here.
    /// </summary>
    private static readonly (string Selector, string Property, string Value, string Why)[] Exceptions =
    [
        (".progress-ring-value", "font-size", "8px", "SVG text in a 42-unit viewBox: user units that scale with the ring, not pixels"),
        (".progress-ring-caption", "font-size", "3.6px", "SVG text in a 42-unit viewBox: user units that scale with the ring, not pixels"),
        (".update-lock-icon", "font-size", "34px", "an emoji glyph used as the lock screens' picture, not text"),
        (".setup-check", "border-radius", "4px", "a 16px check box: the scale's smallest radius rounds it into something that reads as a radio button"),
        (".session-gov-chip-mark", "font-size", "7px", "a bullet glyph sized as a dot inside a chip, not text"),
        (".home-card .dash-panel-head > :first-child", "font-size", "15px", "Home's card titles, 15px Display by the approved trial, between the scale's 14 and 16"),
    ];

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fravashi.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "the test must be able to locate the repository root (Fravashi.sln)");
        return dir!.FullName;
    }

    private static string StudioCss()
    {
        var path = Path.Combine(RepositoryRoot(), "src", "Fravashi.Web", "wwwroot", "studio.css");
        Assert.True(File.Exists(path), $"the studio should ship studio.css at {path}");
        return Regex.Replace(File.ReadAllText(path), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
    }

    /// <summary>Every (selector, property, value) declaration, one per selector of a selector list.</summary>
    private static IEnumerable<(string Selector, string Property, string Value)> Declarations(string css) =>
        Regex.Matches(css, @"(?<selector>[^{}]+)\{(?<body>[^{}]*)\}")
            .SelectMany(m =>
            {
                var selectors = m.Groups["selector"].Value.Split(',')
                    .Select(s => Regex.Replace(s, @"\s+", " ").Trim())
                    .Where(s => s.Length > 0)
                    .ToList();
                var declarations = Regex.Matches(m.Groups["body"].Value, @"(?<![\w-])(?<property>font-size|font-weight|border-radius|font)\s*:\s*(?<value>[^;]+)")
                    .Select(d => (Property: d.Groups["property"].Value, Value: Regex.Replace(d.Groups["value"].Value, @"\s+", " ").Trim()));
                return selectors.SelectMany(s => declarations.Select(d => (s, d.Property, d.Value)));
            });

    private static bool Allowed(string property, string value) => property switch
    {
        "font-size" => value == "inherit"
            || Regex.IsMatch(value, @"^var\(--fs-(" + string.Join("|", SizeTokens) + @")\)$")
            // Relative to the surrounding text (headings inside rendered markdown): they follow the scale.
            || Regex.IsMatch(value, @"^\d*\.?\d+(em|%)$"),
        "font-weight" => value == "inherit" || Regex.IsMatch(value, @"^var\(--fw-(regular|medium|semibold)\)$"),
        "border-radius" => value.Split(' ').All(part =>
            part is "0" or "999px" or "50%" or "inherit" || Regex.IsMatch(part, @"^var\(--r-(sm|md|lg)\)$")),
        "font" => value == "inherit",
        _ => true,
    };

    [Fact]
    public void No_rule_writes_a_literal_font_size_weight_or_radius_outside_the_tokens()
    {
        var offenders = Declarations(StudioCss())
            .Where(d => !Allowed(d.Property, d.Value))
            .Where(d => !Exceptions.Any(e => e.Selector == d.Selector && e.Property == d.Property && e.Value == d.Value))
            .Select(d => $"{d.Selector} {{ {d.Property}: {d.Value} }}")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "use the --fs-*, --fw-* and --r-* tokens (or list a real exception with its reason), found: "
                + string.Join(" | ", offenders));
    }

    [Fact]
    public void Every_listed_exception_is_still_in_the_sheet()
    {
        // A stale exception is a hole someone else can walk through, so the list cannot outlive its rules.
        var declared = Declarations(StudioCss()).ToHashSet();

        var stale = Exceptions
            .Where(e => !declared.Contains((e.Selector, e.Property, e.Value)))
            .Select(e => $"{e.Selector} {{ {e.Property}: {e.Value} }}")
            .ToList();

        Assert.True(stale.Count == 0, "exceptions that no longer match a rule, remove them: " + string.Join(" | ", stale));
    }

    [Fact]
    public void The_tokens_are_defined_on_root_with_the_approved_values()
    {
        var root = Regex.Matches(StudioCss(), @"(?<selector>[^{}]+)\{(?<body>[^{}]*)\}")
            .Where(m => m.Groups["selector"].Value.Trim() == ":root")
            .Select(m => m.Groups["body"].Value)
            .Aggregate(string.Empty, string.Concat);

        string Token(string name)
        {
            var match = Regex.Match(root, @"(?<![\w-])" + Regex.Escape(name) + @"\s*:\s*(?<value>[^;]+);");
            Assert.True(match.Success, $":root must define {name}");
            return Regex.Replace(match.Groups["value"].Value, @"\s+", " ").Trim();
        }

        Assert.Equal(
            ["11px", "12px", "13px", "14px", "16px", "20px", "28px"],
            SizeTokens.Select(t => Token($"--fs-{t}")));
        Assert.Equal(["400", "500", "600"], new[] { "regular", "medium", "semibold" }.Select(t => Token($"--fw-{t}")));
        Assert.Equal(["6px", "8px", "12px"], new[] { "sm", "md", "lg" }.Select(t => Token($"--r-{t}")));

        // System faces only: Windows 11's Segoe UI Variable first, plain Segoe UI and system-ui after it, no web font.
        Assert.StartsWith("\"Segoe UI Variable Text\"", Token("--font-text"), StringComparison.Ordinal);
        Assert.StartsWith("\"Segoe UI Variable Display\"", Token("--font-display"), StringComparison.Ordinal);
        Assert.DoesNotContain("@font-face", StudioCss(), StringComparison.Ordinal);
        Assert.DoesNotContain("@import", StudioCss(), StringComparison.Ordinal);
    }

    [Fact]
    public void Form_controls_inherit_the_page_font()
    {
        // Left to the browser, a button drew at 13.333px in another face: the card titles inside buttons never
        // matched the text around them.
        var rules = Declarations(StudioCss()).ToList();

        string[] controls = ["button", "input", "select", "textarea"];
        var missing = controls.Where(c => !rules.Any(r => r.Selector == c && r.Property == "font" && r.Value == "inherit")).ToList();

        Assert.True(missing.Count == 0, "these controls must declare font: inherit: " + string.Join(" | ", missing));
        Assert.Matches(@"(^|[\s}])body\s*\{\s*font-family\s*:\s*var\(--font-text\)", StudioCss());
    }
}
