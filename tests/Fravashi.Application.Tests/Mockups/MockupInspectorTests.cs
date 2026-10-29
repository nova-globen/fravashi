using System.Text.Json.Nodes;
using AwesomeAssertions;
using Fravashi.Application.Mockups;
using Xunit;

namespace Fravashi.Application.Tests.Mockups;

/// <summary>
/// #198: the property inspector's model. Each type offers exactly the content and layout properties it carries (@14),
/// each change a person makes is ONE edit that one undo takes back, and a change that touches two properties for one
/// act (leaving the grid, switching an input, renaming a column) is still one.
/// </summary>
public class MockupInspectorTests
{
    private const string Tree = """
        {"mockup": 1, "title": "People",
         "data": {"people": [{"name": "Ada", "role": "Admin"}]},
         "screens": [
          {"type": "window", "id": "home", "text": "Home", "layout": "grid", "cols": 2, "children": [
            {"type": "toolbar", "children": [{"type": "button", "id": "go", "text": "Go", "on": {"click": {"open": "confirm"}}}]},
            {"type": "form", "children": [
              {"type": "field", "id": "name", "text": "Name"},
              {"type": "field", "id": "role", "text": "Role", "input": "select", "options": ["Admin", "Member"], "value": "Admin"},
              {"type": "field", "id": "ok", "text": "Agree", "input": "checkbox", "value": true}]},
            {"type": "table", "id": "people", "columns": [{"key": "name", "text": "Name"}, "role"],
             "items": [{"name": "Ada", "role": "Admin"}, {"name": "Lin", "role": "Member"}]},
            {"type": "list", "id": "names", "data": "people", "show": "name"},
            {"type": "tree", "id": "folders", "items": [{"text": "A", "items": ["A1", "A2"]}, "B"]},
            {"type": "image", "a11y": {"decorative": true}},
            {"type": "dialog", "id": "confirm", "text": "Sure?", "children": []}]}]}
        """;

    private const string BoundTableTree = """
        {"mockup": 1, "title": "Team",
         "data": {"team": [{"name": "Ada", "role": "Admin", "office": "A12"}]},
         "screens": [
          {"type": "window", "id": "home", "text": "Home", "children": [
            {"type": "table", "id": "team", "data": "team", "columns": ["name"]}]}]}
        """;

    private static MockupEditSession Open(string json = Tree)
    {
        MockupEditSession.TryOpen(json, out var session, out var refusal).Should().BeTrue(string.Join("; ", refusal));
        session!.Errors.Should().BeEmpty();
        return session;
    }

    private static IReadOnlyList<int> PathOf(MockupEditSession session, string id) =>
        MockupOutline.Rows(session).Single(r => r.Id == id).Path;

    private static IReadOnlyList<int> PathOfType(MockupEditSession session, string type) =>
        MockupOutline.Rows(session).First(r => r.Type == type).Path;

    private static void ApplyAndUndo(MockupEditSession session, MockupEdit? edit)
    {
        edit.Should().NotBeNull();
        var before = session.Json;
        session.Apply(edit!).Applied.Should().BeTrue(string.Join("; ", session.Errors));
        session.Json.Should().NotBe(before);
        session.Undo().Should().BeTrue();
        session.Json.Should().Be(before, "one undo takes back the whole change, so it was ONE edit");
    }

