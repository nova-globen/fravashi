using Fravashi.Domain.AgentSessions;

namespace Fravashi.Application.Sessions;

/// <summary>A tool call parked in front of the user, waiting for Approve or Deny.</summary>
/// <param name="RequestId">The studio's handle for this one pending call.</param>
/// <param name="ToolUseId">The CLI's id for the tool call, when it sent one.</param>
/// <param name="Detail">
/// The subject (a path, a command), RAW (backlog 103): this value becomes the persisted
/// <c>approval_requested</c> row, so it is the command the CLI was really handed, bounded only by the
/// storage cap. A UI renders it SHORTENED (middle-out, so a cut cannot turn one path into a
/// different, plausible one), and it is never the only path-shaped thing shown:
/// <paramref name="Target"/> carries the resolved destination beside it.
/// </param>
/// <param name="Input">
/// The tool's arguments, flattened to strings and capped FOR DISPLAY. These are what the prompt
/// renders, not what the CLI will run: a long one is cut short, so nothing here may be used to decide
/// whether a call is safe. Containment is judged on the raw arguments, before this copy exists.
/// </param>
/// <param name="Warning">
/// Why this call is not the ordinary thing it looks like: a read that leaves the session's contained
/// copy, a change that lands outside it, or a path the studio could not work out. Null when there is
/// nothing to say beyond "every change asks".
/// </param>
/// <param name="Target">
/// Where the call's path really lands, resolved (<see cref="AgentToolTarget"/>), or null when the tool
/// named no location. A UI rendering this request MUST show it whenever it is there: the capped
/// <paramref name="Detail"/> alone let a padded path read as an ordinary one inside the copy while the
/// CLI wrote outside it.
/// </param>
/// <param name="Effects">
/// What the call's arguments MEAN beyond where they point (<see cref="AgentToolEffects"/>), empty when
/// there is nothing to add. A UI MUST show these too, and an
/// <see cref="AgentEffectWeight.Alarm"/> one must not render like the ordinary case: an Agent call names
/// no path at all, so <paramref name="Target"/> is null for it and <c>isolation: "remote"</c>, which
/// sends the workspace copy to a cloud environment off this machine, appeared nowhere on the prompt.
/// </param>
/// <param name="CutArguments">
/// Every argument <paramref name="Input"/> shows only part of, with its RAW value taken from the input the
/// studio decided on (backlog 240), so the person can read the whole of what they approve. Empty or null
/// when nothing was cut. Like <paramref name="Input"/>, this is for the person only: nothing the studio
/// decides reads it, every decision was made on the raw input before this request existed.
/// </param>
/// <param name="Review">
/// What the commit reviewer role said about this commit before the person was asked (backlog 243): a pass with its
/// summary, "not reviewed" because the role is off, or why it gave no verdict. Only an agent commit carries one (a
/// <c>git_commit</c>, or since backlog 250 an <c>attempt_commit</c>), and only where a reviewer is wired; a FAIL never
/// reaches a request, because it is refused without asking.
/// </param>
public sealed record PendingToolApproval(
    string RequestId,
    AgentSessionId SessionId,
    string ToolName,
    string Detail,
    string? ToolUseId,
    IReadOnlyDictionary<string, string> Input,
    DateTimeOffset RequestedUtc,
    string? Warning = null,
    AgentToolTarget? Target = null,
    IReadOnlyList<AgentToolEffect>? Effects = null,
    IReadOnlyList<CutToolArgument>? CutArguments = null,
    CommitReview? Review = null);

/// <summary>
/// One argument the approval card shows only the head and tail of (backlog 240).
/// </summary>
/// <param name="Name">The argument's name, as the CLI sent it.</param>
/// <param name="Length">The raw value's length in characters.</param>
/// <param name="Hidden">How many of those characters the display copy leaves out.</param>
/// <param name="Full">
/// The raw value, exactly as the CLI sent it, or null when it is longer than the card will carry in full;
/// the card then says so loudly instead of offering a view it cannot give.
/// </param>
public sealed record CutToolArgument(string Name, int Length, int Hidden, string? Full);

