using System.Net;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Fravashi.Application.Mockups;
using Fravashi.Application.Prototypes;
using Fravashi.Application.Verification;
using Fravashi.Domain.Wiki;
using Xunit;

namespace Fravashi.Application.Tests.Prototypes;

/// <summary>
/// Backlog 238's acceptance (plan @52): a node's PARTS (a screen's or dialog's title, a caption, a tab, a column header)
/// are addressed by an anchor's <c>part</c>, so an edit on one changes that part and nothing else, an edit on the node
/// no longer reaches into them, and the overlays written before (no part) behave as they did.
/// </summary>
public class PrototypePartTests
{
    private const string Directory = """
        {
          "mockup": 1,
          "title": "Directory",
          "screens": [
            {"type": "window", "id": "directory", "text": "Directory", "children": [
              {"type": "table", "id": "people", "text": "People",
               "columns": [{"key": "name", "text": "Name"}, {"key": "email", "text": "Email"}],
               "items": [{"name": "Ada", "email": "ada@example.com"}]},
              {"type": "list", "id": "names", "text": "Names", "items": ["Ada"]},
              {"type": "tabs", "id": "pages", "children": [
                {"type": "panel", "id": "one", "text": "One"},
                {"type": "panel", "id": "two", "text": "Two"}
              ]},
              {"type": "button", "id": "add", "text": "Add", "on": {"click": {"open": "invite"}}},
              {"type": "dialog", "id": "invite", "text": "Invite someone", "children": [
                {"type": "text", "id": "hint", "text": "Who should join?"}
              ]}
            ]},
            {"type": "window", "id": "blank", "children": [{"type": "text", "id": "lonely", "text": "Nothing here"}]}
          ]
        }
        """;

    /// <summary>The same design one version later: the dialog is gone.</summary>
    private const string WithoutTheDialog = """
        {
          "mockup": 1,
          "title": "Directory",
          "screens": [
            {"type": "window", "id": "directory", "text": "Directory", "children": [
              {"type": "button", "id": "add", "text": "Add"}
            ]},
            {"type": "window", "id": "settings", "text": "Settings"}
          ]
        }
        """;

    private static readonly PrototypeAnchor DirectoryTitle = new("directory", null, DesignPart.Title);
    private static readonly PrototypeAnchor InviteTitle = new("invite", null, DesignPart.Title);
    private static readonly PrototypeAnchor EmailHeader = new("people", null, DesignPart.ColumnHeader, "email");

    private static MockupDocument Valid(string json)
    {
        var read = MockupReader.Read(json);
        read.IsValid.Should().BeTrue(string.Join("\n", read.Errors));
        return read.Document!;
    }

    private static string Html(params PrototypeOperation[] overlay)
    {
        var result = PrototypeRenderer.Render(Valid(Directory), overlay);
        result.Unplaced.Should().BeEmpty();
        return result.Html;
    }

    private static PrototypeSetCssOperation Css(PrototypeAnchor anchor, string property, string value) =>
        new(anchor, new Dictionary<string, string>(StringComparer.Ordinal) { [property] = value });

    private static string Body(string html) => html[html.IndexOf("<body>", StringComparison.Ordinal)..];

    /// <summary>The opening tag of a node's own element.</summary>
    private static string NodeTag(string html, string key) => TagAt(html, $" data-mk-anchor=\"{WebUtility.HtmlEncode(key)}\"");

    /// <summary>The opening tag of a node's part.</summary>
    private static string PartTag(string html, string key, string part, string? column = null) =>
        TagAt(html, $" data-mk-part-of=\"{WebUtility.HtmlEncode(key)}\" data-mk-part=\"{part}\""
            + (column is null ? string.Empty : $"[^>]* data-mk-col=\"{WebUtility.HtmlEncode(column)}\""));

    private static string TagAt(string html, string pattern)
    {
        var body = Body(html);
        var match = Regex.Match(body, $"<[a-z0-9]+{pattern}[^>]*>");
        match.Success.Should().BeTrue($"the page should carry an element matching {pattern}");
        return match.Value;
    }

