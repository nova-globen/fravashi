using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Xunit;

namespace Fravashi.Application.Tests;

/// <summary>
/// Backlog 25, review of 2026-09-14: a typed turn has to READ as one answer even when the model's prose and
/// its document differ. The two shapes named first are the ones the pinned CLI produced on the reviewer's live
/// runs, word for word.
/// </summary>
public class AgentTurnDraftsTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 14, 20, 0, 0, TimeSpan.Zero);

    private static MessageEmitted You(string text) => new(At, TranscriptRole.User, text);

    private static MessageEmitted Agent(string text) => new(At, TranscriptRole.Assistant, text);

    private static TurnCompleted Documented(string message) =>
        new(At, null, false, "completed",
            AgentTurnDocumentReader.Read($$"""{"version":"1","kind":"answer","message":{{System.Text.Json.JsonSerializer.Serialize(message)}}}"""));

    private static TurnCompleted Untyped() => new(At, null, false, "completed");

    /// <summary>The assistant texts a reader sees open.</summary>
    private static List<string> Visible(IReadOnlyList<AgentEvent> transcript)
    {
        var drafts = AgentTurnDrafts.Superseded(transcript);
        return [.. transcript
            .Select((e, i) => (e, i))
            .Where(x => x.e is MessageEmitted { Role: TranscriptRole.Assistant } && !drafts.Contains(x.i))
            .Select(x => ((MessageEmitted)x.e).Text)];
    }

    [Fact]
    public void A_reworded_answer_reads_once_as_the_documents()
    {
        // Live run 2: the prose and the document differ only in markdown, so the parser commits both.
        AgentEvent[] transcript =
        [
            You("What is 17 times 3?"),
            Agent("17 times 3 equals 51."),
            Agent("17 times 3 equals **51**."),
            Documented("17 times 3 equals **51**."),
        ];

        Visible(transcript).Should().Equal("17 times 3 equals **51**.");
        AgentTurnDrafts.Superseded(transcript).Should().Equal(1);
    }

    [Fact]
    public void A_sentence_about_the_tool_after_the_answer_is_folded_and_the_answer_stays_where_it_was()
    {
        // Live run 3: the document repeated the prose exactly (so the parser did not commit it again), and the
        // model then wrote a meta sentence after the nudge. The answer is the earlier bubble.
        AgentEvent[] transcript =
        [
            You("What is 17 times 3?"),
            Agent("17 times 3 is **51**."),
            Agent("You requested that I call the StructuredOutput tool. Here is your response in structured format."),
            Documented("17 times 3 is **51**."),
        ];

        Visible(transcript).Should().Equal("17 times 3 is **51**.");
    }

    [Fact]
    public void A_prompt_the_studio_sent_starts_a_new_stretch_as_a_persons_does()
    {
        // Backlog 184: the resume prompt is the studio's, not the person's, but it still begins a turn, so what the
        // agent said before a restart cut it off is never folded into the resumed turn's answer.
        AgentEvent[] transcript =
        [
            Agent("Half of an answer the restart cut off."),
            new MessageEmitted(At, TranscriptRole.StudioPrompt, "Check what your last step left behind, then continue."),
            Agent("All done."),
            Documented("All done."),
        ];

        Visible(transcript).Should().Equal("Half of an answer the restart cut off.", "All done.");
    }

    [Fact]
    public void Narration_before_a_tool_card_is_never_folded()
    {
        AgentEvent[] transcript =
        [
            You("How long is the readme?"),
            Agent("Let me read the readme first."),
            new ToolCallStarted(At, "t1", "Read", "README.md"),
            new ToolCallCompleted(At, "t1", "Read", true, "..."),
            Agent("It has 12 lines."),
            Agent("The readme has **12** lines."),
            Documented("The readme has **12** lines."),
        ];

        Visible(transcript).Should().Equal("Let me read the readme first.", "The readme has **12** lines.");
    }

    [Fact]
    public void A_turn_without_a_document_folds_nothing()
    {
        AgentEvent[] transcript = [You("hi"), Agent("First."), Agent("Second."), Untyped()];

        AgentTurnDrafts.Superseded(transcript).Should().BeEmpty();
    }

    [Fact]
    public void A_blank_document_message_folds_nothing()
    {
        AgentEvent[] transcript = [You("hi"), Agent("The answer in prose."), Documented("")];

        AgentTurnDrafts.Superseded(transcript).Should().BeEmpty();
    }

    [Fact]
    public void A_stretch_that_does_not_hold_the_documents_text_folds_nothing()
    {
        // The document matched something said before the tool card, so nothing beside the final prose is the
        // answer: folding that prose would leave the reader with no answer at the end of the turn.
        AgentEvent[] transcript =
        [
            You("q"),
            Agent("42"),
            new ToolCallStarted(At, "t1", "Read", "a.txt"),
            new ToolCallCompleted(At, "t1", "Read", true, "..."),
            Agent("Actually, let me reconsider."),
            Documented("42"),
        ];

        AgentTurnDrafts.Superseded(transcript).Should().BeEmpty();
    }

    [Fact]
    public void Each_turn_is_folded_on_its_own()
    {
        AgentEvent[] transcript =
        [
            You("one"),
            Agent("draft one"),
            Agent("answer one"),
            Documented("answer one"),
            You("two"),
            Agent("answer two"),
            Documented("answer two"),
        ];

        Visible(transcript).Should().Equal("answer one", "answer two");
    }

    [Fact]
    public void An_approval_between_the_prose_and_the_turns_end_does_not_hide_the_stretch()
    {
        // The turn-ending StructuredOutput call goes through the hook after the final prose.
        AgentEvent[] transcript =
        [
            You("q"),
            Agent("draft"),
            new ToolApprovalResolved(At, "r1", "StructuredOutput", true, string.Empty),
            Agent("answer"),
            Documented("answer"),
        ];

        Visible(transcript).Should().Equal("answer");
    }
}