/// <summary>
/// A tool call the studio refused ON ITS OWN ACCOUNT without ever parking it in front of the user
/// (backlog 117): a call for a session the broker no longer holds, or a call the endpoint could not read.
/// Nobody was asked, so before this nothing reached the transcript at all and an agent could push
/// against a refusal any number of times in silence.
/// </summary>
/// <param name="SessionId">The session the refused call belongs to.</param>
/// <param name="ToolName">The tool the CLI named, or empty when the call could not be read far enough to say.</param>
/// <param name="Detail">The call's subject (a path, a command), empty when there was none to read.</param>
/// <param name="Reason">The refusal sentence the CLI was sent, verbatim.</param>
/// <param name="RefusedUtc">When the studio refused it.</param>
public sealed record AutomaticToolRefusal(
    AgentSessionId SessionId,
    string ToolName,
    string Detail,
    string Reason,
    DateTimeOffset RefusedUtc);

/// <summary>A change the studio let run without asking, under the workspace's trust level (ADR 0028).</summary>
/// <param name="Detail">The call's subject (the path), as the prompt would have shown it.</param>
/// <param name="Reason">The sentence the CLI was sent, naming the level that let it run.</param>
public sealed record TrustedToolCall(
    AgentSessionId SessionId,
    string ToolName,
    string Detail,
    string? ToolUseId,
    string Reason,
    DateTimeOffset TrustedUtc);

/// <summary>
/// A call an autopilot session's own rule list or judge let run without asking (ADR 0031 decisions 7 and 8,
/// backlog 299), distinct from <see cref="TrustedToolCall"/> (a different mechanism, ADR 0028): this one
/// fires ONLY for a session the autopilot marked, and <see cref="Reason"/> names the rule or the judgement
/// that allowed it, never just "trusted".
/// </summary>
/// <param name="Detail">The call's subject, as the prompt would have shown it.</param>
/// <param name="Reason">The sentence naming the rule or the judgement that allowed it.</param>
public sealed record AutopilotAllowedCall(
    AgentSessionId SessionId,
    string ToolName,
    string Detail,
    string? ToolUseId,
    string Reason,
    DateTimeOffset AllowedUtc);

/// <summary>The answer sent back to the blocked hook, which becomes the CLI's permission decision.</summary>
/// <param name="DecidedBy">
/// Who made this decision (backlog 114). The CLI is told the same thing either way; this rides along so
/// the transcript can tell a refusal the USER made apart from one the STUDIO imposed (a timeout, a
/// released session, a shutdown), which it previously reported as the user's own denial. It is set where
/// the decision is MADE, and it defaults to <see cref="ToolApprovalDecidedBy.Studio"/> so nothing is
/// attributed to a user by omission.
/// </param>
public sealed record ToolApprovalDecision(
    bool Approved,
    string Reason,
    ToolApprovalDecidedBy DecidedBy = ToolApprovalDecidedBy.Studio)
{
    public static ToolApprovalDecision Allow(string reason) => new(true, reason);

    public static ToolApprovalDecision Deny(string reason) => new(false, reason);

    /// <summary>The user's own answer, given in the studio's approval prompt.</summary>
    public static ToolApprovalDecision FromUser(bool approved, string reason) =>
        new(approved, reason, ToolApprovalDecidedBy.User);

    /// <summary>What the agent is told when the user approves in the studio.</summary>
    public const string UserApprovedReason = "approved in the studio";

    /// <summary>What the agent is told when the user denies in the studio and gives no reason.</summary>
    public const string UserDeclinedReason = "the user declined this change in the studio";

    /// <summary>The most of a person's own reason for a denial that is passed on (backlog 220).</summary>
    public const int MaxUserReasonLength = 500;

    /// <summary>
    /// The sentence the agent reads as the call's result when the user answers in the studio (backlog 220). A
    /// denial can carry the person's own words, so the agent can correct and retry in the same turn instead of
    /// concluding the call was simply refused. With no words (or only whitespace) it is exactly the sentence it
    /// always was. The fixed sentence stays in front so a model reads a person's refusal, not a tool error. The
    /// words are passed on as text and nothing in the studio interprets them; they are capped here as well as on
    /// the card, because a circuit can send anything.
    /// </summary>
    public static string UserReason(bool approved, string? reason)
    {
        if (approved)
        {
            return UserApprovedReason;
        }

        var words = reason?.Trim();
        if (string.IsNullOrEmpty(words))
        {
            return UserDeclinedReason;
        }

        if (words.Length > MaxUserReasonLength)
        {
            // Never cut between the two halves of a surrogate pair, which would send a broken character.
            var cut = char.IsHighSurrogate(words[MaxUserReasonLength - 1])
                ? MaxUserReasonLength - 1
                : MaxUserReasonLength;
            words = words[..cut].TrimEnd();
        }

        return UserDeclinedReason + " and gave this reason: " + words;
    }
}

