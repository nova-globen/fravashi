using System.Globalization;
using Fravashi.Application.Notifications;
using Fravashi.Application.Sessions;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Infrastructure.Notifications;

/// <summary>
/// A small, deliberately pure snapshot of what one live session carries (backlog 304), so
/// <see cref="WaitingSessionReader"/> is unit tested directly on a hand-built snapshot value rather than only
/// through a real <c>LiveSessionRegistry</c> and a running session.
/// </summary>
public readonly record struct WaitingSessionSnapshot(
    AgentSessionId Id,
    string WorkspaceId,
    bool TurnInFlight,
    IReadOnlyList<PendingToolApproval> Pending,
    IReadOnlyList<AgentEvent> Transcript);

/// <summary>
/// Decides whether one session is waiting on a person, and on what, the same way
/// <c>LiveSessionsCard.Describe</c>/<c>OpenQuestions</c> already do for the home page's live-sessions card
/// (backlog 35): one pending approval collapses a session's several into one entry, and one open question the
/// same way. Kept separate from that card (which stays untouched) so this reasoning is testable on its own.
/// </summary>
public static class WaitingSessionReader
{
    public static WaitingForPersonEntry? Read(WaitingSessionSnapshot session)
    {
        if (session.Pending.Count > 0)
        {
            var since = session.Pending.Min(p => p.RequestedUtc);
            var more = session.Pending.Count > 1
                ? string.Create(CultureInfo.InvariantCulture, $" and {session.Pending.Count - 1} more")
                : string.Empty;
            return new WaitingForPersonEntry(
                WaitingForPersonKind.Approval,
                // Work item #356: the call in words, never the raw mcp__ name.
                $"Waiting on your approval: {StudioToolDescriptions.ForPeople(session.Pending[0].ToolName, session.Pending[0].Input)}{more}",
                since,
                session.WorkspaceId,
                SessionId: session.Id);
        }

        if (!session.TurnInFlight && OpenQuestions(session.Transcript) is { } questions)
        {
            var more = questions.Questions.Count > 1
                ? string.Create(CultureInfo.InvariantCulture, $" (and {questions.Questions.Count - 1} more)")
                : string.Empty;
            return new WaitingForPersonEntry(
                WaitingForPersonKind.Question,
                $"Waiting on your answer: {Shorten(questions.Questions[0].Question)}{more}",
                questions.Timestamp,
                session.WorkspaceId,
                SessionId: session.Id);
        }

        return null;
    }

    private readonly record struct OpenQuestionsResult(IReadOnlyList<AgentTurnQuestion> Questions, DateTimeOffset Timestamp);

    /// <summary>
    /// The questions the newest completed turn asked, while no user turn has answered them, with that turn's
    /// own timestamp: the same test <c>LiveSessionsCard.OpenQuestions</c> uses
    /// (<see cref="AgentTurnQuestionsReader.WasAnswered"/>), so the two readers cannot disagree about whether
    /// something is still open.
    /// </summary>
    private static OpenQuestionsResult? OpenQuestions(IReadOnlyList<AgentEvent> transcript)
    {
        for (var i = transcript.Count - 1; i >= 0; i--)
        {
            if (transcript[i] is MessageEmitted { Role: TranscriptRole.User })
            {
                return null;
            }

            if (transcript[i] is TurnCompleted turn)
            {
                if (AgentTurnQuestionsReader.WasAnswered(transcript, i))
                {
                    return null;
                }

                var questions = AgentTurnQuestionsReader.Read(turn.Document);
                return questions.Count == 0 ? null : new OpenQuestionsResult(questions, turn.Timestamp);
            }
        }

        return null;
    }

    private static string Shorten(string text)
    {
        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length <= 90 ? single : single[..87] + "...";
    }
}
