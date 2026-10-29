namespace Fravashi.Domain.WorkItems.AgentLayer;

/// <summary>What happened to one step of a scenario when it was run (backlog 55).</summary>
public enum StepOutcome
{
    /// <summary>It ran and ended with no error.</summary>
    Passed,

    /// <summary>It ran and ended with an error.</summary>
    Failed,

    /// <summary>
    /// It was not run. A step with nothing runnable on it is checked by a person, and a step after a
    /// failure is not reached; both are this, and neither is a pass.
    /// </summary>
    NotRun,
}

/// <summary>What one step did.</summary>
/// <param name="StepId">Which step, so a result can be read against the scenario it came from.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Detail">What the studio saw: the exit code and the last of the output, or why it did not run.</param>
public sealed record StepResult(string StepId, StepOutcome Outcome, string Detail);

/// <summary>
/// The result of running a whole scenario (backlog 55). The rule it encodes is the item's acceptance, and
/// it is here rather than in a runner because it is the sentence the product is making, not a detail of
/// how a command was started: <b>every step green with zero errors is the only pass, and a partial run is
/// reported as partial, never as a pass.</b>
/// </summary>
public sealed record ScenarioRun(string ScenarioId, IReadOnlyList<StepResult> Steps)
{
    /// <summary>What the whole run says, by the only rule this product accepts.</summary>
    public EvidenceResult Result
    {
        get
        {
            if (Steps.Count == 0)
            {
                // A scenario with no steps proves nothing. It is not a pass, and calling it one would make
                // "verified" mean "nobody wrote a check".
                return EvidenceResult.Partial;
            }

            if (Steps.Any(s => s.Outcome == StepOutcome.Failed))
            {
                return EvidenceResult.Fail;
            }

            return Steps.All(s => s.Outcome == StepOutcome.Passed)
                ? EvidenceResult.Pass
                : EvidenceResult.Partial;
        }
    }

    /// <summary>How many steps ran and ended with no error.</summary>
    public int Passed => Steps.Count(s => s.Outcome == StepOutcome.Passed);

    /// <summary>How many steps were not run at all.</summary>
    public int NotRun => Steps.Count(s => s.Outcome == StepOutcome.NotRun);

    /// <summary>
    /// One sentence a person can read, saying what happened and being explicit about what did NOT run.
    /// A summary that said "3 of 5 passed" and stopped would let a partial run read as most of a pass.
    /// </summary>
    public string Summarise(string scenarioTitle)
    {
        var total = Steps.Count;
        if (total == 0)
        {
            return $"'{scenarioTitle}' has no steps, so running it proved nothing.";
        }

        var failed = Steps.Count(s => s.Outcome == StepOutcome.Failed);
        if (failed > 0)
        {
            return $"'{scenarioTitle}' failed: {failed} of {total} steps ended with an error"
                + (NotRun > 0 ? $", and {NotRun} were not reached." : ".");
        }

        return NotRun > 0
            ? $"'{scenarioTitle}' is unfinished: {Passed} of {total} steps passed and {NotRun} were not run, "
                + "so this is not a pass."
            : $"'{scenarioTitle}' passed: all {total} steps ran and ended with no error.";
    }
}
