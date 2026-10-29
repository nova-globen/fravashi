using System.Text.Json.Nodes;
using AwesomeAssertions;
using Fravashi.Application.Mockups;
using Xunit;

namespace Fravashi.Application.Tests.Mockups;

/// <summary>
/// #197: the component palette. Every type at every place either inserts a node the reader accepts, exactly where the
/// palette says it lands, as one undoable edit, or is refused with a sentence saying why.
/// </summary>
public class MockupPaletteTests
{
    /// <summary>One node of every type, a panel in tabs, and two screens, so every kind of selection is present.</summary>
    private const string Every = """
        {"mockup": 1, "screens": [
          {"type": "window", "id": "home", "text": "Home", "children": [
            {"type": "toolbar", "children": [{"type": "button", "text": "Go"}]},
            {"type": "panel", "text": "P", "children": [
              {"type": "form", "children": [{"type": "field", "text": "Name"}]},
              {"type": "container", "children": [{"type": "text", "text": "Hi"}]}]},
            {"type": "tabs", "children": [{"type": "panel", "text": "One", "children": [
              {"type": "list", "items": ["a"]}]}]},
            {"type": "table", "columns": ["k"], "items": [{"k": "v"}]},
            {"type": "tree", "items": ["x"]},
            {"type": "image", "a11y": {"label": "Logo"}},
            {"type": "dialog", "id": "dialog", "text": "D", "children": [{"type": "button", "text": "Close"}]}]},
          {"type": "window", "id": "screen", "text": "Other", "children": []}]}
        """;

    private static MockupEditSession Open(string json = Every)
    {
        MockupEditSession.TryOpen(json, out var session, out var refusal).Should().BeTrue(string.Join("; ", refusal));
        return session!;
    }

