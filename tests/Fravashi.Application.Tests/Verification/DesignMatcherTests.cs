using AwesomeAssertions;
using Fravashi.Application.Mockups;
using Fravashi.Application.Verification;
using Xunit;

namespace Fravashi.Application.Tests.Verification;

/// <summary>
/// Backlog 222 (plan @46): the matcher. The six recorded observations are the pages spike #56 checked (wiki @44), recorded
/// in the container with the image's Chromium as plan @46 section 4 says, and each must give the spike's verdict exactly.
/// The rule tests below them use small designs and hand-written snapshots, so each rule is seen on its own.
/// </summary>
public class DesignMatcherTests
{
    private const string Dialog = "add-dialog opened by Add person";

    private static string Text(params string[] path) => File.ReadAllText(Path.Combine([AppContext.BaseDirectory, .. path]));

    private static DesignScreen Screen(string json, int index = 0)
    {
        var read = MockupReader.Read(json);
        read.IsValid.Should().BeTrue(string.Join("; ", read.Errors));
        return DesignExpectations.For(read.Document!).Screens[index];
    }

    private static DesignMatchReport Recorded(string page) => DesignMatcher.Match(
        Screen(Text("Mockups", "Fixtures", "team-directory.attempt-3.json")),
        DesignObservationText.Parse(Text("Verification", "Observations", $"team-directory.{page}.observation.txt")));

    private static (DesignFindingKind, string?, string?, string, string, string?)[] Summary(DesignMatchReport report) =>
        report.Findings.Select(f => (f.Kind, f.Role, f.ExpectedName, f.Anchor, f.Phase, f.Viewport)).ToArray();

    // ---- the six pages of spike #56 ---------------------------------------------------------------

    [Fact]
    public void The_prototype_matches_its_own_design()
    {
        var report = Recorded("prototype");

        report.Matches.Should().BeTrue(string.Join("\n", report.Findings.Select(f => f.Sentence)));
        report.Views.Select(v => (v.Phase, v.Viewport, v.Outcome)).Should().Equal(
            (DesignExpectations.AtRest, "wide", DesignViewOutcome.Matched),
            (DesignExpectations.AtRest, "narrow", DesignViewOutcome.Matched),
            (Dialog, "wide", DesignViewOutcome.Matched),
            (Dialog, "narrow", DesignViewOutcome.Matched));
    }

    [Fact]
    public void The_faithful_build_in_another_look_matches()
    {
        var report = Recorded("faithful");

        report.Matches.Should().BeTrue(string.Join("\n", report.Findings.Select(f => f.Sentence)));
        report.Views.Should().HaveCount(4).And.OnlyContain(v => v.Outcome == DesignViewOutcome.Matched);
    }

    [Fact]
    public void Removing_the_office_field_is_missing_textbox_office()
    {
        var report = Recorded("office-removed");

        report.Matches.Should().BeFalse();
        Summary(report).Should().Equal(
            (DesignFindingKind.Missing, "textbox", "Office", "office-field", Dialog, "wide"),
            (DesignFindingKind.Missing, "textbox", "Office", "office-field", Dialog, "narrow"));
        report.Findings.First().Sentence.Should().Be(
            $"In \"{Dialog}\" at wide, the design's textbox \"Office\" (office-field) is missing.");
    }

    [Fact]
    public void Renaming_add_person_is_a_different_name_and_its_dialog_is_not_run()
    {
        var report = Recorded("renamed");

        var named = report.Findings.Where(f => f.Kind == DesignFindingKind.DifferentName).ToArray();
        named.Select(f => (f.Role, f.ExpectedName, f.Anchor, f.Observed, f.Phase, f.Viewport)).Should().Equal(
            ("button", "Add person", "add-person", "New person", DesignExpectations.AtRest, "wide"),
            ("button", "Add person", "add-person", "New person", DesignExpectations.AtRest, "narrow"));
        named[0].Sentence.Should().Be(
            "In \"at rest\" at wide, the design's button \"Add person\" (add-person) is named \"New person\".");

        var dialog = report.Views.Where(v => v.Phase == Dialog).ToArray();
        dialog.Should().HaveCount(2).And.OnlyContain(v => v.Outcome == DesignViewOutcome.NotRun);
        dialog[0].Findings.Should().OnlyContain(f => f.Kind == DesignFindingKind.NotRun);
        dialog[0].Findings.Select(f => f.Anchor).Should().Equal(
            "add-dialog", "name-field", "role-field", "office-field", "welcome-email", "save-btn", "cancel-btn");
        dialog[0].Findings[0].Sentence.Should().Be(
            $"In \"{Dialog}\" at wide, the design's dialog \"Add a person\" (add-dialog) was not checked: "
            + "button \"Add person\" (add-person) was not found.");

        report.Findings.Should().HaveCount(2 + 14, "nothing but the rename and the dialog it hides");
    }

