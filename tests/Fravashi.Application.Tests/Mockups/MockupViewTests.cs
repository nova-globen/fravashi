using AwesomeAssertions;
using Fravashi.Application.Mockups;
using Xunit;

namespace Fravashi.Application.Tests.Mockups;

/// <summary>#194: what a node looks like for a chosen viewport and state, and what a list, table or tree shows.</summary>
public class MockupViewTests
{
    private static MockupDocument Valid(string json)
    {
        var read = MockupReader.Read(json);
        read.IsValid.Should().BeTrue(string.Join("\n", read.Errors));
        return read.Document!;
    }

    private static MockupNode Only(string node) =>
        Valid($$"""
            {"mockup": 1, "viewports": [{"name": "wide", "width": 1200}, {"name": "narrow", "width": 400}],
             "data": {"people": [{"name": "Ada", "team": [{"name": "Grace"}]}, {"name": "Alan"}]},
             "screens": [{"type": "window", "id": "w", "children": [{{node}}]}]}
            """).Screens[0].Children![0];

    [Fact]
    public void At_rest_at_no_viewport_a_node_is_itself()
    {
        var node = Only("""{"type": "button", "text": "Save", "at": {"narrow": {"text": "S"}}, "states": {"loading": {"text": "Saving..."}}}""");

        MockupView.Resolve(node, null, null).Should().Be(node);
    }

    [Fact]
    public void A_viewport_applies_only_its_own_override_and_keeps_what_the_override_does_not_carry()
    {
        var node = Only("""{"type": "panel", "text": "Base", "layout": "column", "at": {"narrow": {"layout": "row"}, "wide": {"text": "Wide"}}}""");

        var narrow = MockupView.Resolve(node, "narrow", null);
        narrow.Layout.Should().Be(MockupLayout.Row);
        narrow.Text.Should().Be("Base");

        var wide = MockupView.Resolve(node, "wide", null);
        wide.Layout.Should().Be(MockupLayout.Column);
        wide.Text.Should().Be("Wide");

        MockupView.Resolve(node, "somewhere", null).Should().Be(node);
    }

    [Fact]
    public void A_state_goes_over_the_viewport()
    {
        var node = Only("""{"type": "button", "text": "Save", "at": {"narrow": {"text": "S"}}, "states": {"loading": {"text": "Saving..."}, "disabled": {}}}""");

        MockupView.Resolve(node, "narrow", MockupState.Loading).Text.Should().Be("Saving...");
        MockupView.Resolve(node, "narrow", MockupState.Disabled).Text.Should().Be("S");
        MockupView.Resolve(node, null, MockupState.Selected).Text.Should().Be("Save");
    }

    [Fact]
    public void A_list_shows_its_inline_items_or_the_show_key_of_its_data()
    {
        var inline = Only("""{"type": "list", "items": ["one", {"text": "two"}]}""");
        MockupView.Rows(inline, Valid(Doc)).Select(r => r.Text).Should().Equal("one", "two");

        var bound = Only("""{"type": "list", "data": "people", "show": "name"}""");
        var document = Valid(Doc.Replace("\"children\": []", """ "children": [{"type": "list", "data": "people", "show": "name"}]""", StringComparison.Ordinal));
        MockupView.Rows(bound, document).Select(r => r.Text).Should().Equal("Ada", "Alan");
        MockupView.Rows(bound, document).Should().OnlyContain(r => r.Children.Count == 0);
    }

    [Fact]
    public void A_tree_nests_its_items_and_the_records_under_each_record()
    {
        var inline = Only("""{"type": "tree", "items": [{"text": "src", "items": ["app.cs"]}]}""");
        var row = MockupView.Rows(inline, Valid(Doc)).Should().ContainSingle().Subject;
        row.Text.Should().Be("src");
        row.Children.Select(c => c.Text).Should().Equal("app.cs");

        var document = Valid(Doc.Replace("\"children\": []", """ "children": [{"type": "tree", "data": "people", "show": "name"}]""", StringComparison.Ordinal));
        var bound = document.Screens[0].Children![0];
        var rows = MockupView.Rows(bound, document);
        rows.Select(r => r.Text).Should().Equal("Ada", "Alan");
        rows[0].Children.Select(c => c.Text).Should().Equal("Grace");
    }

    [Fact]
    public void A_table_shows_its_records_cell_by_column_key()
    {
        var document = Valid(Doc.Replace("\"children\": []", """ "children": [{"type": "table", "data": "people", "columns": ["name"]}]""", StringComparison.Ordinal));
        var table = document.Screens[0].Children![0];

        var records = MockupView.Records(table, document);
        records.Select(r => MockupView.Cell(r, "name")).Should().Equal("Ada", "Alan");
        MockupView.Cell(records[0], "missing").Should().BeEmpty();
    }

    [Fact]
    public void The_walk_names_every_node_by_the_path_the_reader_would_use()
    {
        var document = Valid("""
            {"mockup": 1, "screens": [{"type": "window", "id": "w", "children": [
              {"type": "panel", "children": [{"type": "text", "id": "deep", "text": "x"}]},
              {"type": "button", "id": "b", "text": "B"}]}]}
            """);

        MockupView.Walk(document.Screens[0], MockupView.ScreenPath(0))
            .Select(p => p.Path)
            .Should().Equal(
                "$.screens[0]",
                "$.screens[0].children[0]",
                "$.screens[0].children[0].children[0]",
                "$.screens[0].children[1]");
    }

    private const string Doc = """
        {"mockup": 1, "data": {"people": [{"name": "Ada", "team": [{"name": "Grace"}]}, {"name": "Alan"}]},
         "screens": [{"type": "window", "id": "w", "children": []}]}
        """;
}