    public static TheoryData<MockupComponent, MockupPalettePlace> Matrix()
    {
        var data = new TheoryData<MockupComponent, MockupPalettePlace>();
        foreach (var component in MockupPalette.Components)
        {
            foreach (var place in Enum.GetValues<MockupPalettePlace>())
            {
                data.Add(component, place);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Every_type_at_every_place_beside_every_node_is_valid_where_offered_and_says_why_where_refused(
        MockupComponent component, MockupPalettePlace place)
    {
        var session = Open();
        var selections = MockupOutline.Rows(session).Select(r => (IReadOnlyList<int>?)r.Path).Prepend(null).ToList();
        var offered = 0;

        foreach (var selected in selections)
        {
            var choice = MockupPalette.Plan(session, selected, component, place);
            var where = $"{component} {place} at {(selected is null ? "nothing" : MockupOutline.JsonPath(selected))}";

            if (!choice.Offered)
            {
                choice.Refusal.Should().NotBeNullOrWhiteSpace(where);
                choice.Lands.Should().BeNull(where);
                continue;
            }

            offered++;
            var before = session.Json;
            var outcome = session.Apply(choice.Edit!);
            outcome.Applied.Should().BeTrue($"{where}: {string.Join(" ", outcome.Refusal)}");
            session.Document.Should().NotBeNull($"{where}: {string.Join(" ", session.Errors)}");

            var landed = MockupOutline.Rows(session).Single(r => r.Path.SequenceEqual(choice.Lands!));
            landed.Type.Should().Be(MockupVocabulary.Name(component), where);

            session.Undo().Should().BeTrue();
            session.Json.Should().Be(before, where);
        }

        // A window is only ever a new screen, so inside a node is the one place it is never offered.
        if (component == MockupComponent.Window && place == MockupPalettePlace.Inside)
        {
            offered.Should().Be(0);
        }
        else
        {
            offered.Should().BePositive($"{component} {place} is offered somewhere");
        }
    }

    [Fact]
    public void Auto_puts_a_node_inside_a_selection_that_may_hold_it_and_after_one_that_may_not()
    {
        var session = Open();

        MockupPalette.Plan(session, [0, 1], MockupComponent.Button, MockupPalettePlace.Auto).Lands.Should().Equal(0, 1, 2);
        MockupPalette.Plan(session, [0, 0, 0], MockupComponent.Button, MockupPalettePlace.Auto).Lands.Should().Equal(0, 0, 1);

        // Tabs hold only panels: a button with the tabs selected goes after them, a panel into them as a new tab.
        MockupPalette.Plan(session, [0, 2], MockupComponent.Button, MockupPalettePlace.Auto).Lands.Should().Equal(0, 3);
        var tab = MockupPalette.Plan(session, [0, 2], MockupComponent.Panel, MockupPalettePlace.Auto);
        tab.Lands.Should().Equal(0, 2, 1);
        ((InsertedNode(tab))["text"]!.GetValue<string>()).Should().Be("Tab 2");
    }

    // #207: inside tabs, a new panel almost always means a new tab, not a nested panel.
    [Fact]
    public void Auto_puts_a_new_panel_after_a_tabs_own_panel_as_a_new_tab_not_nested_inside_it()
    {
        var session = Open();

        // [0,2,0] is "One", the panel that is itself a direct child of tabs at [0,2].
        var afterIt = MockupPalette.Plan(session, [0, 2, 0], MockupComponent.Panel, MockupPalettePlace.Auto);
        afterIt.Lands.Should().Equal(0, 2, 1);
        InsertedNode(afterIt)["text"]!.GetValue<string>().Should().Be("Tab 2");

        // Ctrl+click (place Inside) still reaches nesting a panel inside the selected tab.
        MockupPalette.Plan(session, [0, 2, 0], MockupComponent.Panel, MockupPalettePlace.Inside).Lands
            .Should().Equal(0, 2, 0, 1);

        // A type other than a panel still goes inside the selected tab's own panel, as any other container would.
        MockupPalette.Plan(session, [0, 2, 0], MockupComponent.Button, MockupPalettePlace.Auto).Lands
            .Should().Equal(0, 2, 0, 1);

        // Tabs ITSELF selected is unchanged: a panel still goes inside, as a new tab (the existing rule).
        MockupPalette.Plan(session, [0, 2], MockupComponent.Panel, MockupPalettePlace.Auto).Lands
            .Should().Equal(0, 2, 1);
    }

    [Fact]
    public void A_window_is_only_a_new_screen_and_is_the_one_type_offered_with_nothing_selected()
    {
        var session = Open();

        MockupPalette.Plan(session, null, MockupComponent.Window, MockupPalettePlace.Auto).Lands.Should().Equal(2);
        MockupPalette.Plan(session, [0, 1, 0], MockupComponent.Window, MockupPalettePlace.After).Lands.Should().Equal(1);
        MockupPalette.Plan(session, [1], MockupComponent.Window, MockupPalettePlace.Before).Lands.Should().Equal(1);
        MockupPalette.Plan(session, [0], MockupComponent.Window, MockupPalettePlace.Inside).Refusal
            .Should().Be("A window is a screen: it stands beside the other screens, never inside a node.");

        foreach (var other in MockupPalette.Components.Where(c => c != MockupComponent.Window))
        {
            MockupPalette.Plan(session, null, other, MockupPalettePlace.Auto).Refusal.Should().StartWith("Choose where");
        }
    }

    [Fact]
    public void A_dialog_goes_only_directly_in_a_window()
    {
        var session = Open();

        // Auto: last in the selection's screen, however deep the selection is.
        MockupPalette.Plan(session, [0, 1, 0, 0], MockupComponent.Dialog, MockupPalettePlace.Auto).Lands.Should().Equal(0, 7);
        MockupPalette.Plan(session, [0, 1], MockupComponent.Dialog, MockupPalettePlace.After).Lands.Should().Equal(0, 2);

        MockupPalette.Plan(session, [0, 1], MockupComponent.Dialog, MockupPalettePlace.Inside).Refusal
            .Should().StartWith("A dialog opens over its whole screen, so it goes directly in a window, not inside a panel.");
        MockupPalette.Plan(session, [0], MockupComponent.Dialog, MockupPalettePlace.After).Refusal
            .Should().StartWith("Only windows stand beside the screens.");
    }

    [Fact]
    public void The_editors_own_refusals_say_why()
    {
        var session = Open();

        MockupPalette.Plan(session, [0, 0, 0], MockupComponent.Text, MockupPalettePlace.Inside).Refusal
            .Should().Be("A button holds no children, so nothing goes inside it.");
        MockupPalette.Plan(session, [0, 2], MockupComponent.Text, MockupPalettePlace.Inside).Refusal
            .Should().StartWith("Tabs hold panels, one per tab");
        MockupPalette.Plan(session, [0, 2, 0], MockupComponent.Text, MockupPalettePlace.Before).Refusal
            .Should().StartWith("Tabs hold panels, one per tab");
    }

    [Fact]
    public void A_refusal_the_reader_makes_comes_back_in_its_words()
    {
        // 32 nested containers below the screen: the 32 levels the schema allows below it are all used.
        var node = new JsonObject { ["type"] = "container", ["children"] = new JsonArray() };
        for (var i = 0; i < 31; i++)
        {
            node = new JsonObject { ["type"] = "container", ["children"] = new JsonArray(node) };
        }

        var json = new JsonObject
        {
            ["mockup"] = 1,
            ["screens"] = new JsonArray(new JsonObject { ["type"] = "window", ["id"] = "s", ["children"] = new JsonArray(node) }),
        }.ToJsonString();
        var session = Open(json);
        var deepest = MockupOutline.Rows(session).MaxBy(r => r.Depth)!;
        deepest.Depth.Should().Be(33);

        var parent = deepest.Path.ToArray();
        var choice = MockupPalette.Plan(session, parent, MockupComponent.Container, MockupPalettePlace.Inside);

        choice.Offered.Should().BeFalse();
        var reader = MockupReader.Read(Inserted(json, parent)).Errors.Select(e => e.Message);
        choice.Refusal.Should().Be(string.Join(" ", reader));
    }

    [Fact]
    public void Types_that_need_an_id_get_a_fresh_one()
    {
        var session = Open();

        InsertedNode(MockupPalette.Plan(session, [0], MockupComponent.Window, MockupPalettePlace.After))["id"]!
            .GetValue<string>().Should().Be("screen-2");
        InsertedNode(MockupPalette.Plan(session, [0], MockupComponent.Dialog, MockupPalettePlace.Inside))["id"]!
            .GetValue<string>().Should().Be("dialog-2");

        var window = MockupPalette.Plan(session, [0], MockupComponent.Window, MockupPalettePlace.After);
        session.Apply(window.Edit!).Applied.Should().BeTrue();
        InsertedNode(MockupPalette.Plan(session, [0], MockupComponent.Window, MockupPalettePlace.After))["id"]!
            .GetValue<string>().Should().Be("screen-3");

        foreach (var plain in MockupPalette.Components.Where(c => c is not (MockupComponent.Window or MockupComponent.Dialog)))
        {
            MockupPalette.Node(session, plain, [0]).ContainsKey("id").Should().BeFalse(plain.ToString());
        }
    }

    [Fact]
    public void Each_insert_is_one_edit_that_one_undo_takes_back()
    {
        var session = Open();
        var opened = session.Json;

        session.Apply(MockupPalette.Plan(session, [0], MockupComponent.Table, MockupPalettePlace.Auto).Edit!).Applied.Should().BeTrue();
        session.Apply(MockupPalette.Plan(session, [0], MockupComponent.Tabs, MockupPalettePlace.Auto).Edit!).Applied.Should().BeTrue();
        session.IsDirty.Should().BeTrue();

        session.Undo().Should().BeTrue();
        session.Undo().Should().BeTrue();
        session.CanUndo.Should().BeFalse();
        session.Json.Should().Be(opened);
        session.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void Check_answers_as_apply_would_and_changes_nothing()
    {
        var session = Open();
        var before = session.Json;

        var bad = MockupEdit.Insert(MockupTarget.At(0), 0, new JsonObject { ["type"] = "button" });
        var outcome = session.Check(bad);

        outcome.Applied.Should().BeFalse();
        outcome.Refusal.Select(e => e.Message).Should().Equal(session.Apply(bad).Refusal.Select(e => e.Message));
        session.Json.Should().Be(before);
        session.CanUndo.Should().BeFalse();

        session.Check(MockupEdit.Insert(MockupTarget.At(0), 0, new JsonObject { ["type"] = "text", ["text"] = "x" })).Applied.Should().BeTrue();
        session.Json.Should().Be(before);
        session.CanUndo.Should().BeFalse();
    }

    private static JsonObject InsertedNode(MockupPaletteChoice choice)
    {
        choice.Offered.Should().BeTrue(choice.Refusal);

        // The edit carries its node, so it is applied to a fresh copy of the tree and read back from where it landed.
        var session = Open();
        session.Apply(choice.Edit!).Applied.Should().BeTrue();
        var root = JsonNode.Parse(session.Json)!.AsObject();
        return Descend(root["screens"]![choice.Lands![0]]!.AsObject(), choice.Lands.Skip(1));
    }

    private static JsonObject Descend(JsonObject node, IEnumerable<int> path) =>
        path.Aggregate(node, (at, i) => at["children"]![i]!.AsObject());

    /// <summary>The tree with an empty container added inside the node at <paramref name="parent"/>, written by hand.</summary>
    private static string Inserted(string json, int[] parent)
    {
        var root = JsonNode.Parse(json, documentOptions: new System.Text.Json.JsonDocumentOptions { MaxDepth = 256 })!.AsObject();
        var at = Descend(root["screens"]![parent[0]]!.AsObject(), parent.Skip(1));
        at["children"]!.AsArray().Add(new JsonObject { ["type"] = "container", ["children"] = new JsonArray() });
        return root.ToJsonString();
    }
}
