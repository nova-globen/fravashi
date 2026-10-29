using AwesomeAssertions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Sessions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Plan 2e Task 2. Every assertion here runs against stream-json REALLY emitted by claude 2.1.221
/// inside the container on 2026-08-04 (see Sessions/Fixtures), not against hand-written JSON:
/// an allow-hook run that wrote a file, a deny-hook run that was refused, and a two-turn stdin
/// session. Synthetic lines appear only where a shape has to be provoked (a throttled rate limit,
/// a hook that crashed, garbage).
/// <para>
/// These fixtures are a RECORDING, so green here only proves the parser still parses 2.1.221. That the
/// pinned CLI still emits these shapes is a separate claim and a separate test:
/// <see cref="ClaudeCliContractTests"/> drives the real binary (ADR 0023).
/// </para>
/// </summary>
public class ClaudeStreamJsonParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 18, 0, 0, TimeSpan.Zero);

    private static ClaudeStreamJsonParser Parser() => new(() => Now);

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Sessions", "Fixtures", name);

    private static string[] Fixture(string name) => File.ReadAllLines(FixturePath(name));

    private static List<AgentEvent> Replay(string fixture)
    {
        var parser = Parser();
        return [.. Fixture(fixture).SelectMany(parser.Parse)];
    }

    // ---- the CLI's own session identity -------------------------------------------------

    [Fact]
    public void The_init_line_registers_the_cli_session_id_and_the_serving_model()
    {
        var events = Replay("claude-allow-run.jsonl");

        var registered = events.OfType<SessionRegistered>().First();
        registered.ProviderSessionId.Should().Be("ea12436c-b6eb-455e-b670-687beea9c6d2");
        registered.Model.Should().Be("claude-haiku-4-5-20251001");
    }

    // ---- assistant text and tool calls --------------------------------------------------

    [Fact]
    public void Assistant_text_becomes_a_message_and_thinking_is_not_shown()
    {
        var events = Replay("claude-allow-run.jsonl");

        var messages = events.OfType<MessageEmitted>().Where(m => m.Role == TranscriptRole.Assistant).ToList();
        messages.Should().NotBeEmpty();
        messages.Should().AllSatisfy(m => m.Text.Should().NotBeNullOrWhiteSpace());
        // The fixture's thinking blocks carry a long base64 signature; none of it may reach the transcript.
        messages.Should().AllSatisfy(m => m.Text.Should().NotContain("signature"));
        messages.Last().Text.Should().Contain("approved by hook");
    }

    [Fact]
    public void A_tool_use_block_starts_a_tool_call_with_a_readable_subject()
    {
        var events = Replay("claude-allow-run.jsonl");

        var calls = events.OfType<ToolCallStarted>().ToList();
        calls.Should().HaveCount(2);

        calls[0].ToolName.Should().Be("Write");
        calls[0].ToolUseId.Should().Be("toolu_016bDtCrTkJQc5DYS9AeBDep");
        calls[0].Detail.Should().Be("/tmp/agent-spike/run/note.txt");

        calls[1].ToolName.Should().Be("Read");
        calls[1].Detail.Should().Be("/tmp/agent-spike/run/note.txt");
    }

    [Fact]
    public void A_tool_result_completes_the_matching_call_and_recovers_its_tool_name()
    {
        // The CLI's tool_result carries only the tool_use_id, so the parser has to remember the name.
        var events = Replay("claude-allow-run.jsonl");

        var completed = events.OfType<ToolCallCompleted>().ToList();
        completed.Should().HaveCount(2);
        completed[0].ToolUseId.Should().Be("toolu_016bDtCrTkJQc5DYS9AeBDep");
        completed[0].ToolName.Should().Be("Write");
        completed[0].Success.Should().BeTrue();
        completed[0].Detail.Should().Contain("File created successfully");
    }

    [Fact]
    public void A_denied_tool_call_completes_as_a_failure_carrying_the_reason_the_user_gave()
    {
        var events = Replay("claude-deny-run.jsonl");

        var completed = events.OfType<ToolCallCompleted>().Single();
        completed.ToolName.Should().Be("Write");
        completed.Success.Should().BeFalse();
        completed.Detail.Should().Be("the user declined this change in the studio");
    }

    // ---- backlog 104 D3: tool OUTPUT keeps its shape and stderr is not dropped ------------------

    [Fact]
    public void A_multi_line_tool_result_keeps_its_line_structure_instead_of_flattening_it()
    {
        // The detail is rendered in a preformatted block, so a Read's line-numbered listing (or a diff,
        // or a stack trace) has to survive as lines. The parser used to run it through the one-line
        // subject flattener, turning every newline into a space (backlog 104 D3).
        var line = ToolResultLine("t1", """1\tfirst line\n2\tsecond line\n3\tthird line""");

        var detail = Parser().Parse(line).OfType<ToolCallCompleted>().Single().Detail;

        detail.Should().Be("1\tfirst line\n2\tsecond line\n3\tthird line");
        detail.Should().Contain("\n");
    }

    [Fact]
    public void A_tool_result_that_reports_stderr_carries_it_distinctly_from_stdout()
    {
        // A two-part Bash whose second part failed: the first part's stdout is empty of the failure and
        // is_error is absent, so before the fix the call read as an empty SUCCESS and the error was
        // dropped. stderr rides on the tool_use_result sibling and is now carried on its own field so the
        // failure is visible (backlog 104 D3).
        const string line =
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"ran part one\n"}]},"tool_use_result":{"stdout":"ran part one\n","stderr":"part-two: command not found\n","interrupted":false}}""";
        var parser = Parser();
        parser.Parse(ToolUseLine("Bash", """{"command":"true && missing"}"""));

        var completed = parser.Parse(line).OfType<ToolCallCompleted>().Single();

        completed.ToolName.Should().Be("Bash");
        completed.Detail.Should().Be("ran part one\n");
        completed.Error.Should().Be("part-two: command not found\n");
    }

    [Fact]
    public void A_tool_result_with_no_structured_stderr_carries_none()
    {
        var events = Replay("claude-allow-run.jsonl");

        events.OfType<ToolCallCompleted>().Should().OnlyContain(c => c.Error == null);
    }

    // ---- turn boundaries and usage ------------------------------------------------------

    [Fact]
    public void The_result_line_ends_the_turn_and_reports_usage_and_cost()
    {
        var events = Replay("claude-allow-run.jsonl");

        var turn = events.OfType<TurnCompleted>().Single();
        turn.IsError.Should().BeFalse();
        turn.TerminalReason.Should().Be("completed");
        // Backlog 104 D2: the answer is already an assistant bubble (asserted in the dedup test below),
        // so the turn row no longer carries the same prose a second time.
        turn.ResultText.Should().BeNull();

        var stats = events.OfType<StatsUpdated>().Last().Stats;
        stats.InputTokens.Should().Be(18);
        stats.OutputTokens.Should().Be(290);
        stats.CacheReadTokens.Should().Be(41625);
        stats.CostUsd.Should().BeApproximately(0.0200345m, 0.0000001m);
        stats.ToolCalls.Should().Be(2);
        stats.Turns.Should().Be(1);
    }

    [Fact]
    public void An_untyped_turns_answer_is_stored_once_not_twice_as_message_and_result_string()
    {
        // Backlog 104 D2: the terminating line's `result` string is byte-identical to the last assistant
        // text block, already committed as a message and already persisted. Carrying it on the turn row
        // as well stored the whole answer twice, unseen (the renderer draws a completed turn's prose from
        // the message, never from this string). The prose lives as a message; the turn row does not
        // duplicate it.
        var events = Replay("claude-allow-run.jsonl");

        var answer = events.OfType<MessageEmitted>()
            .Last(m => m.Role == TranscriptRole.Assistant).Text;
        answer.Should().Contain("approved by hook");

        events.OfType<TurnCompleted>().Single().ResultText.Should().BeNull();

        // And the answer is present exactly once across the two stores the row could hold it in.
        var timesStored =
            events.OfType<MessageEmitted>().Count(m => m.Text == answer)
            + events.OfType<TurnCompleted>().Count(t => t.ResultText == answer);
        timesStored.Should().Be(1);
    }

    [Fact]
    public void An_untyped_result_that_was_never_said_is_still_kept_on_the_turn_row()
    {
        // The dedup only fires when the exact prose was already committed this turn. A result string the
        // stream never carried as an assistant block (no measured shape, but the guard must not lose it)
        // stays on the row.
        var line =
            """{"type":"result","is_error":false,"terminal_reason":"completed","result":"prose no assistant block ever carried"}""";

        Parser().Parse(line).OfType<TurnCompleted>().Single()
            .ResultText.Should().Be("prose no assistant block ever carried");
    }

    [Fact]
    public void A_result_line_never_ends_the_session_because_the_process_serves_the_next_turn()
    {
        // Proven live: one process answered two prompts over stdin, emitting a result for each.
        var events = Replay("claude-multi-turn.jsonl");

        events.OfType<TurnCompleted>().Should().HaveCount(2);
        events.OfType<SessionEnded>().Should().BeEmpty();
        events.OfType<MessageEmitted>().Select(m => m.Text)
            .Should().Contain(t => t.Contains("turn one"))
            .And.Contain(t => t.Contains("turn two"));
    }

    [Fact]
    public void Stats_accumulate_across_the_turns_of_one_session()
    {
        var events = Replay("claude-multi-turn.jsonl");

        // A turn's OWN counters land on its result line, and those are the two that matter here. Context
        // usage is reported per request and therefore arrives mid-turn as well, so the run carries more
        // stats rows than turns; the ones that count a turn are the ones whose Turns moved.
        var perTurn = events.OfType<StatsUpdated>().Select(s => s.Stats)
            .Where(s => s.Turns > 0)
            .GroupBy(s => s.Turns)
            .Select(g => g.Last())
            .ToList();
        perTurn.Select(s => s.Turns).Should().Equal(1, 2);
        perTurn[1].CostUsd.Should().BeGreaterThan(perTurn[0].CostUsd);
        perTurn[1].OutputTokens.Should().BeGreaterThan(perTurn[0].OutputTokens);
    }

    [Fact]
    public void Context_usage_is_the_last_request_and_the_window_the_provider_reports()
    {
        // Backlog 18 and 19: context usage did not exist, and the obvious place to read it is wrong.
        // An assistant line carries the usage of ITS OWN request, so its input side is the conversation
        // as it was really sent. The result line's usage is the SUM over the turn's requests, which on a
        // turn with a tool call adds one request's cache reads to the next one's and reports roughly
        // twice the context that ever existed (41625 against a real 24434 in this very fixture).
        var events = Replay("claude-allow-run.jsonl");

        var final = events.OfType<StatsUpdated>().Last().Stats;

        final.ContextTokens.Should().Be(8 + 337 + 24089);
        final.ContextWindow.Should().Be(200_000);
        final.ContextFraction.Should().BeApproximately(24434d / 200_000d, 0.0001);

        // And it is a LEVEL, not a flow: the readings rise with the conversation instead of adding up.
        var readings = events.OfType<StatsUpdated>().Select(s => s.Stats.ContextTokens).Distinct().ToList();
        readings.Should().BeInAscendingOrder().And.HaveCountGreaterThan(1);
        readings.Should().NotContain(r => r > 200_000);
    }

    [Fact]
    public void The_sessions_first_context_reading_is_recorded_as_its_baseline()
    {
        // ~24k tokens ride in the FIRST request of a near-trivial run (CLI system prompt, tool schemas,
        // project instructions, the first message) before the conversation contributes anything. That
        // first reading is kept as the baseline so the strip can attribute the fixed prefix honestly
        // instead of letting a young conversation's percentage read as a wrong number.
        var events = Replay("claude-allow-run.jsonl");

        var readings = events.OfType<StatsUpdated>().Select(s => s.Stats)
            .Where(s => s.ContextTokens > 0).ToList();
        var final = readings.Last();

        final.BaselineTokens.Should().Be(readings.First().ContextTokens);
        final.BaselineTokens.Should().BeGreaterThan(0).And.BeLessThan(final.ContextTokens);
        final.ConversationTokens.Should().Be(final.ContextTokens - final.BaselineTokens);
    }

    [Fact]
    public void A_sidechain_assistant_line_never_moves_the_context_level()
    {
        // A line carrying a parent_tool_use_id belongs to a Task subagent's own conversation, so its
        // usage is the SUBAGENT's request size. Taking it would swing the meter to the wrong
        // conversation's level, and on a first turn it could even become the recorded baseline.
        var parser = Parser();
        var events = new List<AgentEvent>();
        events.AddRange(parser.Parse(
            """{"type":"assistant","parent_tool_use_id":null,"message":{"model":"m","usage":{"input_tokens":10,"cache_read_input_tokens":990},"content":[{"type":"text","text":"main thread"}]}}"""));
        var sidechain = parser.Parse(
            """{"type":"assistant","parent_tool_use_id":"toolu_task_1","message":{"model":"m","usage":{"input_tokens":37},"content":[{"type":"text","text":"subagent"}]}}""");
        events.AddRange(parser.Parse(
            """{"type":"assistant","parent_tool_use_id":null,"message":{"model":"m","usage":{"input_tokens":25,"cache_read_input_tokens":1200},"content":[{"type":"text","text":"main again"}]}}"""));

        sidechain.OfType<StatsUpdated>().Should().BeEmpty();

        var stats = events.OfType<StatsUpdated>().Select(s => s.Stats).ToList();
        stats.Select(s => s.ContextTokens).Should().Equal(1000, 1225);
        stats.Last().BaselineTokens.Should().Be(1000);
    }

    [Fact]
    public void A_run_that_reports_no_context_window_offers_no_percentage_at_all()
    {
        // The window arrives only with a finished turn, so a first turn can honestly know how much
        // context is in use and not yet what it is a share of. Inventing the denominator there would put
        // a made-up percentage on the strip, which is worse than showing the tokens alone.
        var events = new ClaudeStreamJsonParser().Parse(
            """
            {"type":"assistant","message":{"model":"m","usage":{"input_tokens":10,"cache_read_input_tokens":990},
            "content":[{"type":"text","text":"hello"}]}}
            """.ReplaceLineEndings(string.Empty));

        var stats = events.OfType<StatsUpdated>().Single().Stats;
        stats.ContextTokens.Should().Be(1000);
        stats.ContextWindow.Should().Be(0);
        stats.ContextFraction.Should().BeNull();
    }

    [Fact]
    public void A_second_init_line_mid_session_does_not_start_a_second_session()
    {
        // Each stdin turn re-emits system/init with the SAME session id.
        var events = Replay("claude-multi-turn.jsonl");

        events.OfType<SessionRegistered>().Select(r => r.ProviderSessionId).Distinct()
            .Should().ContainSingle().Which.Should().Be("709479af-4619-4c89-b47b-789188a3f1df");
    }

    // ---- live typing --------------------------------------------------------------------

    [Fact]
    public void Text_deltas_stream_the_assistant_message_as_it_is_written()
    {
        var events = Replay("claude-allow-run.jsonl");

        var partials = events.OfType<PartialText>().ToList();
        partials.Should().NotBeEmpty();
        string.Concat(partials.Select(p => p.Delta)).Should().Contain("approved by hook");
    }

    [Fact]
    public void Thinking_deltas_are_not_streamed_to_the_transcript()
    {
        // They do drive the working indicator now (ADR 0022 left turns with no text to stream at all),
        // but the thinking itself still never reaches the transcript or the partial message.
        const string thinkingDelta =
            """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"The user wants me"}},"session_id":"s"}""";

        var events = Parser().Parse(thinkingDelta);

        events.OfType<MessageEmitted>().Should().BeEmpty();
        events.OfType<PartialText>().Should().BeEmpty();
        events.Should().AllBeOfType<TurnActivity>();
    }

    [Theory]
    [InlineData("""{"type":"system","subtype":"status","status":"requesting","session_id":"s"}""")]
    [InlineData("""{"type":"system","subtype":"thinking_tokens","estimated_tokens":5,"session_id":"s"}""")]
    [InlineData("""{"type":"stream_event","event":{"type":"message_stop"},"session_id":"s"}""")]
    [InlineData("""{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"thinking"}}}""")]
    public void Housekeeping_lines_produce_nothing(string line) => Parser().Parse(line).Should().BeEmpty();

    // ---- rate limits --------------------------------------------------------------------

    [Fact]
    public void An_allowed_rate_limit_event_is_not_a_hit()
    {
        // The captured event reports status "allowed": reporting that as throttling would be a lie.
        var line = Fixture("claude-allow-run.jsonl").First(l => l.Contains("\"type\":\"rate_limit_event\""));

        Parser().Parse(line).Should().BeEmpty();
    }

    [Fact]
    public void A_throttled_rate_limit_event_counts_as_a_hit()
    {
        const string line =
            """{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":1785885000,"rateLimitType":"five_hour"},"session_id":"s"}""";

        var stats = Parser().Parse(line).OfType<StatsUpdated>().Single().Stats;
        stats.RateLimitHits.Should().Be(1);
    }

    // ---- the approval hook's own lifecycle ----------------------------------------------

    [Fact]
    public void A_healthy_approval_hook_adds_no_transcript_noise()
    {
        // The broker owns the approval events (it holds the answerable request id); the CLI's own
        // hook_started / hook_response lines would only duplicate them.
        var hookLines = Fixture("claude-allow-run.jsonl")
            .Where(l => l.Contains("\"hook_started\"") || l.Contains("\"hook_response\""))
            .ToList();

        hookLines.Should().HaveCount(2);
        hookLines.SelectMany(Parser().Parse).Should().BeEmpty();
    }

    [Fact]
    public void A_broken_approval_hook_is_surfaced_rather_than_swallowed()
    {
        const string line =
            """{"type":"system","subtype":"hook_response","hook_id":"h1","hook_name":"PreToolUse:Write","hook_event":"PreToolUse","output":"","stdout":"","stderr":"curl: (7) Failed to connect","exit_code":7,"outcome":"error","session_id":"s"}""";

        var message = Parser().Parse(line).OfType<MessageEmitted>().Single();
        message.Role.Should().Be(TranscriptRole.System);
        message.Text.Should().Contain("PreToolUse:Write").And.Contain("Failed to connect");
    }

    // ---- robustness ---------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{"type":"assistant"}""")]
    [InlineData("""{"type":"assistant","message":{"content":"a string, not blocks"}}""")]
    [InlineData("""{"type":"user","message":{"role":"user","content":[{"type":"tool_result"}]}}""")]
    [InlineData("""{"type":"something_from_a_future_release","payload":1}""")]
    [InlineData("""{"type":"result"}""")]
    [InlineData("""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Write"}]}}""")]
    public void A_line_the_parser_does_not_understand_is_skipped_and_never_throws(string line)
    {
        var act = () => Parser().Parse(line).ToList();

        act.Should().NotThrow();
    }

    [Fact]
    public void A_truncated_line_is_skipped() =>
        Parser().Parse("""{"type":"assistant","message":{"content":[{"type":"tex""").Should().BeEmpty();

    // ---- readable tool subjects ---------------------------------------------------------

    [Theory]
    [InlineData("""{"file_path":"/w/app/src/main.ts","content":"x"}""", "/w/app/src/main.ts")]
    [InlineData("""{"command":"npm test","description":"Run the suite"}""", "npm test")]
    [InlineData("""{"pattern":"TODO","path":"src"}""", "TODO")]
    [InlineData("""{"url":"https://example.com/spec"}""", "https://example.com/spec")]
    [InlineData("""{"description":"Investigate the failing test","prompt":"..."}""", "Investigate the failing test")]
    [InlineData("""{"unexpected_field":"some value"}""", "some value")]
    [InlineData("""{"count":3}""", "")]
    [InlineData("""{}""", "")]
    public void A_tool_call_carries_the_readable_subject_of_its_input(string input, string expected)
    {
        var line = ToolUseLine("AnyTool", input);

        Parser().Parse(line).OfType<ToolCallStarted>().Single().Detail.Should().Be(expected);
    }

    [Fact]
    public void A_500_char_command_is_carried_raw_because_the_event_is_the_stored_record_of_what_ran()
    {
        // Backlog 103: AgentToolSubject.Shorten output was persisted, so the literal display elision
        // ("...[N more]...") sat in the store where the command belonged and the real command was
        // unrecoverable. The event carries the RAW value now; the renderer shortens on read.
        var raw = "run --flag " + new string('x', 489);
        var line = ToolUseLine("Bash", "{\"command\":\"" + raw + "\"}");

        var detail = Parser().Parse(line).OfType<ToolCallStarted>().Single().Detail;

        detail.Should().Be(raw);
        detail.Should().NotContain("more]");
    }

    [Fact]
    public void A_padded_path_subject_is_carried_whole_so_the_store_names_the_real_path()
    {
        // The padded-path shape from the approval work: the display cap made it read as an ordinary
        // in-copy folder. The event keeps the whole thing; disguise-resistance at render time is
        // AgentToolSubject.Shorten's contract, tested where it lives.
        var padded = "/workspaces/w1/src" + string.Concat(Enumerable.Repeat("/aaaa", 60))
            + string.Concat(Enumerable.Repeat("/..", 63)) + "/data/keys/key-8c2f.xml";
        var line = ToolUseLine("Write", "{\"file_path\":\"" + padded.Replace("\\", "\\\\") + "\",\"content\":\"x\"}");

        var detail = Parser().Parse(line).OfType<ToolCallStarted>().Single().Detail;

        detail.Should().Be(padded);
    }

    /// <summary>One assistant line carrying a single tool_use block with the given input object.</summary>
    [Fact]
    public void A_studio_tool_call_keeps_its_arguments_for_the_rows_words_and_they_survive_the_store()
    {
        // Work item #351: the row says "Moved #343 to In Progress", which needs the arguments, not only the subject.
        var longReason = new string('x', 5000);
        var started = Parser().Parse(ToolUseLine(
                "mcp__fravashi__work_item_transition",
                "{\"number\":343,\"version\":4,\"status\":\"in_progress\",\"reason\":\"" + longReason + "\",\"paths\":[\"a\",\"b\"]}"))
            .OfType<ToolCallStarted>().Single();

        started.Arguments.Should().NotBeNull();
        started.Arguments!["number"].Should().Be("343");
        started.Arguments["status"].Should().Be("in_progress");
        started.Arguments["paths"].Should().Be("[\"a\",\"b\"]");
        started.Arguments["reason"].Length.Should().BeLessThan(longReason.Length, "a long text is kept only by its presence");

        var stored = (ToolCallStarted)AgentEventJson.Deserialize(
            AgentEventJson.KindOf(started)!, AgentEventJson.Serialize(started))!;
        stored.Arguments.Should().NotBeNull();
        stored.Arguments!["number"].Should().Be("343");
        stored.Arguments["status"].Should().Be("in_progress");
    }

    [Fact]
    public void A_built_in_tool_call_keeps_no_arguments_and_its_stored_row_is_unchanged()
    {
        var started = Parser().Parse(ToolUseLine("Bash", """{"command":"ls"}""")).OfType<ToolCallStarted>().Single();

        started.Arguments.Should().BeNull();
        AgentEventJson.Serialize(started).Should().NotContain("arguments");
    }

    private static string ToolUseLine(string tool, string inputJson) =>
        "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\""
        + tool + "\",\"input\":" + inputJson + "}]}}";

    /// <summary>One user line carrying a single tool_result block for the given id, content verbatim.</summary>
    private static string ToolResultLine(string id, string content) =>
        "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\""
        + id + "\",\"content\":\"" + content + "\"}]}}";

    // ---- the whole run ------------------------------------------------------------------

    [Fact]
    public void The_captured_run_replays_into_the_transcript_the_user_would_have_watched()
    {
        var events = Replay("claude-allow-run.jsonl");

        events.OfType<SessionRegistered>().Should().ContainSingle();
        events.OfType<ToolCallStarted>().Should().HaveCount(2);
        events.OfType<ToolCallCompleted>().Should().HaveCount(2);
        events.OfType<TurnCompleted>().Should().ContainSingle();
        events.Should().AllSatisfy(e => e.Timestamp.Should().NotBe(default));
        // Nothing in a healthy run is reported to the user as a system problem.
        events.OfType<MessageEmitted>().Where(m => m.Role == TranscriptRole.System).Should().BeEmpty();
    }
}
