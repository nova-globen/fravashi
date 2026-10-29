using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Fravashi.Application.Persistence;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>
/// What an autopilot session is doing, for the rule and the judge that need graph context (backlog 299):
/// the title of the bound item(s), joined for the judge's state, and each bound item's board NUMBER mapped
/// to its CURRENT status (the tool's own lower-snake names), for the forward-status-move rule's own-item,
/// one-genuine-step check. <see cref="Empty"/> is the fail-closed answer a failed or unconfigured lookup
/// returns: it matches no item number and gives the judge no title.
/// </summary>
/// <param name="ItemTitle">The bound item(s)' title(s), joined; empty when there are none or none were found.</param>
/// <param name="ItemStatuses">Each bound item's board number mapped to its current status; empty when none were found.</param>
public sealed record AutopilotSessionContext(string ItemTitle, IReadOnlyDictionary<int, string> ItemStatuses)
{
    public static readonly AutopilotSessionContext Empty = new(string.Empty, new Dictionary<int, string>());
}

/// <summary>
/// In-memory <see cref="IAgentApprovalBroker"/> (plan 2e Task 3). The CLI's PreToolUse hook is BLOCKED
/// on an HTTP call while a request sits here, so every path ends in a decision and none of them
/// defaults to allow: an unregistered session, a cancelled call, a released session and an expired
/// timeout all deny.
/// <para>
/// The wait is deliberately long (<see cref="DefaultTimeout"/>): decision 6 says a session
/// effectively PAUSES at its first pending change while the user is away, and the queued request must
/// still be answerable when the window comes back. The CLI-side hook timeout is set above this one
/// (see <see cref="SessionHookWriter"/>) so the studio, not the CLI, is what times a request out.
/// </para>
/// <para>
/// The read-only auto-allow is PATH-SCOPED (<see cref="AgentReadScope"/>): a read is unattended only
/// while it stays inside the copy the session was registered with. A session registered without one
/// auto-allows no filesystem read at all, which is the same fail-closed default as everything else here.
/// </para>
/// <para>
/// One further call is answered without asking and it is not a read at all:
/// <see cref="AgentTurnOutcomeTool"/>, the terminating document of a typed turn (ADR 0022). It is scoped
/// the same way the reads are, by the registration rather than by the name alone: only a session that was
/// registered as asking for documents gets it, so the silence can never outlive the reason for it.
/// </para>
/// </summary>
public sealed class AgentApprovalBroker : IAgentApprovalBroker, IDisposable
{
    /// <summary>How long a change may wait for the user before it is denied.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromHours(4);

    private readonly TimeSpan _timeout;
    private readonly Func<DateTimeOffset> _clock;

    // Backlog 44, gate G1: what each session is working on. Null when no store is wired, which is how the
    // tests and a studio built without a board run: the gate then has nothing to enforce and stays out of
    // the way, rather than denying every change in a product whose tracker is not there.
    private readonly ISessionWorkItems? _work;

    // Backlog 45, gate G2: the plan each session is working under. Null when no store is wired, and then
    // G2 has nothing to enforce, exactly as G1 has nothing to enforce without its own store.
    private readonly ISessionPlans? _plans;

    // Backlog 85: the readiness facts, when the studio can assemble them. With this present the gate asks
    // whether the bindings are WORKABLE rather than merely present, and it also learns about the change
    // afterwards so a budget can mean something. Without it the two bindings are checked on their own.
    private readonly ISessionGovernance? _governance;

    // Backlog 88: the role each session runs under. Null when nothing assigns roles, and then every
    // session is the unrestricted interactive one, which is what they all were before roles existed.
    private readonly ISessionRoles? _roles;

    // secret -> session. The secret is a 256-bit random value and the ONLY authentication on the
    // approval endpoint, so it is never logged and never written anywhere except the session's own
    // hook script (which lives in the session folder and is deleted with it).
    private readonly ConcurrentDictionary<string, AgentSessionId> _secrets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<AgentSessionId, string> _sessionSecrets = new();
    private readonly ConcurrentDictionary<string, Waiter> _pending = new(StringComparer.Ordinal);

    // session -> the contained copy its agent is confined to. Absent means "not known", which makes
    // every filesystem read ask (AgentReadScope), never the other way round.
    private readonly ConcurrentDictionary<AgentSessionId, string> _roots = new();

    // Backlog 202: session -> this session's own slice of its workspace's agent memory, when it has one.
    // Absent means "not known or never linked", which is exactly what leaves every memory read asking,
    // the same fail-closed default the root above has.
    private readonly ConcurrentDictionary<AgentSessionId, string> _memoryRoots = new();

    // Backlog 202: session -> the REAL folder the memory root is supposed to resolve to right now
    // (SessionFrameworkProjector.MemoryDirectoryFor), named independently of the link so AgentReadScope
    // can catch a link that was re-pointed after registration rather than trusting whatever it answers
    // about itself. Always written and revoked together with the root above.
    private readonly ConcurrentDictionary<AgentSessionId, string> _memoryTargets = new();

    // Backlog 338: session -> the current session's own slice of the CLI's saved-tool-output folder
    // (SessionFrameworkProjector.ToolResultsDirectoryFor), when the studio could compute one. Absent means
    // "not known yet" (a fresh session whose provider id the CLI has not announced) or "never computable"
    // (no framework projection), which is the same fail-closed default every other root here has: every
    // tool-results read keeps asking until this is set. Written by RegisterSession for a resumed session
    // (its provider id is already known) and by SetToolResultsRoot for a fresh one, once the CLI's first
    // stream line names it; never re-derived from an agent's own text.
    private readonly ConcurrentDictionary<AgentSessionId, string> _toolResultsRoots = new();

    // session -> the workspace it runs in (ADR 0028), the folder whose trust level applies to it. Absent means
    // no level applies, which is the level that asks. Written and revoked with the root.
    private readonly ConcurrentDictionary<AgentSessionId, string> _workspaces = new();
    private readonly IWorkspaceTrust? _trust;

    // Backlog 312: the studio's own record of every registered workspace's RootPath, used ONLY to tell a
    // worktree's own workspace id apart from its repository's and to find the repository's record. Null
    // behaves exactly as this did before worktrees existed: trust is read for the session's own workspace id.
    private readonly IWorkspaceStore? _workspaceStore;

    // Backlog 288: where the host's git runs hooks from, asked before an edit runs trusted. Null asks nothing, which is
    // ADR 0028's rule as it was before.
    private readonly IWorkspaceHooksFolder? _hooks;

    // Backlog 289: the work-tree files the host's git config includes, asked at the same point as the hooks folder
    // above. Null asks nothing, which is the rule as #288 left it.
    private readonly IWorkspaceConfigIncludes? _configIncludes;

    // The sessions started with --json-schema, so the CLI has a StructuredOutput tool to end a turn with
    // (ADR 0022). Absent means the studio never asked this session for a document, so a call by that name
    // is unexplained and asks. Membership always implies a live registration: it is written by
    // RegisterSession and removed by ReleaseSession alongside the secret.
    private readonly ConcurrentDictionary<AgentSessionId, bool> _typedTurns = new();

    // Backlog 108: the credentials the studio itself puts in an agent child's environment, asked for at each
    // call because a key can be saved or rotated while a session runs. Null when nothing supplies them, and
    // then there is nothing known to look for. A source that THROWS is treated as holding nothing it can
    // name, which is the one place here that cannot fail closed without refusing every call in the product;
    // the sync engine's own check on what reaches the host does not depend on this one.
    private readonly Func<IReadOnlyCollection<AgentHeldCredential>>? _heldCredentials;

    // Backlog 243: the commit reviewer, asked for every agent commit (git_commit, and attempt_commit since backlog 250)
    // that would otherwise reach a person. A function,
    // resolved at the call, because the reviewer runs conversations and so depends on the runner, which depends on this
    // broker. Null (or answering null) is a studio with no reviewer, where a commit asks exactly as it always did.
    private readonly Func<ICommitReviewer?>? _reviewer;

    // Backlog 243: the sessions held to reading, the commit reviewer's conversations. Written before such a session
    // starts and removed when it is released, never by a registration, so a re-registration cannot lift it.
    private readonly ConcurrentDictionary<AgentSessionId, bool> _reviews = new();

    // Work item #352: the sessions held graph-only ("Notes and items only"). Same lifetime rule as _reviews: written
    // before such a session's child can call anything, removed only when it is released, never by a registration.
    private readonly ConcurrentDictionary<AgentSessionId, bool> _graphOnly = new();

    // Work item #359: the sessions held as the studio assistant. Same lifetime rule as _graphOnly.
    private readonly ConcurrentDictionary<AgentSessionId, bool> _assistant = new();

    // Work item #341: each building session's latest FAILED commit review, the only thing a dispute (a commit
    // call's own `answers` argument) is checked against, plus which of its finding numbers have already been
    // answered. Set the moment a review fails, replaced by its successor review (any outcome) or cleared on
    // anything else, so an answer can never name a stale review or a different session's.
    private readonly ConcurrentDictionary<AgentSessionId, FailedReviewState> _latestFailedReviews = new();

    // Work item #341's reviewer finding: a finding the reviewer KEEPS must stay settled "for good", not only
    // through the latest-failed-review's own lifetime above, which a Pass, a TimedOut, an Errored or an Off
    // round all replace or clear. This is deliberately a SEPARATE, longer-lived dictionary for exactly that
    // reason: cleared only in ReleaseSession, never by a non-Failed outcome.
    private readonly ConcurrentDictionary<AgentSessionId, SettledFindings> _settledFindings = new();

    // Backlog 244: the operator's attention, one row per decision made here. Null when nothing counts, which is how the
    // tests that predate it and a studio built without the store run; counting never changes a decision either way.
    private readonly ISessionAttention? _attention;

    // Backlog 299 (ADR 0031 decisions 7 to 9): the sessions the autopilot runs, and nowhere else. The engine marks a
    // building session with SetAutopilot once it starts it; a session absent here behaves exactly as today, which is
    // the fail-closed default every other per-session fact in this file already has.
    private readonly ConcurrentDictionary<AgentSessionId, bool> _autopilot = new();

    // Backlog 299: the judge for an autopilot session's Bash calls the rule list does not settle. Resolved at the
    // call, like the reviewer above, because it can depend on a stored key that changes while a session runs. Null
    // (or IsOn false) means no judge is configured, which asks, never runs unattended.
    private readonly Func<AutopilotCommandJudge?>? _judge;

