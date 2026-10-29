using System.Text.Json.Nodes;
using AwesomeAssertions;
using Fravashi.Application.Mockups;
using Xunit;

namespace Fravashi.Application.Tests.Mockups;

/// <summary>
/// #199: the inspector's model for states, viewports, interactions, accessibility and sample data, and for editing an
/// override with slice 6's own changes. Each change a person makes is ONE edit that one undo takes back byte for byte;
/// removing a state or a viewport takes what names it in the same edit; removing what another node shows is refused in
/// the reader's words.
/// </summary>
public class MockupBehaviourTests
{
    private const string Tree = """
        {"mockup": 1,
         "viewports": [{"name": "wide", "width": 1280}, {"name": "narrow", "width": 390}],
         "data": {"people": [{"name": "Ada", "age": 36, "admin": true, "boss": null, "teams": [{"name": "Core"}]}]},
         "screens": [
          {"type": "window", "id": "home", "text": "Home", "layout": "row", "at": {"narrow": {"layout": "column"}}, "children": [
            {"type": "form", "id": "edit", "on": {"submit": {"set": {"save": "loading", "note": "error"}, "go": "done"}}, "children": [
              {"type": "field", "id": "name", "text": "Name", "required": true},
              {"type": "button", "id": "save", "text": "Save", "state": "loading",
               "states": {"disabled": {}, "loading": {"text": "Saving..."}}},
              {"type": "text", "id": "note", "text": "Oops", "states": {"error": {}}}]},
            {"type": "list", "id": "names", "data": "people", "show": "name"},
            {"type": "button", "id": "ask", "text": "Ask", "on": {"click": {"open": "confirm"}}},
            {"type": "image", "id": "logo", "a11y": {"decorative": true}},
            {"type": "dialog", "id": "confirm", "text": "Sure?", "children": []}]},
          {"type": "window", "id": "done", "text": "Done", "children": []}]}
        """;

    private static MockupEditSession Open(string json = Tree)
    {
        MockupEditSession.TryOpen(json, out var session, out var refusal).Should().BeTrue(string.Join("; ", refusal));
        session!.Errors.Should().BeEmpty();
        return session;
    }

    private static IReadOnlyList<int> PathOf(MockupEditSession session, string id) =>
        MockupOutline.Rows(session).Single(r => r.Id == id).Path;

    private static JsonObject NodeOf(MockupEditSession session, string id) =>
        (JsonObject)JsonNode.Parse(session.Json)!.AsObject()["screens"]!.AsArray()
            .SelectMany(Descend).Single(n => (string?)n["id"] == id);

    private static IEnumerable<JsonObject> Descend(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            yield break;
        }

