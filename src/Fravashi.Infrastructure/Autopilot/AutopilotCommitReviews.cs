using Fravashi.Application.Autopilot;
using Fravashi.Application.Sessions;
using Fravashi.Domain.WorkItems;

namespace Fravashi.Infrastructure.Autopilot;

/// <summary>
/// The real <see cref="IAutopilotCommitReviews"/> (#309): the commit reviewer already ran, synchronously,
/// inside the approval broker's handling of the building session's own <c>attempt_commit</c> call (backlog
/// 243), captured by <see cref="AutopilotBuilderSessions"/>. By the time the engine's Reviewing stage asks,
/// the answer is already there; a review that is not there yet (or already consumed) is exactly "a malformed
/// or missing answer" (ADR 0031 decision 10), so this throws rather than guessing a verdict, and the engine
/// turns that into a <see cref="Fravashi.Domain.Autopilot.AutopilotStopKind.SeamFailed"/> stop.
/// </summary>
public sealed class AutopilotCommitReviews(AutopilotBuilderSessions sessions) : IAutopilotCommitReviews
{
    /// <inheritdoc />
    public Task<CommitReview> ReviewAsync(WorkItemId item, CancellationToken ct = default)
    {
        if (sessions.TakeReview(item) is { } review)
        {
            return Task.FromResult(review);
        }

        throw new InvalidOperationException(
            $"No commit review is recorded for item '{item.Value}' yet. Either its building session has not "
                + "committed, or the review has not finished.");
    }
}
