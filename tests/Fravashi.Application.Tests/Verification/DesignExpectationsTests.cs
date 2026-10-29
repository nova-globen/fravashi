using System.Text.Json;
using AwesomeAssertions;
using Fravashi.Application.Mockups;
using Fravashi.Application.Verification;
using Xunit;

namespace Fravashi.Application.Tests.Verification;

/// <summary>
/// Backlog 221 (plan @45): what a design asks a built page to contain. Each expectation below is stated by hand from the
/// fixture, so the test reads as the design does.
/// </summary>
public class DesignExpectationsTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Mockups", "Fixtures", name));

    /// <summary>
    /// The valid fixtures. team-directory.attempt-1 and attempt-2 are the trees an agent wrote and the reader REFUSED
    /// (that is what they record), so there is no design to expect anything of; <see cref="The_refused_attempts_are_not_designs"/>
    /// holds that, so a fixture that becomes valid joins this list rather than going unasserted.
    /// </summary>
    private static readonly string[] Valid = ["every-feature.json", "team-directory.attempt-3.json", "book-a-room-208-re-run.json"];

    public static TheoryData<string> Fixtures() => new(Valid);

    private static MockupDocument Read(string json)
    {
        var read = MockupReader.Read(json);
        read.IsValid.Should().BeTrue(string.Join("; ", read.Errors));
        return read.Document!;
    }

    private static DesignExpectationSet For(string fixture) => DesignExpectations.For(Read(Fixture(fixture)));

    private static (string? Role, string? Name)[] Asked(DesignView view) =>
        view.Expectations.Select(e => (e.Role, e.Name)).ToArray();

    private static DesignPhase Phase(DesignScreen screen, string name) => screen.Phases.Single(p => p.Name == name);

    // ---- a link (backlog 290) ---------------------------------------------------------------------------

    [Fact]
    public void A_link_is_expected_as_role_link_by_its_text_or_its_label_and_its_target_is_not_asked()
    {
        var screen = DesignExpectations.For(Read("""
            {"mockup": 1, "screens": [{"type": "window", "id": "not-found", "text": "Not found", "children": [
              {"type": "text", "text": "Sorry, there's nothing at this address."},
              {"type": "link", "id": "go-home", "text": "Go to the start page", "href": "/"},
              {"type": "link", "id": "help", "a11y": {"label": "Help"}, "href": "/help"}
            ]}]}
            """)).Screens[0];

        Asked(screen.Phases.Single().Views.Single()).Should().Equal(
            ("heading", "Not found"),
            (null, "Sorry, there's nothing at this address."),
            ("link", "Go to the start page"),
            ("link", "Help"));
        screen.Phases.Single().Views.Single().Expectations[2].Anchor.Should().Be("go-home");
    }

    [Fact]
    public void A_link_that_opens_a_dialog_is_the_step_that_reaches_it_by_role_link()
    {
        var screen = DesignExpectations.For(Read("""
            {"mockup": 1, "screens": [{"type": "window", "id": "w", "text": "W", "children": [
              {"type": "link", "id": "terms", "text": "Terms", "on": {"click": {"open": "terms-dialog"}}},
              {"type": "dialog", "id": "terms-dialog", "text": "Terms", "children": [{"type": "text", "text": "Be nice."}]}
            ]}]}
            """)).Screens[0];

        var step = screen.Phases[1].Steps.Single();
        (step.Action, step.Anchor, step.Role, step.Name).Should().Be((DesignStepAction.Open, "terms", "link", "Terms"));
    }

    // ---- team-directory.attempt-3 -------------------------------------------------------------------

    [Fact]
    public void Team_directory_at_rest_asks_the_heading_toolbar_and_table_with_its_column_headers_in_order()
    {
        var screen = For("team-directory.attempt-3.json").Screens[0];

        screen.Anchor.Should().Be("directory");
        screen.Phases[0].Name.Should().Be(DesignExpectations.AtRest);
        screen.Phases[0].Kind.Should().Be(DesignPhaseKind.AtRest);
        screen.Phases[0].Steps.Should().BeEmpty();
        Asked(screen.Phases[0].Views[0]).Should().Equal(
            ("heading", "Team directory"),
            ("searchbox", "Search by name"),
            ("button", "Add person"),
            ("button", "Refresh"),
            ("table", null),
            ("columnheader", "Name"),
            ("columnheader", "Role"),
            ("columnheader", "Office"));

        var headers = screen.Phases[0].Views[0].Expectations.Where(e => e.Part == DesignPart.ColumnHeader).ToArray();
        headers.Select(e => e.Column).Should().Equal("name", "role", "office");
        headers.Should().OnlyContain(e => e.Anchor == "people-table" && e.Within == "people-table");
        screen.Phases[0].Views[0].Expectations[0].Part.Should().Be(DesignPart.Title);
    }

    [Fact]
    public void Team_directory_add_dialog_is_its_own_phase_opened_by_add_person()
    {
        var screen = For("team-directory.attempt-3.json").Screens[0];

        screen.Phases.Select(p => p.Name).Should().Equal(DesignExpectations.AtRest, "add-dialog opened by Add person");
        var dialog = screen.Phases[1];
        dialog.Kind.Should().Be(DesignPhaseKind.Dialog);
        dialog.Target.Should().Be("add-dialog");
        dialog.Drivable.Should().BeTrue();
        dialog.Steps.Should().Equal(new DesignStep(DesignStepAction.Open, MockupEvent.Click, "add-person", "button", "Add person"));

        var view = dialog.Views[0];
        Asked(view).Should().Equal(
            ("dialog", "Add a person"),
            ("textbox", "Name"),
            ("combobox", "Role"),
            ("textbox", "Office"),
            ("checkbox", "Send a welcome email"),
            ("button", "Save"),
            ("button", "Cancel"));
        view.Expectations.Should().OnlyContain(e => e.Phase == "add-dialog opened by Add person");
        view.Expectations.Skip(1).Should().OnlyContain(e => e.Within == "add-dialog");
        view.Expectations[0].Within.Should().BeNull();
    }

    [Fact]
    public void Team_directory_dialog_contents_are_never_expected_at_rest()
    {
        var atRest = For("team-directory.attempt-3.json").Screens[0].Phases[0];

        foreach (var view in atRest.Views)
        {
            view.Expectations.Select(e => e.Anchor).Should().NotContain(
                ["add-dialog", "add-form", "name-field", "role-field", "office-field", "welcome-email", "save-btn", "cancel-btn"]);
        }
    }

    [Fact]
    public void Team_directory_toolbar_is_a_row_wide_and_a_column_narrow()
    {
        var atRest = For("team-directory.attempt-3.json").Screens[0].Phases[0];

        atRest.Views.Select(v => (v.Viewport, v.Width)).Should().Equal(("wide", 1280), ("narrow", 390));

        var wide = atRest.Views[0].Relations;
        var narrow = atRest.Views[1].Relations;
        wide.Select(r => (r.Container, r.Direction)).Should().Equal(
            ("directory", MockupLayout.Column), ("/screens/0/children/0", MockupLayout.Row));
        narrow.Select(r => (r.Container, r.Direction)).Should().Equal(
            ("directory", MockupLayout.Column), ("/screens/0/children/0", MockupLayout.Column));

        wide[0].Items.Select(i => i.Anchor).Should().Equal("/screens/0/children/0", "people-table");
        wide[0].Items[0].Members.Should().Equal("search", "add-person", "refresh");
        wide[0].Items[1].Members.Should().Equal("people-table");
        wide[1].Items.Select(i => i.Anchor).Should().Equal("search", "add-person", "refresh");
    }

    [Fact]
    public void Team_directory_dialog_phase_relates_its_fields_top_to_bottom_and_its_buttons_left_to_right()
    {
        var view = For("team-directory.attempt-3.json").Screens[0].Phases[1].Views[0];

        view.Relations.Select(r => (r.Container, r.Direction)).Should().Equal(
            ("add-form", MockupLayout.Column), ("/screens/0/children/2/children/0/children/5", MockupLayout.Row));
        view.Relations[0].Items.Select(i => i.Anchor).Should().Equal(
            "name-field", "role-field", "office-field", "welcome-email", "/screens/0/children/2/children/0/children/5");
        view.Relations[1].Items.Select(i => i.Anchor).Should().Equal("save-btn", "cancel-btn");
    }

    [Fact]
    public void Team_directory_profile_later_tab_with_no_id_is_named_for_the_tab_a_person_chose()
    {
        var screen = For("team-directory.attempt-3.json").Screens[1];
        const string activity = "/screens/1/children/2/children/1";

        Asked(screen.Phases[0].Views[0]).Should().Equal(
            ("heading", "Profile"),
            ("img", "Portrait of the team member"),
            ("heading", "Person Name"),
            ("tab", "Details"),
            ("tab", "Activity"),
            (null, "Role: Engineer"),
            (null, "Office: London"),
            ("button", "Back to directory"));

        // The Activity panel carries no id, so its phase is named "Activity tab" rather than by its tree pointer
        // (backlog 275); the tree pointer stays its target, since that is still what an anchor is for.
        var tab = Phase(screen, "Activity tab");
        tab.Kind.Should().Be(DesignPhaseKind.Tab);
        tab.Target.Should().Be(activity);
        tab.Steps.Should().Equal(new DesignStep(DesignStepAction.Choose, MockupEvent.Click, activity, "tab", "Activity"));
        Asked(tab.Views[0]).Should().Equal(("list", null));
        tab.Views[0].Expectations[0].Anchor.Should().Be("activity-list");
        screen.Phases[0].Views.SelectMany(v => v.Expectations).Select(e => e.Anchor).Should().NotContain("activity-list");
    }

    [Fact]
    public void A_later_tab_panel_with_an_id_keeps_todays_anchor_chosen_by_tab_name()
    {
        var document = Read("""
            {"mockup": 1, "screens": [{"type": "window", "id": "s", "children": [
              {"type": "tabs", "id": "pages", "children": [
                {"type": "panel", "text": "One", "children": []},
                {"type": "panel", "id": "settings-panel", "text": "Settings", "children": []}
              ]}
            ]}]}
            """);

        var screen = DesignExpectations.For(document).Screens[0];

        var tab = Phase(screen, "settings-panel chosen by Settings");
        tab.Kind.Should().Be(DesignPhaseKind.Tab);
        tab.Target.Should().Be("settings-panel");
        tab.Steps.Should().Equal(
            new DesignStep(DesignStepAction.Choose, MockupEvent.Click, "settings-panel", "tab", "Settings"));
    }

    [Fact]
    public void Two_id_less_tab_panels_that_share_a_tab_text_still_get_distinct_phase_names()
    {
        var document = Read("""
            {"mockup": 1, "screens": [{"type": "window", "id": "s", "children": [
              {"type": "tabs", "id": "first-tabs", "children": [
                {"type": "panel", "text": "One", "children": []},
                {"type": "panel", "text": "Details", "children": []}
              ]},
              {"type": "tabs", "id": "second-tabs", "children": [
                {"type": "panel", "text": "Two", "children": []},
                {"type": "panel", "text": "Details", "children": []}
              ]}
            ]}]}
            """);

        var tabPhases = DesignExpectations.For(document).Screens[0].Phases
            .Where(p => p.Kind == DesignPhaseKind.Tab).ToArray();

        // The first id-less "Details" panel reads as its tab; the second, which would otherwise collide with it,
        // falls back to today's anchor form, so DesignMatcher's exact-name lookup can still tell them apart.
        tabPhases.Select(p => p.Name).Should().OnlyHaveUniqueItems();
        tabPhases[0].Name.Should().Be("Details tab");
        tabPhases[1].Name.Should().Be($"{tabPhases[1].Target} chosen by Details");
        tabPhases[0].Target.Should().NotBe(tabPhases[1].Target);
    }

    // ---- every-feature --------------------------------------------------------------------------

    [Fact]
    public void Every_feature_at_rest_asks_each_input_by_its_role_with_number_a_spinbutton_and_radio_a_group()
    {
        var screen = For("every-feature.json").Screens[0];

        Asked(screen.Phases[0].Views[0]).Should().Equal(
            ("heading", "Home"),
            ("button", "Save"),
            ("button", "Help"),
            ("button", "Next"),
            (null, "Edit a person"),
            ("textbox", "Name"),
            ("textbox", "Password"),
            ("spinbutton", "Age"),
            ("textbox", "Email"),
            ("textbox", "Born"),
            ("searchbox", "Find a person"),
            ("textbox", "About"),
            ("combobox", "Role"),
            ("group", "Team"),
            ("checkbox", "I agree"),
            ("heading", "People"),
            ("list", null),
            ("list", null),
            ("tree", null),
            ("tree", null),
            ("table", null),
            ("columnheader", "name"),
            ("columnheader", "Age"),
            ("table", null),
            ("columnheader", "City"),
            ("tab", "One"),
            ("tab", "Two"),
            ("img", "A portrait of Ada"),
            (null, "Side"),
            ("button", "Close"));
    }

    [Fact]
    public void Every_feature_phases_are_at_rest_then_the_dialog_help_opens_then_the_later_tab()
    {
        var screen = For("every-feature.json").Screens[0];

        screen.Phases.Select(p => p.Name).Should().Equal(DesignExpectations.AtRest, "about opened by Help", "two chosen by Two");

        var about = screen.Phases[1];
        about.Steps.Should().Equal(new DesignStep(DesignStepAction.Open, MockupEvent.Click, "help", "button", "Help"));
        Asked(about.Views[0]).Should().Equal(("dialog", "About"), (null, "A design that uses everything."));
        about.Views[0].Expectations[1].Within.Should().Be("about");

        var two = screen.Phases[2];
        two.Steps.Should().Equal(new DesignStep(DesignStepAction.Choose, MockupEvent.Click, "two", "tab", "Two"));
        two.Views[0].Expectations.Should().BeEmpty();
    }

    [Fact]
    public void Every_feature_applies_at_per_viewport_to_text_and_layout()
    {
        var atRest = For("every-feature.json").Screens[0].Phases[0];
        var wide = atRest.Views[0];
        var narrow = atRest.Views[1];

        wide.Expectations.Single(e => e.Anchor == "side").Name.Should().Be("Side");
        narrow.Expectations.Single(e => e.Anchor == "side").Name.Should().Be("Side (narrow)");

        // home is a grid at base (no relation) and a column at narrow.
        wide.Relations.Select(r => r.Container).Should().NotContain("home");
        narrow.Relations[0].Container.Should().Be("home");
        narrow.Relations[0].Direction.Should().Be(MockupLayout.Column);
        wide.Relations.Select(r => (r.Container, r.Direction)).Should().Equal(
            ("bar", MockupLayout.Row), ("edit", MockupLayout.Column));

        narrow.Expectations.Where(e => e.Anchor == "inline-table" && e.Part == DesignPart.ColumnHeader)
            .Select(e => e.Name).Should().Equal("City");
    }

    [Fact]
    public void A_viewport_that_drops_a_column_drops_its_header_at_that_viewport_only()
    {
        var document = Read("""
            {"mockup": 1, "viewports": [{"name": "wide", "width": 1280}, {"name": "narrow", "width": 390}],
             "screens": [{"type": "window", "id": "s", "children": [
               {"type": "table", "id": "t", "columns": [{"key": "a", "text": "A"}, {"key": "b", "text": "B"}],
                "at": {"narrow": {"columns": ["a"]}}}
             ]}]}
            """);

        var views = DesignExpectations.For(document).Screens[0].Phases[0].Views;

        views[0].Expectations.Where(e => e.Part == DesignPart.ColumnHeader).Select(e => e.Name).Should().Equal("A", "B");
        views[1].Expectations.Where(e => e.Part == DesignPart.ColumnHeader).Select(e => e.Name).Should().Equal("A");
    }

    // ---- what is never expected -----------------------------------------------------------------

    [Fact]
    public void Sample_data_is_never_expected()
    {
        var samples = new[]
        {
            // every-feature: data sets, items, rows, a field's value and options.
            "Ada", "Lin", "36", "29", "Docs", "Plans", "Notes", "Trash", "London", "Turin", "Admin", "Member", "Core", "Edge",
            // team-directory: its people, its activity, its select's options.
            "Ada Lovelace", "Grace Hopper", "Engineer", "Researcher", "Manager", "Arlington", "Joined the team", "Updated office",
        };

        foreach (var fixture in new[] { "every-feature.json", "team-directory.attempt-3.json" })
        {
            var names = For(fixture).Screens.SelectMany(s => s.Phases).SelectMany(p => p.Views)
                .SelectMany(v => v.Expectations).Select(e => e.Name);
            names.Should().NotContain(samples, fixture);
        }
    }

    [Fact]
    public void A_node_hidden_at_rest_is_never_expected_in_any_phase()
    {
        var everyFeature = For("every-feature.json").Screens.SelectMany(s => s.Phases).SelectMany(p => p.Views)
            .SelectMany(v => v.Expectations).ToArray();
        everyFeature.Select(e => e.Anchor).Should().NotContain("status");
        everyFeature.Select(e => e.Name).Should().NotContain(["All saved", "Could not save"]);

        var teamDirectory = For("team-directory.attempt-3.json").Screens.SelectMany(s => s.Phases).SelectMany(p => p.Views)
            .SelectMany(v => v.Expectations).ToArray();
        teamDirectory.Select(e => e.Anchor).Should().NotContain("error-message");
        teamDirectory.Select(e => e.Name).Should().NotContain("Could not save this person.");
    }

    [Fact]
    public void A_decorative_image_is_never_expected()
    {
        For("every-feature.json").Screens[0].Phases[0].Views[0].Expectations.Select(e => e.Anchor).Should().NotContain("logo");
    }

    [Fact]
    public void A_starting_state_gives_its_text_and_a_hidden_subtree_hides_everything_in_it()
    {
        var document = Read("""
            {"mockup": 1, "screens": [{"type": "window", "id": "s", "children": [
              {"type": "button", "id": "go", "text": "Go", "state": "loading", "states": {"loading": {"text": "Going..."}}},
              {"type": "panel", "id": "secret", "state": "hidden", "states": {"hidden": {}}, "children": [
                {"type": "button", "id": "inside", "text": "Inside"}
              ]}
            ]}]}
            """);

        var asked = DesignExpectations.For(document).Screens[0].Phases[0].Views[0];

        asked.Viewport.Should().BeNull();
        Asked(asked).Should().Equal(("button", "Going..."));
    }

    // ---- reaching phases ----------------------------------------------------------------------------

    [Fact]
    public void Book_a_room_dialog_is_opened_by_choosing_a_row_of_its_list()
    {
        var screen = For("book-a-room-208-re-run.json").Screens[0];

        screen.Phases.Select(p => p.Name).Should().Equal(DesignExpectations.AtRest, "bookingDialog opened by roomList");
        screen.Phases[1].Steps.Should().Equal(new DesignStep(DesignStepAction.Open, MockupEvent.Select, "roomList", "list", null));
        Asked(screen.Phases[1].Views[0]).Should().Equal(
            ("dialog", "Book Fjord"),
            ("textbox", "Start time"),
            ("combobox", "Duration"),
            ("textbox", "Notes"),
            ("button", "Book"),
            ("button", "Cancel"));
    }

    [Fact]
    public void A_dialog_opened_from_inside_another_dialog_chains_both_steps()
    {
        var document = Read("""
            {"mockup": 1, "screens": [{"type": "window", "id": "s", "children": [
              {"type": "button", "id": "first", "text": "First", "on": {"click": {"open": "outer"}}},
              {"type": "dialog", "id": "outer", "text": "Outer", "children": [
                {"type": "button", "id": "second", "text": "Second", "on": {"click": {"open": "inner"}}},
                {"type": "dialog", "id": "inner", "text": "Inner"}
              ]}
            ]}]}
            """);

        var phases = DesignExpectations.For(document).Screens[0].Phases;

        phases.Select(p => p.Name).Should().Equal(DesignExpectations.AtRest, "outer opened by First", "inner opened by Second");
        phases[2].Steps.Select(s => s.Anchor).Should().Equal("first", "second");
        Asked(phases[1].Views[0]).Should().Equal(("dialog", "Outer"), ("button", "Second"));
        Asked(phases[2].Views[0]).Should().Equal(("dialog", "Inner"));
    }

    [Fact]
    public void A_dialog_nothing_on_its_screen_opens_is_listed_as_not_drivable()
    {
        var document = Read("""
            {"mockup": 1, "screens": [
              {"type": "window", "id": "a", "children": [{"type": "dialog", "id": "lonely", "text": "Lonely"}]},
              {"type": "window", "id": "b", "children": [
                {"type": "button", "id": "opener", "text": "Open", "on": {"click": {"open": "lonely"}}}
              ]}
            ]}
            """);

        var phase = DesignExpectations.For(document).Screens[0].Phases[1];

        phase.Drivable.Should().BeFalse();
        phase.Steps.Should().BeEmpty();
        phase.Target.Should().Be("lonely");
        Asked(phase.Views[0]).Should().Equal(("dialog", "Lonely"));
    }

    [Fact]
    public void A_dialog_nothing_opens_is_expected_at_rest_as_the_renderer_draws_it_open()
    {
        var phases = For("every-feature.json").Screens[1].Phases;

        phases.Should().ContainSingle();
        Asked(phases[0].Views[0]).Should().Equal(("heading", "Details"), ("button", "Back"), ("link", "Read the docs"));
    }

    // ---- unchecked overrides, determinism, every fixture -------------------------------------------

    [Fact]
    public void An_override_no_phase_applies_is_listed_not_dropped()
    {
        var document = Read("""
            {"mockup": 1, "viewports": [{"name": "narrow", "width": 390}],
             "screens": [{"type": "window", "id": "s", "children": [
               {"type": "field", "id": "f", "text": "F", "at": {"narrow": {"input": "search", "a11y": {"label": "Find"}}}},
               {"type": "button", "id": "b", "text": "B", "state": "error", "states": {"error": {"a11y": {"label": "Broken"}}}}
             ]}]}
            """);

        var set = DesignExpectations.For(document);

        set.Unchecked.Should().Equal(
            new DesignUnchecked("f", "narrow", null, "input"),
            new DesignUnchecked("f", "narrow", null, "a11y"),
            new DesignUnchecked("b", null, "error", "a11y"));
        Asked(set.Screens[0].Phases[0].Views[0]).Should().Equal(("textbox", "F"), ("button", "B"));
    }

    [Fact]
    public void The_fixtures_need_no_unchecked_override()
    {
        For("every-feature.json").Unchecked.Should().BeEmpty();
        For("team-directory.attempt-3.json").Unchecked.Should().BeEmpty();
    }

    [Theory]
    [InlineData("team-directory.attempt-1.json")]
    [InlineData("team-directory.attempt-2.json")]
    public void The_refused_attempts_are_not_designs(string fixture) =>
        MockupReader.Read(Fixture(fixture)).IsValid.Should().BeFalse();

    [Fact]
    public void Every_fixture_in_the_folder_is_asserted()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Mockups", "Fixtures");
        string[] refused = ["team-directory.attempt-1.json", "team-directory.attempt-2.json"];

        Directory.GetFiles(folder, "*.json").Select(Path.GetFileName).Should().BeEquivalentTo(Valid.Concat(refused));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Every_fixture_gives_the_same_expectations_every_time(string fixture)
    {
        var first = JsonSerializer.Serialize(DesignExpectations.For(Read(Fixture(fixture))));
        var second = JsonSerializer.Serialize(DesignExpectations.For(Read(Fixture(fixture))));

        second.Should().Be(first);
        var set = DesignExpectations.For(Read(Fixture(fixture)));
        set.Screens.Should().NotBeEmpty();
        set.Screens.Should().OnlyContain(s => s.Phases.Count > 0 && s.Phases[0].Name == DesignExpectations.AtRest);
        set.Screens.SelectMany(s => s.Phases).SelectMany(p => p.Views).SelectMany(v => v.Expectations)
            .Should().NotBeEmpty();
    }
}