    /// <summary>The words straight after an opening tag, up to the next tag.</summary>
    private static string Words(string html, string tag)
    {
        var at = html.IndexOf(tag, StringComparison.Ordinal) + tag.Length;
        return WebUtility.HtmlDecode(html[at..html.IndexOf('<', at)]);
    }

    private static string? Style(string tag) =>
        Regex.Match(tag, "\\sstyle=\"([^\"]*)\"") is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;

    // ---- setText and setCss on a part change only the part --------------------------------------------------

    [Fact]
    public void A_style_on_a_screens_title_lands_on_the_title_and_nowhere_else()
    {
        var html = Html(Css(DirectoryTitle, "font-size", "1.6rem"));

        Style(PartTag(html, "directory", "title")).Should().Be("font-size: 1.6rem");
        Style(NodeTag(html, "directory")).Should().BeNull("the screen itself is not what was asked for");
        Regex.Matches(Body(html), "font-size").Should().ContainSingle("only the heading carries the size");
        PartTag(html, "directory", "title").Should().StartWith("<h1 ");
    }

    [Fact]
    public void Words_on_a_screens_title_change_the_heading_only()
    {
        var html = Html(new PrototypeSetTextOperation(DirectoryTitle, "Our people"));

        Words(html, PartTag(html, "directory", "title")).Should().Be("Our people");
        NodeTag(html, "directory").Should().Contain("aria-label=\"Directory\"", "the screen's own name is the design's");
        Words(html, PartTag(html, "people", "caption")).Should().Be("People");
    }

    [Fact]
    public void Words_and_a_style_on_a_dialogs_title_change_only_that_title()
    {
        var html = Html(new PrototypeSetTextOperation(InviteTitle, "Add a colleague"), Css(InviteTitle, "color", "#b00"));

        var title = PartTag(html, "invite", "title");
        title.Should().StartWith("<h2 ");
        Words(html, title).Should().Be("Add a colleague");
        Style(title).Should().Be("color: #b00");
        Style(NodeTag(html, "invite")).Should().BeNull();
        Style(NodeTag(html, "hint")).Should().BeNull();
        Words(html, NodeTag(html, "hint")).Should().Be("Who should join?");
        Words(html, PartTag(html, "directory", "title")).Should().Be("Directory");
    }

    [Fact]
    public void Words_and_a_style_on_one_column_header_change_only_that_header()
    {
        var html = Html(new PrototypeSetTextOperation(EmailHeader, "E-mail"), Css(EmailHeader, "color", "red"));

        var email = PartTag(html, "people", "columnHeader", "email");
        var name = PartTag(html, "people", "columnHeader", "name");
        Words(html, email).Should().Be("E-mail");
        Style(email).Should().Be("color: red");
        Words(html, name).Should().Be("Name");
        Style(name).Should().BeNull();
        Style(NodeTag(html, "people")).Should().BeNull();
        Regex.Matches(Body(html), "<td[^>]*style=").Should().BeEmpty("a cell is not its column's header");
        Words(html, PartTag(html, "people", "caption")).Should().Be("People");
    }

    [Fact]
    public void A_tab_and_a_caption_are_parts_too()
    {
        var tab = new PrototypeAnchor("two", null, DesignPart.Tab);
        var caption = new PrototypeAnchor("names", null, DesignPart.Caption);
        var html = Html(new PrototypeSetTextOperation(tab, "Second"), Css(caption, "font-weight", "700"));

        var button = PartTag(html, "two", "tab");
        Words(html, button).Should().Be("Second");
        button.Should().Contain("data-mk-tab=\"1\"").And.Contain("role=\"tab\"");
        NodeTag(html, "two").Should().Contain("aria-label=\"Two\"", "the panel itself keeps the design's name");
        Style(PartTag(html, "names", "caption")).Should().Be("font-weight: 700");
        Style(NodeTag(html, "names")).Should().BeNull();
    }

    // ---- what a part-less overlay does is what it did ------------------------------------------------------

