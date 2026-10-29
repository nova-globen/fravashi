using AwesomeAssertions;
using Fravashi.Application.Prototypes;
using Fravashi.Domain.Wiki;
using Xunit;

namespace Fravashi.Application.Tests.Prototypes;

/// <summary>
/// Backlog 211's acceptance: the reader refuses an unknown version, a missing or non-design reference, an
/// operation with no anchor, an unknown operation, an inserted fragment carrying a script element or an
/// event-handler attribute (refused by name), and an unknown property, each with its path.
/// </summary>
public class PrototypeReaderTests
{
    private static FakeWikiLibrary Wiki() => new FakeWikiLibrary().With(1, WikiItemType.Design, version: 3);

    private static string Doc(string overlay = "[]", string design = """{"number": 1, "version": 1}""") =>
        $$"""{"prototype": 1, "design": {{design}}, "overlay": {{overlay}}}""";

    private static async Task<PrototypeError> OnlyErrorAsync(string json, FakeWikiLibrary? wiki = null)
    {
        var result = await PrototypeReader.ReadAsync(json, wiki ?? Wiki());
        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle();
        return result.Errors[0];
    }

    [Fact]
    public async Task A_minimal_document_reads()
    {
        var result = await PrototypeReader.ReadAsync(Doc(), Wiki());

        result.IsValid.Should().BeTrue();
        result.Document!.Version.Should().Be(1);
        result.Document.Design.Should().Be(new PrototypeDesignRef(1, 1));
        result.Document.Overlay.Should().BeEmpty();
    }

    [Fact]
    public async Task Text_that_is_not_json_is_refused_with_the_parsers_position()
    {
        var error = await OnlyErrorAsync("{\"prototype\": 1,");

        error.Path.Should().Be("$");
        error.Message.Should().StartWith("This is not valid JSON:");
    }

    [Fact]
    public async Task A_document_without_a_version_is_told_how_a_prototype_starts()
    {
        var error = await OnlyErrorAsync("""{"design": {"number": 1, "version": 1}, "overlay": []}""");

        error.Path.Should().Be("$");
        error.Message.Should().Contain("{\"prototype\": 1, ...}");
    }

    [Fact]
    public async Task An_unknown_schema_version_is_refused()
    {
        var error = await OnlyErrorAsync("""{"prototype": 2, "design": {"number": 1, "version": 1}, "overlay": []}""");

        error.Path.Should().Be("$.prototype");
        error.Message.Should().Be("This is prototype version 2, and this build reads version 1 only.");
    }

    [Fact]
    public async Task An_unknown_root_property_names_the_allowed_ones()
    {
        var error = await OnlyErrorAsync(
            """{"prototype": 1, "design": {"number": 1, "version": 1}, "overlay": [], "extra": true}""");

        error.Path.Should().Be("$.extra");
        error.Message.Should().Contain("\"prototype\", \"design\" or \"overlay\"");
    }

    [Fact]
    public async Task A_missing_design_reference_is_refused()
    {
        var error = await OnlyErrorAsync("""{"prototype": 1, "overlay": []}""");

        error.Path.Should().Be("$");
        error.Message.Should().Contain("\"design\": {\"number\": 12, \"version\": 1}");
    }

    [Fact]
    public async Task A_design_reference_to_a_nonexistent_document_is_refused()
    {
        var error = await OnlyErrorAsync(Doc(design: """{"number": 999, "version": 1}"""), new FakeWikiLibrary());

        error.Path.Should().Be("$.design.number");
        error.Message.Should().Be("There is no wiki document numbered 999.");
    }

    [Fact]
    public async Task A_reference_to_a_document_that_is_not_a_design_is_refused_naming_its_real_type()
    {
        var wiki = new FakeWikiLibrary().With(1, WikiItemType.Plan);

        var error = await OnlyErrorAsync(Doc(), wiki);

        error.Path.Should().Be("$.design.number");
        error.Message.Should().Be("@1 is a plan, not a design; a prototype is generated from a design.");
    }

