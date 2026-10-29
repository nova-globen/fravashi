namespace Fravashi.Domain.AgentSessions;

/// <summary>
/// Domain events streamed by an agent-session runner as it parses the CLI's stream-json output
/// (plan 2e). The live registry fans them out to every UI surface watching the session, and the
/// session store persists them as the transcript.
/// </summary>
public abstract record AgentEvent(DateTimeOffset Timestamp);

/// <summary>The studio started a session; its own id is the handle every surface addresses it by.</summary>
/// <param name="TypedTurns">
/// True when this session's CLI process was started with <c>--json-schema</c> (ADR 0022), so every turn it
/// serves was ASKED to end in a document. It is recorded here, on the studio's own event, rather than
/// inferred later, because a turn that carries no document means two different things depending on it:
/// on an untyped session that is simply how turns end, and on a typed one it is outcome ii, the model
/// giving up on the schema and answering in prose, which the transcript must mark rather than present as
/// a typed answer (backlog 25 increment 5). It is a process argument, so it holds for every turn until
/// the next <see cref="SessionStarted"/>; a row persisted before it existed reads back as false, which is
/// what those sessions were by default.
/// </param>
public sealed record SessionStarted(DateTimeOffset Timestamp, AgentSessionId SessionId, bool TypedTurns = false)
    : AgentEvent(Timestamp);

/// <summary>
/// The CLI announced itself (its <c>system</c>/init line): the session id it will answer to under
/// <c>--resume</c>, and the model actually serving the run (which may differ from what was asked for).
/// </summary>
public sealed record SessionRegistered(DateTimeOffset Timestamp, string ProviderSessionId, string? Model)
    : AgentEvent(Timestamp);

/// <summary>
/// The studio started the conversation's process at an effort level, or moved it to one (requested 2026-10-22,
/// model and effort per work item). Null is the CLI's own default. The studio records it itself because the CLI's
/// init line says nothing about effort, so this row is the only memory of it: a resume that names no effort reads
/// the last one back, the way it reads the last model from <see cref="SessionRegistered"/>.
/// </summary>
public sealed record EffortChosen(DateTimeOffset Timestamp, AgentEffort? Effort) : AgentEvent(Timestamp);

/// <summary>
/// The conversation is GRAPH-ONLY, "Notes and items only" (work item #352): it may read the folder's copy and read and
/// write the tracker and the wiki, and nothing else (<see cref="AgentGraphOnlyTools"/>). Written once, when such a
/// conversation is first started, and never undone: there is no event that makes a conversation an editing one, so
/// every later resume reads this row back and starts it graph-only again.
/// </summary>
public sealed record GraphOnlyChosen(DateTimeOffset Timestamp) : AgentEvent(Timestamp);

/// <summary>
/// The conversation is the STUDIO ASSISTANT (work item #359): Home's general assistant, held to
/// <see cref="AgentAssistantTools"/>. Written once, when such a conversation is first started, and never undone, like
/// <see cref="GraphOnlyChosen"/>: every later resume reads this row back and starts it as the assistant again.
/// </summary>
public sealed record AssistantChosen(DateTimeOffset Timestamp) : AgentEvent(Timestamp);

/// <summary>A complete message turn from one participant, ready to render as a chat bubble.</summary>
public sealed record MessageEmitted(DateTimeOffset Timestamp, TranscriptRole Role, string Text) : AgentEvent(Timestamp);

/// <summary>
/// A streamed text fragment of the assistant message still being written (the CLI's
/// <c>--include-partial-messages</c> deltas). Transient: the finished text arrives as a
/// <see cref="MessageEmitted"/>, so partials are never persisted.
/// </summary>
public sealed record PartialText(DateTimeOffset Timestamp, string Delta) : AgentEvent(Timestamp);

/// <summary>
/// What the agent is doing right now, read off the CLI's partial-message stream.
/// <para>
/// This exists because a turn under <c>--json-schema</c> can be completely silent in text: measured on
/// 2026-08-06 against claude 2.1.222, a turn where the model calls <c>StructuredOutput</c> straight away
/// carries NO assistant text block at all, so no <c>text_delta</c> and therefore no
/// <see cref="PartialText"/> between the user pressing send and the result line arriving. What every one
/// of those turns did carry was <c>thinking_delta</c> and <c>input_json_delta</c>, so those are what an
/// honest working indicator is built on.
/// </para>
/// </summary>
public enum AgentTurnPhase
{
    /// <summary>The turn was written to the CLI's stdin and nothing has come back yet.</summary>
    Submitted,

