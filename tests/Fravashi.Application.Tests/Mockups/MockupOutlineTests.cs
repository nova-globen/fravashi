using AwesomeAssertions;
using Fravashi.Application.Mockups;
using Xunit;

namespace Fravashi.Application.Tests.Mockups;

/// <summary>
/// #196: the outline over a tree under edit. Each action is one #195 edit, so it is judged, undone and redone like
/// any other, and it says where the node lands so the selection can follow it.
/// </summary>
public class MockupOutlineTests
{
    private const string Tree = """
        {"mockup": 1, "screens": [{"type": "window", "id": "home", "text": "Home", "children": [
          {"type": "toolbar", "children": [
            {"type": "button", "id": "a", "text": "A"},
            {"type": "button", "id": "b", "a11y": {"label": "Bee"}}]},
          {"type": "panel", "id": "p", "text": "P", "children": []},
          {"type": "text", "id": "t", "text": "T"}]}]}
        """;

    private static MockupEditSession Open(string json = Tree)
    {
        MockupEditSession.TryOpen(json, out var session, out var refusal).Should().BeTrue(string.Join("; ", refusal));
        return session!;
    }

    private static string[] Ids(MockupEditSession session) =>
        MockupOutline.Rows(session).Select(r => r.Id ?? r.Type).ToArray();

    private static MockupOutlineStep Plan(MockupEditSession session, MockupOutlineAction action, params int[] path) =>
        MockupOutline.Plan(session, path, action) ?? throw new InvalidOperationException("no step");

    [Fact]
    public void The_rows_are_every_node_in_document_order_with_where_it_is_and_what_it_says()
    {
        var rows = MockupOutline.Rows(Open());

        rows.Select(r => r.JsonPath).Should().Equal(
            "$.screens[0]",
            "$.screens[0].children[0]",
            "$.screens[0].children[0].children[0]",
            "$.screens[0].children[0].children[1]",
            "$.screens[0].children[1]",
            "$.screens[0].children[2]");
        rows.Select(r => r.Depth).Should().Equal(1, 2, 3, 3, 2, 2);
        rows[0].Should().Match<MockupOutlineRow>(r => r.Type == "window" && r.Text == "Home" && r.Id == "home");
        rows[3].Label.Should().Be("Bee");
        rows[4].Siblings.Should().Be(3);
        rows[4].Index.Should().Be(1);
    }

    [Fact]
    public void An_invalid_tree_still_has_an_outline_to_repair_it_in()
    {
        var session = Open("""{"mockup": 1, "screens": [{"type": "window", "children": [{"type": "gizmo"}]}]}""");

        session.Document.Should().BeNull();
        MockupOutline.Rows(session).Select(r => r.Type).Should().Equal("window", "gizmo");
    }

    [Fact]
    public void Move_up_and_down_reorder_among_siblings_and_say_where_the_node_lands()
    {
        var session = Open();

        var up = Plan(session, MockupOutlineAction.MoveUp, 0, 0, 1);
        session.Apply(up.Edit).Applied.Should().BeTrue();
        up.Lands.Should().Equal(0, 0, 0);
        Ids(session).Should().Equal("home", "toolbar", "b", "a", "p", "t");

        var down = Plan(session, MockupOutlineAction.MoveDown, 0, 1);
        session.Apply(down.Edit).Applied.Should().BeTrue();
        down.Lands.Should().Equal(0, 2);
        Ids(session).Should().Equal("home", "toolbar", "b", "a", "t", "p");
    }

    [Fact]
    public void Indent_puts_the_node_last_into_the_sibling_before_it()
    {
        var session = Open();

        var indent = Plan(session, MockupOutlineAction.Indent, 0, 2);
        session.Apply(indent.Edit).Applied.Should().BeTrue();

        indent.Lands.Should().Equal(0, 1, 0);
        MockupOutline.Rows(session).Single(r => r.Id == "t").JsonPath.Should().Be("$.screens[0].children[1].children[0]");
    }

    [Fact]
    public void Outdent_puts_the_node_just_after_its_parent()
    {
        var session = Open();

        var outdent = Plan(session, MockupOutlineAction.Outdent, 0, 0, 0);
        session.Apply(outdent.Edit).Applied.Should().BeTrue();

        outdent.Lands.Should().Equal(0, 1);
        Ids(session).Should().Equal("home", "toolbar", "b", "a", "p", "t");
        MockupOutline.Rows(session).Single(r => r.Id == "a").Depth.Should().Be(2);
    }

    [Fact]
    public void Remove_takes_the_node_and_everything_under_it_and_undo_puts_it_back()
    {
        var session = Open();
        var opened = session.Json;

        var remove = Plan(session, MockupOutlineAction.Remove, 0, 0);
        session.Apply(remove.Edit).Applied.Should().BeTrue();
        remove.Lands.Should().BeNull();
        Ids(session).Should().Equal("home", "p", "t");

        session.Undo();
        session.Json.Should().Be(opened);
    }

    [Theory]
    [InlineData(MockupOutlineAction.MoveUp, new[] { 0, 0 })]
    [InlineData(MockupOutlineAction.MoveDown, new[] { 0, 2 })]
    [InlineData(MockupOutlineAction.Indent, new[] { 0, 0 })]
    [InlineData(MockupOutlineAction.Outdent, new[] { 0 })]
    [InlineData(MockupOutlineAction.MoveUp, new[] { 0 })]
    [InlineData(MockupOutlineAction.Remove, new[] { 0, 9 })]
    public void An_action_with_no_meaning_where_the_node_is_has_no_step(MockupOutlineAction action, int[] path)
    {
        MockupOutline.Plan(Open(), path, action).Should().BeNull();
    }

    [Fact]
    public void A_step_the_reader_would_refuse_is_refused_by_the_session_in_the_readers_words()
    {
        var session = Open();
        var opened = session.Json;

        // A button holds no children, so indenting the second button into the first is not a tree the reader takes.
        var indent = Plan(session, MockupOutlineAction.Indent, 0, 0, 1);
        var outcome = session.Apply(indent.Edit);

        outcome.Applied.Should().BeFalse();
        outcome.Refusal.Should().NotBeEmpty();
        session.Json.Should().Be(opened);

        // And outdenting a toolbar to the screen list makes it a screen, which only a window may be.
        session.Apply(Plan(session, MockupOutlineAction.Outdent, 0, 0).Edit).Applied.Should().BeFalse();
    }

    [Theory]
    [InlineData("$.screens[0]", new[] { 0 })]
    [InlineData("$.screens[1].children[2].children[0]", new[] { 1, 2, 0 })]
    [InlineData("$.screens[0].children[1].text", new[] { 0, 1 })]
    [InlineData("$.screens[0].children[1].states.loading.children[0]", new[] { 0, 1 })]
    [InlineData("$.screens[0].children[3].items[2]", new[] { 0, 3 })]
    public void An_error_path_belongs_to_the_node_it_is_inside(string path, int[] node)
    {
        MockupOutline.NodeOf(path).Should().Equal(node);
    }

    [Theory]
    [InlineData("$")]
    [InlineData("$.viewports[0]")]
    [InlineData("body")]
    [InlineData("$.screens")]
    public void An_error_outside_every_screen_belongs_to_no_node(string path)
    {
        MockupOutline.NodeOf(path).Should().BeNull();
    }
}
