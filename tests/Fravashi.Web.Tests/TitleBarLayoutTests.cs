using System.Text.RegularExpressions;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 125: the title bar's centre pill is absolutely window-centred, so nothing in normal flow stops it
/// running into the left cluster. At the window's real minimum (760px wide) it did: measured in the studio
/// window, the cluster ended at x=378 and the pill started at x=216, and the workspace name, the branch and
/// the title were drawn over each other.
///
/// Like <see cref="TranscriptScrollAnchoringTests"/>, these read the shipped stylesheet on purpose. The
/// behaviour is layout and bunit has none, so the proof was the real engine: the edited studio.css loaded
/// over the installed one in the studio window showed no overlap at 760, 900, 1200 and 1440, both names whole
/// from 900 up, and the pill back at its full 480px at the usual size. What these guard is that the two
/// halves of that arithmetic stay tied to ONE number, because changing either alone brings the overlap back.
/// </summary>
public class TitleBarLayoutTests
{
    private static string Stylesheet()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fravashi.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "the test must be able to locate the repository root (Fravashi.sln)");
        var css = File.ReadAllText(Path.Combine(dir!.FullName, "src", "Fravashi.Web", "wwwroot", "studio.css"));
        return Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
    }

    private static string Body(string css, string selector)
    {
        var match = Regex.Match(css, Regex.Escape(selector) + @"\s*\{(?<body>[^{}]*)\}");
        Assert.True(match.Success, $"studio.css should still declare '{selector}'");
        return match.Groups["body"].Value;
    }

    [Fact]
    public void The_pill_and_the_left_cluster_are_sized_from_the_same_number()
    {
        var css = Stylesheet();

        Assert.Matches(@"--titlebar-pill\s*:\s*clamp\(", Body(css, "html.window-host .studio-titlebar"));
        Assert.Matches(@"width\s*:\s*var\(--titlebar-pill\)", Body(css, ".studio-titlebar-search"));
        Assert.Matches(@"max-width\s*:\s*calc\([^;]*var\(--titlebar-pill\)", Body(css, ".studio-titlebar-nav"));
    }

    [Fact]
    public void The_left_cluster_is_never_clipped_because_its_menus_open_out_of_it()
    {
        // The obvious way to stop the cluster spilling is overflow: hidden, and it would silently cut off the
        // workspace and branch dropdown panels, which are children of this row drawn below it.
        Assert.DoesNotMatch(@"overflow(-x)?\s*:\s*(hidden|clip)", Body(Stylesheet(), ".studio-titlebar-nav"));
    }

    [Fact]
    public void A_title_that_does_not_fit_is_cut_with_an_ellipsis_and_not_mid_glyph()
    {
        var span = Body(Stylesheet(), ".studio-titlebar-search > span");

        Assert.Matches(@"text-overflow\s*:\s*ellipsis", span);
        Assert.Matches(@"white-space\s*:\s*nowrap", span);
    }
}
