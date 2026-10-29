using Fravashi.Domain.AgentSessions;

namespace Fravashi.Domain.Autopilot;

/// <summary>
/// What to do with an item that has no scenario or design when autopilot reaches it (ADR 0031 decision 2).
/// Default is <see cref="Write"/>.
/// </summary>
public enum MissingScenarioPolicy
{
    /// <summary>Write one before building. The shipped default.</summary>
    Write,

    /// <summary>Refuse the item; it is left for a person.</summary>
    Refuse,

    /// <summary>Treat the scenario as optional and verify without one.</summary>
    Optional,
}

/// <summary>Which runtime and model fill a role in a run (the building agent or the operator).</summary>
/// <param name="Runtime">The agent runtime.</param>
/// <param name="Model">The model id passed to it.</param>
public sealed record AutopilotRole(AgentTool Runtime, string Model);

/// <summary>
/// The five limits a dispatch sets (ADR 0031 decision 6), each with a default a dispatch may lower or raise. The
/// figures here are a host decision, recorded in plan @114: not named in the ADR, chosen so nothing in this slice
/// is unbounded while #298's dispatch form does not exist yet to set them per run.
/// </summary>
/// <param name="MoneyPerItemUsd">The most one item may spend before it stops.</param>
/// <param name="MoneyPerRunUsd">The most the whole run may spend before it stops.</param>
/// <param name="FailedCommitReviewsPerItem">The ceiling of failed commit reviews for one item; reaching it stops the item.</param>
/// <param name="TimePerItem">The longest one item may run before it stops.</param>
/// <param name="NoProgressWindow">
/// The longest the run may go without progress (the queue shrinking or a new commit on the current item's
/// branch) before it stops.
/// </param>
public sealed record AutopilotLimits(
    decimal MoneyPerItemUsd,
    decimal MoneyPerRunUsd,
    int FailedCommitReviewsPerItem,
    TimeSpan TimePerItem,
    TimeSpan NoProgressWindow)
{
    /// <summary>The shipped defaults.</summary>
    public static AutopilotLimits Default { get; } = new(
        MoneyPerItemUsd: 15m,
        MoneyPerRunUsd: 100m,
        FailedCommitReviewsPerItem: 3,
        TimePerItem: TimeSpan.FromHours(2),
        NoProgressWindow: TimeSpan.FromMinutes(30));
}

/// <summary>
/// What a dispatch configures (ADR 0031 decision 2). The commit reviewer is deliberately not named here: ADR
/// decision 2 says "commit reviewer as Settings says", and that role already lives in
/// <c>CommitReviewerSetting</c> (backlog 243).
/// </summary>
/// <param name="Limits">The five stop conditions.</param>
/// <param name="MissingScenario">What to do with an item that has no scenario or design.</param>
/// <param name="PersonWaitTimeout">How long the run waits for a person before parking the item and moving on.</param>
/// <param name="Builder">The building agent's runtime and model.</param>
/// <param name="Operator">The operator's runtime and model.</param>
/// <param name="ConcurrencyCapOverride">
/// A lower concurrency cap for this run than the Settings one (ADR 0031 decision 16). Null means "use the
/// Settings cap"; an override that is not a positive number strictly below the Settings cap is ignored, never
/// clamped, by <see cref="AutopilotConcurrency.Effective"/>, since a dispatch may only lower the cap, never
/// raise it.
/// </param>
/// <param name="EffectiveConcurrencyCap">
/// <see cref="AutopilotConcurrency.Effective"/>, resolved ONCE by <c>AutopilotDispatcher</c> at dispatch time
/// from the Settings cap and <see cref="ConcurrencyCapOverride"/>, and never re-read from live Settings after
/// (#308). This is what decides whether every item of the run shares one workspace or gets its own worktree
/// (decision 2): a Settings change made WHILE the run is open must never flip a run between the two, since a
/// run whose items already share one workspace cannot safely be handed worktrees mid-flight, and the reverse
/// would leave a worktree-backed item started before the change orphaned from the shared-workspace machinery.
/// Defaults to 1 (sequential, unchanged), which is also what a run saved before this field existed reads back
/// as.
/// </param>
public sealed record AutopilotDispatchSettings(
    AutopilotLimits Limits,
    MissingScenarioPolicy MissingScenario,
    TimeSpan PersonWaitTimeout,
    AutopilotRole Builder,
    AutopilotRole Operator,
    int? ConcurrencyCapOverride = null,
    int EffectiveConcurrencyCap = 1)
{
    /// <summary>The shipped defaults: building agent on Opus 5.5, operator on Sonnet 5 (ADR decision 2).</summary>
    public static AutopilotDispatchSettings Default { get; } = new(
        AutopilotLimits.Default,
        MissingScenarioPolicy.Write,
        PersonWaitTimeout: TimeSpan.FromHours(1),
        Builder: new AutopilotRole(AgentTool.Claude, "claude-opus-5-5"),
        Operator: new AutopilotRole(AgentTool.Claude, "claude-sonnet-5"));
}
