using System.Text.Json;
using System.Text.Json.Nodes;
using Fravashi.Application.Sessions;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;
using Fravashi.Domain;

namespace Fravashi.Infrastructure.Sessions.Tools;

/// <summary>
/// Writes a work item's test scenarios (backlog 55): ordered steps with expected outcomes, and for
/// non-UI work a command that checks each one. Acceptance criteria say what Done means; a scenario says
/// how anybody checks it.
/// </summary>
public sealed class ScenarioWriteTool(ITrackerResolver trackers, IWorkItemChanges changes)
    : WorkItemToolBase(trackers, changes)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "work_item_scenario_write";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        $"Writes the test scenarios of a {ProductName.Short} work item: ordered steps, each saying what to do and what "
        + "should happen. Give a step a command and the studio can check it by running it; leave the "
        + "command out and a person checks that step. This REPLACES the item's whole scenario list, so "
        + "read the item first and send back the ones you want to keep.";

    /// <inheritdoc />
    public override string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "number": { "type": "integer" },
            "version": { "type": "integer", "description": "The version you read." },
            "scenarios": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "title": { "type": "string" },
                  "steps": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "properties": {
                        "action": { "type": "string", "description": "What is done." },
                        "expected": { "type": "string", "description": "What should happen." },
                        "command": { "type": "string", "description": "What to run to check it, if anything." }
                      },
                      "required": ["action", "expected"],
                      "additionalProperties": false
                    }
                  }
                },
                "required": ["title", "steps"],
                "additionalProperties": false
              }
            }
          },
          "required": ["number", "version", "scenarios"],
          "additionalProperties": false
        }
        """;

    /// <inheritdoc />
    public override StudioToolAccess Access => StudioToolAccess.Mutating;

    /// <inheritdoc />
    protected override async Task<StudioToolResult> RunAsync(
        StudioToolCall call, CancellationToken cancellationToken)
    {
        if (Number(call, "number") is not { } number)
        {
            return Rejected("number_required", "number", "Name the item's number.");
        }

        if (Number(call, "version") is not { } version)
        {
            return Rejected("version_required", "version", "Pass the version you read.");
        }

        var item = await TrackerFor(call).GetByNumberAsync(number, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return Rejected("item_not_found", "number", $"This project has no item #{number}.");
        }

        if (!call.Arguments.TryGetProperty("scenarios", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return Rejected("scenarios_required", "scenarios", "Send the whole scenario list, even if it is empty.");
        }

        var scenarios = new List<TestScenario>();
        foreach (var element in array.EnumerateArray())
        {
            var title = Read(element, "title");
            if (string.IsNullOrWhiteSpace(title))
            {
                return Rejected("scenario_title_required", "scenarios", "A scenario needs a title.");
            }

            var steps = new List<ScenarioStep>();
            if (element.TryGetProperty("steps", out var stepArray) && stepArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var stepElement in stepArray.EnumerateArray())
                {
                    var action = Read(stepElement, "action");
                    var expected = Read(stepElement, "expected");
                    if (string.IsNullOrWhiteSpace(action) || string.IsNullOrWhiteSpace(expected))
                    {
                        return Rejected(
                            "scenario_step_incomplete",
                            "scenarios",
                            "A step says what is done AND what should happen; both are needed.");
                    }

                    steps.Add(new ScenarioStep(
                        string.Empty, action, expected, Data: null, Command: Read(stepElement, "command")));
                }
            }

            scenarios.Add(new TestScenario(string.Empty, title, steps));
        }

        var updated = await TrackerFor(call).UpdateAsync(
            item.Id,
            new WorkItemPatch { Scenarios = Optional<IReadOnlyList<TestScenario>>.Of(scenarios) },
            version,
            ContextFor(call),
            cancellationToken).ConfigureAwait(false);

        if (!updated.IsOk)
        {
            return Refused(updated, "scenarios");
        }

        Announce(call);
        return Changed("scenarios_written", Full(updated.Value));
    }

    private static string? Read(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { } text
        && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;
}

/// <summary>
/// Runs a scenario and records the result as evidence (backlog 55). Mutating on purpose: a step's command
/// is text somebody wrote into the graph, and the graph is an injection surface, so running one is a
/// deliberate act a person approves rather than a quiet capability.
/// </summary>
public sealed class ScenarioRunTool(
    ITrackerResolver trackers,
    IWorkItemChanges changes,
    ScenarioRunner runner,
    Func<Fravashi.Domain.AgentSessions.AgentSessionId, string?> workingDirectory)
    : WorkItemToolBase(trackers, changes)
{
    /// <summary>The tool's bare name.</summary>
    public const string ToolName = "work_item_scenario_run";

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        "Runs one of a work item's test scenarios in this session's workspace copy and records what "
        + "happened as evidence on the item. Every step green with no errors is the only pass; a step with "
        + "no command, and every step after a failure, is reported as not run, and a run with any of those "
        + "is partial rather than a pass.";

    /// <inheritdoc />
    public override string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "number": { "type": "integer" },
            "scenario": { "type": "string", "description": "The scenario's id, as work_item_read reports it." }
          },
          "required": ["number", "scenario"],
          "additionalProperties": false
        }
        """;

    /// <inheritdoc />
    public override StudioToolAccess Access => StudioToolAccess.Mutating;

    /// <inheritdoc />
    protected override async Task<StudioToolResult> RunAsync(
        StudioToolCall call, CancellationToken cancellationToken)
    {
        if (Number(call, "number") is not { } number)
        {
            return Rejected("number_required", "number", "Name the item's number.");
        }

        if (Text(call, "scenario") is not { } scenarioId)
        {
            return Rejected("scenario_required", "scenario", "Name the scenario's id.");
        }

        var item = await TrackerFor(call).GetByNumberAsync(number, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return Rejected("item_not_found", "number", $"This project has no item #{number}.");
        }

        var attempt = await runner.AttemptAsync(
            call.Project,
            item.Id,
            scenarioId,
            workingDirectory(call.SessionId),
            ContextFor(call),
            cancellationToken).ConfigureAwait(false);

        if (attempt.AlreadyRunning)
        {
            // Backlog 293: a second start would be a second build of the application. The running one records its
            // result as evidence on the item when it ends, whoever is still waiting for it.
            return Rejected(
                "scenario_already_running",
                "scenario",
                $"This scenario is already running as run {attempt.RunId}; nothing was started. Its result is recorded "
                + $"as evidence on item #{number} when it ends, so read the item with work_item_read later instead of "
                + "running it again.");
        }

        if (attempt.Run is not { } run)
        {
            return Rejected(
                "scenario_not_found", "scenario", $"Item #{number} has no scenario with that id.");
        }

        Announce(call);
        return Changed("scenario_run", new JsonObject
        {
            ["run"] = attempt.RunId,
            ["result"] = StableNames.ToName(run.Result),
            ["passed"] = run.Passed,
            ["notRun"] = run.NotRun,
            ["steps"] = new JsonArray(
            [
                .. run.Steps.Select(s => (JsonNode)new JsonObject
                {
                    ["step"] = s.StepId,
                    ["outcome"] = s.Outcome.ToString(),
                    ["detail"] = s.Detail,
                }),
            ]),
        });
    }
}
