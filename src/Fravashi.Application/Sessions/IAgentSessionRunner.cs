using Fravashi.Domain.AgentSessions;

namespace Fravashi.Application.Sessions;

/// <summary>
/// A request to run one agent session against a workspace's CONTAINED copy (plan 2d decision 2: the
/// studio never runs an agent on a host path).
/// </summary>
/// <param name="SessionId">The studio's own id for the session; every surface addresses it by this.</param>
/// <param name="WorkspaceId">The workspace whose synced copy the agent works in.</param>
/// <param name="WorkingDirectory">The copy's absolute path inside the container (<c>/workspaces/{id}</c>).</param>
/// <param name="Prompt">
/// The first user turn. Follow-ups arrive through <see cref="IAgentSessionRunner.SendAsync"/>. Blank only
/// when continuing a conversation (<paramref name="ResumeSessionId"/> set) with nothing to say yet: the
/// resumed process then waits for the next turn instead of being handed one (backlog 25 increment 6).
/// </param>
/// <param name="PromptImages">
/// Images pasted into the first user turn (backlog 26), sent as image blocks ahead of <paramref name="Prompt"/>.
/// When present the prompt may be blank and the turn is still sent: it is not a Continue.
/// </param>
/// <param name="ResumeSessionId">The CLI's own session id to <c>--resume</c>, when continuing a prior conversation.</param>
/// <param name="Model">The model to pass to the CLI; null keeps the CLI's own default (decision 7).</param>
/// <param name="DocumentKinds">
/// The document kinds this session's turns may end in (ADR 0022). Non-empty makes the runner pass
/// <c>--json-schema</c> with the schema <see cref="AgentTurnDocumentSchema.For"/> assembles for exactly
/// those kinds, so the CLI enforces that every turn ends by calling <c>StructuredOutput</c>. Null or empty
/// is the untyped path: no flag, and prose is the answer.
/// <para>
/// The set is per SESSION rather than per turn, because <c>--json-schema</c> is a process argument and one
/// long-lived process serves the whole conversation (decision 6). Varying it per turn would mean
/// restarting the CLI mid-conversation, and nothing needs that while v1 sends one kind.
/// </para>
/// </param>
public sealed record AgentSessionRequest(
    AgentSessionId SessionId,
    string WorkspaceId,
    string WorkingDirectory,
    string Prompt,
    string? ResumeSessionId = null,
    string? Model = null,
    IReadOnlyList<AgentTurnDocumentKind>? DocumentKinds = null,
    decimal? MaxBudgetUsd = null,
    IReadOnlyList<AgentImageAttachment>? PromptImages = null)
{
    /// <summary>
    /// The per-session spend ceiling in USD (backlog 62), or null for no ceiling. It becomes the CLI's
    /// own <c>--max-budget-usd</c>, which is the half of the item the studio does not have to build: the
    /// invocation stops itself, so an unattended run cannot outspend it between the studio's own checks.
    /// Null is the whole of "unset": a ceiling nobody typed must never become one that stops a run.
    /// </summary>
    public decimal? MaxBudgetUsd { get; init; } = MaxBudgetUsd is > 0m ? MaxBudgetUsd : null;

    /// <summary>
    /// The ONLY built-in tools the CLI starts with, or null for its full set (backlog 243). The commit reviewer's
    /// conversation is started with <c>AgentReviewTools.BuiltIn</c> so it has no Edit, Write or Bash to call at all; the
    /// approval broker refuses anything else it reaches for as well. Null on every ordinary conversation, which then
    /// sends no <c>--tools</c> flag and its command line is exactly the recorded one (ADR 0023).
    /// </summary>
    public IReadOnlyList<string>? BuiltInTools { get; init; }

    /// <summary>
    /// True for a GRAPH-ONLY conversation, "Notes and items only" (work item #352). The runner then starts the CLI
    /// with <c>AgentGraphOnlyTools.BuiltIn</c> as its <c>--tools</c> whatever <see cref="BuiltInTools"/> says, holds
    /// the session graph-only at the approval broker the moment it registers it, and opens its first turn with
    /// <c>AgentFirstTurnBriefing.GraphOnlyNote</c>. A <c>with</c> copy (a model, effort or credential restart) keeps it.
    /// False on every other conversation, whose command line is then exactly what it was.
    /// </summary>
    public bool GraphOnly { get; init; }

    /// <summary>
    /// The built-in tools the CLI is really started with: a graph-only conversation's reading set, else
    /// <see cref="BuiltInTools"/>. The one reading both the command line and anything asking about it use.
    /// </summary>
    public IReadOnlyList<string>? EffectiveBuiltInTools =>
        Assistant ? AgentAssistantTools.BuiltIn : GraphOnly ? AgentGraphOnlyTools.BuiltIn : BuiltInTools;

    /// <summary>
    /// True for the STUDIO ASSISTANT (work item #359), Home's general assistant. Like <see cref="GraphOnly"/>: the runner
    /// starts the CLI with <c>AgentAssistantTools.BuiltIn</c> as its <c>--tools</c> whatever <see cref="BuiltInTools"/>
    /// says, holds the session as the assistant at the approval broker the moment it registers it, and opens its first
    /// turn with <c>AgentFirstTurnBriefing.AssistantNote</c>. A <c>with</c> copy keeps it.
    /// </summary>
    public bool Assistant { get; init; }

    /// <summary>
    /// How hard the CLI is asked to think (requested 2026-10-22, model and effort per work item), or null for
    /// "Default", which sends no <c>--effort</c> flag at all, so every request that names none has exactly the
    /// recorded command line (ADR 0023, <c>AgentCliPinTests</c>). A closed set rather than a string: only
    /// <see cref="AgentEfforts.CliValue"/> of a defined level ever reaches the command line.
    /// </summary>
    public AgentEffort? Effort { get; init; }

    /// <summary>
    /// True when this session's turns end in a document, which is exactly when the runner passes
    /// <c>--json-schema</c> and therefore exactly when the CLI has a <c>StructuredOutput</c> tool to call.
    /// <para>
    /// It is one property rather than three readings of <see cref="DocumentKinds"/> because three parts of
    /// the studio have to agree about it: the flag on the command line, the parser (prose is provisional
    /// only under a schema), and the approval broker (a turn's terminating document is not something to ask
    /// the user about, but ONLY when the studio is the one that asked for it). If those three ever
    /// disagreed, the disagreement would show up as a session that hangs on an approval nobody expected.
    /// </para>
    /// </summary>
    public bool TurnsAreTyped => DocumentKinds is { Count: > 0 };
}

