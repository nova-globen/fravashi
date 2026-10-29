using System.Globalization;
using AwesomeAssertions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Sessions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// The parser under <c>--json-schema</c> (ADR 0022, spec <c>docs/plans/typed-agent-turns.md</c>).
/// <para>
/// Every fixture here is raw stdout from claude <b>2.1.222</b>, driven in the <c>fravashi</c> container on
/// 2026-08-06 with the production flag set plus <c>--json-schema</c>, model
/// <c>claude-haiku-4-5-20251001</c>. Nothing in this file is hand-written except the lines that provoke a
/// shape the CLI would not hand out on demand (a null <c>structured_output</c>, a tool block index that
/// was never opened), and those are marked where they appear.
/// </para>
/// <para>
/// How each terminal outcome was provoked, so it can be redriven: <b>i</b> by an ordinary prompt under the
/// shipping <c>answer</c> schema; <b>ii</b> by adding a <c>nonce</c> whose <c>pattern</c> and
/// <c>minLength</c> contradict each other, which the model noticed and answered around in prose;
/// <b>iii</b> by a <c>nonce</c> pattern of exactly 997 hex characters, which it kept trying and missing
/// until the five attempts ran out; <b>iv</b> by <c>--max-budget-usd 0.0001</c>.
/// </para>
/// <para>
/// Three later captures came from claude <b>2.1.223</b> on 2026-08-07, same container and model:
/// <c>claude-typed-narrated-tool-turn.jsonl</c> (narrate, call a real tool, then answer),
/// <c>claude-typed-retry-with-tool-turn.jsonl</c> (answer, get the document rejected, read a file, retry)
/// and <c>claude-typed-revised-answer-turn.jsonl</c> (the same narrate-read-answer shape driven again to
/// check whether a document that REVISES the prose rather than repeating it is normal; it is).
/// </para>
/// <para>
/// These are a RECORDING like the rest of Sessions/Fixtures. That the pinned CLI still emits these shapes
/// is a separate claim owned by <see cref="ClaudeCliContractTests"/>, whose live tier now drives a real
/// turn under <c>--json-schema</c> and asserts a document comes back. The runner passes the flag when the
/// studio's typed-turn row is on (increment 4); the gate itself is <see cref="TypedTurnGateTests"/>.
/// </para>
/// </summary>
public class ClaudeStreamJsonParserTypedTurnTests
{
    private const string TypedTurn = "claude-typed-turn.jsonl";
    private const string NudgedTurn = "claude-typed-nudged-turn.jsonl";
    private const string NarratedToolTurn = "claude-typed-narrated-tool-turn.jsonl";
    private const string RetryWithToolTurn = "claude-typed-retry-with-tool-turn.jsonl";
    private const string RevisedAnswerTurn = "claude-typed-revised-answer-turn.jsonl";
    private const string UntypedSuccess = "claude-typed-untyped-success.jsonl";
    private const string RetriesExhausted = "claude-typed-retries-exhausted.jsonl";
    private const string BudgetExhausted = "claude-typed-budget-exhausted.jsonl";

