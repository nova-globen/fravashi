using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>
/// What the transcript of a conversation that ran BEFORE counting existed can say about the operator's attention
/// (backlog 244), as attention rows marked reconstructed. Schema step 38 runs it once over every stored transcript.
/// <para>
/// What it can give: every card asked and how it was answered (a request and its resolution, the wait being the gap
/// between them), every refusal the studio made without asking, every trusted edit, every turn a person typed and
/// every commit review's note. What it cannot: the reads and graph calls the studio allowed without asking, because
/// the transcript never kept those, and every surface showing a reconstructed count says so.
/// </para>
/// <para>
/// It reads the same closed facts the live count does (a tool's name, who decided, whether the reason was the fixed
/// sentence, the review note's fixed verdict words), never what a call carried.
/// </para>
/// </summary>
public static class AttentionReconstruction
{
    /// <summary>How a review note written by <see cref="CommitReview.TranscriptNote"/> begins.</summary>
    private const string ReviewNotePrefix = "Commit review: ";

    /// <summary>The start of the timeout sentence the broker denies an unanswered card with.</summary>
    private const string TimeoutPrefix = "The studio saw no answer within ";

    /// <summary>Attention rows rebuilt from one conversation's transcript, oldest first.</summary>
    public static IReadOnlyList<AttentionEntry> From(AgentSessionId session, IEnumerable<AgentEvent> transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        var asked = new Dictionary<string, ToolApprovalRequested>(StringComparer.Ordinal);
        var entries = new List<AttentionEntry>();
        foreach (var e in transcript)
        {
            switch (e)
            {
                case ToolApprovalRequested request:
                    asked[request.RequestId] = request;
                    break;

                case ToolApprovalResolved resolved when asked.Remove(resolved.RequestId, out var request):
                    entries.Add(AttentionEntry.Decision(
                        session,
                        AttentionToolKinds.Of(request.ToolName),
                        Outcome(resolved),
                        resolved.Timestamp - request.Timestamp,
                        resolved.Timestamp,
                        reconstructed: true));
                    break;

                case ToolCallRefused refused:
                    entries.Add(AttentionEntry.Decision(
                        session, AttentionToolKinds.Of(refused.ToolName), AttentionOutcome.Refused, TimeSpan.Zero, refused.Timestamp, reconstructed: true));
                    break;

                case ToolCallTrusted trusted:
                    entries.Add(AttentionEntry.Decision(
                        session, AttentionToolKinds.Of(trusted.ToolName), AttentionOutcome.Auto, TimeSpan.Zero, trusted.Timestamp, reconstructed: true));
                    break;

                case MessageEmitted { Role: TranscriptRole.User } turn:
                    entries.Add(AttentionEntry.Turn(session, turn.Timestamp, reconstructed: true));
                    break;

                case MessageEmitted { Role: TranscriptRole.System } note when Review(note.Text) is { } review:
                    entries.Add(AttentionEntry.ReviewOf(session, review, note.Timestamp, reconstructed: true));
                    break;
            }
        }

        return entries;
    }

    /// <summary>How a resolved card ended, by the same rule the broker counts a live one with.</summary>
    internal static AttentionOutcome Outcome(ToolApprovalResolved resolved) =>
        AgentApprovalBroker.Answered(
            new ToolApprovalDecision(resolved.Approved, resolved.Reason, resolved.DecidedBy),
            timedOut: resolved.Reason.StartsWith(TimeoutPrefix, StringComparison.Ordinal));

    /// <summary>The verdict of a review note, or null when the text is not one.</summary>
    internal static CommitReviewOutcome? Review(string? text)
    {
        if (text is null || !text.StartsWith(ReviewNotePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var verdict = text[ReviewNotePrefix.Length..];
        return verdict.StartsWith("pass", StringComparison.Ordinal) ? CommitReviewOutcome.Passed
            : verdict.StartsWith("fail", StringComparison.Ordinal) ? CommitReviewOutcome.Failed
            : verdict.StartsWith("not reviewed", StringComparison.Ordinal) ? CommitReviewOutcome.Off
            : verdict.StartsWith("no verdict (bound", StringComparison.Ordinal) ? CommitReviewOutcome.TimedOut
            : verdict.StartsWith("no verdict", StringComparison.Ordinal) ? CommitReviewOutcome.Errored
            : null;
    }
}
