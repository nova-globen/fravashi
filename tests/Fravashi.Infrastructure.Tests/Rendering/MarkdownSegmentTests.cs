using Fravashi.Infrastructure.Rendering;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Rendering;

/// <summary>
/// Backlog 110. The transcript's copy controls hand over the SOURCE, so the source has to survive the
/// trip from markdown to screen. These pin what the splitter keeps: a fenced block's content without its
/// fences, the prose either side of it, and the streaming case where the closing fence has not arrived.
/// </summary>
public class MarkdownSegmentTests
{
    private readonly MarkdownPreviewRenderer _renderer = new();

    [Fact]
    public void A_fenced_block_becomes_a_code_segment_whose_source_has_no_fence_markers()
    {
        const string markdown = """
            Here is the fix:

            ```csharp
            var x = 1;
            if (x > 0) { Run(); }
            ```

            That is all.
            """;

        var segments = _renderer.ToSegments(markdown);

        var code = Assert.Single(segments, s => s.IsCode);
        Assert.Equal("var x = 1;\nif (x > 0) { Run(); }", code.Source);
        Assert.Equal("csharp", code.Language);
        Assert.DoesNotContain("`", code.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prose_on_either_side_stays_prose_and_is_rendered()
    {
        const string markdown = """
            Before.

            ```
            code
            ```

            After.
            """;

        var segments = _renderer.ToSegments(markdown);

        Assert.Equal(3, segments.Count);
        Assert.False(segments[0].IsCode);
        Assert.True(segments[1].IsCode);
        Assert.False(segments[2].IsCode);
        Assert.Contains("<p>Before.</p>", segments[0].Html, StringComparison.Ordinal);
        Assert.Contains("<p>After.</p>", segments[2].Html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_message_with_no_code_at_all_is_one_prose_segment()
    {
        var segments = _renderer.ToSegments("just words, and `inline code` is not a block");

        var only = Assert.Single(segments);
        Assert.False(only.IsCode);
        Assert.Contains("<code>inline code</code>", only.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fence_that_has_not_closed_yet_is_still_a_code_segment()
    {
        // The normal case while a turn streams: the transcript renders the partial message on every
        // token, and for most of a code block's life the closing fence has not arrived.
        //
        // The whole list is asserted, not just the code segment. Assert.Single(segments, predicate)
        // constrains only what it matches, so the version of this test that pinned the code segment alone
        // stayed green while the same input ALSO produced a spurious prose segment carrying the info
        // string and the code (see the two tests below).
        var segments = _renderer.ToSegments("Working on it:\n\n```js\nconst a = 1;");

        Assert.Equal(2, segments.Count);
        Assert.False(segments[0].IsCode);
        Assert.Equal("Working on it:\n\n", segments[0].Source);
        Assert.True(segments[1].IsCode);
        Assert.Equal("const a = 1;", segments[1].Source);
        Assert.Equal("js", segments[1].Language);
    }

    [Fact]
    public void An_open_fence_with_nothing_in_it_yet_leaves_no_part_of_its_language_behind()
    {
        // Every streamed block passes through this state. Markdig's span for an open fence covers the
        // backticks and stops, so slicing the prose from its end handed back the tail of the info string
        // and the transcript RENDERED it: "```python\n" put a paragraph reading "ython" under an empty
        // block.
        var segments = _renderer.ToSegments("Working:\n\n```python\n");

        Assert.Equal(2, segments.Count);
        Assert.False(segments[0].IsCode);
        Assert.Contains("<p>Working:</p>", segments[0].Html, StringComparison.Ordinal);
        Assert.True(segments[1].IsCode);
        Assert.Equal(string.Empty, segments[1].Source);
        Assert.Equal("python", segments[1].Language);
    }

    [Theory]
    [InlineData("Here:\n\n```javascript\nconst a = 1;\n```\n\nDone.")]
    [InlineData("Here:\r\n\r\n```python\r\nx = 1\r\n```\r\n\r\nDone.")]
    [InlineData("Here:\n\n    indented();\n    more();\n\nDone.")]
    public void No_prefix_of_a_streamed_block_hands_the_fence_or_the_code_back_as_prose(string full)
    {
        // A block arrives one character at a time, so the split has to hold at every length, not only at
        // the finished message. The prose in these samples is "Here:" and "Done." and nothing else: any
        // other prose segment is a piece of the block that escaped into the rendering.
        for (var length = 1; length <= full.Length; length++)
        {
            var segments = _renderer.ToSegments(full[..length]);
            foreach (var prose in segments.Where(s => !s.IsCode))
            {
                // A trailing backtick or two is not yet a fence and is genuinely still prose at that
                // moment (one backtick opens inline code), so it is allowed here; three is a block.
                var shown = prose.Source.Trim().TrimEnd('`').Trim();
                Assert.True(
                    "Here:".StartsWith(shown, StringComparison.Ordinal)
                    || "Done.".StartsWith(shown, StringComparison.Ordinal),
                    $"prefix of length {length} rendered '{shown}' as prose");
            }
        }
    }

    [Fact]
    public void An_indented_block_keeps_its_code_and_loses_the_indent_markdown_used_to_find_it()
    {
        var segments = _renderer.ToSegments("Text.\n\n    indented();\n    more();\n");

        var code = Assert.Single(segments, s => s.IsCode);
        Assert.Equal("indented();\nmore();", code.Source);
        Assert.Null(code.Language);
    }

    [Fact]
    public void Nothing_at_all_yields_no_segments()
    {
        Assert.Empty(_renderer.ToSegments(null));
        Assert.Empty(_renderer.ToSegments(string.Empty));
    }

    // ---- blocks the split cannot cut out (backlog 110) ---------------------------------------

    [Fact]
    public void A_block_inside_a_list_keeps_the_list_whole_and_still_carries_a_copy_control()
    {
        // An agent putting a command inside a numbered list is an everyday shape, and cutting the source
        // there would hand the two halves of the list to the parser separately. So the list is rendered
        // whole and the control is written into it, with the block's SOURCE on it rather than anything
        // scraped back out of the rendering.
        var segments = _renderer.ToSegments("Steps:\n\n1. run this:\n\n   ```sh\n   make test\n   ```\n\n2. done");

        var only = Assert.Single(segments);
        Assert.False(only.IsCode);

        // One list, both items, the second still numbered by the list rather than restarting.
        Assert.Contains("<ol>", only.Html, StringComparison.Ordinal);
        Assert.Equal(2, only.Html.Split("<li>").Length - 1);

        Assert.Contains("data-testid=\"copy-code\"", only.Html, StringComparison.Ordinal);
        Assert.Contains("data-copy-source=\"make test\"", only.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_block_inside_a_blockquote_carries_one_too()
    {
        var segments = _renderer.ToSegments("> see:\n>\n> ```sh\n> ls\n> ```\n");

        var only = Assert.Single(segments);
        Assert.Contains("<blockquote>", only.Html, StringComparison.Ordinal);
        Assert.Contains("data-copy-source=\"ls\"", only.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_source_on_a_nested_control_is_escaped_so_markup_in_the_code_cannot_break_out()
    {
        var segments = _renderer.ToSegments("1. this:\n\n   ```html\n   <b class=\"x\">&</b>\n   ```");

        var only = Assert.Single(segments);
        Assert.Contains("data-copy-source=\"&lt;b class=&quot;x&quot;&gt;&amp;&lt;/b&gt;\"", only.Html,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_code_pages_markdown_preview_keeps_the_plain_rendering()
    {
        // ToHtml is the Code page's preview pane, which is not a conversation: the transcript's copy
        // markup must not follow the rendering out of the sessions surface.
        var html = _renderer.ToHtml("```sh\nmake test\n```");

        Assert.Contains("<pre><code class=\"language-sh\">", html, StringComparison.Ordinal);
        Assert.DoesNotContain("session-copy", html, StringComparison.Ordinal);
    }
}