    /// <summary>The model is reasoning (a <c>thinking_delta</c>). The thinking itself is never rendered.</summary>
    Thinking,

    /// <summary>The model is writing the turn's document (an <c>input_json_delta</c> on the outcome tool's block).</summary>
    Composing,

    /// <summary>The model is writing an ordinary tool call's input, which already has its own card.</summary>
    ToolInput,
}

/// <summary>
/// The agent is still working, and this is what it is doing. Transient exactly like
/// <see cref="PartialText"/>: it drives the working indicator and is never persisted, because a phase is
/// only true for the instant it was emitted.
/// </summary>
public sealed record TurnActivity(DateTimeOffset Timestamp, AgentTurnPhase Phase) : AgentEvent(Timestamp);

/// <summary>
/// The agent CLI's model call failed and the CLI is trying it again by itself (its <c>system</c>/<c>api_retry</c>
/// line, backlog 229). Measured against the pinned 2.1.280 on 2026-10-07 with an unresolvable API host: ten of
/// these over about three minutes, with the delay doubling from half a second to about forty, and <c>error</c>
/// "unknown" on every one, so this says only that it is retrying, never why. Transient like
/// <see cref="TurnActivity"/>: it drives a progress line and is never persisted.
/// </summary>
public sealed record ApiRetrying(DateTimeOffset Timestamp, int Attempt, int MaxRetries, TimeSpan Delay)
    : AgentEvent(Timestamp);

/// <summary>
/// The agent CLI's OWN words for a failed model call (backlog 229): the synthetic assistant message it marks
/// <c>is_api_error_message</c> (model <c>&lt;synthetic&gt;</c>, measured against 2.1.280 on 2026-10-07), as opposed to
/// anything the agent wrote. It rides beside the ordinary message the same line still yields, so the transcript
/// is unchanged; only what decides "was this the network" reads it. Transient, never persisted.
/// </summary>
public sealed record ApiErrorReported(DateTimeOffset Timestamp, string Text) : AgentEvent(Timestamp);

/// <summary>
/// The agent invoked a tool. <paramref name="Detail"/> is the subject (a path, a command), RAW: the
/// stored transcript is the record of what actually ran, so display shortening derives from this on
/// read, never the reverse (backlog 103).
/// </summary>
public sealed record ToolCallStarted(DateTimeOffset Timestamp, string ToolUseId, string ToolName, string Detail)
    : AgentEvent(Timestamp)
{
    /// <summary>
    /// A studio tool call's arguments, flattened to strings and each capped (work item #351), so its transcript row can
    /// say what it did in words ("Linked this conversation to #343") and show the arguments when opened. Null for a
    /// built-in tool, whose subject already says it, and for every row stored before the arguments were kept.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Arguments { get; init; }
}

/// <summary>The tool's result came back. A denied call arrives here too, as a failure carrying the reason.</summary>
/// <param name="Detail">
/// The tool's standard OUTPUT (or the denial reason), with its line structure intact: it is rendered in
/// a preformatted block, so a diff, a stack trace or a file listing keeps the shape the tool produced
/// (backlog 104 D3). The one-line subject flattening is for the tool SUBJECT, never its output.
/// </param>
/// <param name="Error">
/// The tool's standard ERROR, kept distinct from <paramref name="Detail"/> so a sub-command that failed
/// only by writing to stderr is not shown as an empty success (backlog 104 D3). Null when the tool
/// reported none; a tool that legitimately writes progress to stderr still succeeds, so this carries the
/// text without deciding the outcome (that stays with <paramref name="Success"/>).
/// </param>
public sealed record ToolCallCompleted(
    DateTimeOffset Timestamp,
    string ToolUseId,
    string ToolName,
    bool Success,
    string Detail,
    string? Error = null)
    : AgentEvent(Timestamp);

/// <summary>
/// A tool call is waiting on the user (decision 5): the PreToolUse hook is blocked inside the
/// CLI until the studio answers <see cref="ToolApprovalResolved"/> for this request id.
/// </summary>
/// <param name="Warning">
/// Why a call the user would expect to run unattended is asking anyway (a read that leaves the
/// session's contained copy, or one whose path could not be worked out), so the transcript keeps that
/// reason and not only the answer. Null for an ordinary change, which asks because every change asks.
/// </param>
public sealed record ToolApprovalRequested(
    DateTimeOffset Timestamp,
    string RequestId,
    string ToolName,
    string Detail,
    string? Warning = null)
    : AgentEvent(Timestamp);