    private static readonly DateTimeOffset Now = new(2026, 8, 6, 6, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The parser as the runner builds it. There is only one build of it now: it is not told whether a
    /// schema went out, because the prose path no longer depends on knowing (see the four-property
    /// section below).
    /// </summary>
    private static ClaudeStreamJsonParser Parser() => new(() => Now);

    private static List<AgentEvent> Replay(string fixture)
    {
        var parser = Parser();
        var path = Path.Combine(AppContext.BaseDirectory, "Sessions", "Fixtures", fixture);
        return [.. File.ReadAllLines(path).SelectMany(parser.Parse)];
    }

    private static TurnCompleted Turn(string fixture) => Replay(fixture).OfType<TurnCompleted>().Single();

    // ---- outcome i: the typed turn --------------------------------------------------------

    [Fact]
    public void A_turn_that_ends_in_a_document_carries_it_as_data()
    {
        var turn = Turn(TypedTurn);

        turn.Document.Should().NotBeNull();
        turn.Document!.Version.Should().Be("1");
        turn.Document.Kind.Should().Be("answer");
        turn.Document.KnownKind.Should().Be(AgentTurnDocumentKind.Answer);
        turn.Document.Message.Should().Be("56");
        turn.Document.IsTyped.Should().BeTrue();
        turn.IsError.Should().BeFalse();
        turn.TerminalReason.Should().Be("completed");
        turn.ValidationFailures.Should().Be(0);
    }

    [Fact]
    public void The_raw_result_string_is_not_surfaced_as_text_once_the_document_is_present()
    {
        // The terminating line carries the document TWICE: parsed in structured_output and as the same
        // JSON in the result string. Rendering the string too would put raw JSON in the transcript.
        var turn = Turn(TypedTurn);

        turn.ResultText.Should().BeNull();
    }

    [Fact]
    public void A_turn_answered_straight_through_the_tool_streams_no_text_at_all()
    {
        // This is the silence the working indicator exists for: no assistant text block, so no
        // text_delta, so nothing at all between send and the result line under the old filter. What the
        // user reads arrives at the end, out of the document, and nothing was streamed to preview it.
        var events = Replay(TypedTurn);

        events.OfType<PartialText>().Should().BeEmpty();
        events.OfType<MessageEmitted>().Should().ContainSingle()
            .Which.Text.Should().Be("56", "the document's message is the whole of what the turn said");
    }

    // ---- the four properties of the prose path ---------------------------------------------
    //
    // This section replaced four rounds of per-turn heuristics that each decided which prose was "the
    // answer" and which was "narration", suppressing or relocating accordingly. Each round satisfied
    // its own tests and broke a shape nobody had anticipated; the sequence is written out in
    // docs/plans/typed-agent-turns.md. The parser stops asking the question: every assistant text
    // block is committed as it completes, in the order it arrived, and the document is committed at
    // the result line only if that exact text has not already been committed during this turn.
    //
    // What is asserted below is the four properties that follow from that, not the mechanism:
    //   1. nothing is lost         - everything is committed as it happens, Stop and crash included
    //   2. nothing is re-ordered   - nothing is held, so nothing can arrive out of order
    //   3. nothing renders twice   - one comparison, against what was really committed
    //   4. nothing is concatenated - two messages are two bubbles, always
    //
    // Each is driven through the real parser on a captured fixture where one covers the shape, and on
    // a hand-built interleaving where none does.

    [Fact]
    public void The_document_is_committed_as_the_assistant_message_the_transcript_shows()
    {
        var events = Replay(TypedTurn);

        var message = events.OfType<MessageEmitted>().Single();
        message.Role.Should().Be(TranscriptRole.Assistant);
        message.Text.Should().Be("56");
        events.IndexOf(message).Should().BeLessThan(
            events.FindIndex(e => e is TurnCompleted),
            "the answer belongs in the transcript before the turn that produced it is closed");
    }

    // ---- property 1: nothing is lost --------------------------------------------------------

    [Fact]
    public void Property_1_prose_in_a_message_that_calls_no_tool_of_its_own_is_still_shown()
    {
        // THE defect this rewrite was ordered for, measured by replay at the previous HEAD: an
        // assistant message containing only narration, then a SEPARATE message calling the tool, then
        // an answer, then a document. The narration belonged to no tool call in its own message, so
        // the message-scoped buffer moved it into the held answer attempt, and the document then
        // superseded the lot. The narration was deleted outright.
        //
        // It is a normal shape, not an exotic one: any message ending in text without calling
        // StructuredOutput draws the enforcement nudge, and the reply is a new message.
        var parser = Parser();
        List<AgentEvent> events =
        [
            .. parser.Parse(AssistantBlock("msg_a", """{"type":"text","text":"Let me check the config file."}""")),
            .. parser.Parse(AssistantBlock(
                "msg_b", """{"type":"tool_use","id":"toolu_r","name":"Read","input":{"file_path":"/x"}}""")),
            .. parser.Parse(AssistantBlock("msg_c", """{"type":"text","text":"The timeout is 30 seconds."}""")),
            .. parser.Parse(ResultLine("""{"version":"1","kind":"answer","message":"The timeout is 30 seconds."}""")),
        ];

        events.OfType<MessageEmitted>().Where(m => m.Role == TranscriptRole.Assistant).Select(m => m.Text)
            .Should().Equal("Let me check the config file.", "The timeout is 30 seconds.");
    }

    [Fact]
    public void Property_1_everything_written_before_a_stop_is_already_in_the_transcript()
    {
        // Stop kills the process tree, so no result line is ever parsed; a crash, an OOM kill and a
        // dropped pipe end the stream the same way. Nothing waits for that line any more, so the
        // transcript is complete before Drain is even called.
        var parser = Parser();
        List<AgentEvent> events =
        [
            .. parser.Parse(AssistantBlock("msg_a", """{"type":"text","text":"The answer is 42."}""")),
            .. parser.Parse(AssistantBlock("msg_b", """{"type":"text","text":"Let me double check that."}""")),
            .. parser.Parse(AssistantBlock(
                "msg_b", """{"type":"tool_use","id":"toolu_r","name":"Read","input":{"file_path":"/x"}}""")),
        ];
        var drained = parser.Drain();

        events.OfType<MessageEmitted>().Select(m => m.Text)
            .Should().Equal("The answer is 42.", "Let me double check that.");
        drained.Should().BeEmpty("a completed block was committed when it completed, not held for the end");
    }

    [Fact]
    public void Property_1_a_block_still_being_typed_when_the_stream_dies_is_not_lost_either()
    {
        // The one thing a completed-block commit cannot cover: Stop pressed mid-sentence. The deltas
        // the reader watched have no assistant line to commit them, so Drain is what gets them out.
        var parser = Parser();
        parser.Parse(TextBlockStart(1));
        parser.Parse(TextDelta(1, "half a sentence the user watched"));

        parser.Drain().OfType<MessageEmitted>().Should().ContainSingle()
            .Which.Text.Should().Be("half a sentence the user watched");
    }

    [Fact]
    public void Property_1_a_streamed_tail_the_turn_already_said_once_is_still_drained()
    {
        // The defect this rule exists to prevent, reintroduced through the wrong door. The streamed tail
        // is agent-written prose, exactly like a completed block, so it goes out UNCONDITIONALLY: the
        // de-duplication set is for the DOCUMENT alone, and running the tail through it means a sentence
        // the agent wrote twice is only shown once, or (as here) not at all.
        //
        // Measured end to end by review, parser through LiveSessionRegistry into SQLite: the tail was
        // dropped by Drain, LiveAgentSession then cleared the preview on SessionEnded, and the sentence
        // survived in neither the pane nor the persisted transcript.
        var parser = Parser();
        parser.Parse(AssistantBlock("msg_a", """{"type":"text","text":"Reading the next file."}"""));
        parser.Parse(AssistantBlock(
            "msg_a", """{"type":"tool_use","id":"toolu_1","name":"Read","input":{"file_path":"/a"}}"""));
        parser.Parse("""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"ok"}]}}""");
        parser.Parse(MessageStart);
        parser.Parse(TextBlockStart(0));
        parser.Parse(TextDelta(0, "Reading the next file."));

        // Stop: the process tree dies, so no result line ever arrives.
        parser.Drain().OfType<MessageEmitted>().Should().ContainSingle()
            .Which.Text.Should().Be(
                "Reading the next file.",
                "the tail is prose the agent wrote, not the document, so nothing dedupes it");
    }

    [Fact]
    public void Property_1_a_result_line_arriving_mid_block_still_shows_what_was_typed()
    {
        // The same door, the other side of it: a turn that reaches its terminal result line with a text
        // block still open. The tail has no assistant line to commit it, so the result line commits it,
        // and it must not be swallowed just because the turn said the same sentence earlier.
        var parser = Parser();
        parser.Parse(AssistantBlock("msg_a", """{"type":"text","text":"Reading the next file."}"""));
        parser.Parse(AssistantBlock(
            "msg_a", """{"type":"tool_use","id":"toolu_1","name":"Read","input":{"file_path":"/a"}}"""));
        parser.Parse(MessageStart);
        parser.Parse(TextBlockStart(0));
        parser.Parse(TextDelta(0, "Reading the next file."));

        var ended = parser.Parse(ResultLine("""{"version":"1","kind":"answer","message":"Both files read."}"""));

        ended.OfType<MessageEmitted>().Select(m => m.Text)
            .Should().Equal("Reading the next file.", "Both files read.");
    }

    [Fact]
    public void Property_1_a_turn_that_reached_its_result_line_has_nothing_left_to_drain()
    {
        var parser = Parser();
        parser.Parse(AssistantText("the prose"));
        parser.Parse(ResultLine("""{"version":"1","kind":"answer","message":"the answer"}"""));

        parser.Drain().Should().BeEmpty("that turn already committed everything it had");
    }

    [Fact]
    public void Property_1_prose_survives_a_turn_whose_validation_retries_ran_out()
    {
        // Outcome iii ends the turn AND the process, which is the worst moment to silently drop the
        // only prose the user could have read. Both text blocks the model wrote are in the transcript.
        Replay(RetriesExhausted).OfType<MessageEmitted>().Select(m => m.Text)
            .Should().Equal("2 plus 2 equals 4.", "Let me generate the correct nonce by counting carefully:");
    }

    [Fact]
    public void Property_1_prose_survives_a_turn_that_ends_with_no_document_at_all()
    {
        // Outcome ii, the trap: the model gave up on the tool and answered in prose. Nothing about the
        // prose path depends on a document arriving, so there is nothing to fall back to.
        var said = Replay(UntypedSuccess).OfType<MessageEmitted>()
            .Where(m => m.Role == TranscriptRole.Assistant).Select(m => m.Text).ToList();

        said.Should().HaveCount(2);
        said[0].Should().Be("2 plus 2 is 4.");
        said[1].Should().Contain("contradiction");
    }

    [Fact]
    public void Property_1_an_empty_document_message_takes_nothing_down_with_it()
    {
        // v1 is permissive on purpose (spec decision 2: no minLength), so "" is a document the reader
        // accepts and no user can read. It commits nothing, and it can no longer erase anything either,
        // because the prose was committed when it arrived rather than held for this line to decide on.
        var parser = Parser();
        var written = parser.Parse(AssistantText("what the model actually wrote"));

        var ended = parser.Parse(ResultLine("""{"version":"1","kind":"answer","message":""}"""));

        written.OfType<MessageEmitted>().Should().ContainSingle()
            .Which.Text.Should().Be("what the model actually wrote");
        ended.OfType<MessageEmitted>().Should().BeEmpty("an empty message is nothing to say");
        ended.OfType<TurnCompleted>().Single().Document
            .Should().NotBeNull("an empty message is still the document the turn returned");
    }

    // ---- property 2: nothing is re-ordered ---------------------------------------------------

    [Fact]
    public void Property_2_narration_sits_above_the_card_it_preceded()
    {
        // Captured in the container on 2026-08-07 (claude 2.1.223): asked to say what it was about to
        // do, the model wrote that as its OWN text block and THEN opened a Read.
        var events = Replay(NarratedToolTurn);

        var narration = events.OfType<MessageEmitted>().First(m => m.Role == TranscriptRole.Assistant);
        narration.Text.Should().StartWith("I'm going to read the note.txt file");
        events.IndexOf(narration).Should().BeLessThan(
            events.FindIndex(e => e is ToolCallStarted { ToolName: "Read" }),
            "the agent said it before it read, and a transcript is what happened");
    }

    [Fact]
    public void Property_2_the_retry_capture_renders_in_the_order_the_model_wrote_it()
    {
        // Captured live on 2026-08-07: the model answered in text, its FIRST StructuredOutput was
        // rejected by the validator, it read a file, and the accepted document repeated the answer word
        // for word. The answer was written BEFORE the Read, so that is where it renders, and the
        // document adds nothing because the user has already read exactly it.
        var events = Replay(RetryWithToolTurn);
        var said = events.OfType<MessageEmitted>().Where(m => m.Role == TranscriptRole.Assistant)
            .Select(m => m.Text).ToList();

        said.Should().Equal("The launch code is 4471.", "Let me read the note.txt file as instructed:");
        events.FindIndex(e => e is MessageEmitted { Text: "The launch code is 4471." })
            .Should().BeLessThan(
                events.FindIndex(e => e is ToolCallStarted { ToolName: "Read" }),
                "the model said it before it read, whatever the document did afterwards");
    }

    [Fact]
    public void Property_2_an_answer_the_agent_retracted_keeps_its_place()
    {
        // The second live defect of the previous build, and it needed no document to appear: the held
        // attempt was re-promoted to the BOTTOM of the transcript on Stop and glued to its own
        // correction, so the last bubble read "The answer is 42.\n\nCorrection: the answer is 43." with
        // the chronology gone. Nothing is held now, so the retraction can only follow what it retracts.
        var parser = Parser();
        List<AgentEvent> events =
        [
            .. parser.Parse(AssistantBlock("msg_a", """{"type":"text","text":"The answer is 42."}""")),
            .. parser.Parse(AssistantBlock(
                "msg_b", """{"type":"tool_use","id":"toolu_r","name":"Read","input":{"file_path":"/x"}}""")),
            .. parser.Parse(AssistantBlock("msg_c", """{"type":"text","text":"Correction: the answer is 43."}""")),
            .. parser.Drain(),
        ];

        var said = events.OfType<MessageEmitted>().Select(m => m.Text).ToList();
        said.Should().Equal("The answer is 42.", "Correction: the answer is 43.");
        events.FindIndex(e => e is MessageEmitted { Text: "The answer is 42." })
            .Should().BeLessThan(events.FindIndex(e => e is ToolCallStarted));
    }

    // ---- property 3: nothing renders twice ---------------------------------------------------

    [Fact]
    public void Property_3_a_document_repeating_the_prose_it_matches_is_not_shown_again()
    {
        // Measured 6 of 6 on 2026-08-06 and in both 2026-08-07 captures: the pre-nudge prose was byte
        // identical to the document's message. This fixture is one of those turns, and it is the whole
        // reason the comparison exists.
        var events = Replay(NudgedTurn);

        events.OfType<MessageEmitted>().Where(m => m.Role == TranscriptRole.Assistant)
            .Should().ContainSingle().Which.Text.Should().Be("17 times 3 is **51**.");
        events.OfType<TurnCompleted>().Single().Document!.Message.Should().Be("17 times 3 is **51**.");
    }

    [Fact]
    public void Property_3_the_comparison_is_against_what_was_committed_not_against_a_buffer()
    {
        // Hand-written, and it has to be: the model would not naturally write its whole answer and then
        // open a tool in the same message with nothing else to say. This is the reviewer's case, and
        // the one every buffer-scoping rule got wrong, because here the identical text was committed
        // as ordinary prose long before the document arrived.
        var parser = Parser();
        List<AgentEvent> events =
        [
            .. parser.Parse(AssistantBlock("msg_a", """{"type":"text","text":"The answer is 42."}""")),
            .. parser.Parse(AssistantBlock(
                "msg_a", """{"type":"tool_use","id":"toolu_r","name":"Read","input":{"file_path":"/x"}}""")),
            .. parser.Parse(ResultLine("""{"version":"1","kind":"answer","message":"The answer is 42."}""")),
        ];

        events.OfType<MessageEmitted>().Where(m => m.Role == TranscriptRole.Assistant)
            .Should().ContainSingle().Which.Text.Should().Be("The answer is 42.");
        events.OfType<ToolCallStarted>().Should().ContainSingle();
    }

    [Fact]
    public void Property_3_only_whitespace_separates_a_match_from_a_repeat()
    {
        // The comparison is ordinal on the trimmed text: the CLI's document message and the prose block
        // differ by a trailing newline often enough that a raw comparison would print both.
        var parser = Parser();
        parser.Parse(AssistantText("The answer is 42."));

        var ended = parser.Parse(ResultLine("""{"version":"1","kind":"answer","message":"The answer is 42.\n"}"""));

        ended.OfType<MessageEmitted>().Should().BeEmpty();
    }

    [Fact]
    public void Property_3_a_document_that_says_something_new_is_shown_as_well_as_the_prose()
    {
        // The stated cost of not guessing, and it is deliberate: a reader sees an answer the model
        // later revised, because the agent did write it. Suppressing the earlier one needs a judgement
        // about which prose "counts", which is the judgement that failed four times.
        var parser = Parser();
        var attempt = parser.Parse(AssistantText("Roughly fifty."));

        var ended = parser.Parse(ResultLine("""{"version":"1","kind":"answer","message":"17 times 3 is 51."}"""));

        attempt.OfType<MessageEmitted>().Should().ContainSingle().Which.Text.Should().Be("Roughly fifty.");
        ended.OfType<MessageEmitted>().Should().ContainSingle().Which.Text.Should().Be("17 times 3 is 51.");
    }

    [Fact]
    public void Property_3_the_narrated_capture_shows_the_revision_the_model_really_made()
    {
        // The same cost on a real capture rather than a construction. The model wrote "The file
        // contains: **the launch code is 4471**" and its document said "The file
        // `/tmp/probe-scratch/note.txt` contains the text: ...", which is a revision, not a repeat.
        // The previous build hid it by discarding the prose; three bubbles is what happened.
        var events = Replay(NarratedToolTurn);
        var said = events.OfType<MessageEmitted>().Where(m => m.Role == TranscriptRole.Assistant)
            .Select(m => m.Text).ToList();

        said.Should().HaveCount(3);
        said[0].Should().StartWith("I'm going to read the note.txt file");
        said[1].Should().Be("The file contains: **the launch code is 4471**");
        said[2].Should().Be(events.OfType<TurnCompleted>().Single().Document!.Message);
    }

    [Fact]
    public void Property_3_a_revising_document_is_the_measured_shape_and_not_a_one_off()
    {
        // Driven live in the container on 2026-08-07 (claude 2.1.223, claude-haiku-4-5-20251001, the
        // shipping answer schema) to check that the cost above is a real shape rather than one unlucky
        // capture. It is: told to say what it was about to do, read a file and report, the model wrote
        // "The file says: ..." in prose and its document said "The note.txt file contains a single line
        // stating: ...". Two of two tool-using typed captures revise rather than repeat, and the reader
        // sees both, in the order the agent wrote them.
        var events = Replay(RevisedAnswerTurn);
        var said = events.OfType<MessageEmitted>().Where(m => m.Role == TranscriptRole.Assistant)
            .Select(m => m.Text).ToList();

        said.Should().HaveCount(3);
        said[0].Should().Be("I'll read the file and tell you what it says.");
        said[1].Should().Be("The file says: \"the badge number is 7781\"");
        said[2].Should().Be(events.OfType<TurnCompleted>().Single().Document!.Message);
        events.IndexOf(events.OfType<MessageEmitted>().First())
            .Should().BeLessThan(events.FindIndex(e => e is ToolCallStarted { ToolName: "Read" }));
    }

    [Fact]
    public void Property_3_what_a_turn_has_said_belongs_to_that_turn_alone()
    {
        // The same sentence in the NEXT turn is the agent saying something, so the comparison is reset.
        var parser = Parser();
        parser.Parse(AssistantText("the same answer"));
        var first = parser.Parse(ResultLine("""{"version":"1","kind":"answer","message":"the same answer"}"""));

        var second = parser.Parse(ResultLine("""{"version":"1","kind":"answer","message":"the same answer"}"""));

        first.OfType<MessageEmitted>().Should().BeEmpty();
        second.OfType<MessageEmitted>().Should().ContainSingle().Which.Text.Should().Be("the same answer");
    }

    [Fact]
    public void Property_3_the_agent_saying_the_same_thing_twice_is_not_deduplicated()
    {
        // Only the document is compared. The same sentence before two different tool calls is the agent
        // saying it twice, and dropping the second would leave a card with nothing said about it.
        var parser = Parser();
        List<AgentEvent> events =
        [
            .. parser.Parse(AssistantBlock("msg_a", """{"type":"text","text":"Reading the next file."}""")),
            .. parser.Parse(AssistantBlock(
                "msg_a", """{"type":"tool_use","id":"toolu_1","name":"Read","input":{"file_path":"/a"}}""")),
            .. parser.Parse(AssistantBlock("msg_b", """{"type":"text","text":"Reading the next file."}""")),
            .. parser.Parse(AssistantBlock(
                "msg_b", """{"type":"tool_use","id":"toolu_2","name":"Read","input":{"file_path":"/b"}}""")),
        ];

        events.OfType<MessageEmitted>().Should().HaveCount(2);
    }

    // ---- property 4: nothing is concatenated -------------------------------------------------

    [Fact]
    public void Property_4_two_messages_are_two_bubbles_and_never_one_utterance()
    {
        // The glued-messages half of the retraction defect: two things the model wrote in two messages
        // came out as one bubble with a blank line between them, so the transcript read as a single
        // utterance the agent never made. Nothing joins text any more, so there is nothing to join with.
        var parser = Parser();
        List<AgentEvent> events =
        [
            .. parser.Parse(AssistantBlock("msg_a", """{"type":"text","text":"The answer is 42."}""")),
            .. parser.Parse(AssistantBlock("msg_b", """{"type":"text","text":"Correction: the answer is 43."}""")),
            .. parser.Parse("""{"type":"result","is_error":false,"terminal_reason":"completed","result":"ignored"}"""),
        ];

        var said = events.OfType<MessageEmitted>().Select(m => m.Text).ToList();
        said.Should().Equal("The answer is 42.", "Correction: the answer is 43.");
        said.Should().AllSatisfy(t => t.Should().NotContain("\n\n"));
    }

    [Fact]
    public void Property_4_two_text_blocks_of_one_message_are_two_bubbles_too()
    {
        // Joining them was a smaller version of the same guess: two blocks are two things the model
        // chose to write separately, and a renderer that wants them tighter can decide that from the
        // events. The parser inventing a paragraph break is the parser writing prose.
        var parser = Parser();
        const string line =
            """
            {"type":"assistant","message":{"id":"msg_a","content":[
              {"type":"text","text":"First."},
              {"type":"text","text":"Second."}]}}
            """;

        var events = parser.Parse(line.ReplaceLineEndings(string.Empty));

        events.OfType<MessageEmitted>().Select(m => m.Text).Should().Equal("First.", "Second.");
    }

    [Fact]
    public void Property_4_a_new_text_block_starting_replaces_the_streamed_tail_it_follows()
    {
        // The streamed tail is ONE block, and the block that opens is a different one. Carrying the
        // previous block's deltas into it makes Drain emit "First message.Second message." as a single
        // bubble: two things the model wrote separately, rendered as one utterance, which is exactly the
        // property this section exists for.
        //
        // Latent only because the CLI emits a per-block assistant line before the next block starts, on
        // all 11 captures. The parser is written to survive lines it does not understand and the runner
        // explicitly handles a stream that ends abruptly, so it cannot rest on that.
        var parser = Parser();
        parser.Parse(MessageStart);
        parser.Parse(TextBlockStart(0));
        parser.Parse(TextDelta(0, "First message."));
        parser.Parse(TextBlockStop(0));
        parser.Parse(TextBlockStart(1));
        parser.Parse(TextDelta(1, "Second message."));

        parser.Drain().OfType<MessageEmitted>().Should().ContainSingle()
            .Which.Text.Should().Be("Second message.", "the tail is the block that is still open, only");
    }

    [Fact]
    public void Property_4_a_new_message_starting_replaces_the_streamed_tail_too()
    {
        // Same shape with a message boundary between the blocks. message_start already retires the open
        // tool blocks, because indexes restart per message; the streamed tail is per block, so it is
        // stale for the same reason and by the same line.
        var parser = Parser();
        parser.Parse(MessageStart);
        parser.Parse(TextBlockStart(0));
        parser.Parse(TextDelta(0, "First message."));
        parser.Parse(MessageStart);
        parser.Parse(TextBlockStart(0));
        parser.Parse(TextDelta(0, "Second message."));

        parser.Drain().OfType<MessageEmitted>().Should().ContainSingle()
            .Which.Text.Should().Be("Second message.");
    }

    [Fact]
    public void Without_a_schema_prose_is_still_the_answer_and_is_committed_as_it_arrives()
    {
        // The gate is off by default, so this is the path the product ships today. It is now the SAME
        // path: the parser is no longer told whether a schema went out, because it no longer behaves
        // differently, and a whole class of "the parser believed the wrong thing" cannot arise.
        var parser = new ClaudeStreamJsonParser(() => Now);

        var events = parser.Parse("""{"type":"assistant","message":{"content":[{"type":"text","text":"here you go"}]}}""");

        events.OfType<MessageEmitted>().Should().ContainSingle().Which.Text.Should().Be("here you go");
    }

    // ---- the live preview matches what commits -----------------------------------------------

    [Fact]
    public void What_streams_as_a_preview_is_the_same_text_that_is_committed()
    {
        // One block streams, that same block commits, so the preview the reader is watching is replaced
        // by a bubble saying exactly it. No separator is invented and none is needed, which is what
        // makes LiveAgentSession clearing the preview on MessageEmitted correct rather than lossy.
        var parser = Parser();
        List<AgentEvent> events =
        [
            .. parser.Parse(TextBlockStart(1)),
            .. parser.Parse(TextDelta(1, "first ")),
            .. parser.Parse(TextDelta(1, "paragraph")),
            .. parser.Parse(AssistantText("first paragraph")),
            .. parser.Parse("""{"type":"stream_event","event":{"type":"message_start","message":{"role":"assistant"}}}"""),
            .. parser.Parse(TextBlockStart(1)),
            .. parser.Parse(TextDelta(1, "second paragraph")),
            .. parser.Parse(AssistantText("second paragraph")),
            .. parser.Parse("""{"type":"result","is_error":false,"subtype":"success","terminal_reason":"completed","result":"ignored"}"""),
        ];

        var streamed = string.Concat(events.OfType<PartialText>().Select(p => p.Delta));
        streamed.Should().Be(
            string.Concat(events.OfType<MessageEmitted>().Select(m => m.Text)),
            "each block's deltas are exactly the bubble that block commits");
        events.OfType<MessageEmitted>().Should().HaveCount(2);
    }

    [Fact]
    public void The_indicator_is_fed_by_thinking_and_by_the_document_being_written()
    {
        var phases = Replay(TypedTurn).OfType<TurnActivity>().Select(a => a.Phase).ToList();

        phases.Should().Contain(AgentTurnPhase.Thinking);
        phases.Should().Contain(AgentTurnPhase.Composing);
        phases.Should().NotContain(AgentTurnPhase.ToolInput);
    }

    // ---- the outcome tool is not a tool card ----------------------------------------------

    [Fact]
    public void The_structured_output_call_renders_as_the_outcome_and_never_as_a_tool_card()
    {
        var events = Replay(TypedTurn);

        events.OfType<ToolCallStarted>().Should().BeEmpty();
        events.OfType<ToolCallCompleted>().Should().BeEmpty();
        events.OfType<StatsUpdated>().Last().Stats.ToolCalls
            .Should().Be(0, "counting the outcome tool over-reports every typed turn by exactly one");
    }

    [Fact]
    public void An_ordinary_tool_in_the_same_turn_still_gets_its_card_and_its_count()
    {
        var parser = Parser();
        const string assistant =
            """
            {"type":"assistant","message":{"content":[
              {"type":"tool_use","id":"toolu_read","name":"Read","input":{"file_path":"/workspaces/w1/README.md"}},
              {"type":"tool_use","id":"toolu_out","name":"StructuredOutput","input":{"kind":"answer"}}]}}
            """;

        var events = parser.Parse(assistant.ReplaceLineEndings(string.Empty)).ToList();
        events.AddRange(parser.Parse(ResultLine("""{"version":"1","kind":"answer","message":"done"}""")));

        events.OfType<ToolCallStarted>().Should().ContainSingle().Which.ToolName.Should().Be("Read");
        events.OfType<StatsUpdated>().Last().Stats.ToolCalls.Should().Be(1);
    }

    [Fact]
    public void The_outcome_tools_own_result_is_not_a_tool_card_either()
    {
        // "Structured output provided successfully" is plumbing, and so is a validator complaint.
        var events = Replay(NudgedTurn);

        events.OfType<ToolCallCompleted>().Should().BeEmpty();
    }

    // ---- the synthetic nudge --------------------------------------------------------------

    [Fact]
    public void The_synthetic_nudge_never_appears_as_something_the_user_said()
    {
        var events = Replay(NudgedTurn);

        events.OfType<MessageEmitted>().Where(m => m.Role == TranscriptRole.User).Should().BeEmpty();
        events.OfType<MessageEmitted>().Should().AllSatisfy(
            m => m.Text.Should().NotContain("structured-output-enforce"));
    }

    [Fact]
    public void A_synthetic_line_is_suppressed_by_its_flag()
    {
        // The primary check: isSynthetic at the line root, which is where the CLI puts it. The text is
        // deliberately ordinary here, so only the flag can be what suppressed the line.
        const string line =
            """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"please continue"}]},"isSynthetic":true}""";

        Parser().Parse(line).Should().BeEmpty();
    }

    [Fact]
    public void A_nudge_is_suppressed_by_its_text_even_without_the_flag()
    {
        // The backstop: isSynthetic is undocumented and could be renamed in a CLI release. If it is,
        // this test still keeps the nudge out of the transcript and the other one says what moved.
        const string line =
            """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"[structured-output-enforce] You MUST call the StructuredOutput tool to complete this request. Call this tool now."}]}}""";

        Parser().Parse(line).Should().BeEmpty();
    }

    [Fact]
    public void A_real_user_line_still_becomes_a_message()
    {
        const string line =
            """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"write the readme"}]}}""";

        var message = Parser().Parse(line).OfType<MessageEmitted>().Single();
        message.Role.Should().Be(TranscriptRole.User);
        message.Text.Should().Be("write the readme");
    }

    [Fact]
    public void A_real_tool_result_on_a_user_line_still_completes_its_call()
    {
        // Suppression must not extend to tool_result blocks: the same line kind carries real results.
        var parser = Parser();
        parser.Parse("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Write","input":{"file_path":"/w/note.txt"}}]}}""");

        const string result =
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"File created successfully"}]}}""";

        parser.Parse(result).OfType<ToolCallCompleted>().Single().ToolName.Should().Be("Write");
    }

    // ---- outcome ii: the trap -------------------------------------------------------------

    [Fact]
    public void A_turn_that_reports_success_but_carries_no_document_is_read_as_untyped()
    {
        // Every field a reader would normally check says this turn went fine. It did not: the model gave
        // up on the tool and answered in prose. Only the absence of structured_output says so.
        var events = Replay(UntypedSuccess);
        var turn = events.OfType<TurnCompleted>().Single();

        turn.IsError.Should().BeFalse();
        turn.TerminalReason.Should().Be("completed");
        turn.Document.Should().BeNull();
        // Backlog 104 D2: the give-up prose is an assistant bubble already (Property_1 above asserts it),
        // and the result string repeats it byte for byte, so the turn row no longer duplicates it.
        turn.ResultText.Should().BeNull();
        events.OfType<MessageEmitted>().Select(m => m.Text)
            .Should().Contain(t => t.StartsWith("I'm encountering a contradiction", StringComparison.Ordinal));
    }

    [Fact]
    public void The_attempts_a_turn_burned_before_giving_up_are_not_lost_with_the_error_cards()
    {
        var turn = Turn(UntypedSuccess);

        turn.ValidationFailures.Should().Be(2);
        turn.LastValidationError.Should().Contain("/nonce").And.Contain("must match pattern");
    }

    // ---- outcome iii: the retries ran out --------------------------------------------------

    [Fact]
    public void A_turn_whose_validation_retries_ran_out_is_a_failed_turn_with_no_document()
    {
        var turn = Turn(RetriesExhausted);

        turn.IsError.Should().BeTrue();
        turn.TerminalReason.Should().Be("structured_output_retry_exhausted");
        turn.Document.Should().BeNull();
        // The line has no result field at all on this outcome.
        turn.ResultText.Should().BeNull();
        AgentTerminalReason.ToSessionState(turn.TerminalReason, turn.IsError)
            .Should().Be(AgentSessionState.Failed);
    }

    [Fact]
    public void An_exhausted_turn_reports_the_five_attempts_and_the_last_pointer()
    {
        // The whole budget, and the message that names which part of the schema was never satisfied.
        var turn = Turn(RetriesExhausted);

        turn.ValidationFailures.Should().Be(5);
        turn.LastValidationError.Should().Contain("/nonce").And.Contain("^[a-f0-9]{997}$");
    }

    [Fact]
    public void An_exhausted_turn_keeps_the_clis_own_words_for_it()
    {
        Turn(RetriesExhausted).ErrorText.Should().Be("Failed to provide valid structured output after 5 attempts");
    }

    // ---- outcome iv: the budget ran out ----------------------------------------------------

    [Fact]
    public void A_turn_that_ran_out_of_budget_is_a_failed_turn_naming_the_budget()
    {
        var turn = Turn(BudgetExhausted);

        turn.IsError.Should().BeTrue();
        turn.TerminalReason.Should().Be("budget_exhausted");
        turn.Document.Should().BeNull();
        turn.ResultText.Should().BeNull();
        turn.ValidationFailures.Should().Be(0);

        // The budget is named in the CLI's own words, so the transcript quotes it rather than guessing.
        turn.ErrorText.Should().Be("Reached maximum budget ($0.0001)");
    }

    [Fact]
    public void A_budget_exhausted_turn_is_accounted_from_model_usage_not_from_its_zeroed_usage_block()
    {
        // Decided 2026-09-14 (backlog 25 increment 7). On this outcome the result line's usage block
        // is all zeroes while modelUsage carries what the turn really consumed (544 in, 16 out, $0.000624),
        // and the ceiling was measured being overshot about 10x, so the zeroed block under-reports exactly
        // the turn that cost the most. The tokens prove which of the two was read: the usage block says 0.
        var stats = Replay(BudgetExhausted).OfType<StatsUpdated>().Last().Stats;

        stats.InputTokens.Should().Be(544);
        stats.OutputTokens.Should().Be(16);
        stats.CostUsd.Should().Be(0.000624m);
        stats.Turns.Should().Be(1);
    }

    [Fact]
    public void The_recorded_cost_of_a_budget_exhausted_turn_is_the_model_usage_figure()
    {
        // The same captured line with total_cost_usd taken away (the one edit, hand-made): the cost that is
        // still recorded can only have come from modelUsage's costUSD, which is the figure the account uses.
        var path = Path.Combine(AppContext.BaseDirectory, "Sessions", "Fixtures", BudgetExhausted);
        var parser = Parser();
        List<AgentEvent> events = [];
        foreach (var line in File.ReadAllLines(path))
        {
            events.AddRange(parser.Parse(line.Contains("\"type\":\"result\"", StringComparison.Ordinal)
                ? line.Replace("\"total_cost_usd\":0.000624,", string.Empty, StringComparison.Ordinal)
                : line));
        }

        File.ReadAllText(path).Should().Contain("\"total_cost_usd\":0.000624,", "the edit above must really remove something");
        events.OfType<StatsUpdated>().Last().Stats.CostUsd.Should().Be(0.000624m);
    }

    [Fact]
    public void An_ordinary_turn_is_still_accounted_from_its_usage_block()
    {
        // The modelUsage reading is for a zeroed block only: a real usage block is the per-turn sum the rest
        // of the studio was built on, and this turn's modelUsage input (554) differs from it (10).
        var stats = Replay(TypedTurn).OfType<StatsUpdated>().Last().Stats;

        stats.InputTokens.Should().Be(10);
        stats.OutputTokens.Should().Be(156);
    }

    /// <summary>
    /// The multi-turn capture with the SECOND turn's usage block zeroed (the one edit, hand-made), which is the
    /// shape a budget that runs out on a later turn of a conversation takes: --max-budget-usd is a
    /// whole-invocation ceiling, so that is where it usually lands.
    /// </summary>
    private static List<AgentEvent> MultiTurnWithTheSecondUsageBlockZeroed(ClaudeStreamJsonParser parser)
    {
        const string SecondUsage =
            "\"usage\":{\"input_tokens\":10,\"cache_creation_input_tokens\":214,\"cache_read_input_tokens\":24066,\"output_tokens\":55,";
        const string Zeroed =
            "\"usage\":{\"input_tokens\":0,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":0,";

        var path = Path.Combine(AppContext.BaseDirectory, "Sessions", "Fixtures", "claude-multi-turn.jsonl");
        File.ReadAllText(path).Should().Contain(SecondUsage, "the edit below must really zero something");
        return [.. File.ReadAllLines(path)
            .Select(line => line.Replace(SecondUsage, Zeroed, StringComparison.Ordinal))
            .SelectMany(parser.Parse)];
    }

    [Fact]
    public void A_later_turn_with_a_zeroed_usage_block_is_accounted_from_its_share_of_the_process_wide_model_usage()
    {
        // modelUsage is cumulative for the CLI process: the capture's second result line says 546 in, 121 out,
        // 41602 cache-read, which is both turns together (41602 = 17536 + 24066). Taking it as the second turn's
        // own figure added the first turn a second time. The turn's share is what moved since the first result
        // line, so the session's totals come out equal to the process's final cumulative figure, and no higher.
        var perTurn = MultiTurnWithTheSecondUsageBlockZeroed(Parser())
            .OfType<StatsUpdated>().Select(s => s.Stats)
            .Where(s => s.Turns > 0)
            .GroupBy(s => s.Turns)
            .Select(g => g.Last())
            .ToList();

        perTurn.Select(s => s.Turns).Should().Equal(1, 2);
        perTurn[0].InputTokens.Should().Be(10, "the first turn's usage block is real and is read as before");
        perTurn[1].InputTokens.Should().Be(546);
        perTurn[1].OutputTokens.Should().Be(121);
        perTurn[1].CacheReadTokens.Should().Be(41602);
        perTurn[1].CostUsd.Should().Be(0.0187992m);
    }

    [Fact]
    public void A_restarted_process_counts_its_model_usage_from_zero_again()
    {
        // A model change restarts the child, and the new process's modelUsage starts over, so the previous
        // process's total must not be subtracted from it.
        var parser = Parser();
        var multi = Path.Combine(AppContext.BaseDirectory, "Sessions", "Fixtures", "claude-multi-turn.jsonl");
        var firstResult = File.ReadAllLines(multi).First(l => l.Contains("\"type\":\"result\"", StringComparison.Ordinal));
        parser.Parse(firstResult);

        parser.ExpectRestart();
        var budget = Path.Combine(AppContext.BaseDirectory, "Sessions", "Fixtures", BudgetExhausted);
        var stats = File.ReadAllLines(budget).SelectMany(parser.Parse).OfType<StatsUpdated>().Last().Stats;

        stats.InputTokens.Should().Be(10 + 544);
        stats.OutputTokens.Should().Be(56 + 16);
    }

    // ---- reading structured_output ---------------------------------------------------------

    [Theory]
    // Hand-written: the CLI omits the field entirely rather than nulling it, but a reader that trusts
    // TryGetProperty alone would take either of these for a document.
    [InlineData("""{"type":"result","is_error":false,"terminal_reason":"completed","result":"plain prose","structured_output":null}""")]
    [InlineData("""{"type":"result","is_error":false,"terminal_reason":"completed","result":"plain prose","structured_output":"not an object"}""")]
    [InlineData("""{"type":"result","is_error":false,"terminal_reason":"completed","result":"plain prose","structured_output":[]}""")]
    public void A_structured_output_that_is_not_an_object_counts_as_absent(string line)
    {
        var turn = Parser().Parse(line).OfType<TurnCompleted>().Single();

        turn.Document.Should().BeNull();
        turn.ResultText.Should().Be("plain prose");
    }

    [Fact]
    public void An_envelope_this_build_cannot_read_still_renders_its_message()
    {
        var line = ResultLine("""{"version":"2","kind":"answer","message":"written by a later studio"}""");

        var document = Parser().Parse(line).OfType<TurnCompleted>().Single().Document;

        document.Should().NotBeNull();
        document!.Message.Should().Be("written by a later studio");
        document.Degradation.Should().Be(AgentTurnDocumentDegradation.UnknownVersion);
    }

    [Fact]
    public void The_failure_count_belongs_to_its_own_turn_and_does_not_leak_into_the_next()
    {
        var parser = Parser();
        var path = Path.Combine(AppContext.BaseDirectory, "Sessions", "Fixtures", RetriesExhausted);
        var first = File.ReadAllLines(path).SelectMany(parser.Parse).OfType<TurnCompleted>().Single();

        var second = parser
            .Parse(ResultLine("""{"version":"1","kind":"answer","message":"the next turn was fine"}"""))
            .OfType<TurnCompleted>().Single();

        first.ValidationFailures.Should().Be(5);
        second.ValidationFailures.Should().Be(0);
        second.LastValidationError.Should().BeNull();
    }

    // ---- attributing the json deltas -------------------------------------------------------

    [Fact]
    public void A_json_delta_on_the_outcome_tools_block_is_the_answer_being_composed()
    {
        var parser = Parser();
        parser.Parse(BlockStart(1, "StructuredOutput"));

        parser.Parse(JsonDelta(1)).OfType<TurnActivity>().Single().Phase
            .Should().Be(AgentTurnPhase.Composing);
    }

    [Fact]
    public void A_json_delta_on_another_tools_block_is_ordinary_tool_input()
    {
        // That call already has a card of its own, so calling it "composing the answer" would be wrong.
        var parser = Parser();
        parser.Parse(BlockStart(1, "Read"));

        parser.Parse(JsonDelta(1)).OfType<TurnActivity>().Single().Phase
            .Should().Be(AgentTurnPhase.ToolInput);
    }

    [Fact]
    public void A_json_delta_whose_block_was_never_opened_is_not_claimed_as_the_answer()
    {
        // Hand-written: the CLI always opens the block first. If a future release stops, the honest
        // reading is "some tool is being written", not "the turn's answer is being written".
        Parser().Parse(JsonDelta(7)).OfType<TurnActivity>().Single().Phase
            .Should().Be(AgentTurnPhase.ToolInput);
    }

    [Fact]
    public void A_new_message_retires_the_block_indexes_of_the_last_one()
    {
        // Indexes restart per message, so index 1 of the next message is a different block.
        var parser = Parser();
        parser.Parse(BlockStart(1, "StructuredOutput"));
        parser.Parse("""{"type":"stream_event","event":{"type":"message_start","message":{"role":"assistant"}}}""");

        parser.Parse(JsonDelta(1)).OfType<TurnActivity>().Single().Phase
            .Should().Be(AgentTurnPhase.ToolInput);
    }

    [Fact]
    public void A_text_block_taking_over_an_index_retires_the_tool_that_held_it()
    {
        var parser = Parser();
        parser.Parse(BlockStart(1, "StructuredOutput"));
        parser.Parse("""{"type":"stream_event","event":{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}}""");

        parser.Parse(JsonDelta(1)).OfType<TurnActivity>().Single().Phase
            .Should().Be(AgentTurnPhase.ToolInput);
    }

    [Fact]
    public void A_thinking_delta_moves_the_indicator_without_leaking_the_thinking()
    {
        const string line =
            """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"The user wants me"}}}""";

        var events = Parser().Parse(line);

        events.OfType<TurnActivity>().Single().Phase.Should().Be(AgentTurnPhase.Thinking);
        events.OfType<MessageEmitted>().Should().BeEmpty();
        events.OfType<PartialText>().Should().BeEmpty();
    }

    [Fact]
    public void The_working_indicator_is_never_persisted()
    {
        // Transient exactly like PartialText: a phase is only true for the instant it was emitted.
        AgentEventJson.KindOf(new TurnActivity(Now, AgentTurnPhase.Composing)).Should().BeNull();
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static string ResultLine(string document) =>
        """{"type":"result","is_error":false,"subtype":"success","terminal_reason":"completed","result":"ignored","structured_output":"""
        + document + "}";

    private static string BlockStart(int index, string tool) =>
        "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_start\",\"index\":"
        + index.ToString(CultureInfo.InvariantCulture)
        + ",\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_x\",\"name\":\""
        + tool + "\",\"input\":{}}}}";

    /// <summary>
    /// One content block on an assistant line that names the message it belongs to, which is how the
    /// parser tells prose the model wrote on its way to a tool call from prose it ended a message with.
    /// Every real capture carries this id on every assistant line.
    /// </summary>
    private static string AssistantBlock(string messageId, string block) =>
        "{\"type\":\"assistant\",\"message\":{\"id\":\"" + messageId + "\",\"content\":[" + block + "]}}";

    private static string AssistantText(string text) =>
        "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\""
        + text + "\"}]}}";

    private const string MessageStart =
        """{"type":"stream_event","event":{"type":"message_start","message":{"role":"assistant"}}}""";

    private static string TextBlockStart(int index) =>
        "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_start\",\"index\":"
        + index.ToString(CultureInfo.InvariantCulture)
        + ",\"content_block\":{\"type\":\"text\",\"text\":\"\"}}}";

    private static string TextBlockStop(int index) =>
        "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_stop\",\"index\":"
        + index.ToString(CultureInfo.InvariantCulture) + "}}";

    private static string TextDelta(int index, string text) =>
        "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"index\":"
        + index.ToString(CultureInfo.InvariantCulture)
        + ",\"delta\":{\"type\":\"text_delta\",\"text\":\"" + text + "\"}}}";

    private static string JsonDelta(int index) =>
        "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"index\":"
        + index.ToString(CultureInfo.InvariantCulture)
        + ",\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"kind\\\":\"}}}";
}
