using System.Collections.Concurrent;
using System.Globalization;
using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.Verification;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;

namespace Fravashi.Application.WorkItems;

/// <summary>What asking for a scenario run produced (backlog 293).</summary>
/// <param name="RunId">This run's id, or the id of the run already in flight when <paramref name="AlreadyRunning"/>.</param>
/// <param name="Run">The finished run, or null when nothing ran: the scenario does not exist, or one is already running.</param>
/// <param name="AlreadyRunning">True when the same scenario was already running, so nothing was started.</param>
public sealed record ScenarioRunAttempt(string RunId, ScenarioRun? Run, bool AlreadyRunning);

/// <summary>
/// Runs a work item's test scenario and records what happened as evidence (backlog 55).
/// <para>
/// <b>What it will and will not do, and why.</b> A step's command is text that was written into the graph,
/// and the graph is an injection surface (the threat model for backlog 63 says so in as many words): an
/// issue or a document written by somebody else can try to instruct the agent reading it. So a scenario's
/// commands are NOT a quiet capability. They run in the contained copy, never on a host path, and running
/// them is a deliberate act that the caller has to be entitled to; the tool that offers this is a
/// MUTATING one, so it goes through the same human approval as every other change. Making verification
/// unattended is item 58's decision to make with a policy, not this runner's to assume.
/// </para>
/// <para>
/// The pass rule lives in <see cref="ScenarioRun"/>, not here, because it is the sentence the product is
/// making rather than a detail of how a process was started.
/// </para>
/// <para>
/// <b>The project's own application</b> (backlog 87). A step that uses <c>$VERIFY_APP_URL</c> is exercising the
/// application, so before the first step the runner asks <see cref="IApplicationRuns"/> to build it from the
/// workspace's container spec and start it, hands every step its address in that variable, and removes it after the
/// last one, which is what makes each run start from a clean application. It is asked for only by a scenario that
/// uses it, so a scenario of unit tests never waits on a build.
/// </para>
/// <para>
/// <b>The approved design</b> (backlog 224, plan @63). When such a scenario runs on an item with an approved design, each
/// screen of the PINNED design is one more step after the scenario's own, checked against the same application by
/// <see cref="DesignVerification"/> while it is still running. Its report, the consoles, the application's logs and the
/// paired screenshots are kept as verification artifacts and named by the evidence record. An item with no approved
/// design runs exactly as before, and its evidence says so.
/// </para>
/// </summary>
public sealed class ScenarioRunner(
    ITrackerResolver trackers,
    IAgentCommandRunner commands,
    IApplicationRuns? applications = null,
    Func<ProjectId, IReadOnlyDictionary<string, string>>? projectEnvironment = null,
    DesignVerification? design = null)
{
    /// <summary>The variable a step reads the application's address from, and whose use asks for an application.</summary>
    public const string AppUrlVariable = "VERIFY_APP_URL";

    /// <summary>How long one step may take before it is killed and reported as failed.</summary>
    public static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(10);

    /// <summary>The shell each step's command is handed to, per platform.</summary>
    private static (string File, string Flag) Shell =>
        OperatingSystem.IsWindows() ? ("cmd.exe", "/c") : ("/bin/sh", "-c");

    /// <summary>
    /// The runs in flight, by project, item and scenario, each with its run id (backlog 293). A run that uses the
    /// application builds it, which takes minutes and a lot of memory, so a second start of the same scenario while one
    /// runs is refused with the running one's id rather than starting a second build.
    /// </summary>
    private readonly ConcurrentDictionary<(ProjectId Project, WorkItemId Item, string Scenario), string> _inFlight = new();

    /// <summary>
    /// Runs one scenario of one item and records the result as evidence. Returns null when the item or the
    /// scenario does not exist, which the caller reports rather than inventing a failure for. Throws when the same
    /// scenario is already running; <see cref="AttemptAsync"/> answers that instead.
    /// </summary>
    /// <param name="workingDirectory">
    /// The session's CONTAINED COPY. Null is not "the current directory": with nowhere safe to run, nothing
    /// runs, and every step is reported as not run.
    /// </param>
    public async Task<ScenarioRun?> RunAsync(
        ProjectId project,
        WorkItemId item,
        string scenarioId,
        string? workingDirectory,
        MutationContext context,
        CancellationToken ct = default)
    {
        var attempt = await AttemptAsync(project, item, scenarioId, workingDirectory, context, ct).ConfigureAwait(false);
        return attempt.AlreadyRunning
            ? throw new InvalidOperationException($"Scenario {scenarioId} is already running as run {attempt.RunId}.")
            : attempt.Run;
    }

    /// <summary>
    /// Runs one scenario of one item unless the same scenario is already running, in which case nothing starts and the
    /// answer names the running run (backlog 293). Otherwise exactly <see cref="RunAsync"/>, with the run's id.
    /// </summary>
    public async Task<ScenarioRunAttempt> AttemptAsync(
        ProjectId project,
        WorkItemId item,
        string scenarioId,
        string? workingDirectory,
        MutationContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        var key = (project, item, scenarioId);
        var runId = NewRunId();
        var holder = _inFlight.GetOrAdd(key, runId);
        if (!string.Equals(holder, runId, StringComparison.Ordinal))
        {
            return new ScenarioRunAttempt(holder, null, AlreadyRunning: true);
        }

        try
        {
            var run = await RunOnceAsync(project, item, scenarioId, workingDirectory, context, ct).ConfigureAwait(false);
            return new ScenarioRunAttempt(runId, run, AlreadyRunning: false);
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<(ProjectId, WorkItemId, string), string>(key, runId));
        }
    }

    /// <summary>A run's id, readable and sortable: when it started, and enough randomness to tell two apart.</summary>
    private static string NewRunId() =>
        DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
        + "-" + Guid.NewGuid().ToString("N")[..8];

    private async Task<ScenarioRun?> RunOnceAsync(
        ProjectId project,
        WorkItemId item,
        string scenarioId,
        string? workingDirectory,
        MutationContext context,
        CancellationToken ct)
    {

        var tracker = trackers.For(project);
        var stored = await tracker.GetAsync(item, ct).ConfigureAwait(false);
        if (stored?.Scenarios.FirstOrDefault(s => s.Id == scenarioId) is not { } scenario)
        {
            return null;
        }

        var results = new List<StepResult>();
        var stopped = false;
        ApplicationRun? application = null;
        string? noApplication = null;
        DesignVerificationResult? designed = null;
        var usesApplication = !string.IsNullOrWhiteSpace(workingDirectory)
            && scenario.Steps.Any(s => s.IsRunnable && s.Command!.Contains(AppUrlVariable, StringComparison.Ordinal));
        if (usesApplication)
        {
            // The contained copy's folder is named by its workspace id (plan 2d), which is the id the host knows.
            var workspaceId = Path.GetFileName(workingDirectory!.TrimEnd('/', Path.DirectorySeparatorChar));
            if (applications is not { Available: true })
            {
                noApplication = "This scenario uses the application, and no app runner is reachable from this studio.";
            }
            else
            {
                application = await applications.StartAsync(workspaceId, ct).ConfigureAwait(false);
                noApplication = application.Started ? null : "The application could not be started: " + application.Failure;
            }
        }

        try
        {
            foreach (var step in scenario.Steps)
            {
                if (stopped)
                {
                    // Everything after a failure is NOT RUN rather than failed: the studio did not check it,
                    // and saying it failed would be inventing a result nobody measured.
                    results.Add(new StepResult(step.Id, StepOutcome.NotRun, "An earlier step failed."));
                    continue;
                }

                if (!step.IsRunnable)
                {
                    results.Add(new StepResult(
                        step.Id,
                        StepOutcome.NotRun,
                        "This step has nothing to run, so a person checks it: " + step.Expected));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(workingDirectory))
                {
                    results.Add(new StepResult(
                        step.Id,
                        StepOutcome.NotRun,
                        "There is no contained copy to run this in, so nothing was run."));
                    continue;
                }

                if (noApplication is not null && step.Command!.Contains(AppUrlVariable, StringComparison.Ordinal))
                {
                    // Not a failure of the step: the thing it checks was never there to check.
                    results.Add(new StepResult(step.Id, StepOutcome.NotRun, noApplication));
                    continue;
                }

                var (file, flag) = Shell;

                // Backlog 154: the project's own variables reach a step too (a token a check needs), with the
                // application's address on top, because that one is this run's and nobody else's.
                var environment = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (key, value) in projectEnvironment?.Invoke(project) ?? new Dictionary<string, string>())
                {
                    environment[key] = value;
                }

                if (application is { Started: true })
                {
                    environment[AppUrlVariable] = application.Url!;
                }

                // Nothing to add is still nothing, as it was before: a step with no variables runs with none.
                var result = await commands.RunAsync(
                    new AgentCommand(
                        file, [flag, step.Command!], StepTimeout, environment.Count == 0 ? null : environment, WorkingDirectory: workingDirectory),
                    ct).ConfigureAwait(false);

                var outcome = result is { Started: true, ExitCode: 0 } ? StepOutcome.Passed : StepOutcome.Failed;
                results.Add(new StepResult(step.Id, outcome, Describe(result)));
                stopped = outcome == StepOutcome.Failed;
            }

            // Backlog 224: the design is checked while the application is still up, after the scenario's own steps, and
            // under the same rule: after a failure, or with no application, its screens are NOT RUN with the reason.
            if (usesApplication && design is not null)
            {
                var running = application is { Started: true } ? application : null;
                designed = await design.RunAsync(
                    stored,
                    running?.Url,
                    scenario.Steps,
                    stopped ? "An earlier step failed." : noApplication,
                    c => running is not null && applications is not null
                        ? applications.LogsAsync(running, c)
                        : Task.FromResult<string?>(null),
                    ct,
                    running?.Marker).ConfigureAwait(false);
                results.AddRange(designed.Steps);
            }
        }
        finally
        {
            if (application is { Started: true } && applications is not null)
            {
                await applications.RemoveAsync(application, CancellationToken.None).ConfigureAwait(false);
            }
        }

        var run = new ScenarioRun(scenario.Id, results);

        // Recorded as evidence whatever happened, including a failure: a verification nobody can find
        // afterwards is a verification that did not happen as far as the next reader is concerned.
        await tracker.RecordEvidenceAsync(
            item,
            new NewEvidence
            {
                Kind = EvidenceKind.TestRun,
                Result = run.Result,
                Summary = designed?.Sentence is { } sentence ? run.Summarise(scenario.Title) + " " + sentence : run.Summarise(scenario.Title),
                Gate = "verify",
                ArtefactRef = designed?.ArtefactRef,
                Scenario = scenario.Id,
                Details = Details(run, designed?.Details),
            },
            context,
            ct).ConfigureAwait(false);

        return run;
    }

    /// <summary>What the studio saw, short enough to read and specific enough to act on.</summary>
    private static string Describe(AgentCommandResult result)
    {
        if (!result.Started)
        {
            return "The command could not be started.";
        }

        if (result.TimedOut)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"The command was still running after {StepTimeout.TotalMinutes} minutes and was stopped.");
        }

        var output = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;

        return string.Create(CultureInfo.InvariantCulture, $"Exit code {result.ExitCode}. ")
            + Tail(output ?? string.Empty);
    }

    /// <summary>The LAST of the output, because that is where a test runner says what happened.</summary>
    private static string Tail(string text, int max = 400)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= max ? trimmed : "..." + trimmed[^max..];
    }

    private static string Details(ScenarioRun run, System.Text.Json.Nodes.JsonNode? design)
    {
        var details = new System.Text.Json.Nodes.JsonObject
        {
            ["steps"] = new System.Text.Json.Nodes.JsonArray(
            [
                .. run.Steps.Select(s => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
                {
                    ["step"] = s.StepId,
                    ["outcome"] = s.Outcome.ToString(),
                    ["detail"] = s.Detail,
                }),
            ]),
        };

        if (design is not null)
        {
            details["design"] = design;
        }

        return details.ToJsonString();
    }
}
