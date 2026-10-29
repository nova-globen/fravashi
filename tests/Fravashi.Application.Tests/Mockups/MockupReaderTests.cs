using System.Text;
using AwesomeAssertions;
using Fravashi.Application.Mockups;
using Xunit;

namespace Fravashi.Application.Tests.Mockups;

/// <summary>
/// #50's acceptance clause "an invalid tree is rejected with a useful message": every rule, each asserted on
/// WHERE (the JSON path) and on the part of the sentence that tells the author what to do instead.
/// </summary>
public class MockupReaderTests
{
    /// <summary>A document around the given screen children, with optional extra root properties.</summary>
    private static string Doc(string children, string root = "") =>
        "{\"mockup\": 1, " + root + "\"screens\": [{\"type\": \"window\", \"id\": \"main\", \"children\": [" + children + "]}]}";

    private static MockupError OnlyError(string json)
    {
        var result = MockupReader.Read(json);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle();
        return result.Errors[0];
    }

    [Fact]
    public void A_minimal_tree_reads()
    {
        var result = MockupReader.Read("""{"mockup": 1, "screens": [{"type": "window", "id": "main"}]}""");

        result.IsValid.Should().BeTrue();
        result.Document!.Version.Should().Be(1);
        result.Document.Screens.Should().ContainSingle().Which.Id.Should().Be("main");
    }

    // ---- a link (backlog 290) ------------------------------------------------------------------------

    [Fact]
    public void A_link_with_text_and_a_target_reads()
    {
        var result = MockupReader.Read(Doc("""{"type": "link", "id": "home", "text": "Go to the start page", "href": "/", "on": {"click": {"go": "main"}}}"""));

        result.Errors.Should().BeEmpty();
        var link = result.Document!.Screens[0].Children![0];
        (link.Type, link.Text, link.Href).Should().Be((MockupComponent.Link, "Go to the start page", "/"));
    }

    [Fact]
    public void A_link_named_only_by_its_label_reads_and_its_target_is_optional()
    {
        MockupReader.Read(Doc("""{"type": "link", "a11y": {"label": "Home"}}""")).Errors.Should().BeEmpty();
    }

    [Fact]
    public void A_link_with_no_name_is_told_to_carry_text_or_a_label()
    {
        var error = OnlyError(Doc("""{"type": "link", "href": "/"}"""));

        error.Path.Should().Be("$.screens[0].children[0]");
        error.Message.Should().Contain("A link needs \"text\"").And.Contain("\"a11y\": {\"label\": \"...\"}");
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("42")]
    [InlineData("null")]
    public void A_links_href_is_a_non_empty_string(string href)
    {
        var error = OnlyError(Doc($$$"""{"type": "link", "text": "Home", "href": {{{href}}}}"""));

        error.Path.Should().Be("$.screens[0].children[0].href");
        error.Message.Should().Contain("is a non-empty string");
    }

    [Fact]
    public void Href_belongs_on_a_link_and_a_button_is_told_so()
    {
        var error = OnlyError(Doc("""{"type": "button", "text": "Home", "href": "/"}"""));

        error.Path.Should().Be("$.screens[0].children[0].href");
        error.Message.Should().Contain("A button cannot carry \"href\"; it belongs on \"link\"");
    }

    [Fact]
    public void A_link_holds_no_children()
    {
        var error = OnlyError(Doc("""{"type": "link", "text": "Home", "children": []}"""));

        error.Path.Should().Be("$.screens[0].children[0].children");
    }

    [Fact]
    public void Text_that_is_not_json_is_refused_with_the_parsers_position()
    {
        var error = OnlyError("{\"mockup\": 1, \"screens\": [");

        error.Path.Should().Be("$");
        error.Message.Should().StartWith("This is not valid JSON:");
    }

    [Fact]
    public void A_tree_without_a_version_is_told_how_a_mockup_starts()
    {
        var error = OnlyError("""{"screens": []}""");

        error.Path.Should().Be("$");
        error.Message.Should().Contain("{\"mockup\": 1, ...}");
    }

    [Fact]
    public void Another_version_is_refused_alone_because_its_rules_are_not_these()
    {
        // The node below is broken by version 1's rules; it must not be reported, because it was written
        // against version 2's.
        var error = OnlyError("""{"mockup": 2, "screens": [{"type": "hologram"}]}""");

        error.Path.Should().Be("$.mockup");
        error.Message.Should().Be("This is mockup version 2, and this build reads version 1 only.");
    }

