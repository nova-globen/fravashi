using AwesomeAssertions;
using Fravashi.Domain.Wiki;
using Xunit;

namespace Fravashi.Domain.Tests.Wiki;

/// <summary>
/// Backlog 83's second, quieter problem: nothing defined the conversion between the markdown this product
/// stores and the format a provider keeps pages in, what it loses, or whether a round trip is stable. The
/// acceptance that matters most here is the last clause: anything the conversion cannot represent is
/// REPORTED rather than silently dropped.
/// </summary>
public class WikiFormatConversionTests
{
    // ---- what it carries ------------------------------------------------------------------------

    [Fact]
    public void A_heading_a_paragraph_and_a_list_survive_a_round_trip()
    {
        const string markdown = "# The title\n\nSome prose.\n\n- one\n- two\n";

        var storage = WikiFormatConversion.ToStorage(markdown);
        var back = WikiFormatConversion.ToMarkdown(storage.Text);

        storage.Lossless.Should().BeTrue(string.Join("; ", storage.Losses.Select(l => l.What)));
        back.Lossless.Should().BeTrue(string.Join("; ", back.Losses.Select(l => l.What)));
        back.Text.Should().Contain("# The title").And.Contain("Some prose.")
            .And.Contain("- one").And.Contain("- two");
    }

    [Fact]
    public void A_page_saved_by_the_sites_own_editor_keeps_its_structure()
    {
        // Seen live on 2026-09-21 (backlog 83): a page this product had published came back, after a person
        // edited it in Confluence, with every ordinary element reported as "markdown has no equal" and the
        // document flattened into one run of text. The site's editor writes ATTRIBUTES on ordinary tags, and
        // every pattern here knew only the bare tag. The attribute is the site's bookkeeping, not content.
        const string storage =
            "<h2 local-id=\"a1\">Summary</h2><p local-id=\"a2\">Some <strong data-x=\"1\">bold</strong> prose.</p>"
            + "<ul local-id=\"a3\"><li local-id=\"a4\"><p local-id=\"a5\">one</p></li><li local-id=\"a6\">two</li></ul>"
            + "<h2 local-id=\"a7\">Test Edit</h2><p local-id=\"a8\" />";

        var result = WikiFormatConversion.ToMarkdown(storage);

        result.Lossless.Should().BeTrue(string.Join("; ", result.Losses.Select(l => l.Detail)));
        result.Text.Should().Contain("## Summary\n").And.Contain("Some **bold** prose.\n")
            .And.Contain("- one\n").And.Contain("- two\n").And.Contain("## Test Edit");
    }

    [Fact]
    public void A_tag_that_only_starts_like_a_known_one_is_still_reported()
    {
        // Allowing attributes must not turn "<param>" into a paragraph or "<link>" into a list item.
        var result = WikiFormatConversion.ToMarkdown("<p>kept</p><param name=\"x\">inside</param>");

        result.Losses.Should().ContainSingle(l => l.Detail.Contains("'<param>'"));
        result.Text.Should().Contain("kept");
    }

    [Fact]
    public void Emphasis_code_and_links_survive_a_round_trip()
    {
        const string markdown = "This is **bold**, this is *italic*, this is `code`, and [a link](/x).\n";

        var back = WikiFormatConversion.ToMarkdown(WikiFormatConversion.ToStorage(markdown).Text);

        back.Text.Should().Contain("**bold**").And.Contain("*italic*")
            .And.Contain("`code`").And.Contain("[a link](/x)");
    }

    [Fact]
    public void A_code_block_survives_with_its_contents_intact()
    {
        const string markdown = "Before.\n\n```\nvar x = 1 < 2 && 3 > 2;\n```\n\nAfter.\n";

        var back = WikiFormatConversion.ToMarkdown(WikiFormatConversion.ToStorage(markdown).Text);

        // The angle brackets and ampersands inside a code block are the thing most likely to be mangled.
        back.Text.Should().Contain("var x = 1 < 2 && 3 > 2;");
    }

    [Fact]
    public void Markup_characters_in_prose_are_escaped_and_come_back_as_themselves()
    {
        const string markdown = "A < B && C > D, and 5 & 6.\n";

        var storage = WikiFormatConversion.ToStorage(markdown);
        storage.Text.Should().Contain("&lt;").And.Contain("&amp;");

        WikiFormatConversion.ToMarkdown(storage.Text).Text.Should().Contain("A < B && C > D, and 5 & 6.");
    }

    // ---- what it reports -------------------------------------------------------------------------