/// <summary>The user (or the auto-allow policy, or the timeout) answered a pending approval.</summary>
/// <param name="DecidedBy">
/// Who the answer came from (backlog 114). A transcript may only say the user denied something when the
/// user is the one who did; a studio-imposed refusal is named for its cause instead. It DEFAULTS to
/// <see cref="ToolApprovalDecidedBy.Studio"/>, which is also what a row persisted before this parameter
/// existed reads back as, because attributing a refusal to the user who never made it is the worse of
/// the two possible mistakes.
/// </param>
/// <param name="RepeatCount">
/// For a refusal the STUDIO imposed, how many times this session has now been refused the same call
/// automatically, this one included (backlog 117); 1 for the first time and for every other answer. An
/// agent pushing against a refusal is exactly the signal a reader must be able to see.
/// </param>
public sealed record ToolApprovalResolved(
    DateTimeOffset Timestamp,
    string RequestId,
    string ToolName,
    bool Approved,
    string Reason,
    ToolApprovalDecidedBy DecidedBy = ToolApprovalDecidedBy.Studio,
    int RepeatCount = 1)
    : AgentEvent(Timestamp);

/// <summary>
/// The studio refused a tool call ON ITS OWN ACCOUNT without asking anybody (backlog 117): the call was
/// never parked, so there is no <see cref="ToolApprovalRequested"/> before it and no request id. Before
/// this existed such a refusal left nothing in the transcript, and an agent could retry a refused call any
/// number of times while the user saw nothing.
/// </summary>
/// <param name="ToolName">The tool the CLI named, empty when the call could not be read far enough to say.</param>
/// <param name="Detail">The call's subject (a path, a command), empty when there was none.</param>
/// <param name="Reason">The refusal sentence the CLI was sent.</param>
/// <param name="RepeatCount">
/// How many times this session has now been refused the same call automatically, this one included, so a
/// repeat reads as a repeat rather than as one more ordinary line.
/// </param>
public sealed record ToolCallRefused(
    DateTimeOffset Timestamp,
    string ToolName,
    string Detail,
    string Reason,
    int RepeatCount = 1)
    : AgentEvent(Timestamp);

/// <summary>
/// A change the studio let run WITHOUT asking under the workspace's trust level (ADR 0028, backlog 152). Recorded
/// because an unattended change must be as visible as a refused one: the detail is the call's subject (the path)
/// and the reason names the level that let it run.
/// </summary>
public sealed record ToolCallTrusted(
    DateTimeOffset Timestamp,
    string ToolName,
    string Detail,
    string Reason)
    : AgentEvent(Timestamp);

/// <summary>
/// A call an autopilot session's own rule list or judge let run WITHOUT asking (ADR 0031 decisions 7 and 8,
/// backlog 299), distinct from <see cref="ToolCallTrusted"/> (ADR 0028's different mechanism): recorded
/// because an unattended autopilot decision must be exactly as visible as a refused one, and the reason
/// names the rule or the judgement that allowed it.
/// </summary>
public sealed record ToolCallAutopilotAllowed(
    DateTimeOffset Timestamp,
    string ToolName,
    string Detail,
    string Reason)
    : AgentEvent(Timestamp);

/// <summary>Refreshed run counters (tokens, cost, tool calls), emitted when the CLI reports usage.</summary>
public sealed record StatsUpdated(DateTimeOffset Timestamp, AgentStatsSnapshot Stats) : AgentEvent(Timestamp);

