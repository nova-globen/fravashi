using Fravashi.Application.Sessions;
using Fravashi.Infrastructure.Sessions;

namespace Fravashi.Web.Components.Sessions;

/// <summary>
/// The ONE way a person's Approve or Deny leaves the studio's UI (work item #366). The approval card in
/// <see cref="SessionPane"/> and the Code page's diff header both call <see cref="Answer"/>, so there is a single
/// resolution path and no second route by which a decision can be made.
///
/// <para>
/// It changes nothing about who decides: the PreToolUse hook still fails closed and the broker resolves the
/// request exactly once (<see cref="LiveSessionRegistry.Answer"/>). What this adds is the binding: the answer is
/// for ONE request id, and it is refused unless that id is still pending on the very session the person was
/// looking at. A diff built for one approval can therefore never decide a different one, and a decision made in
/// one place leaves nothing for the other to apply.
/// </para>
/// </summary>
public static class SessionApprovalResolution
{
    /// <summary>
    /// Answers <paramref name="requestId"/> on <paramref name="session"/>. False, and nothing sent, when the request
    /// is not pending on that session any more (answered elsewhere, timed out, or another session's).
    /// </summary>
    public static bool Answer(
        LiveSessionRegistry registry, LiveAgentSession session, string requestId, bool approved, string? reason)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(session);

        if (string.IsNullOrEmpty(requestId) || session.Pending.All(p => !string.Equals(p.RequestId, requestId, StringComparison.Ordinal)))
        {
            return false;
        }

        // Backlog 220: a denial can carry the person's own words; without words it is the standard sentence.
        return registry.Answer(requestId, approved, ToolApprovalDecision.UserReason(approved, reason));
    }
}
