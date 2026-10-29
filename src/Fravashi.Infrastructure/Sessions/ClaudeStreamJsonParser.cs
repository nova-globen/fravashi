using System.Globalization;
using System.Text;
using System.Text.Json;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>
/// Turns one line of the Claude CLI's <c>stream-json</c> output into domain events (plan 2e Task 2).
/// The fixtures the tests replay verbatim are claude 2.1.221 output captured in the container on
/// 2026-08-04, and they have NOT been recaptured since. What keeps them honest is that the shapes this
/// parser depends on were driven live against the pinned 2.1.222 on 2026-08-05 (<c>ClaudeCliContractTests</c>:
/// the init line's session id, assistant text, partial deltas, a real tool_use and its tool_result, and the
/// result line's usage block all still map to the same events). Anything outside that list, notably
/// <c>rate_limit_event</c> and a FAILING <c>hook_response</c>, still rests on the 2.1.221 capture.
/// <para>
/// The parser is stateful on purpose: a <c>tool_result</c> carries only the <c>tool_use_id</c>, so the
/// tool's NAME has to be remembered from the <c>tool_use</c> that opened it, and usage/cost arrive once
/// per turn and accumulate across the turns of one session.
/// </para>
/// <para>
/// It never throws. A malformed, truncated or unrecognised line yields no events, because a protocol
/// the CLI grows in a later release must not take a running session down.
/// </para>
/// <para>
/// The typed-turn shapes (ADR 0022) were captured separately, from claude 2.1.222 in the container on
/// 2026-08-06, and are replayed by <c>ClaudeStreamJsonParserTypedTurnTests</c>: all four terminal
/// outcomes, the synthetic nudge, and the <c>input_json_delta</c> stream that is the only thing moving on
/// a turn the model answers straight through the tool. Two more were captured from 2.1.223 on
/// 2026-08-07: <c>claude-typed-narrated-tool-turn.jsonl</c>, a turn that narrates, calls a real tool and
/// only then answers, and <c>claude-typed-retry-with-tool-turn.jsonl</c>, a turn whose first
/// <c>StructuredOutput</c> was rejected and which read a file before retrying. A third,
/// <c>claude-typed-revised-answer-turn.jsonl</c>, was driven the same day to measure how often a document
/// REVISES the prose instead of repeating it: two of the two tool-using typed captures do.
/// </para>
/// <para>
/// <b>Prose is never held and never re-ordered</b>, with or without a schema. Four attempts to decide per
/// turn which prose was "the answer" and which was "narration", and to suppress or relocate accordingly,
/// each satisfied their own tests and broke a shape nobody had anticipated (the history is in
/// <c>docs/plans/typed-agent-turns.md</c>). That question is not reliably answerable from the stream, so
/// the parser stops asking it: every assistant text block is committed as it completes, in the order it
/// arrived, and the turn's document is committed at the terminal <c>result</c> line ONLY if that exact
/// text has not already been committed during this turn. One comparison, no state machine. The cost is
/// that the transcript carries an answer the model later revised, which is honest: the agent did write it.
/// What a reader sees OPEN is decided downstream, over this complete data, by
/// <see cref="AgentTurnDrafts"/>, which folds the wording a turn's document replaced.
/// </para>
/// </summary>
/// <param name="clock">Where a line with no timestamp of its own gets one.</param>
public sealed class ClaudeStreamJsonParser(Func<DateTimeOffset>? clock = null)
{
    /// <summary>Tool OUTPUT is allowed more room than the subject, but not an unbounded amount.</summary>
    private const int MaxResultLength = 2000;

    /// <summary>
    /// How the harness's structured-output nudge starts. A BACKSTOP for <c>isSynthetic</c>, which is
    /// undocumented and could move in a CLI release: either check alone keeps the nudge out of the
    /// transcript, and each has its own test, so a rename fails the test that names the mechanism.
    /// </summary>
    private const string NudgePrefix = "[structured-output-enforce]";

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Dictionary<string, string> _toolNames = new(StringComparer.Ordinal);

    /// <summary>
    /// The tool name of each open <c>tool_use</c> content block, by its index in the message. An
    /// <c>input_json_delta</c> carries only <c>partial_json</c> and that index, so this is the only way to
    /// tell the turn's own document being written from an ordinary tool call being written.
    /// </summary>
    private readonly Dictionary<int, string> _openToolBlocks = [];

