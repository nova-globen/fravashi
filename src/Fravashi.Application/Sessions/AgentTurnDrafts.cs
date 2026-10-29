using Fravashi.Domain.AgentSessions;

namespace Fravashi.Application.Sessions;

/// <summary>
/// Which assistant messages of a transcript a reader should see folded away because the turn's DOCUMENT
/// replaced them (backlog 25, review of 2026-09-14). The parser commits every text block where it arrived
/// and only drops the document when its text is byte identical to a block already committed, which is
/// right for the data and wrong for the reading: driven against the pinned CLI, a typed turn came back as
/// "17 times 3 equals 51." followed by the document's "17 times 3 equals **51**.", and on another run as
/// the answer followed by "You requested that I call the StructuredOutput tool. Here is your response in
/// structured format." Both are the agent's own words, so nothing is deleted; this decides only what is
/// shown open.
/// <para>
/// This is the fix <c>docs/plans/typed-agent-turns.md</c> section 3 reserved for exactly this case: a
/// presentation choice over complete data, in the renderer's input, and not a fifth parser heuristic.
/// </para>
/// <para>
/// <b>The rule, and why it is narrow.</b> A turn that ended in a document with a readable message has a
/// FINAL STRETCH: the assistant messages written after the turn's last tool card (or after the user's
/// turn, when there was no tool). The parser commits the document's message at the result line, which is
/// inside that stretch, unless the same text was already said. When exactly that text is in the stretch,
/// every OTHER assistant message in the stretch is a
/// draft the document superseded: a rewording, a meta sentence about the tool, a repeat. Prose written
/// before a tool card (narration of work) is never folded, a turn with no document folds nothing (its
/// prose is the answer, ADR 0022 outcome ii), and a stretch that does not hold the document's text folds
/// nothing either, because then the answer the reader would be left with is not beside the drafts. That
/// last case is captured, not hypothetical: in <c>claude-typed-retry-with-tool-turn.jsonl</c> the answer is
/// written before a <c>Read</c> and the identical document after it, so the stretch is empty and the answer
/// stays open above the card where it was written.
/// </para>
/// </summary>
public static class AgentTurnDrafts
{
    /// <summary>The transcript positions of the assistant messages a document superseded.</summary>
    public static IReadOnlySet<int> Superseded(IReadOnlyList<AgentEvent> transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        HashSet<int> superseded = [];
        List<int> stretch = [];
        for (var index = 0; index < transcript.Count; index++)
        {
            switch (transcript[index])
            {
                case MessageEmitted { Role: TranscriptRole.Assistant }:
                    stretch.Add(index);
                    break;

                // Anything the reader reads as the turn doing or receiving something starts a new stretch.
                // Approval events are deliberately NOT here: the turn-ending StructuredOutput call can be
                // answered through the hook after the final prose, and a real tool's approval always sits
                // beside that tool's own card.
                case MessageEmitted { Role: TranscriptRole.User or TranscriptRole.StudioPrompt }:
                case ToolCallStarted:
                case ToolCallCompleted:
                case SessionStarted:
                case SessionEnded:
                    stretch.Clear();
                    break;

                case TurnCompleted turn:
                    Fold(transcript, stretch, turn, superseded);
                    stretch.Clear();
                    break;
            }
        }

        return superseded;
    }

    private static void Fold(
        IReadOnlyList<AgentEvent> transcript, List<int> stretch, TurnCompleted turn, HashSet<int> superseded)
    {
        if (turn.Document?.Message is not { } message || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var answer = message.Trim();
        var kept = stretch.FindIndex(i => string.Equals(
            ((MessageEmitted)transcript[i]).Text.Trim(), answer, StringComparison.Ordinal));
        if (kept < 0)
        {
            return;
        }

        for (var position = 0; position < stretch.Count; position++)
        {
            if (position != kept)
            {
                superseded.Add(stretch[position]);
            }
        }
    }
}
