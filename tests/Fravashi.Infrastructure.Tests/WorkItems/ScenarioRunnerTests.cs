using AwesomeAssertions;
using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;
using Fravashi.Infrastructure.Tests.Persistence;
using Fravashi.Infrastructure.WorkItems;
using Xunit;

namespace Fravashi.Infrastructure.Tests.WorkItems;

/// <summary>
/// Backlog 55, over the real board: a scenario is authored, stored, executed, and its result recorded as
/// evidence. The rule the run is held to is the item's acceptance, and the cases that decide it are the
/// ones where something did NOT run.
/// </summary>
public sealed class ScenarioRunnerTests : IDisposable
{
    private readonly SqliteTestContext _store = new();
    private readonly LocalTrackerResolver _trackers;
    private readonly ITracker _board;
    private readonly RecordingCommands _commands = new();
    private readonly ScenarioRunner _runner;

    public ScenarioRunnerTests()
    {
        _trackers = new LocalTrackerResolver(_store.Factory, TimeProvider.System);
        _board = _trackers.For(ProjectId.Default);
        _runner = new ScenarioRunner(_trackers, _commands);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task Every_step_running_green_is_a_pass_and_is_recorded_as_evidence()
    {
        var item = await ItemAsync(
            Scenario("The suite is green", Runnable("dotnet test"), Runnable("dotnet build")));

        var run = await _runner.RunAsync(ProjectId.Default, item.Id, ScenarioId(item), "/workspaces/a", MutationContext.User);

        run!.Result.Should().Be(EvidenceResult.Pass);
        _commands.Ran.Should().HaveCount(2);

        var evidence = await _board.EvidenceAsync(item.Id);
        var record = evidence.Should().ContainSingle().Subject;
        record.Kind.Should().Be(EvidenceKind.TestRun);
        record.Result.Should().Be(EvidenceResult.Pass);
        record.Scenario.Should().Be(ScenarioId(item));
        record.Summary.Should().Contain("all 2 steps ran");
    }

    [Fact]
    public async Task A_failing_step_fails_the_run_and_stops_it()
    {
        _commands.FailFrom = 1;
        var item = await ItemAsync(
            Scenario("The suite", Runnable("one"), Runnable("two"), Runnable("three")));

        var run = await _runner.RunAsync(ProjectId.Default, item.Id, ScenarioId(item), "/workspaces/a", MutationContext.User);

        run!.Result.Should().Be(EvidenceResult.Fail);

        // The steps after the failure are NOT RUN rather than failed: the studio did not check them, and
        // saying they failed would be inventing a result nobody measured.
        run.Steps[1].Outcome.Should().Be(StepOutcome.Failed);
        run.Steps[2].Outcome.Should().Be(StepOutcome.NotRun);
        _commands.Ran.Should().HaveCount(2, "nothing ran after the failure");

        (await _board.EvidenceAsync(item.Id)).Should().ContainSingle()
            .Which.Result.Should().Be(EvidenceResult.Fail);
    }

    [Fact]
    public async Task A_step_a_person_has_to_check_makes_the_run_partial_rather_than_a_pass()
    {
        var item = await ItemAsync(Scenario(
            "Mixed",
            Runnable("dotnet test"),
            new ScenarioStep(string.Empty, "Look at the page", "It looks right")));

        var run = await _runner.RunAsync(ProjectId.Default, item.Id, ScenarioId(item), "/workspaces/a", MutationContext.User);

        run!.Result.Should().Be(EvidenceResult.Partial);
        run.Steps[1].Detail.Should().Contain("a person checks it");

        (await _board.EvidenceAsync(item.Id)).Should().ContainSingle()
            .Which.Summary.Should().Contain("not a pass");
    }

    [Fact]
    public async Task With_no_contained_copy_nothing_runs_and_nothing_is_called_a_pass()
    {
        // Null is not "the current directory": with nowhere safe to run, nothing runs.
        var item = await ItemAsync(Scenario("The suite", Runnable("dotnet test")));

        var run = await _runner.RunAsync(ProjectId.Default, item.Id, ScenarioId(item), null, MutationContext.User);

        run!.Result.Should().Be(EvidenceResult.Partial);
        _commands.Ran.Should().BeEmpty();
        run.Steps[0].Detail.Should().Contain("no contained copy");
    }

    [Fact]
    public async Task A_command_runs_in_the_contained_copy_and_nowhere_else()
    {
        var item = await ItemAsync(Scenario("The suite", Runnable("dotnet test")));

        await _runner.RunAsync(ProjectId.Default, item.Id, ScenarioId(item), "/workspaces/w1", MutationContext.User);

        _commands.Ran.Should().ContainSingle()
            .Which.WorkingDirectory.Should().Be("/workspaces/w1");
    }

    [Fact]
    public async Task A_command_that_could_not_start_is_a_failure_rather_than_a_silence()
    {
        _commands.NeverStarts = true;
        var item = await ItemAsync(Scenario("The suite", Runnable("nonsense")));

        var run = await _runner.RunAsync(ProjectId.Default, item.Id, ScenarioId(item), "/workspaces/a", MutationContext.User);

        run!.Result.Should().Be(EvidenceResult.Fail);
        run.Steps[0].Detail.Should().Contain("could not be started");
    }

    [Fact]
    public async Task A_scenario_that_does_not_exist_is_reported_rather_than_invented_as_a_failure()
    {
        var item = await ItemAsync(Scenario("The suite", Runnable("dotnet test")));

        (await _runner.RunAsync(ProjectId.Default, item.Id, "no-such-scenario", "/workspaces/a", MutationContext.User))
            .Should().BeNull();

        (await _board.EvidenceAsync(item.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_evidence_carries_what_each_step_did()
    {
        var item = await ItemAsync(Scenario("The suite", Runnable("dotnet test")));

        await _runner.RunAsync(ProjectId.Default, item.Id, ScenarioId(item), "/workspaces/a", MutationContext.User);

        var record = (await _board.EvidenceAsync(item.Id)).Should().ContainSingle().Subject;
        record.Details.Should().NotBeNull();
        record.Details.Should().Contain("Passed").And.Contain("Exit code 0");
    }

    // ---- backlog 87: the project's own application ------------------------------------------------

    private sealed class FakeApplications(bool available = true, string? failure = null) : IApplicationRuns
    {
        public List<string> Started { get; } = [];
        public List<ApplicationRun> Removed { get; } = [];

        public bool Available => available;

        public Task<ApplicationRun> StartAsync(string workspaceId, CancellationToken cancellationToken)
        {
            Started.Add(workspaceId);
            return Task.FromResult(failure is null
                ? new ApplicationRun("r" + Started.Count, "http://fravashi-run-r1-relay:8080", null)
                : ApplicationRun.Failed(failure));
        }

        public Task RemoveAsync(ApplicationRun run, CancellationToken cancellationToken)
        {
            Removed.Add(run);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_scenario_that_uses_the_application_gets_a_fresh_one_for_this_run_and_it_is_removed_after()
    {
        var applications = new FakeApplications();
        var runner = new ScenarioRunner(_trackers, _commands, applications);
        var item = await ItemAsync(Scenario("The shop answers",
            Runnable("curl -fsS \"$VERIFY_APP_URL/\""), Runnable("curl -fsS \"$VERIFY_APP_URL/health\"")));

        var first = await runner.RunAsync(ProjectId.Default, item.Id, ScenarioId(item), "/workspaces/ws-shop", MutationContext.User);
        var second = await runner.RunAsync(ProjectId.Default, item.Id, ScenarioId(item), "/workspaces/ws-shop", MutationContext.User);

        first!.Result.Should().Be(EvidenceResult.Pass);
        second!.Result.Should().Be(EvidenceResult.Pass);
        applications.Started.Should().Equal(["ws-shop", "ws-shop"], "every run starts from a clean application");
        applications.Removed.Should().HaveCount(2);
        _commands.Ran.Should().OnlyContain(c => c.Environment![ScenarioRunner.AppUrlVariable] == "http://fravashi-run-r1-relay:8080");
    }

    [Fact]
    public async Task A_scenario_that_does_not_use_the_application_never_waits_for_one()
    {
        var applications = new FakeApplications();
        var runner = new ScenarioRunner(_trackers, _commands, applications);
        var item = await ItemAsync(Scenario("Unit tests", Runnable("dotnet test")));

        await runner.RunAsync(ProjectId.Default, item.Id, ScenarioId(item), "/workspaces/ws-shop", MutationContext.User);

        applications.Started.Should().BeEmpty();
        _commands.Ran.Should().ContainSingle().Which.Environment.Should().BeNull();
    }

    [Fact]
    public async Task An_application_that_could_not_start_leaves_the_steps_that_need_it_not_run_with_the_reason()
    {
        var runner = new ScenarioRunner(_trackers, _commands, new FakeApplications(failure: "the project's container spec was refused: privileged: ..."));
        var item = await ItemAsync(Scenario("Mixed", Runnable("dotnet test"), Runnable("curl \"$VERIFY_APP_URL\"")));

        var run = await runner.RunAsync(ProjectId.Default, item.Id, ScenarioId(item), "/workspaces/ws-shop", MutationContext.User);

        run!.Result.Should().Be(EvidenceResult.Partial, "the step was never there to check, so it did not fail");
        run.Steps[0].Outcome.Should().Be(StepOutcome.Passed);
        run.Steps[1].Outcome.Should().Be(StepOutcome.NotRun);
        run.Steps[1].Detail.Should().Contain("privileged");
        _commands.Ran.Should().ContainSingle();
    }

    [Fact]
    public async Task With_no_app_runner_the_steps_that_need_one_say_so()
    {
        var runner = new ScenarioRunner(_trackers, _commands, new FakeApplications(available: false));
        var item = await ItemAsync(Scenario("The shop", Runnable("curl \"$VERIFY_APP_URL\"")));

        var run = await runner.RunAsync(ProjectId.Default, item.Id, ScenarioId(item), "/workspaces/ws-shop", MutationContext.User);

        run!.Steps[0].Outcome.Should().Be(StepOutcome.NotRun);
        run.Steps[0].Detail.Should().Contain("no app runner");
    }

    // ---- backlog 293: one run of a scenario at a time ---------------------------------------------

    /// <summary>An app runner whose build does not finish until the test says so.</summary>
    private sealed class BuildingApplications : IApplicationRuns
    {
        public TaskCompletionSource Building { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Starts { get; private set; }

        public bool Available => true;

        public async Task<ApplicationRun> StartAsync(string workspaceId, CancellationToken cancellationToken)
        {
            Starts++;
            Building.TrySetResult();
            await Finish.Task.WaitAsync(cancellationToken);
            return new ApplicationRun("r" + Starts, "http://fravashi-run-r1-relay:8080", null);
        }

        public Task RemoveAsync(ApplicationRun run, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task A_second_run_of_a_scenario_that_is_still_running_is_refused_with_the_running_runs_id()
    {
        var applications = new BuildingApplications();
        var runner = new ScenarioRunner(_trackers, _commands, applications);
        var item = await ItemAsync(Scenario("The shop answers", Runnable("curl -fsS \"$VERIFY_APP_URL/\"")));

        var first = runner.AttemptAsync(ProjectId.Default, item.Id, ScenarioId(item), "/workspaces/ws-shop", MutationContext.User);
        await applications.Building.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var second = await runner.AttemptAsync(ProjectId.Default, item.Id, ScenarioId(item), "/workspaces/ws-shop", MutationContext.User);

        second.AlreadyRunning.Should().BeTrue();
        second.Run.Should().BeNull();
        applications.Starts.Should().Be(1, "a second build is exactly what the refusal is for");

        applications.Finish.SetResult();
        var finished = await first.WaitAsync(TimeSpan.FromSeconds(10));
        finished.AlreadyRunning.Should().BeFalse();
        finished.Run!.Result.Should().Be(EvidenceResult.Pass);
        second.RunId.Should().Be(finished.RunId, "the refusal names the run that is in flight");
        finished.RunId.Should().MatchRegex(@"^\d{8}-\d{6}-[0-9a-f]{8}$");
        (await _board.EvidenceAsync(item.Id)).Should().ContainSingle("the refused attempt recorded nothing");
    }

    [Fact]
    public async Task Once_a_run_has_ended_the_scenario_can_run_again_under_a_new_id()
    {
        var item = await ItemAsync(Scenario("The suite", Runnable("dotnet test")));

        var first = await _runner.AttemptAsync(ProjectId.Default, item.Id, ScenarioId(item), "/workspaces/a", MutationContext.User);
        var again = await _runner.AttemptAsync(ProjectId.Default, item.Id, ScenarioId(item), "/workspaces/a", MutationContext.User);

        first.AlreadyRunning.Should().BeFalse();
        again.AlreadyRunning.Should().BeFalse();
        again.RunId.Should().NotBe(first.RunId);
        _commands.Ran.Should().HaveCount(2);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static ScenarioStep Runnable(string command) =>
        new(string.Empty, "Run " + command, "It succeeds", Data: null, Command: command);

    private static TestScenario Scenario(string title, params ScenarioStep[] steps) =>
        new(string.Empty, title, steps);

    private string ScenarioId(WorkItem item) =>
        _board.GetAsync(item.Id).GetAwaiter().GetResult()!.Scenarios[0].Id;

    private async Task<WorkItem> ItemAsync(TestScenario scenario)
    {
        var created = await _board.CreateAsync(
            new NewWorkItem
            {
                Level = WorkItemLevel.Task,
                Title = "Verified work",
                Description = "Why it exists.",
                Scenarios = [scenario],
            },
            MutationContext.User);

        created.Outcome.Should().Be(TrackerOutcome.Ok, string.Join("; ", created.Errors.Select(e => e.Code)));
        return created.Value;
    }

    /// <summary>A command runner that records what it was asked to do and answers as told.</summary>
    private sealed class RecordingCommands : IAgentCommandRunner
    {
        public List<AgentCommand> Ran { get; } = [];

        /// <summary>The index from which a command fails, or null when they all succeed.</summary>
        public int? FailFrom { get; set; }

        /// <summary>When true, no command starts at all.</summary>
        public bool NeverStarts { get; set; }

        public Task<AgentCommandResult> RunAsync(AgentCommand command, CancellationToken cancellationToken = default)
        {
            var index = Ran.Count;
            Ran.Add(command);

            if (NeverStarts)
            {
                return Task.FromResult(new AgentCommandResult(false, null, string.Empty, string.Empty, false));
            }

            var failed = FailFrom is { } from && index >= from;
            return Task.FromResult(new AgentCommandResult(
                true,
                failed ? 1 : 0,
                failed ? string.Empty : "ok",
                failed ? "it went wrong" : string.Empty,
                false));
        }
    }
}
