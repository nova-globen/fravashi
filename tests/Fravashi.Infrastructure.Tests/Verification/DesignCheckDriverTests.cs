using System.Text;
using AwesomeAssertions;
using Fravashi.Application.Mockups;
using Fravashi.Application.Verification;
using Fravashi.Infrastructure.Verification;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Verification;

/// <summary>
/// Backlog 223: the design check's drive, against a scripted page with no browser. The live tier
/// (<see cref="DesignCheckLiveTests"/>) shows the same drive against a real Chromium.
/// </summary>
public sealed class DesignCheckDriverTests
{
    private const string Spent = "the budget ran out";

    private static string Text(params string[] path) => File.ReadAllText(Path.Combine([AppContext.BaseDirectory, .. path]));

    private static DesignScreen TeamDirectory()
    {
        var read = MockupReader.Read(Text("Mockups", "Fixtures", "team-directory.attempt-3.json"));
        read.IsValid.Should().BeTrue(string.Join("; ", read.Errors));
        return DesignExpectations.For(read.Document!).Screens[0];
    }

    private static Task<DesignDrive> Drive(IDesignPage page, DesignScreen screen, CancellationToken budget = default) =>
        DesignCheckDriver.DriveAsync(page, screen, budget, Spent);

    // ---- replaying backlog 222's recordings ----------------------------------------------------------------