    [Fact]
    public void Swapping_toolbar_and_table_puts_the_table_out_of_order()
    {
        var report = Recorded("swapped");

        var expected = new[] { "wide", "narrow" }.SelectMany(v => new[]
        {
            (DesignFindingKind.OutOfOrder, (string?)"table", (string?)null, "people-table", DesignExpectations.AtRest, (string?)v),
            (DesignFindingKind.OutOfOrder, "columnheader", "Name", "people-table", DesignExpectations.AtRest, v),
            (DesignFindingKind.OutOfOrder, "columnheader", "Role", "people-table", DesignExpectations.AtRest, v),
            (DesignFindingKind.OutOfOrder, "columnheader", "Office", "people-table", DesignExpectations.AtRest, v),
        });
        Summary(report).Should().Equal(expected);
        report.Findings.First().Sentence.Should().Be(
            "In \"at rest\" at wide, the design's table (people-table) is there, but before something the design puts ahead of it.");
    }

    [Fact]
    public void Drawing_add_person_in_its_background_colour_is_illegible()
    {
        var report = Recorded("invisible");

        Summary(report).Should().Equal(
            (DesignFindingKind.Illegible, "button", "Add person", "add-person", DesignExpectations.AtRest, "wide"),
            (DesignFindingKind.Illegible, "button", "Add person", "add-person", DesignExpectations.AtRest, "narrow"));
        report.Findings.Should().OnlyContain(f => f.Observed == "1.00");
        report.Findings.First().Sentence.Should().Be(
            "In \"at rest\" at wide, the design's button \"Add person\" (add-person) has text contrast 1.00:1, under the 3:1 floor.");
    }

    [Fact]
    public void Every_finding_carries_kind_role_name_anchor_phase_viewport_and_a_sentence()
    {
        foreach (var page in new[] { "office-removed", "renamed", "swapped", "invisible" })
        {
            Recorded(page).Findings.Should().NotBeEmpty().And.OnlyContain(f =>
                f.Anchor.Length > 0 && f.Phase.Length > 0 && f.Viewport != null && f.Role != null
                && f.Sentence.StartsWith("In \"", StringComparison.Ordinal) && f.Sentence.EndsWith('.'));
        }
    }

    // ---- the rules, one at a time -------------------------------------------------------------------

    /// <summary>Heading Home; a row of Add person and Refresh; the words Pick someone; a table with a Name column.</summary>
    private const string Small = """
        {"mockup": 1, "screens": [{"type": "window", "id": "s", "text": "Home", "children": [
          {"type": "toolbar", "id": "bar", "layout": "row", "children": [
            {"type": "button", "id": "add", "text": "Add person"},
            {"type": "button", "id": "refresh", "text": "Refresh"}
          ]},
          {"type": "text", "id": "note", "text": "Pick someone"},
          {"type": "table", "id": "people", "columns": [{"key": "name", "text": "Name"}]}
        ]}]}
        """;

    private static DesignMatchReport Check(string snapshot, string design = Small) =>
        DesignMatcher.Match(Screen(design), DesignObservationText.Parse("@phase at rest\n" + snapshot));

    private static string Page(string add = "Add person", string refresh = "Refresh") => $"""
        - heading "Home" [level=1]
        - button "{add}" [ref=e1]
        - button "{refresh}" [ref=e2]
        - paragraph: Pick someone
        - table:
          - row:
            - columnheader "Name"
        """;

    [Fact]
    public void The_small_page_matches()
    {
        var report = Check(Page());

        report.Matches.Should().BeTrue(string.Join("\n", report.Findings.Select(f => f.Sentence)));
        report.Views.Single().Viewport.Should().BeNull();
        report.Views.Single().Extras.Should().BeEmpty();
    }