    /// <summary>
    /// The text deltas of the content block currently streaming, and nothing else. It is emptied the
    /// moment that block's assistant line commits it, so it only ever holds prose the user has watched
    /// being typed and the transcript does NOT yet carry. That is exactly one case: a stream cut off
    /// mid-block (Stop kills the process tree; a crash, an OOM kill and a dropped pipe end it the same
    /// way), and <see cref="Drain"/> is where it comes out.
    /// <para>
    /// It is emptied by the NEXT block opening and by the next message starting as well, not only by the
    /// assistant line. The CLI closes every block with an assistant line of its own on all eleven
    /// captures, so those two are belt and braces today, but this parser is written to survive lines it
    /// does not understand and the runner explicitly handles a stream that ends abruptly. Without them a
    /// missing assistant line makes this the deltas of TWO blocks run together, and one bubble reading
    /// "First message.Second message." is two things the model wrote rendered as one utterance.
    /// </para>
    /// <para>
    /// It is not a hold. Nothing waits on it, nothing is re-ordered by it, and on every turn that
    /// reaches its <c>result</c> line it is already empty.
    /// </para>
    /// </summary>
    private readonly StringBuilder _streamed = new();

    /// <summary>
    /// Every assistant bubble this turn has already put in front of the user, so the turn's document
    /// can be recognised as something they have already read. Ordinal, and per turn: the same sentence
    /// in the NEXT turn is a new thing to say.
    /// </summary>
    private readonly HashSet<string> _said = new(StringComparer.Ordinal);

    private readonly AgentRunStats _stats = new();
    private string? _providerSessionId;
    private int _validationFailures;
    private string? _lastValidationError;

    /// <summary>The last context occupancy reported, so an unchanged reading emits no stats row.</summary>
    private long _contextTokens;

    /// <summary>
    /// The <c>modelUsage</c> totals the previous result line of THIS process reported. That block is
    /// cumulative for the whole CLI process, not per turn (the multi-turn capture's second result line says
    /// 41602 cache-read tokens where its own usage block says 24066, and 41602 is 17536 + 24066), so a turn's
    /// share of it is the difference from here. Null until a result line carried one, and reset with the
    /// process by <see cref="ExpectRestart"/>, because a restarted child counts from zero again.
    /// </summary>
    private (long Input, long Output, long CacheRead, decimal CostUsd)? _processModelUsage;

    /// <summary>The CLI's own session id, once its init line has been seen.</summary>
    public string? ProviderSessionId => _providerSessionId;

    /// <summary>
    /// A model change restarts the CLI child with <c>--resume</c>, and the resumed child announces the
    /// SAME conversation id again on its own init line. Without this the parser would swallow that line
    /// as a repeat, and its model field is the first thing that says the switch really took. The runner
    /// calls it right before pumping the restarted child; between-child state ends there and per-turn
    /// state is already clean, because a restart only ever happens between turns.
    /// </summary>
    public void ExpectRestart()
    {
        _providerSessionId = null;
        _processModelUsage = null;
    }

    /// <summary>
    /// Whatever the user watched being typed and the transcript does not carry, as the events to emit,
    /// and clears it. The caller is the runner, on a stream that ENDED without a terminal <c>result</c>
    /// line: the user pressed Stop (which kills the process tree), the CLI crashed, the kernel took it,
    /// or the pipe dropped. Every COMPLETED text block was already committed where it arrived, so all
    /// this can hold is the block that was still streaming, and that is still something the agent
    /// wrote. Empty when the turn closed normally, and empty when the stream was cut between blocks.
    /// <para>
    /// It goes out through <see cref="Say"/> and never through <see cref="SayOnce"/>: the tail is prose
    /// the agent wrote, exactly like a completed block, and the said-already set exists to recognise the
    /// DOCUMENT alone. Running the tail through it drops a sentence this turn happened to say twice, and
    /// on Stop there is no later line to say it again, so it survives in neither the pane nor the
    /// persisted transcript.
    /// </para>
    /// </summary>
    public IReadOnlyList<AgentEvent> Drain()
    {
        List<AgentEvent> events = [];
        Say(events, _streamed.ToString(), _clock());
        EndTurn();
        return events;
    }

