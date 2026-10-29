using System.Text.RegularExpressions;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Standing guards over the shipped stylesheets, for defects that a browser accepts in silence.
/// bunit has no layout and no cascade, so a rendered component proves nothing here; these read the
/// asset the studio actually serves, exactly as <see cref="TranscriptScrollAnchoringTests"/> does.
///
/// Two things are held:
///
/// 1. Backlog 28. A row that acts on click must not also behave like a paragraph: double-clicking a
///    folder in the explorer ran the browser's native word selection and the blue highlight spread
///    across every sibling row. The cure, <c>user-select: none</c>, INHERITS, so it is the kind of
///    fix that is easy to apply one element too high: a single declaration on the page, the body
///    split, or the editor area would take Monaco's selection, the markdown preview and the
///    transcript with it, and nothing would report that. So the rule is not only "the rows suppress
///    selection" but also "nothing else does", and the second half is checked as an exact set: a new
///    <c>user-select: none</c> fails this test until somebody adds its selector below on purpose.
///
/// 2. An undefined design token. <c>var(--surface-1)</c> resolves to nothing and ships a fully
///    transparent background with no error of any kind; it has already happened twice. A usage with
///    a fallback is deliberate and allowed (app.css runs before studio.css loads and uses only that
///    form); a usage without one has to name a token the stylesheets define.
/// </summary>
public class StudioStylesheetGuardTests
{
    /// <summary>
    /// The rows whose click is an action (open, toggle, activate), where a text selection is never
    /// what the user meant. Deliberately NOT here: <c>.code-search-match</c>, whose content is a line
    /// of the user's own source and worth copying, and every ancestor of the editor.
    /// </summary>
    private static readonly string[] RowsThatActOnClick =
    [
        ".code-tree-body",
        ".code-open-editor",
        ".code-tab",
        ".code-search-file-row",
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

    private static string Asset(string name)
    {
        var path = Path.Combine(RepositoryRoot(), "src", "Fravashi.Web", "wwwroot", name);
        Assert.True(File.Exists(path), $"the studio should ship {name} at {path}");
        return Regex.Replace(File.ReadAllText(path), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
    }

    /// <summary>
    /// Selector + body pairs. A body never contains braces, so an <c>@media</c> or <c>@keyframes</c>
    /// wrapper simply drops out and the rules inside it are matched on their own.
    /// </summary>
    private static IEnumerable<(string Selector, string Body)> Rules(string css) =>
        Regex.Matches(css, @"(?<selector>[^{}]+)\{(?<body>[^{}]*)\}")
            .SelectMany(m => m.Groups["selector"].Value
                .Split(',')
                .Select(s => (Selector: Regex.Replace(s, @"\s+", " ").Trim(), Body: m.Groups["body"].Value)))
            .Where(r => r.Selector.Length > 0);

    private static bool SuppressesSelection(string body) =>
        Regex.IsMatch(body, @"(^|[\s;])(-webkit-)?user-select\s*:\s*none");

    [Fact]
    public void A_row_that_acts_on_click_does_not_select_text_when_it_is_double_clicked()
    {
        var declaring = Rules(Asset("studio.css"))
            .Where(r => SuppressesSelection(r.Body))
            .Select(r => r.Selector)
            .ToHashSet(StringComparer.Ordinal);

        var missing = RowsThatActOnClick.Where(s => !declaring.Contains(s)).ToList();

        Assert.True(
            missing.Count == 0,
            "every click-to-act row must declare user-select: none, missing: " + string.Join(" | ", missing));
    }

    [Fact]
    public void Nothing_outside_those_rows_suppresses_selection_where_the_user_reads_or_copies()
    {
        var allowed = RowsThatActOnClick.ToHashSet(StringComparer.Ordinal);

        var offenders = Rules(Asset("studio.css"))
            .Where(r => SuppressesSelection(r.Body))
            .Select(r => r.Selector)
            .Where(s => !allowed.Contains(s))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // user-select inherits, so this is the half that keeps the cure from spreading. If a new
        // selector genuinely needs it, add it to RowsThatActOnClick and say why in its comment;
        // do not widen this test to a prefix match, because .code-body and .code-page would pass one.
        Assert.True(
            offenders.Count == 0,
            "only a click-to-act row may suppress text selection, found: " + string.Join(" | ", offenders));
    }

    [Fact]
    public void Every_design_token_used_without_a_fallback_is_actually_defined()
    {
        var sheets = new[] { Asset("studio.css"), Asset("app.css") };

        var defined = sheets
            .SelectMany(css => Regex.Matches(css, @"(?<![\w-])(?<name>--[a-z0-9-]+)\s*:").Select(m => m.Groups["name"].Value))
            .ToHashSet(StringComparer.Ordinal);

        var undefined = sheets
            .SelectMany(css => Regex.Matches(css, @"var\(\s*(?<name>--[a-z0-9-]+)\s*\)").Select(m => m.Groups["name"].Value))
            .Where(name => !defined.Contains(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            undefined.Count == 0,
            "a var() with no fallback must name a defined token, undefined: " + string.Join(" | ", undefined));
    }

    /// <summary>
    /// Backlog 23. There are two agent-panel toggles in the markup and CSS picks which one a user
    /// sees: the title bar's exists only under <c>html.window-host</c>, so the activity rail's has to
    /// be hidden by exactly that class. Get the gate backwards or drop it and the result is either two
    /// toggles in the window shell or none in a browser, and bunit has no cascade to notice either.
    /// </summary>
    [Fact]
    public void The_fallback_agent_toggle_is_hidden_exactly_where_the_title_bar_appears()
    {
        var rules = Rules(Asset("studio.css")).ToList();

        var titleBarShownBy = rules
            .Where(r => Regex.IsMatch(r.Body, @"display\s*:\s*flex"))
            .Select(r => r.Selector)
            .Where(s => s.EndsWith(".studio-titlebar", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            titleBarShownBy.Contains("html.window-host .studio-titlebar"),
            "the title bar is revealed by html.window-host, found: " + string.Join(" | ", titleBarShownBy));

        var fallbackHiddenBy = rules
            .Where(r => Regex.IsMatch(r.Body, @"display\s*:\s*none"))
            .Select(r => r.Selector)
            .Where(s => s.Contains(".studio-agent-fallback", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            fallbackHiddenBy.Contains("html.window-host .studio-agent-fallback"),
            "the rail's fallback toggle must be hidden by html.window-host and nothing else, found: "
                + string.Join(" | ", fallbackHiddenBy));
    }

    /// <summary>
    /// Backlog 18. The redesigned agent panel has to hold up at BOTH widths it is rendered at: the Code
    /// page's panel, whose narrowest real width is 280px (the floor of .code-body-split.with-session's
    /// clamp), and the full Sessions page. bunit has no layout, so the property that makes that true is
    /// checked where it is declared: the panel is a stack of bands and every band except the transcript
    /// refuses to shrink, so a narrow pane takes its height out of the conversation and never out of the
    /// composer, the approvals or the usage strip. Get one band wrong and it is the composer that leaves
    /// the pane, which is the defect backlog 16 already had to fix once.
    /// </summary>
    [Fact]
    public void Every_band_of_the_agent_panel_keeps_its_height_so_only_the_transcript_gives_way()
    {
        var rules = Rules(Asset("studio.css")).ToList();

        string[] bands = [".session-pane-head", ".session-gov", ".session-usage", ".session-composer", ".session-confirm", ".session-node"];

        var soft = bands.Where(band => !rules.Any(r =>
            r.Selector == band && Regex.IsMatch(r.Body, @"(^|[\s;])flex\s*:\s*none"))).ToList();

        Assert.True(
            soft.Count == 0,
            "every band around the transcript must declare flex: none, missing on: " + string.Join(" | ", soft));

        // And the thing that does give way, in both of the ways that matter. The lopsided SHRINK factor
        // is backlog 17's fix: with equal factors a tight pane squeezes a single approval card to a
        // sliver while the transcript keeps most of its height. The zero BASIS is backlog 18's: with an
        // auto basis the conversation's own content height joins the total the flex algorithm starts
        // from, and a long transcript then clipped the composer's last line off the bottom of the pane.
        Assert.Contains(
            rules,
            r => r.Selector == ".session-transcript-wrap" && Regex.IsMatch(r.Body, @"flex\s*:\s*1\s+1000\s+0\s*;"));
    }

    /// <summary>
    /// Backlog 18 and 19: the four-line usage stack became ONE line, and one line is a layout property
    /// rather than a markup one. Two declarations carry it, and either one alone lets the strip wrap the
    /// moment the panel is narrow: the row does not wrap, and the meter is the only elastic thing in it,
    /// so it is what shrinks when the numbers need the width.
    /// </summary>
    [Fact]
    public void The_usage_strip_stays_one_line_at_any_panel_width()
    {
        var rules = Rules(Asset("studio.css")).ToList();

        var strip = rules.Where(r => r.Selector == ".session-usage").ToList();
        Assert.NotEmpty(strip);
        Assert.Contains(strip, r => Regex.IsMatch(r.Body, @"white-space\s*:\s*nowrap"));
        Assert.DoesNotContain(strip, r => Regex.IsMatch(r.Body, @"flex-wrap\s*:\s*wrap"));

        Assert.Contains(
            rules,
            r => r.Selector == ".session-usage-meter" && Regex.IsMatch(r.Body, @"flex\s*:\s*1\s+1\s+auto"));
    }

    /// <summary>
    /// Backlog 22. The empty state centres itself in the pane, and a centred flex item that is ALSO
    /// allowed to shrink below its content is a trap that a browser reports in no way at all: the
    /// overflow goes out of both ends equally and a scroll container can only reach the end it overflows
    /// downwards. Measured in Chromium against this stylesheet in a 300px pane, <c>min-height: 0</c>
    /// squeezed the block to 54px and left its heading and the sentence about working on a copy of the
    /// repository sitting above scrollTop 0, unreachable at any scroll position. The state whose whole
    /// purpose is to answer the trust question would have answered it off screen.
    ///
    /// The zero was not invented, it was inherited: every scrolling band in this pane declares it, and
    /// this block has no scroller of its own to take the job over. So the guard is on the empty state's
    /// automatic minimum size specifically, and it is why the stylesheet writes the initial value out.
    /// </summary>
    [Fact]
    public void The_empty_state_is_never_compressed_below_its_own_content()
    {
        var rules = Rules(Asset("studio.css")).ToList();

        var empty = rules.Where(r => r.Selector == ".session-empty").ToList();
        Assert.NotEmpty(empty);

        Assert.DoesNotContain(
            empty,
            r => Regex.IsMatch(r.Body, @"min-height\s*:\s*0"));
        Assert.Contains(
            empty,
            r => Regex.IsMatch(r.Body, @"min-height\s*:\s*auto"));

        // And the pane's body is what scrolls when the block does not fit, rather than clipping it.
        Assert.Contains(
            rules,
            r => r.Selector == ".session-pane-body" && Regex.IsMatch(r.Body, @"overflow-y\s*:\s*auto"));
    }

    /// <summary>
    /// Backlog 31 asks that the title bar's branch dropdown keep local, ahead, behind and not-published
    /// distinguishable "matching the Git page's existing colouring". Two stylesheet rules with the same
    /// hex value in them match until somebody edits one of them, which is a thing no test would report,
    /// so the requirement is held as identity instead: for every branch state, the Git page's row and the
    /// dropdown's option row must be coloured by the SAME declaration.
    /// </summary>
    [Fact]
    public void Every_branch_state_colours_the_git_page_and_the_title_bar_dropdown_in_one_declaration()
    {
        var css = Asset("studio.css");

        // Rule bodies, keyed by the selectors that share them. A shared body means one declaration.
        var bodies = Regex.Matches(css, @"(?<selector>[^{}]+)\{(?<body>[^{}]*)\}")
            .Select(m => (
                Selectors: m.Groups["selector"].Value.Split(',')
                    .Select(s => Regex.Replace(s, @"\s+", " ").Trim())
                    .Where(s => s.Length > 0)
                    .ToArray(),
                Body: m.Groups["body"].Value))
            .Where(r => Regex.IsMatch(r.Body, @"(^|[\s;])color\s*:"))
            .ToList();

        string[] states = ["git-b-synced", "git-b-ahead", "git-b-behind", "git-b-diverged", "git-b-unpublished"];

        var split = states.Where(state =>
        {
            var shared = bodies.FirstOrDefault(r =>
                r.Selectors.Contains($".{state} .git-branch-status", StringComparer.Ordinal)
                && r.Selectors.Contains($".{state} .studio-dropdown-opt-subtitle", StringComparer.Ordinal));
            return shared.Selectors is null;
        }).ToList();

        Assert.True(
            split.Count == 0,
            "the Git page's branch row and the title bar's dropdown option must share one colour "
                + "declaration per state, not shared for: " + string.Join(" | ", split));
    }

    /// <summary>
    /// Backlog 112. The brand accent is one value in one file (<c>docs/brand/color.txt</c>), and the
    /// stylesheet defines it TWICE, once per theme. A rebrand that updates only the light block leaves
    /// the dark theme on the old colour and nothing reports it, so both definitions are read and both
    /// are held against the brand file itself rather than against a hex repeated in this test.
    /// </summary>
    [Fact]
    public void The_accent_token_is_the_brand_colour_in_both_themes()
    {
        var brand = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "brand", "color.txt")).Trim();
        Assert.Matches("^#[0-9a-fA-F]{6}$", brand);

        string[] themes = [":root", "html[data-theme=\"dark\"]"];
        var accents = Rules(Asset("studio.css"))
            .Where(r => themes.Contains(r.Selector, StringComparer.Ordinal))
            .Select(r => (r.Selector, Match: Regex.Match(r.Body, @"(?<![\w-])--accent\s*:\s*(?<value>[^;]+);")))
            .Where(r => r.Match.Success)
            .Select(r => (r.Selector, Value: r.Match.Groups["value"].Value.Trim()))
            .ToList();

        var undeclared = themes.Where(t => !accents.Any(a => a.Selector == t)).ToList();
        Assert.True(
            undeclared.Count == 0,
            "every theme block must define --accent, missing in: " + string.Join(" | ", undeclared));

        var wrong = accents
            .Where(a => !string.Equals(a.Value, brand, StringComparison.OrdinalIgnoreCase))
            .Select(a => $"{a.Selector} = {a.Value}")
            .ToList();
        Assert.True(
            wrong.Count == 0,
            $"--accent must be the brand colour {brand} in every theme, but: " + string.Join(" | ", wrong));
    }

    /// <summary>
    /// Text on the green fill is <c>--on-accent</c>, in every rule that sets both. <c>--accent-text</c> is the green
    /// for text on a NEUTRAL surface, and it reads as one ("accent" and "text"), so it was the colour picked for
    /// <c>.studio-button</c>: light green on green in the dark theme, and the Edit and Re-run buttons in Settings
    /// could not be read (owner report 2026-09-30). A browser reports nothing, bunit has no cascade, and the same
    /// page's <c>.studio-btn</c> was right, so only a rule over the sheet itself holds it.
    /// </summary>
    [Fact]
    public void Text_on_the_accent_fill_is_the_on_accent_colour()
    {
        var wrong = Rules(Asset("studio.css"))
            .Where(r => Regex.IsMatch(r.Body, @"(^|[\s;])background(-color)?\s*:\s*var\(--accent(-hover)?\)"))
            .Select(r => (r.Selector, Color: Regex.Match(r.Body, @"(^|[\s;])color\s*:\s*(?<value>[^;]+)")))
            .Where(r => r.Color.Success && r.Color.Groups["value"].Value.Trim() != "var(--on-accent)")
            .Select(r => $"{r.Selector} {{ color: {r.Color.Groups["value"].Value.Trim()} }}")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            wrong.Count == 0,
            "a rule that fills with the accent must colour its text var(--on-accent), but: " + string.Join(" | ", wrong));
    }

    /// <summary>
    /// The derived tokens are the other half of that rebrand, and they are the half that hides: a
    /// leftover <c>--accent-soft</c> or <c>--accent-text</c> from the retired amber still renders, still
    /// reads, and simply looks like a slightly odd chip. So the retired values are named and banned.
    /// </summary>
    [Fact]
    public void No_value_derived_from_the_retired_accent_survives_anywhere_in_the_stylesheet()
    {
        string[] retired =
        [
            "#ffc418",   // the old accent
            "#e0a800",   // its light-theme hover
            "#fff5d6",   // its light-theme soft chip
            "#9a6a00",   // its light-theme text colour
            "#ffd75e",   // its dark-theme hover and text colour
            "255, 196, 24", "255,196,24",   // its rings and soft chip, as rgba components
        ];

        var css = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Fravashi.Web", "wwwroot", "studio.css"));

        var found = retired.Where(v => css.Contains(v, StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.True(found.Count == 0, "retired accent values still in studio.css: " + string.Join(", ", found));
    }
}