    [Theory]
    [InlineData("add person")]
    [InlineData("ADD PERSON")]
    [InlineData("  Add \t  person ")]
    [InlineData("Add\nperson")]
    public void Names_differing_only_in_case_or_whitespace_match(string name)
    {
        var page = Page(add: name.Replace("\t", "\\t", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal));

        Check(page).Matches.Should().BeTrue();
    }

    [Theory]
    [InlineData("Add-person")]
    [InlineData("Add persons")]
    [InlineData("Addperson")]
    [InlineData("Add person.")]
    [InlineData("New person")]
    public void Any_other_difference_is_a_different_name(string name)
    {
        var report = Check(Page(add: name));

        report.Findings.Should().ContainSingle().Which.Should().Match<DesignFinding>(f =>
            f.Kind == DesignFindingKind.DifferentName && f.Anchor == "add" && f.ExpectedName == "Add person" && f.Observed == name);
    }

    [Fact]
    public void Fold_collapses_whitespace_and_case_and_nothing_else()
    {
        DesignMatcher.Fold("  Add \t\n PERSON ").Should().Be("ADD PERSON");
        DesignMatcher.Fold("Add-person").Should().Be("ADD-PERSON");
        DesignMatcher.SameName("Add person", "add   person").Should().BeTrue();
        DesignMatcher.SameName("Add person", "Add-person").Should().BeFalse();
        DesignMatcher.SameName("Add person", null).Should().BeFalse();
    }

    [Fact]
    public void Extra_elements_still_match_and_are_listed()
    {
        var report = Check("""
            - navigation:
              - link "Help" [ref=e9]
            - heading "Home" [level=1]
            - button "Add person"
            - button "Export"
            - button "Refresh"
            - paragraph: Pick someone
            - table:
              - row:
                - columnheader "Name"
                - columnheader "Email"
              - row:
                - cell "Ada"
            - button "Sign out"
            """);

        report.Matches.Should().BeTrue(string.Join("\n", report.Findings.Select(f => f.Sentence)));
        report.Views.Single().Outcome.Should().Be(DesignViewOutcome.Matched);
        report.Views.Single().Extras.Should().Equal(
            new DesignExtra("link", "Help", "e9"),
            new DesignExtra("button", "Export", null),
            new DesignExtra("columnheader", "Email", null),
            new DesignExtra("button", "Sign out", null));
    }

    [Fact]
    public void Order_is_a_subsequence_read_front_to_back()
    {
        var report = Check("""
            - heading "Home" [level=1]
            - button "Refresh"
            - button "Add person"
            - paragraph: Pick someone
            - table:
              - columnheader "Name"
            """);

        report.Findings.Select(f => (f.Kind, f.Anchor)).Should().Equal((DesignFindingKind.OutOfOrder, "refresh"));
    }

    [Fact]
    public void Out_of_order_outranks_a_different_name_in_its_place()
    {
        var report = Check("""
            - button "Refresh"
            - heading "Home" [level=1]
            - button "Add person"
            - button "Reload"
            - paragraph: Pick someone
            - table:
              - columnheader "Name"
            """);

        report.Findings.Select(f => (f.Kind, f.Anchor)).Should().Equal((DesignFindingKind.OutOfOrder, "refresh"));
        report.Views.Single().Extras.Should().Equal(new DesignExtra("button", "Reload", null));
    }

    [Fact]
    public void A_control_of_the_role_outside_its_place_is_not_a_rename_so_it_is_missing()
    {
        var report = Check("""
            - heading "Home" [level=1]
            - button "Add person"
            - paragraph: Pick someone
            - table:
              - columnheader "Name"
            - button "Sign out"
            """);

        report.Findings.Select(f => (f.Kind, f.Anchor)).Should().Equal((DesignFindingKind.Missing, "refresh"));
        report.Views.Single().Extras.Should().Equal(new DesignExtra("button", "Sign out", null));
    }

    [Fact]
    public void A_control_that_a_later_expectation_names_is_never_taken_as_a_rename()
    {
        const string design = """
            {"mockup": 1, "screens": [{"type": "window", "id": "s", "children": [
              {"type": "button", "id": "add", "text": "Add"},
              {"type": "button", "id": "refresh", "text": "Refresh"},
              {"type": "button", "id": "save", "text": "Save"}
            ]}]}
            """;
        var report = Check("""
            - button "Save"
            - button "Refresh"
            """, design);

        report.Findings.Select(f => (f.Kind, f.Anchor)).Should().Equal(
            (DesignFindingKind.Missing, "add"), (DesignFindingKind.OutOfOrder, "save"));
    }