/// <summary>
/// The studio side of the CLI's PreToolUse hook (plan 2e Task 3). The hook POSTs a pending tool call
/// and BLOCKS; this broker parks it, surfaces it to the UI, and completes it when the user answers.
/// Read-only tools (<see cref="AgentToolSafety"/>) are answered immediately without asking, but ONLY
/// while they stay inside the session's contained copy: a read that leaves it asks like anything else.
/// <para>
/// Exactly one other call is answered without asking, and it is not a read:
/// <see cref="AgentTurnOutcomeTool"/>, the terminating document the CLI builds out of the studio's own
/// schema (ADR 0022). It is kept out of the read-only list on purpose, and it only passes for a session
/// that was registered as asking for documents.
/// </para>
/// <para>
/// Every session gets its own secret, which is the ONLY authentication on the approval endpoint
/// (<c>/api/*</c> is exempt from the studio's gates by design). An unknown secret resolves to no
/// session, and an unanswered request is DENIED once its timeout expires: this fails closed.
/// </para>
/// </summary>
public interface IAgentApprovalBroker
{
    /// <summary>
    /// Registers a session and returns its freshly generated secret. Never logged.
    /// </summary>
    /// <param name="workspaceRoot">
    /// The contained copy the session's agent is confined to (its child's working directory). It is what
    /// scopes the read-only auto-allow, so a session registered WITHOUT one auto-allows no filesystem
    /// read at all: every one of them asks.
    /// </param>
    /// <param name="typedTurns">
    /// True when this session was started with <c>--json-schema</c>, so the CLI built a
    /// <see cref="AgentTurnOutcomeTool"/> call for the model to end each turn with (ADR 0022). That call
    /// is the studio's own turn terminator, not work the agent chose, so it is answered without asking.
    /// <para>
    /// It defaults to FALSE, and that default is the fail-closed one: a session that never asked for a
    /// document has no explanation for a call by that name, so it parks in front of the user like anything
    /// else. Re-registering without it REVOKES it, the same way <paramref name="workspaceRoot"/> is
    /// revoked, because what a session is allowed must always be what its latest registration said.
    /// </para>
    /// </param>
    /// <param name="workspaceId">
    /// The workspace the session runs in (ADR 0028, backlog 152): the folder whose trust level decides whether an
    /// edit inside the copy may run without asking. Null means no level applies and every change asks. Like the
    /// root, it is what the latest registration said and nothing else.
    /// </param>
    /// <param name="memoryRoot">
    /// Backlog 202: this session's own slice of its workspace's agent memory (backlog 155), named the same way
    /// <c>SessionFrameworkProjector.MemoryLinkPathFor</c> builds the link, when the session has one. A read that
    /// stays inside it runs unattended exactly as a read inside <paramref name="workspaceRoot"/> does; null means
    /// the session was never linked to a shared memory, and every memory read keeps asking. Like the root, it is
    /// revoked by a re-registration that no longer names one. Taken together with
    /// <paramref name="memoryTarget"/>: a root named with no target (or the reverse) is discarded rather than
    /// kept half-pinned.
    /// </param>
    /// <param name="memoryTarget">
    /// Where <paramref name="memoryRoot"/> is SUPPOSED to point right now (<c>SessionFrameworkProjector
    /// .MemoryDirectoryFor</c>), named independently of the link itself. A link is a live thing: re-resolving it
    /// on every call is what lets a read through it be judged at all, but on its own it would make the root
    /// "inside itself" by construction, since a re-pointed link and a read through it are resolved through the
    /// SAME live link at the SAME moment. This is the independent name that catches a link that no longer points
    /// where the studio put it; a mismatch drops the memory root and every memory read asks.
    /// </param>
    /// <param name="toolResultsRoot">
    /// Backlog 338: the current session's own slice of the CLI's saved-tool-output folder
    /// (<c>SessionFrameworkProjector.ToolResultsDirectoryFor</c>), when the studio already knows the CLI's own
    /// conversation id (a RESUMED session always does). A read that stays inside it runs unattended exactly as a
    /// read inside <paramref name="workspaceRoot"/> does, PROVIDED the session config home below it still holds
    /// no symlink (<c>AgentReadScope.ContainmentRoots</c> checks this live, on every call, because that folder
    /// is writable by the agent's own uid); null leaves every tool-results read asking, the same fail-closed
    /// default every other root here has. A FRESH session does not know this id yet (the CLI announces it only
    /// after the process starts), so it is set afterwards through
    /// <see cref="IAgentApprovalBroker.SetToolResultsRoot"/> rather than through this method a second time,
    /// which would rotate the secret this call is about to hand out.
    /// </param>
    string RegisterSession(
        AgentSessionId sessionId,
        string? workspaceRoot = null,
        bool typedTurns = false,
        string? workspaceId = null,
        string? memoryRoot = null,
        string? memoryTarget = null,
        string? toolResultsRoot = null);

