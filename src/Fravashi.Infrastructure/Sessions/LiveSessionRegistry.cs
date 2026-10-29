using System.Collections.Concurrent;
using Fravashi.Application.AgentSync;
using Fravashi.Application.Network;
using Fravashi.Application.Sessions;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Application.Telemetry;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Telemetry;
using Fravashi.Infrastructure.Sessions.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Fravashi.Domain;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>What to start a session with.</summary>
/// <param name="ResumeOf">A previous session to continue: its CLI conversation and its transcript.</param>
/// <param name="Images">Images pasted into the first turn (backlog 26); with them the prompt may be blank.</param>
/// <param name="FromStudio">The studio wrote the prompt, not a person, so the transcript must not say they did (backlog 184).</param>
/// <param name="Effort">
/// The effort to start at (requested 2026-10-22, model and effort per work item). Null means nobody picked one: a
/// fresh conversation then passes no <c>--effort</c>, and a resumed one keeps the effort it was last at, exactly as a
/// null <paramref name="Model"/> keeps its model.
/// </param>
public sealed record SessionStartRequest(
    string WorkspaceId,
    string WorkingDirectory,
    string Prompt,
    string? Model = null,
    AgentSessionId? ResumeOf = null,
    string? Title = null,
    IReadOnlyList<AgentImageAttachment>? Images = null,
    bool FromStudio = false,
    AgentEffort? Effort = null,
    bool GraphOnly = false,
    bool Assistant = false);

/// <summary>
/// The studio's live sessions (plan 2e Task 5). A singleton, which is the whole point: the session
/// belongs to the STUDIO, not to a circuit, so the Sessions area and the Code page panel watch the
/// same object and closing the window leaves the agent running (decision 6).
/// <para>
/// One live session per workspace (decision 2): starting a second one is refused rather than
/// silently queued.
/// </para>
/// <para>
/// Work item #352 adds ONE graph-only conversation per workspace beside it ("Notes and items only",
/// <see cref="AgentGraphOnlyTools"/>): it never touches the workspace, so it cannot collide with the editing one.
/// It has a slot of its own (a second graph-only one is refused), it does not take the editing slot (an editing
/// conversation still cannot start while another editing one runs, and can start while a graph-only one runs),
/// and once a conversation is graph-only it stays so through every resume, model change and effort change.
/// </para>
/// <para>
/// Work item #359 adds ONE studio assistant conversation per workspace (Home's general assistant,
/// <see cref="AgentAssistantTools"/>), in a third slot of its own: it takes neither the editing slot nor the graph-only
/// one, so a report on Home never collides with a running conversation, and a second assistant start is refused (the
/// box continues the running one instead). Like graph-only, the mark is permanent.
/// </para>
/// </summary>
public sealed class LiveSessionRegistry : IDisposable
{
    private readonly IAgentSessionRunner _runner;
    private readonly ISessionStore _store;
    private readonly IAgentApprovalBroker _broker;
    private readonly IWorkspaceAgentSync? _sync;

    // Backlog 62: the owner's spend ceilings, asked on every decision rather than captured once, so a
    // limit saved in Settings bites the next turn instead of the next restart.
    private readonly IAgentSpendLimitSource _limits;

    // Backlog 74: the only place in the studio that sees a session's whole life, so the only place the
    // reliability events about sessions can honestly be produced. It records nothing unless the owner
    // switched recording on, and it never leaves this machine.
    private readonly IProductEventRecorder _telemetry;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<AgentTurnDocumentKind> _documentKinds;

    // Backlog 84: the studio's tool server, whose refusals the transcript has to hear about, and its ledger,
    // which goes when the conversation does. Both optional so a wiring without the tool server keeps working.
    private readonly StudioToolServer? _toolServer;
    private readonly IToolCallLedger? _toolLedger;

    // Backlog 44: what the conversation was working on. Deleted with the conversation, because a link to a
    // session nobody can open again is a row that only ever misleads a later reader of the board.
    private readonly ISessionWorkItems? _work;

    // Backlog 45: and the plan it was working under, deleted with the conversation for the same reason.
    private readonly ISessionPlans? _plans;

    // Backlog 85: and what it was approved to change, which is only meaningful while the conversation is.
    private readonly ISessionGovernance? _governance;

    // Backlog 88: and the role it ran under.
    private readonly ISessionRoles? _roles;

    // Backlog 86: and the branch its attempt ran on. The BRANCH itself stays: deleting a conversation
    // must not delete the work it produced, only the studio's record of which conversation produced it.
    private readonly ISessionBranches? _branches;

    // Backlog 244: the operator's attention. The registry counts the turns a person types; the broker counts decisions.
    private readonly ISessionAttention? _attention;
    // Backlog 157: fravashi.exe's upgrade operation, and the upgrade a conversation scheduled for the end of its turn.
    private readonly Application.Updates.IStudioUpgrades? _upgrades;
    private readonly StudioUpgradeSchedule? _upgradeSchedule;
    private readonly ConcurrentDictionary<AgentSessionId, bool> _toolRefusalShown = new();
    private readonly ConcurrentDictionary<AgentSessionId, LiveAgentSession> _sessions = new();

    /// <summary>
    /// Per workspace, the session the user walked away from with "new conversation". A pane resolving
    /// the workspace's LATEST session must not serve it any more: the fresh state used to live in the
    /// one pane that was clicked, so the Code panel kept rendering the replaced conversation and a
    /// message typed there started a new session while that history silently vanished. Dismissal is
    /// registry state because the panes are views of ONE conversation per workspace.
    /// </summary>
    private readonly ConcurrentDictionary<string, AgentSessionId> _dismissedLatest = new();

    private readonly Lock _startGate = new();

    /// <summary>
    /// The agent CLI's conversation files, or null for a wiring that has no data volume to look at (tests of
    /// the registry alone). With it, a resume is offered only when the file is there, and deleting a
    /// conversation removes them; without it, neither check nor removal happens.
    /// </summary>
    private readonly AgentConversationFiles? _conversations;

    /// <summary>
    /// What this process read, once, from <see cref="StudioRestartReason.ConsumeOnce"/> (backlog 218): a plain-words
    /// reason a specific actor left for a restart it caused on purpose, or null for the ordinary case (the process
    /// simply restarted, with no particular actor to name). Every session this registry finds orphaned during its
    /// whole lifetime was orphaned by the SAME restart that started this process, so one value read at construction
    /// covers all of them - there is no need to correlate it per session or re-read it later.
    /// </summary>
    private readonly string? _restartReason;

    // Backlog 229: whether the agent's model host answers, asked only while a conversation waits for it, and the
    // pause between asks (Task.Delay in production; a test's own, which moves its fake clock). Null reachability is a
    // wiring with no probe, where a failed turn ends exactly as it did before.
    private readonly INetworkReachability? _reachability;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ConcurrentDictionary<AgentSessionId, NetworkWaitTicket> _networkWaits = new();

    // Backlog 235: the broker token, where the broker carries the runtime. Null (or not applying) is every other wiring,
    // where nothing here changes.
    private readonly BrokerCredentialRenewal? _credentials;

    /// <param name="documentKinds">
    /// The document kinds every session started here asks its turns to end in (ADR 0022). Empty is the
    /// untyped path, which is what the studio ships until the typed-turn row is switched on
    /// (<see cref="SessionsServiceCollectionExtensions.TypedTurnKindsFor"/>).
    /// </param>
    public LiveSessionRegistry(
        IAgentSessionRunner runner,
        ISessionStore store,
        IAgentApprovalBroker broker,
        IWorkspaceAgentSync? sync = null,
        Func<DateTimeOffset>? clock = null,
        ILogger<LiveSessionRegistry>? logger = null,
        IReadOnlyList<AgentTurnDocumentKind>? documentKinds = null,
        IProductEventRecorder? telemetry = null,
        IAgentSpendLimitSource? limits = null,
        AgentConversationFiles? conversations = null,
        StudioToolServer? toolServer = null,
        IToolCallLedger? toolLedger = null,
        ISessionWorkItems? work = null,
        ISessionPlans? plans = null,
        ISessionGovernance? governance = null,
        ISessionRoles? roles = null,
        ISessionBranches? branches = null,
        Application.Updates.IStudioUpgrades? upgrades = null,
        StudioUpgradeSchedule? upgradeSchedule = null,
        string? restartReason = null,
        INetworkReachability? reachability = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        BrokerCredentialRenewal? credentials = null,
        ISessionAttention? attention = null)
    {
        _attention = attention;
        _restartReason = string.IsNullOrWhiteSpace(restartReason) ? null : restartReason.Trim();
        _reachability = reachability;
        _credentials = credentials is { Applies: true } ? credentials : null;
        _delay = delay ?? Task.Delay;
        _upgrades = upgrades;
        _upgradeSchedule = upgradeSchedule;
        _limits = limits ?? IAgentSpendLimitSource.None;
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _sync = sync;
        _telemetry = telemetry ?? IProductEventRecorder.Off;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _logger = logger ?? NullLogger<LiveSessionRegistry>.Instance;
        _documentKinds = documentKinds ?? [];
        _conversations = conversations;

        _broker.Requested += OnApprovalRequested;
        _broker.Resolved += OnApprovalResolved;

        _toolServer = toolServer;
        _toolLedger = toolLedger;
        _work = work;
        _plans = plans;
        _governance = governance;
        _roles = roles;
        _branches = branches;
        if (_toolServer is not null)
        {
            _toolServer.ClientRefused += OnToolClientRefused;
        }
        _broker.Refused += OnToolRefused;
        _broker.Trusted += OnToolTrusted;
        _broker.AutopilotAllowed += OnAutopilotAllowed;
        _broker.Reviewed += OnCommitReviewed;
    }

    /// <summary>Raised when a session starts or ends, for surfaces that list them.</summary>
    public event Action? SessionsChanged;

    /// <summary>Every session this studio process currently holds, most recently active (started or resumed) first.</summary>
    public IReadOnlyList<LiveAgentSession> Sessions =>
        [.. _sessions.Values.OrderByDescending(s => s.ActivatedUtc)];

    public LiveAgentSession? Find(AgentSessionId id) =>
        _sessions.TryGetValue(id, out var session) ? session : null;

    /// <summary>
    /// The workspace's EDITING session that is still running, if any: the one live session per workspace (owner
    /// decision 2). A graph-only conversation running beside it is not this (work item #352); see
    /// <see cref="RunningGraphOnlyFor"/>.
    /// </summary>
    public LiveAgentSession? RunningFor(string workspaceId) =>
        _sessions.Values.FirstOrDefault(s => s.WorkspaceId == workspaceId && s.IsRunning && !s.IsGraphOnly && !s.IsAssistant);

