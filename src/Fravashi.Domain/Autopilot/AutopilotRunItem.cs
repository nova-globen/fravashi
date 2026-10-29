using Fravashi.Domain.WorkItems;

namespace Fravashi.Domain.Autopilot;

/// <summary>One marked item in a run's queue, in board order, with everything the engine tracks about it.</summary>
/// <param name="Item">The work item.</param>
/// <param name="Order">Its position in the queue (board order).</param>
/// <param name="Stage">Where it has got to.</param>
/// <param name="SpentUsd">What has been spent on it so far.</param>
/// <param name="FailedReviews">How many commit reviews have failed for it.</param>
/// <param name="StartedUtc">When it left <see cref="AutopilotItemStage.Queued"/>, null until then.</param>
/// <param name="BranchHeadSha">Its attempt branch's head, once the builder has made a commit.</param>
/// <param name="ParkedReason">Why it is parked, set only while <see cref="Stage"/> is <see cref="AutopilotItemStage.Parked"/>.</param>
/// <param name="Prompt">The operator's prompt for it, once written.</param>
/// <param name="WaitingSinceUtc">
/// When this item first asked for a person and the run started waiting (ADR 0031 decision 12, #305), while it
/// is still in its ORIGINAL stage rather than parked: null while it has never asked, or once the wait is over
/// one way or another (it was answered in time, or the wait ran out and it actually parked). Never set at the
/// same time <see cref="Stage"/> is <see cref="AutopilotItemStage.Parked"/>: parking clears it, because there
/// is nothing left to time out once a person is no longer being waited on within this run.
/// </param>
/// <param name="WaitingForCapacityReason">
/// Words for the autopilot (#308) when this item's own fresh build start was refused by the concurrency gate (the
/// cap or the memory floor, ADR 0031 decision 16): still in its ORIGINAL stage, spending NO limit at all
/// while it carries this (host note point 4), including <see cref="AutopilotLimits.TimePerItem"/>, which the
/// engine keeps from firing by rebasing <see cref="StartedUtc"/> to "now" on every tick this stays set, the
/// same rebase <c>AutopilotDiscipline.OnRestart</c> already gives a Queued or Prompting item across an
/// outage. A capacity refusal is not a person to wait on and must never park; it ends only when a slot frees
/// (a later attempt is tried and the gate allows it) or a person stops the run. Cleared the moment a later
/// attempt actually starts.
/// </param>
public sealed record AutopilotRunItem(
    WorkItemId Item,
    int Order,
    AutopilotItemStage Stage,
    decimal SpentUsd,
    int FailedReviews,
    DateTimeOffset? StartedUtc,
    string? BranchHeadSha,
    string? ParkedReason,
    string? Prompt = null,
    DateTimeOffset? WaitingSinceUtc = null,
    string? WaitingForCapacityReason = null)
{
    /// <summary>A fresh queue entry, waiting to start.</summary>
    public static AutopilotRunItem Queue(WorkItemId item, int order) =>
        new(item, order, AutopilotItemStage.Queued, 0m, 0, null, null, null);
}
