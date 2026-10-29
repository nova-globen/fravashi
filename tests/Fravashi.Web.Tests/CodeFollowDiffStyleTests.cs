using System.Text.RegularExpressions;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Work item #369. bunit has no cascade, so what keeps the follow diff's Approve clear of Monaco is held here by
/// reading the sheet the studio serves: the header is not positioned over the editor, it wraps when narrow, and the
/// editor host is its own stacking context so its minimap cannot draw over what is laid on top of it.
/// </summary>
public class CodeFollowDiffStyleTests
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
    public void The_follow_diff_header_is_a_row_that_wraps_never_an_overlay()
    {
        var css = StudioCss();

        Assert.DoesNotMatch(@"position\s*:\s*absolute", Rule(css, ".code-follow-diff"));
        Assert.DoesNotMatch(@"position\s*:\s*absolute", Rule(css, ".code-follow-diff-head"));
        Assert.Matches(@"flex-wrap\s*:\s*wrap", Rule(css, ".code-follow-diff-head"));
        Assert.Matches(@"flex-wrap\s*:\s*wrap", Rule(css, ".code-follow-diff-actions"));

        // Only the body covers the editor.
        Assert.Matches(@"position\s*:\s*absolute", Rule(css, ".code-follow-diff-body"));
    }

    [Fact]
    public void The_editor_host_contains_its_own_stacking_so_the_minimap_cannot_cover_a_sibling()
    {
        Assert.Matches(@"isolation\s*:\s*isolate", Rule(StudioCss(), ".code-editor-host"));
    }
}