        yield return obj;
        foreach (var child in (obj["children"] as JsonArray ?? []).SelectMany(Descend))
        {
            yield return child;
        }
    }

    /// <summary>Applies, checks the result is valid and says what it should, then one undo restores the bytes.</summary>
    private static void ApplyThenUndo(MockupEditSession session, MockupEdit? edit, Action<MockupEditSession> then)
    {
        edit.Should().NotBeNull();
        var before = session.Json;
        var outcome = session.Apply(edit!);
        outcome.Applied.Should().BeTrue(string.Join("; ", outcome.Refusal));
        session.Errors.Should().BeEmpty();
        then(session);
        session.Undo().Should().BeTrue();
        session.Json.Should().Be(before, "one undo takes back the whole change, so it was ONE edit");
    }

    // ---- the every-feature fixture ---------------------------------------------------------------------------------

    [Fact]
    public void The_every_feature_fixture_is_valid_and_uses_every_component_state_event_input_and_a11y_property()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Mockups", "Fixtures", "every-feature.json"));
        var read = MockupReader.Read(json);
        read.Errors.Should().BeEmpty();

        var nodes = JsonNode.Parse(json)!["screens"]!.AsArray().SelectMany(Descend).ToList();
        nodes.Select(n => (string)n["type"]!).Distinct().Should().BeEquivalentTo(MockupVocabulary.Names<MockupComponent>());
        nodes.SelectMany(n => (n["states"] as JsonObject ?? []).Select(p => p.Key)).Distinct()
            .Should().BeEquivalentTo(MockupVocabulary.Names<MockupState>());
        nodes.SelectMany(n => (n["on"] as JsonObject ?? []).Select(p => p.Key)).Distinct()
            .Should().BeEquivalentTo(MockupVocabulary.Names<MockupEvent>());
        nodes.SelectMany(n => (n["on"] as JsonObject ?? []).SelectMany(p => p.Value!.AsObject().Select(a => a.Key))).Distinct()
            .Should().BeEquivalentTo(MockupVocabulary.ActionProperties);
        nodes.Select(n => (string?)n["input"]).OfType<string>().Distinct()
            .Should().BeEquivalentTo(MockupVocabulary.Names<MockupInput>().Where(i => i != "text"), "text is the default and left unwritten");
        nodes.SelectMany(n => (n["a11y"] as JsonObject ?? []).Select(p => p.Key)).Distinct()
            .Should().BeEquivalentTo(MockupVocabulary.A11yProperties);
        nodes.Select(n => (string?)n["layout"]).OfType<string>().Distinct().Should().BeEquivalentTo(MockupVocabulary.Names<MockupLayout>());
        nodes.Should().Contain(n => n.ContainsKey("at")).And.Contain(n => n.ContainsKey("state")).And.Contain(n => n.ContainsKey("cols"));
    }

    // ---- reading ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_node_shows_its_states_overrides_actions_and_only_targets_that_exist()
    {
        var session = Open();

        var save = MockupBehaviour.Read(session, PathOf(session, "save"))!;
        save.States.Should().Equal("disabled", "loading");
        save.InitialState.Should().Be("loading");
        save.SetBy.Should().Contain("loading", 1);
        save.Events.Should().Equal("click");

        var form = MockupBehaviour.Read(session, PathOf(session, "edit"))!;
        form.Events.Should().ContainSingle().Which.Should().Be("submit", "a form raises submit and nothing else");
        form.Actions["submit"].Should().BeEquivalentTo(new MockupInspectorAction("done", null, null,
            [new MockupSetTarget("save", "loading"), new MockupSetTarget("note", "error")]));
        form.Screens.Should().Equal("home", "done");
        form.Dialogs.Should().Equal("confirm");
        form.Settable.Select(c => c.Id).Should().Equal("home", "edit", "name", "save", "note", "names", "ask", "logo", "confirm", "done");
        form.Settable.Single(c => c.Id == "save").States.Should().Equal("disabled", "loading");

        var home = MockupBehaviour.Read(session, PathOf(session, "home"))!;
        home.Viewports.Should().Equal("wide", "narrow");
        home.AtViewports.Should().Equal("narrow");
        home.Events.Should().BeEmpty("a window raises no event");

        MockupInspector.Read(session, PathOf(session, "logo"))!.Decorative.Should().BeTrue();
    }

    // ---- states ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Describing_a_state_is_one_edit_and_an_existing_one_is_no_change()
    {
        var session = Open();
        var save = PathOf(session, "save");

        MockupBehaviour.DescribeState(session, save, MockupState.Loading).Should().BeNull();
        ApplyThenUndo(session, MockupBehaviour.DescribeState(session, save, MockupState.Error),
            s => NodeOf(s, "save")["states"]!.AsObject().Select(p => p.Key).Should().Equal("disabled", "loading", "error"));
    }

    [Fact]
    public void Dropping_a_state_also_drops_the_initial_state_and_every_set_naming_it_in_one_edit()
    {
        var session = Open();
        ApplyThenUndo(session, MockupBehaviour.DropState(session, PathOf(session, "save"), "loading"), s =>
        {
            var save = NodeOf(s, "save");
            save["states"]!.AsObject().Select(p => p.Key).Should().Equal("disabled");
            save.ContainsKey("state").Should().BeFalse();
            var submit = NodeOf(s, "edit")["on"]!["submit"]!.AsObject();
            submit["set"]!.AsObject().Select(p => p.Key).Should().Equal("note");
            ((string?)submit["go"]).Should().Be("done");
        });
    }

    [Fact]
    public void Dropping_the_last_state_a_set_names_drops_an_action_left_empty_with_its_event()
    {
        var session = Open("""
            {"mockup": 1, "screens": [{"type": "window", "id": "home", "text": "Home", "children": [
              {"type": "button", "id": "go", "text": "Go", "on": {"click": {"set": {"spin": "loading"}}}},
              {"type": "text", "id": "spin", "text": "Wait", "states": {"loading": {}}}]}]}
            """);
        ApplyThenUndo(session, MockupBehaviour.DropState(session, PathOf(session, "spin"), "loading"), s =>
        {
            NodeOf(s, "spin").ContainsKey("states").Should().BeFalse("an empty states map says nothing");
            NodeOf(s, "go").ContainsKey("on").Should().BeFalse("an action with nothing to do is not written");
        });
    }

    [Fact]
    public void The_initial_state_is_one_of_the_described_ones_and_any_other_is_refused_in_the_readers_words()
    {
        var session = Open();
        var note = PathOf(session, "note");
        ApplyThenUndo(session, MockupBehaviour.SetInitialState(session, note, "error"),
            s => ((string?)NodeOf(s, "note")["state"]).Should().Be("error"));

        var refused = session.Apply(MockupBehaviour.SetInitialState(session, note, "loading")!);
        refused.Applied.Should().BeFalse();
        refused.Refusal.Should().ContainSingle().Which.Message.Should().Contain("does not describe it");
    }

    // ---- overrides, with slice 6's changes ---------------------------------------------------------------------

    [Fact]
    public void An_override_is_read_as_the_node_looks_there_and_offers_what_the_node_does_except_its_id()
    {
        var session = Open();
        var loading = MockupInspector.Read(session, PathOf(session, "save"), MockupOverrideScope.InState(MockupState.Loading))!;
        loading.Text.Should().Be("Saving...");
        loading.Overridden.Should().BeEquivalentTo(["text"]);
        loading.Offered.Should().Equal("text");

        var disabled = MockupInspector.Read(session, PathOf(session, "save"), MockupOverrideScope.InState(MockupState.Disabled))!;
        disabled.Text.Should().Be("Save", "an empty override shows the node as it is at rest");
        disabled.Overridden.Should().BeEmpty();

        var narrow = MockupInspector.Read(session, PathOf(session, "home"), MockupOverrideScope.AtViewport("narrow"))!;
        narrow.Layout.Should().Be("column");
        narrow.Offered.Should().Equal("text", "layout");
    }

    [Fact]
    public void A_change_in_an_override_is_one_entry_edit_and_leaves_the_node_at_rest_alone()
    {
        var session = Open();
        var save = PathOf(session, "save");
        var disabled = MockupOverrideScope.InState(MockupState.Disabled);

        ApplyThenUndo(session, MockupInspector.InOverride(session, save, disabled, (s, p) => MockupInspector.SetString(s, p, "text", "Wait")), s =>
        {
            ((string?)NodeOf(s, "save")["text"]).Should().Be("Save");
            NodeOf(s, "save")["states"]!["disabled"]!.ToJsonString().Should().Be("""{"text":"Wait"}""");
        });
    }

    [Fact]
    public void Clearing_in_an_override_means_as_at_rest_and_setting_the_value_at_rest_drops_it()
    {
        var session = Open();
        var save = PathOf(session, "save");
        var loading = MockupOverrideScope.InState(MockupState.Loading);

        ApplyThenUndo(session, MockupInspector.InOverride(session, save, loading, (s, p) => MockupInspector.SetString(s, p, "text", "")),
            s => NodeOf(s, "save")["states"]!["loading"]!.ToJsonString().Should().Be("{}"));
        ApplyThenUndo(session, MockupInspector.InOverride(session, save, loading, (s, p) => MockupInspector.SetString(s, p, "text", "Save")),
            s => NodeOf(s, "save")["states"]!["loading"]!.ToJsonString().Should().Be("{}"));
        MockupInspector.InOverride(session, save, loading, (s, p) => MockupInspector.SetString(s, p, "text", "Saving...")).Should().BeNull();
    }

    [Fact]
    public void Unticking_required_in_an_override_of_a_required_field_writes_false()
    {
        var session = Open();
        var name = PathOf(session, "name");
        session.Apply(MockupBehaviour.DescribeState(session, name, MockupState.Disabled)!).Applied.Should().BeTrue();

        ApplyThenUndo(session, MockupInspector.InOverride(session, name, MockupOverrideScope.InState(MockupState.Disabled),
                (s, p) => MockupInspector.SetRequired(s, p, false)),
            s => NodeOf(s, "name")["states"]!["disabled"]!.ToJsonString().Should().Be("""{"required":false}"""));
    }

    [Fact]
    public void A_viewport_override_takes_slice_6s_two_property_changes_as_one_entry()
    {
        var session = Open();
        var home = PathOf(session, "home");
        var narrow = MockupOverrideScope.AtViewport("narrow");

        ApplyThenUndo(session, MockupInspector.InOverride(session, home, narrow, (s, p) => MockupInspector.SetLayout(s, p, "grid")),
            s => NodeOf(s, "home")["at"]!["narrow"]!.ToJsonString().Should().Be("""{"layout":"grid"}"""));
    }

    [Fact]
    public void A_value_an_override_cannot_carry_is_refused_in_the_readers_words()
    {
        var session = Open();
        var refused = session.Apply(MockupInspector.InOverride(session, PathOf(session, "home"), MockupOverrideScope.AtViewport("narrow"),
            (s, p) => MockupInspector.SetCols(s, p, "3"))!);
        refused.Applied.Should().BeFalse();
        refused.Refusal.Should().ContainSingle().Which.Message.Should().Contain("grid");
    }

    [Fact]
    public void Accessibility_in_an_override_goes_through_the_same_changes()
    {
        var session = Open();
        var save = PathOf(session, "save");
        ApplyThenUndo(session, MockupInspector.InOverride(session, save, MockupOverrideScope.InState(MockupState.Disabled),
                (s, p) => MockupBehaviour.SetLabel(s, p, "Save (unavailable)")),
            s => NodeOf(s, "save")["states"]!["disabled"]!.ToJsonString().Should().Be("""{"a11y":{"label":"Save (unavailable)"}}"""));
    }

    [Fact]
    public void A_node_gains_and_loses_a_viewport_override_in_one_edit_each()
    {
        var session = Open();
        var ask = PathOf(session, "ask");
        ApplyThenUndo(session, MockupBehaviour.AddViewportOverride(session, ask, "narrow"),
            s => NodeOf(s, "ask")["at"]!.ToJsonString().Should().Be("""{"narrow":{}}"""));
        ApplyThenUndo(session, MockupBehaviour.DropViewportOverride(session, PathOf(session, "home"), "narrow"),
            s => NodeOf(s, "home").ContainsKey("at").Should().BeFalse());
        MockupBehaviour.DropViewportOverride(session, ask, "narrow").Should().BeNull();
    }

    // ---- interactions ------------------------------------------------------------------------------------------

    [Fact]
    public void An_action_is_written_whole_and_an_empty_one_takes_its_event_with_it()
    {
        var session = Open();
        var ask = PathOf(session, "ask");

        ApplyThenUndo(session, MockupBehaviour.SetAction(session, ask, "click",
                new MockupInspectorAction("done", null, "confirm", [new MockupSetTarget("save", "disabled")])),
            s => NodeOf(s, "ask")["on"]!.ToJsonString().Should().Be("""{"click":{"go":"done","close":"confirm","set":{"save":"disabled"}}}"""));
        ApplyThenUndo(session, MockupBehaviour.SetAction(session, ask, "click", MockupInspectorAction.None),
            s => NodeOf(s, "ask").ContainsKey("on").Should().BeFalse());
        MockupBehaviour.SetAction(session, ask, "click", new MockupInspectorAction(null, "confirm", null, [])).Should().BeNull();
    }

    [Fact]
    public void An_action_naming_a_state_its_node_does_not_describe_is_refused_in_the_readers_words()
    {
        var session = Open();
        var refused = session.Apply(MockupBehaviour.SetAction(session, PathOf(session, "ask"), "click",
            new MockupInspectorAction(null, null, null, [new MockupSetTarget("save", "error")]))!);
        refused.Applied.Should().BeFalse();
        refused.Refusal.Should().ContainSingle().Which.Message.Should().Contain("does not describe the state \"error\"");
    }

    [Fact]
    public void Removing_a_dialog_something_opens_stays_refused_naming_the_reference()
    {
        var session = Open();
        var step = MockupOutline.Plan(session, PathOf(session, "confirm"), MockupOutlineAction.Remove)!;
        var refused = session.Apply(step.Edit);
        refused.Applied.Should().BeFalse();
        refused.Refusal.Should().Contain(e => e.Path.EndsWith(".on.click.open", StringComparison.Ordinal) && e.Message.Contains("confirm"));
    }

    // ---- accessibility -----------------------------------------------------------------------------------------

    [Fact]
    public void Labelled_and_decorative_exclude_each_other_in_one_edit()
    {
        var session = Open();
        var logo = PathOf(session, "logo");
        ApplyThenUndo(session, MockupBehaviour.SetLabel(session, logo, "Our logo"),
            s => NodeOf(s, "logo")["a11y"]!.ToJsonString().Should().Be("""{"label":"Our logo"}"""));

        session.Apply(MockupBehaviour.SetLabel(session, logo, "Our logo")!).Applied.Should().BeTrue();
        ApplyThenUndo(session, MockupBehaviour.SetDecorative(session, logo, true),
            s => NodeOf(s, "logo")["a11y"]!.ToJsonString().Should().Be("""{"decorative":true}"""));
    }

    [Fact]
    public void Heading_and_live_are_one_edit_each_and_a_heading_out_of_range_is_refused()
    {
        var session = Open();
        var note = PathOf(session, "note");
        ApplyThenUndo(session, MockupBehaviour.SetHeading(session, note, "2"),
            s => NodeOf(s, "note")["a11y"]!.ToJsonString().Should().Be("""{"heading":2}"""));
        ApplyThenUndo(session, MockupBehaviour.SetLive(session, note, "polite"),
            s => NodeOf(s, "note")["a11y"]!.ToJsonString().Should().Be("""{"live":"polite"}"""));

        var refused = session.Apply(MockupBehaviour.SetHeading(session, note, "7")!);
        refused.Applied.Should().BeFalse();
        refused.Refusal.Should().ContainSingle().Which.Path.Should().EndWith(".a11y.heading");
    }

    // ---- the document's viewports ------------------------------------------------------------------------------

    [Fact]
    public void A_viewport_is_added_renamed_with_every_override_and_resized_in_one_edit_each()
    {
        var session = Open();
        ApplyThenUndo(session, MockupBehaviour.AddViewport(session),
            s => MockupBehaviour.Viewports(s).Select(v => $"{v.Name} {v.Width}").Should().Equal("wide 1280", "narrow 390", "medium 768"));
        ApplyThenUndo(session, MockupBehaviour.RenameViewport(session, "narrow", "phone"), s =>
        {
            MockupBehaviour.Viewports(s).Select(v => v.Name).Should().Equal("wide", "phone");
            NodeOf(s, "home")["at"]!.ToJsonString().Should().Be("""{"phone":{"layout":"column"}}""");
        });
        ApplyThenUndo(session, MockupBehaviour.ResizeViewport(session, "narrow", "360"),
            s => MockupBehaviour.Viewports(s).Select(v => $"{v.Name} {v.Width}").Should().Equal("wide 1280", "narrow 360"));
    }

    [Fact]
    public void A_bad_viewport_name_or_width_is_refused_in_the_readers_words()
    {
        var session = Open();
        session.Apply(MockupBehaviour.ResizeViewport(session, "narrow", "50")!).Refusal.Should().ContainSingle()
            .Which.Message.Should().Contain("200");
        session.Apply(MockupBehaviour.RenameViewport(session, "narrow", "wide")!).Applied.Should().BeFalse();
        session.Apply(MockupBehaviour.RenameViewport(session, "narrow", "9 lives")!).Applied.Should().BeFalse();
    }

    [Fact]
    public void Removing_a_viewport_removes_every_override_for_it_in_the_same_edit()
    {
        var session = Open();
        ApplyThenUndo(session, MockupBehaviour.RemoveViewport(session, "narrow"), s =>
        {
            MockupBehaviour.Viewports(s).Select(v => v.Name).Should().Equal("wide");
            NodeOf(s, "home").ContainsKey("at").Should().BeFalse();
        });
    }

    // ---- sample data -------------------------------------------------------------------------------------------

    [Fact]
    public void A_data_set_reads_as_a_table_with_every_kind_and_nested_records_marked()
    {
        var set = MockupBehaviour.DataSets(Open()).Single();
        set.Name.Should().Be("people");
        set.Keys.Should().Equal("admin", "age", "boss", "name", "teams");
        set.BoundBy.Should().Equal("names");
        var row = set.Records.Single();
        row["name"].Should().Be(new MockupDataCell("Ada", MockupDataCellKind.String));
        row["age"].Should().Be(new MockupDataCell("36", MockupDataCellKind.Number));
        row["admin"].Should().Be(new MockupDataCell("true", MockupDataCellKind.Boolean));
        row["boss"].Kind.Should().Be(MockupDataCellKind.Null);
        row["teams"].Should().Be(new MockupDataCell("1 nested record", MockupDataCellKind.Records));
    }

    [Fact]
    public void A_cell_keeps_its_kind_and_a_nested_list_is_not_edited_here()
    {
        var session = Open();
        ApplyThenUndo(session, MockupBehaviour.SetDataCell(session, "people", 0, "age", "37"),
            s => JsonNode.Parse(s.Json)!["data"]!["people"]![0]!["age"]!.ToJsonString().Should().Be("37"));
        ApplyThenUndo(session, MockupBehaviour.SetDataCell(session, "people", 0, "admin", "false"),
            s => JsonNode.Parse(s.Json)!["data"]!["people"]![0]!["admin"]!.ToJsonString().Should().Be("false"));
        ApplyThenUndo(session, MockupBehaviour.SetDataCell(session, "people", 0, "boss", "Lin"),
            s => JsonNode.Parse(s.Json)!["data"]!["people"]![0]!["boss"]!.ToJsonString().Should().Be("\"Lin\""));
        ApplyThenUndo(session, MockupBehaviour.SetDataCell(session, "people", 0, "name", "42"),
            s => JsonNode.Parse(s.Json)!["data"]!["people"]![0]!["name"]!.ToJsonString().Should().Be("\"42\""));
        MockupBehaviour.SetDataCell(session, "people", 0, "teams", "x").Should().BeNull();
    }

    [Fact]
    public void Records_and_keys_are_added_and_removed_in_one_edit_each()
    {
        var session = Open();
        ApplyThenUndo(session, MockupBehaviour.AddRecord(session, "people"),
            s => MockupBehaviour.DataSets(s).Single().Records.Should().HaveCount(2));
        ApplyThenUndo(session, MockupBehaviour.AddKey(session, "people"),
            s => MockupBehaviour.DataSets(s).Single().Keys.Should().Equal("admin", "age", "boss", "key", "name", "teams"));
        ApplyThenUndo(session, MockupBehaviour.RemoveKey(session, "people", "age"),
            s => MockupBehaviour.DataSets(s).Single().Keys.Should().NotContain("age"));
        ApplyThenUndo(session, MockupBehaviour.RenameKey(session, "people", "age", "years"),
            s => MockupBehaviour.DataSets(s).Single().Keys.Should().Equal("admin", "boss", "name", "teams", "years"));
    }

    // #207: the inspector shows a set's keys ordinally sorted, the same order before and after Save, agreeing with
    // #206's decision that this is the edit session's canonical order rather than a second notion of it.
    [Fact]
    public void A_renamed_keys_order_matches_what_save_actually_stores()
    {
        var session = Open();
        session.Apply(MockupBehaviour.RenameKey(session, "people", "age", "years")!).Applied.Should().BeTrue();

        var shown = MockupBehaviour.DataSets(session).Single().Keys;
        shown.Should().Equal("admin", "boss", "name", "teams", "years");

        var storedKeys = ((JsonObject)JsonNode.Parse(MockupWriter.Write(session.Document!))!["data"]!["people"]![0]!)
            .Select(p => p.Key);
        storedKeys.Should().Equal(shown);
    }

    // #207: a data set keeps its place among the others while it is renamed in this session, even to a name that
    // would sort before its neighbours (the exact shape of the bug: "sample2" renamed to "activity" jumped above
    // "people"). The stored form (MockupWriter) stays alphabetical regardless, since it is never read from here.
    [Fact]
    public void A_renamed_data_set_keeps_its_place_among_the_others()
    {
        var session = Open("""
            {"mockup": 1, "data": {"activity": [{"name": "A"}], "people": [{"name": "Ada"}], "sample2": [{"name": "Bo"}]},
             "screens": [{"type": "window", "id": "home", "text": "Home", "children": []}]}
            """);

        MockupBehaviour.DataSets(session).Select(d => d.Name).Should().Equal("activity", "people", "sample2");

        ApplyThenUndo(session, MockupBehaviour.RenameDataSet(session, "sample2", "zz"),
            s => MockupBehaviour.DataSets(s).Select(d => d.Name).Should().Equal("activity", "people", "zz"));

        // Renaming it to something that sorts BEFORE every other set still keeps its own place, third, not first.
        ApplyThenUndo(session, MockupBehaviour.RenameDataSet(session, "sample2", "aardvark"),
            s => MockupBehaviour.DataSets(s).Select(d => d.Name).Should().Equal("activity", "people", "aardvark"));

        // Renaming onto an existing set's name overwrites that set's records, keeping ITS place.
        ApplyThenUndo(session, MockupBehaviour.RenameDataSet(session, "sample2", "people"), s =>
        {
            MockupBehaviour.DataSets(s).Select(d => d.Name).Should().Equal("activity", "people");
            MockupBehaviour.DataSets(s).Single(d => d.Name == "people").Records.Single()["name"]
                .Should().Be(new MockupDataCell("Bo", MockupDataCellKind.String));
        });
    }

    [Fact]
    public void A_key_a_binding_uses_cannot_be_renamed_or_removed_and_the_reader_says_where()
    {
        var session = Open();
        var renamed = session.Apply(MockupBehaviour.RenameKey(session, "people", "name", "who")!);
        renamed.Applied.Should().BeFalse();
        renamed.Refusal.Should().NotBeEmpty();
        session.Apply(MockupBehaviour.RemoveKey(session, "people", "name")!).Applied.Should().BeFalse();
    }

    [Fact]
    public void A_data_set_is_added_renamed_with_its_bindings_and_a_bound_one_is_not_removed()
    {
        var session = Open();
        ApplyThenUndo(session, MockupBehaviour.AddDataSet(session),
            s => MockupBehaviour.DataSets(s).Select(d => d.Name).Should().Equal("people", "sample1"));
        ApplyThenUndo(session, MockupBehaviour.RenameDataSet(session, "people", "staff"), s =>
        {
            MockupBehaviour.DataSets(s).Select(d => d.Name).Should().Equal("staff");
            ((string?)NodeOf(s, "names")["data"]).Should().Be("staff");
        });

        var removed = session.Apply(MockupBehaviour.RemoveDataSet(session, "people")!);
        removed.Applied.Should().BeFalse();
        removed.Refusal.Should().Contain(e => e.Message.Contains("people"));
    }
}
