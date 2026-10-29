using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.Mockups;
using Fravashi.Application.Verification;
using Fravashi.Application.Wiki;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Projects;
using Fravashi.Domain.Wiki;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Tests.Persistence;
using Fravashi.Infrastructure.Verification;
using Fravashi.Infrastructure.Wiki;
using Fravashi.Infrastructure.WorkItems;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Verification;

/// <summary>
/// Backlog 224, over the real board, the real wiki and the real artifact store: a scenario that uses the application, on
/// an item with an approved design, checks the application against the PINNED design and keeps the evidence. The pages are
/// #223's scripted replays of #222's recordings behind the same drive and matcher the browser check uses; no browser runs.
/// </summary>
public sealed class ScenarioDesignCheckTests : IDisposable
{
    private const string AppUrl = "http://fravashi-run-r1-relay:8080";

    private readonly SqliteTestContext _store = new();
    private readonly LocalTrackerResolver _trackers;
    private readonly ITracker _board;
    private readonly SqliteWikiLibrary _wiki;
    private readonly DesignApproval _approval;
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "fravashi-224-" + Guid.NewGuid().ToString("N"));
    private readonly VerificationArtifactStore _artifacts;
    private readonly Commands _commands = new();
    private readonly Applications _applications = new();

    public ScenarioDesignCheckTests()
    {
        _trackers = new LocalTrackerResolver(_store.Factory, TimeProvider.System);
        _board = _trackers.For(ProjectId.Default);
        _wiki = new SqliteWikiLibrary(_store.Factory, TimeProvider.System, ProjectId.Default);
        _approval = new DesignApproval(_trackers, new OneWiki(_wiki));
        _artifacts = new VerificationArtifactStore(new DataDirectory(_dataPath));
    }

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }
    }

    [Fact]
    public async Task A_matching_build_passes_and_the_report_and_paired_screenshots_are_kept_naming_the_design_and_its_version()
    {
        var check = new ScriptedCheck(() => DesignCheckDriverTests.Replay("faithful"));
        var (item, design) = await ApprovedItemAsync(Tree());

        var run = await RunAsync(item, check);

        run.Result.Should().Be(EvidenceResult.Pass, string.Join(" | ", run.Steps.Select(s => s.Detail)));
        run.Steps.Select(s => s.StepId).Should().Equal([run.Steps[0].StepId, "design:directory"]);
        check.Requests.Should().ContainSingle().Which.Targets.Should().Equal([new DesignCheckTarget(0, AppUrl + "/")]);

        var record = await EvidenceAsync(item);
        record.Result.Should().Be(EvidenceResult.Pass);
        record.Summary.Should().Contain($"@{design.Number}").And.Contain("at version 1");
        record.ArtefactRef.Should().StartWith("verification/");

        var files = await _artifacts.ListAsync(record.ArtefactRef!);
        files.Should().Contain([DesignVerification.ReportFile, DesignVerification.ConsoleFile, DesignVerification.LogFile]);
        var builds = files.Where(f => f.EndsWith(".build.png", StringComparison.Ordinal)).ToList();
        builds.Should().NotBeEmpty();
        builds.Should().AllSatisfy(b => files.Should().Contain(b.Replace(".build.png", ".prototype.png", StringComparison.Ordinal)));

        var report = await TextAsync(record, DesignVerification.ReportFile);
        report.Should().Contain($"@{design.Number}").And.Contain("version 1").And.Contain("PASSED");
        (await TextAsync(record, DesignVerification.LogFile)).Should().Contain("listening on 8080");

        var details = JsonNode.Parse(record.Details!)!["design"]!;
        details["number"]!.GetValue<int>().Should().Be(design.Number);
        details["version"]!.GetValue<long>().Should().Be(1);
        details["screens"]![0]!["outcome"]!.GetValue<string>().Should().Be("Passed");
    }

    [Fact]
    public async Task A_renamed_button_fails_the_run_with_DIFFERENT_NAME_in_the_designs_terms()
    {
        var (item, _) = await ApprovedItemAsync(Tree());

        var run = await RunAsync(item, new ScriptedCheck(() => DesignCheckDriverTests.Replay("renamed")));

        run.Result.Should().Be(EvidenceResult.Fail);
        var step = run.Steps.Single(s => s.StepId == "design:directory");
        step.Outcome.Should().Be(StepOutcome.Failed);
        step.Detail.Should().Contain("DIFFERENT NAME").And.Contain("Add person");

        var record = await EvidenceAsync(item);
        (await TextAsync(record, DesignVerification.ReportFile)).Should().Contain("DIFFERENT NAME").And.Contain("NOT RUN");
        record.Details.Should().Contain("DIFFERENT NAME");
    }

    [Fact]
    public async Task An_error_in_the_builds_console_fails_the_run_even_when_the_page_matches()
    {
        var (item, _) = await ApprovedItemAsync(Tree());

        var run = await RunAsync(item, new ScriptedCheck(
            () => DesignCheckDriverTests.Replay("faithful").Says("[screen 0, at rest, wide] console error: boom")));

        run.Result.Should().Be(EvidenceResult.Fail);
        run.Steps.Single(s => s.StepId == "design:directory").Detail.Should().Contain("console has 1 error");
        (await TextAsync(await EvidenceAsync(item), DesignVerification.ConsoleFile)).Should().Contain("console error: boom");
    }

    [Fact]
    public async Task A_phase_that_could_not_be_driven_makes_the_run_partial_with_NOT_RUN_and_never_a_pass()
    {
        var (item, _) = await ApprovedItemAsync(Tree());

        var run = await RunAsync(item, new ScriptedCheck(() =>
        {
            var page = DesignCheckDriverTests.Replay("faithful");
            page.FailNextLoad = new Fravashi.Infrastructure.Verification.DesignPageException("the page did not load");
            return page;
        }));

        run.Result.Should().Be(EvidenceResult.Partial);
        var step = run.Steps.Single(s => s.StepId == "design:directory");
        step.Outcome.Should().Be(StepOutcome.NotRun);
        step.Detail.Should().Contain("NOT RUN");
        (await EvidenceAsync(item)).Summary.Should().Contain("not a pass");
    }

    [Fact]
    public async Task An_item_with_no_approved_design_says_so_and_the_rest_of_the_scenario_runs_as_before()
    {
        var check = new ScriptedCheck(() => DesignCheckDriverTests.Replay("faithful"));
        var item = await ItemAsync();

        var run = await RunAsync(item, check);

        run.Result.Should().Be(EvidenceResult.Pass);
        run.Steps.Should().ContainSingle("only the scenario's own step is listed");
        _commands.Ran.Should().ContainSingle();
        check.Requests.Should().BeEmpty();

        var record = await EvidenceAsync(item);
        record.ArtefactRef.Should().BeNull();
        JsonNode.Parse(record.Details!)!["design"]!.GetValue<string>().Should().Be(DesignVerification.NoDesign);
    }

    [Fact]
    public async Task The_pinned_version_is_checked_when_the_design_moved_on_after_approval()
    {
        var check = new ScriptedCheck(() => DesignCheckDriverTests.Replay("faithful"));
        var (item, design) = await ApprovedItemAsync(Tree());

        // Somebody edits the design after approval: the button the build carries is renamed in the document.
        var edited = await _wiki.UpdateAsync(
            design.Id, new WikiItemEdit { Body = Body(item, Tree(rename: "Invite someone")) }, design.Version);
        edited.IsValid.Should().BeTrue();
        edited.Value.Version.Should().Be(2);

        var run = await RunAsync(item, check);

        run.Result.Should().Be(EvidenceResult.Pass, "the build matches what was APPROVED, version 1");
        JsonSerializer.Serialize(check.Requests.Single().Design.Screens[0]).Should().Contain("Add person").And.NotContain("Invite someone");
        var record = await EvidenceAsync(item);
        record.Summary.Should().Contain("at version 1");
    }

    [Fact]
    public async Task After_a_failed_step_the_design_screens_are_not_run_and_the_check_never_starts()
    {
        _commands.Fail = true;
        var check = new ScriptedCheck(() => DesignCheckDriverTests.Replay("faithful"));
        var (item, _) = await ApprovedItemAsync(Tree());

        var run = await RunAsync(item, check);

        run.Result.Should().Be(EvidenceResult.Fail);
        run.Steps.Single(s => s.StepId == "design:directory").Should().Match<StepResult>(
            s => s.Outcome == StepOutcome.NotRun && s.Detail.Contains("An earlier step failed.", StringComparison.Ordinal));
        check.Requests.Should().BeEmpty();
        _applications.Removed.Should().ContainSingle("the application is still removed after the run");
    }

    [Fact]
    public async Task A_screen_no_page_of_the_scenario_reaches_is_not_run_rather_than_passed()
    {
        // The whole fixture: "Profile" is a second screen, and this scenario reaches only the root.
        var (item, _) = await ApprovedItemAsync(Tree(oneScreen: false));

        var run = await RunAsync(item, new ScriptedCheck(() => DesignCheckDriverTests.Replay("faithful")));

        run.Steps.Single(s => s.StepId == "design:directory").Outcome.Should().Be(StepOutcome.Passed);
        var profile = run.Steps.Single(s => s.StepId == "design:profile");
        profile.Outcome.Should().Be(StepOutcome.NotRun);
        profile.Detail.Should().Contain("No page the scenario reaches").And.Contain("$VERIFY_APP_URL/profile");
        run.Result.Should().Be(EvidenceResult.Partial);
    }

    [Fact]
    public async Task With_no_browser_the_screens_are_not_run_with_its_reason()
    {
        var (item, _) = await ApprovedItemAsync(Tree());

        var run = await RunAsync(item, new ScriptedCheck(() => DesignCheckDriverTests.Replay("faithful"))
        {
            UnavailableReason = "No Chromium was found.",
        });

        run.Result.Should().Be(EvidenceResult.Partial);
        run.Steps.Single(s => s.StepId == "design:directory").Detail.Should().Contain("No Chromium was found.");
        (await EvidenceAsync(item)).ArtefactRef.Should().BeNull();
    }

    [Fact]
    public async Task What_the_application_logged_is_redacted_before_it_is_kept()
    {
        var key = "sk-ant-api03-" + new string('a', 40);
        _applications.Logs = "listening on 8080\nANTHROPIC_API_KEY=" + key + "\n";
        var (item, _) = await ApprovedItemAsync(Tree());

        await RunAsync(item, new ScriptedCheck(() => DesignCheckDriverTests.Replay("faithful")));

        (await TextAsync(await EvidenceAsync(item), DesignVerification.LogFile)).Should().NotContain(key).And.Contain("listening on 8080");
    }

    [Fact]
    public async Task A_runs_window_marker_reaches_the_check_and_no_step_and_its_token_is_in_no_evidence()
    {
        // Backlog 226: the marker the app runner minted rides from the run to the check, and only there.
        const string token = "c0ffee00c0ffee00c0ffee00c0ffee00c0ffee00c0ffee00c0ffee00c0ffee00";
        _applications.Marker = WindowMarker.For(AppUrl, token);
        _applications.Logs = $"listening on 8080\nrequest from FravashiWindow/1.0 tk/{token}\n";
        var (item, _) = await ApprovedItemAsync(Tree());
        var check = new ScriptedCheck(() =>
            DesignCheckDriverTests.Replay("faithful").Says($"[screen 0, at rest, wide] console error: agent FravashiWindow/1.0 tk/{token}"));

        var run = await RunAsync(item, check);

        check.Requests.Should().ContainSingle().Which.Marker.Should().BeSameAs(_applications.Marker);
        _commands.Ran.Should().NotBeEmpty();
        _commands.Ran.Should().OnlyContain(c =>
            !c.Arguments.Any(a => a.Contains(token, StringComparison.Ordinal))
            && (c.Environment == null || (!c.Environment.Values.Any(v => v.Contains(token, StringComparison.Ordinal))
                                          && !c.Environment.ContainsKey("FRAVASHI_WINDOW_TOKEN"))),
            "a scenario step is the repository's own command; it never receives the run's token");

        run.Steps.Should().OnlyContain(s => !s.Detail.Contains(token, StringComparison.Ordinal));
        var record = await EvidenceAsync(item);
        record.Summary.Should().NotContain(token);
        record.Details.Should().NotContain(token);
        foreach (var file in new[] { DesignVerification.ReportFile, DesignVerification.ConsoleFile, DesignVerification.LogFile })
        {
            (await TextAsync(record, file)).Should().NotContain(token, file);
        }

        (await TextAsync(record, DesignVerification.ConsoleFile)).Should().Contain(WindowMarker.Withheld);
    }

    [Fact]
    public async Task The_store_reads_nothing_outside_its_own_run_folders()
    {
        (await _artifacts.ReadAsync("verification/../fravashi.db", "x")).Should().BeNull();
        (await _artifacts.ReadAsync("verification/20261007-120000-abcdef01", "../../fravashi.db")).Should().BeNull();
        (await _artifacts.ListAsync("/data/keys")).Should().BeEmpty();
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private async Task<ScenarioRun> RunAsync(WorkItem item, IDesignCheck check)
    {
        var runner = new ScenarioRunner(
            _trackers, _commands, _applications, design: new DesignVerification(_approval, check, _artifacts));
        var scenario = (await _board.GetAsync(item.Id))!.Scenarios[0].Id;
        var run = await runner.RunAsync(ProjectId.Default, item.Id, scenario, "/workspaces/ws-team", MutationContext.User);
        return run!;
    }

    private async Task<EvidenceRecord> EvidenceAsync(WorkItem item) =>
        (await _board.EvidenceAsync(item.Id)).Should().ContainSingle().Subject;

    private async Task<string> TextAsync(EvidenceRecord record, string name)
    {
        record.ArtefactRef.Should().NotBeNull();
        var bytes = await _artifacts.ReadAsync(record.ArtefactRef!, name);
        bytes.Should().NotBeNull();
        return System.Text.Encoding.UTF8.GetString(bytes!);
    }

    /// <summary>The team-directory fixture; with one screen, its second screen and the navigation to it are taken out.</summary>
    private static string Tree(bool oneScreen = true, string? rename = null)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Mockups", "Fixtures", "team-directory.attempt-3.json"));
        if (rename is not null)
        {
            json = json.Replace("\"Add person\"", $"\"{rename}\"", StringComparison.Ordinal);
        }

        if (!oneScreen)
        {
            return json;
        }

        var tree = JsonNode.Parse(json)!.AsObject();
        tree["screens"]!.AsArray().RemoveAt(1);
        WithoutNavigation(tree["screens"]![0]!);
        return tree.ToJsonString();
    }

    private static void WithoutNavigation(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (obj["on"] is JsonObject on && on.Any(e => e.Value is JsonObject action && action.ContainsKey("go")))
            {
                obj.Remove("on");
            }

            foreach (var child in obj.Select(e => e.Value).OfType<JsonNode>().ToList())
            {
                WithoutNavigation(child);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.OfType<JsonNode>().ToList())
            {
                WithoutNavigation(child);
            }
        }
    }

    private static string Body(WorkItem item, string tree) => $"The design for #{item.Number}.\n\n```mockup\n{tree}\n```\n";

    private async Task<WorkItem> ItemAsync()
    {
        var created = await _board.CreateAsync(
            new NewWorkItem
            {
                Level = WorkItemLevel.Task,
                Title = "The team directory",
                Description = "Why it exists.",
                Scenarios =
                [
                    new TestScenario(string.Empty, "The directory answers",
                    [
                        new ScenarioStep(string.Empty, "Ask the root", "It answers", Command: "curl -fsS \"$VERIFY_APP_URL/\""),
                    ]),
                ],
            },
            MutationContext.User);
        created.Outcome.Should().Be(TrackerOutcome.Ok);
        return created.Value;
    }

    private async Task<(WorkItem Item, WikiItem Design)> ApprovedItemAsync(string tree)
    {
        var item = await ItemAsync();
        var created = await _wiki.CreateAsync(new NewWikiItem
        {
            Type = WikiItemType.Design,
            Title = "Team directory",
            Body = Body(item, tree),
        });
        created.IsValid.Should().BeTrue(string.Join("; ", created.Errors.Select(e => e.Code)));
        DesignMockup.Read(created.Value.Body).IsValid.Should().BeTrue(string.Join("; ", DesignMockup.Read(created.Value.Body).Errors));

        (await _approval.ApproveAsync(ProjectId.Default, created.Value.Number, item.Number)).Approved.Should().BeTrue();
        return ((await _board.GetAsync(item.Id))!, created.Value);
    }

    /// <summary>
    /// <see cref="IDesignCheck"/> exactly as <see cref="PlaywrightDesignCheck"/> composes it (drive each side, match each
    /// side), over scripted pages instead of a browser.
    /// </summary>
    private sealed class ScriptedCheck(Func<ScriptedDesignPage> build) : IDesignCheck
    {
        public List<DesignCheckRequest> Requests { get; } = [];

        public string? UnavailableReason { get; init; }

        public async Task<DesignCheckResult> CheckAsync(DesignCheckRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var expectations = DesignExpectations.For(request.Design);
            var screens = new List<DesignScreenCheck>();
            foreach (var target in request.Targets)
            {
                var screen = expectations.Screens[target.Screen];
                screens.Add(new DesignScreenCheck(
                    target.Screen,
                    target.Url,
                    await SideAsync(build(), screen),
                    await SideAsync(DesignCheckDriverTests.Replay("prototype"), screen)));
            }

            return new DesignCheckResult(screens, null);
        }

        private static async Task<DesignCheckSide> SideAsync(ScriptedDesignPage page, DesignScreen screen)
        {
            var drive = await DesignCheckDriver.DriveAsync(page, screen, CancellationToken.None, "spent");
            return new DesignCheckSide(drive.Observation, DesignMatcher.Match(screen, drive.Observation), drive.Screenshots, drive.Console);
        }
    }

    private sealed class Applications : IApplicationRuns
    {
        public List<ApplicationRun> Removed { get; } = [];

        public string Logs { get; set; } = "listening on 8080\n";

        public WindowMarker? Marker { get; set; }

        public bool Available => true;

        public Task<ApplicationRun> StartAsync(string workspaceId, CancellationToken cancellationToken) =>
            Task.FromResult(new ApplicationRun("r1", AppUrl, null) { Marker = Marker });

        public Task<string?> LogsAsync(ApplicationRun run, CancellationToken cancellationToken) => Task.FromResult<string?>(Logs);

        public Task RemoveAsync(ApplicationRun run, CancellationToken cancellationToken)
        {
            Removed.Add(run);
            return Task.CompletedTask;
        }
    }

    private sealed class Commands : IAgentCommandRunner
    {
        public List<AgentCommand> Ran { get; } = [];

        public bool Fail { get; set; }

        public Task<AgentCommandResult> RunAsync(AgentCommand command, CancellationToken cancellationToken = default)
        {
            Ran.Add(command);
            return Task.FromResult(new AgentCommandResult(true, Fail ? 1 : 0, "ok", Fail ? "it went wrong" : string.Empty, false));
        }
    }
}