    /// <summary>The workspace's graph-only conversation that is still running, if any (work item #352).</summary>
    public LiveAgentSession? RunningGraphOnlyFor(string workspaceId) =>
        _sessions.Values.FirstOrDefault(s => s.WorkspaceId == workspaceId && s.IsRunning && s.IsGraphOnly);

    /// <summary>The workspace's studio assistant conversation that is still running, if any (work item #359).</summary>
    public LiveAgentSession? RunningAssistantFor(string workspaceId) =>
        _sessions.Values.FirstOrDefault(s => s.WorkspaceId == workspaceId && s.IsRunning && s.IsAssistant);

    /// <summary>
    /// True while ANY conversation runs in the workspace, editing or graph-only: what a change to the folder itself
    /// (its project, say) must wait for, since a graph-only conversation writes that project's tracker.
    /// </summary>
    public bool AnyRunningFor(string workspaceId) =>
        _sessions.Values.Any(s => s.WorkspaceId == workspaceId && s.IsRunning);

    // Work item #352: what a stored conversation is, read once from its transcript. A conversation's kind never
    // changes after its first start, so a cached answer cannot go stale; a live session answers for itself.
    private readonly ConcurrentDictionary<AgentSessionId, bool> _storedGraphOnly = new();

    /// <summary>
    /// True when the conversation is graph-only, "Notes and items only" (work item #352), whether this process holds
    /// it or it is only a stored row: what the conversation list's chip and a resume both read.
    /// </summary>
    public bool IsGraphOnly(AgentSessionId id)
    {
        if (Find(id) is { } live)
        {
            return live.IsGraphOnly;
        }

        return _storedGraphOnly.GetOrAdd(id, key => _store.Transcript(key).OfType<GraphOnlyChosen>().Any());
    }

    // Work item #359: the same, for the studio assistant's mark.
    private readonly ConcurrentDictionary<AgentSessionId, bool> _storedAssistant = new();

    /// <summary>
    /// True when the conversation is the studio assistant (work item #359), whether this process holds it or it is only
    /// a stored row.
    /// </summary>
    public bool IsAssistant(AgentSessionId id)
    {
        if (Find(id) is { } live)
        {
            return live.IsAssistant;
        }

        return _storedAssistant.GetOrAdd(id, key => _store.Transcript(key).OfType<AssistantChosen>().Any());
    }

    /// <summary>The running conversation a start or resume of this kind would collide with, or null when its slot is free.</summary>
    private LiveAgentSession? Occupying(string workspaceId, bool graphOnly, bool assistant = false) =>
        assistant ? RunningAssistantFor(workspaceId) : graphOnly ? RunningGraphOnlyFor(workspaceId) : RunningFor(workspaceId);

    /// <summary>The running conversation a resume of this stored or held conversation would collide with.</summary>
    private LiveAgentSession? OccupyingFor(string workspaceId, AgentSessionId id) =>
        Occupying(workspaceId, IsGraphOnly(id), IsAssistant(id));

    /// <summary>What a second studio assistant start in a folder is told (work item #359); Home continues the first instead.</summary>
    public const string AssistantAlreadyRunning =
        $"This folder's {ProductName.Short} assistant is already running. Continue it, or start a new topic, which ends it first.";

    /// <summary>What a start is told when its slot is taken; the editing sentence is the one it always was.</summary>
    public const string GraphOnlyAlreadyRunning =
        "This workspace already has a notes and items conversation running. Stop it before starting another.";

    /// <summary>
    /// The workspace's most recent session, running or not; null once the user has replaced it with a
    /// fresh conversation (<see cref="StartFresh"/>). It can still be reached by id, and it stays in
    /// the history list; it just stopped being what a pane shows by default.
    /// </summary>
    public LiveAgentSession? LatestFor(string workspaceId)
    {
        // Work item #359: the studio assistant lives on Home, so the panes that show "the folder's conversation" (the
        // Sessions area's default, the Code page's panel) never land on it; it is still listed and opens by id.
        var latest = _sessions.Values.Where(s => s.WorkspaceId == workspaceId && !s.IsAssistant).MaxBy(s => s.ActivatedUtc);
        return latest is not null
            && _dismissedLatest.TryGetValue(workspaceId, out var dismissed)
            && dismissed == latest.Id
            ? null
            : latest;
    }

    /// <summary>
    /// The user chose a fresh conversation for this workspace: whatever session was its latest is
    /// dismissed on EVERY surface at once, so the Sessions area and the Code panel agree that the
    /// replaced conversation is gone. The next <see cref="Start"/> lifts the dismissal.
    /// </summary>
    public void StartFresh(string workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return;
        }

        var latest = _sessions.Values.Where(s => s.WorkspaceId == workspaceId && !s.IsAssistant).MaxBy(s => s.ActivatedUtc);
        if (latest is null)
        {
            return;
        }