/// <summary>
/// One TURN finished (the CLI's <c>result</c> line). In a stdin-driven session the process stays alive
/// for the next prompt, so this is not the end of the session: that is <see cref="SessionEnded"/>,
/// emitted when the process exits (two turns in one process, seen against claude 2.1.221 and driven again
/// against the pinned 2.1.222 on 2026-08-05; see ADR 0023).
/// <para>
/// Under <c>--json-schema</c> (ADR 0022) a turn also ends in a <see cref="Document"/>, and
/// <b>its presence is the only honest discriminator of what happened</b>. A turn whose model gave up and
/// answered in prose instead reports <see cref="IsError"/> false, <c>subtype</c> success and
/// <see cref="TerminalReason"/> completed while carrying no document (measured 2026-08-06), which is why
/// nothing may read "was this typed" off <see cref="IsError"/>. Those two keep their old meaning, because
/// they are still the right inputs to <see cref="AgentTerminalReason.ToSessionState"/>.
/// </para>
/// </summary>
/// <param name="ResultText">
/// The CLI's own <c>result</c> string, and null once <paramref name="Document"/> is present: the
/// terminating line carries the document TWICE, parsed and as the same JSON in this string, so surfacing
/// the string as well would put raw JSON in front of the user.
/// </param>
/// <param name="Document">The turn's typed outcome, or null when the turn ended without one.</param>
/// <param name="ValidationFailures">
/// How many <c>StructuredOutput</c> attempts the CLI's validator rejected during this turn. The harness
/// owns the retry (five attempts, measured) and the studio adds none of its own; the count exists so that
/// keeping those error results out of the transcript does not also destroy the only evidence that a turn
/// was quietly burning attempts.
/// </param>
/// <param name="LastValidationError">
/// The validator's own message for the last rejected attempt, naming the JSON-Pointer path and the
/// violated keyword. The most actionable thing the studio has when a schema is genuinely wrong.
/// </param>
/// <param name="ErrorText">
/// The CLI's own <c>errors</c> for a turn it ended in error, joined, or null when it gave none. This is
/// where a turn that ran out of budget names the budget (<c>Reached maximum budget ($0.0001)</c>) and a
/// turn that exhausted its validation attempts states how many (measured 2026-08-06), so the transcript
/// quotes the CLI instead of guessing at what the ceiling was.
/// </param>
public sealed record TurnCompleted(
    DateTimeOffset Timestamp,
    string? ResultText,
    bool IsError,
    string? TerminalReason,
    AgentTurnDocument? Document = null,
    int ValidationFailures = 0,
    string? LastValidationError = null,
    string? ErrorText = null)
    : AgentEvent(Timestamp);

/// <summary>The session process is gone, with the state it ended in.</summary>
/// <param name="Resumable">
/// True when the process ended but the CONVERSATION did not: the CLI exited because one turn exhausted its
/// validation attempts (ADR 0022 outcome iii exits with code 1, and this runner holds one process per
/// conversation), while the provider session id it would resume under is already known. The studio then
/// offers Continue, which starts <c>--resume</c> on the same conversation, instead of a failure that
/// silently loses it (backlog 25 increment 6). False for every other ending, including a row persisted
/// before it existed.
/// </param>
public sealed record SessionEnded(
    DateTimeOffset Timestamp,
    AgentSessionState FinalState,
    string? FailureReason,
    bool Resumable = false)
    : AgentEvent(Timestamp);

/// <summary>
/// The CLI's own words for why a turn ended, as they arrive on its <c>terminal_reason</c> (ADR 0022's
/// measured table). Only the reasons the studio renders differently are named; anything else is left to
/// <see cref="AgentTerminalReason.ToSessionState"/>.
/// </summary>
public static class AgentTurnEnding
{
    /// <summary>Outcome iii: the turn's document failed validation on every attempt the harness allows.</summary>
    public const string RetriesExhausted = "structured_output_retry_exhausted";

    /// <summary>Outcome iv: the invocation reached its <c>--max-budget-usd</c> ceiling.</summary>
    public const string BudgetExhausted = "budget_exhausted";
}

/// <summary>
/// Maps the CLI's own end-of-run vocabulary (<c>terminal_reason</c> plus the <c>is_error</c> flag on its
/// <c>result</c> line) onto a session state. Deliberately conservative: an unrecognised reason is NOT
/// evidence of failure, so the run counts as finished and the raw word is kept for the UI to show.
/// </summary>
public static class AgentTerminalReason
{
    public static AgentSessionState ToSessionState(string? terminalReason, bool isError)
    {
        if (isError)
        {
            return AgentSessionState.Failed;
        }

        var reason = terminalReason?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(reason))
        {
            return AgentSessionState.Completed;
        }

        if (reason.StartsWith("error", StringComparison.Ordinal))
        {
            return AgentSessionState.Failed;
        }

        return reason is "cancelled" or "canceled" or "aborted" or "interrupted"
            ? AgentSessionState.Cancelled
            : AgentSessionState.Completed;
    }
}

/// <summary>Lifecycle helpers for <see cref="AgentSessionState"/>.</summary>
public static class AgentSessionStateExtensions
{
    /// <summary>True once the session has ended, however it ended.</summary>
    public static bool IsTerminal(this AgentSessionState state) =>
        state is AgentSessionState.Completed or AgentSessionState.Failed or AgentSessionState.Cancelled;
}

/// <summary>
/// Images the user pasted into the turn that follows (backlog 26). Recorded immediately BEFORE that turn's
/// user <see cref="MessageEmitted"/>, so everything after a user message is still the agent's answer to it,
/// and surfaces render the images inside the user's bubble. It is the studio's own event: the CLI never
/// echoes a user turn, so the parser never produces one.
/// </summary>
public sealed record UserImagesAttached(DateTimeOffset Timestamp, IReadOnlyList<AgentImageAttachment> Images)
    : AgentEvent(Timestamp);
