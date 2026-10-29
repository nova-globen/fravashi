using System.Text.RegularExpressions;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 17's second half: a reader who scrolled up to read history keeps their place. The transcript
/// changes size ABOVE them all the time (a ToolCallCard is rendered from ToolCallStarted and grows when
/// its ToolCallCompleted brings Success and Output), and browser scroll anchoring is the only mechanism
/// that compensates for that. A single `overflow-anchor: none` on the scroller silently turns it off, and
/// every tool completion then slides the paragraph they were reading down the pane.
///
/// These read the shipped assets rather than a rendered component on purpose: the behaviour is a layout
/// effect of the stylesheet, and bunit has no layout at all. They are a guard against reintroducing the
/// declaration, not a proof that anchoring works. That proof needs a real engine and was taken by hand in
/// Chromium against this stylesheet and sessionScroll.js (numbers in the studio.css comment).
/// </summary>
public class TranscriptScrollAnchoringTests
{
    /// <summary>The scroller and its wrapper. Excluding either one suppresses anchoring for the scroller.</summary>
    private const string TranscriptSelector = "session-transcript";

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
        return File.ReadAllText(path);
    }

    [Fact]
    public void The_transcript_leaves_browser_scroll_anchoring_on_so_a_reader_keeps_their_place()
    {
        var css = Regex.Replace(Asset("studio.css"), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

        // Selector + body pairs. A body never contains braces, so an @media wrapper simply drops out and
        // the rules inside it are matched on their own.
        var offenders = Regex.Matches(css, @"(?<selector>[^{}]+)\{(?<body>[^{}]*)\}")
            .Where(m => m.Groups["selector"].Value.Contains(TranscriptSelector, StringComparison.Ordinal))
            .Where(m => Regex.IsMatch(m.Groups["body"].Value, @"overflow-anchor\s*:\s*none"))
            .Select(m => m.Groups["selector"].Value.Trim())
            .ToList();

        // Scroll anchoring is what keeps the reader's position when a tool card above the viewport grows
        // its output, and it costs the pin nothing: while the reader is stuck to the bottom,
        // sessionScroll.js writes scrollTop = scrollHeight on every content change anyway.
        Assert.True(
            offenders.Count == 0,
            "no transcript rule may disable scroll anchoring, found: " + string.Join(" | ", offenders));
    }

    [Fact]
    public void The_scroll_script_does_not_turn_anchoring_off_from_javascript_either()
    {
        // The same defect one file over: setting el.style.overflowAnchor (or the attribute) from the
        // script would reintroduce it while leaving the stylesheet looking correct.
        var js = Asset("sessionScroll.js");

        Assert.DoesNotContain("overflowAnchor", js, StringComparison.Ordinal);
        Assert.DoesNotContain("overflow-anchor", js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_pin_that_makes_anchoring_unnecessary_at_the_bottom_is_still_there()
    {
        // The reason removing the declaration is safe: the script overrides any anchoring adjustment
        // while the reader is stuck, by writing the scroll position itself on every content change. If
        // that ever stops being how the pin works, the trade-off above has to be measured again.
        var js = Asset("sessionScroll.js");

        Assert.Contains("state.el.scrollTop = state.el.scrollHeight", js, StringComparison.Ordinal);
        Assert.Contains("new MutationObserver", js, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_readers_own_input_can_unstick_them_from_the_newest_content()
    {
        // A scroll event says the position changed, not who changed it. Measured in the real studio window on
        // the installed 0.9.24, on a live agent turn, two things moved it with nobody touching anything and
        // each one stopped the pin for the rest of the turn behind a Jump to latest nobody had asked for:
        // the approval prompt (a SIBLING of the transcript, so outside the subtree the MutationObserver
        // watches) took 168px of viewport and left the scroller exactly 168px from the bottom against a 40px
        // tolerance, and the 20px streaming draft being replaced by a 1292px final message left it 1884px
        // away at the end of the turn. A first fix that special-cased the viewport height was proved green
        // and then missed the second case on the very next live turn, which is why the rule is about INTENT:
        // a scroll unsticks the reader only while they are wheeling, touching, pressing a key or holding a
        // pointer on the scroller. With that in place both triggers stayed pinned at 0px (twice each), a real
        // wheel up still unstuck the reader, a sibling appearing while they were away did not drag them down,
        // and wheeling back to the bottom re-stuck them. Like the rest of this file, this guards that the
        // mechanism is present; the proof was the real engine, because bunit has no layout at all.
        var js = Asset("sessionScroll.js");

        Assert.Contains("new ResizeObserver", js, StringComparison.Ordinal);
        foreach (var input in new[] { "'wheel'", "'touchmove'", "'keydown'", "'pointerdown'" })
        {
            Assert.Contains(input, js, StringComparison.Ordinal);
        }

        // The scroll handler must consult intent BEFORE it is allowed to conclude the reader left.
        Assert.Matches(@"if\s*\(state\.stick\s*&&\s*!readerIsScrolling\(\)\)\s*\{\s*hold\(\);\s*return;", js);
    }

    [Fact]
    public void A_hidden_window_is_pinned_without_waiting_for_an_animation_frame_that_never_comes()
    {
        // Measured in the installed window, 2026-09-21 (backlog 17). A hidden page gets no animation frames,
        // and a turn is normally left to run with the studio behind other windows: the frame pin() asked for
        // never came, and the transcript sat at the top while 940px of answer streamed in under it (gap 157,
        // 306, 394, 534). With the direct write below loaded over the installed script the gap held at 0 for a
        // whole turn, window still hidden. This guard only holds the SHAPE: inside pin(), the hidden branch
        // writes the position and returns BEFORE any frame is asked for.
        var js = Asset("sessionScroll.js");
        var pin = js[js.IndexOf("function pin(state)", StringComparison.Ordinal)..];
        pin = pin[..pin.IndexOf("return {", StringComparison.Ordinal)];

        var hidden = pin.IndexOf("document.hidden", StringComparison.Ordinal);
        var frame = pin.IndexOf("requestAnimationFrame", StringComparison.Ordinal);

        Assert.True(hidden >= 0, "pin() has to ask whether the page is hidden");
        Assert.True(frame > hidden, "the hidden branch has to come before the frame is asked for");
        Assert.Contains("state.el.scrollTop = state.el.scrollHeight;", pin[hidden..frame], StringComparison.Ordinal);
    }
}