    [Fact]
    public void A_macro_is_reported_by_name_rather_than_quietly_flattened()
    {
        // A converter that turned a macro into the text inside it would produce a document that looked
        // right and had lost the thing that made it work.
        const string storage =
            "<p>Before</p><ac:structured-macro ac:name=\"jira\"><ac:parameter>KEY-1</ac:parameter>"
            + "</ac:structured-macro><p>After</p>";

        var result = WikiFormatConversion.ToMarkdown(storage);

        result.Lossless.Should().BeFalse();
        result.Losses.Should().Contain(l => l.What == "macro" && l.Detail.Contains("jira"));
    }

    [Fact]
    public void An_attachment_is_reported_because_the_file_does_not_travel_with_the_text()
    {
        const string storage = "<p>See <ri:attachment ri:filename=\"diagram.png\" /></p>";

        var result = WikiFormatConversion.ToMarkdown(storage);

        result.Losses.Should().Contain(l => l.What == "attachment" && l.Detail.Contains("diagram.png"));
    }

    [Fact]
    public void A_column_layout_is_reported_because_markdown_cannot_say_it()
    {
        var result = WikiFormatConversion.ToMarkdown("<ac:layout><ac:layout-section><p>x</p></ac:layout-section></ac:layout>");

        result.Losses.Should().Contain(l => l.What == "layout");
    }

    [Fact]
    public void Markup_nobody_taught_this_converter_is_reported_once_per_kind()
    {
        // Forty list tags do not need forty lines about them.
        const string storage = "<div><span>a</span></div><div><span>b</span></div>";

        var result = WikiFormatConversion.ToMarkdown(storage);

        result.Losses.Count(l => l.What == "markup" && l.Detail.Contains("<span>")).Should().Be(1);
        result.Losses.Should().Contain(l => l.Detail.Contains("<div>"));
    }

    [Fact]
    public void A_code_blocks_language_is_reported_because_it_is_not_carried()
    {
        var result = WikiFormatConversion.ToStorage("```csharp\nvar x = 1;\n```\n");

        result.Losses.Should().Contain(l => l.What == "code language" && l.Detail.Contains("csharp"));
    }

    [Fact]
    public void A_table_becomes_a_table_and_comes_back_as_one()
    {
        // Seen on the first page of this repository's imported documents published to Confluence: every row of a
        // table arrived as its own paragraph of pipes.
        const string markdown = "| File | Subject |\n|---|---|\n| [`a.md`](a.md) | The **first** one |\n| b | a \\| pipe |\n";

        var storage = WikiFormatConversion.ToStorage(markdown);
        var back = WikiFormatConversion.ToMarkdown(storage.Text);

        storage.Lossless.Should().BeTrue(string.Join("; ", storage.Losses.Select(l => l.Detail)));
        storage.Text.Should().StartWith("<table><tbody><tr><th>File</th><th>Subject</th></tr>")
            .And.Contain("<td><a href=\"a.md\"><code>a.md</code></a></td>").And.Contain("<td>a | pipe</td>");
        back.Lossless.Should().BeTrue(string.Join("; ", back.Losses.Select(l => l.Detail)));
        back.Text.Should().Contain("| File | Subject |\n|---|---|\n").And.Contain("| The **first** one |")
            .And.Contain("a \\| pipe");
    }

    [Fact]
    public void A_wrapped_paragraph_is_one_paragraph()
    {
        // The source wraps at 120 columns; the page must not break where the source file did.
        var storage = WikiFormatConversion.ToStorage("One sentence that\nwraps onto a second line.\n\nAnother.\n");

        storage.Text.Should().Be("<p>One sentence that wraps onto a second line.</p><p>Another.</p>");
    }

    [Fact]
    public void Numbered_nested_and_wrapped_list_items_keep_their_shape()
    {
        const string markdown = "1. First\n   continues here\n2. Second\n   - inner\n   - inner two\n3. Third\n\nAfter.\n";

        var storage = WikiFormatConversion.ToStorage(markdown);

        storage.Text.Should().Be(
            "<ol><li>First continues here</li><li>Second<ul><li>inner</li><li>inner two</li></ul></li><li>Third</li></ol>"
            + "<p>After.</p>");
        WikiFormatConversion.ToMarkdown("<ol><li>one</li><li>two</li></ol>").Text.Should().Contain("1. one\n1. two");
    }

