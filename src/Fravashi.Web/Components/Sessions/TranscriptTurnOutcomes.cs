using System.Globalization;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Web.Components.Sessions;

/// <summary>
/// What a transcript has to say about how each TURN ended, read the same way for a live conversation and a
/// stored one (backlog 25 increments 5 and 6), so the Sessions page's history and the pane can never
/// disagree about whether an answer was typed or a turn failed.
/// </summary>
public static class TranscriptTurnOutcomes
{
    /// <summary>
    /// The positions of the assistant messages that were a typed session's UNTYPED answer: ADR 0022 outcome
    /// ii, where the session asked for a document and the turn succeeded without one because the model gave
    /// up on the schema and answered in prose.
    /// <para>
    /// Decided 2026-09-14: that prose is committed once as the assistant message (the parser already
    /// did, where the model wrote it), it is marked quietly rather than raised as an error, it is counted,
    /// and nothing is resent. The mark goes on the turn's LAST assistant message, which is where a document
    /// would have been read; narration earlier in the same turn is left as it is on a typed turn too.
    /// </para>
    /// <para>
    /// Whether a session asked for a document is read off its own <see cref="SessionStarted.TypedTurns"/>,
    /// the most recent one before the turn, because a continued conversation carries its predecessor's
    /// history and each process was started with its own flags. A turn that ENDED in error is not an
    /// untyped answer: outcomes iii and iv are failed turns and say so through <see cref="FailedTurnNote"/>.
    /// </para>
    /// </summary>
    public static IReadOnlySet<int> UntypedAnswers(IReadOnlyList<AgentEvent> transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        HashSet<int> untyped = [];
        var typed = false;
        var lastAnswer = -1;
        for (var index = 0; index < transcript.Count; index++)
        {
            switch (transcript[index])
            {
                case SessionStarted started:
                    typed = started.TypedTurns;
                    lastAnswer = -1;
                    break;

                case MessageEmitted { Role: TranscriptRole.Assistant }:
                    lastAnswer = index;
                    break;

                case TurnCompleted turn:
                    if (typed && turn.Document is null && !turn.IsError && lastAnswer >= 0)
                    {
                        untyped.Add(lastAnswer);
                    }

                    lastAnswer = -1;
                    break;
            }
        }

        return untyped;
    }

    /// <summary>
    /// The sentence a turn that FAILED gets in the transcript, or null for any turn that did not. Outcome
    /// iii names the attempts and the validator's last complaint, which is the most actionable thing the
    /// studio has when a schema is genuinely wrong; outcome iv names the budget in the CLI's own words.
    /// </summary>
    public static string? FailedTurnNote(TurnCompleted turn)
    {
        ArgumentNullException.ThrowIfNull(turn);

        if (turn.Document is not null)
        {
            return null;
        }

        switch (turn.TerminalReason)
        {
            case AgentTurnEnding.RetriesExhausted:
                var attempts = turn.ValidationFailures > 0
                    ? "after " + turn.ValidationFailures.ToString(CultureInfo.InvariantCulture)
                        + (turn.ValidationFailures == 1 ? " attempt" : " attempts")
                    : "on every attempt the agent CLI allows";
                var last = string.IsNullOrWhiteSpace(turn.LastValidationError)
                    ? string.Empty
                    : " Last error: " + turn.LastValidationError.Trim();
                return "This turn failed validation " + attempts + ", so it ended without an answer." + last;

            case AgentTurnEnding.BudgetExhausted:
                var budget = string.IsNullOrWhiteSpace(turn.ErrorText)
                    ? "it reached the per-session budget"
                    : turn.ErrorText.Trim().TrimEnd('.');
                return "This turn stopped at the conversation's spending ceiling: " + budget + ". "
                    + "The agent CLI enforces that ceiling and can overshoot it.";

            default:
                return null;
        }
    }
}