    [Fact]
    public void Words_match_by_name_or_text_in_any_element_and_are_never_renamed()
    {
        Check(Page().Replace("- paragraph: Pick someone", "- text: pick   SOMEONE", StringComparison.Ordinal)).Matches.Should().BeTrue();
        Check(Page().Replace("- paragraph: Pick someone", "- generic \"Pick someone\"", StringComparison.Ordinal)).Matches.Should().BeTrue();

        var report = Check(Page().Replace("- paragraph: Pick someone", "- paragraph: Choose someone", StringComparison.Ordinal));
        report.Findings.Single().Should().Match<DesignFinding>(f =>
            f.Kind == DesignFindingKind.Missing && f.Role == null && f.ExpectedName == "Pick someone" && f.Anchor == "note");
        report.Findings.Single().Sentence.Should().Be("In \"at rest\", the design's text \"Pick someone\" (note) is missing.");
    }

    [Fact]
    public void A_column_header_outside_its_table_is_outside_its_container()
    {
        var report = Check("""
            - heading "Home" [level=1]
            - button "Add person"
            - button "Refresh"
            - paragraph: Pick someone
            - table:
              - row:
                - cell "Ada"
            - columnheader "Name"
            """);

        report.Findings.Should().ContainSingle().Which.Should().Match<DesignFinding>(f =>
            f.Kind == DesignFindingKind.OutsideItsContainer && f.Role == "columnheader" && f.ExpectedName == "Name"
            && f.Anchor == "people" && f.Part == DesignPart.ColumnHeader && f.Column == "name" && f.Observed == "people");
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(98, true)]
    [InlineData(97.9, false)]
    [InlineData(0, false)]
    public void A_row_is_left_to_right_within_two_pixels_of_slack(double refreshX, bool matches)
    {
        var report = Check(Page() + $"""

            @box e1 0 0 100 30
            @box e2 {refreshX.ToString(System.Globalization.CultureInfo.InvariantCulture)} 0 80 30
            """);

        if (matches)
        {
            report.Matches.Should().BeTrue();
            return;
        }

        report.Findings.Should().ContainSingle().Which.Should().Match<DesignFinding>(f =>
            f.Kind == DesignFindingKind.NotLaidOutAsDesigned && f.Anchor == "refresh" && f.Observed == "add");
        report.Findings.Single().Sentence.Should().Be(
            "In \"at rest\", the design's button \"Refresh\" (refresh) is not right of add as the design's row bar says.");
    }

    [Theory]
    [InlineData(28, true)]
    [InlineData(27, false)]
    public void A_column_is_top_to_bottom_within_two_pixels_of_slack(double secondY, bool matches)
    {
        const string design = """
            {"mockup": 1, "screens": [{"type": "window", "id": "s", "layout": "column", "children": [
              {"type": "container", "layout": "row", "children": [
                {"type": "button", "id": "a", "text": "A"},
                {"type": "button", "id": "b", "text": "B"}
              ]},
              {"type": "button", "id": "c", "text": "C"}
            ]}]}
            """;
        var report = Check($"""
            - button "A" [ref=e1]
            - button "B" [ref=e2]
            - button "C" [ref=e3]
            @box e1 0 0 50 30
            @box e2 60 0 50 30
            @box e3 0 {secondY.ToString(System.Globalization.CultureInfo.InvariantCulture)} 50 30
            """, design);

        if (matches)
        {
            report.Matches.Should().BeTrue();
            return;
        }

        report.Findings.Should().ContainSingle().Which.Should().Match<DesignFinding>(f =>
            f.Kind == DesignFindingKind.NotLaidOutAsDesigned && f.Anchor == "c" && f.Observed == "/screens/0/children/0");
    }

