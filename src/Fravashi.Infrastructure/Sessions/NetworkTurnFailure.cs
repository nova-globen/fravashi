using Fravashi.Application.Network;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>
/// Whether an agent turn, or the agent process, ended because the NETWORK failed (backlog 229, plan @55). The one
/// place that decides it, so a failed turn and an exited process cannot be judged two ways. The classification
/// itself is slice A's <see cref="NetworkFailure.ClassifyCliText"/>; this only chooses which of the CLI's words to
/// hand it, and which endings are never a network failure whatever they say.
/// <para>
/// Measured against the pinned 2.1.280 on 2026-10-07, with an unresolvable API host: after ten retries the CLI writes
/// a SYNTHETIC assistant message, "API Error: Can't reach the API server, check your internet or DNS (ENOTFOUND)",
/// marked <c>is_api_error_message</c>, then a result line with <c>is_error</c> true, <c>terminal_reason</c>
/// <c>api_error</c>, the same sentence as its <c>result</c> and no <c>errors</c>.
/// </para>
/// <para>
/// ONLY the CLI's own words are read: its <c>errors</c> and the message it marked as its own
/// (<see cref="ApiErrorReported"/>). Never the agent's prose, and never the <c>result</c> string, which on other endings
/// can be the agent's last words: a turn that failed for some other reason after the agent wrote about somebody's
/// ECONNREFUSED must not be continued as if the network had dropped.
/// </para>
/// </summary>
public static class NetworkTurnFailure
{
    /// <summary>
    /// What a completed turn says about the network. <see cref="NetworkFailure.None"/> for a turn that did not fail,
    /// and for the two endings that have their own handling (validation attempts used up, the budget reached), whose
    /// own text never makes them a network failure.
    /// </summary>
    /// <param name="turn">The turn's result.</param>
    /// <param name="cliApiError">The text of the CLI's own API error message in that turn (<see cref="ApiErrorReported"/>), or null.</param>
    public static NetworkFailure Of(TurnCompleted turn, string? cliApiError)
    {
        ArgumentNullException.ThrowIfNull(turn);
        if (!turn.IsError
            || turn.Document is not null
            || turn.TerminalReason is AgentTurnEnding.RetriesExhausted or AgentTurnEnding.BudgetExhausted)
        {
            return NetworkFailure.None;
        }

        return First(turn.ErrorText, cliApiError);
    }

    /// <summary>
    /// What a process ending says about the network: only a Failed ending can be one (a stop is the person's, a clean
    /// exit is no failure), and only when its own words, or the failed turn just before it, name a network failure.
    /// </summary>
    public static NetworkFailure OfExit(SessionEnded ended, NetworkFailure? lastTurn)
    {
        ArgumentNullException.ThrowIfNull(ended);
        if (ended.FinalState != AgentSessionState.Failed || ended.Resumable)
        {
            return NetworkFailure.None;
        }

        var said = NetworkFailure.ClassifyCliText(ended.FailureReason);
        return said.IsNetwork ? said : lastTurn is { IsNetwork: true } turn ? turn : NetworkFailure.None;
    }

    private static NetworkFailure First(params string?[] texts)
    {
        foreach (var text in texts)
        {
            if (NetworkFailure.ClassifyCliText(text) is { IsNetwork: true } found)
            {
                return found;
            }
        }

        return NetworkFailure.None;
    }
}