    [Fact]
    public void An_edit_on_the_node_stays_on_the_node_and_its_words_still_name_its_title()
    {
        var screen = new PrototypeAnchor("directory", null);
        var html = Html(Css(screen, "font-size", "1.6rem"), new PrototypeSetTextOperation(screen, "People"));

        Style(NodeTag(html, "directory")).Should().Be("font-size: 1.6rem");
        Style(PartTag(html, "directory", "title")).Should().BeNull("an edit on the node no longer reaches into its parts");
        Words(html, PartTag(html, "directory", "title")).Should().Be("People", "the screen's text IS its title, as before");
    }

    [Fact]
    public void A_setText_on_the_title_wins_over_one_on_its_node()
    {
        var html = Html(
            new PrototypeSetTextOperation(DirectoryTitle, "From the title"),
            new PrototypeSetTextOperation(new PrototypeAnchor("directory", null), "From the screen"));

        Words(html, PartTag(html, "directory", "title")).Should().Be("From the title");
    }

    [Fact]
    public void The_page_stays_deterministic_with_parts_in_the_overlay()
    {
        var overlay = new PrototypeOperation[] { Css(DirectoryTitle, "font-size", "2rem"), new PrototypeSetTextOperation(EmailHeader, "Mail") };

        Html(overlay).Should().Be(Html(overlay));
    }

    // ---- what the render lists, and what it cannot place ---------------------------------------------------

    [Fact]
    public void A_render_lists_every_part_it_drew_and_only_those()
    {
        var result = PrototypeRenderer.Render(Valid(Directory), [new PrototypeSetTextOperation(EmailHeader, "Mail")]);

        result.Find("directory", DesignPart.Title)!.Anchor.Should().Be(DirectoryTitle);
        result.Find("directory", DesignPart.Title)!.Type.Should().Be("window");
        result.Find("invite", DesignPart.Title)!.Text.Should().Be("Invite someone");
        result.Find("people", DesignPart.Caption).Should().NotBeNull();
        result.Find("people", DesignPart.ColumnHeader, "email")!.Text.Should().Be("Mail");
        result.Find("people", DesignPart.ColumnHeader, "name")!.Anchor.Should().Be(new PrototypeAnchor("people", null, DesignPart.ColumnHeader, "name"));
        result.Find("names", DesignPart.Caption).Should().NotBeNull();
        result.Find("one", DesignPart.Tab)!.Text.Should().Be("One");

        result.Find("add", DesignPart.Title).Should().BeNull("a button has no title");
        result.Find("people", DesignPart.ColumnHeader, "phone").Should().BeNull();
        result.Find("blank", DesignPart.Title).Should().BeNull("a screen with no words draws no heading");
        result.Find("directory").Should().NotBeNull("a node is found without a part, as before");
        result.Parts.Keys.Should().OnlyContain(k => k.Part != DesignPart.Node);
    }

    [Fact]
    public void An_operation_on_a_part_that_is_not_drawn_is_unplaced_never_dropped()
    {
        var onBlankTitle = Css(new PrototypeAnchor("blank", null, DesignPart.Title), "color", "red");
        var onButtonTitle = Css(new PrototypeAnchor("add", null, DesignPart.Title), "color", "red");

        var result = PrototypeRenderer.Render(Valid(Directory), [onBlankTitle, onButtonTitle]);

        result.Unplaced.Should().Equal(onBlankTitle, onButtonTitle);
        Regex.Matches(Body(result.Html), "color: red").Should().BeEmpty();
    }

    [Fact]
    public void A_words_only_title_is_drawn_for_a_screen_with_no_words_when_the_title_is_given_some()
    {
        var result = PrototypeRenderer.Render(
            Valid(Directory), [new PrototypeSetTextOperation(new PrototypeAnchor("blank", null, DesignPart.Title), "Now titled")]);

        result.Unplaced.Should().BeEmpty();
        Words(result.Html, PartTag(result.Html, "blank", "title")).Should().Be("Now titled");
    }