    [Fact]
    public void A_container_item_is_named_by_what_it_holds()
    {
        const string design = """
            {"mockup": 1, "screens": [{"type": "window", "id": "s", "layout": "column", "children": [
              {"type": "button", "id": "c", "text": "C"},
              {"type": "container", "id": "pair", "layout": "row", "children": [
                {"type": "button", "id": "a", "text": "A"},
                {"type": "button", "id": "b", "text": "B"}
              ]}
            ]}]}
            """;
        var report = Check("""
            - button "C" [ref=e3]
            - button "A" [ref=e1]
            - button "B" [ref=e2]
            @box e1 0 0 50 30
            @box e2 60 0 50 30
            @box e3 0 10 50 30
            """, design);

        report.Findings.Single().Should().Match<DesignFinding>(f =>
            f.Kind == DesignFindingKind.NotLaidOutAsDesigned && f.Anchor == "pair" && f.Role == "button" && f.ExpectedName == "A");
        report.Findings.Single().Sentence.Should().Be(
            "In \"at rest\", the design's pair (holding button \"A\" (a)) is not below c as the design's column s says.");
    }

    [Fact]
    public void An_item_not_found_in_order_is_not_placed()
    {
        var report = Check("""
            - heading "Home" [level=1]
            - button "Refresh" [ref=e2]
            - button "Add person" [ref=e1]
            - paragraph: Pick someone
            - table:
              - columnheader "Name"
            @box e1 100 0 100 30
            @box e2 0 0 80 30
            """);

        report.Findings.Select(f => f.Kind).Should().Equal(DesignFindingKind.OutOfOrder);
    }

    [Theory]
    [InlineData(2.99, true)]
    [InlineData(3.0, false)]
    public void A_button_or_heading_under_three_to_one_is_illegible(double ratio, bool illegible)
    {
        var shown = ratio.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var report = Check(Page().Replace("- heading \"Home\" [level=1]", "- heading \"Home\" [level=1] [ref=e5]", StringComparison.Ordinal)
            + $"\n@contrast e5 {shown}\n@contrast e1 {shown}\n");

        (DesignFindingKind, string)[] expected = illegible
            ? [(DesignFindingKind.Illegible, "s"), (DesignFindingKind.Illegible, "add")]
            : [];
        report.Findings.Select(f => (f.Kind, f.Anchor)).Should().Equal(expected);
    }

    private const string Opening = """
        {"mockup": 1, "screens": [{"type": "window", "id": "s", "children": [
          {"type": "button", "id": "open", "text": "Open", "on": {"click": {"open": "d"}}},
          {"type": "dialog", "id": "d", "text": "Details", "children": [{"type": "button", "id": "ok", "text": "OK"}]}
        ]}]}
        """;

    [Fact]
    public void A_phase_not_reached_is_not_run_never_passed_or_failed()
    {
        var report = DesignMatcher.Match(Screen(Opening), DesignObservationText.Parse("""
            @phase at rest
            - button "Open"
            @phase d opened by Open
            @not-reached the opener was not found
            """));

        var dialog = report.Views[1];
        dialog.Outcome.Should().Be(DesignViewOutcome.NotRun);
        dialog.Findings.Select(f => (f.Kind, f.Anchor, f.Observed)).Should().Equal(
            (DesignFindingKind.NotRun, "d", "the opener was not found"),
            (DesignFindingKind.NotRun, "ok", "the opener was not found"));
        report.Views[0].Outcome.Should().Be(DesignViewOutcome.Matched);
        report.Matches.Should().BeFalse();
    }

    [Fact]
    public void A_phase_with_no_observation_is_not_run()
    {
        var report = DesignMatcher.Match(Screen(Opening), DesignObservationText.Parse("@phase at rest\n- button \"Open\"\n"));

        report.Views[1].Outcome.Should().Be(DesignViewOutcome.NotRun);
        report.Views[1].Findings.Should().OnlyContain(f => f.Observed == "no observation was made of this phase");
    }

    [Fact]
    public void A_dialog_phase_lists_only_what_it_revealed_as_extra()
    {
        var report = DesignMatcher.Match(Screen(Opening), DesignObservationText.Parse("""
            @phase at rest
            - button "Open"
            - link "Home"
            @phase d opened by Open
            - button "Open"
            - link "Home"
            - dialog "Details":
              - heading "Details" [level=2]
              - button "OK"
              - button "Close"
            """));

        report.Matches.Should().BeTrue();
        report.Views[0].Extras.Should().Equal(new DesignExtra("link", "Home", null));
        report.Views[1].Extras.Should().Equal(new DesignExtra("heading", "Details", null), new DesignExtra("button", "Close", null));
    }