    // Backlog 299: the title of the item(s) an autopilot session is doing (the judge's state) and the board
    // NUMBERS it is doing them under (the forward-status-move rule's one check: THIS item, not any item). Null
    // means nothing can answer it, so the judge gets an empty title and the status-move rule matches no number,
    // both of which are the fail-closed direction. A throw from this is caught around every call to it, never
    // left to propagate out of RequestAsync: a graph lookup failing must turn into "ask", not an unanswered call.
    private readonly Func<AgentSessionId, CancellationToken, Task<AutopilotSessionContext>>? _autopilotContext;

    // Backlog 299: the session's own recorded attempt branch (ISessionBranches, synchronous: it is an in-memory/
    // SQLite fact, not a git call) and the workspace's actual current branch (a real git call, so it is async).
    // Both are read only for a tagless git_push on an autopilot session, and only when they are non-null and
    // equal does the push rule allow it: the push rule's whole point is never trusting an assumption about which
    // branch is checked out when it can ask instead.
    private readonly Func<AgentSessionId, string?>? _autopilotAttemptBranch;
    private readonly Func<AgentSessionId, CancellationToken, Task<string?>>? _autopilotCurrentBranch;

    // Backlog 322: the run's own base branch for a not-yet-started item (ISessionAttemptBases), read only for
    // attempt_branch and only alongside the two facts above, so the start rule can tell a worktree item (already
    // branched) apart from a cap-1 item (branches from this instead of the folder's current checkout).
    private readonly Func<AgentSessionId, string?>? _autopilotKnownBase;

    // Follow-up filed 2026-10-22 (approval card with no arguments): a studio tool's BARE name to its declared input
    // schema (IStudioTool.InputSchemaJson), or null when there is no such tool. Resolved at the call, because the tools
    // are built by the same container that builds this broker. Null (or answering null, or throwing) checks nothing,
    // which is how the broker decided every call before the check existed.
    private readonly Func<string, string?>? _studioToolSchema;

    public AgentApprovalBroker(
        TimeSpan? timeout = null,
        Func<DateTimeOffset>? clock = null,
        ISessionWorkItems? work = null,
        ISessionPlans? plans = null,
        ISessionGovernance? governance = null,
        ISessionRoles? roles = null,
        Func<IReadOnlyCollection<AgentHeldCredential>>? heldCredentials = null,
        IWorkspaceTrust? trust = null,
        IWorkspaceStore? workspaceStore = null,
        Func<ICommitReviewer?>? reviewer = null,
        ISessionAttention? attention = null,
        IWorkspaceHooksFolder? hooks = null,
        IWorkspaceConfigIncludes? configIncludes = null,
        Func<AutopilotCommandJudge?>? judge = null,
        Func<AgentSessionId, CancellationToken, Task<AutopilotSessionContext>>? autopilotContext = null,
        Func<AgentSessionId, string?>? autopilotAttemptBranch = null,
        Func<AgentSessionId, CancellationToken, Task<string?>>? autopilotCurrentBranch = null,
        Func<AgentSessionId, string?>? autopilotKnownBase = null,
        Func<string, string?>? studioToolSchema = null)
    {
        _studioToolSchema = studioToolSchema;
        _hooks = hooks;
        _configIncludes = configIncludes;
        _judge = judge;
        _autopilotContext = autopilotContext;
        _autopilotAttemptBranch = autopilotAttemptBranch;
        _autopilotCurrentBranch = autopilotCurrentBranch;
        _autopilotKnownBase = autopilotKnownBase;
        _attention = attention;
        _reviewer = reviewer;
        _heldCredentials = heldCredentials;
        _trust = trust;
        _workspaceStore = workspaceStore;
        _timeout = timeout ?? DefaultTimeout;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _work = work;
        _plans = plans;
        _governance = governance;
        _roles = roles;
    }