    [Fact]
    public void A_part_whose_node_disappears_goes_unplaced_and_can_be_re_anchored_onto_another_nodes_part()
    {
        var bigger = Css(InviteTitle, "font-size", "1.6rem");
        var document = new PrototypeDocument(PrototypeVocabulary.Version, new PrototypeDesignRef(1, 1), [bigger]);

        var newer = PrototypeRenderer.Render(Valid(WithoutTheDialog), document.Overlay);
        newer.Unplaced.Should().Equal(bigger);
        PrototypeOverlayDescription.Describe(newer.Unplaced[0]).Should().Be("Sets font-size: 1.6rem on the title of \"invite\".");

        var session = PrototypeEditSession.Open(document);
        var target = newer.Find("settings", DesignPart.Title)!.Anchor;
        session.Reanchor(0, target).Should().BeTrue();

        var settled = PrototypeRenderer.Render(Valid(WithoutTheDialog), session.Overlay);
        settled.Unplaced.Should().BeEmpty();
        Style(PartTag(settled.Html, "settings", "title")).Should().Be("font-size: 1.6rem");
        Style(NodeTag(settled.Html, "settings")).Should().BeNull();
        PrototypeWriter.Write(session.Document).Should().Contain("\"part\": \"title\"");
    }

    // ---- Edit mode: the selected part is outlined by its own attributes -------------------------------------

    [Fact]
    public void An_edit_render_outlines_a_selected_part_and_never_one_it_did_not_draw()
    {
        var title = PrototypeRenderer.Render(
            Valid(Directory), [], new PrototypeRenderOptions(Edit: true, Selected: "directory", SelectedPart: DesignPart.Title)).Html;
        var header = PrototypeRenderer.Render(
            Valid(Directory), [], new PrototypeRenderOptions(Edit: true, Selected: "people", SelectedPart: DesignPart.ColumnHeader, SelectedColumn: "email")).Html;
        var forged = PrototypeRenderer.Render(
            Valid(Directory), [], new PrototypeRenderOptions(Edit: true, Selected: "add", SelectedPart: DesignPart.Title)).Html;

        title.Should().Contain("[data-mk-part-of=\"directory\"][data-mk-part=\"title\"] { outline: 2px solid")
            .And.NotContain("[data-mk-anchor=\"directory\"] { outline");
        header.Should().Contain("[data-mk-part-of=\"people\"][data-mk-part=\"columnHeader\"][data-mk-col=\"email\"] { outline: 2px solid");
        forged.Should().NotContain("outline: 2px solid #2f6fdf !important");
        title.Should().Contain("[data-mk-part-of]:hover");
    }

    // ---- reading and writing a part ------------------------------------------------------------------------

    [Fact]
    public async Task A_document_with_parts_reads_and_writes_back_byte_for_byte()
    {
        var wiki = new FakeWikiLibrary().With(1, WikiItemType.Design, version: 1);
        var document = new PrototypeDocument(PrototypeVocabulary.Version, new PrototypeDesignRef(1, 1),
        [
            Css(DirectoryTitle, "font-size", "1.6rem"),
            new PrototypeSetTextOperation(EmailHeader, "E-mail"),
            new PrototypeAddClassOperation(new PrototypeAnchor(null, "/screens/0/children/2/children/1", DesignPart.Tab), "loud"),
            new PrototypeSetTextOperation(new PrototypeAnchor("add", null), "Add someone"),
        ]);

        var written = PrototypeWriter.Write(document);
        written.Should().Contain("\"anchor\": {\n        \"id\": \"people\",\n        \"part\": \"columnHeader\",\n        \"column\": \"email\"\n      }");
        written.Should().Contain("\"anchor\": {\n        \"id\": \"add\"\n      }", "an anchor with no part writes as it always did");

        var read = await PrototypeReader.ReadAsync(written, wiki);
        read.Errors.Should().BeEmpty();
        read.Document!.Overlay.Select(PrototypeRenderer.AnchorOf).Should().Equal(document.Overlay.Select(PrototypeRenderer.AnchorOf));
        PrototypeWriter.Write(read.Document).Should().Be(written);
    }