    /// <summary>The events one line yields: usually none or one, occasionally several.</summary>
    public IReadOnlyList<AgentEvent> Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return [];
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var timestamp = TimestampOf(root);
            return Text(root, "type") switch
            {
                "system" => SystemLine(root, timestamp),
                "assistant" => Assistant(root, timestamp),
                "user" => User(root, timestamp),
                "result" => Result(root, timestamp),
                "rate_limit_event" => RateLimit(root, timestamp),
                "stream_event" => StreamEvent(root, timestamp),
                _ => [],
            };
        }
    }

    // ---- line kinds ---------------------------------------------------------------------

    private IReadOnlyList<AgentEvent> SystemLine(JsonElement root, DateTimeOffset timestamp)
    {
        switch (Text(root, "subtype"))
        {
            case "init":
                var sessionId = Text(root, "session_id");
                if (string.IsNullOrEmpty(sessionId))
                {
                    return [];
                }

                // Each stdin turn re-announces the SAME session; only the first registration is news.
                if (string.Equals(_providerSessionId, sessionId, StringComparison.Ordinal))
                {
                    return [];
                }

                _providerSessionId = sessionId;
                return [new SessionRegistered(timestamp, sessionId, Text(root, "model"))];

            case "hook_response":
                // A healthy approval hook adds nothing: the broker already owns the approval events
                // and holds the answerable request id. A BROKEN one is a silent failure of the whole
                // approval channel, so it is reported.
                return HookFailure(root, timestamp);

            case "api_retry":
                // Backlog 229: the CLI retrying a failed model call by itself, which was silence before. No
                // captured recording before 2026-10-07 carries one, so nothing an earlier capture yields moves.
                var attempt = (int)Number(root, "attempt");
                return attempt <= 0
                    ? []
                    : [new ApiRetrying(
                        timestamp,
                        attempt,
                        (int)Number(root, "max_retries"),
                        TimeSpan.FromMilliseconds(Number(root, "retry_delay_ms")))];

            default:
                return [];
        }
    }

    private static IReadOnlyList<AgentEvent> HookFailure(JsonElement root, DateTimeOffset timestamp)
    {
        var outcome = Text(root, "outcome");
        var exitCode = root.TryGetProperty("exit_code", out var code) && code.TryGetInt32(out var value) ? value : 0;
        if (exitCode == 0 && (string.IsNullOrEmpty(outcome) || outcome == "success"))
        {
            return [];
        }

        var hook = Text(root, "hook_name") ?? "hook";
        var detail = Text(root, "stderr");
        if (string.IsNullOrWhiteSpace(detail))
        {
            detail = Text(root, "stdout");
        }

        return
        [
            new MessageEmitted(
                timestamp,
                TranscriptRole.System,
                $"The approval hook {hook} failed (exit {exitCode}): {AgentToolSubject.Trim(detail, MaxResultLength)}"),
        ];
    }

    private IReadOnlyList<AgentEvent> Assistant(JsonElement root, DateTimeOffset timestamp)
    {
        if (!TryBlocks(root, out var blocks))
        {
            return [];
        }

        var events = new List<AgentEvent>();

        // Context occupancy is read HERE and not off the result line, because the two answer different
        // questions and only one of them is the question (measured against the pinned CLI, 2026-08-07).
        // An assistant line carries the usage of ITS OWN request, so its input side is the conversation
        // as it was actually sent. The result line's usage is the SUM over the turn's requests, so on a
        // turn with a tool call it adds one request's cache reads to the next one's and reports roughly
        // twice the context that ever existed (41590 against a real 24054 on the probe).
        //
        // And only from the MAIN thread. A line with a parent_tool_use_id belongs to a subagent's
        // sidechain (a Task tool's own conversation), so its usage is the size of the SUBAGENT's request:
        // taking it would swing the meter to the wrong conversation's level until the next main-thread
        // line corrected it, and on the session's first turn it could even become the recorded baseline.
        if (!IsSidechain(root) && Context(root) is { } occupied)
        {
            _stats.ObserveContext(occupied);

            // Only when it MOVED: the CLI repeats one request's usage on every content block of the
            // message it produced, and a stats row per block would be four rows saying one thing.
            if (occupied != _contextTokens)
            {
                _contextTokens = occupied;
                events.Add(new StatsUpdated(timestamp, _stats.Snapshot()));
            }
        }

        foreach (var block in blocks)
        {
            switch (Text(block, "type"))
            {
                case "text":
                    // This line IS the completed block whose deltas the preview was showing, so the
                    // preview has nothing left to rescue whether or not the text is worth committing.
                    _streamed.Clear();

                    var text = Text(block, "text");
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        break;
                    }

                    // Committed where it arrived, always, schema or no schema. Nothing decides here
                    // whether this is "the answer" or "narration": four attempts to make that call from
                    // the stream each broke a shape nobody had anticipated, and it is the document at
                    // the result line, not this block, that gets deduplicated.
                    Say(events, text, timestamp);
                    break;

                case "tool_use":
                    var id = Text(block, "id");
                    var name = Text(block, "name");
                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
                    {
                        break;
                    }

                    // The name is remembered even for the outcome tool, because the matching tool_result
                    // carries only the id and has to be recognisable when it comes back.
                    _toolNames[id] = name;
                    if (name == AgentTurnDocumentSchema.ToolName)
                    {
                        // The turn's OUTCOME, not a tool the user asked for (ADR 0022): no card beside
                        // Read and Edit, and no increment, because counting it over-reports every typed
                        // turn's tool calls by exactly one in the usage strip.
                        break;
                    }

                    // Nothing to flush here any more: whatever the agent said before this call was
                    // committed when its own block completed, which is above this card by
                    // construction rather than by a rule about what tool calls mean.
                    _stats.IncrementToolCalls();
                    events.Add(new ToolCallStarted(timestamp, id, name, Subject(block))
                    {
                        Arguments = StudioArguments(name, block),
                    });
                    break;

                // "thinking" is deliberately not rendered in v1.
            }
        }

        // Backlog 229: a message the CLI wrote ITSELF about a failed model call is told apart from the agent's prose,
        // because only the CLI's own words may decide that a turn failed on the network. No recording taken before
        // 2026-10-07 carries the marker, so nothing an earlier capture yields moves.
        if (root.TryGetProperty("is_api_error_message", out var apiError) && apiError.ValueKind == JsonValueKind.True)
        {
            var said = string.Join(
                "\n",
                blocks
                    .Where(b => Text(b, "type") == "text")
                    .Select(b => Text(b, "text"))
                    .Where(t => !string.IsNullOrWhiteSpace(t)));
            if (said.Length > 0)
            {
                events.Add(new ApiErrorReported(timestamp, said));
            }
        }

        return events;
    }

    private IReadOnlyList<AgentEvent> User(JsonElement root, DateTimeOffset timestamp)
    {
        // The harness's structured-output nudge arrives as an ordinary user line, and the user never
        // wrote it: rendering it would put "[structured-output-enforce] You MUST call ..." in the
        // transcript as something they said. A synthetic line yields NO events at all.
        if (root.TryGetProperty("isSynthetic", out var synthetic) && synthetic.ValueKind == JsonValueKind.True)
        {
            return [];
        }

        if (!TryBlocks(root, out var blocks))
        {
            return [];
        }

        // The structured result rides on the user line beside the content blocks (a Bash result's
        // stdout/stderr live here), so it is read once and attributed to the line's tool result.
        var stderr = ToolStderr(root);

        var events = new List<AgentEvent>();
        foreach (var block in blocks)
        {
            switch (Text(block, "type"))
            {
                case "tool_result":
                    var id = Text(block, "tool_use_id");
                    if (string.IsNullOrEmpty(id))
                    {
                        break;
                    }

                    var isError = block.TryGetProperty("is_error", out var flag)
                        && flag.ValueKind == JsonValueKind.True;
                    var name = _toolNames.TryGetValue(id, out var known) ? known : "tool";
                    // Cap, not Trim: a tool result is rendered in a preformatted block, so its newlines
                    // are content and must survive (backlog 104 D3). Trim would flatten them to spaces.
                    var detail = AgentToolSubject.Cap(ResultContent(block), MaxResultLength);
                    if (name == AgentTurnDocumentSchema.ToolName)
                    {
                        // Either "Structured output provided successfully" or a validator complaint, and
                        // neither is something a user should read as a tool result. A rejection is kept
                        // as a count and a message on the turn instead of being thrown away.
                        if (isError)
                        {
                            _validationFailures++;
                            _lastValidationError = detail;
                        }

                        break;
                    }

                    events.Add(new ToolCallCompleted(timestamp, id, name, !isError, detail, stderr));
                    break;

                case "text":
                    var text = Text(block, "text");
                    if (string.IsNullOrWhiteSpace(text) || text.StartsWith(NudgePrefix, StringComparison.Ordinal))
                    {
                        break;
                    }

                    events.Add(new MessageEmitted(timestamp, TranscriptRole.User, text));
                    break;
            }
        }

        return events;
    }

    private IReadOnlyList<AgentEvent> Result(JsonElement root, DateTimeOffset timestamp)
    {
        var isError = root.TryGetProperty("is_error", out var errorFlag) && errorFlag.ValueKind == JsonValueKind.True;
        long input = 0, output = 0, cacheRead = 0;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            input = Number(usage, "input_tokens");
            output = Number(usage, "output_tokens");
            cacheRead = Number(usage, "cache_read_input_tokens");
        }

        var cost = root.TryGetProperty("total_cost_usd", out var costElement)
            && costElement.TryGetDecimal(out var parsed)
            ? parsed
            : 0m;

        // Outcome iv (ADR 0022, measured 2026-08-06): a turn that ran out of budget reports a usage block of
        // ZEROES, and that ceiling was measured being overshot by about 10x. Reading the zeroed block would
        // under-report exactly the turn that cost the most, and the spend guard (backlog 62) sums these
        // figures, so whenever the usage block says nothing was used, the turn is accounted from modelUsage
        // instead (decided 2026-09-14). modelUsage is CUMULATIVE for the process, though, and
        // --max-budget-usd is a whole-invocation ceiling, so exhaustion typically lands on a later turn of a
        // long conversation: taking the block as it stands would add every earlier turn a second time into
        // stats that already sum per turn. The turn's share is what moved since this process's previous
        // result line. An ordinary turn is untouched: its usage block is the per-turn sum the rest of the
        // studio was built on, and its modelUsage is still remembered so the next turn's share is right.
        var processUsage = ModelUsage(root);
        if (input == 0 && output == 0 && cacheRead == 0 && processUsage is { } total)
        {
            var previous = _processModelUsage ?? (0, 0, 0, 0m);
            input = Math.Max(0, total.Input - previous.Input);
            output = Math.Max(0, total.Output - previous.Output);
            cacheRead = Math.Max(0, total.CacheRead - previous.CacheRead);
            var spent = total.CostUsd - previous.CostUsd;
            if (spent > 0m)
            {
                cost = spent;
            }
            else if (_processModelUsage is not null)
            {
                // total_cost_usd rides the same process-wide total, so with nothing new in modelUsage there
                // is no per-turn figure to fall back to and zero is the honest share.
                cost = 0m;
            }
        }

        if (processUsage is not null)
        {
            _processModelUsage = processUsage;
        }

        _stats.AddTurn(input, output, cacheRead, cost);
        _stats.ObserveContextWindow(ContextWindow(root));

        // THE rule of the typed read path (ADR 0022, measured): the presence of structured_output is the
        // only discriminator. Never is_error, never subtype, never terminal_reason. A turn whose model
        // gave up and answered in prose reports success on every one of those and carries no document,
        // so a reader keying off them accepts an untyped turn as a typed one. A field that is present but
        // null, or present and not an object, counts as ABSENT: there is nothing to render either way.
        var document = root.TryGetProperty("structured_output", out var structured)
            && structured.ValueKind == JsonValueKind.Object
            ? AgentTurnDocumentReader.Read(structured)
            : null;

        var failures = _validationFailures;
        var lastError = _lastValidationError;
        _validationFailures = 0;
        _lastValidationError = null;

        List<AgentEvent> events = [new StatsUpdated(timestamp, _stats.Snapshot())];

        // A block that was still streaming when the result line arrived has no assistant line to
        // commit it, so it is committed here, before the document, in the place it was written. Through
        // Say, not SayOnce: this is prose and not the document, and a turn that said the same sentence
        // earlier would otherwise lose the half the user watched being typed. On every capture we have
        // it is empty anyway: the CLI closes its text blocks.
        Say(events, _streamed.ToString(), timestamp);
        _streamed.Clear();

        // THE one comparison. The document is the turn's answer and it is committed like any other
        // message, unless the user has already read exactly it during this turn, which is the shape
        // every measured typed turn has (the pre-nudge prose was byte identical to the document's
        // message in 6 of 6 turns on 2026-08-06 and in both captures taken on 2026-08-07). Nothing is
        // held for it and nothing is superseded by it: a document that says something new is a new
        // bubble under what came before, in the order it arrived.
        //
        // An EMPTY document message is nothing a user can read, and v1 is permissive on purpose (spec
        // decision 2: no minLength), so it commits nothing. The prose the turn wrote is already in the
        // transcript either way.
        SayOnce(events, document?.Message, timestamp);

        // Backlog 104 D2: the untyped `result` string is byte-identical to the turn's last assistant text
        // block, which was already committed as a message above this row (through Say, so it is in _said)
        // and already persisted. Storing it in the turn row as well stored the whole answer twice, unseen,
        // because the renderer draws a completed turn's prose from that message and never from this string.
        // Drop the duplicate prose but KEEP the row: it is load-bearing for typed documents and for
        // questions carried across a resume. Computed before EndTurn clears the said-already set. A result
        // string that was somehow NOT already said is kept, because losing prose is worse than a rare
        // double.
        var resultText = document is null ? Text(root, "result") : null;
        if (resultText is not null && _said.Contains(resultText.Trim()))
        {
            resultText = null;
        }

        EndTurn();
        events.Add(new TurnCompleted(
            timestamp,
            resultText,
            isError,
            Text(root, "terminal_reason"),
            document,
            failures,
            lastError,
            Errors(root)));

        return events;
    }

    /// <summary>
    /// What the result line's per-model usage adds up to, or null when it carries no tokens and no cost at
    /// all. Summed across models because a turn that delegated to a subagent on another model is billed for
    /// both.
    /// </summary>
    private static (long Input, long Output, long CacheRead, decimal CostUsd)? ModelUsage(JsonElement root)
    {
        if (!root.TryGetProperty("modelUsage", out var models) || models.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        long input = 0, output = 0, cacheRead = 0;
        var cost = 0m;
        foreach (var model in models.EnumerateObject())
        {
            if (model.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            input += Math.Max(0, Number(model.Value, "inputTokens"));
            output += Math.Max(0, Number(model.Value, "outputTokens"));
            cacheRead += Math.Max(0, Number(model.Value, "cacheReadInputTokens"));
            if (model.Value.TryGetProperty("costUSD", out var modelCost)
                && modelCost.TryGetDecimal(out var value)
                && value > 0m)
            {
                cost += value;
            }
        }

        return input == 0 && output == 0 && cacheRead == 0 && cost == 0m
            ? null
            : (input, output, cacheRead, cost);
    }

    /// <summary>
    /// The CLI's own <c>errors</c> on a terminating line, joined, or null when it gave none. Captured
    /// shapes: <c>["Reached maximum budget ($0.0001)"]</c> on outcome iv and
    /// <c>["Failed to provide valid structured output after 5 attempts"]</c> on outcome iii.
    /// </summary>
    private static string? Errors(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var texts = errors.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()))
            .Select(e => e.GetString()!.Trim())
            .ToList();

        return texts.Count == 0 ? null : AgentToolSubject.Trim(string.Join("; ", texts), MaxResultLength);
    }

    /// <summary>
    /// Commits an assistant bubble and remembers that the user has now read it. Unconditional except for
    /// having nothing to say: the same sentence written twice is the agent saying it twice, and a
    /// transcript is what happened. Only the document is checked against what was said, in
    /// <see cref="SayOnce"/>. This is the door ALL prose goes through, a completed block and a streamed
    /// tail alike.
    /// </summary>
    private void Say(List<AgentEvent> events, string? text, DateTimeOffset timestamp)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _said.Add(text.Trim());
        events.Add(new MessageEmitted(timestamp, TranscriptRole.Assistant, text));
    }

    /// <summary>
    /// Commits an assistant bubble unless this turn has already put exactly that text in front of the
    /// user. This is the whole of the de-duplication: one comparison, at the end of the turn, against
    /// what was really committed, rather than a per-turn judgement about which prose was "the answer".
    /// Nothing is lost by staying silent here, because the identical text is already on screen where
    /// the model wrote it.
    /// </summary>
    private void SayOnce(List<AgentEvent> events, string? text, DateTimeOffset timestamp)
    {
        if (string.IsNullOrWhiteSpace(text) || !_said.Add(text.Trim()))
        {
            return;
        }

        events.Add(new MessageEmitted(timestamp, TranscriptRole.Assistant, text));
    }

    /// <summary>
    /// Everything that is true only within one turn: the streamed tail, because it has just been
    /// committed, and the said-already set, because the next turn saying the same sentence is the next
    /// turn saying something.
    /// </summary>
    private void EndTurn()
    {
        _streamed.Clear();
        _said.Clear();
    }

    private IReadOnlyList<AgentEvent> RateLimit(JsonElement root, DateTimeOffset timestamp)
    {
        // The CLI reports the limit's state on every turn, including the healthy one. Only a state
        // that is NOT "allowed" is a throttling event; counting the rest would overstate the problem.
        if (!root.TryGetProperty("rate_limit_info", out var info)
            || info.ValueKind != JsonValueKind.Object
            || Text(info, "status") is null or "allowed")
        {
            return [];
        }

        _stats.IncrementRateLimitHits();
        return [new StatsUpdated(timestamp, _stats.Snapshot())];
    }

    private IReadOnlyList<AgentEvent> StreamEvent(JsonElement root, DateTimeOffset timestamp)
    {
        if (!root.TryGetProperty("event", out var inner) || inner.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        switch (Text(inner, "type"))
        {
            case "message_start":
                // Block indexes restart with every message, so an entry left over from the previous one
                // would attribute the next message's deltas to a tool that is no longer being written,
                // and index 1 of this message is a different block from index 1 of the last. The
                // streamed tail is stale for exactly the same reason: whatever block it belonged to
                // closed with the message that held it, and appending the next message's deltas to it
                // would join two separate things the model wrote into one bubble.
                _openToolBlocks.Clear();
                _streamed.Clear();
                return [];

            case "content_block_start":
                RememberBlock(inner);
                return [];

            case "content_block_delta":
                return Delta(inner, timestamp);

            default:
                return [];
        }
    }

    /// <summary>
    /// Records which content block index is which tool, so its json deltas can be attributed, and starts
    /// a text block's preview from empty.
    /// </summary>
    private void RememberBlock(JsonElement inner)
    {
        if (!inner.TryGetProperty("index", out var index)
            || !index.TryGetInt32(out var position)
            || !inner.TryGetProperty("content_block", out var block)
            || block.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var type = Text(block, "type");
        if (type == "tool_use" && Text(block, "name") is { Length: > 0 } name)
        {
            _openToolBlocks[position] = name;
        }
        else
        {
            // A text or thinking block reusing an index retires whatever tool held it.
            _openToolBlocks.Remove(position);
        }

        if (type == "text")
        {
            // A NEW block's deltas are a new bubble, so the previous block's tail cannot be part of it.
            // Normally that tail is already gone, committed by its own assistant line; when it is not,
            // keeping it would render two blocks as one utterance instead of dropping the first.
            _streamed.Clear();
        }
    }

    private IReadOnlyList<AgentEvent> Delta(JsonElement inner, DateTimeOffset timestamp)
    {
        if (!inner.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        switch (Text(delta, "type"))
        {
            case "text_delta":
                var text = Text(delta, "text");
                if (string.IsNullOrEmpty(text))
                {
                    return [];
                }

                // The preview and the commit are the same string, with no fixing up: one block streams,
                // that same block commits as one bubble, and the reader watches a bubble appear rather
                // than a paragraph reflow. Kept so a stream cut off mid-block still shows what was
                // typed (Drain).
                _streamed.Append(text);
                return [new PartialText(timestamp, text)];

            case "thinking_delta":
                // The thinking itself stays unrendered; that it is happening is the honest signal.
                return [new TurnActivity(timestamp, AgentTurnPhase.Thinking)];

            case "input_json_delta":
                // On a turn the model answers straight through the tool this is the ONLY thing moving,
                // which is why the indicator has to be built on it (measured 2026-08-06). An index whose
                // opening block was never seen is treated as an ordinary tool: claiming the turn's answer
                // is being written when that is not known would be the studio believing something it
                // was not told.
                return
                [
                    new TurnActivity(
                        timestamp,
                        ToolAt(inner) == AgentTurnDocumentSchema.ToolName
                            ? AgentTurnPhase.Composing
                            : AgentTurnPhase.ToolInput),
                ];

            default:
                return [];
        }
    }

    private string? ToolAt(JsonElement inner) =>
        _openToolBlocks.TryGetValue(IndexOf(inner), out var name) ? name : null;

    /// <summary>The content-block index a stream event is about, or -1 when it does not carry one.</summary>
    private static int IndexOf(JsonElement inner) =>
        inner.TryGetProperty("index", out var index) && index.TryGetInt32(out var position) ? position : -1;

    // ---- helpers ------------------------------------------------------------------------

    /// <summary>
    /// True when the line belongs to a subagent's sidechain rather than the main conversation. Every
    /// captured line carries <c>parent_tool_use_id</c>, null on the main thread and the spawning Task's
    /// tool_use id inside a sidechain, so a string there is the discriminator.
    /// </summary>
    private static bool IsSidechain(JsonElement root) =>
        root.TryGetProperty("parent_tool_use_id", out var parent) && parent.ValueKind == JsonValueKind.String;

    /// <summary>
    /// The input side of one request, which is the conversation as the model received it: the fresh
    /// tokens, the ones read from cache, and the ones written to cache. Null when the line carries no
    /// usage at all, so that a missing block leaves the previous reading standing rather than zeroing it.
    /// </summary>
    private static long? Context(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var total = Number(usage, "input_tokens")
            + Number(usage, "cache_read_input_tokens")
            + Number(usage, "cache_creation_input_tokens");

        return total > 0 ? total : null;
    }

    /// <summary>
    /// The context window the provider states for the model that served this turn, read from the result
    /// line's per-model usage. The turn names exactly one model there in every capture we have, and the
    /// LARGEST window is taken when it names more, because a percentage against the smaller one would
    /// overstate how full the conversation is.
    /// </summary>
    private static long ContextWindow(JsonElement root)
    {
        if (!root.TryGetProperty("modelUsage", out var models) || models.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        long window = 0;
        foreach (var model in models.EnumerateObject())
        {
            if (model.Value.ValueKind == JsonValueKind.Object)
            {
                window = Math.Max(window, Number(model.Value, "contextWindow"));
            }
        }

        return window;
    }

    /// <summary>
    /// The subject of a tool call: what it is about to act on. RAW, because this becomes the
    /// persisted <c>tool_started</c> row (backlog 103): shortening here stored the literal display
    /// elision where the command belonged and made the real command unrecoverable. The renderer
    /// shortens on read; only <see cref="AgentToolSubject.ForStorage"/>'s honest cap applies here.
    /// </summary>
    /// <summary>The longest single argument value a transcript row keeps for a studio tool call (work item #351).</summary>
    internal const int MaxStoredArgumentLength = 400;

    /// <summary>
    /// A studio tool call's arguments for its transcript row (work item #351): every top-level value as a string, a long
    /// text capped (its presence is what a label reads, never its whole body) and an array kept as its JSON while it is
    /// short, else as "N items" so a label can still count it. Null for every tool that is not the studio's own, whose
    /// subject already says what it did.
    /// </summary>
    private static Dictionary<string, string>? StudioArguments(string name, JsonElement block)
    {
        if (!name.StartsWith(AgentWorkGate.StudioToolPrefix, StringComparison.Ordinal)
            || !block.TryGetProperty("input", out var input)
            || input.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in input.EnumerateObject())
        {
            var value = property.Value;
            var text = value.ValueKind switch
            {
                JsonValueKind.String => AgentToolSubject.Shorten(value.GetString(), MaxStoredArgumentLength),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
                JsonValueKind.Array => value.GetRawText() is { Length: <= MaxStoredArgumentLength } raw
                    ? raw
                    : value.GetArrayLength().ToString(CultureInfo.InvariantCulture) + " items",
                JsonValueKind.Object => "{...}",
                _ => null,
            };

            if (!string.IsNullOrEmpty(text))
            {
                arguments[property.Name] = text;
            }
        }

        return arguments.Count == 0 ? null : arguments;
    }

    private static string Subject(JsonElement block)
    {
        if (!block.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        foreach (var field in AgentToolSubject.Fields)
        {
            if (input.TryGetProperty(field, out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return AgentToolSubject.ForStorage(value.GetString());
            }
        }

        // An unknown tool still deserves a subject: the first thing in it that reads as one.
        foreach (var property in input.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                return AgentToolSubject.ForStorage(property.Value.GetString());
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// A tool's standard ERROR, read from the user line's structured <c>tool_use_result</c> sibling: the
    /// content block the model saw carries the tool's OUTPUT, and a Bash result's stderr is a separate
    /// field on this object beside it. Kept apart from the content so a sub-command that failed only on
    /// stderr is not rendered as an empty success (backlog 104 D3); newlines preserved, capped only for
    /// length. Null when the line carries no structured stderr, which is every non-Bash result.
    /// </summary>
    private static string? ToolStderr(JsonElement root)
    {
        if (!root.TryGetProperty("tool_use_result", out var result) || result.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var stderr = Text(result, "stderr");
        return string.IsNullOrEmpty(stderr) ? null : AgentToolSubject.Cap(stderr, MaxResultLength);
    }

    /// <summary>A tool_result's content is either a plain string or a list of content blocks.</summary>
    private static string ResultContent(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var content))
        {
            return string.Empty;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && Text(item, "type") == "text")
            {
                builder.Append(Text(item, "text"));
            }
        }

        return builder.ToString();
    }

    private static bool TryBlocks(JsonElement root, out JsonElement.ArrayEnumerator blocks)
    {
        blocks = default;
        if (!root.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        blocks = content.EnumerateArray();
        return true;
    }

    private DateTimeOffset TimestampOf(JsonElement root) =>
        root.TryGetProperty("timestamp", out var value)
        && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(
            value.GetString(),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : _clock();

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt64(out var parsed) ? parsed : 0;
}