    // ---- links (backlog 290) --------------------------------------------------------------------------

    /// <summary>The studio's Not found page: its title, its words, and a link back to the start page.</summary>
    private const string NotFound = """
        {"mockup": 1, "screens": [{"type": "window", "id": "not-found", "text": "Not found", "children": [
          {"type": "text", "text": "Sorry, there's nothing at this address."},
          {"type": "link", "id": "go-home", "text": "Go to the start page", "href": "/"},
          {"type": "button", "id": "report", "text": "Report this"}
        ]}]}
        """;

    // A raw string takes the line endings of this file as it was checked out, and the tests below edit it with "\n":
    // on a checkout with core.autocrlf=true (GitHub's Windows runners) those edits found nothing, so it is normalised.
    private static string NotFoundPage(string home = "link", string report = "button") => $"""
        - heading "Not found" [level=1]
        - paragraph: Sorry, there's nothing at this address.
        - {home} "Go to the start page" [ref=e1]:
          - /url: /
        - {report} "Report this" [ref=e2]
        """.ReplaceLineEndings("\n");

    [Fact]
    public void A_designed_link_is_found_as_a_link_by_its_name()
    {
        var report = DesignMatcher.Match(Screen(NotFound), DesignObservationText.Parse("@phase at rest\n" + NotFoundPage()));

        report.Matches.Should().BeTrue(string.Join("\n", report.Findings.Select(f => f.Sentence)));
        report.Views.Single().Extras.Should().BeEmpty();
    }

    [Fact]
    public void A_link_built_as_a_button_is_a_different_role_naming_the_node()
    {
        var report = DesignMatcher.Match(Screen(NotFound), DesignObservationText.Parse("@phase at rest\n" + NotFoundPage(home: "button")));

        var finding = report.Findings.Should().ContainSingle().Subject;
        finding.Kind.Should().Be(DesignFindingKind.DifferentRole);
        (finding.Role, finding.ExpectedName, finding.Anchor, finding.Observed).Should().Be(
            ("link", "Go to the start page", "go-home", "button"));
        finding.Sentence.Should().Be(
            "In \"at rest\", the design's link \"Go to the start page\" (go-home) is there as a button, not a link.");
        DesignVerification.Say(finding).Should().StartWith("DIFFERENT ROLE: ");
        report.Views.Single().Extras.Should().BeEmpty("the button stood in for the link, so it is not an extra");
    }

    [Fact]
    public void A_button_built_as_a_link_is_a_different_role_naming_the_node()
    {
        var report = DesignMatcher.Match(Screen(NotFound), DesignObservationText.Parse("@phase at rest\n" + NotFoundPage(report: "link")));

        report.Findings.Should().ContainSingle().Which.Should().Match<DesignFinding>(f =>
            f.Kind == DesignFindingKind.DifferentRole && f.Role == "button" && f.Anchor == "report" && f.Observed == "link"
            && f.Sentence.EndsWith("is there as a link, not a button.", StringComparison.Ordinal));
    }

    [Fact]
    public void Nothing_of_the_links_name_anywhere_is_still_missing()
    {
        var page = NotFoundPage().Replace("- link \"Go to the start page\" [ref=e1]:\n  - /url: /\n", string.Empty, StringComparison.Ordinal);

        var report = DesignMatcher.Match(Screen(NotFound), DesignObservationText.Parse("@phase at rest\n" + page));

        report.Findings.Should().ContainSingle().Which.Kind.Should().Be(DesignFindingKind.Missing);
    }

    [Fact]
    public void Words_of_the_same_name_in_a_paragraph_are_not_a_different_role()
    {
        // Prose is not a role a person finds or acts on, so the same words as text do not stand in for the link.
        var page = NotFoundPage().Replace(
            "- link \"Go to the start page\" [ref=e1]:\n  - /url: /", "- paragraph: Go to the start page", StringComparison.Ordinal);

        var report = DesignMatcher.Match(Screen(NotFound), DesignObservationText.Parse("@phase at rest\n" + page));

        report.Findings.Should().ContainSingle().Which.Kind.Should().Be(DesignFindingKind.Missing);
    }
}
