using Fravashi.Domain.AgentSessions;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>
/// Whether an agent turn ended because the credential BROKER refused its token (backlog 235, plan @60). The same rule as
/// <see cref="NetworkTurnFailure"/>: only the CLI's own words are read, never the agent's prose and never the
/// <c>result</c> string.
/// <para>
/// Measured against the pinned 2.1.280 on 2026-10-07, with a local stand-in for the broker that answered every model
/// call as the broker answers an expired bearer (401 <c>{"error":"expired"}</c>): the CLI retried ten times on the SAME
/// bearer it started with (about three minutes), wrote a synthetic message marked <c>is_api_error_message</c>,
/// "Failed to authenticate. API Error: 401 {"error":"expired"}", then a result with <c>is_error</c> true, terminal reason
/// <c>api_error</c> and no <c>errors</c>, and stayed alive on stdin. Its next turn sent the same bearer again
/// (<c>Sessions/Fixtures/Network/claude-api-error-broker-token-expired.jsonl</c>).
/// </para>
/// </summary>
public static class CredentialTurnFailure
{
    /// <summary>The CLI's own words for a model call answered 401.</summary>
    public const string Marker = "API Error: 401";

    /// <summary>
    /// True when <paramref name="turn"/> failed because the model call was answered 401. Callers ask only where the broker
    /// carries the runtime: there, the broker is what answers, and a 401 means the token was refused.
    /// </summary>
    /// <param name="turn">The turn's result.</param>
    /// <param name="cliApiError">The text of the CLI's own API error message in that turn, or null.</param>
    public static bool Refused(TurnCompleted turn, string? cliApiError)
    {
        ArgumentNullException.ThrowIfNull(turn);
        if (!turn.IsError
            || turn.Document is not null
            || turn.TerminalReason is AgentTurnEnding.RetriesExhausted or AgentTurnEnding.BudgetExhausted)
        {
            return false;
        }

        return Says(turn.ErrorText) || Says(cliApiError);
    }

    private static bool Says(string? text) =>
        text is not null && text.Contains(Marker, StringComparison.Ordinal)
        && (text.Length == text.IndexOf(Marker, StringComparison.Ordinal) + Marker.Length
            || !char.IsAsciiDigit(text[text.IndexOf(Marker, StringComparison.Ordinal) + Marker.Length]));
}