    public static TheoryData<MockupComponent> Components()
    {
        var data = new TheoryData<MockupComponent>();
        foreach (var component in MockupPalette.Components)
        {
            data.Add(component);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Components))]
    public void A_type_is_offered_only_content_properties_it_carries(MockupComponent component)
    {
        var session = Open("""{"mockup": 1, "screens": [{"type": "window", "id": "s", "text": "S", "children": []}]}""");
        var insert = component == MockupComponent.Window ? MockupPalettePlace.After : MockupPalettePlace.Inside;
        var choice = MockupPalette.Plan(session, [0], component, insert);
        session.Apply(choice.Edit!).Applied.Should().BeTrue();

        var node = MockupInspector.Read(session, choice.Lands!)!;
        node.Component.Should().Be(component);
        var carried = MockupVocabulary.PropertiesOf(component);
        node.Offered.Should().OnlyContain(p => carried.Contains(p), "a property the type does not carry is never offered");
        node.Offered.Should().NotContain(["type", "children", "a11y", "state", "states", "at", "on"]);
        node.Offered.Should().Contain(["id", "text"]);
    }

    [Fact]
    public void Conditional_properties_are_offered_only_where_they_mean_something()
    {
        var session = Open();
        var name = MockupInspector.Read(session, PathOf(session, "name"))!;
        name.Offered.Should().Equal("id", "text", "input", "value", "placeholder", "required");

        var role = MockupInspector.Read(session, PathOf(session, "role"))!;
        role.Offered.Should().Contain("options");
        role.Options.Should().Equal("Admin", "Member");

        var home = MockupInspector.Read(session, PathOf(session, "home"))!;
        home.Offered.Should().Equal("id", "text", "layout", "cols");
        var toolbar = MockupInspector.Read(session, PathOfType(session, "toolbar"))!;
        toolbar.Offered.Should().Equal("id", "text", "layout");

        var names = MockupInspector.Read(session, PathOf(session, "names"))!;
        names.Offered.Should().Equal("id", "text", "data", "show");
        names.DataKeys.Should().Equal("name", "role");

        var folders = MockupInspector.Read(session, PathOf(session, "folders"))!;
        folders.Offered.Should().Equal("id", "text", "data", "items");
        folders.ItemLines.Should().Be("A\n  A1\n  A2\nB");

        var people = MockupInspector.Read(session, PathOf(session, "people"))!;
        people.Offered.Should().Equal("id", "text", "data", "columns", "items");
        people.Columns.Should().Equal(new MockupInspectorColumn("name", "Name"), new MockupInspectorColumn("role", null));
        people.Rows[1]["role"].Should().Be("Member");

        MockupInspector.Read(session, PathOfType(session, "button"))!.Offered.Should().Equal("id", "text");
    }

    [Fact]
    public void A_link_offers_its_target_and_setting_it_is_one_edit()
    {
        // Backlog 290.
        var session = Open("""
            {"mockup": 1, "screens": [{"type": "window", "id": "s", "text": "S", "children": [
              {"type": "link", "id": "home", "text": "Home", "href": "/"}]}]}
            """);
        var link = MockupInspector.Read(session, PathOf(session, "home"))!;

        link.Offered.Should().Equal("id", "text", "href");
        link.Href.Should().Be("/");
        ApplyAndUndo(session, MockupInspector.SetString(session, PathOf(session, "home"), "href", "/start"));
        ApplyAndUndo(session, MockupInspector.SetString(session, PathOf(session, "home"), "href", ""));
    }

    [Fact]
    public void Text_id_and_title_are_each_one_edit_and_renaming_an_id_renames_what_names_it()
    {
        var session = Open();
        ApplyAndUndo(session, MockupInspector.SetString(session, PathOfType(session, "button"), "text", "Submit"));
        ApplyAndUndo(session, MockupInspector.SetTitle(session, "Directory"));

        var rename = MockupInspector.SetId(session, PathOf(session, "confirm"), "are-you-sure");
        session.Apply(rename!).Applied.Should().BeTrue();
        session.Json.Should().Contain("\"open\": \"are-you-sure\"");

        MockupInspector.SetString(session, PathOfType(session, "button"), "text", "Go").Should().BeNull("nothing changes");
        MockupInspector.SetTitle(session, "People").Should().BeNull();
    }

    [Fact]
    public void Clearing_a_fields_label_is_allowed_and_leaves_the_readers_sentence_on_the_node()
    {
        var session = Open();
        var path = PathOf(session, "name");
        session.Apply(MockupInspector.SetString(session, path, "text", "")!).Applied.Should().BeTrue();

        session.Errors.Should().ContainSingle(e => e.Path == MockupOutline.JsonPath(path) && e.Message.StartsWith("A field needs \"text\""));
        session.Document.Should().BeNull();
        MockupInspector.Read(session, path)!.Text.Should().BeNull("the edit is kept, not lost");
    }

    [Fact]
    public void Clearing_a_referenced_id_is_refused_in_the_readers_words()
    {
        var session = Open();
        var outcome = session.Apply(MockupInspector.SetId(session, PathOf(session, "confirm"), " ")!);
        outcome.Applied.Should().BeFalse();
        outcome.Refusal.Should().Contain(e => e.Message.Contains("dialog", StringComparison.Ordinal));
    }

    [Fact]
    public void Leaving_the_grid_layout_drops_cols_in_the_same_edit()
    {
        var session = Open();
        var home = PathOf(session, "home");
        var edit = MockupInspector.SetLayout(session, home, "row");
        session.Apply(edit!).Applied.Should().BeTrue();
        MockupInspector.Read(session, home)!.Cols.Should().BeNull();
        session.Errors.Should().BeEmpty();
        session.Undo();
        MockupInspector.Read(session, home)!.Cols.Should().Be("2");

        ApplyAndUndo(session, MockupInspector.SetCols(session, home, "4"));
        var outOfRange = session.Apply(MockupInspector.SetCols(session, home, "13")!);
        outOfRange.Applied.Should().BeFalse();
        outOfRange.Refusal.Single().Message.Should().Contain("1 to 12");
    }

    [Fact]
    public void Cols_is_refused_on_a_node_without_a_grid_and_layout_none_clears_it()
    {
        var session = Open();
        var toolbar = PathOfType(session, "toolbar");
        session.Apply(MockupInspector.SetCols(session, toolbar, "2")!).Applied.Should().BeFalse();
        ApplyAndUndo(session, MockupInspector.SetLayout(session, toolbar, "column"));
        ApplyAndUndo(session, MockupInspector.SetLayout(session, PathOf(session, "home"), ""));
    }

    [Fact]
    public void Switching_a_field_to_select_seeds_options_and_away_drops_them_and_a_value_that_no_longer_fits()
    {
        var session = Open();
        var name = PathOf(session, "name");
        session.Apply(MockupInspector.SetInput(session, name, "radio")!).Applied.Should().BeTrue();
        MockupInspector.Read(session, name)!.Options.Should().Equal(MockupInspector.SeedOptions);
        session.Errors.Should().BeEmpty();
        session.Undo();
        session.CanUndo.Should().BeFalse();

        var role = PathOf(session, "role");
        session.Apply(MockupInspector.SetInput(session, role, "")!).Applied.Should().BeTrue();
        var plain = MockupInspector.Read(session, role)!;
        plain.Options.Should().BeEmpty();
        plain.Value.Should().Be("Admin", "a string still fits a text field");
        session.Errors.Should().BeEmpty();
        session.Undo();

        var ok = PathOf(session, "ok");
        session.Apply(MockupInspector.SetInput(session, ok, "email")!).Applied.Should().BeTrue();
        MockupInspector.Read(session, ok)!.Value.Should().BeNull("true or false is only a checkbox's value");
        session.Errors.Should().BeEmpty();
    }

    [Fact]
    public void A_value_is_typed_by_the_input_and_a_choice_must_be_an_option()
    {
        var session = Open();
        ApplyAndUndo(session, MockupInspector.SetValue(session, PathOf(session, "ok"), "false"));
        session.Apply(MockupInspector.SetValue(session, PathOf(session, "ok"), "false")!);
        session.Json.Should().Contain("\"value\": false");
        session.Undo();

        var role = PathOf(session, "role");
        ApplyAndUndo(session, MockupInspector.SetValue(session, role, "Member"));
        session.Apply(MockupInspector.SetValue(session, role, "Owner")!).Applied.Should().BeFalse();

        var name = PathOf(session, "name");
        session.Apply(MockupInspector.SetInput(session, name, "number")!);
        session.Apply(MockupInspector.SetValue(session, name, "42")!);
        session.Json.Should().Contain("\"value\": 42");

        ApplyAndUndo(session, MockupInspector.SetRequired(session, name, true));
        ApplyAndUndo(session, MockupInspector.SetString(session, name, "placeholder", "e.g. 7"));
    }

    [Fact]
    public void Options_carry_their_value_when_renamed_and_clear_it_when_removed()
    {
        var session = Open();
        var role = PathOf(session, "role");

        session.Apply(MockupInspector.SetOptions(session, role, ["Administrator", "Member"])!).Applied.Should().BeTrue();
        MockupInspector.Read(session, role)!.Value.Should().Be("Administrator");
        session.Undo();
        session.CanUndo.Should().BeFalse();

        session.Apply(MockupInspector.SetOptions(session, role, ["Member", "Admin"])!).Applied.Should().BeTrue();
        MockupInspector.Read(session, role)!.Value.Should().Be("Admin", "a reorder keeps the value");
        session.Undo();

        session.Apply(MockupInspector.SetOptions(session, role, ["Member"])!).Applied.Should().BeTrue();
        MockupInspector.Read(session, role)!.Value.Should().BeNull();
        session.Errors.Should().BeEmpty();
        session.Undo();

        session.Apply(MockupInspector.SetOptions(session, role, [])!).Applied.Should().BeTrue("cleared, it is the reader's error to show");
        session.Errors.Should().ContainSingle(e => e.Message.Contains("needs \"options\""));
        session.Undo();

        var empty = session.Apply(MockupInspector.SetOptions(session, role, ["Admin", ""])!);
        empty.Applied.Should().BeFalse();
        empty.Refusal.Single().Message.Should().Contain("non-empty string");
    }

    [Fact]
    public void Item_lines_become_items_and_a_tree_nests_by_indent()
    {
        var session = Open();
        var folders = PathOf(session, "folders");
        MockupInspector.SetItemLines(session, folders, "A\n  A1\n  A2\nB").Should().BeNull("the same items change nothing");

        session.Apply(MockupInspector.SetItemLines(session, folders, "Root\n      Deep\n  Two\n\nNext\r\n")!).Applied.Should().BeTrue();
        var items = JsonNode.Parse(session.Json)!["screens"]![0]!["children"]![folders[1]]!["items"];
        JsonNode.DeepEquals(items, JsonNode.Parse("""[{"text":"Root","items":["Deep","Two"]},"Next"]""")).Should().BeTrue(items!.ToJsonString());
        session.Undo();

        session.Apply(MockupInspector.SetItemLines(session, folders, "")!).Applied.Should().BeTrue();
        MockupInspector.Read(session, folders)!.HasItems.Should().BeFalse();
    }

    [Fact]
    public void A_list_moves_between_a_data_set_and_inline_items_in_one_edit()
    {
        var session = Open();
        var names = PathOf(session, "names");
        session.Apply(MockupInspector.SetSource(session, names, "")!).Applied.Should().BeTrue();
        var inline = MockupInspector.Read(session, names)!;
        (inline.Data, inline.Show, inline.HasItems).Should().Be((null, null, true));
        session.Errors.Should().BeEmpty();

        session.Apply(MockupInspector.SetSource(session, names, "people")!).Applied.Should().BeTrue();
        var bound = MockupInspector.Read(session, names)!;
        (bound.Data, bound.Show, bound.HasItems).Should().Be(("people", "name", false));
        session.Errors.Should().BeEmpty();

        ApplyAndUndo(session, MockupInspector.SetShow(session, names, "role"));
        session.Apply(MockupInspector.SetShow(session, names, "age")!).Applied.Should().BeFalse();
    }

    [Fact]
    public void A_table_column_takes_its_cells_with_it()
    {
        var session = Open();
        var people = PathOf(session, "people");

        session.Apply(MockupInspector.RenameColumn(session, people, 1, "title")!).Applied.Should().BeTrue();
        var renamed = MockupInspector.Read(session, people)!;
        renamed.Columns[1].Key.Should().Be("title");
        renamed.Rows.Select(r => r["title"]).Should().Equal("Admin", "Member");
        session.Errors.Should().BeEmpty();
        session.Undo();
        session.CanUndo.Should().BeFalse();

        session.Apply(MockupInspector.AddColumn(session, people)!).Applied.Should().BeTrue();
        MockupInspector.Read(session, people)!.Rows.Should().OnlyContain(r => r["column"] == "");
        session.Errors.Should().BeEmpty();
        session.Undo();

        session.Apply(MockupInspector.RemoveColumn(session, people, 0)!).Applied.Should().BeTrue();
        MockupInspector.Read(session, people)!.Rows.Should().OnlyContain(r => !r.ContainsKey("name"));
        session.Undo();

        ApplyAndUndo(session, MockupInspector.MoveColumn(session, people, 1, -1));
        ApplyAndUndo(session, MockupInspector.SetColumnHeading(session, people, 1, "Role"));
        MockupInspector.MoveColumn(session, people, 0, -1).Should().BeNull();
    }

    [Fact]
    public void A_bound_table_adds_the_data_sets_own_next_key_as_a_column()
    {
        // The session canonicalises a valid tree (MockupWriter sorts a record's keys, @14), so the "records' key
        // order" AddColumn walks is name, office, role, not the order they were declared in BoundTableTree.
        var session = Open(BoundTableTree);
        var team = PathOf(session, "team");

        MockupInspector.Read(session, team)!.Columns.Select(c => c.Key).Should().Equal("name");
        MockupInspector.CanAddColumn(MockupInspector.Read(session, team)!).Should().BeTrue();

        ApplyAndUndo(session, MockupInspector.AddColumn(session, team));
        session.Apply(MockupInspector.AddColumn(session, team)!).Applied.Should().BeTrue();
        var afterFirst = MockupInspector.Read(session, team)!;
        afterFirst.Columns.Should().Equal(new MockupInspectorColumn("name", null), new MockupInspectorColumn("office", "office"));
        session.Errors.Should().BeEmpty();
        MockupInspector.CanAddColumn(afterFirst).Should().BeTrue("the set still has an unshown key, \"role\"");

        session.Apply(MockupInspector.AddColumn(session, team)!).Applied.Should().BeTrue();
        var afterSecond = MockupInspector.Read(session, team)!;
        afterSecond.Columns.Select(c => c.Key).Should().Equal("name", "office", "role");
        session.Errors.Should().BeEmpty();

        MockupInspector.CanAddColumn(afterSecond).Should().BeFalse("every record key already has a column");
        MockupInspector.AddColumn(session, team).Should().BeNull("there is no key left to propose that the reader would accept");
    }

    [Fact]
    public void A_table_row_is_edited_cell_by_cell_and_rows_are_added_and_removed()
    {
        var session = Open();
        var people = PathOf(session, "people");
        ApplyAndUndo(session, MockupInspector.SetCell(session, people, 1, "name", "Grace"));
        ApplyAndUndo(session, MockupInspector.AddRow(session, people));
        ApplyAndUndo(session, MockupInspector.RemoveRow(session, people, 0));
        MockupInspector.SetCell(session, people, 0, "name", "Ada").Should().BeNull();

        session.Apply(MockupInspector.SetSource(session, people, "people")!).Applied.Should().BeTrue();
        session.Errors.Should().BeEmpty("the set has both column keys");
        MockupInspector.Read(session, people)!.Offered.Should().NotContain("items");
    }

    [Fact]
    public void Together_is_one_step_and_a_value_group_is_wrong_only_where_it_wrote()
    {
        var session = Open();
        var button = MockupTarget.ById("go");
        var group = MockupEdit.Together(
            MockupEdit.SetProperty(button, "text", "One"),
            MockupEdit.SetProperty(MockupTarget.ById("name"), "text", null));

        session.Apply(group).Applied.Should().BeTrue("a field left without a label is an error elsewhere, allowed");
        session.Undo().Should().BeTrue();
        session.IsDirty.Should().BeFalse();
        session.Redo().Should().BeTrue();
        session.Json.Should().Contain("\"text\": \"One\"");

        var wrong = MockupEdit.Together(
            MockupEdit.SetProperty(button, "text", "Two"),
            MockupEdit.SetProperty(button, "cols", 3));
        session.Apply(wrong).Applied.Should().BeFalse("cols on a button is wrong where the group wrote it");
        session.Json.Should().Contain("\"text\": \"One\"", "a refused group changes nothing");

        FluentActions.Invoking(() => MockupEdit.Together()).Should().Throw<ArgumentException>();
    }
}