    /// <summary>
    /// The declared input schema of the studio tool a CLI tool name names (<c>mcp__fravashi__{name}</c>), or null when
    /// the name is not a studio tool, nothing supplies schemas, or the source throws: every one of those leaves the call
    /// decided exactly as it was before the schema check existed.
    /// </summary>
    private string? StudioToolSchemaFor(string? toolName)
    {
        if (_studioToolSchema is null
            || string.IsNullOrEmpty(toolName)
            || !toolName.StartsWith(AgentWorkGate.StudioToolPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return _studioToolSchema(toolName[AgentWorkGate.StudioToolPrefix.Length..]);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private IReadOnlyCollection<AgentHeldCredential> HeldCredentials()
    {
        try
        {
            return _heldCredentials?.Invoke() ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    public event Action<PendingToolApproval>? Requested;

    public event Action<PendingToolApproval, ToolApprovalDecision>? Resolved;

    public event Action<AutomaticToolRefusal>? Refused;

    public event Action<TrustedToolCall>? Trusted;

    public event Action<AutopilotAllowedCall>? AutopilotAllowed;

    public event Action<AgentSessionId>? Released;

    public event Action<AgentSessionId, CommitReview>? Reviewed;

    public string RegisterSession(
        AgentSessionId sessionId,
        string? workspaceRoot = null,
        bool typedTurns = false,
        string? workspaceId = null,
        string? memoryRoot = null,
        string? memoryTarget = null,
        string? toolResultsRoot = null)
    {
        // The workspace follows the same rule as the root: the latest registration decides, and a registration
        // that names none revokes the previous one, so no trust level outlives the session that earned it.
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            _workspaces.TryRemove(sessionId, out _);
        }
        else
        {
            _workspaces[sessionId] = workspaceId;
        }

        // A re-registered session (a restart, a rewritten hook) invalidates its previous secret.
        if (_sessionSecrets.TryRemove(sessionId, out var previous))
        {
            _secrets.TryRemove(previous, out _);
        }

        // Re-registering with no root REVOKES the old one rather than keeping it: the scope a read is
        // measured against must always be the one this registration named.
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            _roots.TryRemove(sessionId, out _);
        }
        else
        {
            _roots[sessionId] = workspaceRoot;
        }

        // Same rule for the session's own memory (backlog 202): the latest registration decides, and
        // one that names none revokes the previous one rather than leaving a stale second root in
        // place. The target is kept or dropped WITH the root, never on its own: a root with no pinned
        // target is exactly the shape AgentReadScope treats as "do not trust this link".
        if (string.IsNullOrWhiteSpace(memoryRoot) || string.IsNullOrWhiteSpace(memoryTarget))
        {
            _memoryRoots.TryRemove(sessionId, out _);
            _memoryTargets.TryRemove(sessionId, out _);
        }
        else
        {
            _memoryRoots[sessionId] = memoryRoot;
            _memoryTargets[sessionId] = memoryTarget;
        }

        // Backlog 338: same rule for the session's own saved-tool-output folder. A RESUMED session already
        // knows its provider id here; a fresh one does not yet, and sets this later through
        // SetToolResultsRoot (never through this method again, because that would rotate the secret this
        // call is about to hand out from under the hook already running with the old one).
        if (string.IsNullOrWhiteSpace(toolResultsRoot))
        {
            _toolResultsRoots.TryRemove(sessionId, out _);
        }
        else
        {
            _toolResultsRoots[sessionId] = toolResultsRoot;
        }

        // Same rule, same reason: a re-registration that no longer asks for documents takes the silence
        // away with it. Whatever a session is allowed is whatever its LATEST registration said.
        if (typedTurns)
        {
            _typedTurns[sessionId] = true;
        }
        else
        {
            _typedTurns.TryRemove(sessionId, out _);
        }

        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        _secrets[secret] = sessionId;
        _sessionSecrets[sessionId] = secret;
        return secret;
    }

    public void ReleaseSession(AgentSessionId sessionId)
    {
        if (_sessionSecrets.TryRemove(sessionId, out var secret))
        {
            _secrets.TryRemove(secret, out _);
        }

        _roots.TryRemove(sessionId, out _);
        _memoryRoots.TryRemove(sessionId, out _);
        _memoryTargets.TryRemove(sessionId, out _);
        _toolResultsRoots.TryRemove(sessionId, out _);
        _workspaces.TryRemove(sessionId, out _);
        _typedTurns.TryRemove(sessionId, out _);
        _reviews.TryRemove(sessionId, out _);
        _graphOnly.TryRemove(sessionId, out _);
        _assistant.TryRemove(sessionId, out _);
        _autopilot.TryRemove(sessionId, out _);
        _latestFailedReviews.TryRemove(sessionId, out _);
        _settledFindings.TryRemove(sessionId, out _);

        foreach (var waiter in _pending.Values.Where(w => w.Request.SessionId.Equals(sessionId)).ToList())
        {
            Complete(waiter.Request.RequestId, ToolApprovalDecision.Deny("The session ended before this call was answered."));
        }

        Released?.Invoke(sessionId);
    }

    public void HoldToReview(AgentSessionId sessionId) => _reviews[sessionId] = true;

    /// <inheritdoc />
    public void HoldToGraphOnly(AgentSessionId sessionId) => _graphOnly[sessionId] = true;

    /// <inheritdoc />
    public bool IsGraphOnly(AgentSessionId sessionId) => _graphOnly.ContainsKey(sessionId);

    /// <inheritdoc />
    public void HoldToAssistant(AgentSessionId sessionId) => _assistant[sessionId] = true;

    /// <inheritdoc />
    public bool IsAssistant(AgentSessionId sessionId) => _assistant.ContainsKey(sessionId);

    /// <summary>
    /// Sets (or clears) the current session's own tool-results containment root after registration (backlog
    /// 338), for the one case <see cref="RegisterSession"/> cannot cover: a fresh session's provider id, which
    /// decides where the CLI saves a long tool result, is not known until the CLI's own first stream line
    /// announces it, by which point <see cref="RegisterSession"/> has already handed out the secret the
    /// running hook script authenticates with. Calling <see cref="RegisterSession"/> again at that point would
    /// rotate that secret out from under it; this touches only the one dictionary entry, the same way
    /// <see cref="SetAutopilot"/> and <see cref="HoldToReview"/> set a side fact without re-registering. A
    /// session never told one keeps asking about its own saved output, which is the same fail-closed default
    /// every other root here has.
    /// </summary>
    public void SetToolResultsRoot(AgentSessionId sessionId, string? toolResultsRoot)
    {
        if (string.IsNullOrWhiteSpace(toolResultsRoot))
        {
            _toolResultsRoots.TryRemove(sessionId, out _);
        }
        else
        {
            _toolResultsRoots[sessionId] = toolResultsRoot;
        }
    }

    /// <summary>
    /// Marks (or unmarks) a session as one the autopilot runs (ADR 0031 decision 7 to 9, backlog 299). The engine
    /// calls this when it starts a building session; a session never marked behaves exactly as it did before this
    /// existed, which is why every check this flag gates falls through to the ordinary answer when it is absent.
    /// </summary>
    public void SetAutopilot(AgentSessionId sessionId, bool autopilot)
    {
        if (autopilot)
        {
            _autopilot[sessionId] = true;
        }
        else
        {
            _autopilot.TryRemove(sessionId, out _);
        }
    }

    /// <summary>Whether this session is one the autopilot runs (backlog 299).</summary>
    public bool IsAutopilot(AgentSessionId sessionId) => _autopilot.ContainsKey(sessionId);

    /// <summary>
    /// The contained copy a session's agent is confined to, or null when the studio does not know one.
    /// Backlog 55 reads it: a scenario runs THERE or nowhere, and "nowhere" is a real answer rather than a
    /// reason to fall back to whatever directory the studio happens to be sitting in.
    /// </summary>
    public string? RootFor(AgentSessionId sessionId) =>
        _roots.TryGetValue(sessionId, out var root) ? root : null;

    public AgentSessionId? ResolveSecret(string? secret) =>
        !string.IsNullOrWhiteSpace(secret) && _secrets.TryGetValue(secret, out var session) ? session : null;

    public async Task<ToolApprovalDecision> RequestAsync(
        AgentSessionId sessionId,
        string toolName,
        string? toolUseId,
        AgentToolInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Decision 5: reads run unattended, everything that can change something asks, every time. Since
        // ADR 0028 (2026-09-26) there is one bounded exception, further down: a workspace whose person chose to
        // trust edits lets an EDIT that provably lands inside the copy run, after every gate below has passed.
        // "Unattended" is bounded by WHERE the read lands (plan 2d decision 2): the agent runs in the
        // studio's own container as the studio's own uid, so an unscoped read reaches /proc/1/environ,
        // /data and the key ring. Only a read that provably stays inside the session's contained copy
        // is allowed here; everything else falls through and asks, with the reason on the prompt.
        //
        // `input` is the RAW argument the CLI sent, and it has to stay raw all the way to this check:
        // the CLI acts on what it sent, so anything the studio shortens or rewrites first is a
        // different path than the one that will be opened. It also carries each value's JSON kind, so
        // a path-bearing field that was never a string is refused rather than read as a directory
        // name. The UI's capped copy is built below.
        _roots.TryGetValue(sessionId, out var root);
        _memoryRoots.TryGetValue(sessionId, out var memoryRoot);
        _memoryTargets.TryGetValue(sessionId, out var memoryTarget);
        _toolResultsRoots.TryGetValue(sessionId, out var toolResultsRoot);

        // Before any of that: the one call that is not a tool call. Under --json-schema the CLI builds a
        // StructuredOutput tool out of the schema THIS studio sent, and the model calls it to END its turn
        // (ADR 0022). The hook's matcher is "*", so it arrives here like everything else, and asking about
        // it would stop every single turn on a button: measured in the container on 2026-08-07 against
        // claude 2.1.223, an ordinary typed turn fires this hook once, and a turn whose document keeps
        // failing validation fires it five times before the turn ends.
        //
        // Allowing it widens nothing. It opens no file, starts no process and names no location; its one
        // argument is the document the studio asked for, which the CLI has already validated against that
        // same schema, and which travels no further than the studio's own transcript. That is also why it
        // is NOT in AgentToolSafety: that list is about WHERE a read lands, and this reads nothing.
        //
        // The condition is the fail-closed half. A session that never asked for a document has nothing that
        // explains a call by this name, so it falls through and asks; so does any name that is merely close
        // to it (AgentTurnOutcomeTool matches ordinally and exactly).
        //
        // Backlog 244: and it is the one decision here that is NOT counted. It is how every typed turn ends, not a call
        // an agent chose, so counting it would add one "auto" per turn and say nothing about anyone's attention.
        if (AgentTurnOutcomeTool.Is(toolName) && _typedTurns.ContainsKey(sessionId))
        {
            return ToolApprovalDecision.Allow(
                "This is how the turn ends: the studio asked for a document, the CLI checked it against "
                + "the studio's schema, and the call itself changes nothing.");
        }

        // Backlog 108: a call whose arguments CARRY a credential the agent is holding is refused, reads
        // included and before anything is auto-allowed. Whatever needs the value already has it from the
        // environment, so its presence in an argument is only ever the value being moved. The refusal names
        // the variable and never the value, and it is raised like every other automatic refusal.
        var held = HeldCredentials();
        if (AgentCredentialExposure.CarriedBy(input, held) is { } carried)
        {
            return Refuse(
                sessionId,
                toolName,
                $"(arguments withheld: they carry the value of {carried})",
                $"This call's arguments contain the value of {carried}, a credential the agent is running with. "
                + "The studio refuses to pass a credential's value through a tool call.");
        }

        // Backlog 243: a commit review conversation only reads, and nothing it does is put in front of a person. Its own
        // built-in reads run when they stay inside the copy (the same answer every read gets below), its four studio
        // reads run, and EVERY other call is refused here, before the gates and the card: an edit, a write, a command,
        // the browser, a graph write and a read that leaves the copy alike. The turn-ending document was allowed above.
        if (_reviews.ContainsKey(sessionId))
        {
            if (AgentReviewTools.IsBuiltIn(toolName) && AgentToolSafety.IsReadOnly(toolName))
            {
                var read = AgentReadScope.Check(toolName, input, root, null, null);
                return read.Contained
                    ? Auto(sessionId, toolName, ToolApprovalDecision.Allow(read.Reason))
                    : Refuse(sessionId, toolName, AgentToolSubject.From(input), read.Reason + " " + AgentReviewTools.Refusal(toolName));
            }

            return AgentReviewTools.IsStudio(toolName) && _sessionSecrets.ContainsKey(sessionId)
                ? Auto(sessionId, toolName, ToolApprovalDecision.Allow("A commit review's read of the studio's own records."))
                : Refuse(sessionId, toolName, AgentToolSubject.From(input), AgentReviewTools.Refusal(toolName));
        }

        // Work item #352: a graph-only conversation ("Notes and items only") never changes the workspace or the
        // machine. Anything not on its explicit list is refused HERE, before the reads, the role and work gates, the
        // graph allow-list, the autopilot rules, the reviewer and the card, so it is never put in front of a person
        // and nothing below can widen it (default deny: a tool not named on the list is refused). What IS on the list
        // goes on to the ordinary answer unchanged: a read inside the copy runs, one outside asks, a graph creation
        // runs and a graph change asks. The work gate passes graph tools and reads by construction (ADR 0020), so a
        // graph-only session needs no plan to write items, and the gate is not touched for editing sessions.
        if (_graphOnly.ContainsKey(sessionId) && !AgentGraphOnlyTools.Allows(toolName))
        {
            return Refuse(sessionId, toolName, AgentToolSubject.From(input), AgentGraphOnlyTools.Refusal(toolName));
        }

        // Work item #359: the studio assistant is held the same way, to its own explicit list (the graph-only list plus
        // the wiki-only design tools, attaching a conversation image, and its two own tools). Default deny, here,
        // before everything below, so nothing below can widen it.
        var assistant = _assistant.ContainsKey(sessionId);
        if (assistant && !AgentAssistantTools.Allows(toolName))
        {
            return Refuse(sessionId, toolName, AgentToolSubject.From(input), AgentAssistantTools.Refusal(toolName));
        }

        // Work item #359: the assistant's two own tools (start a building conversation, dispatch the autopilot) act on
        // the person's behalf beyond the graph. A conversation that is not the assistant (an editing one, a graph-only
        // one, an autopilot builder) never gets them: refused here, in words, without asking anyone. For the assistant
        // they are taken straight to a person's card below: they are on no allow-list (AgentGraphTools), they are not
        // edits (so no trust level reaches them), the autopilot's rules and judge never name them and are skipped for
        // them explicitly, and nothing between here and the card returns an allow for them.
        var mustAsk = AgentAssistantTools.MustAsk(toolName);
        if (mustAsk && !assistant)
        {
            return Refuse(sessionId, toolName, AgentToolSubject.From(input), AgentAssistantTools.NotAnAssistant(toolName));
        }

        string? warning = null;
        if (AgentToolSafety.IsReadOnly(toolName))
        {
            var verdict = AgentReadScope.Check(toolName, input, root, memoryRoot, memoryTarget, toolResultsRoot);
            if (verdict.Contained)
            {
                return Auto(sessionId, toolName, ToolApprovalDecision.Allow(verdict.Reason));
            }

            warning = verdict.Reason;
        }

        // Gates G1 and G2 (ADR 0020, backlog 44 and 45): no change to the WORKSPACE without a work item and
        // a plan. They sit after the read-only answer above, so reading and searching are never gated, and
        // they let the studio's own graph tools through by name, because writing the work item and the plan
        // is how a session clears them. Each store answers false when it cannot tell, so an unreadable
        // database denies the change.
        //
        // G2 is enforced only where there is a store to enforce it with: a studio with a board and no wiki
        // keeps G1 and has no G2, rather than refusing every change for a plan it could never hold.
        // Where the change would land, RELATIVE to the session's contained copy, because that is the shape
        // a scope pattern is written in. Resolved from the raw input under both readings, which is the same
        // answer the prompt below is built from. A call that names nowhere has no path here and therefore
        // cannot be out of scope, and costs nothing against a change budget either.
        var destination = AgentToolSafety.IsReadOnly(toolName)
            ? null
            // Backlog 202: memoryRoot is deliberately NOT passed here. This Resolve() feeds the destination
            // the work gates and the ADR 0028 trust-edit branch below key on, and a write is never covered by
            // the memory exemption (only AgentToolSafety's read-only list is, and no edit tool is on it): a
            // write that happens to land inside this session's own memory must still ask, exactly like one
            // that lands anywhere else outside the copy, so this containment answer is measured against the
            // workspace copy alone.
            : InCopy(AgentReadScope.Resolve(toolName, input, root), root);

        // Backlog 88: the role's capability set, checked BEFORE the work gates. A verifier asked to edit a
        // file is not missing a work item; it is being asked to do somebody else's job, and saying so is a
        // more useful answer than telling it to write a plan for a change it must not make.
        if (AgentRoleGate.Check(
                _roles?.For(sessionId),
                toolName,
                AgentToolSafety.IsReadOnly(toolName),
                toolName is { Length: > 0 }
                    && toolName.StartsWith(AgentWorkGate.StudioToolPrefix, StringComparison.Ordinal))
            is { } roleRefusal)
        {
            return Refuse(sessionId, toolName, AgentToolSubject.From(input), roleRefusal);
        }

        // Backlog 166: a Bash command that only reads is sent back to Read, Grep and Glob, which run unattended. Both
        // self-hosting trials read through Bash, and each such call asked a person (or met the work gate, whose "write
        // a plan first" is the wrong answer to someone who is only looking). It only ever refuses, so it widens
        // nothing, and it sits before the work gates so a look is answered as a look.
        if (AgentReadRedirect.Check(toolName, input.Text("command")) is { } lookInstead)
        {
            return Refuse(sessionId, toolName, AgentToolSubject.From(input), lookInstead);
        }

        // Work item #323: a Bash call that runs git inside a session's contained copy is refused up front, never
        // asked, because the copy has no .git by design and a git init there would make a commit look real and
        // then lose it. Gated on a registered root: this version ships one runtime (Claude Code, CLAUDE.md;
        // Codex and Copilot are parked under V2_CODEX_COPILOT), and ClaudeSessionRunner.Start refuses to start a
        // session with no contained copy and registers the resolved one as the root before a single tool call
        // can reach here, so this is never null for a session that actually runs. The check exists for a
        // session registered with no root at all (today only a test), where "this copy has no .git" is a claim
        // the studio cannot actually back, so it is not made; the call falls through to ask a person like any
        // other call this gate cannot explain, the same default every other fact-gated check here already uses.
        if (root is not null && AgentGitInBash.Check(toolName, input.Text("command")) is { } useGitCommit)
        {
            return Refuse(sessionId, toolName, AgentToolSubject.From(input), useGitCommit);
        }

        // Backlog 271: a write the pinned CLI refuses on its own after any approval (a name on its protected lists, such
        // as .devcontainer, .vscode or .npmrc), refused here with what to do instead, so a person is not asked for an
        // answer that changes nothing and the agent does not loop on the CLI's refusal. Checked on the resolved
        // destination too, so a link inside the copy cannot route around it (that destination is
        // AgentToolTarget.Canonical, shortened past AgentToolSubject.MaxTargetLength, so a link in a path longer than
        // that can still get past this check and is then asked about as before). Only refuses.
        if (AgentCliProtectedWrite.PathField(toolName) is { } pathField
            && AgentCliProtectedWrite.Check(toolName, input.Text(pathField), destination) is { } protectedWrite)
        {
            return Refuse(sessionId, toolName, AgentToolSubject.From(input), protectedWrite);
        }

        if (_work is { } work)
        {
            var facts = _governance is { } governance
                ? governance.Facts(sessionId, destination)
                : Bindings(work, sessionId);

            // Work item #340: a bare `pwd` changes nothing, so this gate's own readOnly fact also counts it as
            // one, alongside AgentToolSafety's tool-level answer. It still falls through to the ordinary Bash
            // approval flow below and asks a person exactly as before; only this gate's pre-plan refusal lifts.
            var readOnlyForGate = AgentToolSafety.IsReadOnly(toolName)
                || AgentWorkGate.IsBarePwd(toolName, input.Text("command"));

            var verdict = AgentWorkGate.Check(toolName, readOnlyForGate, facts);
            if (verdict.SentenceFor() is { } refusal)
            {
                return Refuse(sessionId, toolName, AgentToolSubject.From(input), refusal);
            }
        }

        if (!_sessionSecrets.ContainsKey(sessionId))
        {
            // Nobody is asked, so this is exactly the refusal backlog 117 is about: it is raised, never
            // returned in silence, so an agent retrying against it leaves a trace.
            return Refuse(
                sessionId,
                toolName,
                AgentToolSubject.From(input),
                "The studio has no running session to ask about this call.");
        }

        // Backlog 77 (decided 2026-09-21): reads of the studio's own graph, and the creations and
        // links a session makes in order to CLEAR the gates above, run without asking. It sits after the
        // role gate, the work gates and the registration check on purpose, so it widens nothing they
        // refused, and it is an exact list (AgentGraphTools): a change to something that already exists,
        // and every studio tool that reaches outside the graph, falls through and asks like anything else.
        if (!mustAsk && AgentGraphTools.RunsUnattended(toolName, input) is { } graphReason)
        {
            return Auto(sessionId, toolName, ToolApprovalDecision.Allow(graphReason));
        }

        // And what the arguments MEAN, which is a separate question from where they point and has its
        // own silence to end: an Agent call names no path, so the target block above is empty for it
        // while "isolation": "remote" ships the workspace copy to a cloud environment off this machine.
        // Read from the RAW input for the same reason everything else here is: the display copy is
        // capped, and this is what the user's answer turns on.
        //
        // Backlog 108 adds the other argument an answer turns on: one that NAMES a credential the agent is
        // holding, or reads the whole environment. Not refused, because the studio cannot know what a shell
        // does with a name; made impossible to approve without seeing.
        //
        // Backlog 187 adds one more: an argument naming wslc's host alias reaches a service on this PC.
        // Backlog 191 scopes that alarm off the studio's own graph-text tools (work_item_*, wiki_*): a call
        // naming the alias in one of those only stores the text in the studio's database and reaches nothing.
        //
        // Backlog 241 scopes the credential alarm's whole-environment line off those same graph-text tools, for the
        // same reason; the line naming a held variable stays on every tool, and so does the refusal above.
        // Backlog 314: both credential alarms skip a field that is only a label for a person (a Bash description), the
        // list being AgentCredentialExposure.DisplayOnlyFields; the refusal above still reads every field.
        //
        // Backlog 215 (plan @40): a prototype adjustment names no path and its arguments are a JSON list, so the
        // card says what each operation DOES, in words, read from the raw input by the reader the tool itself runs.
        //
        // Backlog 299: computed HERE, before the autopilot rule list below, because the Bash build/test rule must
        // see the same alarms the judge is kept from running past: a "plain" build carrying
        // dangerouslyDisableSandbox or run_in_background is not plain at all, whatever its command text says.
        IReadOnlyList<AgentToolEffect> effects =
        [
            .. AgentHostAlias.Naming(toolName, input),
            .. AgentCredentialExposure.Naming(toolName, input, held),
            .. AgentToolEffects.Of(toolName, input),
            .. Tools.PrototypeAdjustTool.Effects(toolName, input),
        ];

        // ADR 0031 decisions 7 to 9 (backlog 299): a session the autopilot marks gets a third answerer for
        // routine calls, counted separately from the allow-lists above so the attention split (#244) can
        // tell rules, judge and person apart. A session this flag was never set on reaches none of this.
        if (_autopilot.ContainsKey(sessionId) && !mustAsk)
        {
            if (effects.Count == 0
                && string.Equals(toolName, "Bash", StringComparison.Ordinal)
                && AutopilotApprovalRules.IsPlainBuildOrTest(input.Text("command")))
            {
                return AutoByRule(sessionId, toolName, toolUseId, input, ToolApprovalDecision.Allow(
                    "This is the project's own plain build or test command, which the autopilot's rule list runs unread."));
            }

            // The item's bound numbers are fetched only for a transition call, never on every autopilot call.
            if (string.Equals(toolName, AutopilotApprovalRules.TransitionTool, StringComparison.Ordinal))
            {
                var boundStatuses = (await AutopilotContextAsync(sessionId, cancellationToken).ConfigureAwait(false)).ItemStatuses;
                if (AutopilotApprovalRules.IsForwardStatusMove(toolName, input, boundStatuses))
                {
                    return AutoByRule(sessionId, toolName, toolUseId, input, ToolApprovalDecision.Allow(
                        "This moves the item forward along its own path (Ready, In Progress, In Review), which the "
                        + "autopilot's rule list runs unread."));
                }
            }

            // The branch check is fetched only for a tagless push, never on every autopilot call, and only once
            // the cheap checks (tool name, no tag) already match.
            if (string.Equals(toolName, AutopilotApprovalRules.PushTool, StringComparison.Ordinal)
                && input.KindOf("tag") == AgentToolValueKind.Absent)
            {
                var attemptBranch = AutopilotAttemptBranchFor(sessionId);
                var currentBranch = await AutopilotCurrentBranchAsync(sessionId, cancellationToken).ConfigureAwait(false);
                if (AutopilotApprovalRules.IsAttemptBranchPush(toolName, input, attemptBranch, currentBranch))
                {
                    return AutoByRule(sessionId, toolName, toolUseId, input, ToolApprovalDecision.Allow(
                        "This pushes the item's own attempt branch, which the autopilot's rule list runs unread."));
                }
            }

            // The branch facts are fetched only for attempt_branch, never on every autopilot call. Backlog 322:
            // a worktree item already has its branch recorded (ISessionBranches) before this call ever runs, and
            // a cap-1 item instead has the run's own base recorded (ISessionAttemptBases); attempt_branch itself
            // takes no argument at all, so these are the only facts that could ever say this is the item's OWN
            // branch.
            if (string.Equals(toolName, AutopilotApprovalRules.AttemptBranchTool, StringComparison.Ordinal))
            {
                var alreadyStarted = AutopilotAttemptBranchFor(sessionId);
                var liveBranch = await AutopilotCurrentBranchAsync(sessionId, cancellationToken).ConfigureAwait(false);
                var knownBase = AutopilotKnownBaseFor(sessionId);
                if (AutopilotApprovalRules.IsAttemptBranchStart(toolName, alreadyStarted, liveBranch, knownBase))
                {
                    return AutoByRule(sessionId, toolName, toolUseId, input, ToolApprovalDecision.Allow(
                        "This answers or creates the item's own attempt branch, which the autopilot's rule list runs unread."));
                }
            }
        }

        // Follow-up filed 2026-10-22 (approval card with no arguments): a call to one of the studio's OWN tools whose
        // arguments its own declared schema could not accept (a required field missing, or a declared field of the wrong
        // JSON kind) is refused here, before the commit reviewer, the judge or a card, with the fields named and the
        // ToolSearch step the agent skipped. Seen three times on 2026-10-21: a deferred work_item_update called before its
        // schema was loaded carried {} and a person was asked to approve a call the tool itself would refuse. It sits
        // after every allow above (the graph list, the autopilot rules), so it only ever turns what would have ASKED into
        // a refusal and never into an allow; a schema it cannot read or evaluate asks exactly as before.
        if (StudioToolArgumentCheck.Check(toolName, StudioToolSchemaFor(toolName), input) is { } schemaRefusal)
        {
            return Refuse(sessionId, toolName, AgentToolSubject.From(input), schemaRefusal);
        }

        // Work item #356: a studio tool call whose string argument ends in leaked tool-call markup (a closing parameter
        // or invoke tag) is refused in words, not asked: one conversation retried such a call 19 times, each a card. Same
        // place and same rule as the schema check above: after every allow, so it only turns an ask into a refusal.
        if (StudioToolMarkupCheck.Check(toolName, input) is { } markupRefusal)
        {
            return Refuse(sessionId, toolName, AgentToolSubject.From(input), markupRefusal);
        }

        // What the user is really answering about. The subject the prompt RENDERS is capped, and a
        // capped path is a different path: a 500-character file_path that lands on the key ring reads,
        // at 200, as an ordinary folder deep inside the copy. The trust branch below keys on the target's
        // CONTAINMENT, which was decided on the raw value, never on these display strings; apart from that
        // a change is never auto-allowed, so this changes what the person making the decision is shown.
        // Resolved from the RAW input, like every other containment answer here. (The request's Detail
        // itself stays RAW too, backlog 103: it becomes the persisted approval_requested row.)
        // Backlog 202: memoryRoot stays out of this one too, for the same reason as the destination above.
        // AgentTargetContainment.Inside here is exactly what feeds the trust-edit branch below, and a write
        // under this session's own memory is not the bounded exception ADR 0028 describes.
        var target = AgentReadScope.Resolve(toolName, input, root);

        // ADR 0028 (backlog 152, decided 2026-09-26): a workspace whose person chose "trust edits" lets an
        // EDIT run without asking, and only when five things hold at once. It is one of the four editing tools
        // by exact name (a command is not an edit, whatever it writes); its target resolved INSIDE the copy
        // under every reading, the same answer the read-only policy demands; it has a destination in the copy
        // (a call naming nowhere is not an edit anyone can trust); that destination is not a HIDDEN path,
        // because hooks, workflows and the agent's own settings run somewhere a person did not say they
        // trusted; and the level really is Edits, read live so a change of mind applies to the next call. It
        // sits after the credential, role and work gates, so it widens nothing they refused: a trusted edit
        // still needs its work item and plan and still counts against the change budget, which is recorded
        // here because no answer is coming to record it after. And it is RAISED, never silent: an unattended
        // change is as visible in the transcript as a refused one.
        //
        // Backlog 288: the configuration plane is not only the hidden paths. Git runs hooks from wherever
        // core.hooksPath points, which can be an ordinary folder, so the host's git is asked (live, at this call, and
        // only once everything else would let the edit run) and a path under that folder asks with the reason on the
        // card. A lookup that fails asks for any folder that looks like a hooks folder rather than allowing.
        //
        // Backlog 289: nor only the hooks folder. A work-tree file named by include.path or includeif.<condition>.path
        // anywhere in the repository's own config chain is configuration plane too, present or not and whether or not
        // an includeif condition on it currently holds, because an edit there can set core.fsmonitor, core.sshCommand,
        // core.hooksPath or a diff/merge/filter driver. An include can name any file at all, so a failed lookup here
        // widens all the way (every edit in the workspace asks) rather than narrowing to a naming convention.
        if (!mustAsk
            && destination is not null
            && target is { Containment: AgentTargetContainment.Inside }
            && WorkspaceTrustLevels.IsEdit(toolName)
            && !WorkspaceTrustLevels.IsHiddenPath(destination)
            && TrustFor(sessionId) == WorkspaceTrust.Edits)
        {
            var hooks = await HooksFolderFor(sessionId, cancellationToken).ConfigureAwait(false);
            var includes = await ConfigIncludesFor(sessionId, cancellationToken).ConfigureAwait(false);
            if (WorkspaceTrustLevels.ConfigurationPlaneReason(destination, hooks, includes) is { Length: > 0 } plane)
            {
                warning ??= plane;
            }
            else
            {
                _governance?.RecordApprovedChange(sessionId, destination);
                Trusted?.Invoke(new TrustedToolCall(
                    sessionId, toolName, AgentToolSubject.From(input), toolUseId, TrustedEditReason, _clock()));
                return Auto(sessionId, toolName, ToolApprovalDecision.Allow(TrustedEditReason));
            }
        }

        warning ??= Explain(toolName, target, root);

        // Backlog 243: a commit is reviewed by the commit reviewer role BEFORE a person is asked. It sits after every
        // gate above, so it runs only for a commit that would otherwise reach a card and never widens what they refused.
        // A fail is refused with the findings as the reason (#220's channel) and nobody is asked; anything else (a
        // pass, the role off, a review that gave no verdict) goes on to the card, which says which. Never a silent pass.
        //
        // Backlog 250: attempt_commit commits too, so it passes the same gate (AgentReviewTools.IsCommit, exact and ordinal)
        // with the same refusal, card and transcript line.
        //
        // Work item #341: before any of that, a commit call's own `answers` argument (the builder disputing a finding
        // from its latest failed review) is resolved and validated here, in code, never left to the reviewer's prompt:
        // an invalid answer is refused outright, so it never reaches a review and never reaches a commit either.
        CommitReview? review = null;
        if (AgentReviewTools.IsCommit(toolName))
        {
            var (answers, answersRefusal, markedState) = ResolveCommitAnswers(sessionId, input);
            if (answersRefusal is { Length: > 0 })
            {
                return Refuse(sessionId, toolName, AgentToolSubject.From(input), answersRefusal);
            }

            if (_reviewer?.Invoke() is { } reviewer)
            {
                var reviewRequest = CommitRequest(toolName, input) with { Answers = answers };
                var reviewOrNull = await ReviewAsync(reviewer, sessionId, reviewRequest, cancellationToken).ConfigureAwait(false);
                if (reviewOrNull is null)
                {
                    // Work item #341's reviewer finding: the hook's own cancellation cut the review itself short,
                    // so there is no verdict, genuine or manufactured, to record, and nothing read this call's own
                    // answers either. Bookkeeping is skipped entirely, and any finding numbers THIS call just
                    // marked answered are released, under the same lock, so the builder can answer them again: a
                    // call nobody read must not spend the one answer item #341 promises. Released on the EXACT
                    // state instance ResolveCommitAnswers marked them in, never re-fetched from the dictionary: a
                    // racing commit call for the same session could have already replaced it with a newer Failed
                    // review (one that may itself carry a settled finding forward onto the very same number), and
                    // releasing on THAT would un-settle a dispute this call never touched.
                    if (answers is { Count: > 0 } && markedState is { } stillPending)
                    {
                        lock (stillPending)
                        {
                            foreach (var answer in answers)
                            {
                                stillPending.Answered.Remove(answer.FindingNumber);
                            }
                        }
                    }

                    Count(sessionId, toolName, AttentionOutcome.Withdrawn, TimeSpan.Zero);
                    return ToolApprovalDecision.Deny("The studio stopped waiting for an answer.");
                }

                review = reviewOrNull;

                // Backlog 244: the verdict is counted on the building conversation, before the refusal or the card it leads to.
                Record(AttentionEntry.ReviewOf(sessionId, review.Outcome, _clock()));
                Reviewed?.Invoke(sessionId, review);

                // Work item #341: this review becomes the session's new latest-failed-review exactly when it still
                // blocks the commit, which is the only state a FUTURE answer can ever be validated against; a pass,
                // the role off or no verdict leaves nothing still blocking to dispute. The session's SETTLED
                // findings (below) are a SEPARATE, longer-lived fact: they survive this replacement either way, so
                // a kept dispute stays settled through an intervening Pass, TimedOut, Errored or Off round too,
                // never only through the round directly after it was kept. Done BEFORE the cancellation check
                // below, never after: by this point the review returned a genuine verdict (the null, no-verdict
                // case above already returned), so it is real regardless of whether the hook is still there to be
                // told about it, and a dispute it decided must be recorded (and a kept finding settled) rather
                // than silently lost along with the connection.
                var settled = _settledFindings.GetOrAdd(sessionId, static _ => new SettledFindings());
                if (review.Outcome == CommitReviewOutcome.Failed)
                {
                    var state = new FailedReviewState(review);

                    // A finding the reviewer just KEPT is settled: pre-mark it answered under its OWN new number
                    // in this new state too, so a second answer to it is refused under that number, not only its
                    // original one. Recorded in the session's longer-lived Settled set too (by content, not by
                    // number, which means nothing across rounds), so it is recognised again as long as a LATER
                    // round keeps re-listing it at the same file, line and rule, whether or not that round's own
                    // call answered it.
                    foreach (var outcome in review.AllAnswerOutcomes)
                    {
                        if (outcome.Verdict == CommitReviewAnswerVerdict.Kept && outcome.KeptAsFindingNumber is { } keptNumber)
                        {
                            state.Answered.Add(keptNumber);
                            settled.Add(review.AllFindings[keptNumber - 1]);
                        }
                    }

                    // Everything else already Settled from an EARLIER round, however many non-Failed rounds came
                    // between: matched against this round's own findings, whether or not this round's call
                    // answered it, which is the gap a kept finding re-listed untouched would otherwise fall
                    // through.
                    for (var number = 1; number <= review.AllFindings.Count; number++)
                    {
                        if (!state.Answered.Contains(number) && settled.MatchesCarriedForward(review.AllFindings[number - 1]))
                        {
                            state.Answered.Add(number);
                        }
                    }

                    _latestFailedReviews[sessionId] = state;
                }
                else
                {
                    _latestFailedReviews.TryRemove(sessionId, out _);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    // The hook went away while the review ran: there is nobody to answer, so nothing is put on a
                    // card. The bookkeeping above still ran, so the review's own verdict is not lost with it.
                    Count(sessionId, toolName, AttentionOutcome.Withdrawn, TimeSpan.Zero);
                    return ToolApprovalDecision.Deny("The studio stopped waiting for an answer.");
                }

                if (!review.AsksPerson)
                {
                    return Refuse(sessionId, toolName, AgentToolSubject.From(input), review.DenialReason);
                }

                // ADR 0031 decision 7: on an autopilot session, a PASS is the rule that clears the commit itself,
                // never a review the role was off for or one that gave no verdict (both still reach the card below).
                if (_autopilot.ContainsKey(sessionId)
                    && AutopilotApprovalRules.IsCommitAfterPassedReview(review.Outcome == CommitReviewOutcome.Passed))
                {
                    return AutoByRule(sessionId, toolName, toolUseId, input, ToolApprovalDecision.Allow(
                        "The commit reviewer passed this commit, which the autopilot's rule list runs unread."));
                }
            }
        }

        // ADR 0031 decision 8 (backlog 299): the judge answers for a Bash call on an autopilot session that
        // reached neither the rule list above nor an earlier refusal. A command carrying a held credential's
        // value never reaches here at all: that is refused at the very top of this method, before the
        // autopilot flag is even read, so there is nothing new to guard here. The judge is asked about the
        // COMMAND TEXT ALONE (with the session's own copy root rewritten to a dot and a redirection to
        // /dev/null removed first, backlog 324 and 326; the dev/null rule moved to the shared
        // DevNullRedirects, Domain, on backlog 337, unchanged in behaviour), so it must never be consulted
        // when `effects` already carries something a person has to see (a held credential's name, a
        // whole-environment read, the host alias, sandbox off, a remote isolation): those are exactly the
        // things the card exists to show, and the judge's questions say nothing about them.
        if (_autopilot.ContainsKey(sessionId)
            && !mustAsk
            && effects.Count == 0
            && string.Equals(toolName, "Bash", StringComparison.Ordinal)
            && input.Text("command") is { Length: > 0 } autopilotCommand
            && _judge?.Invoke() is { IsOn: true } autopilotJudge)
        {
            var judgedCommand = DevNullRedirects.Strip(RewriteCopyRootToDot(autopilotCommand, root));

            // Backlog 324: the rewrite above only reaches an occurrence of the root that stands as its own
            // shell word; one fused to something else (a glued flag, a brace expansion, a colon list, a
            // glob) is still in `judgedCommand`. The judge must never see the root at all, so such a fused
            // command is not sent to it: it falls through and asks a person instead, asking MORE than
            // before this rewrite existed (a command that reached the judge gate carrying the root used to
            // be sent to the judge with the root in it, fused or standing alone; a read-only command or one
            // carrying a held credential's value never reached the judge gate at all, refused earlier for
            // their own reasons). The STANDALONE case (the two v0.9.85 proof commands that carried an
            // absolute in-copy path AS QUOTED on #324, which the judge cleared neither of) is the opposite:
            // it is exactly what the rewrite above turns into a dot and still sends to the judge, which is
            // the point of this backlog item.
            if (!string.IsNullOrEmpty(root) && judgedCommand.Contains(root, StringComparison.Ordinal))
            {
                const string rootReason =
                    "This command still names the session's own copy root after the rewrite, so the autopilot's judge was not asked.";
                warning = warning is null ? rootReason : warning + " " + rootReason;
            }
            else
            {
                var itemTitle = (await AutopilotContextAsync(sessionId, cancellationToken).ConfigureAwait(false)).ItemTitle;
                var verdict = await autopilotJudge.JudgeAsync(
                    judgedCommand, WorkspaceName(root), itemTitle, cancellationToken).ConfigureAwait(false);
                if (verdict.Verdict == AutopilotJudgeVerdict.Safe)
                {
                    return AutoByJudge(sessionId, toolName, toolUseId, input, ToolApprovalDecision.Allow(
                        "The autopilot's judge found nothing unsafe about this command."));
                }

                var judgeReason = $"The autopilot's judge did not clear this command to run unread ({verdict.Reason}).";
                warning = warning is null ? judgeReason : warning + " " + judgeReason;
            }
        }

        var request = new PendingToolApproval(
            Guid.NewGuid().ToString("N"),
            sessionId,
            toolName,
            AgentToolSubject.From(input),
            toolUseId,
            AgentToolSubject.ForDisplay(input),
            _clock(),
            warning,
            target,
            effects,
            // Backlog 240: the whole of every argument the display copy cuts, from the RAW input, for the person
            // to read. Built after every decision above and read by nothing but the card.
            AgentToolSubject.CutArguments(input),
            review);

        var waiter = new Waiter(request);
        _pending[request.RequestId] = waiter;
        Requested?.Invoke(request);

        using var timeout = new CancellationTokenSource(_timeout);
        using var registration = timeout.Token.Register(() =>
        {
            // Backlog 244: marked before completing, so the count can tell a card nobody answered in time from one the
            // studio withdrew. A person's answer racing it still wins, because that answer is attributed to the user.
            waiter.TimedOut = true;
            Complete(
                request.RequestId,
                ToolApprovalDecision.Deny(
                    $"The studio saw no answer within {Describe(_timeout)}, so the call was denied."));
        });
        using var cancellation = cancellationToken.Register(() => Complete(
            request.RequestId,
            ToolApprovalDecision.Deny("The studio stopped waiting for an answer.")));

        var decision = await waiter.Completion.Task.ConfigureAwait(false);
        Count(sessionId, toolName, Answered(decision, waiter.TimedOut), _clock() - request.RequestedUtc);

        // A change the user approved counts against the budget, and only then: a refused change is not a
        // change. Recorded after the answer rather than before it, so the number says what happened.
        if (decision.Approved && destination is not null)
        {
            _governance?.RecordApprovedChange(sessionId, destination);
        }

        return decision;
    }

    /// <summary>
    /// What a commit call asks to commit, from its RAW arguments. <c>git_commit</c>: the message as text and the paths as
    /// the JSON array the CLI sent. <c>attempt_commit</c>: its summary as the message and NO paths whatever the call
    /// carries, because the tool never passes any and commits what the session was approved to change (backlog 250), so
    /// the reviewer reads exactly the set the tool will commit.
    /// </summary>
    private static CommitReviewRequest CommitRequest(string toolName, AgentToolInput input) =>
        string.Equals(toolName, AgentReviewTools.AttemptCommit, StringComparison.Ordinal)
            ? new CommitReviewRequest(input.Text("summary"), null)
            : new CommitReviewRequest(input.Text("message"), CommitPaths(input));

    /// <summary>
    /// Runs the commit review for an agent commit. A reviewer that throws, which the port says it must not, is an
    /// ERROR review that asks the person, never a pass and never a refusal, EXCEPT when the throw is the hook's own
    /// cancellation cutting the review itself short: that is not a verdict, genuine or manufactured, at all, so this
    /// returns null instead, and the caller skips its bookkeeping entirely for this call rather than treat a
    /// cancellation as though it were a real, if unlucky, Errored review.
    /// </summary>
    private static async Task<CommitReview?> ReviewAsync(
        ICommitReviewer reviewer, AgentSessionId sessionId, CommitReviewRequest request, CancellationToken cancellationToken)
    {
        var role = reviewer.Role;
        try
        {
            return await reviewer.ReviewAsync(sessionId, request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Work item #341's reviewer finding: this is the hook's own cancellation cutting the review's run
            // short (never AgentCommitReviewer's own internal timeout, which it catches and turns into a real
            // TimedOut verdict itself), so there is no verdict here at all, genuine or manufactured.
            return null;
        }
        catch (Exception ex)
        {
            // Work item #341: a reviewer that throws for any OTHER reason is still a no-verdict path, so any
            // answer this request carried is Undecided, never silently dropped along with the exception.
            return new CommitReview(
                CommitReviewOutcome.Errored, role, Problem: "The review failed: " + ex.Message,
                AnswerOutcomes: CommitReviewAnswerOutcomes.Undecided(request.Answers));
        }
    }

    /// <summary>
    /// Work item #341: validates a commit call's own <c>answers</c> argument against this session's latest FAILED
    /// commit review, which is the only thing an answer is ever checked against. Every bound is enforced here, in
    /// code: a malformed <c>answers</c> value (not an array, an entry with no integer <c>finding</c> or a non-string
    /// <c>reason</c>) is refused rather than silently skipped, a finding number must belong to THAT review (never an
    /// older one, never another session's), a number named twice in one call or already answered before is refused,
    /// and a call naming <c>answers</c> with no failed review on record (there never was one, or the latest review
    /// since cleared it) is refused rather than silently ignored. A valid answer is attached below to the next
    /// <see cref="CommitReviewRequest"/>. The check and the marking run under one lock on the session's own
    /// <see cref="FailedReviewState"/>, so two commit calls for the same session racing each other can never both
    /// pass the check for the same finding number before either marks it: the numbers are answered immediately,
    /// before the review that reads them even starts, so the same numbers cannot be submitted again on a retry,
    /// EXCEPT when the hook's own cancellation cuts that review short before any verdict (<see cref="ReviewAsync"/>
    /// returning null): <see cref="RequestAsync"/> then releases them again, on the EXACT <see cref="FailedReviewState"/>
    /// instance returned here, never whatever the broker's dictionary currently holds, so a racing commit call that
    /// has since replaced it with a newer one (which may itself have carried a settled finding forward onto the
    /// same number) is never disturbed by the release.
    /// </summary>
    private (IReadOnlyList<CommitReviewAnsweredFinding>? Answers, string? Refusal, FailedReviewState? State) ResolveCommitAnswers(
        AgentSessionId sessionId, AgentToolInput input)
    {
        var (raw, parseError) = CommitAnswers(input);
        if (parseError is { Length: > 0 })
        {
            return (null, parseError, null);
        }

        if (raw is not { Count: > 0 })
        {
            return (null, null, null);
        }

        if (!_latestFailedReviews.TryGetValue(sessionId, out var state))
        {
            return (null,
                "This call answers a finding, but there is no failed commit review for this session to answer. "
                + "Call git_commit (or attempt_commit) without answers first.", null);
        }

        lock (state)
        {
            var named = new HashSet<int>();
            List<CommitReviewAnsweredFinding> resolved = [];
            foreach (var (number, reason) in raw)
            {
                if (!named.Add(number))
                {
                    return (null, $"Finding {number} is named more than once in answers; answer each finding once.", null);
                }

                if (number < 1 || number > state.Review.AllFindings.Count)
                {
                    return (null,
                        $"Finding {number} was never made: the latest failed review has "
                        + $"{state.Review.AllFindings.Count} finding(s).", null);
                }

                if (state.Answered.Contains(number))
                {
                    return (null, $"Finding {number} was already answered; a finding is answered at most once.", null);
                }

                if (string.IsNullOrWhiteSpace(reason))
                {
                    return (null, $"Finding {number}'s answer needs a reason.", null);
                }

                var trimmedReason = reason.Trim();
                if (trimmedReason.Length > CommitReviewAnsweredFinding.MaxReasonLength)
                {
                    return (null,
                        $"Finding {number}'s answer is {trimmedReason.Length} characters; the most a reason may "
                        + $"carry is {CommitReviewAnsweredFinding.MaxReasonLength}. Shorten it and answer again.", null);
                }

                resolved.Add(new CommitReviewAnsweredFinding(number, state.Review.AllFindings[number - 1], trimmedReason));
            }

            foreach (var answer in resolved)
            {
                state.Answered.Add(answer.FindingNumber);
            }

            return (resolved, null, state);
        }
    }

    /// <summary>
    /// The <c>answers</c> argument of a commit call as (finding number, reason) pairs, or (null, null) when the
    /// field is absent (the ordinary case: no dispute). Anything PRESENT but malformed (not an array, an entry
    /// that is not an object, a missing or non-integer <c>finding</c>, a <c>reason</c> that is not a string) answers
    /// an error instead of silently dropping the bad entry: a dropped entry would run the review as though the
    /// builder had disputed nothing, and the builder would never be told why its dispute never arrived.
    /// </summary>
    private static (IReadOnlyList<(int Number, string Reason)>? Answers, string? Error) CommitAnswers(AgentToolInput input)
    {
        var kind = input.KindOf("answers");
        if (kind == AgentToolValueKind.Absent)
        {
            return (null, null);
        }

        if (kind != AgentToolValueKind.NotText)
        {
            return (null, "answers was sent as plain text; it must be a JSON array of objects, each with an integer finding and a string reason.");
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(input["answers"]);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return (null, "answers must be a JSON array of objects, each with an integer finding and a string reason.");
            }

            List<(int, string)> result = [];
            var index = 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                index++;
                if (item.ValueKind != System.Text.Json.JsonValueKind.Object)
                {
                    return (null, $"answers[{index}] must be an object with an integer finding and a string reason.");
                }

                if (!item.TryGetProperty("finding", out var findingElement)
                    || findingElement.ValueKind != System.Text.Json.JsonValueKind.Number
                    || !findingElement.TryGetInt32(out var number))
                {
                    return (null, $"answers[{index}] needs an integer finding.");
                }

                string reason;
                if (item.TryGetProperty("reason", out var reasonElement))
                {
                    if (reasonElement.ValueKind != System.Text.Json.JsonValueKind.String)
                    {
                        return (null, $"answers[{index}]'s reason must be a string.");
                    }

                    reason = reasonElement.GetString()?.Trim() ?? string.Empty;
                }
                else
                {
                    reason = string.Empty;
                }

                result.Add((number, reason));
            }

            return (result, null);
        }
        catch (System.Text.Json.JsonException)
        {
            return (null, "answers could not be read as JSON.");
        }
    }

    /// <summary>Work item #341: one building session's latest failed review, and which of its findings are already answered.</summary>
    private sealed class FailedReviewState(CommitReview review)
    {
        public CommitReview Review { get; } = review;

        // Guarded by `lock (this)` in ResolveCommitAnswers: the check and the mark are one atomic step, so two
        // commit calls for the same session racing each other can never both pass the check for the same number.
        public HashSet<int> Answered { get; } = [];
    }

    /// <summary>
    /// Work item #341's reviewer finding: every finding a building session has EVER had Kept, by content, kept
    /// independently of <see cref="FailedReviewState"/> (which a Pass, a TimedOut, an Errored or an Off outcome
    /// all replace or clear) so a kept dispute stays settled through any of those too, not only through the
    /// round directly after it was kept. Thread-safe on its own (a plain <see cref="List{T}"/> under one lock),
    /// because <see cref="AgentApprovalBroker"/>'s own per-session dictionaries hand out one instance per
    /// session that more than one call can reach.
    /// </summary>
    private sealed class SettledFindings
    {
        private readonly List<CommitReviewFinding> _findings = [];

        public void Add(CommitReviewFinding finding)
        {
            lock (_findings)
            {
                if (!_findings.Any(f => CommitReviewFinding.MatchesSameDispute(f, finding)))
                {
                    _findings.Add(finding);
                }
            }
        }

        /// <summary>Whether <paramref name="candidate"/> is a LATER review re-listing one of these settled findings.</summary>
        public bool MatchesCarriedForward(CommitReviewFinding candidate)
        {
            lock (_findings)
            {
                return _findings.Any(f => CommitReviewFinding.MatchesSameDispute(f, candidate));
            }
        }
    }

    /// <summary>The <c>paths</c> argument of a commit call as strings, or null when it named none.</summary>
    private static IReadOnlyList<string>? CommitPaths(AgentToolInput input)
    {
        if (input.KindOf("paths") != AgentToolValueKind.NotText)
        {
            return null;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(input["paths"]);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return null;
            }

            List<string> paths =
            [
                .. document.RootElement.EnumerateArray()
                    .Where(e => e.ValueKind == System.Text.Json.JsonValueKind.String)
                    .Select(e => e.GetString()!.Trim())
                    .Where(p => p.Length > 0),
            ];
            return paths.Count == 0 ? null : paths;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The sentence a trusted edit runs under, sent to the CLI and written to the transcript.</summary>
    public const string TrustedEditReason =
        "Ran without asking: an edit inside this session's contained copy, and this folder's trust level lets those run (ADR 0028).";

    /// <summary>
    /// The trust level that applies to a session: its repository folder's, or Ask when none is known. A
    /// worktree session (#306, #308) never has its own level; <see cref="WorkspaceTrustScope.For"/> resolves
    /// which workspace id actually governs it (backlog 312), the SAME resolution the session work strip uses
    /// so the level it shows and sets is the one this reads, live on every call so a change to the folder's
    /// level reaches its worktrees immediately.
    /// </summary>
    private WorkspaceTrust TrustFor(AgentSessionId sessionId)
    {
        if (_trust is null || !_workspaces.TryGetValue(sessionId, out var workspaceId))
        {
            return WorkspaceTrust.Ask;
        }

        var repositoryWorkspaceId = WorkspaceTrustScope.For(_workspaceStore, workspaceId);
        return repositoryWorkspaceId is null ? WorkspaceTrust.Ask : _trust.For(repositoryWorkspaceId);
    }

    /// <summary>
    /// The folder the host's git runs this session's hooks from (backlog 288). None when nothing here can ask, which is
    /// ADR 0028's rule as it was; a port that cannot answer says Unknown itself, and that asks.
    /// </summary>
    private Task<WorkspaceHooksFolder> HooksFolderFor(AgentSessionId sessionId, CancellationToken cancellationToken) =>
        _hooks is not null && _workspaces.TryGetValue(sessionId, out var workspaceId)
            ? _hooks.ForAsync(workspaceId, cancellationToken)
            : Task.FromResult(WorkspaceHooksFolder.None);

    /// <summary>
    /// The work-tree files this session's host git config includes (backlog 289). None when nothing here can ask,
    /// which is the rule as #288 left it; a port that cannot answer says Unknown itself, and that asks for everything.
    /// </summary>
    private Task<WorkspaceConfigIncludes> ConfigIncludesFor(AgentSessionId sessionId, CancellationToken cancellationToken) =>
        _configIncludes is not null && _workspaces.TryGetValue(sessionId, out var workspaceId)
            ? _configIncludes.ForAsync(workspaceId, cancellationToken)
            : Task.FromResult(WorkspaceConfigIncludes.None);

    // The ONE entry point a decision can be attributed to the user by (backlog 114). Every other
    // completion in this file is the studio refusing on its own account, and each of those keeps the
    // default attribution, so a refusal nobody was asked about can never be reported as the user's.
    public bool Answer(string requestId, bool approved, string reason) =>
        Complete(requestId, ToolApprovalDecision.FromUser(approved, reason));

    public ToolApprovalDecision Refuse(AgentSessionId sessionId, string toolName, string detail, string reason)
    {
        var decision = ToolApprovalDecision.Deny(reason);
        Count(sessionId, toolName, AttentionOutcome.Refused, TimeSpan.Zero);
        Refused?.Invoke(new AutomaticToolRefusal(
            sessionId, toolName ?? string.Empty, detail ?? string.Empty, decision.Reason, _clock()));
        return decision;
    }

    public IReadOnlyList<PendingToolApproval> PendingFor(AgentSessionId sessionId) =>
    [
        .. _pending.Values
            .Where(w => w.Request.SessionId.Equals(sessionId))
            .Select(w => w.Request)
            .OrderBy(r => r.RequestedUtc)
            .ThenBy(r => r.RequestId, StringComparer.Ordinal),
    ];

    public void Dispose()
    {
        foreach (var waiter in _pending.Values.ToList())
        {
            Complete(waiter.Request.RequestId, ToolApprovalDecision.Deny("The studio shut down."));
        }
    }

    /// <summary>Resolves a parked request exactly once; a second answer changes nothing.</summary>
    /// <summary>The bindings alone, for a studio that cannot assemble the fuller facts (backlog 85).</summary>
    private WorkGateFacts Bindings(ISessionWorkItems work, AgentSessionId sessionId)
    {
        var hasWork = work.HasWork(sessionId);
        var hasPlan = _plans is not { } plans || plans.HasPlan(sessionId);
        return new WorkGateFacts(
            HasWork: hasWork,
            WorkIsWorkable: hasWork,
            HasPlan: hasPlan,
            PlanHasContent: hasPlan,
            PlanCoversWork: hasPlan,
            InDeclaredScope: true,
            WithinChangeBudget: true);
    }

    /// <summary>
    /// The destination as a path INSIDE the copy, with forward separators, or null when there is none or
    /// it lands outside. A scope pattern is written against the repository, so a path that is not in the
    /// repository is not a path a pattern could have meant.
    /// </summary>
    private static string? InCopy(AgentToolTarget? target, string? root)
    {
        if (target is null || string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        var resolved = target.Canonical ?? target.Collapsed ?? target.Requested;
        if (string.IsNullOrWhiteSpace(resolved))
        {
            return null;
        }

        var prefix = root.Replace('\\', '/').TrimEnd('/');
        var path = resolved.Replace('\\', '/');
        if (!path.StartsWith(prefix + "/", StringComparison.Ordinal))
        {
            return null;
        }

        return path[(prefix.Length + 1)..];
    }

    private bool Complete(string requestId, ToolApprovalDecision decision)
    {
        if (string.IsNullOrEmpty(requestId) || !_pending.TryRemove(requestId, out var waiter))
        {
            return false;
        }

        if (!waiter.Completion.TrySetResult(decision))
        {
            return false;
        }

        Resolved?.Invoke(waiter.Request, decision);
        return true;
    }

    /// <summary>
    /// The sentence a CHANGE earns about where it lands, or null when there is nothing to say.
    /// <para>
    /// A read that leaves the copy has said so since the read scope existed; a change that leaves it
    /// said nothing at all, and the prompt it produced was indistinguishable from an ordinary in-copy
    /// edit. That asymmetry is what turned one disguised approval into a write to
    /// <c>&lt;data dir&gt;/agent-sessions/{id}/approve.sh</c>, which is the file that answers the NEXT
    /// approval. Nothing here allows anything: it only refuses to be quiet.
    /// </para>
    /// </summary>
    private static string? Explain(string toolName, AgentToolTarget? target, string? root) => target switch
    {
        null => null,

        { Containment: AgentTargetContainment.Outside } outside =>
            $"{Name(toolName)} names a path that lands on {outside.Canonical ?? outside.Requested}, which is "
            + "OUTSIDE this workspace's contained copy. Approving this changes something the workspace does "
            + "not contain.",

        // "Could not tell" is not "inside", and it is only worth saying when there was a copy to
        // measure against: a session registered without one has already been told that elsewhere.
        { Containment: AgentTargetContainment.Undetermined } when !string.IsNullOrWhiteSpace(root) =>
            $"The studio could not work out where {Name(toolName)} would act, so it cannot say whether this "
            + "stays inside this workspace's contained copy.",

        _ => null,
    };

    private static string Name(string? toolName) =>
        string.IsNullOrWhiteSpace(toolName) ? "This tool" : toolName;

    /// <summary>An allow the studio's own rules made, counted as such (backlog 244) and returned unchanged.</summary>
    private ToolApprovalDecision Auto(AgentSessionId sessionId, string toolName, ToolApprovalDecision decision)
    {
        Count(sessionId, toolName, AttentionOutcome.Auto, TimeSpan.Zero);
        return decision;
    }

    /// <summary>
    /// An allow the autopilot's rule list made (ADR 0031 decision 7), counted apart from <see cref="Auto"/> and
    /// raised on <see cref="AutopilotAllowed"/> so the transcript names the rule, exactly as visible as a
    /// refusal (backlog 299: "every unattended answer is recorded with the rule or the judgement that allowed it").
    /// </summary>
    private ToolApprovalDecision AutoByRule(
        AgentSessionId sessionId, string toolName, string? toolUseId, AgentToolInput input, ToolApprovalDecision decision)
    {
        Count(sessionId, toolName, AttentionOutcome.AutopilotRule, TimeSpan.Zero);
        AutopilotAllowed?.Invoke(new AutopilotAllowedCall(
            sessionId, toolName, AgentToolSubject.From(input), toolUseId, decision.Reason, _clock()));
        return decision;
    }

    /// <summary>
    /// An allow the autopilot's judge made (ADR 0031 decision 8), counted apart from <see cref="Auto"/> and
    /// raised on <see cref="AutopilotAllowed"/> so the transcript names the judgement that allowed it.
    /// </summary>
    private ToolApprovalDecision AutoByJudge(
        AgentSessionId sessionId, string toolName, string? toolUseId, AgentToolInput input, ToolApprovalDecision decision)
    {
        Count(sessionId, toolName, AttentionOutcome.AutopilotJudge, TimeSpan.Zero);
        AutopilotAllowed?.Invoke(new AutopilotAllowedCall(
            sessionId, toolName, AgentToolSubject.From(input), toolUseId, decision.Reason, _clock()));
        return decision;
    }

    /// <summary>
    /// Backlog 324: before a command is judged, every occurrence of the session's own copy root that
    /// stands as its own shell word, or begins one, is rewritten to <c>.</c>, so a command written with an
    /// absolute in-copy path (agents do this constantly: <c>/workspaces/&lt;id&gt;/README.md</c>) reads to
    /// the judge exactly as it would if it had been written relative to the working folder. An occurrence
    /// fused to something else (a brace expansion such as <c>{/workspaces/w1/a,/workspaces/w1/b}</c>, a
    /// flag glued to its value such as <c>-I/workspaces/w1/include</c>, a colon-separated list, a glob like
    /// <c>/workspaces/w1*</c>) is not one of the shapes checked below and is left as written. This function
    /// does not by itself guarantee <paramref name="root"/> never reaches the judge: its caller does, by
    /// checking whether the result still contains <paramref name="root"/> and refusing to call the judge at
    /// all when it does (falling through to ask a person instead), so what the judge actually sees never
    /// contains the root.
    /// <para>
    /// "Stands as its own shell word, or begins one" is checked with an ALLOW-list of boundary characters,
    /// not a reject-list of word characters: a folder name may legally contain a letter, digit, underscore,
    /// hyphen or dot, so a reject-list of "word" characters would wrongly rewrite <c>/workspaces/w1-old</c>
    /// or <c>/workspaces/w1.bak</c>, two DIFFERENT folders that merely start with the root
    /// <c>/workspaces/w1</c>, as though they were the copy itself. The character immediately before the
    /// match (or the start of the command) must be whitespace, a quote (<c>'</c> or <c>"</c>), <c>=</c> (an
    /// env-var assignment), or a shell metacharacter (<c>;</c> <c>&amp;</c> <c>|</c> <c>&lt;</c> <c>&gt;</c>
    /// <c>(</c> <c>)</c> <c>`</c>); the character immediately after the match (or the end of the command)
    /// must be one of those same shell metacharacters, whitespace, a quote, or <c>/</c> (continuing into a
    /// subpath). A root embedded in a longer, unrelated path (<c>/data/workspaces/w1</c>) also fails: the
    /// character before the match is an ordinary letter, not an allowed boundary character.
    /// </para>
    /// </summary>
    private static string RewriteCopyRootToDot(string command, string? root)
    {
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(command))
        {
            return command;
        }

        var result = new StringBuilder(command.Length);
        var searchFrom = 0;
        while (true)
        {
            var index = command.IndexOf(root, searchFrom, StringComparison.Ordinal);
            if (index < 0)
            {
                result.Append(command, searchFrom, command.Length - searchFrom);
                break;
            }

            var before = index > 0 ? command[index - 1] : (char?)null;
            var afterIndex = index + root.Length;
            var after = afterIndex < command.Length ? command[afterIndex] : (char?)null;

            if (!IsBoundaryBefore(before) || !IsBoundaryAfter(after))
            {
                // Not a boundary match (a longer name, or the root embedded in a longer path): copy
                // through the first character of this occurrence unchanged and keep looking past it.
                result.Append(command, searchFrom, index - searchFrom + 1);
                searchFrom = index + 1;
                continue;
            }

            result.Append(command, searchFrom, index - searchFrom);
            result.Append('.');
            searchFrom = afterIndex;
        }

        return result.ToString();
    }

    private const string ShellMetacharacters = ";&|<>()`";

    private static bool IsBoundaryBefore(char? c) =>
        c is null || char.IsWhiteSpace(c.Value) || c is '\'' or '"' or '=' || ShellMetacharacters.Contains(c.Value);

    private static bool IsBoundaryAfter(char? c) =>
        c is null || c == '/' || char.IsWhiteSpace(c.Value) || c is '\'' or '"' || ShellMetacharacters.Contains(c.Value);

    /// <summary>
    /// The workspace folder's bare NAME, never its path, for the judge's state (decision 8 names only the
    /// folder's name). Empty when there is no root to name.
    /// </summary>
    private static string WorkspaceName(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return string.Empty;
        }

        var trimmed = root.Replace('\\', '/').TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }

    /// <summary>
    /// The autopilot session context (backlog 299), fetched and never let propagate: a graph lookup that
    /// throws (a provider outage, an unreadable store) must turn into the fail-closed empty context, which
    /// matches no item number and gives the judge no title, rather than an exception out of RequestAsync
    /// that would leave the call unanswered instead of on a card.
    /// </summary>
    private async Task<AutopilotSessionContext> AutopilotContextAsync(AgentSessionId sessionId, CancellationToken cancellationToken)
    {
        if (_autopilotContext is null)
        {
            return AutopilotSessionContext.Empty;
        }

        try
        {
            return await _autopilotContext(sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return AutopilotSessionContext.Empty;
        }
    }

    /// <summary>The session's own recorded attempt branch (backlog 299), or null when there is none or the lookup fails.</summary>
    private string? AutopilotAttemptBranchFor(AgentSessionId sessionId)
    {
        try
        {
            return _autopilotAttemptBranch?.Invoke(sessionId);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The run's own base branch for this session's not-yet-started attempt (backlog 322), or null when there is none or the lookup fails.</summary>
    private string? AutopilotKnownBaseFor(AgentSessionId sessionId)
    {
        try
        {
            return _autopilotKnownBase?.Invoke(sessionId);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The workspace's actual current branch (backlog 299), or null when there is none or the lookup fails.</summary>
    private async Task<string?> AutopilotCurrentBranchAsync(AgentSessionId sessionId, CancellationToken cancellationToken)
    {
        if (_autopilotCurrentBranch is null)
        {
            return null;
        }

        try
        {
            return await _autopilotCurrentBranch(sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// How a parked card ended (backlog 244). Only the user's own answer is approved or denied; a denial with any words
    /// beyond the fixed sentence is a denial with a reason, decided here so the words themselves are never kept.
    /// </summary>
    public static AttentionOutcome Answered(ToolApprovalDecision decision, bool timedOut)
    {
        if (decision.DecidedBy == ToolApprovalDecidedBy.User)
        {
            return decision.Approved
                ? AttentionOutcome.Approved
                : string.Equals(decision.Reason, ToolApprovalDecision.UserDeclinedReason, StringComparison.Ordinal)
                    ? AttentionOutcome.Denied
                    : AttentionOutcome.DeniedWithReason;
        }

        return timedOut ? AttentionOutcome.TimedOut : AttentionOutcome.Withdrawn;
    }

    private void Count(AgentSessionId sessionId, string? toolName, AttentionOutcome outcome, TimeSpan waited) =>
        Record(AttentionEntry.Decision(sessionId, AttentionToolKinds.Of(toolName), outcome, waited, _clock()));

    // The store promises not to throw; this is the belt to that brace, because a counting failure must never become the
    // answer to a tool call.
    private void Record(AttentionEntry entry)
    {
        try
        {
            _attention?.Record(entry);
        }
        catch (Exception)
        {
            // Deliberately swallowed: the count is short by one, the decision stands.
        }
    }

    private static string Describe(TimeSpan timeout) =>
        timeout.TotalHours >= 1
            ? $"{timeout.TotalHours:0.#} hour(s)"
            : $"{timeout.TotalMinutes:0.#} minute(s)";

    private sealed class Waiter(PendingToolApproval request)
    {
        public PendingToolApproval Request { get; } = request;

        /// <summary>Set when the studio's own timeout is what ended the wait (backlog 244).</summary>
        public bool TimedOut
        {
            get => Volatile.Read(ref _timedOut);
            set => Volatile.Write(ref _timedOut, value);
        }

        private bool _timedOut;

        public TaskCompletionSource<ToolApprovalDecision> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