    [Fact]
    public void An_unknown_root_property_names_the_allowed_ones()
    {
        var error = OnlyError("""{"mockup": 1, "pages": [], "screens": [{"type": "window", "id": "main"}]}""");

        error.Path.Should().Be("$.pages");
        error.Message.Should().Contain("\"mockup\", \"title\", \"viewports\", \"data\" or \"screens\"");
    }

    [Fact]
    public void An_unknown_node_property_names_what_that_component_may_carry()
    {
        var error = OnlyError(Doc("""{"type": "button", "text": "Save", "colour": "red"}"""));

        error.Path.Should().Be("$.screens[0].children[0].colour");
        error.Message.Should().Contain("\"colour\" is not a property of the mockup schema")
            .And.Contain("A button may carry \"type\", \"id\", \"text\", \"a11y\", \"state\", \"states\", \"at\" or \"on\"");
    }

    [Fact]
    public void A_property_on_the_wrong_component_says_where_it_belongs()
    {
        var error = OnlyError(Doc("""{"type": "button", "text": "Save", "options": ["a"]}"""));

        error.Path.Should().Be("$.screens[0].children[0].options");
        error.Message.Should().StartWith("A button cannot carry \"options\"; it belongs on \"field\".");
    }

    [Fact]
    public void An_unknown_component_lists_the_palette()
    {
        var error = OnlyError(Doc("""{"type": "carousel"}"""));

        error.Path.Should().Be("$.screens[0].children[0].type");
        error.Message.Should().Contain("\"carousel\" is not a component").And.Contain("\"container\"");
    }

    [Fact]
    public void A_misspelt_case_is_not_quietly_accepted()
    {
        OnlyError(Doc("""{"type": "Button", "text": "Save"}""")).Message.Should().Contain("\"Button\" is not a component");
    }

    [Fact]
    public void A_node_without_a_type_is_told_the_palette()
    {
        var error = OnlyError(Doc("""{"text": "Hello"}"""));

        error.Path.Should().Be("$.screens[0].children[0]");
        error.Message.Should().Contain("has no \"type\"");
    }

    [Fact]
    public void An_id_used_twice_names_where_it_was_first_used()
    {
        var error = OnlyError(Doc("""{"type": "text", "id": "main", "text": "Hi"}"""));

        error.Path.Should().Be("$.screens[0].children[0].id");
        error.Message.Should().Be("The id \"main\" is already used at $.screens[0]; an id is unique in a document.");
    }

    [Fact]
    public void An_id_that_is_not_a_name_says_what_a_name_is()
    {
        OnlyError(Doc("""{"type": "text", "id": "1st", "text": "Hi"}""")).Message
            .Should().Contain("starts with a letter");
    }

    [Fact]
    public void A_property_written_twice_is_refused()
    {
        OnlyError(Doc("""{"type": "text", "text": "a", "text": "b"}""")).Message
            .Should().Be("\"text\" appears twice in one node.");
    }

    [Fact]
    public void A_screen_that_is_not_a_window_is_told_to_wrap_it()
    {
        var error = OnlyError("""{"mockup": 1, "screens": [{"type": "panel"}]}""");

        error.Path.Should().Be("$.screens[0]");
        error.Message.Should().Contain("A screen is a window, and this is a panel");
    }

    [Fact]
    public void A_screen_without_an_id_cannot_be_navigated_to()
    {
        OnlyError("""{"mockup": 1, "screens": [{"type": "window"}]}""").Message
            .Should().Contain("A screen needs an \"id\"");
    }

    [Fact]
    public void A_window_inside_a_window_is_refused()
    {
        OnlyError(Doc("""{"type": "window", "id": "inner"}""")).Message
            .Should().Contain("stands only at the top");
    }

    [Fact]
    public void A_dialog_needs_an_id_to_be_opened()
    {
        OnlyError(Doc("""{"type": "dialog"}""")).Message.Should().Contain("A dialog needs an \"id\"");
    }

    [Fact]
    public void Tabs_hold_labelled_panels()
    {
        var result = MockupReader.Read(Doc("""{"type": "tabs", "children": [{"type": "text", "text": "x"}, {"type": "panel"}]}"""));

        result.Errors.Select(e => (e.Path, e.Message)).Should().BeEquivalentTo(
        [
            ("$.screens[0].children[0].children[0]", "Tabs hold panels, one per tab, and this is a text."),
            ("$.screens[0].children[0].children[1]", "A panel inside tabs needs \"text\": it is the tab's label."),
        ]);
    }