    /// <summary>
    /// A scripted page that shows exactly what #222 recorded for one page: each recorded snapshot as a state, its boxes
    /// and contrasts as probes, and a click from rest leading to the recorded dialog.
    /// </summary>
    internal static ScriptedDesignPage Replay(string page)
    {
        var scripted = new ScriptedDesignPage();
        var widths = new Dictionary<int, string>();
        var boxes = new Dictionary<string, ObservedBox>(StringComparer.Ordinal);
        var contrasts = new Dictionary<string, double>(StringComparer.Ordinal);
        var snapshot = new StringBuilder();
        string? state = null;
        string? phase = null;

        void Flush()
        {
            if (state is not null && snapshot.Length > 0)
            {
                scripted.Snapshot(state, snapshot.ToString());
            }

            snapshot.Clear();
        }

        foreach (var line in Text("Verification", "Observations", $"team-directory.{page}.observation.txt").Split('\n'))
        {
            var parts = line.TrimEnd('\r').Split(' ');
            if (!line.StartsWith('@'))
            {
                snapshot.Append(line.TrimEnd('\r')).Append('\n');
                continue;
            }

            switch (parts[0])
            {
                case "@phase":
                    Flush();
                    phase = line[7..].TrimEnd('\r');
                    break;
                case "@viewport":
                    widths[int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture)] = parts[1];
                    state = parts[1] + "|" + phase;
                    break;
                case "@box":
                    var n = parts[2..].Select(p => double.Parse(p, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    boxes[state + "|" + parts[1]] = new ObservedBox(n[0], n[1], n[2], n[3]);
                    break;
                case "@contrast":
                    contrasts[state + "|" + parts[1]] = double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                default:
                    // @screen and @not-reached: the drive works those out for itself.
                    break;
            }
        }

        Flush();
        foreach (var key in boxes.Keys.Union(contrasts.Keys))
        {
            var at = key.LastIndexOf('|');
            scripted.Probe(key[..at], key[(at + 1)..], new DesignProbe(
                boxes.GetValueOrDefault(key), contrasts.TryGetValue(key, out var ratio) ? ratio : null, null));
        }

        scripted.StartAt = width => widths[width] + "|" + DesignExpectations.AtRest;
        scripted.MoveWith = (from, _) => from.EndsWith("|" + DesignExpectations.AtRest, StringComparison.Ordinal)
            ? from.Split('|')[0] + "|add-dialog opened by Add person"
            : null;
        return scripted;
    }

    public static TheoryData<string> Recorded() => new(["prototype", "faithful", "office-removed", "renamed", "swapped", "invisible"]);

    [Theory]
    [MemberData(nameof(Recorded))]
    public async Task Driving_a_page_that_shows_what_222_recorded_gives_the_report_the_recording_gives(string page)
    {
        var screen = TeamDirectory();
        var recorded = DesignMatcher.Match(
            screen, DesignObservationText.Parse(Text("Verification", "Observations", $"team-directory.{page}.observation.txt")));

        var drive = await Drive(Replay(page), screen);
        var driven = DesignMatcher.Match(screen, drive.Observation);

        driven.Matches.Should().Be(recorded.Matches);
        driven.Views.Select(v => (v.Phase, v.Viewport, v.Outcome)).Should().Equal(recorded.Views.Select(v => (v.Phase, v.Viewport, v.Outcome)));
        driven.Findings.Should().Equal(recorded.Findings);
        driven.Views.SelectMany(v => v.Extras).Select(e => (e.Role, e.Name))
            .Should().Equal(recorded.Views.SelectMany(v => v.Extras).Select(e => (e.Role, e.Name)));
    }

    [Fact]
    public async Task A_missing_opener_is_not_reached_in_the_recordings_own_words_and_the_drive_carries_on()
    {
        var drive = await Drive(Replay("renamed"), TeamDirectory());

        var dialogs = drive.Observation.Phases.Where(p => p.Phase == "add-dialog opened by Add person").ToArray();
        dialogs.Should().HaveCount(2, "both viewports were still attempted after the first was not reached");
        dialogs.Should().AllSatisfy(p =>
        {
            p.Nodes.Should().BeNull();
            p.NotReached.Should().Be("button \"Add person\" (add-person) was not found");
        });
        drive.Observation.Phases.Where(p => p.Phase == DesignExpectations.AtRest).Should().AllSatisfy(p => p.Nodes.Should().NotBeNull());
    }

    // ---- the drive's shape -----------------------------------------------------------------------------------

    private static DesignView View(params DesignExpectation[] expectations) => new("wide", 1280, expectations, []);

    private static DesignPhase Rest(params DesignExpectation[] expectations) =>
        new(DesignExpectations.AtRest, DesignPhaseKind.AtRest, null, [], true, [View(expectations)]);

    private static DesignPhase Dialog(string name, DesignStep step, params DesignExpectation[] expectations) =>
        new(name, DesignPhaseKind.Dialog, "dlg", [step], true, [View(expectations)]);

    private static DesignStep Open(string? role, string? name, MockupEvent on = MockupEvent.Click, string anchor = "opener") =>
        new(DesignStepAction.Open, on, anchor, role, name);

    private static DesignExpectation Expect(string? role, string? name, string anchor, string phase = DesignExpectations.AtRest) =>
        new(role, name, anchor, DesignPart.Node, null, phase, null);

    private const string RestPage = """
        - main [ref=e1]:
          - heading "Team" [level=1] [ref=e2]
          - button "ADD   person" [ref=e3]
          - generic [ref=e4]:
            - text: More
        """;

    private const string DialogPage = """
        - main [ref=e1]:
          - heading "Team" [level=1] [ref=e2]
          - button "ADD   person" [ref=e3]
        - dialog [ref=e5]:
          - button "Save" [ref=e6]
        """;

    [Fact]
    public async Task Each_phase_starts_from_a_fresh_load_and_finds_its_opener_by_folded_name()
    {
        var page = new ScriptedDesignPage().Snapshot("rest", RestPage).Snapshot("dialog", DialogPage).Move("e3", "dialog");
        var screen = new DesignScreen(0, "/screens/0",
        [
            Rest(Expect("button", "Add person", "add")),
            Dialog("dlg opened by Add person", Open("button", "Add person"), Expect("button", "Save", "save", "dlg opened by Add person")),
        ]);

        var drive = await Drive(page, screen);

        page.Calls.Where(c => !c.StartsWith("probe", StringComparison.Ordinal)).Should().Equal(
            "viewport 1280x800",
            "load", "settle", "snapshot rest", "screenshot rest",
            "load", "settle", "snapshot rest", "act Click e3", "settle dialog", "snapshot dialog", "screenshot dialog");
        drive.Observation.Phases.Should().HaveCount(2).And.AllSatisfy(p => p.Nodes.Should().NotBeNull());
        DesignMatcher.Match(screen, drive.Observation).Matches.Should().BeTrue();
    }

    [Fact]
    public async Task A_missing_opener_leaves_the_phase_not_reached_and_the_next_phase_is_still_driven()
    {
        var page = new ScriptedDesignPage().Snapshot("rest", RestPage).Snapshot("dialog", DialogPage).Move("e3", "dialog");
        var screen = new DesignScreen(0, "/screens/0",
        [
            Rest(),
            Dialog("first", Open("button", "New person", anchor: "add-person")),
            Dialog("second", Open("button", "Add person")),
        ]);

        var drive = await Drive(page, screen);

        drive.Observation.Phases[1].NotReached.Should().Be("button \"New person\" (add-person) was not found");
        drive.Observation.Phases[2].Nodes.Should().NotBeNull("the drive carried on after the phase it could not reach");
        drive.Screenshots.Select(s => s.Key).Should().Equal(
            "screen-0.wide.00-at-rest", "screen-0.wide.01-first", "screen-0.wide.02-second");
    }

    [Fact]
    public async Task A_phase_the_design_cannot_drive_is_not_attempted()
    {
        var page = new ScriptedDesignPage().Snapshot("rest", RestPage);
        var lost = new DesignPhase("dlg with nothing on this screen to open it", DesignPhaseKind.Dialog, "dlg", [], false, [View()]);

        var drive = await Drive(page, new DesignScreen(0, "/screens/0", [Rest(), lost]));

        page.Calls.Count(c => c == "load").Should().Be(1);
        drive.Observation.Phases.Should().ContainSingle();
    }

    [Fact]
    public async Task A_page_failure_is_the_phases_reason_and_the_next_phase_runs()
    {
        var page = new ScriptedDesignPage { FailNextLoad = new DesignPageException("loading the page failed: net::ERR_CONNECTION_REFUSED") }
            .Snapshot("rest", RestPage).Snapshot("dialog", DialogPage).Move("e3", "dialog");
        var screen = new DesignScreen(0, "/screens/0", [Rest(), Dialog("d", Open("button", "Add person"))]);

        var drive = await Drive(page, screen);

        drive.Observation.Phases[0].NotReached.Should().Be("loading the page failed: net::ERR_CONNECTION_REFUSED");
        drive.Observation.Phases[1].Nodes.Should().NotBeNull();
        drive.Screenshots.Should().ContainSingle("a page that never loaded has nothing to picture");
    }

    [Fact]
    public async Task An_outline_that_cannot_be_read_is_the_phases_reason_never_a_guess()
    {
        var page = new ScriptedDesignPage().Snapshot("rest", "not a snapshot");

        var drive = await Drive(page, new DesignScreen(0, "/screens/0", [Rest()]));

        drive.Observation.Phases[0].NotReached.Should().StartWith("the page's outline could not be read: ");
    }

    [Fact]
    public async Task A_spent_budget_marks_the_phase_it_stopped_in_and_every_one_after_it()
    {
        var page = new ScriptedDesignPage { OnLoad = token => Task.Delay(Timeout.Infinite, token) }.Snapshot("rest", RestPage);
        var screen = new DesignScreen(0, "/screens/0",
        [
            new(DesignExpectations.AtRest, DesignPhaseKind.AtRest, null, [], true, [View(), new DesignView("narrow", 390, [], [])]),
            new("d", DesignPhaseKind.Dialog, "dlg", [Open("button", "Add person")], true, [View(), new DesignView("narrow", 390, [], [])]),
        ]);
        using var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var drive = await Drive(page, screen, budget.Token);

        drive.Observation.Phases.Should().HaveCount(4).And.AllSatisfy(p => p.NotReached.Should().Be(Spent));
        page.Calls.Count(c => c == "load").Should().Be(1, "nothing is attempted once the budget is spent");
    }

    [Fact]
    public async Task Cancelling_the_check_abandons_the_drive()
    {
        var page = new ScriptedDesignPage { OnLoad = token => Task.Delay(Timeout.Infinite, token) }.Snapshot("rest", RestPage);
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var act = () => DesignCheckDriver.DriveAsync(page, new DesignScreen(0, "/screens/0", [Rest()]), CancellationToken.None, Spent, caller.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Probes_ask_boxes_contrast_and_a_missing_name_and_a_confirmed_name_is_kept()
    {
        var page = new ScriptedDesignPage().Snapshot("rest", DialogPage)
            .Probe("rest", "e5", new DesignProbe(new ObservedBox(10, 10, 300, 200), null, "Add a person"))
            .Probe("rest", "e6", new DesignProbe(new ObservedBox(20, 20, 50, 30), 2.5, null));

        var drive = await Drive(page, new DesignScreen(0, "/screens/0", [Rest()]));

        page.Calls.Where(c => c.StartsWith("probe", StringComparison.Ordinal)).Should().Equal(
            "probe e1 main box=True contrast=False name=True",
            "probe e2 heading box=True contrast=True name=False",
            "probe e3 button box=True contrast=True name=False",
            "probe e5 dialog box=True contrast=False name=True",
            "probe e6 button box=True contrast=True name=False");
        var dialog = drive.Observation.Phases[0].Nodes![1];
        dialog.Name.Should().Be("Add a person", "the name the AI-mode snapshot left out was recovered");
        dialog.Box.Should().Be(new ObservedBox(10, 10, 300, 200));
        dialog.Children[0].Contrast.Should().Be(2.5);
    }

    [Fact]
    public async Task Console_entries_say_where_they_happened_and_are_capped()
    {
        var page = new ScriptedDesignPage().Snapshot("rest", RestPage)
            .Says([.. Enumerable.Range(0, 250).Select(i => "console error: " + new string('x', i))]);

        var drive = await Drive(page, new DesignScreen(0, "/screens/0", [Rest()]));

        drive.Console.Should().HaveCount(DesignCheckDriver.MaxConsole);
        drive.Console.Should().AllSatisfy(c =>
        {
            c.Should().StartWith("[screen 0, at rest, wide] console error: ");
            c.Length.Should().BeLessThanOrEqualTo(DesignCheckDriver.MaxConsoleLength);
        });
    }

    // ---- choosing what to act on ------------------------------------------------------------------------------

    private static (string? Reference, DesignPageAct Act, string? NotReached) Plan(DesignStep step, string snapshot) =>
        DesignCheckDriver.Plan(step, AriaSnapshot.Parse(snapshot));

    [Fact]
    public void A_step_with_no_role_is_found_by_its_words_and_acted_on_through_the_nearest_reference()
    {
        Plan(Open(null, "more"), RestPage).Should().Be(("e4", DesignPageAct.Click, (string?)null));
    }

    [Fact]
    public void A_step_with_neither_role_nor_name_cannot_be_searched_for()
    {
        Plan(Open(null, null, anchor: "/screens/0/children/3"), RestPage).NotReached
            .Should().Be("/screens/0/children/3 has no role or name a page can be searched for");
    }

    [Fact]
    public void Names_fold_whitespace_and_case_only()
    {
        Plan(Open("button", "add person"), RestPage).Reference.Should().Be("e3");
        Plan(Open("button", "Add-person"), RestPage).NotReached.Should().Be("button \"Add-person\" (opener) was not found");
        Plan(Open("heading", null), RestPage).Reference.Should().Be("e2", "a step that gives no name takes the first of its role");
    }

    [Fact]
    public void Choosing_a_row_clicks_the_first_row_of_data_not_the_header()
    {
        const string table = """
            - table [ref=e1]:
              - rowgroup [ref=e2]:
                - row "Name" [ref=e3]:
                  - columnheader "Name" [ref=e4]
              - rowgroup [ref=e5]:
                - row "Ada" [ref=e6]:
                  - cell "Ada" [ref=e7]
                - row "Grace" [ref=e8]:
                  - cell "Grace" [ref=e9]
            """;
        const string list = """
            - list "Rooms" [ref=e1]:
              - listitem [ref=e2]: Blue room
              - listitem [ref=e3]: Red room
            """;
        const string empty = """
            - list "Rooms" [ref=e1]
            """;

        Plan(Open("table", null, MockupEvent.Select), table).Should().Be(("e6", DesignPageAct.Click, (string?)null));
        Plan(Open("list", "Rooms", MockupEvent.Select), list).Should().Be(("e2", DesignPageAct.Click, (string?)null));
        Plan(Open("list", "Rooms", MockupEvent.Select, "rooms"), empty).NotReached.Should().Be("list \"Rooms\" (rooms) has no row to choose");
    }

    [Fact]
    public void A_change_is_made_the_way_its_control_takes_one()
    {
        const string fields = """
            - checkbox "Notify" [ref=e1]
            - combobox "Role" [ref=e2]
            - textbox "Office" [ref=e3]
            """;

        Plan(Open("checkbox", "Notify", MockupEvent.Change), fields).Act.Should().Be(DesignPageAct.Click);
        Plan(Open("combobox", "Role", MockupEvent.Change), fields).Act.Should().Be(DesignPageAct.ChooseSecondOption);
        Plan(Open("textbox", "Office", MockupEvent.Change), fields).Act.Should().Be(DesignPageAct.Type);
        Plan(new DesignStep(DesignStepAction.Choose, MockupEvent.Click, "two", "textbox", "Office"), fields).Act
            .Should().Be(DesignPageAct.Click, "choosing a tab is a click whatever the event");
    }
}
