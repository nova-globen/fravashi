using AwesomeAssertions;
using Fravashi.Application.Mockups;
using Fravashi.Application.Prototypes;
using Xunit;

namespace Fravashi.Application.Tests.Prototypes;

/// <summary>
/// Backlog 212's acceptance: deterministic byte-identical output, every node of every-feature.json and the
/// team-directory attempts found by its anchor, each overlay operation kind applied at its anchor, an
/// unplaced operation reported rather than dropped, no external reference, and tree text HTML-encoded.
/// </summary>
public class PrototypeRendererTests
{
    private static MockupDocument Valid(string json)
    {
        var read = MockupReader.Read(json);
        read.IsValid.Should().BeTrue(string.Join("\n", read.Errors));
        return read.Document!;
    }

    private static readonly string FixturesRoot = Path.Combine(
        AppContext.BaseDirectory, "Mockups", "Fixtures");

    private static MockupDocument FromFixture(string name) =>
        Valid(File.ReadAllText(Path.Combine(FixturesRoot, name)));

    private static MockupDocument OneButton(string? text = "Save") =>
        Valid($$"""
            {"mockup": 1, "screens": [{"type": "window", "id": "home", "children": [
                {"type": "button", "id": "save", "text": {{(text is null ? "null" : System.Text.Json.JsonSerializer.Serialize(text))}}}
            ]}]}
            """);

    // ---- determinism -----------------------------------------------------------------------------

    [Fact]
    public void The_same_tree_and_overlay_render_byte_identical_output()
    {
        var document = FromFixture("every-feature.json");
        IReadOnlyList<PrototypeOperation> overlay =
        [
            new PrototypeSetTextOperation(new PrototypeAnchor("save", null), "Saving..."),
            new PrototypeAddClassOperation(new PrototypeAnchor("save", null), "is-primary"),
            new PrototypePageCssOperation("body { font-family: sans-serif; }"),
        ];

        var first = PrototypeRenderer.Render(document, overlay);
        var second = PrototypeRenderer.Render(document, overlay);

        first.Html.Should().Be(second.Html);
    }

    // ---- every node of every fixture, found by its anchor ----------------------------------------