/// <summary>
/// Drives a headless coding-agent CLI over its structured protocol and streams its activity as
/// <see cref="AgentEvent"/>s. The process is long-lived: it stays up between turns (two turns served by
/// one process over stdin, seen against claude 2.1.221 on 2026-08-04 and driven again against the pinned
/// 2.1.222 on 2026-08-05 by <c>ClaudeCliContractTests.A_second_turn_is_served_by_the_same_process</c>), so
/// a session survives the window closing (decision 6) and <see cref="SendAsync"/> continues it.
/// </summary>
public interface IAgentSessionRunner
{
    /// <summary>
    /// Starts the session and streams its events until the process exits or the token is cancelled.
    /// The stream is single-consumer by design; the live registry is what fans it out to the UI.
    /// </summary>
    IAsyncEnumerable<AgentEvent> RunAsync(AgentSessionRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a follow-up user turn to a running session. False when no live process holds that id
    /// (it ended, or this process never started it).
    /// </summary>
    Task<bool> SendAsync(AgentSessionId sessionId, string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a follow-up user turn that carries pasted images (backlog 26): each becomes an image content
    /// block ahead of the text, the shape the pinned CLI's stream-json input was measured to accept. The text
    /// may be blank when the images are the whole turn. A runner that cannot carry images refuses rather than
    /// sending the words without the picture they were about.
    /// </summary>
    Task<bool> SendAsync(
        AgentSessionId sessionId,
        string text,
        IReadOnlyList<AgentImageAttachment> images,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This agent runner cannot send images.");

    /// <summary>Stops a running session, killing its process tree. False when nothing was running.</summary>
    bool Stop(AgentSessionId sessionId);

    /// <summary>
    /// Moves a running session onto another model (null returns to the CLI's own default). The mechanism
    /// is a deliberate child restart: the current CLI process ends and a new one starts with
    /// <c>--resume</c> and the new <c>--model</c>, which the pinned-lineage CLI honors with the full
    /// conversation history and the same provider session id (measured in the container against 2.1.223
    /// on 2026-08-08; the init line's model field only echoes the flag, the server-reported
    /// <c>message.model</c> is what proved the switch). The session's approval channel, hook files and
    /// transcript stay exactly where they were. False when no live process holds that id, or when the
    /// CLI has not yet announced the conversation a restart would resume.
    /// </summary>
    bool ChangeModel(AgentSessionId sessionId, string? model);

    /// <summary>
    /// Moves a running session onto another effort level (null returns to the CLI's own default; request
    /// 2026-10-22, model and effort per work item). The same deliberate child restart as <see cref="ChangeModel"/>:
    /// <c>--effort</c> is a process argument, so only a new child resumed with <c>--resume</c> can carry a new one.
    /// The restart keeps the model the session is on, as a model change keeps its effort. False when no live process
    /// holds that id, when the CLI has not yet announced the conversation a restart would resume, or when the
    /// runner has no effort to change.
    /// </summary>
    bool ChangeEffort(AgentSessionId sessionId, AgentEffort? effort) => false;

    /// <summary>
    /// Hands a running session the CURRENT credential (backlog 235): the same deliberate child restart as
    /// <see cref="ChangeModel"/>, on the model it is already on, so the new child's environment is built again from the
    /// credential store and carries the broker token minted since the old child started. Measured against the pinned
    /// 2.1.280 on 2026-10-07: a running child keeps the bearer it started with for every later call, so nothing short
    /// of a new process can change it. A turn written meanwhile waits for the new child. False when no live process
    /// holds that id, or when the CLI has not yet announced the conversation a restart would resume.
    /// </summary>
    bool RefreshCredentials(AgentSessionId sessionId) => false;
}
