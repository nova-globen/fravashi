using System.Text.Json.Nodes;
using AwesomeAssertions;
using Fravashi.Application.Mockups;
using Xunit;

namespace Fravashi.Application.Tests.Mockups;

/// <summary>
/// #195: each edit operation, what it does and what refuses it. The tree is the team directory fixture's valid
/// attempt; its first screen holds a toolbar (search, add-person, refresh), the people table and the add
/// dialog (a form of four fields, the error text and a container of save-btn and cancel-btn). A refusal is the
/// READER's sentence wherever the reader is what objects, so the editor never words a rule a second time.
/// </summary>
public class MockupEditTests
{
    private static MockupEditSession Directory()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Mockups", "Fixtures", "team-directory.attempt-3.json"));
        MockupEditSession.TryOpen(json, out var session, out _).Should().BeTrue();
        session!.Errors.Should().BeEmpty();
        return session;
    }

    private static JsonObject Node(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static JsonObject Tree(MockupEditSession session) => (JsonObject)JsonNode.Parse(session.Json)!;

    private static JsonObject At(MockupEditSession session, params int[] path)
    {
        var node = (JsonObject)Tree(session)["screens"]![path[0]]!;
        foreach (var index in path.Skip(1))
        {
            node = (JsonObject)node["children"]![index]!;
        }

        return node;
    }

    private static void Refused(MockupEditSession session, MockupEdit edit, string sentence)
    {
        var before = session.Json;
        var outcome = session.Apply(edit);

        outcome.Applied.Should().BeFalse();
        outcome.Refusal.Select(e => e.Message).Should().Contain(m => m.Contains(sentence, StringComparison.Ordinal));
        session.Json.Should().Be(before, "a refused edit changes nothing");
        session.CanUndo.Should().BeFalse();
    }

    private static void Applied(MockupEditSession session, MockupEdit edit)
    {
        var outcome = session.Apply(edit);
        outcome.Refusal.Should().BeEmpty();
        outcome.Applied.Should().BeTrue();
    }

    [Fact]
    public void Insert_places_a_node_at_its_index_and_undo_takes_it_away()
    {
        var session = Directory();
        var before = session.Json;

        Applied(session, MockupEdit.Insert(MockupTarget.ById("add-form"), 0, Node("""{"type": "text", "text": "Who joins?"}""")));

        At(session, 0, 2, 0, 0)["text"]!.GetValue<string>().Should().Be("Who joins?");
        session.Document.Should().NotBeNull();
        session.Undo().Should().BeTrue();
        session.Json.Should().Be(before);
    }

    [Fact]
    public void Insert_into_a_leaf_that_had_no_children_and_its_undo_leave_no_empty_list_behind()
    {
        var session = Directory();
        var before = session.Json;

        // A panel in the profile tabs' first panel gets a container, which has no children list until now.
        Applied(session, MockupEdit.Insert(MockupTarget.At(1, 2, 0), 2, Node("""{"type": "container", "layout": "row"}""")));
        Applied(session, MockupEdit.Insert(MockupTarget.At(1, 2, 0, 2), 0, Node("""{"type": "text", "text": "Nested"}""")));

        session.Undo();
        session.Undo();
        session.Json.Should().Be(before);
    }

    [Fact]
    public void Insert_is_refused_in_the_readers_words_when_the_node_would_not_be_valid()
    {
        Refused(Directory(), MockupEdit.Insert(MockupTarget.ById("add-form"), 0, Node("""{"type": "field", "input": "text"}""")),
            "A field needs \"text\" (its visible label)");
        Refused(Directory(), MockupEdit.Insert(MockupTarget.ById("refresh"), 0, Node("""{"type": "text", "text": "x"}""")),
            "A button cannot carry \"children\"");
        Refused(Directory(), MockupEdit.Insert(MockupTarget.Screens, 0, Node("""{"type": "panel"}""")),
            "A screen is a window, and this is a panel.");
        Refused(Directory(), MockupEdit.Insert(MockupTarget.ById("profile-tabs"), 0, Node("""{"type": "button", "text": "x"}""")),
            "Tabs hold panels, one per tab, and this is a button.");
        Refused(Directory(), MockupEdit.Insert(MockupTarget.ById("add-form"), 0, Node("""{"type": "button", "id": "save-btn", "text": "x"}""")),
            "The id \"save-btn\" is already used at");
    }

    [Fact]
    public void Insert_is_refused_where_there_is_no_such_parent_or_position()
    {
        Refused(Directory(), MockupEdit.Insert(MockupTarget.ById("nope"), 0, Node("""{"type": "text", "text": "x"}""")),
            "There is no node with the id \"nope\".");
        Refused(Directory(), MockupEdit.Insert(MockupTarget.At(0, 9), 0, Node("""{"type": "text", "text": "x"}""")),
            "There is no node here.");
        Refused(Directory(), MockupEdit.Insert(MockupTarget.ById("add-form"), 7, Node("""{"type": "text", "text": "x"}""")),
            "There is no position 7 here: it holds 6 nodes, so a new one goes at 0 to 6.");
    }

    [Fact]
    public void A_screen_is_an_insert_into_the_screen_list_and_a_dialog_is_an_insert_of_a_dialog()
    {
        var session = Directory();

        Applied(session, MockupEdit.Insert(MockupTarget.Screens, 2, Node("""{"type": "window", "id": "settings"}""")));
        Applied(session, MockupEdit.Insert(MockupTarget.ById("settings"), 0, Node("""{"type": "dialog", "id": "confirm"}""")));

        session.Document!.Screens.Should().HaveCount(3);
        session.Document.Screens[2].Children![0].Type.Should().Be(MockupComponent.Dialog);
    }

    [Fact]
    public void Remove_is_refused_while_something_still_names_what_it_would_take_away()
    {
        Refused(Directory(), MockupEdit.Remove(MockupTarget.ById("add-dialog")), "There is no dialog with the id \"add-dialog\".");
        Refused(Directory(), MockupEdit.Remove(MockupTarget.ById("profile")), "There is no screen \"profile\".");
        Refused(Directory(), MockupEdit.Remove(MockupTarget.ById("save-btn")), "There is no node with the id \"save-btn\".");
    }

    [Fact]
    public void Remove_is_refused_for_the_last_screen_and_for_the_screen_list()
    {
        MockupEditSession.TryOpen("""{"mockup": 1, "screens": [{"type": "window", "id": "main"}]}""", out var session, out _);

        Refused(session!, MockupEdit.Remove(MockupTarget.At(0)), "\"screens\" is a list of at least one window; this is an empty list.");
        Refused(session!, MockupEdit.Remove(MockupTarget.Screens), "The screen list is not a node");
    }

    [Fact]
    public void Remove_takes_the_node_and_everything_under_it_and_undo_puts_it_back_where_it_was()
    {
        var session = Directory();
        var before = session.Json;

        Applied(session, MockupEdit.Remove(MockupTarget.ById("refresh")));
        Applied(session, MockupEdit.Remove(MockupTarget.At(1, 2, 1)));

        session.Json.Should().NotContain("\"refresh\"").And.NotContain("activity-list");
        session.Undo();
        session.Undo();
        session.Json.Should().Be(before);
    }

    [Fact]
    public void Move_reorders_indents_and_outdents_and_each_undoes_exactly()
    {
        var session = Directory();
        var before = session.Json;

        // Reorder: refresh to the front of the toolbar.
        Applied(session, MockupEdit.Move(MockupTarget.ById("refresh"), MockupTarget.At(0, 0), 0));
        At(session, 0, 0, 0)["id"]!.GetValue<string>().Should().Be("refresh");

        // Indent: the error text into the button row beside it.
        Applied(session, MockupEdit.Move(MockupTarget.ById("error-message"), MockupTarget.At(0, 2, 0, 5), 0));
        At(session, 0, 2, 0, 4, 0)["id"]!.GetValue<string>().Should().Be("error-message");

        // Outdent: cancel out of the row, to the end of the form.
        Applied(session, MockupEdit.Move(MockupTarget.ById("cancel-btn"), MockupTarget.ById("add-form"), 5));
        At(session, 0, 2, 0, 5)["id"]!.GetValue<string>().Should().Be("cancel-btn");

        // Across screens, from before the destination's ancestor: the toolbar into the form, whose path moves up
        // by one once the toolbar has left the window.
        Applied(session, MockupEdit.Move(MockupTarget.At(0, 0), MockupTarget.At(0, 2, 0), 0));
        At(session, 0, 1, 0, 0)["type"]!.GetValue<string>().Should().Be("toolbar");

        session.Document.Should().NotBeNull();
        while (session.Undo())
        {
        }

        session.Json.Should().Be(before);
        session.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void Moving_a_node_into_itself_or_anything_inside_it_is_refused()
    {
        Refused(Directory(), MockupEdit.Move(MockupTarget.ById("add-dialog"), MockupTarget.ById("add-dialog"), 0),
            "A node cannot move into itself or into anything inside it.");
        Refused(Directory(), MockupEdit.Move(MockupTarget.ById("add-dialog"), MockupTarget.At(0, 2, 0, 5), 0),
            "A node cannot move into itself or into anything inside it.");
        Refused(Directory(), MockupEdit.Move(MockupTarget.ById("directory"), MockupTarget.ById("add-form"), 0),
            "A node cannot move into itself or into anything inside it.");
    }

    [Fact]
    public void Move_is_refused_in_the_readers_words_and_where_there_is_no_such_position()
    {
        Refused(Directory(), MockupEdit.Move(MockupTarget.ById("search"), MockupTarget.ById("profile-tabs"), 0),
            "Tabs hold panels, one per tab, and this is a field.");
        Refused(Directory(), MockupEdit.Move(MockupTarget.ById("profile"), MockupTarget.ById("add-form"), 0),
            "A window is a screen and stands only at the top");
        Refused(Directory(), MockupEdit.Move(MockupTarget.ById("refresh"), MockupTarget.At(0, 0), 3),
            "There is no position 3 here once the node has moved out of its place: it holds 2 nodes");
    }

    [Fact]
    public void A_property_is_set_in_place_added_in_canonical_order_and_cleared()
    {
        var session = Directory();

        Applied(session, MockupEdit.SetProperty(MockupTarget.ById("search"), "required", JsonValue.Create(true)));
        Applied(session, MockupEdit.SetProperty(MockupTarget.ById("search"), "text", JsonValue.Create("Search")));
        Applied(session, MockupEdit.SetProperty(MockupTarget.ById("search"), "placeholder", null));

        At(session, 0, 0, 0).Select(p => p.Key).Should().Equal("type", "id", "text", "input", "required", "a11y");
    }

    [Fact]
    public void A_property_whose_value_is_itself_wrong_is_refused_in_the_readers_words()
    {
        Refused(Directory(), MockupEdit.SetProperty(MockupTarget.ById("refresh"), "cols", JsonValue.Create(2)),
            "A button cannot carry \"cols\"");
        Refused(Directory(), MockupEdit.SetProperty(MockupTarget.ById("people-table"), "data", JsonValue.Create("nobody")),
            "There is no data set \"nobody\". The data sets are \"activity\" or \"people\".");
        Refused(Directory(), MockupEdit.SetProperty(MockupTarget.ById("directory"), "layout", JsonValue.Create("diagonal")),
            "\"diagonal\" is not valid here. A layout is \"row\", \"column\" or \"grid\".");
        Refused(Directory(), MockupEdit.SetProperty(MockupTarget.ById("directory"), "cols", JsonValue.Create(3)),
            "\"cols\" applies to a grid layout; add \"layout\": \"grid\" or remove \"cols\".");
        Refused(Directory(), MockupEdit.SetProperty(MockupTarget.ById("refresh"), "colour", JsonValue.Create("red")),
            "\"colour\" is not a property of the mockup schema.");
    }

    [Fact]
    public void The_properties_with_an_edit_of_their_own_are_refused_as_plain_properties()
    {
        Refused(Directory(), MockupEdit.SetProperty(MockupTarget.ById("refresh"), "type", JsonValue.Create("text")),
            "A node's type is what it is");
        Refused(Directory(), MockupEdit.SetProperty(MockupTarget.ById("refresh"), "id", JsonValue.Create("again")),
            "An id is changed by renaming it");
        Refused(Directory(), MockupEdit.SetProperty(MockupTarget.ById("add-form"), "children", new JsonArray()),
            "Children are inserted, removed and moved one node at a time.");
        Refused(Directory(), MockupEdit.SetProperty(MockupTarget.ById("refresh"), "states", new JsonObject()),
            "\"states\" is changed one entry at a time.");
    }

    [Fact]
    public void A_property_edit_may_leave_the_node_incomplete_and_the_error_is_shown_not_hidden()
    {
        var session = Directory();

        Applied(session, MockupEdit.SetProperty(MockupTarget.ById("name-field"), "text", null));

        session.Document.Should().BeNull();
        session.Errors.Should().ContainSingle()
            .Which.Should().Be(new MockupError("$.screens[0].children[2].children[0].children[0]",
                "A field needs \"text\" (its visible label), or \"a11y\": {\"label\": \"...\"} when it shows none. "
                + "A placeholder is not a label: it disappears as soon as somebody types."));
        session.IsDirty.Should().BeTrue();

        Applied(session, MockupEdit.SetProperty(MockupTarget.ById("name-field"), "text", JsonValue.Create("Name")));
        session.Errors.Should().BeEmpty();
        session.IsDirty.Should().BeFalse("the tree is the one opened again");
    }

    [Fact]
    public void An_entry_is_set_in_its_maps_order_and_clearing_the_last_one_clears_the_map()
    {
        var session = Directory();
        var before = session.Json;

        Applied(session, MockupEdit.SetEntry(MockupTarget.ById("refresh"), MockupEntryMap.States, "loading", Node("""{}""")));
        Applied(session, MockupEdit.SetEntry(MockupTarget.ById("refresh"), MockupEntryMap.States, "disabled", Node("""{}""")));
        At(session, 0, 0, 2)["states"]!.AsObject().Select(p => p.Key).Should().Equal("disabled", "loading");

        Applied(session, MockupEdit.SetEntry(MockupTarget.ById("refresh"), MockupEntryMap.States, "disabled", null));
        Applied(session, MockupEdit.SetEntry(MockupTarget.ById("refresh"), MockupEntryMap.States, "loading", null));
        At(session, 0, 0, 2).ContainsKey("states").Should().BeFalse();
        session.Json.Should().Be(before);
    }

    [Fact]
    public void An_entry_whose_value_is_itself_wrong_is_refused_in_the_readers_words()
    {
        Refused(Directory(), MockupEdit.SetEntry(MockupTarget.ById("refresh"), MockupEntryMap.States, "visible", Node("""{}""")),
            "\"visible\" is not a state.");
        Refused(Directory(), MockupEdit.SetEntry(MockupTarget.ById("refresh"), MockupEntryMap.At, "tablet", Node("""{}""")),
            "\"tablet\" is not a declared viewport. The viewports are \"wide\" or \"narrow\".");
        Refused(Directory(), MockupEdit.SetEntry(MockupTarget.ById("refresh"), MockupEntryMap.On, "submit", Node("""{"go": "profile"}""")),
            "A button does not raise \"submit\"");
        Refused(Directory(), MockupEdit.SetEntry(MockupTarget.ById("refresh"), MockupEntryMap.On, "click", Node("""{"go": "nowhere"}""")),
            "There is no screen \"nowhere\".");
        Refused(Directory(), MockupEdit.SetEntry(MockupTarget.ById("refresh"), MockupEntryMap.States, "loading", Node("""{"id": "x"}""")),
            "An override changes how a node looks, not what it is, so it cannot carry \"id\".");
        Refused(Directory(), MockupEdit.SetEntry(MockupTarget.ById("person-photo"), MockupEntryMap.A11y, "decorative", JsonValue.Create(true)),
            "A node is either decorative or labelled, not both");
        Refused(Directory(), MockupEdit.SetEntry(MockupTarget.ById("person-name"), MockupEntryMap.A11y, "heading", JsonValue.Create(9)),
            "A heading level is a whole number from 1 to 6; this is the number 9.");
    }

    [Fact]
    public void Clearing_a_state_an_action_still_sets_is_allowed_and_the_dangling_action_is_shown()
    {
        var session = Directory();

        Applied(session, MockupEdit.SetEntry(MockupTarget.ById("save-btn"), MockupEntryMap.States, "loading", null));

        session.Errors.Should().ContainSingle().Which.Message.Should()
            .StartWith("The node \"save-btn\" (at $.screens[0].children[2].children[0].children[5].children[0]) does not describe the state \"loading\"");
    }

    [Fact]
    public void Renaming_an_id_renames_every_action_that_names_it_and_one_undo_puts_it_all_back()
    {
        var session = Directory();
        var before = session.Json;

        Applied(session, MockupEdit.RenameId(MockupTarget.ById("add-dialog"), "new-person"));
        Applied(session, MockupEdit.RenameId(MockupTarget.ById("save-btn"), "save"));
        Applied(session, MockupEdit.RenameId(MockupTarget.ById("profile"), "person"));

        session.Errors.Should().BeEmpty();
        At(session, 0, 0, 1)["on"]!["click"]!["open"]!.GetValue<string>().Should().Be("new-person");
        At(session, 0, 2, 0, 5, 1)["on"]!["click"]!["close"]!.GetValue<string>().Should().Be("new-person");
        At(session, 0, 2, 0)["on"]!["submit"]!["set"]!["save"]!.GetValue<string>().Should().Be("loading");
        At(session, 0, 1)["on"]!["select"]!["go"]!.GetValue<string>().Should().Be("person");

        session.Undo();
        session.Undo();
        session.Undo();
        session.Json.Should().Be(before);
    }

    [Fact]
    public void Giving_an_id_to_a_node_without_one_adds_it_after_the_type()
    {
        var session = Directory();

        Applied(session, MockupEdit.RenameId(MockupTarget.At(0, 0), "tools"));

        At(session, 0, 0).Select(p => p.Key).First(k => k != "type").Should().Be("id");
    }

    [Fact]
    public void A_rename_is_refused_in_the_readers_words()
    {
        Refused(Directory(), MockupEdit.RenameId(MockupTarget.ById("save-btn"), "cancel-btn"), "The id \"cancel-btn\" is already used at");
        Refused(Directory(), MockupEdit.RenameId(MockupTarget.ById("save-btn"), "9lives"),
            "An id \"9lives\" is not a valid name: it starts with a letter");
        Refused(Directory(), MockupEdit.RenameId(MockupTarget.ById("add-dialog"), null),
            "A dialog needs an \"id\", which is how \"open\" and \"close\" name it.");
        Refused(Directory(), MockupEdit.RenameId(MockupTarget.ById("profile"), null), "There is no screen \"profile\".");
    }

    [Fact]
    public void The_title_is_set_and_cleared()
    {
        var session = Directory();

        Applied(session, MockupEdit.SetTitle("Directory"));
        session.Document!.Title.Should().Be("Directory");
        Applied(session, MockupEdit.SetTitle(null));
        session.Document!.Title.Should().BeNull();
    }

    [Fact]
    public void A_viewport_is_declared_at_its_index_and_removed()
    {
        var session = Directory();
        var before = session.Json;

        Applied(session, MockupEdit.InsertViewport(1, "tablet", 820));
        session.Document!.Viewports.Select(v => v.Name).Should().Equal("wide", "tablet", "narrow");
        Applied(session, MockupEdit.RemoveViewport("tablet"));
        session.Json.Should().Be(before);
    }

    [Fact]
    public void A_viewport_edit_is_refused_in_the_readers_words()
    {
        Refused(Directory(), MockupEdit.RemoveViewport("narrow"), "\"narrow\" is not a declared viewport. The viewports are \"wide\".");
        Refused(Directory(), MockupEdit.RemoveViewport("tablet"), "There is no viewport \"tablet\".");
        Refused(Directory(), MockupEdit.InsertViewport(0, "wide", 1440), "The viewport \"wide\" is declared twice.");
        Refused(Directory(), MockupEdit.InsertViewport(0, "watch", 50),
            "A viewport width is a whole number of CSS pixels from 200 to 7680; this is the number 50.");
        Refused(Directory(), MockupEdit.InsertViewport(5, "tablet", 820), "There is no position 5 here: 2 viewports are declared");
    }

    [Fact]
    public void A_data_set_is_added_in_name_order_replaced_and_removed()
    {
        var session = Directory();
        var before = session.Json;

        Applied(session, MockupEdit.SetDataSet("teams", (JsonArray)JsonNode.Parse("""[{"name": "Platform"}]""")!));
        Applied(session, MockupEdit.SetDataSet("activity", (JsonArray)JsonNode.Parse("""[{"event": "Left"}]""")!));
        session.Document!.Data.Select(d => d.Name).Should().Equal("activity", "people", "teams");
        Applied(session, MockupEdit.SetDataSet("teams", null));
        Applied(session, MockupEdit.SetDataSet("activity", (JsonArray)JsonNode.Parse("""[{"event": "Joined the team"}, {"event": "Updated office"}]""")!));

        session.Json.Should().Be(before);
        session.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void A_data_set_edit_is_refused_in_the_readers_words()
    {
        Refused(Directory(), MockupEdit.SetDataSet("people", null), "There is no data set \"people\". The data sets are \"activity\".");
        Refused(Directory(), MockupEdit.SetDataSet("teams", null), "There is no data set \"teams\".");
        Refused(Directory(), MockupEdit.SetDataSet("activity", (JsonArray)JsonNode.Parse("""[{"what": "Left"}]""")!),
            "No record in \"activity\" has the key \"event\"");
        Refused(Directory(), MockupEdit.SetDataSet("1st", new JsonArray()), "The data set name \"1st\" is not a valid name");
        Refused(Directory(), MockupEdit.SetDataSet("teams", (JsonArray)JsonNode.Parse("""["Platform"]""")!),
            "A record is a JSON object");
    }
}