    // team-directory.attempt-1.json and attempt-2.json are deliberately invalid MockupDocuments (early,
    // uncorrected agent drafts kept so MockupAuthoredByAgentTests can assert the reader's own error
    // messages against them); only attempt-3 is a valid tree PrototypeRenderer can be handed at all.
    public static IEnumerable<object[]> Fixtures()
    {
        yield return ["every-feature.json"];
        yield return ["team-directory.attempt-3.json"];
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Every_node_including_those_with_no_id_is_found_by_its_anchor(string fixture)
    {
        var document = FromFixture(fixture);
        var result = PrototypeRenderer.Render(document, []);

        foreach (var anchor in AllAnchors(document))
        {
            result.Html.Should().Contain($"data-mk-anchor=\"{System.Net.WebUtility.HtmlEncode(anchor)}\"");
        }
    }

    /// <summary>Every node's own anchor (id, else its RFC-6901 pointer), root screens included.</summary>
    private static IEnumerable<string> AllAnchors(MockupDocument document)
    {
        for (var i = 0; i < document.Screens.Count; i++)
        {
            foreach (var anchor in Walk(document.Screens[i], $"/screens/{i}"))
            {
                yield return anchor;
            }
        }
    }

    private static IEnumerable<string> Walk(MockupNode node, string path)
    {
        yield return node.Id ?? path;
        if (node.Children is { } children)
        {
            for (var i = 0; i < children.Count; i++)
            {
                foreach (var anchor in Walk(children[i], $"{path}/children/{i}"))
                {
                    yield return anchor;
                }
            }
        }
    }

    [Fact]
    public void Every_feature_json_renders_with_no_exception()
    {
        var document = FromFixture("every-feature.json");

        var act = () => PrototypeRenderer.Render(document, []);

        act.Should().NotThrow();
    }

    // ---- each overlay operation kind, applied at its anchor --------------------------------------

    [Fact]
    public void SetText_replaces_the_anchors_visible_text()
    {
        var document = OneButton("Save");
        var result = PrototypeRenderer.Render(
            document, [new PrototypeSetTextOperation(new PrototypeAnchor("save", null), "Saving...")]);

        result.Html.Should().Contain(">Saving...<");
        result.Html.Should().NotContain(">Save<");
        result.Unplaced.Should().BeEmpty();
    }

    [Fact]
    public void SetCss_appears_in_the_anchors_style_attribute()
    {
        var document = OneButton();
        var result = PrototypeRenderer.Render(
            document,
            [new PrototypeSetCssOperation(new PrototypeAnchor("save", null), new Dictionary<string, string> { ["color"] = "red" })]);

        result.Html.Should().Contain("style=\"color: red\"");
        result.Unplaced.Should().BeEmpty();
    }

    [Fact]
    public void SetAttribute_sets_the_named_attribute_on_the_anchor()
    {
        var document = OneButton();
        var result = PrototypeRenderer.Render(
            document, [new PrototypeSetAttributeOperation(new PrototypeAnchor("save", null), "aria-label", "Save changes")]);

        result.Html.Should().Contain("aria-label=\"Save changes\"");
        result.Unplaced.Should().BeEmpty();
    }

    [Fact]
    public void AddClass_appears_in_the_anchors_class_attribute()
    {
        var document = OneButton();
        var result = PrototypeRenderer.Render(
            document, [new PrototypeAddClassOperation(new PrototypeAnchor("save", null), "is-primary")]);

        result.Html.Should().Contain("class=\"mk-button is-primary\"");
        result.Unplaced.Should().BeEmpty();
    }

    [Fact]
    public void PageCss_is_added_to_the_pages_style_block()
    {
        var document = OneButton();
        var result = PrototypeRenderer.Render(
            document, [new PrototypePageCssOperation("body { font-family: sans-serif; }")]);

        result.Html.Should().Contain("body { font-family: sans-serif; }");
        result.Unplaced.Should().BeEmpty();
    }

    [Theory]
    [InlineData(PrototypePlacement.Before, "<small>Draft</small><button")]
    [InlineData(PrototypePlacement.After, "</button><small>Draft</small>")]
    [InlineData(PrototypePlacement.Inside, "<small>Draft</small></button>")]
    public void InsertHtml_lands_raw_at_its_placement(PrototypePlacement placement, string expected)
    {
        var document = OneButton();
        var result = PrototypeRenderer.Render(
            document,
            [new PrototypeInsertHtmlOperation(new PrototypeAnchor("save", null), placement, "<small>Draft</small>")]);

        result.Html.Should().Contain(expected);
        result.Unplaced.Should().BeEmpty();
    }

    [Fact]
    public void Operations_on_the_same_anchor_apply_in_overlay_order()
    {
        var document = OneButton();
        var result = PrototypeRenderer.Render(
            document,
            [
                new PrototypeSetAttributeOperation(new PrototypeAnchor("save", null), "class", "x"),
                new PrototypeAddClassOperation(new PrototypeAnchor("save", null), "y"),
            ]);

        result.Html.Should().Contain("class=\"x y\"");
    }

    // ---- an anchor that resolves to nothing is reported, not dropped ------------------------------

    [Fact]
    public void An_operation_whose_id_anchor_matches_no_node_is_unplaced()
    {
        var document = OneButton();
        var operation = new PrototypeSetTextOperation(new PrototypeAnchor("does-not-exist", null), "x");
        var result = PrototypeRenderer.Render(document, [operation]);

        result.Unplaced.Should().ContainSingle().Which.Should().BeSameAs(operation);
        result.Html.Should().NotContain("does-not-exist");
    }

    [Fact]
    public void An_operation_whose_path_anchor_matches_no_node_is_unplaced()
    {
        var document = OneButton();
        var operation = new PrototypeAddClassOperation(new PrototypeAnchor(null, "/screens/9/children/9"), "x");
        var result = PrototypeRenderer.Render(document, [operation]);

        result.Unplaced.Should().ContainSingle().Which.Should().BeSameAs(operation);
    }

    [Fact]
    public void A_path_anchor_that_does_resolve_is_placed()
    {
        var document = OneButton();
        var operation = new PrototypeAddClassOperation(
            new PrototypeAnchor(null, "/screens/0/children/0"), "is-primary");
        var result = PrototypeRenderer.Render(document, [operation]);

        result.Unplaced.Should().BeEmpty();
        result.Html.Should().Contain("is-primary");
    }

    // ---- encoding ----------------------------------------------------------------------------------

    [Fact]
    public void Text_from_the_tree_is_html_encoded()
    {
        var document = OneButton("<b>Save</b> & \"quote\"");

        var result = PrototypeRenderer.Render(document, []);

        result.Html.Should().NotContain("<b>Save</b>");
        result.Html.Should().Contain("&lt;b&gt;Save&lt;/b&gt; &amp; \"quote\"");
    }

    [Fact]
    public void An_inserted_fragment_is_written_raw_not_encoded()
    {
        var document = OneButton();
        var result = PrototypeRenderer.Render(
            document,
            [new PrototypeInsertHtmlOperation(new PrototypeAnchor("save", null), PrototypePlacement.After, "<small>Draft</small>")]);

        result.Html.Should().Contain("<small>Draft</small>");
        result.Html.Should().NotContain("&lt;small&gt;");
    }

    // ---- no external reference ---------------------------------------------------------------------

    [Fact]
    public void The_rendered_page_carries_no_external_reference()
    {
        var document = FromFixture("every-feature.json");
        var result = PrototypeRenderer.Render(document, []);

        result.Html.Should().NotContain("src=");

        // Backlog 290: a link's href is the page's own "#", never somewhere else; the design's target is inert data.
        System.Text.RegularExpressions.Regex.Matches(result.Html, "(?<![\\w-])href=\"(?<value>[^\"]*)\"")
            .Select(m => m.Groups["value"].Value).Should().Equal("#");
        result.Html.Should().NotContain("@import");
        result.Html.Should().NotContain("url(http");

        // Backlog 213: the page's one script is inline, never fetched.
        result.Html.Should().NotContain("<script ");
        System.Text.RegularExpressions.Regex.Count(result.Html, "<script").Should().Be(1);
    }

    // ---- `at` overrides as media queries -------------------------------------------------------------

    [Fact]
    public void A_layout_override_at_a_viewport_becomes_a_media_query()
    {
        var document = FromFixture("every-feature.json");
        var result = PrototypeRenderer.Render(document, []);

        result.Html.Should().Contain("@media (max-width: 390px)");
        result.Html.Should().Contain("[data-mk-anchor=\"home\"] { display: flex; flex-direction: column;");
    }

    [Fact]
    public void A_table_columns_override_hides_the_excluded_columns_and_never_the_kept_one()
    {
        // every-feature.json's own "inline-table" has only one column, identical at both viewports, so
        // hiding never fires there (nothing to hide) - this fixture has two, one kept, one hidden, which
        // is what proves the rule targets "age", never "name" (the bug: a `:not()` of the hidden columns
        // matches, and so hides, every OTHER column - the kept one - instead).
        var document = Valid("""
            {"mockup": 1, "viewports": [{"name": "wide", "width": 1280}, {"name": "narrow", "width": 390}],
             "screens": [{"type": "window", "id": "home", "children": [
                {"type": "table", "id": "t", "columns": [{"key": "name"}, {"key": "age"}],
                 "items": [{"name": "Ada", "age": 36}], "at": {"narrow": {"columns": ["name"]}}}
             ]}]}
            """);

        var result = PrototypeRenderer.Render(document, []);

        result.Html.Should().Contain(
            "@media (max-width: 390px) { [data-mk-anchor=\"t\"] [data-mk-col=\"age\"] { display: none; } }");
        result.Html.Should().NotContain("[data-mk-col=\"name\"] { display: none; }");
    }

    [Fact]
    public void A_text_override_at_a_viewport_renders_both_variants_toggled_by_media_query()
    {
        var document = FromFixture("every-feature.json");
        var result = PrototypeRenderer.Render(document, []);

        result.Html.Should().Contain("mk-text-base");
        result.Html.Should().Contain("mk-text-at-narrow");
        result.Html.Should().Contain("Side (narrow)");
    }

    [Fact]
    public void Viewport_bands_are_disjoint_whatever_order_the_document_declares_them_in()
    {
        // narrow declared BEFORE wide, the opposite of every-feature.json's own order, to prove the bands
        // come from sorted width, not declaration order.
        var document = Valid("""
            {"mockup": 1, "viewports": [{"name": "narrow", "width": 390}, {"name": "wide", "width": 1280}],
             "screens": [{"type": "window", "id": "home", "children": [
                {"type": "panel", "id": "p", "text": "Base", "layout": "row",
                 "at": {"narrow": {"text": "N", "layout": "column"}, "wide": {"text": "W", "layout": "row"}}}
             ]}]}
            """);

        var result = PrototypeRenderer.Render(document, []);

        result.Html.Should().Contain("@media (max-width: 390px)");
        result.Html.Should().Contain("@media (min-width: 391px) and (max-width: 1280px)");
        result.Html.Should().NotContain("@media (max-width: 1280px)");
        result.Html.Should().Contain("mk-text-at-narrow");
        result.Html.Should().Contain("mk-text-at-wide");
    }

    [Fact]
    public void A_layout_override_at_the_wider_of_two_viewports_gets_a_bounded_band_not_a_bare_max_width()
    {
        var document = Valid("""
            {"mockup": 1, "viewports": [{"name": "narrow", "width": 390}, {"name": "wide", "width": 1280}],
             "screens": [{"type": "window", "id": "home", "children": [
                {"type": "panel", "id": "p", "layout": "column", "at": {"wide": {"layout": "row"}}}
             ]}]}
            """);

        var result = PrototypeRenderer.Render(document, []);

        result.Html.Should().Contain(
            "@media (min-width: 391px) and (max-width: 1280px) "
            + "{ [data-mk-anchor=\"p\"] { display: flex; flex-direction: row; gap: 0.75rem; } }");
    }

    // ---- attribute-name injection (defense in depth, see PrototypeReaderTests for the reader's own gate) --

    [Theory]
    [InlineData("x\" onclick=\"alert(1)")]
    [InlineData("x><img src=x onerror=alert(1)>")]
    public void SetAttribute_with_an_unsafe_name_built_directly_is_left_unplaced_not_written(string name)
    {
        var document = OneButton();
        var operation = new PrototypeSetAttributeOperation(new PrototypeAnchor("save", null), name, "x");

        var result = PrototypeRenderer.Render(document, [operation]);

        result.Html.Should().NotContain("onclick");
        result.Html.Should().NotContain("onerror");
        result.Unplaced.Should().ContainSingle().Which.Should().BeSameAs(operation);
    }

    [Fact]
    public void SetAttribute_naming_the_renderers_own_anchor_attribute_is_left_unplaced()
    {
        var document = OneButton();
        var operation = new PrototypeSetAttributeOperation(new PrototypeAnchor("save", null), "data-mk-anchor", "evil");

        var result = PrototypeRenderer.Render(document, [operation]);

        result.Unplaced.Should().ContainSingle().Which.Should().BeSameAs(operation);
        result.Html.Should().NotContain("data-mk-anchor=\"evil\"");
    }

    // ---- a dialog is a wireframe dialog, not an invisible one -------------------------------------------

    [Fact]
    public void A_dialog_nothing_opens_carries_the_open_attribute()
    {
        var document = Valid("""
            {"mockup": 1, "screens": [{"type": "window", "id": "home", "children": [
                {"type": "dialog", "id": "about", "text": "About", "children": []}
            ]}]}
            """);
        var result = PrototypeRenderer.Render(document, []);

        result.Html.Should().Contain("<dialog data-mk-anchor=\"about\" class=\"mk-dialog\" open=\"open\" aria-label=\"About\">");
    }

    [Fact]
    public void A_dialog_some_action_opens_starts_closed()
    {
        // Backlog 213: every-feature.json's "help" button opens "about", so the tree says it starts closed.
        var document = FromFixture("every-feature.json");
        var result = PrototypeRenderer.Render(document, []);

        result.Html.Should().Contain("<dialog data-mk-anchor=\"about\" class=\"mk-dialog\" aria-label=\"About\">");
    }

    // ---- the overlay never duplicates an attribute the component already emitted -----------------------

    [Fact]
    public void SetAttribute_replaces_an_attribute_the_component_already_emitted_rather_than_duplicating_it()
    {
        var document = Valid("""
            {"mockup": 1, "screens": [{"type": "window", "id": "home", "children": [
                {"type": "button", "id": "help", "a11y": {"label": "Help"}}
            ]}]}
            """);
        var result = PrototypeRenderer.Render(
            document, [new PrototypeSetAttributeOperation(new PrototypeAnchor("help", null), "aria-label", "Save changes")]);

        // An exact match on the whole opening tag is what rules out a duplicate: if the overlay had
        // appended a second aria-label instead of replacing the first, this precise tag text would not
        // appear at all (the real tag would carry both).
        result.Html.Should().Contain(
            "<button data-mk-anchor=\"help\" class=\"mk-button\" type=\"button\" aria-label=\"Save changes\">");
        result.Html.Should().NotContain("aria-label=\"Help\"");
    }

    // ---- `at` overrides with no CSS mechanism are reported, not dropped ---------------------------------

    [Fact]
    public void An_at_override_property_with_no_css_mechanism_is_reported_as_unexpressed()
    {
        var document = Valid("""
            {"mockup": 1, "viewports": [{"name": "wide", "width": 1280}, {"name": "narrow", "width": 390}],
             "screens": [{"type": "window", "id": "home", "children": [
                {"type": "field", "id": "search", "text": "Search", "input": "search", "placeholder": "Search",
                 "at": {"narrow": {"placeholder": "Find"}}}
             ]}]}
            """);

        var result = PrototypeRenderer.Render(document, []);

        result.UnexpressedOverrides.Should().ContainSingle()
            .Which.Should().Be(new PrototypeUnexpressedOverride("search", "narrow", "placeholder"));
    }

    [Fact]
    public void Expressed_overrides_text_layout_and_columns_are_never_reported_as_unexpressed()
    {
        var document = FromFixture("every-feature.json");
        var result = PrototypeRenderer.Render(document, []);

        // Its one unexpressed override is a STATE's (backlog 213): the name field's error placeholder.
        result.UnexpressedOverrides.Where(o => o.Viewport is not null).Should().BeEmpty();
        result.UnexpressedOverrides.Should().ContainSingle()
            .Which.Should().Be(new PrototypeUnexpressedOverride("name", null, "placeholder", "error"));
    }

    // ---- a link (backlog 290) ---------------------------------------------------------------------

    private static MockupDocument OneLink(string href) =>
        Valid($$$"""
            {"mockup": 1, "screens": [{"type": "window", "id": "home", "children": [
                {"type": "link", "id": "go-home", "text": "Go to the start page", "href": {{{System.Text.Json.JsonSerializer.Serialize(href)}}}},
                {"type": "link", "id": "help", "a11y": {"label": "Help"}}
            ]}]}
            """);

    [Theory]
    [InlineData("/")]
    [InlineData("https://example.invalid/elsewhere")]
    [InlineData("javascript:alert(1)")]
    public void A_link_is_an_anchor_whose_only_href_is_the_inert_hash_whatever_the_design_says(string target)
    {
        var html = PrototypeRenderer.Render(OneLink(target), []).Html;

        html.Should().Contain(
            $"<a data-mk-anchor=\"go-home\" class=\"mk-link\" href=\"#\" data-mk-href=\"{target.Replace("&", "&amp;", StringComparison.Ordinal)}\">Go to the start page</a>");
        System.Text.RegularExpressions.Regex.Matches(html, "\\shref=\"(?<value>[^\"]*)\"").Select(m => m.Groups["value"].Value)
            .Should().OnlyContain(v => v == "#", "no element of the page links anywhere live");
    }

    [Fact]
    public void A_links_click_is_cancelled_by_the_pages_script_and_the_policy_lets_nothing_load()
    {
        // The page's one script cancels a click on any a[href] before acting, and an edit render cancels every click;
        // the policy loads nothing from anywhere. With href always "#", even a page whose script never ran stays put.
        PrototypeRenderer.Script.Should().Contain("if (target.closest(\"a[href]\")) { e.preventDefault(); }");
        PrototypeRenderer.ContentSecurityPolicy.Should().StartWith("default-src 'none';");
        PrototypeRenderer.Render(OneLink("/"), []).Html.Should().Contain(PrototypeRenderer.Script);
    }

    [Fact]
    public void A_label_only_link_shows_its_label_as_a_hidden_placeholder_and_is_named_by_it()
    {
        var html = PrototypeRenderer.Render(OneLink("/"), []).Html;

        html.Should().Contain(
            "<a data-mk-anchor=\"help\" class=\"mk-link\" href=\"#\" aria-label=\"Help\"><span class=\"mk-placeholder\" aria-hidden=\"true\">Help</span></a>");
    }

    [Fact]
    public void A_link_with_no_target_carries_no_data_href()
    {
        var document = Valid("""
            {"mockup": 1, "screens": [{"type": "window", "id": "home", "children": [{"type": "link", "id": "l", "text": "Back"}]}]}
            """);

        PrototypeRenderer.Render(document, []).Html.Should().Contain("<a data-mk-anchor=\"l\" class=\"mk-link\" href=\"#\">Back</a>");
    }
}