    [Fact]
    public void A_quote_and_a_rule_are_carried_both_ways()
    {
        var storage = WikiFormatConversion.ToStorage("> Somebody said\n> this.\n\n---\n\nAfter.\n");
        var back = WikiFormatConversion.ToMarkdown(storage.Text);

        storage.Lossless.Should().BeTrue();
        storage.Text.Should().Be("<blockquote><p>Somebody said this.</p></blockquote><hr /><p>After.</p>");
        back.Text.Should().Contain("> Somebody said this.").And.Contain("---").And.Contain("After.");
    }

    [Fact]
    public void Emphasis_inside_a_code_span_stays_literal()
    {
        WikiFormatConversion.ToStorage("Run `a*b*c` now.\n").Text.Should().Be("<p>Run <code>a*b*c</code> now.</p>");
    }

    [Fact]
    public void A_code_block_carrying_a_cdata_terminator_stays_well_formed()
    {
        var storage = WikiFormatConversion.ToStorage("```\nx]]>y\n```\n");

        storage.Text.Should().Contain("x]]]]><![CDATA[>y");
    }

    [Fact]
    public void An_unclosed_code_block_is_reported_rather_than_producing_a_page_that_looks_fine()
    {
        var result = WikiFormatConversion.ToStorage("```\nvar x = 1;\n");

        result.Losses.Should().Contain(l => l.What == "code block");
    }

    // ---- what it refuses to carry ----------------------------------------------------------------
    //
    // A document is authored by anybody and can be synced in from a provider, so its text is untrusted:
    // the markdown goes to the user's own wiki page and the storage format comes back into the studio's
    // own rendered page. Neither direction may carry something that runs.

    [Fact]
    public void A_link_destination_cannot_break_out_of_the_attribute_it_sits_in()
    {
        var result = WikiFormatConversion.ToStorage("[x](\" onmouseover=\"alert(1))\n");

        result.Text.Should().NotContain("onmouseover=\"alert",
            "a quote in a destination closed the attribute and opened an event handler");
        result.Text.Should().Contain("&quot;");
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("vbscript:msgbox(1)")]
    public void A_scheme_that_runs_something_is_written_as_text_and_reported(string href)
    {
        var result = WikiFormatConversion.ToStorage($"See [this]({href}).\n");

        result.Text.Should().NotContain("<a href", "a link that runs code is not a link");
        result.Losses.Should().Contain(l => l.What == "unsafe link");
        result.Text.Should().Contain("this", "the words stay so the reader can see what was written");
    }

    [Theory]
    [InlineData("https://example.com/x")]
    [InlineData("http://example.com/x")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("/docs/plan")]
    [InlineData("../sibling.md")]
    [InlineData("#a-section")]
    [InlineData("a/b:c")]
    public void An_ordinary_destination_is_still_a_link(string href)
    {
        var result = WikiFormatConversion.ToStorage($"See [this]({href}).\n");

        result.Text.Should().Contain("<a href=");
        result.Losses.Should().NotContain(l => l.What == "unsafe link");
    }

    [Fact]
    public void A_page_that_carries_a_running_link_does_not_bring_it_into_the_studio()
    {
        // The markdown this produces is rendered in the studio's own page.
        var result = WikiFormatConversion.ToMarkdown(
            "<p>See <a href=\"javascript:alert(1)\">this</a></p>");

        result.Text.Should().NotContain("](javascript:");
        result.Losses.Should().Contain(l => l.What == "unsafe link");
        result.Text.Should().Contain("this");
    }

    [Fact]
    public void An_ordinary_link_still_comes_out_of_a_page_as_a_link()
    {
        var result = WikiFormatConversion.ToMarkdown("<p>See <a href=\"/docs/x\">this</a></p>");

        result.Text.Should().Contain("[this](/docs/x)");
        result.Losses.Should().NotContain(l => l.What == "unsafe link");
    }

    [Fact]
    public void An_empty_document_converts_to_an_empty_one_and_loses_nothing()
    {
        WikiFormatConversion.ToStorage(string.Empty).Lossless.Should().BeTrue();
        WikiFormatConversion.ToMarkdown(string.Empty).Lossless.Should().BeTrue();
        WikiFormatConversion.ToMarkdown(null).Text.Trim().Should().BeEmpty();
    }

    [Fact]
    public void A_document_that_converts_cleanly_says_so_and_that_is_what_a_caller_acts_on()
    {
        // Lossless is the only signal a caller may treat as a clean round trip, so it has to be exact.
        WikiFormatConversion.ToStorage("# Title\n\nProse.\n").Lossless.Should().BeTrue();
        WikiFormatConversion.ToStorage("```csharp\nx\n```\n").Lossless.Should().BeFalse();
    }
}