    [Fact]
    public async Task A_design_version_the_document_has_not_reached_is_refused()
    {
        var error = await OnlyErrorAsync(Doc(design: """{"number": 1, "version": 99}"""));

        error.Path.Should().Be("$.design.version");
        error.Message.Should().Be("Design @1 has no version 99; it is at version 3.");
    }

    [Fact]
    public async Task An_operation_with_no_anchor_is_refused()
    {
        var error = await OnlyErrorAsync(Doc("""[{"op": "setText", "text": "Hi"}]"""));

        error.Path.Should().Be("$.overlay[0]");
        error.Message.Should().Contain("no \"anchor\"");
    }

    [Fact]
    public async Task An_anchor_with_both_id_and_path_is_refused()
    {
        var error = await OnlyErrorAsync(
            Doc("""[{"op": "addClass", "anchor": {"id": "a", "path": "/screens/0"}, "class": "x"}]"""));

        error.Path.Should().Be("$.overlay[0].anchor");
        error.Message.Should().Contain("never both");
    }

    [Fact]
    public async Task An_anchor_with_neither_id_nor_path_is_refused()
    {
        var error = await OnlyErrorAsync(Doc("""[{"op": "addClass", "anchor": {}, "class": "x"}]"""));

        error.Path.Should().Be("$.overlay[0].anchor");
        error.Message.Should().Contain("exactly one of \"id\"");
    }

    [Fact]
    public async Task An_unknown_operation_names_the_closed_list()
    {
        var error = await OnlyErrorAsync(Doc("""[{"op": "deleteNode", "anchor": {"id": "a"}}]"""));

        error.Path.Should().Be("$.overlay[0].op");
        error.Message.Should().Contain("\"setText\", \"setCss\", \"setAttribute\", \"addClass\", \"pageCss\" or \"insertHtml\"");
    }

    [Fact]
    public async Task An_operation_with_no_op_is_refused()
    {
        var error = await OnlyErrorAsync(Doc("""[{"anchor": {"id": "a"}}]"""));

        error.Path.Should().Be("$.overlay[0]");
        error.Message.Should().Contain("has no \"op\"");
    }

    [Fact]
    public async Task An_unknown_property_on_an_operation_names_what_it_may_carry()
    {
        var error = await OnlyErrorAsync(
            Doc("""[{"op": "addClass", "anchor": {"id": "a"}, "class": "x", "colour": "red"}]"""));

        error.Path.Should().Be("$.overlay[0].colour");
        error.Message.Should().Contain("is not a property of the overlay schema");
        error.Message.Should().Contain("\"anchor\" or \"class\"");
    }

    [Fact]
    public async Task A_property_belonging_to_another_operation_says_where_it_belongs()
    {
        var error = await OnlyErrorAsync(
            Doc("""[{"op": "addClass", "anchor": {"id": "a"}, "class": "x", "rule": "body { color: red; }"}]"""));

        error.Path.Should().Be("$.overlay[0].rule");
        error.Message.Should().StartWith("A \"addClass\" operation cannot carry \"rule\"; it belongs on \"pageCss\".");
    }

    [Fact]
    public async Task PageCss_carries_no_anchor_and_refuses_one()
    {
        var error = await OnlyErrorAsync(
            Doc("""[{"op": "pageCss", "anchor": {"id": "a"}, "rule": "body { color: red; }"}]"""));

        error.Path.Should().Be("$.overlay[0].anchor");
        error.Message.Should().StartWith("A \"pageCss\" operation cannot carry \"anchor\"");
    }

    [Fact]
    public async Task InsertHtml_carrying_a_script_element_is_refused_by_name()
    {
        var error = await OnlyErrorAsync(
            Doc("""[{"op": "insertHtml", "anchor": {"id": "a"}, "placement": "after", "html": "<script>alert(1)</script>"}]"""));

        error.Path.Should().Be("$.overlay[0].html");
        error.Message.Should().Contain("\"<script>\"");
    }