    /// <summary>Forgets a session's secret and denies anything still pending for it.</summary>
    void ReleaseSession(AgentSessionId sessionId);

    /// <summary>
    /// Sets (or clears) a registered session's tool-results containment root without touching its secret or
    /// anything else about it (backlog 338): the one update <see cref="RegisterSession"/> cannot make, because a
    /// fresh session's provider id is unknown until after registration and calling it again would rotate the
    /// secret the running hook script already authenticates with.
    /// </summary>
    void SetToolResultsRoot(AgentSessionId sessionId, string? toolResultsRoot);

    /// <summary>
    /// Holds a session to READING (backlog 243): the commit reviewer's conversation. Called before the session starts
    /// and kept until it is released. For such a session the only calls allowed are the review's own reads (Read, Grep
    /// and Glob inside the contained copy, ToolSearch, and the studio's <c>git_diff</c>, <c>git_show</c>,
    /// <c>work_item_read</c> and <c>wiki_read</c>) and the turn-ending document; everything else is refused WITHOUT
    /// asking anyone, so a reviewer can never change the copy, run a command or write to the graph, and never puts a
    /// card in front of a person.
    /// </summary>
    void HoldToReview(AgentSessionId sessionId);

    /// <summary>
    /// Holds a session GRAPH-ONLY, "Notes and items only" (work item #352). Kept until the session is released; a
    /// re-registration does not lift it and there is no call that does. For such a session every call that is not on
    /// <c>AgentGraphOnlyTools.Allows</c> is refused up front, in words, WITHOUT asking anyone: an edit, a write, a
    /// command, a git tool, the browser, an upgrade and any tool the list does not name. What it does allow is then
    /// answered exactly as for any other session (a read outside the copy asks; a graph change that asks still asks).
    /// </summary>
    void HoldToGraphOnly(AgentSessionId sessionId);

    /// <summary>True for a session held graph-only (<see cref="HoldToGraphOnly"/>) and not yet released.</summary>
    bool IsGraphOnly(AgentSessionId sessionId);

    /// <summary>
    /// Holds a session as the STUDIO ASSISTANT (work item #359). Same lifetime as <see cref="HoldToGraphOnly"/>: kept
    /// until the session is released, never lifted by a re-registration. For such a session every call that is not on
    /// <c>AgentAssistantTools.Allows</c> is refused up front, in words, without asking anyone, and the assistant's own
    /// two tools (<c>AgentAssistantTools.AlwaysAsk</c>) always go to a person. Any session NOT held this way is refused
    /// those two tools up front.
    /// </summary>
    void HoldToAssistant(AgentSessionId sessionId);

    /// <summary>True for a session held as the studio assistant (<see cref="HoldToAssistant"/>) and not yet released.</summary>
    bool IsAssistant(AgentSessionId sessionId);

    /// <summary>
    /// Marks (or unmarks) a session as one the autopilot runs (ADR 0031 decisions 7 to 9, backlog 299 and
    /// 309): the rule list and the judge apply to it, and nowhere else. The engine calls it once, when it
    /// starts a building session; a re-registration does not clear it, unlike the per-session facts
    /// <see cref="RegisterSession"/> revokes, because a studio restart stops any run whose current item was
    /// mid-Building before a resumed session could ever reach this again.
    /// </summary>
    void SetAutopilot(AgentSessionId sessionId, bool autopilot);

