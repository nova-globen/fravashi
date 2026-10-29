using System.Text.RegularExpressions;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Work item #363. bunit has no cascade, so what the board's columns and the reading columns do with a wide window is
/// held here by reading the sheet the studio serves. The layout itself (all seven columns visible at 1920px, the
/// reading columns centred) is the window's to prove.
/// </summary>
public class BoardAndReadingLayoutStyleTests
{
    private static string StudioCss()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fravashi.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "the test must be able to locate the repository root (Fravashi.sln)");
        var path = Path.Combine(dir!.FullName, "src", "Fravashi.Web", "wwwroot", "studio.css");
        return Regex.Replace(File.ReadAllText(path), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
    }

    /// <summary>The body of the first rule whose selector list contains exactly <paramref name="selector"/>.</summary>
    private static string Rule(string css, string selector)
    {
        foreach (Match m in Regex.Matches(css, @"(?<selector>[^{}]+)\{(?<body>[^{}]*)\}"))
        {
            var selectors = m.Groups["selector"].Value.Split(',').Select(s => Regex.Replace(s, @"\s+", " ").Trim());
            if (selectors.Contains(selector))
            {
                return m.Groups["body"].Value;
            }
        }

        Assert.Fail($"studio.css should have a rule for {selector}");
        return string.Empty;
    }

    [Fact]
    public void The_board_columns_share_the_width_instead_of_each_taking_a_fixed_one()
    {
        // Seven columns of a fixed 240px are 1776px before gaps, so at 1920px with the sidebar open Done was behind
        // a horizontal scroll bar. They now grow and shrink from a zero basis down to a readable minimum.
        var column = Rule(StudioCss(), ".board-column");

        Assert.Matches(@"(?<![\w-])flex\s*:\s*1 1 0\s*;", column);
        Assert.DoesNotMatch(@"(?<![\w-])flex\s*:\s*0 0 \d+px", column);
        Assert.DoesNotMatch(@"(?<![\w-])(width|flex-basis)\s*:\s*\d+px", column);

        var minimum = Regex.Match(column, @"min-width\s*:\s*(?<px>\d+)px");
        Assert.True(minimum.Success, "a column needs a readable minimum width");
        Assert.InRange(int.Parse(minimum.Groups["px"].Value, System.Globalization.CultureInfo.InvariantCulture), 140, 200);

        // Only a window genuinely too narrow scrolls the row sideways, and a long column scrolls on its own.
        Assert.Matches(@"overflow-x\s*:\s*auto", Rule(StudioCss(), ".board-columns"));
        Assert.Matches(@"overflow-y\s*:\s*auto", column);
    }

    [Theory]
    [InlineData("triage")]
    [InlineData("backlog")]
    [InlineData("ready")]
    [InlineData("in_progress")]
    [InlineData("in_review")]
    [InlineData("done")]
    [InlineData("cancelled")]
    public void Every_status_has_an_accent_drawn_from_a_theme_token(string status)
    {
        var body = Rule(StudioCss(), $".board-card-{status}");

        Assert.Matches(@"--card-accent\s*:\s*var\(--[\w-]+\)", body);
    }

    [Theory]
    [InlineData(".work-item-detail")]
    [InlineData(".wiki-article")]
    [InlineData(".wiki-editor")]
    [InlineData(".page-body-content")]
    [InlineData(".page-body .ob-step")]
    public void A_reading_column_keeps_its_measure_and_centres(string selector)
    {
        var body = Rule(StudioCss(), selector);

        Assert.Matches(@"max-width\s*:\s*\d+px", body);
        Assert.Matches(@"margin-inline\s*:\s*auto", body);
    }
}