    [Fact]
    public async Task InsertHtml_carrying_an_event_handler_attribute_is_refused_by_name()
    {
        var error = await OnlyErrorAsync(
            Doc("""[{"op": "insertHtml", "anchor": {"id": "a"}, "placement": "inside", "html": "<button onclick=\"x()\">Go</button>"}]"""));

        error.Path.Should().Be("$.overlay[0].html");
        error.Message.Should().Contain("\"onclick\"");
    }

    [Fact]
    public async Task SetAttribute_refuses_setting_an_event_handler_attribute_by_name()
    {
        var error = await OnlyErrorAsync(
            Doc("""[{"op": "setAttribute", "anchor": {"id": "a"}, "name": "onclick", "value": "x()"}]"""));

        error.Path.Should().Be("$.overlay[0].name");
        error.Message.Should().Contain("\"onclick\" is an event-handler attribute");
    }

    [Theory]
    [InlineData("x\" onclick=\"alert(1)")]
    [InlineData("x><img src=x onerror=alert(1)>")]
    public async Task SetAttribute_refuses_a_name_shaped_to_inject_a_new_attribute(string name)
    {
        var error = await OnlyErrorAsync(
            Doc($$"""[{"op": "setAttribute", "anchor": {"id": "a"}, "name": {{System.Text.Json.JsonSerializer.Serialize(name)}}, "value": "x"}]"""));

        error.Path.Should().Be("$.overlay[0].name");
        error.Message.Should().Contain("not a plain attribute name");
    }

    [Fact]
    public async Task SetAttribute_refuses_the_renderers_own_reserved_anchor_attribute()
    {
        var error = await OnlyErrorAsync(
            Doc("""[{"op": "setAttribute", "anchor": {"id": "a"}, "name": "data-mk-anchor", "value": "x"}]"""));

        error.Path.Should().Be("$.overlay[0].name");
        error.Message.Should().Contain("data-mk-anchor");
    }

    // ---- backlog 213: the bypasses the host measured against #211's guard, each now refused by name -------
    // Defence in depth only: the viewer's sandbox and the page's policy are the boundary (plan @38), and
    // PrototypePageTests shows a fragment the guard does NOT catch landing under that policy.