    [Theory]
    [InlineData("""{"id": "x", "part": "heading"}""", "$.operations[0].anchor.part", "\"heading\" is not a part")]
    [InlineData("""{"id": "x", "part": "node"}""", "$.operations[0].anchor.part", "leave \"part\" out to address the node itself")]
    [InlineData("""{"id": "x", "part": 3}""", "$.operations[0].anchor.part", "is not a part")]
    [InlineData("""{"id": "x", "column": "email"}""", "$.operations[0].anchor.column", "goes with \"part\": \"columnHeader\" only")]
    [InlineData("""{"id": "x", "part": "title", "column": "email"}""", "$.operations[0].anchor.column", "a \"title\" part has no column")]
    [InlineData("""{"id": "x", "part": "columnHeader"}""", "$.operations[0].anchor", "needs \"column\"")]
    [InlineData("""{"id": "x", "part": "columnHeader", "column": ""}""", "$.operations[0].anchor.column", "non-empty string")]
    [InlineData("""{"id": "x", "part": "title", "row": 1}""", "$.operations[0].anchor.row", "may carry \"id\", \"path\", \"part\" or \"column\"")]
    public void A_malformed_part_is_refused_with_its_reason(string anchor, string path, string says)
    {
        var read = PrototypeReader.ReadOperations($$"""[{"op": "setText", "anchor": {{anchor}}, "text": "x"}]""");

        read.IsValid.Should().BeFalse();
        read.Errors.Should().ContainSingle(e => e.Path == path && e.Message.Contains(says),
            string.Join("\n", read.Errors.Select(e => e.ToString())));
    }

    // ---- a part the component does not have ------------------------------------------------------------------

    [Fact]
    public void A_part_the_component_does_not_have_is_refused_with_its_reason()
    {
        var operations = new PrototypeOperation[]
        {
            Css(new PrototypeAnchor("add", null, DesignPart.Title), "color", "red"),
            Css(new PrototypeAnchor("directory", null, DesignPart.Caption), "color", "red"),
            Css(new PrototypeAnchor("people", null, DesignPart.ColumnHeader, "phone"), "color", "red"),
            Css(new PrototypeAnchor("people", null, DesignPart.Tab), "color", "red"),
            Css(new PrototypeAnchor("invite", null, DesignPart.Caption), "color", "red"),
        };

        var errors = PrototypeReader.CheckParts(operations, Valid(Directory));

        errors.Select(e => e.ToString()).Should().Equal(
            "$.operations[0].anchor.part: The button \"add\" has no \"title\" part; it has no parts, so leave \"part\" out to address the button itself.",
            "$.operations[1].anchor.part: The screen \"directory\" has no \"caption\" part; its part is \"title\".",
            "$.operations[2].anchor.column: The table \"people\" has no column \"phone\"; its columns are \"name\" and \"email\".",
            "$.operations[3].anchor.part: The table \"people\" has no \"tab\" part; its parts are \"caption\" and \"columnHeader\".",
            "$.operations[4].anchor.part: The dialog \"invite\" has no \"caption\" part; its part is \"title\".");
    }

    [Fact]
    public void Every_part_the_page_draws_passes_the_check_and_an_anchor_with_no_node_is_left_to_the_renderer()
    {
        var tree = Valid(Directory);
        var drawn = PrototypeRenderer.Render(tree, []).Parts.Values
            .Select(p => (PrototypeOperation)Css(p.Anchor, "color", "red")).ToList();
        drawn.Should().HaveCount(8, "two titles, two captions, two headers and two tabs; the wordless screen draws none");

        PrototypeReader.CheckParts(drawn, tree).Should().BeEmpty();
        PrototypeReader.CheckParts([Css(new PrototypeAnchor("gone", null, DesignPart.Title), "color", "red")], tree).Should().BeEmpty();
        PrototypeReader.CheckParts([Css(new PrototypeAnchor(null, "/screens/0/children/2/children/0", DesignPart.Tab), "color", "red")], tree)
            .Should().BeEmpty("a tab is addressed on its panel, by path as well as by id");
        PrototypeReader.CheckParts([new PrototypePageCssOperation("h1 { color: red; }")], tree, "$.overlay").Should().BeEmpty();
    }
}