        _dismissedLatest[workspaceId] = latest.Id;
        SessionsChanged?.Invoke();
    }

    /// <summary>
    /// Work item #359: a conversation to SHOW, by id: the live object when this process holds it, else a detached copy
    /// seeded from its stored transcript and settled to its stored state. The copy is never registered and never runs;
    /// continuing the conversation is a <see cref="Start"/> with <see cref="SessionStartRequest.ResumeOf"/>, which
    /// builds the real one. Null when there is no such conversation.
    /// </summary>
    public LiveAgentSession? ShowOf(AgentSessionId id)
    {
        if (Find(id) is { } live)
        {
            return live;
        }

        if (Settle(_store.Find(id)) is not { } stored)
        {
            return null;
        }

        var view = new LiveAgentSession(stored.Id, stored.WorkspaceId, stored.Title, stored.StartedUtc, stored.CustomTitle);
        view.Seed(_store.Transcript(id));
        view.SettleAsStored(stored.State, stored.EndedUtc);
        return view;
    }

    /// <summary>A workspace's persisted sessions, newest first, for the history list.</summary>
    public IReadOnlyList<SessionRecord> History(string workspaceId, int max = 50) =>
        [.. _store.ForWorkspace(workspaceId, max).Select(r => Settle(r)!)];

    /// <summary>
    /// A stored row as it honestly stands (owner report 2026-09-15). A row still saying Running or Pending
    /// whose session this studio process does not hold has no process behind it: the studio restarted, or
    /// its container was recreated, while the conversation ran, so the pump never saw an ending and the row
    /// said Running for ever. Such a row is corrected where it is read, in the store as well as on screen,
    /// to <see cref="AgentSessionState.Cancelled"/> (the process was ended from outside, which is what that
    /// state already means) with an ending in its transcript that says what happened. Nothing is lost by it:
    /// a conversation with a provider id stays resumable, because resuming asks for that id and not for a
    /// particular ending.
    /// <para>
    /// A session this process holds is never touched, and a live session is in <c>_sessions</c> before its
    /// row is first written, so a row that is merely new cannot be mistaken for an orphan.
    /// </para>
    /// </summary>
    private SessionRecord? Settle(SessionRecord? record)
    {
        if (record is null || record.State.IsTerminal() || Find(record.Id) is not null)
        {
            return record;
        }

        // Under the start gate, re-checked inside it: a resume of this very row may have been launched since
        // the row was read, and saving Cancelled over the Running it saved would end a live conversation on
        // paper. The gate is re-entrant, so Launch's own call through here is fine.
        lock (_startGate)
        {
            if (Find(record.Id) is not null || _store.Find(record.Id) is not { } current || current.State.IsTerminal())
            {
                return Find(record.Id) is { } live ? live.ToRecord() : _store.Find(record.Id) ?? record;
            }

            return SettleLocked(current);
        }
    }

    private SessionRecord SettleLocked(SessionRecord record)
    {
        // Ended when it last said anything, not when someone happened to open the list days later: a stamp of
        // "now" would stretch its duration (backlog 36) by however long the studio was away.
        var endedAt = _store.Transcript(record.Id).LastOrDefault()?.Timestamp ?? record.StartedUtc;
        var ended = record with { State = AgentSessionState.Cancelled, EndedUtc = endedAt };
        // Backlog 218: a restart THIS process knows the cause of (e.g. the launcher recovering a stale DNS
        // resolver) names it in plain words instead of the generic sentence below, read once at startup.
        var reason = _restartReason ?? OrphanedEnding;
        try
        {
            _store.Save(ended);
            _store.Append(record.Id, [new SessionEnded(
                endedAt,
                AgentSessionState.Cancelled,
                record.ProviderSessionId is { Length: > 0 }
                    ? reason + " Resume continues the conversation."
                    : reason)]);
        }
        catch (Exception ex)
        {
            // Shown ended either way: a row that cannot be corrected on disk is still not running.
            _logger.LogWarning(ex, "Could not record that session {SessionId} ended with the studio.", record.Id);
        }

        return ended;
    }

    /// <summary>
    /// What a conversation whose process went away with the studio says about its ending, when nothing more
    /// specific is known (<see cref="_restartReason"/> is null - the ordinary case).
    /// </summary>
    public const string OrphanedEnding =
        "The studio restarted while this conversation was running, so its agent process ended.";

    /// <summary>
    /// The most recent conversations across EVERY workspace, newest first (backlog 34), for the home
    /// surface, which is not bound to one workspace. Read from the STORE rather than merged with the
    /// live set: a session is persisted the instant it starts and re-saved as its state moves, so the
    /// stored row already carries a running session with the state and title the live object has.
    /// </summary>
    public IReadOnlyList<SessionRecord> RecentConversations(int max = 10) =>
        [.. _store.Recent(max).Select(r => Settle(r)!)];

    /// <summary>
    /// Forgets a conversation and its transcript (backlog 34), through the store's own delete. Refused
    /// while the session is still RUNNING here: deleting the transcript out from under a live pump would
    /// leave the CLI writing into rows that no longer exist, and the user has a Stop for that. A session
    /// this process still holds but has finished is dropped from the live set too, so the sessions list
    /// does not keep showing a conversation whose transcript is gone.
    /// </summary>
    public bool Delete(AgentSessionId id)
    {
        // Under the start gate, so a resume cannot bring the conversation back to life between the running
        // check and the removal of the files it would resume from.
        lock (_startGate)
        {
            if (Find(id) is { IsRunning: true })
            {
                return false;
            }

            var provider = Find(id)?.ProviderSessionId ?? _store.Find(id)?.ProviderSessionId;
            _store.Delete(id);
            _sessions.TryRemove(id, out _);
            _storedGraphOnly.TryRemove(id, out _);
            _storedAssistant.TryRemove(id, out _);

            // Decided 2026-09-15: the CLI's conversation file lives exactly as long as the conversation.
            _conversations?.Delete(id, provider);

            // Backlog 84: the tool server's idempotency ledger goes with the conversation too, inside the
            // same gate, so a resume cannot replay a call whose record is being removed.
            _toolLedger?.Forget(id);

            // Backlog 44 and 45: and what it was working on, and under, inside the same gate.
            _work?.Forget(id);
            _plans?.Forget(id);
            _governance?.Forget(id);
            _roles?.Forget(id);
            _branches?.Forget(id);
            _attention?.Forget(id);
        }

        SessionsChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// True when the conversation has a provider id but the CLI's file for it is gone, so it can never be
    /// resumed (owner report 2026-09-15): conversations that ended before the fix had their file deleted at
    /// teardown. False when there is nothing to check against, or when there is no provider id at all.
    /// </summary>
    public bool HistoryRemoved(AgentSessionId id)
    {
        var provider = Find(id)?.ProviderSessionId ?? _store.Find(id)?.ProviderSessionId;
        return provider is { Length: > 0 } && _conversations is not null && !_conversations.Exists(id, provider);
    }

    /// <summary>What a conversation whose CLI history is gone says instead of offering Resume.</summary>
    public const string HistoryRemovedNotice =
        "This conversation can no longer be continued: its agent history was removed when it ended, before this was fixed.";

    /// <summary>The transcript of a session this process no longer holds.</summary>
    public IReadOnlyList<AgentEvent> StoredTranscript(AgentSessionId id) => _store.Transcript(id);

    /// <summary>
    /// Cost and billed tokens across every session in every workspace (backlog 19): today, this calendar
    /// month and all time. Both boundaries are the first instant of the current UTC day and month, taken
    /// from the registry's own clock so they line up with the timestamps sessions are stored under. Read
    /// from the persisted per-session summaries, so it reconciles with what each session's strip reports.
    /// </summary>
    public SessionUsageTotals UsageTotals()
    {
        var now = _clock().ToUniversalTime();
        return _store.ReadUsageTotals(
            new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero));
    }

    /// <summary>
    /// The owner's spend ceilings as they stand (backlog 62), for the surfaces that show them. The
    /// registry is what every session surface already injects, so reading them through it is what keeps
    /// the number the composer shows and the number the gate enforces the same number.
    /// </summary>
    public AgentSpendLimits SpendLimits => _limits.Current();

    /// <summary>
    /// The ceiling that stands in the way of running anything more right now, or null when there is
    /// room (backlog 62). Public because a surface may want to say so BEFORE the user types a turn, and
    /// because the three places that refuse a run all have to mean exactly the same thing by it.
    /// </summary>
    public AgentSpendRefusal? SpendRefusal() => AgentSpendGuard.Refuse(UsageTotals(), _limits.Current());

    public LiveAgentSession Start(SessionStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Images is not { Count: > 0 })
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        }

        ThrowIfTooManyImages(request.Images);
        return Launch(request, continuing: false);
    }

    /// <summary>
    /// True when the conversation can be CONTINUED (backlog 25 increment 6): its CLI process ended because
    /// one turn exhausted its validation attempts, the provider id <c>--resume</c> needs is known, and
    /// nothing else is running in its workspace. Works for a session this process still holds and for one
    /// that is only a stored row, because the ending that makes it resumable is persisted with the transcript.
    /// </summary>
    public bool CanContinue(AgentSessionId id)
    {
        if (Find(id) is { } live)
        {
            return live.IsResumable && Occupying(live.WorkspaceId, live.IsGraphOnly, live.IsAssistant) is null && !HistoryRemoved(id);
        }

        return Settle(_store.Find(id)) is { ProviderSessionId.Length: > 0 } stored
            && _store.Transcript(id).OfType<SessionEnded>().LastOrDefault() is { Resumable: true }
            && OccupyingFor(stored.WorkspaceId, id) is null
            && !HistoryRemoved(id);
    }

    /// <summary>
    /// True when a conversation that is not running can be resumed from the list (owner report 2026-09-15):
    /// the CLI announced the provider id <c>--resume</c> needs, and nothing else is running in its workspace.
    /// Unlike <see cref="CanContinue"/> it does not care HOW the conversation ended; both go through the
    /// same in-place resume.
    /// </summary>
    public bool CanResume(AgentSessionId id)
    {
        if (Find(id) is { } live)
        {
            return !live.IsRunning
                && live.ProviderSessionId is { Length: > 0 }
                && Occupying(live.WorkspaceId, live.IsGraphOnly, live.IsAssistant) is null
                && !HistoryRemoved(id);
        }

        return Settle(_store.Find(id)) is { ProviderSessionId.Length: > 0 } stored
            && OccupyingFor(stored.WorkspaceId, id) is null
            && !HistoryRemoved(id);
    }

    /// <summary>
    /// Resumes a conversation IN PLACE and shows it at once (owner report 2026-09-15): the same session id,
    /// row and transcript, running again under <c>--resume</c> with nothing sent, so the pane shows the
    /// earlier turns with the composer armed and the user's next message continues it through
    /// <see cref="SendAsync"/>. The same path <see cref="Continue"/> takes.
    /// </summary>
    /// <exception cref="InvalidOperationException">The conversation cannot be resumed, or spending is refused.</exception>
    public LiveAgentSession Resume(AgentSessionId id, string workingDirectory, string? model = null, AgentEffort? effort = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        if (!CanResume(id) || _store.Find(id) is not { } stored)
        {
            throw new InvalidOperationException("This conversation cannot be resumed.");
        }

        return Launch(
            new SessionStartRequest(stored.WorkspaceId, workingDirectory, string.Empty, model, id, stored.Title, Effort: effort),
            continuing: true);
    }

    /// <summary>
    /// Continues a conversation whose process ended resumably: the SAME session, under its own id, resumes the
    /// CLI conversation (<c>--resume</c> with its provider id) through the path <see cref="Resume"/> takes, and
    /// sends nothing. The resumed
    /// process waits for the user's next turn, which reaches it through <see cref="SendAsync"/> like any
    /// follow-up. Nothing is resent on the user's behalf: the turn that failed was already tried as many
    /// times as the harness allows.
    /// </summary>
    /// <exception cref="InvalidOperationException">The conversation cannot be continued, or spending is refused.</exception>
    public LiveAgentSession Continue(AgentSessionId id, string workingDirectory, string? model = null, AgentEffort? effort = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        if (!CanContinue(id) || _store.Find(id) is not { } stored)
        {
            throw new InvalidOperationException("This conversation cannot be continued.");
        }

        return Launch(
            new SessionStartRequest(stored.WorkspaceId, workingDirectory, string.Empty, model, id, stored.Title, Effort: effort),
            continuing: true);
    }

    /// <summary>
    /// The model a resumed conversation was last running on, for a resume that names none (backlog 126).
    /// <para>
    /// A pick always wins; this only answers "nobody picked". It used to answer that with nothing, so the
    /// CLI fell back to its own default: seen on the installed 0.9.24, a conversation started on Haiku came
    /// back from a container restart as <c>claude ... --resume id</c> with no <c>--model</c> at all, because
    /// the pane's model field starts empty on every page load and was the only thing asked. A resumed
    /// conversation is the SAME conversation (owner, 2026-09-15), and the model is part of what it is. No
    /// new storage is needed: every start already records what the CLI registered, and it is the dated id,
    /// which <c>--model</c> accepts and which pins the exact model rather than re-resolving an alias.
    /// </para>
    /// A fresh conversation has no record and gets null, which is the CLI's default, as before.
    /// </summary>
    private string? RecordedModel(SessionRecord? resumed, LiveAgentSession session)
    {
        if (resumed is null)
        {
            return null;
        }

        if (session.Model is { Length: > 0 } live)
        {
            return live;
        }

        return _store.Transcript(resumed.Id)
            .OfType<SessionRegistered>()
            .Select(registered => registered.Model)
            .LastOrDefault(model => !string.IsNullOrWhiteSpace(model));
    }

    private LiveAgentSession Launch(SessionStartRequest request, bool continuing)
    {
        LiveAgentSession session;
        AgentSessionRequest runnerRequest;
        bool effortMoved;

        // Backlog 62: before anything is created. A refusal here is the same sentence the composer would
        // show for a turn and the same one the transcript gets at a turn boundary, because all three
        // read one guard over one set of totals.
        if (SpendRefusal() is { } refusal)
        {
            throw new InvalidOperationException(refusal.Message);
        }

        bool graphOnly;
        bool graphOnlyNew;
        bool assistant;
        bool assistantNew;

        lock (_startGate)
        {
            // Work item #352: a graph-only conversation stays graph-only. A resume of one is graph-only whatever the
            // request says (nothing can upgrade it), and a request can ask for graph-only on any start. Decided
            // before the slot check, because which slot a start needs depends on it.
            //
            // Work item #359: the studio assistant is its own kind with its own slot, decided the same way: a resume of
            // one is the assistant whatever the request says, and an assistant is never also graph-only. A resumed
            // graph-only conversation keeps its kind rather than becoming the assistant.
            var wasGraphOnly = request.ResumeOf is { } earlier && IsGraphOnly(earlier);
            var wasAssistant = request.ResumeOf is { } earlierAssistant && IsAssistant(earlierAssistant);
            assistant = wasAssistant || (request.Assistant && !wasGraphOnly);
            assistantNew = assistant && !wasAssistant;
            graphOnly = !assistant && (request.GraphOnly || wasGraphOnly);
            graphOnlyNew = graphOnly && !wasGraphOnly;

            if (assistant && RunningAssistantFor(request.WorkspaceId) is not null)
            {
                throw new InvalidOperationException(AssistantAlreadyRunning);
            }

            if (graphOnly && RunningGraphOnlyFor(request.WorkspaceId) is not null)
            {
                throw new InvalidOperationException(GraphOnlyAlreadyRunning);
            }

            if (!graphOnly && !assistant && RunningFor(request.WorkspaceId) is not null)
            {
                throw new InvalidOperationException(
                    "This workspace already has a session running. Stop it before starting another.");
            }

            // Only a conversation the CLI can actually continue is resumed; one that never announced a
            // provider id has nothing for --resume to name, so asking to resume it starts a fresh one.
            var resumed = request.ResumeOf is { } previous
                && Settle(_store.Find(previous)) is { ProviderSessionId.Length: > 0 } found
                && Find(previous) is not { IsRunning: true }
                    ? found
                    : null;

            if (resumed is not null && HistoryRemoved(resumed.Id))
            {
                // Starting the CLI would only fail with its own "No conversation found" a moment later.
                throw new InvalidOperationException(HistoryRemovedNotice);
            }

            if (resumed is not null)
            {
                // Backlog 229: a person who resumes a conversation waiting for the network has continued it, so
                // the wait must not continue it a second time. The wait's own resume claimed its wait first.
                EndNetworkWait(resumed.Id);

                // Owner report 2026-09-15: a resumed conversation is ONE conversation. It continues under
                // its OWN id, so it keeps its row in every list, its title, its stored transcript (the new
                // turns append to it) and its config home on the data volume, which is where the CLI keeps
                // the conversation file --resume has to find. Reusing the id is safe for the approval
                // channel because nothing about it outlives a process: the runner releases the id's broker
                // registration and deletes its hook script when the previous process ends, and registering
                // the id again mints a NEW secret and rewrites the script (a re-registration revokes the old
                // secret, AgentApprovalBroker.RegisterSession). One process per id is already guaranteed by
                // the one-running-session-per-workspace gate above, which a resume passes through too.
                if (Find(resumed.Id) is { } held)
                {
                    session = held;
                }
                else
                {
                    session = new LiveAgentSession(
                        resumed.Id, resumed.WorkspaceId, resumed.Title, resumed.StartedUtc, resumed.CustomTitle);
                    session.Seed(_store.Transcript(resumed.Id));
                }

                session.Reopen(_clock());
                session.AdoptProviderSession(resumed.ProviderSessionId!);
            }
            else
            {
                session = new LiveAgentSession(
                    AgentSessionId.New(),
                    request.WorkspaceId,
                    request.Title ?? TitleFrom(request.Prompt),
                    _clock());
            }

            _sessions[session.Id] = session;

            // The new session IS the fresh conversation the dismissal was waiting for.
            _dismissedLatest.TryRemove(request.WorkspaceId, out _);

            // The agent works in the workspace's contained copy; warming it is idempotent.
            _sync?.EnsureStarted(request.WorkspaceId);

            runnerRequest = new AgentSessionRequest(
                session.Id,
                request.WorkspaceId,
                request.WorkingDirectory,
                request.Prompt,
                resumed?.ProviderSessionId,
                request.Model ?? RecordedModel(resumed, session),
                _documentKinds,
                // Backlog 62: the per-session ceiling rides with the request, so the CLI enforces it from
                // inside the invocation. Read at START, because that is the invocation it applies to;
                // lowering it later governs the NEXT session, not this one's already-running process.
                _limits.Current().PerSessionUsd,
                // Backlog 26: images the composer attached ride with the first turn, or nothing at all.
                request.Images is { Count: > 0 } images ? images : null)
            {
                // Requested 2026-10-22: a pick wins; otherwise a resumed conversation keeps the effort it was last
                // at (seeded from its own EffortChosen rows above), and a fresh one has none, which passes no flag.
                Effort = request.Effort ?? (resumed is null ? null : session.Effort),

                // Work item #352: the runner narrows the CLI's built-in tools, holds the session at the broker and
                // briefs the first turn from this one flag; a model or effort restart is a `with` copy and keeps it.
                GraphOnly = graphOnly,

                // Work item #359: the same one flag for the studio assistant.
                Assistant = assistant,
            };

            effortMoved = runnerRequest.Effort != session.Effort;

            // Work item #352: marked on the live object INSIDE the gate, so the slot checks above see it at once and a
            // second graph-only start racing this one is refused. The transcript row follows once the row is saved.
            if (graphOnly)
            {
                if (!session.IsGraphOnly)
                {
                    session.Apply(new GraphOnlyChosen(_clock()));
                }

                _broker.HoldToGraphOnly(session.Id);
            }

            // Work item #359: the studio assistant is marked the same way, inside the gate.
            if (assistant)
            {
                if (!session.IsAssistant)
                {
                    session.Apply(new AssistantChosen(_clock()));
                }

                _broker.HoldToAssistant(session.Id);
            }
        }

        _store.Save(session.ToRecord());

        // The mark is persisted once, on the conversation's first graph-only start; a resume reads it back (Seed).
        if (graphOnlyNew)
        {
            PersistOnly(session, session.Transcript.OfType<GraphOnlyChosen>().Last());
        }

        if (assistantNew)
        {
            PersistOnly(session, session.Transcript.OfType<AssistantChosen>().Last());
        }

        SessionsChanged?.Invoke();

        // The effort is the studio's own record (the CLI reports none), so a start that sets or moves it says so in
        // the transcript, where the next resume reads it back. A start that leaves it where it was writes nothing.
        // After the row is saved, so the transcript row always has a session to belong to.
        if (effortMoved)
        {
            Record(session, new EffortChosen(_clock(), runnerRequest.Effort));
        }

        // Backlog 235: which broker token the process about to start will carry, read BEFORE it builds its environment,
        // so a token minted in between makes the record older than the process, never newer (an extra restart later,
        // never a stale token taken for a fresh one).
        session.CredentialTokenId = _credentials?.CurrentTokenId();

        if (!continuing)
        {
            // The turn's prompt belongs in the transcript before the agent answers it, under whoever wrote it.
            if (request.FromStudio)
            {
                // Backlog 52: a studio prompt can carry an image the person chose (a drawing to turn into a
                // design), and the transcript shows it above the words, as it does for a person's own turn.
                if (request.Images is { Count: > 0 } studioImages)
                {
                    Record(session, new UserImagesAttached(_clock(), [.. studioImages]));
                }

                Record(session, new MessageEmitted(_clock(), TranscriptRole.StudioPrompt, request.Prompt));
            }
            else
            {
                RecordUserTurn(session, request.Prompt, request.Images);
            }

            // Backlog 20: the gap the owner reported is between pressing send and the first byte coming
            // back, which is a window the CLI says nothing in. The studio knows the turn was submitted, so
            // the studio is what says so; the CLI's own thinking and tool phases replace this the moment
            // they arrive. A Continue submits nothing, so there is nothing to be working on yet.
            session.Apply(new TurnActivity(_clock(), AgentTurnPhase.Submitted));
        }

        // Started on its own thread rather than queued on the thread pool: a session pump lives for
        // the whole conversation (minutes, sometimes hours), and queueing it behind pool work meant a
        // busy machine could leave a started session sitting there, not started at all.
        _ = Task.Factory.StartNew(
            () => PumpAsync(session, runnerRequest),
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);

        return session;
    }

    public Task<bool> SendAsync(AgentSessionId id, string text, CancellationToken cancellationToken = default) =>
        SendAsync(id, text, null, cancellationToken);

    /// <summary>
    /// Sends a follow-up turn, with the images pasted into it when there are any (backlog 26). The images are
    /// recorded with the turn, so the transcript still shows them after a reload.
    /// </summary>
    public Task<bool> SendAsync(
        AgentSessionId id,
        string text,
        IReadOnlyList<AgentImageAttachment>? images,
        CancellationToken cancellationToken = default)
    {
        var hasImages = images is { Count: > 0 };
        if (Find(id) is not { } session || !session.IsRunning || (string.IsNullOrWhiteSpace(text) && !hasImages))
        {
            return Task.FromResult(false);
        }

        ThrowIfTooManyImages(images);

        // Backlog 62: a new turn is new spending, so it is refused exactly as a new session is. Thrown
        // rather than returned false, because false here means "there is no session to send to" and the
        // user would be told nothing at all; the surfaces that send turns already show this message.
        if (SpendRefusal() is { } refusal)
        {
            throw new InvalidOperationException(refusal.Message);
        }

        // Backlog 229: the person's own turn is the continuation, so the studio adds none of its own.
        EndNetworkWait(session.Id);

        // Backlog 235: a process started before the broker token was renewed still carries the old one, and would send
        // this turn on it. It is restarted first; the runner holds the turn until the new process is there.
        HandFreshTokenIfStale(session);
        RecordUserTurn(session, text, images);
        session.Apply(new TurnActivity(_clock(), AgentTurnPhase.Submitted));
        return hasImages
            ? _runner.SendAsync(id, text, images!, cancellationToken)
            : _runner.SendAsync(id, text, cancellationToken);
    }

    /// <summary>
    /// The user's turn, as the transcript keeps it: pasted images first (their own row, so the stored shape of
    /// a plain message never changed), then the words. Surfaces fold the images into the bubble below them.
    /// </summary>
    private void RecordUserTurn(LiveAgentSession session, string text, IReadOnlyList<AgentImageAttachment>? images)
    {
        if (images is { Count: > 0 })
        {
            Record(session, new UserImagesAttached(_clock(), [.. images]));
        }

        var at = _clock();
        Record(session, new MessageEmitted(at, TranscriptRole.User, text ?? string.Empty));

        // Backlog 244: every turn a PERSON typed is counted; the earliest is the opening prompt and every later one is
        // guidance. A studio prompt never comes through here, so it is never counted as either.
        try
        {
            _attention?.Record(AttentionEntry.Turn(session.Id, at));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The studio could not count a turn of session {SessionId}.", session.Id);
        }
    }

    private static void ThrowIfTooManyImages(IReadOnlyList<AgentImageAttachment>? images)
    {
        if (images is { Count: > AgentImageAttachment.MaxPerTurn })
        {
            throw new InvalidOperationException(
                $"A turn can carry at most {AgentImageAttachment.MaxPerTurn} images.");
        }
    }

    /// <summary>
    /// Stops the conversation's process, and any wait for the network with it (backlog 229), so nothing is sent
    /// afterwards. A wait whose process had already ended has nothing to stop but the wait itself, and the
    /// conversation stays resumable.
    /// </summary>
    public bool Stop(AgentSessionId id)
    {
        if (EndNetworkWait(id) && Find(id) is { IsRunning: false } ended)
        {
            Record(ended, new MessageEmitted(_clock(), TranscriptRole.System, NetworkWait.StoppedNotice));
            return true;
        }

        return _runner.Stop(id);
    }

    /// <summary>
    /// Moves a running conversation onto another model (null means back to the CLI's own default). The
    /// runner ends the CLI child and resumes the same conversation in a new one, and the transcript
    /// gets a studio notice where the switch happened. Only BETWEEN turns: mid-turn the child is busy
    /// answering and ending it would throw work away, so the composer keeps the picker locked while a
    /// turn is in flight and this refusal is the backstop behind it.
    /// </summary>
    public bool ChangeModel(AgentSessionId id, string? model)
    {
        if (Find(id) is not { } session || !session.IsRunning || session.TurnInFlight)
        {
            return false;
        }

        return _runner.ChangeModel(id, model);
    }

    /// <summary>
    /// Moves a running conversation onto another effort level (requested 2026-10-22, model and effort per work
    /// item; null means back to the CLI's own default). The same restart and the same between-turns rule as
    /// <see cref="ChangeModel"/>, and the model stays where it is. The transcript records the new level, because
    /// the CLI reports none and a later resume that names none reads it back from there.
    /// </summary>
    public bool ChangeEffort(AgentSessionId id, AgentEffort? effort)
    {
        if (Find(id) is not { } session || !session.IsRunning || session.TurnInFlight)
        {
            return false;
        }

        if (!_runner.ChangeEffort(id, effort))
        {
            return false;
        }

        Record(session, new EffortChosen(_clock(), effort));
        return true;
    }

    /// <summary>
    /// Renames a conversation to a title the owner chose (backlog 109). The CUSTOM title is set and the
    /// derived one stays as the fallback; the store is written so the name survives a restart, and the
    /// list is told. Works on a session this process holds and on one that is only a stored row (the
    /// list offers both). False, with nothing written and nobody told, when the title is blank, when it
    /// is the name already shown, or when the id is unknown to the live set and the store alike.
    /// </summary>
    public bool Rename(AgentSessionId id, string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var trimmed = title.Trim();

        if (Find(id) is { } live)
        {
            if (!live.Rename(trimmed))
            {
                return false;
            }

            _store.Save(live.ToRecord());
            SessionsChanged?.Invoke();
            return true;
        }

        if (_store.Find(id) is not { } stored || string.Equals(stored.DisplayTitle, trimmed, StringComparison.Ordinal))
        {
            return false;
        }

        _store.Save(stored with { CustomTitle = trimmed });
        SessionsChanged?.Invoke();
        return true;
    }

    /// <summary>Answers a pending tool call. False when the request is unknown or already answered.</summary>
    public bool Answer(string requestId, bool approved, string reason) =>
        _broker.Answer(requestId, approved, reason);

    public void Dispose()
    {
        _broker.Requested -= OnApprovalRequested;
        _broker.Resolved -= OnApprovalResolved;
        if (_toolServer is not null)
        {
            _toolServer.ClientRefused -= OnToolClientRefused;
        }

        _broker.Refused -= OnToolRefused;
        _broker.Reviewed -= OnCommitReviewed;

        foreach (var id in _networkWaits.Keys)
        {
            EndNetworkWait(id);
        }
    }

    /// <summary>
    /// Backlog 84, run once at studio start: a studio tool call the previous process started and never finished
    /// is resolved in the ledger as interrupted, and the conversation it belonged to is TOLD, in the studio's own
    /// voice, that nobody can say whether it took effect. Returns how many were found.
    /// </summary>
    public int RecoverInterruptedToolCalls()
    {
        if (_toolLedger is null)
        {
            return 0;
        }

        IReadOnlyList<InterruptedToolCall> interrupted;
        try
        {
            interrupted = _toolLedger.RecoverInterrupted();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not sweep the tool ledger for calls a previous studio process left open.");
            return 0;
        }

        foreach (var call in interrupted)
        {
            _logger.LogWarning(
                "Studio tool call {Tool} in session {SessionId} was running when the studio stopped; recorded as interrupted.",
                call.ToolName,
                call.SessionId);

            if (_store.Find(call.SessionId) is null)
            {
                continue;
            }

            try
            {
                _store.Append(call.SessionId, [new MessageEmitted(
                    _clock(),
                    TranscriptRole.System,
                    InterruptedToolCallNotice(call.ToolName))]);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not record an interrupted tool call on session {SessionId}.", call.SessionId);
            }
        }

        return interrupted.Count;
    }

    /// <summary>
    /// Resumes, at studio start, every conversation the previous process left MID-TURN (backlog 156): the container
    /// was replaced by an upgrade or restarted by a crash while an agent was working. A conversation that was idle
    /// is left as it is (its row is settled to an ending when it is next read, and a person can resume it); one whose
    /// last stored events after its last completed turn are a person's prompt or a tool call had work in flight, and
    /// that work is picked up rather than abandoned: the transcript is told what happened, and the agent is asked
    /// to check what its last step left behind before it goes on. Returns how many were resumed.
    /// <para>
    /// Everything that guards an ordinary start guards this one: the spend ceilings, one running session per
    /// workspace, a provider id the CLI can resume. A workspace with no contained copy (a dev host, or the sync
    /// engine not wired) resumes nothing and says so in the log.
    /// </para>
    /// </summary>
    public int ResumeInterruptedConversations()
    {
        IReadOnlyList<SessionRecord> unfinished;
        try
        {
            unfinished = _store.Unfinished();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read which conversations the previous studio process left running.");
            return 0;
        }

        var resumed = 0;
        foreach (var record in unfinished)
        {
            if (Find(record.Id) is not null || record.ProviderSessionId is not { Length: > 0 })
            {
                continue;
            }

            IReadOnlyList<AgentEvent> transcript;
            try
            {
                transcript = _store.Transcript(record.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read the transcript of session {SessionId} to see whether it was mid-turn.", record.Id);
                continue;
            }

            // Backlog 157: a conversation that asked for the upgrade ended its turn first, so it is not mid-turn, and it
            // is resumed for that reason instead, with a note saying what happened rather than "your step may be half done".
            var upgradedTo = _upgradeSchedule?.TakeResume(record.Id);
            if (upgradedTo is null && !WasMidTurn(transcript))
            {
                continue;
            }

            _sync?.EnsureStarted(record.WorkspaceId);
            if (_sync?.CopyPathFor(record.WorkspaceId) is not { Length: > 0 } copy)
            {
                _logger.LogWarning(
                    "Session {SessionId} was mid-turn when the studio stopped, but workspace {WorkspaceId} has no contained copy here, so it is not resumed.",
                    record.Id, record.WorkspaceId);
                continue;
            }

            try
            {
                // The previous process's ending is recorded FIRST, so the transcript reads in the order things
                // happened: that run stopped, the studio came back, the conversation goes on. Recorded by the
                // resume instead, it landed after the note and read "Session stopped." under "resumed" (seen with
                // the first agent-scheduled upgrade, 2026-10-02).
                Settle(_store.Find(record.Id));
                _store.Append(record.Id, [new MessageEmitted(
                    _clock(), TranscriptRole.System,
                    upgradedTo is null ? InterruptedTurnNoticeFor(_restartReason) : UpgradedNotice(upgradedTo))]);
                Start(new SessionStartRequest(
                    record.WorkspaceId, copy,
                    upgradedTo is null ? InterruptedTurnPromptFor(_restartReason) : UpgradedPrompt(upgradedTo), ResumeOf: record.Id,
                    FromStudio: true));
                resumed++;
                _logger.LogInformation("Resumed session {SessionId}, which was mid-turn when the studio stopped.", record.Id);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                // A ceiling, another session already running in the folder, or a conversation the CLI cannot find:
                // each is its own honest refusal, and the row is left for a person exactly as before.
                _logger.LogWarning(ex, "Session {SessionId} was mid-turn when the studio stopped and could not be resumed.", record.Id);
            }
        }

        return resumed;
    }

    /// <summary>
    /// Whether a stored transcript ends inside a turn: after its last completed turn there is a person's prompt or
    /// a tool call. Nothing after the last turn, or only the agent's finished answer, is an idle conversation.
    /// </summary>
    public static bool WasMidTurn(IReadOnlyList<AgentEvent> transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        var midTurn = false;
        foreach (var e in transcript)
        {
            switch (e)
            {
                case TurnCompleted or SessionEnded:
                    midTurn = false;
                    break;
                case MessageEmitted { Role: TranscriptRole.User or TranscriptRole.StudioPrompt } or ToolCallStarted:
                    midTurn = true;
                    break;
            }
        }

        return midTurn;
    }

    /// <summary>What the transcript says when a conversation is resumed by the studio after a restart.</summary>
    public const string InterruptedTurnNotice =
        "The studio restarted while this conversation was working (an upgrade, or the container stopped). It is resumed "
        + "automatically, and the agent is asked to check its last step before continuing.";

    /// <summary>The prompt the resumed agent gets: check, then go on.</summary>
    public const string InterruptedTurnPrompt =
        "The studio restarted while you were working, so your last step may be half done. Check what it left behind "
        + "(files, tool calls, the work item), then continue where you were.";

    /// <summary>
    /// The notice for a resume after a restart whose cause this process knows (backlog 218: the launcher's DNS
    /// recovery leaves one). The ending recorded just before it carries the same reason, but the transcript shows a
    /// stopped session only as "Session stopped.", so this line is where the person reads it in plain words.
    /// </summary>
    public static string InterruptedTurnNoticeFor(string? restartReason) => restartReason is null
        ? InterruptedTurnNotice
        : restartReason + " This conversation was working at the time. It is resumed automatically, and the agent is "
            + "asked to check its last step before continuing.";

    /// <summary>
    /// The resumed agent's prompt when the restart's cause is known, so the agent is told why in plain words rather
    /// than left with whatever its last tool call failed with (backlog 218: every call had been failing to resolve).
    /// </summary>
    public static string InterruptedTurnPromptFor(string? restartReason) => restartReason is null
        ? InterruptedTurnPrompt
        : restartReason + " You were working at the time, so your last step may be half done. Check what it left behind "
            + "(files, tool calls, the work item), then continue where you were.";

    /// <summary>What the transcript says when a conversation is resumed on the upgrade it asked for (backlog 157).</summary>
    public static string UpgradedNotice(string version) =>
        $"The studio was upgraded to {version}, as this conversation asked, and the conversation is resumed on the new build.";

    /// <summary>The prompt the resumed agent gets after its upgrade: confirm, then go on.</summary>
    public static string UpgradedPrompt(string version) =>
        $"The studio was upgraded to {version} at your request (studio_upgrade) and this conversation was resumed on the new "
        + "build. Call studio_version to confirm what is running now, then continue where you were.";

    /// <summary>The transcript line for a studio tool call a restart cut off.</summary>
    public static string InterruptedToolCallNotice(string toolName) =>
        $"The studio stopped while the tool {StudioToolServer.CliToolName(toolName)} was running, so it cannot tell "
        + "whether that call took effect. It will not be run again under the same call.";

    private void OnToolClientRefused(AgentSessionId sessionId, string sentence)
    {
        // Once per session: a CLI that reconnects would otherwise repeat the same line on every attempt.
        if (Find(sessionId) is not { } session || !_toolRefusalShown.TryAdd(sessionId, true))
        {
            return;
        }

        Record(session, new MessageEmitted(_clock(), TranscriptRole.System, sentence));
    }

    // ---- the network (backlog 229, plan @55) ----------------------------------------------

    /// <summary>
    /// The person's "Retry now" while a conversation waits for the network: one probe, at once. Only an answer of
    /// reachable continues the conversation (through the same path the wait itself takes), so this never sends into a
    /// network still known to be down. True when it continued.
    /// </summary>
    public async Task<bool> RetryNetworkNowAsync(AgentSessionId id, CancellationToken cancellationToken = default)
    {
        if (Find(id) is not { } session || !_networkWaits.TryGetValue(id, out var ticket))
        {
            return false;
        }

        // Backlog 235: a wait for a fresh broker token has no probe of its own; asking the broker for a token is the check.
        if (session.NetworkWait is not { Reason: WaitReason.Credentials })
        {
            if (_reachability is null)
            {
                return false;
            }

            var state = await _reachability.ProbeNowAsync(cancellationToken).ConfigureAwait(false);
            if (state.Reachable != true)
            {
                if (ticket.IsOpen)
                {
                    session.UpdateNetworkWait(current => current with { Probe = state });
                }

                return false;
            }
        }

        return await TokenThenContinueAsync(session, ticket, now: true, cancellationToken).ConfigureAwait(false) == true;
    }

    /// <summary>
    /// The network answers (or the wait is for a token): where the broker carries the runtime, a conversation continues
    /// only on a token that is valid and is not the one the broker refused (backlog 235), so a token that expired during
    /// the outage is minted again first. True when it continued, false when it stopped (a refusal, said in words), null
    /// while it still waits, with the wait's words updated.
    /// </summary>
    private async Task<bool?> TokenThenContinueAsync(
        LiveAgentSession session, NetworkWaitTicket ticket, bool now, CancellationToken cancellationToken)
    {
        if (_credentials is null)
        {
            return await ContinueAfterNetworkAsync(session, ticket).ConfigureAwait(false);
        }

        var renewal = await _credentials
            .FreshAsync(session.NetworkWait?.RefusedTokenId, now, cancellationToken)
            .ConfigureAwait(false);
        switch (renewal.State)
        {
            case TokenRenewalState.Fresh:
                return await ContinueAfterNetworkAsync(session, ticket).ConfigureAwait(false);

            case TokenRenewalState.Refused:
                StopWaitingRefused(session, ticket, renewal.Words);
                return false;

            default:
                // The network is back but the token is not: from here on this is a wait for a token, in its words.
                if (ticket.IsOpen)
                {
                    session.UpdateNetworkWait(current => current with
                    {
                        Reason = WaitReason.Credentials,
                        Credential = renewal.Words ?? current.Credential,
                        CredentialOutage = renewal.Words is null ? current.CredentialOutage : renewal.Outage,
                    });
                }

                return null;
        }
    }

    /// <summary>
    /// A process started before the broker token was renewed is restarted on the current one (backlog 235), between
    /// turns only: the callers are the two that are about to write a turn. Nothing happens where the broker does not
    /// carry the runtime, while no valid token is stored, or when the process already has the current one.
    /// </summary>
    private void HandFreshTokenIfStale(LiveAgentSession session)
    {
        if (_credentials is null || !session.IsRunning || session.TurnInFlight)
        {
            return;
        }

        var current = _credentials.CurrentTokenId();
        if (current is null || string.Equals(current, session.CredentialTokenId, StringComparison.Ordinal))
        {
            return;
        }

        if (_runner.RefreshCredentials(session.Id))
        {
            session.CredentialTokenId = current;
            _logger.LogInformation(
                "Session {SessionId}'s agent process restarts to take the renewed broker token {TokenId}.", session.Id, current);
        }
    }

    /// <summary>The broker refused the fresh token: the wait ends once, in words, and nothing asks again by itself.</summary>
    private void StopWaitingRefused(LiveAgentSession session, NetworkWaitTicket ticket, string? words)
    {
        if (!ticket.TryClaim())
        {
            return;
        }

        _networkWaits.TryRemove(new KeyValuePair<AgentSessionId, NetworkWaitTicket>(session.Id, ticket));
        var ended = session.NetworkWait?.ProcessEnded ?? !session.IsRunning;
        session.SetNetworkWait(null);
        Record(session, new MessageEmitted(
            _clock(), TranscriptRole.System, NetworkWait.TokenRefusedStopNotice(words ?? "the broker refused to mint a token", ended)));
        _logger.LogInformation("Session {SessionId} stopped waiting for a broker token: the broker refused to mint one.", session.Id);
    }

    /// <summary>
    /// Starts waiting for the network for a conversation whose turn (or process) failed on it. Nothing is sent now:
    /// the wait sends a new prompt once the model's host answers. A wait already running only learns that the
    /// process has gone too. No wait starts past <see cref="NetworkWait.MaxContinues"/> automatic continues in a row,
    /// or while spending is refused, and the transcript says why.
    /// </summary>
    private void BeginNetworkWait(
        LiveAgentSession session,
        string workingDirectory,
        NetworkFailure failure,
        bool processEnded,
        WaitReason reason = WaitReason.Network)
    {
        // A network wait needs the probe; a wait for a broker token (backlog 235) needs the broker.
        if (reason == WaitReason.Network ? _reachability is null : _credentials is null)
        {
            return;
        }

        var host = _reachability?.Current.Target.Host ?? "the credential broker";
        if (_networkWaits.TryGetValue(session.Id, out _))
        {
            if (processEnded)
            {
                session.UpdateNetworkWait(current => current with { ProcessEnded = true });
            }

            return;
        }

        if (session.NetworkContinues >= NetworkWait.MaxContinues)
        {
            Record(session, new MessageEmitted(
                _clock(),
                TranscriptRole.System,
                reason == WaitReason.Credentials
                    ? NetworkWait.TooManyTokenNotice(processEnded)
                    : NetworkWait.TooManyNotice(failure, host, processEnded)));
            return;
        }

        if (SpendRefusal() is not null)
        {
            // The spend stop has said its own sentence and is ending the session; continuing would only be refused.
            return;
        }

        var ticket = new NetworkWaitTicket(workingDirectory);
        if (!_networkWaits.TryAdd(session.Id, ticket))
        {
            return;
        }

        var credentialWait = reason == WaitReason.Credentials;
        session.SetNetworkWait(new NetworkWait(
            _clock(),
            failure,
            host,
            session.NetworkContinues + 1,
            processEnded,
            Reason: reason,
            RefusedTokenId: credentialWait ? session.CredentialTokenId : null));
        if (!processEnded)
        {
            // A process that ended says the same in its ending; a live one says it here.
            Record(session, new MessageEmitted(
                _clock(),
                TranscriptRole.System,
                credentialWait ? NetworkWait.TokenRefusedNotice : NetworkWait.DroppedNotice(failure, host)));
        }

        if (credentialWait)
        {
            _logger.LogInformation(
                "Session {SessionId} is waiting for a fresh broker token: the broker refused token {TokenId}.",
                session.Id, session.CredentialTokenId ?? "(unknown)");
        }
        else
        {
            _logger.LogInformation(
                "Session {SessionId} is waiting for the network: {Failure} ({Detail}).", session.Id, failure.Kind, failure.Detail);
        }

        _ = Task.Run(() => WaitForNetworkAsync(session, ticket), CancellationToken.None);
    }

    /// <summary>
    /// Probes the model's host on the reachability probe's own backoff (5 s doubling to 60 s) until it answers, then
    /// continues the conversation; gives up after <see cref="NetworkWait.GiveUpAfter"/>. Never continues before a
    /// probe has answered reachable.
    /// <para>
    /// Backlog 235: where the broker carries the runtime, an answering network is not enough. The conversation continues
    /// only once a valid token is stored that is not the one the broker refused, minted on the way if needed
    /// (<see cref="TokenThenContinueAsync"/>). A wait for a token asks the broker on the token's own backoff (15 s
    /// doubling to 2 minutes) instead of probing, because a mint attempt is itself the check.
    /// </para>
    /// </summary>
    private async Task WaitForNetworkAsync(LiveAgentSession session, NetworkWaitTicket ticket)
    {
        try
        {
            while (ticket.IsOpen)
            {
                if (session.NetworkWait is not { } waiting)
                {
                    return;
                }

                DateTimeOffset? next;
                var sawNetwork = false;
                if (waiting.Reason == WaitReason.Network)
                {
                    var state = await _reachability!.ProbeNowAsync(ticket.Token).ConfigureAwait(false);
                    if (state.Reachable == true)
                    {
                        sawNetwork = true;
                    }
                    else if (!ticket.IsOpen || session.UpdateNetworkWait(wait => wait with { Probe = state }) is null)
                    {
                        return;
                    }

                    next = state.NextProbeAt;
                }
                else
                {
                    next = null;
                }

                if (sawNetwork || waiting.Reason == WaitReason.Credentials)
                {
                    var outcome = await TokenThenContinueAsync(session, ticket, now: sawNetwork, ticket.Token)
                        .ConfigureAwait(false);
                    if (outcome is not null)
                    {
                        return;
                    }

                    next = _credentials?.NextAttemptAt;
                }

                if (!ticket.IsOpen || session.NetworkWait is not { } current)
                {
                    return;
                }

                var now = _clock();
                var remaining = current.Since + NetworkWait.GiveUpAfter - now;
                if (remaining <= TimeSpan.Zero)
                {
                    GiveUpWaiting(session, ticket);
                    return;
                }

                var pause = (next ?? now + (current.Reason == WaitReason.Credentials
                    ? Application.AgentRuntimes.Broker.BrokerTokenHealth.FirstRetry
                    : Network.NetworkReachability.FirstRetry)) - now;
                pause = pause < MinimumProbePause ? MinimumProbePause : pause;
                await _delay(pause > remaining ? remaining : pause, ticket.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped, or the person sent a turn: the wait simply ends.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The wait for the network of session {SessionId} ended unexpectedly.", session.Id);
            if (ticket.TryClaim())
            {
                _networkWaits.TryRemove(new KeyValuePair<AgentSessionId, NetworkWaitTicket>(session.Id, ticket));
                session.SetNetworkWait(null);
            }
        }
    }

    // A probe answer whose next time is already past (a slow probe) still pauses, so the loop cannot spin.
    private static readonly TimeSpan MinimumProbePause = TimeSpan.FromSeconds(1);

    private void GiveUpWaiting(LiveAgentSession session, NetworkWaitTicket ticket)
    {
        if (!ticket.TryClaim())
        {
            return;
        }

        _networkWaits.TryRemove(new KeyValuePair<AgentSessionId, NetworkWaitTicket>(session.Id, ticket));
        var ended = session.NetworkWait?.ProcessEnded ?? !session.IsRunning;
        var reason = session.NetworkWait?.Reason ?? WaitReason.Network;
        session.SetNetworkWait(null);
        Record(session, new MessageEmitted(_clock(), TranscriptRole.System, NetworkWait.GaveUpNotice(ended, reason)));
        _logger.LogInformation("Session {SessionId} stopped waiting for the network after {Minutes} minutes.",
            session.Id, NetworkWait.GiveUpAfter.TotalMinutes);
    }

    /// <summary>
    /// Continues a conversation the network interrupted, once and only once per wait (the ticket is claimed, so the
    /// wait and a "Retry now" cannot both send). A NEW studio prompt is sent, never the failed turn again: that turn is
    /// already in the CLI's conversation, and the prompt tells the agent its last step may be half done. A live
    /// process gets it as its next turn; an ended one is resumed with <c>--resume</c> through <see cref="Start"/>, which
    /// carries every guard an ordinary start has. Nothing is sent while the conversation is already moving again.
    /// </summary>
    private async Task<bool> ContinueAfterNetworkAsync(LiveAgentSession session, NetworkWaitTicket ticket)
    {
        if (!ticket.TryClaim())
        {
            return false;
        }

        _networkWaits.TryRemove(new KeyValuePair<AgentSessionId, NetworkWaitTicket>(session.Id, ticket));
        var wait = session.NetworkWait;
        session.SetNetworkWait(null);
        if (wait is null)
        {
            return false;
        }

        var prompt = wait.ContinueWith;
        if (session.IsRunning)
        {
            if (session.TurnInFlight || session.Pending.Count > 0)
            {
                return false;
            }

            if (SpendRefusal() is { } refusal)
            {
                Record(session, new MessageEmitted(_clock(), TranscriptRole.System, refusal.Message));
                return false;
            }

            session.CountNetworkContinue();
            Record(session, new MessageEmitted(_clock(), TranscriptRole.System, wait.BackWords));

            // Backlog 235: the process may still hold the token the broker refused, or one that expired during the
            // outage. It is restarted on the fresh one first, and the runner holds the prompt until it is there.
            HandFreshTokenIfStale(session);
            Record(session, new MessageEmitted(_clock(), TranscriptRole.StudioPrompt, prompt));
            session.Apply(new TurnActivity(_clock(), AgentTurnPhase.Submitted));
            if (await _runner.SendAsync(session.Id, prompt).ConfigureAwait(false))
            {
                return true;
            }

            Record(session, new MessageEmitted(
                _clock(), TranscriptRole.System, "The network is back, but the agent process did not take the prompt."));
            return false;
        }

        session.CountNetworkContinue();
        Record(session, new MessageEmitted(_clock(), TranscriptRole.System, wait.BackWords));
        try
        {
            Start(new SessionStartRequest(
                session.WorkspaceId, ticket.WorkingDirectory, prompt, ResumeOf: session.Id, FromStudio: true));
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // A ceiling, another conversation running in the folder, a history that is gone: each its own honest
            // refusal, said here, and the conversation stays resumable.
            Record(session, new MessageEmitted(
                _clock(), TranscriptRole.System, $"The network is back, but this conversation could not be resumed: {ex.Message}"));
            return false;
        }
    }

    /// <summary>Ends a conversation's wait for the network, if it has one. True when there was one.</summary>
    private bool EndNetworkWait(AgentSessionId id)
    {
        if (!_networkWaits.TryRemove(id, out var ticket))
        {
            return false;
        }

        var claimed = ticket.TryClaim();
        ticket.Cancel();
        Find(id)?.SetNetworkWait(null);
        return claimed;
    }

    /// <summary>One wait: its cancellation, the folder to resume in, and the claim that lets exactly one path end it.</summary>
    private sealed class NetworkWaitTicket(string workingDirectory)
    {
        private readonly CancellationTokenSource _cancel = new();
        private int _claimed;

        public string WorkingDirectory { get; } = workingDirectory;

        public CancellationToken Token => _cancel.Token;

        public bool IsOpen => Volatile.Read(ref _claimed) == 0 && !_cancel.IsCancellationRequested;

        /// <summary>Claims the wait for the one path that ends it, and ends its loop: nothing may act on it after.</summary>
        public bool TryClaim()
        {
            var first = Interlocked.Exchange(ref _claimed, 1) == 0;
            if (first)
            {
                _cancel.Cancel();
            }

            return first;
        }

        public void Cancel() => _cancel.Cancel();
    }

    // ---- the stream ---------------------------------------------------------------------

    private async Task PumpAsync(LiveAgentSession session, AgentSessionRequest request)
    {
        // Backlog 74: whether the CLI ever announced itself is what separates "the session started" from
        // "the session failed to start", and this loop is where both are visible. It is a local, because
        // it is only true of THIS pump.
        var started = false;

        // Backlog 229: the CLI's OWN message about a failed model call in this turn (never the agent's prose), and how
        // the last turn failed, which is what a process exit after it is judged by when its own words say nothing.
        string? cliApiError = null;
        NetworkFailure? lastTurnFailure = null;

        // Backlog 235: whether the last turn was refused by the broker for its token, which is what a process exit after
        // it is judged by.
        var lastTurnTokenRefused = false;
        try
        {
            await foreach (var raw in _runner.RunAsync(request).ConfigureAwait(false))
            {
                var e = raw;
                NetworkFailure? exitFailure = null;
                var exitTokenRefused = false;
                if (e is SessionEnded ended
                    && _reachability is not null
                    && session.ProviderSessionId is { Length: > 0 }
                    && NetworkTurnFailure.OfExit(ended, lastTurnFailure) is { IsNetwork: true } exit)
                {
                    // The process went with the network, the conversation did not: it ends resumable, in words that
                    // say so, and the wait below resumes it.
                    exitFailure = exit;
                    e = ended with
                    {
                        FailureReason = NetworkWait.EndedNotice(exit, _reachability.Current.Target.Host),
                        Resumable = true,
                    };
                }
                else if (e is SessionEnded tokenEnded
                    && lastTurnTokenRefused
                    && tokenEnded is { FinalState: AgentSessionState.Failed, Resumable: false }
                    && session.ProviderSessionId is { Length: > 0 })
                {
                    // The same for a process that went after the broker refused its token: the conversation is resumed
                    // on a fresh one.
                    exitTokenRefused = true;
                    e = tokenEnded with { FailureReason = NetworkWait.TokenEndedNotice, Resumable = true };
                }

                Record(session, e);

                if (e is ApiErrorReported apiError)
                {
                    cliApiError = apiError.Text;
                }

                if (e is SessionRegistered or SessionEnded)
                {
                    _store.Save(session.ToRecord());
                }

                if (e is SessionEnded)
                {
                    SessionsChanged?.Invoke();
                }

                started |= Measure(session, e, started, request.TurnsAreTyped);

                if (e is TurnCompleted turn)
                {
                    lastTurnFailure = NetworkTurnFailure.Of(turn, cliApiError);
                    lastTurnTokenRefused = _credentials is not null && CredentialTurnFailure.Refused(turn, cliApiError);
                    cliApiError = null;
                    StopIfOverspent(session);
                    await UpgradeIfScheduledAsync(session).ConfigureAwait(false);
                    if (lastTurnFailure.IsNetwork && session.IsRunning)
                    {
                        BeginNetworkWait(session, request.WorkingDirectory, lastTurnFailure, processEnded: false);
                    }
                    else if (lastTurnTokenRefused && session.IsRunning)
                    {
                        BeginNetworkWait(
                            session, request.WorkingDirectory, NetworkFailure.None, processEnded: false, WaitReason.Credentials);
                    }
                }

                if (exitFailure is not null)
                {
                    BeginNetworkWait(session, request.WorkingDirectory, exitFailure, processEnded: true);
                }
                else if (exitTokenRefused)
                {
                    BeginNetworkWait(
                        session, request.WorkingDirectory, NetworkFailure.None, processEnded: true, WaitReason.Credentials);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The agent session {SessionId} ended unexpectedly.", session.Id);
            var ended = new SessionEnded(_clock(), AgentSessionState.Failed, ex.Message);
            Record(session, ended);
            _store.Save(session.ToRecord());
            SessionsChanged?.Invoke();
            Measure(session, ended, started, request.TurnsAreTyped);
        }
    }

    /// <summary>
    /// Ends a run that has spent its day or month ceiling, at the turn boundary (backlog 62). The
    /// boundary is the only honest place for it: mid-turn there is work in flight that stopping would
    /// throw away, and the CLI's own <c>--max-budget-usd</c> is what covers the inside of an invocation.
    /// <para>
    /// It SAYS SO first and stops second, in that order, because the transcript is the only record an
    /// unattended run leaves: a session that simply ended would look like a crash. The stop is what makes
    /// the acceptance criterion true, since refusing the next turn would leave an unattended agent free
    /// to keep spending on turns it asks itself for.
    /// </para>
    /// <para>
    /// Reads the store, which already carries this turn's cost: the result line emits its usage snapshot
    /// before the turn's completion, and every event is persisted as it arrives, so the figure compared
    /// here includes the turn that just finished.
    /// </para>
    /// </summary>
    private void StopIfOverspent(LiveAgentSession session)
    {
        if (SpendRefusal() is not { } refusal)
        {
            return;
        }

        Record(session, new MessageEmitted(
            _clock(),
            TranscriptRole.System,
            refusal.Message + " This session has been stopped."));

        _runner.Stop(session.Id);
    }

    /// <summary>
    /// Carries out the upgrade this conversation scheduled with <c>studio_upgrade</c>, now that its turn has ended
    /// (backlog 157). The boundary is the only honest moment, as for the spend stop: mid-turn there is work in flight.
    /// <para>
    /// In this order, and each step is why the next is safe. The agent's edits are settled to the HOST first, because
    /// the container about to be replaced holds the copy they were made in. The resume marker is written BEFORE fravashi.exe
    /// is asked, because once it is asked this process may be gone before the next line runs. Then fravashi.exe is asked,
    /// and answers at once: a refusal is said in the transcript and the marker removed, so the conversation simply goes
    /// on. The session is NOT stopped: replacing the container ends its process, and a stop would make the row terminal,
    /// which the resume at the next start would skip. If the upgrade fails on the host, the studio is still here, and a
    /// watcher says so and removes the marker.
    /// </para>
    /// </summary>
    private async Task UpgradeIfScheduledAsync(LiveAgentSession session)
    {
        if (_upgrades is null || _upgradeSchedule?.Take(session.Id) is not { } version)
        {
            return;
        }

        if (_sync is not null)
        {
            try
            {
                await _sync.SettleAsync(session.WorkspaceId, UpgradeSettleTimeout).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not settle workspace {WorkspaceId} before the upgrade.", session.WorkspaceId);
            }
        }

        _upgradeSchedule.MarkResume(session.Id, version);
        if (await _upgrades.ApplyAsync(version).ConfigureAwait(false) is { } refusal)
        {
            _upgradeSchedule.ClearResume(session.Id);
            Record(session, new MessageEmitted(_clock(), TranscriptRole.System, $"The upgrade to {version} did not start: {refusal}"));
            return;
        }

        Record(session, new MessageEmitted(
            _clock(),
            TranscriptRole.System,
            $"Upgrading {ProductName.Short} to {version}. The studio restarts on it and this conversation is resumed there."));
        _ = Task.Run(() => WatchUpgradeAsync(session, version));
    }

    /// <summary>How long the agent's edits get to reach the host before the container holding them is replaced.</summary>
    private static readonly TimeSpan UpgradeSettleTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Watches an upgrade this studio asked for. Success is never observed here, because success replaces this process;
    /// what can be seen is a failure, which leaves the studio running and is said in the conversation that asked.
    /// </summary>
    private async Task WatchUpgradeAsync(LiveAgentSession session, string version)
    {
        var until = _clock() + TimeSpan.FromMinutes(45);
        while (_clock() < until)
        {
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (await _upgrades!.StatusAsync().ConfigureAwait(false) is { State: "Failed" } failed)
            {
                _upgradeSchedule!.ClearResume(session.Id);
                Record(session, new MessageEmitted(
                    _clock(),
                    TranscriptRole.System,
                    $"The upgrade to {failed.Version ?? version} did not complete: {failed.Said} The studio keeps running the version it had."));
                return;
            }
        }
    }

    /// <summary>
    /// Turns one session event into the reliability facts it carries (backlog 74), and returns whether
    /// this event was the CLI announcing itself.
    /// <para>
    /// Deliberately separate from <see cref="Record"/>: that one is the transcript, which is the user's
    /// record of the conversation, and this one is the measurement, which is a count the owner opted
    /// into. Keeping them apart is what lets the measurement be switched off without the conversation
    /// losing anything.
    /// </para>
    /// </summary>
    private bool Measure(LiveAgentSession session, AgentEvent e, bool alreadyStarted, bool typed)
    {
        // Outcome ii (ADR 0022), counted rather than hidden and never resent (decided 2026-09-14):
        // a typed session's turn that SUCCEEDED without a document. Read off the document's absence alone,
        // never off is_error or terminal_reason, which report plain success on exactly this turn. It is
        // counted beside a retry count, not instead of one: a turn can burn attempts and then give up.
        if (typed && e is TurnCompleted { Document: null, IsError: false } untyped)
        {
            _telemetry.Record(ProductEvent.TurnUntyped(untyped.Timestamp, session.WorkspaceId));
        }

        switch (e)
        {
            case SessionRegistered when !alreadyStarted:
                _telemetry.Record(ProductEvent.SessionStarted(
                    // From when THIS process was asked for: a resumed conversation keeps its original start.
                    _clock(), session.WorkspaceId, _clock() - session.ActivatedUtc));
                return true;

            // A turn the harness had to re-ask for (ADR 0022). The count is the CLI's own, not ours: the
            // studio adds no retry of its own, so this measures the model failing its schema.
            case TurnCompleted { ValidationFailures: > 0 } turn:
                _telemetry.Record(ProductEvent.TurnRetried(
                    turn.Timestamp, session.WorkspaceId, turn.ValidationFailures));
                return false;

            // Ending without ever having been registered is a start failure, whatever the CLI said on the
            // way out; ending after that is an abort only when it did not complete.
            case SessionEnded ended when !alreadyStarted:
                _telemetry.Record(ProductEvent.SessionStartFailed(ended.Timestamp, session.WorkspaceId));
                return false;

            case SessionEnded { FinalState: AgentSessionState.Cancelled }:
                _telemetry.Record(ProductEvent.SessionAborted(
                    e.Timestamp, session.WorkspaceId, ProductEventOutcome.Cancelled));
                return false;

            case SessionEnded { FinalState: AgentSessionState.Failed }:
                _telemetry.Record(ProductEvent.SessionAborted(
                    e.Timestamp, session.WorkspaceId, ProductEventOutcome.Failed));
                return false;

            default:
                return false;
        }
    }

    /// <summary>Applies an event to the live session and persists it (transient events are skipped).</summary>
    /// <summary>Persists an event the live session has already applied (work item #352's mark, applied inside the start gate).</summary>
    private void PersistOnly(LiveAgentSession session, AgentEvent e)
    {
        try
        {
            _store.Append(session.Id, [e]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist a transcript event for session {SessionId}.", session.Id);
        }
    }

    private void Record(LiveAgentSession session, AgentEvent e)
    {
        // A resumed process counts usage from zero; the conversation does not (see WithCarriedUsage).
        e = session.WithCarriedUsage(e);
        session.Apply(e);

        if (AgentEventJson.KindOf(e) is null)
        {
            return;
        }

        try
        {
            _store.Append(session.Id, [e]);
        }
        catch (Exception ex)
        {
            // A transcript that cannot be written must not take the running session down.
            _logger.LogWarning(ex, "Could not persist a transcript event for session {SessionId}.", session.Id);
        }
    }

    // ---- approvals ----------------------------------------------------------------------

    private void OnApprovalRequested(PendingToolApproval approval)
    {
        if (Find(approval.SessionId) is not { } session)
        {
            return;
        }

        session.AddPending(approval);
        Record(session, new ToolApprovalRequested(
            approval.RequestedUtc, approval.RequestId, approval.ToolName, approval.Detail, approval.Warning));
    }

    private void OnApprovalResolved(PendingToolApproval approval, ToolApprovalDecision decision)
    {
        if (Find(approval.SessionId) is not { } session)
        {
            return;
        }

        // Backlog 74: one recorded event carries both facts the item asks for. Its OUTCOME is the
        // denial signal, split the way the transcript splits it (a refusal the user made and one the
        // studio imposed are different things, backlog 114), and its DURATION is the approval latency:
        // how long the agent sat blocked on a person. No tool name and no argument goes with it.
        _telemetry.Record(ProductEvent.ApprovalResolved(
            _clock(),
            session.WorkspaceId,
            decision.Approved
                ? ProductEventOutcome.Approved
                : decision.DecidedBy == ToolApprovalDecidedBy.User
                    ? ProductEventOutcome.DeniedByUser
                    : ProductEventOutcome.DeniedByStudio,
            _clock() - approval.RequestedUtc));

        // Backlog 117: a refusal nobody answered is an enforcement action, and one that repeats is an
        // agent pushing against it, so it carries how many times this session has been refused the same
        // call. The user's own answers are not counted: they already see every one of those.
        var repeats = !decision.Approved && decision.DecidedBy == ToolApprovalDecidedBy.Studio
            ? session.CountAutomaticRefusal(approval.ToolName, approval.Detail)
            : 1;

        // The attribution rides through unchanged (backlog 114): the transcript must be able to say who
        // refused a call, and the only place that is known is where the decision was made.
        Record(session, new ToolApprovalResolved(
            _clock(),
            approval.RequestId,
            approval.ToolName,
            decision.Approved,
            decision.Reason,
            decision.DecidedBy,
            repeats));
    }

    /// <summary>
    /// A refusal the studio imposed on a call it never parked (backlog 117). Recorded like every other
    /// transcript event, with its repeat count, because a refusal that leaves no trace lets an agent
    /// retry a refused call forever while the transcript shows nothing.
    /// </summary>
    private void OnToolRefused(AutomaticToolRefusal refusal)
    {
        if (Find(refusal.SessionId) is not { } session)
        {
            // No session this studio holds, so there is no transcript to write it into. Logged instead, so
            // it is still not invisible; the tool name and the session id are not secrets.
            _logger.LogWarning(
                "The studio refused a {ToolName} call for session {SessionId}, which it does not hold.",
                refusal.ToolName,
                refusal.SessionId);
            return;
        }

        Record(session, new ToolCallRefused(
            refusal.RefusedUtc,
            refusal.ToolName,
            refusal.Detail,
            refusal.Reason,
            session.CountAutomaticRefusal(refusal.ToolName, refusal.Detail)));
    }

    /// <summary>
    /// Backlog 243 and 244: every commit review is written on the BUILDING conversation, in the studio's voice, with its
    /// verdict, role, duration, tokens, cost, finding count and the review conversation's id, so each review can be
    /// counted against what the host found. Written before the refusal or the card it leads to.
    /// </summary>
    private void OnCommitReviewed(AgentSessionId builder, CommitReview review)
    {
        if (Find(builder) is not { } session)
        {
            _logger.LogInformation("A commit review for session {SessionId}, which this studio does not hold: {Note}", builder, review.TranscriptNote);
            return;
        }

        Record(session, new MessageEmitted(_clock(), TranscriptRole.System, review.TranscriptNote));
    }

    /// <summary>
    /// A change the studio let run without asking under the workspace's trust level (ADR 0028). Written to the
    /// transcript like a refusal is, because an unattended change must be exactly as visible as a refused one.
    /// </summary>
    private void OnToolTrusted(TrustedToolCall trusted)
    {
        if (Find(trusted.SessionId) is not { } session)
        {
            _logger.LogWarning(
                "The studio trusted a {ToolName} call for session {SessionId}, which it does not hold.",
                trusted.ToolName,
                trusted.SessionId);
            return;
        }

        Record(session, new ToolCallTrusted(trusted.TrustedUtc, trusted.ToolName, trusted.Detail, trusted.Reason));
    }

    /// <summary>
    /// A call an autopilot session's own rule list or judge let run WITHOUT asking (ADR 0031 decisions 7 and
    /// 8, backlog 299), distinct from <see cref="OnToolTrusted"/> (ADR 0028's own mechanism). Written to the
    /// transcript like a refusal or a trusted edit is, so the rule or the judgement that allowed it is
    /// actually visible, not only counted.
    /// </summary>
    private void OnAutopilotAllowed(AutopilotAllowedCall allowed)
    {
        if (Find(allowed.SessionId) is not { } session)
        {
            _logger.LogWarning(
                "The studio recorded an autopilot allow for a {ToolName} call for session {SessionId}, which it does not hold.",
                allowed.ToolName,
                allowed.SessionId);
            return;
        }

        Record(session, new ToolCallAutopilotAllowed(allowed.AllowedUtc, allowed.ToolName, allowed.Detail, allowed.Reason));
    }

    private static string TitleFrom(string prompt)
    {
        var single = prompt.ReplaceLineEndings(" ").Trim();
        if (single.Length == 0)
        {
            return "Pasted image";
        }

        return single.Length <= 60 ? single : single[..57] + "...";
    }
}
