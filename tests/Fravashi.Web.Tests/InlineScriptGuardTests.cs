using System.Text.RegularExpressions;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 287: the studio's Content-Security-Policy runs script files from 'self' only, so an inline event handler
/// attribute (<c>onclick="..."</c>, as opposed to Blazor's <c>@onclick</c>) or an inline script element in the
/// studio's own markup would silently do nothing in the window. This reads every .razor file under <c>src</c> so a new
/// one is red here rather than dead in the window: name an action in a data attribute and listen for it in a script file
/// (windowShell.js does it for the title bar), and put script in a file under wwwroot.
/// </summary>
public sealed partial class InlineScriptGuardTests
{
    // An attribute named on... that is not a Blazor directive (@onclick) or part of a longer name (data-on..., version).
    // Case-sensitive on purpose: a component parameter is PascalCase (OnClick="Save", OnContextMenu="..."), and an
    // HTML handler is written in lower case.
    [GeneratedRegex("""(?<![@\w-])on[a-z]+\s*=\s*["']""")]
    private static partial Regex InlineHandler();

    // A script element with no src attribute.
    [GeneratedRegex("""<script(?![^>]*\bsrc\s*=)[^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex InlineScript();

    [Fact]
    public void No_razor_file_carries_an_inline_event_handler_attribute() =>
        Assert.Empty(Offences(InlineHandler()));

    [Fact]
    public void No_razor_file_carries_an_inline_script_element() =>
        Assert.Empty(Offences(InlineScript()));

    [Fact]
    public void The_guard_sees_a_handler_and_an_inline_script_and_passes_a_directive()
    {
        Assert.Matches(InlineHandler(), """<button onclick="history.back()">""");
        Assert.Matches(InlineHandler(), """<img src="x" onerror='x()' />""");
        Assert.Matches(InlineHandler(), """<input type="checkbox" onclick = "return false" />""");
        Assert.DoesNotMatch(InlineHandler(), """<button @onclick="Back">""");
        Assert.DoesNotMatch(InlineHandler(), """<FluentButton OnClick="SaveAsync">""");
        Assert.DoesNotMatch(InlineHandler(), """<button data-window-action="back" version="1">""");
        Assert.Matches(InlineScript(), "<script>alert(1)</script>");
        Assert.DoesNotMatch(InlineScript(), """<script src="bootstrap.js"></script>""");
    }

    private static List<string> Offences(Regex pattern)
    {
        var src = Path.Combine(StudioContentSecurityPolicyTests.RepositoryRoot(), "src");
        var offences = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.razor", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(src, file);
            var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Contains("bin") || segments.Contains("obj"))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (pattern.IsMatch(lines[i]))
                {
                    offences.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        return offences;
    }
}