    [Theory]
    [InlineData("<img src=x/onerror=alert(1)>", "\"onerror\"")]
    [InlineData("<img alt=\"a>\"onerror=alert(1)>", "\"onerror\"")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>", "javascript:")]
    [InlineData("<a href=\"JaVa\tScRiPt:alert(1)\">x</a>", "javascript:")]
    [InlineData("<a href=\"vbscript:x\">x</a>", "javascript:")]
    [InlineData("<iframe srcdoc=\"x\"></iframe>", "\"<iframe>\"")]
    [InlineData("<object data=\"https://example.com\"></object>", "\"<object>\"")]
    [InlineData("<embed src=\"https://example.com\">", "\"<embed>\"")]
    [InlineData("<form action=\"https://example.com\"><button>Go</button></form>", "\"<form>\"")]
    [InlineData("<base href=\"https://example.com/\">", "\"<base>\"")]
    [InlineData("<meta http-equiv=\"refresh\" content=\"0;url=https://example.com\">", "\"<meta>\"")]
    [InlineData("<link rel=\"stylesheet\" href=\"https://example.com/a.css\">", "\"<link>\"")]
    [InlineData("<frameset><frame src=\"https://example.com\"></frameset>", "\"<frameset>\"")]
    [InlineData("<span data-mk-on='{\"click\":{\"go\":\"x\"}}'>x</span>", "\"data-mk-on\"")]
    [InlineData("<span data-mk-anchor=\"save\">x</span>", "\"data-mk-anchor\"")]
    public async Task InsertHtml_carrying_what_loads_embeds_navigates_or_runs_is_refused_by_name(string html, string named)
    {
        var error = await OnlyErrorAsync(
            Doc($$"""[{"op": "insertHtml", "anchor": {"id": "a"}, "placement": "inside", "html": {{System.Text.Json.JsonSerializer.Serialize(html)}}}]"""));

        error.Path.Should().Be("$.overlay[0].html");
        error.Message.Should().Contain(named);
    }

    [Fact]
    public async Task InsertHtml_with_ordinary_markup_and_a_data_image_still_reads()
    {
        var result = await PrototypeReader.ReadAsync(
            Doc("""[{"op": "insertHtml", "anchor": {"id": "a"}, "placement": "inside", "html": "<p class=\"note\">Turn it on, <b>then</b> save.</p><img alt=\"dot\" src=\"data:image/gif;base64,R0lGODlhAQABAAAAACw=\">"}]"""),
            Wiki());

        result.IsValid.Should().BeTrue(string.Join("\n", result.Errors));
    }

    [Fact]
    public async Task SetAttribute_refuses_a_script_url_as_its_value()
    {
        var error = await OnlyErrorAsync(
            Doc("""[{"op": "setAttribute", "anchor": {"id": "a"}, "name": "title", "value": "javascript:alert(1)"}]"""));

        error.Path.Should().Be("$.overlay[0].value");
        error.Message.Should().Contain("javascript:");
    }

    [Fact]
    public async Task SetAttribute_refuses_any_name_in_the_renderers_data_mk_namespace()
    {
        var error = await OnlyErrorAsync(
            Doc("""[{"op": "setAttribute", "anchor": {"id": "a"}, "name": "data-mk-on", "value": "{}"}]"""));

        error.Path.Should().Be("$.overlay[0].name");
        error.Message.Should().Contain("data-mk- namespace");
    }

    [Fact]
    public async Task PageCss_with_a_style_breakout_is_refused()
    {
        var error = await OnlyErrorAsync(
            Doc("""[{"op": "pageCss", "rule": "body {} </style><script>alert(1)</script>"}]"""));

        error.Path.Should().Be("$.overlay[0].rule");
        error.Message.Should().Contain("</style");
    }

    [Fact]
    public async Task Every_operation_kind_is_read_into_its_own_record()
    {
        var overlay = """
            [
              {"op": "setText", "anchor": {"id": "a"}, "text": "Hi"},
              {"op": "setCss", "anchor": {"id": "a"}, "properties": {"color": "red"}},
              {"op": "setAttribute", "anchor": {"id": "a"}, "name": "aria-label", "value": "Close"},
              {"op": "addClass", "anchor": {"id": "a"}, "class": "x"},
              {"op": "pageCss", "rule": "body { color: red; }"},
              {"op": "insertHtml", "anchor": {"path": "/screens/0"}, "placement": "before", "html": "<em>hi</em>"}
            ]
            """;

        var result = await PrototypeReader.ReadAsync(Doc(overlay), Wiki());

        result.IsValid.Should().BeTrue();
        result.Document!.Overlay.Should().SatisfyRespectively(
            op => op.Should().BeOfType<PrototypeSetTextOperation>(),
            op => op.Should().BeOfType<PrototypeSetCssOperation>(),
            op => op.Should().BeOfType<PrototypeSetAttributeOperation>(),
            op => op.Should().BeOfType<PrototypeAddClassOperation>(),
            op => op.Should().BeOfType<PrototypePageCssOperation>(),
            op => op.Should().BeOfType<PrototypeInsertHtmlOperation>());

        var insert = (PrototypeInsertHtmlOperation)result.Document.Overlay[5];
        insert.Anchor.Path.Should().Be("/screens/0");
        insert.Placement.Should().Be(PrototypePlacement.Before);
    }
}