    [Fact]
    public void A_leaf_cannot_hold_children()
    {
        OnlyError(Doc("""{"type": "button", "text": "Go", "children": []}""")).Message
            .Should().StartWith("A button cannot carry \"children\"");
    }

    [Fact]
    public void Cols_belongs_to_a_grid()
    {
        var error = OnlyError(Doc("""{"type": "panel", "cols": 3}"""));

        error.Path.Should().Be("$.screens[0].children[0].cols");
        error.Message.Should().Contain("add \"layout\": \"grid\"");
    }

    [Fact]
    public void A_grid_with_columns_reads()
    {
        MockupReader.Read(Doc("""{"type": "panel", "layout": "grid", "cols": 3}""")).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Go_names_a_screen_that_exists()
    {
        var error = OnlyError(Doc("""{"type": "button", "text": "Go", "on": {"click": {"go": "settings"}}}"""));

        error.Path.Should().Be("$.screens[0].children[0].on.click.go");
        error.Message.Should().Be("There is no screen \"settings\". The screens are \"main\".");
    }

    [Fact]
    public void Go_may_name_a_screen_written_later()
    {
        var json = """
            {"mockup": 1, "screens": [
              {"type": "window", "id": "a", "children": [{"type": "button", "text": "Next", "on": {"click": {"go": "b"}}}]},
              {"type": "window", "id": "b"}]}
            """;

        MockupReader.Read(json).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Open_names_a_dialog_not_some_other_node()
    {
        var error = OnlyError(Doc("""
            {"type": "panel", "id": "side"},
            {"type": "button", "text": "Open", "on": {"click": {"open": "side"}}}
            """));

        error.Path.Should().Be("$.screens[0].children[1].on.click.open");
        error.Message.Should().Be("\"side\" is a panel, and only a dialog opens and closes.");
    }

    [Fact]
    public void Set_names_a_state_the_target_describes()
    {
        var error = OnlyError(Doc("""
            {"type": "button", "id": "save", "text": "Save", "states": {"disabled": {}}},
            {"type": "button", "text": "Go", "on": {"click": {"set": {"save": "loading"}}}}
            """));

        error.Path.Should().Be("$.screens[0].children[1].on.click.set.save");
        error.Message.Should().Contain("does not describe the state \"loading\"").And.Contain("$.screens[0].children[0]");
    }

    [Fact]
    public void Set_to_default_needs_no_described_state()
    {
        MockupReader.Read(Doc("""
            {"type": "button", "id": "save", "text": "Save"},
            {"type": "button", "text": "Reset", "on": {"click": {"set": {"save": "default"}}}}
            """)).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Set_names_a_node_that_exists()
    {
        OnlyError(Doc("""{"type": "button", "text": "Go", "on": {"click": {"set": {"ghost": "hidden"}}}}""")).Message
            .Should().Be("There is no node with the id \"ghost\".");
    }

    [Fact]
    public void An_empty_action_does_nothing_and_is_refused()
    {
        OnlyError(Doc("""{"type": "button", "text": "Go", "on": {"click": {}}}""")).Message
            .Should().Contain("give it at least one of \"go\", \"open\", \"close\" or \"set\"");
    }

    [Fact]
    public void A_component_only_raises_the_events_it_has()
    {
        var error = OnlyError(Doc("""{"type": "button", "text": "Go", "on": {"submit": {"go": "main"}}}"""));

        error.Path.Should().Be("$.screens[0].children[0].on.submit");
        error.Message.Should().Be("A button does not raise \"submit\"; \"form\" do.");
    }

    [Fact]
    public void An_unknown_event_lists_the_events()
    {
        OnlyError(Doc("""{"type": "button", "text": "Go", "on": {"hover": {"go": "main"}}}""")).Message
            .Should().Contain("\"hover\" is not an event");
    }

    [Fact]
    public void A_state_outside_the_vocabulary_lists_the_states()
    {
        var error = OnlyError(Doc("""{"type": "button", "text": "Go", "states": {"pressed": {}}}"""));

        error.Path.Should().Be("$.screens[0].children[0].states.pressed");
        error.Message.Should().Contain("\"pressed\" is not a state");
    }

    [Fact]
    public void The_initial_state_must_be_described()
    {
        OnlyError(Doc("""{"type": "text", "text": "Oops", "state": "hidden"}""")).Message
            .Should().Contain("add \"states\": {\"hidden\": {...}}");
    }

    [Fact]
    public void An_override_changes_how_a_node_looks_not_what_it_is()
    {
        var result = MockupReader.Read(Doc("""
            {"type": "button", "text": "Save", "states": {"loading": {"type": "text", "on": {"click": {"go": "main"}}, "text": "Saving..."}}}
            """));

        result.Errors.Select(e => e.Path).Should().BeEquivalentTo(
            "$.screens[0].children[0].states.loading.type",
            "$.screens[0].children[0].states.loading.on");
        result.Errors.Should().AllSatisfy(e => e.Message.Should().StartWith("An override changes how a node looks"));
    }

    [Fact]
    public void An_override_is_held_to_its_owners_component()
    {
        OnlyError(Doc("""{"type": "button", "text": "Save", "states": {"loading": {"options": ["a"]}}}""")).Message
            .Should().StartWith("A button cannot carry \"options\"");
    }

    [Fact]
    public void At_names_a_declared_viewport()
    {
        var error = OnlyError(Doc(
            """{"type": "panel", "at": {"phone": {"layout": "column"}}}""",
            """ "viewports": [{"name": "wide", "width": 1280}], """));

        error.Path.Should().Be("$.screens[0].children[0].at.phone");
        error.Message.Should().Be("\"phone\" is not a declared viewport. The viewports are \"wide\".");
    }

    [Fact]
    public void At_with_no_viewports_says_how_to_declare_one()
    {
        OnlyError(Doc("""{"type": "panel", "at": {"phone": {}}}""")).Message
            .Should().Contain("add \"viewports\": [{\"name\": \"phone\"");
    }

    [Fact]
    public void A_viewport_width_is_bounded()
    {
        var error = OnlyError(Doc("", """ "viewports": [{"name": "tiny", "width": 20}], """));

        error.Path.Should().Be("$.viewports[0].width");
        error.Message.Should().Contain("from 200 to 7680");
    }

    [Fact]
    public void A_binding_names_a_declared_data_set()
    {
        var error = OnlyError(Doc(
            """{"type": "list", "data": "people", "show": "name"}""",
            """ "data": {"teams": [{"name": "Core"}]}, """));

        error.Path.Should().Be("$.screens[0].children[0].data");
        error.Message.Should().Be("There is no data set \"people\". The data sets are \"teams\".");
    }

    [Fact]
    public void A_bound_list_says_which_key_it_shows()
    {
        OnlyError(Doc("""{"type": "list", "data": "people"}""", """ "data": {"people": [{"name": "Ada"}]}, """)).Message
            .Should().Contain("needs \"show\"");
    }

    [Fact]
    public void A_shown_key_is_in_the_records()
    {
        var error = OnlyError(Doc("""{"type": "list", "data": "people", "show": "nmae"}""", """ "data": {"people": [{"name": "Ada"}]}, """));

        error.Path.Should().Be("$.screens[0].children[0].show");
        error.Message.Should().Be("No record in \"people\" has the key \"nmae\"; its keys are \"name\".");
    }

    [Fact]
    public void A_table_column_whose_key_no_record_has_would_always_be_empty()
    {
        var error = OnlyError(Doc(
            """{"type": "table", "data": "people", "columns": ["name", {"key": "email", "text": "Email"}]}""",
            """ "data": {"people": [{"name": "Ada", "role": "Engineer"}]}, """));

        error.Path.Should().Be("$.screens[0].children[0].columns[1]");
        error.Message.Should().Contain("\"email\"").And.Contain("always be empty");
    }

    [Fact]
    public void A_table_needs_columns()
    {
        OnlyError(Doc("""{"type": "table", "items": [{"a": 1}]}""")).Message.Should().StartWith("A table needs \"columns\"");
    }

    [Fact]
    public void Items_and_data_are_not_both_given()
    {
        OnlyError(Doc("""{"type": "list", "items": ["a"], "data": "people", "show": "name"}""",
            """ "data": {"people": [{"name": "Ada"}]}, """)).Message
            .Should().Be("A list shows either inline \"items\" or a \"data\" set, not both.");
    }

    [Fact]
    public void Only_a_tree_nests_its_items()
    {
        MockupReader.Read(Doc("""{"type": "tree", "items": [{"text": "src", "items": ["a.cs"]}]}""")).IsValid.Should().BeTrue();

        OnlyError(Doc("""{"type": "list", "items": [{"text": "src", "items": ["a.cs"]}]}""")).Path
            .Should().Be("$.screens[0].children[0].items[0].items");
    }

    [Fact]
    public void A_record_holds_plain_values()
    {
        var error = OnlyError(Doc("", """ "data": {"people": [{"name": {"first": "Ada"}}]}, """));

        error.Path.Should().Be("$.data.people[0].name");
        error.Message.Should().Contain("a list of records");
    }

    [Fact]
    public void An_image_says_what_it_shows_or_that_it_shows_nothing()
    {
        OnlyError(Doc("""{"type": "image"}""")).Message.Should().Contain("\"decorative\": true");
        MockupReader.Read(Doc("""{"type": "image", "a11y": {"decorative": true}}""")).IsValid.Should().BeTrue();
        MockupReader.Read(Doc("""{"type": "image", "a11y": {"label": "A map of the office"}}""")).IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_node_is_labelled_or_decorative_not_both()
    {
        OnlyError(Doc("""{"type": "image", "a11y": {"label": "Logo", "decorative": true}}""")).Message
            .Should().Contain("either decorative or labelled");
    }

    [Fact]
    public void An_icon_button_needs_an_accessible_name()
    {
        OnlyError(Doc("""{"type": "button"}""")).Message.Should().Contain("\"a11y\": {\"label\": \"...\"}");
        MockupReader.Read(Doc("""{"type": "button", "a11y": {"label": "Refresh"}}""")).IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_heading_level_is_one_to_six()
    {
        OnlyError(Doc("""{"type": "text", "text": "Title", "a11y": {"heading": 7}}""")).Path
            .Should().Be("$.screens[0].children[0].a11y.heading");
    }

    [Fact]
    public void A_field_with_only_a_placeholder_has_no_label()
    {
        OnlyError(Doc("""{"type": "field", "placeholder": "Search"}""")).Message
            .Should().Contain("A placeholder is not a label");
    }

    [Fact]
    public void A_choice_field_needs_options_and_other_fields_do_not_take_them()
    {
        OnlyError(Doc("""{"type": "field", "text": "Role", "input": "select"}""")).Message
            .Should().Be("A select field needs \"options\": the choices it offers.");
        OnlyError(Doc("""{"type": "field", "text": "Name", "options": ["a"]}""")).Path
            .Should().Be("$.screens[0].children[0].options");
    }

    [Fact]
    public void A_choice_fields_value_is_one_of_its_options()
    {
        OnlyError(Doc("""{"type": "field", "text": "Role", "input": "radio", "options": ["a", "b"], "value": "c"}""")).Message
            .Should().Be("\"c\" is not one of this field's options (\"a\" or \"b\").");
    }

    [Fact]
    public void A_checkbox_value_is_a_boolean()
    {
        OnlyError(Doc("""{"type": "field", "text": "Agree", "input": "checkbox", "value": "yes"}""")).Message
            .Should().Be("A checkbox's value is true or false.");
    }

    [Fact]
    public void An_unknown_input_lists_the_inputs()
    {
        OnlyError(Doc("""{"type": "field", "text": "Colour", "input": "colour"}""")).Message
            .Should().Contain("An input is \"text\"");
    }

    [Fact]
    public void Every_error_is_reported_in_one_read_so_an_author_fixes_them_in_one_round()
    {
        var result = MockupReader.Read(Doc("""
            {"type": "image"},
            {"type": "button"},
            {"type": "field", "input": "select"},
            {"type": "carousel"}
            """));

        // The select field is wrong twice over (no label, no options), so four nodes carry five errors.
        result.Errors.Should().HaveCount(5);
        result.Errors.Select(e => e.Path).Distinct().Should().HaveCount(4);
    }

    [Fact]
    public void A_tree_nested_past_the_limit_is_refused_once()
    {
        var json = new StringBuilder();
        for (var i = 0; i < 40; i++)
        {
            json.Append("{\"type\": \"container\", \"children\": [");
        }

        for (var i = 0; i < 40; i++)
        {
            json.Append("]}");
        }

        var error = OnlyError(Doc(json.ToString()));

        error.Message.Should().Contain("nested deeper than 32 levels");
    }

    [Fact]
    public void Errors_print_as_path_then_message()
    {
        new MockupError("$.screens[0]", "Wrong.").ToString().Should().Be("$.screens[0]: Wrong.");
    }
}