    /// <summary>True for a session the autopilot has marked (<see cref="SetAutopilot"/>).</summary>
    bool IsAutopilot(AgentSessionId sessionId);

    /// <summary>The session a secret belongs to, or null when it belongs to none.</summary>
    AgentSessionId? ResolveSecret(string? secret);

    /// <summary>
    /// Parks a tool call until the user answers, the timeout expires (deny), or the session is
    /// released (deny). A read-only tool returns an immediate allow when its target is provably inside
    /// the session's contained copy; anything else parks.
    /// </summary>
    /// <param name="input">
    /// The tool's arguments EXACTLY as the CLI sent them, INCLUDING whether each one arrived as a JSON
    /// string: containment is decided here, and the CLI will open the path it sent, so a caller must
    /// never shorten, rewrite or retype a value on the way in. The display-capped copy is built inside,
    /// on the parked request.
    /// </param>
    Task<ToolApprovalDecision> RequestAsync(
        AgentSessionId sessionId,
        string toolName,
        string? toolUseId,
        AgentToolInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Answers a pending request AS THE USER. False when the id is unknown or already answered.
    /// <para>
    /// This is the only path a decision may be attributed to the user by (backlog 114): everything the
    /// studio decides on its own account, including every refusal it imposes when nobody answered,
    /// resolves inside the broker and stays attributed to the studio.
    /// </para>
    /// </summary>
    bool Answer(string requestId, bool approved, string reason);

    /// <summary>Everything currently waiting on the user for a session, oldest first.</summary>
    IReadOnlyList<PendingToolApproval> PendingFor(AgentSessionId sessionId);

    /// <summary>Raised when a call parks; the live registry turns it into a transcript event.</summary>
    event Action<PendingToolApproval>? Requested;

    /// <summary>Raised when a parked call is answered, however it was answered.</summary>
    event Action<PendingToolApproval, ToolApprovalDecision>? Resolved;

    /// <summary>
    /// Raised for every refusal the studio imposed on a call it never parked (backlog 117), so no
    /// enforcement action is invisible: the live registry turns it into a transcript event. A refusal of a
    /// PARKED call (a timeout, a released session, a shutdown) arrives through <see cref="Resolved"/>
    /// instead, attributed to the studio, and is never raised here as well.
    /// </summary>
    event Action<AutomaticToolRefusal>? Refused;

    /// <summary>
    /// Raised for every change the studio let run WITHOUT asking under a workspace's trust level (ADR 0028), so
    /// an unattended change is as visible in the transcript as a refused one. A read allowed by the read-only
    /// policy is not raised here: it changes nothing and has always run unattended.
    /// </summary>
    event Action<TrustedToolCall>? Trusted;

    /// <summary>
    /// Raised for every call an autopilot session's rule list or judge let run without asking (ADR 0031
    /// decisions 7 and 8, backlog 299), so each one is recorded with the rule or the judgement that allowed
    /// it, exactly as visible in the transcript as a refusal or a trusted edit. Never raised for a session
    /// the autopilot did not mark.
    /// </summary>
    event Action<AutopilotAllowedCall>? AutopilotAllowed;

    /// <summary>
    /// Raised when a session's registration is released, which is when its agent process has ended (backlog 159): what
    /// the session held for its process, such as its browser page, is let go here. A resume registers again.
    /// </summary>
    event Action<AgentSessionId>? Released;

    /// <summary>
    /// Raised for every commit review (backlog 243), with the BUILDING session and what the review found, before the
    /// call is refused or parked: the live registry writes it on that conversation, so each review's verdict, duration
    /// and tokens are recorded where its findings can be counted (backlog 244).
    /// </summary>
    event Action<AgentSessionId, CommitReview>? Reviewed;

    /// <summary>
    /// Refuses a call for a known session without asking anybody, and says so through
    /// <see cref="Refused"/> (backlog 117). Used where the refusal is decided OUTSIDE the broker's own
    /// policy, such as a hook payload the endpoint could not read: the session is known, so the refusal
    /// belongs in its transcript.
    /// </summary>
    ToolApprovalDecision Refuse(AgentSessionId sessionId, string toolName, string detail, string reason);
}
