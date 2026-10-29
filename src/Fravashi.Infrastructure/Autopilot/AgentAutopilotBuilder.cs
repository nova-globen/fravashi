using System.Globalization;
using System.Text.Json;
using Fravashi.Application.AgentSync;
using Fravashi.Application.Autopilot;
using Fravashi.Application.Git;
using Fravashi.Application.Persistence;
using Fravashi.Application.Sessions;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Autopilot;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;
using Fravashi.Infrastructure.AgentSync;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.Sessions.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fravashi.Infrastructure.Autopilot;

/// <summary>
/// The real <see cref="IAutopilotBuilder"/> (ADR 0031 decision 11, #309): the building agent is an ORDINARY
/// studio session, started through <see cref="LiveSessionRegistry"/> exactly the way a person's own
/// conversation is, with the session pane, the work strip, its gates and the commit reviewer all unchanged.
/// The only things this adds are: it is marked autopilot (<see cref="IAgentApprovalBroker.SetAutopilot"/>, so
/// the rule list and the judge apply to it, ADR 0031 decisions 7 to 9), and it is linked to the item and its
/// plan, exactly as a person's own session would be linked by hand.
/// <para>
/// <b>One call is one turn, and it never waits for a person.</b> <see cref="BuildAsync"/> starts the session
/// (or sends a follow-up turn to the one it already started for this item) and waits only until that turn
/// SETTLES: either it ends (<see cref="LiveAgentSession.TurnInFlight"/> goes false) or a tool call genuinely
/// parks in front of a person (<see cref="LiveAgentSession.Pending"/> is non-empty). The autopilot's rule list
/// and judge both resolve a call synchronously, inside the approval broker, before it would ever reach
/// <c>Pending</c>, so a non-empty <c>Pending</c> here means exactly what ADR 0031 decision 9 calls "everything
/// else waits for a person": nobody answered it yet, and this method does not wait for them to.
/// </para>
/// <para>
/// <b>The workspace holds one live session, and this never fights a PERSON for it, though it does end an
/// EARLIER ITEM's own.</b> Before starting a FRESH session (no session bound yet for this item, or the one
/// that was bound has stopped running), <see cref="LiveSessionRegistry.RunningFor"/> is asked directly. When
/// something is already running there, <see cref="AutopilotBuilderSessions.ItemFor"/> says which of two
/// things it is: a session bound to a DIFFERENT item of this same run is this run's own leftover (an
/// ordinary session outlives the turn that closed its item, so the first item's session is still there when
/// the second is about to start), and it is stopped and waited on
/// (<see cref="LiveSessionRegistry.Stop"/>, <see cref="WaitForStoppedAsync"/>) so the fresh start has room;
/// a session bound to no item at all is a person's own conversation, and THAT is refused outright, naming
/// what is running (its title), rather than attempting a start that <see cref="LiveSessionRegistry.Start"/>
/// would refuse anyway with a less specific reason.
/// </para>
/// </summary>
public sealed class AgentAutopilotBuilder : IAutopilotBuilder
{
    /// <summary>How long one turn may run before this gives up on it and throws.</summary>
    public static readonly TimeSpan DefaultTurnTimeout = TimeSpan.FromMinutes(60);

    /// <summary>How long an earlier item's session may take to actually stop before this gives up and throws.</summary>
    private static readonly TimeSpan StopWaitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a worktree's or workspace's contained copy may take to finish its first pull before this gives up
    /// on it (#328): generous against the observed failure (the CLI's working directory was missing in under a
    /// second, before the copy's first pull had created it), far short of <see cref="DefaultTurnTimeout"/>, so a
    /// genuinely stuck copy is reported quickly rather than silently eating most of an hour first.
    /// </summary>
    private static readonly TimeSpan CopyReadyTimeout = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The name a studio tool call for <c>attempt_commit</c> carries on the two transcript events this class
    /// reads, never the tool's own bare <see cref="AttemptCommitTool.ToolName"/>: <see cref="ToolCallStarted"/>
    /// carries the CLI-facing MCP name, measured against the real CLI (<c>ClaudeCliContractTests</c>), and
    /// <see cref="ToolCallCompleted"/> carries that SAME name (<c>ClaudeStreamJsonParser</c> looks it up by the
    /// call's tool-use id, from the started event, rather than reading one of its own); a
    /// <see cref="ToolCallRefused"/> carries whatever name the hook actually posted for the pending call, which
    /// is this same MCP-prefixed name too (also the approval broker's own convention, matched in
    /// <c>CommitReviewTests</c> and <c>AgentApprovalBrokerAutopilotTests</c>).
    /// </summary>
    private static readonly string AttemptCommitCliName = StudioToolServer.CliToolName(AttemptCommitTool.ToolName);

    private const string LinkedBy = "autopilot";

    /// <summary>
    /// A parked call as a person reads it (work item #356): a studio tool's approval sentence in quotes, a built-in by
    /// its own name. The parked reason is shown on Home, so it must never carry a raw mcp__ name.
    /// </summary>
    private static string ParkedCallWords(PendingToolApproval pending) =>
        Fravashi.Application.Sessions.Tools.StudioToolDescriptions.Describes(pending.ToolName)
            ? "\"" + Fravashi.Application.Sessions.Tools.StudioToolDescriptions.ForPeople(pending.ToolName, pending.Input) + "\""
            : Fravashi.Application.Sessions.Tools.StudioToolDescriptions.DisplayName(pending.ToolName);

    private const string BuilderPreamble =
        """
        This is an autopilot building session (ADR 0031). Start your work on this item's own attempt branch
        with attempt_branch (call it once, if you have not already), and commit with attempt_commit, never
        git_commit or git_push: this run works on this folder's own attempt branches while it runs, each made
        from the branch the run started from, and the folder is returned to the branch it started from before
        each item and once the run ends, never to another ref. If you need
        a person to decide something, ask; otherwise keep working within what you were given.
        """;

    /// <summary>
    /// The preamble for an item given its own git worktree (#308: an effective concurrency cap above 1).
    /// Unlike <see cref="BuilderPreamble"/>, nothing here switches the person's own folder at all, so this
    /// never claims that it is returned to anything; the worktree's own branch is already the attempt branch
    /// (attempt_branch makes no second one, #308 decision 3), and the worktree itself is pruned, not returned,
    /// once this item ends (backlog 312).
    /// </summary>
    private const string BuilderWorktreePreamble =
        """
        This is an autopilot building session (ADR 0031). This item runs in its own git worktree, whose branch
        is already the attempt branch: attempt_branch (call it once, if you have not already) answers that
        branch rather than making a new one. Commit with attempt_commit, never git_commit or git_push. The
        worktree is pruned once this item ends; it never touches the person's own folder. If you need a
        person to decide something, ask; otherwise keep working within what you were given.
        """;

    /// <summary>
    /// Backlog 332: told once, before the first prompt, only when the worktree this call just resolved (new
    /// or reused from a stopped earlier attempt) is not clean. attempt_commit's own default already includes
    /// this work (<c>SessionGitWork.DefaultPathsAsync</c>), so the sentence only needs to say that is true,
    /// never to instruct a re-read of the tree.
    /// </summary>
    private const string ResumedWorktreeNote =
        "This worktree already holds uncommitted work from an earlier attempt; attempt_commit will include "
            + "it, so there is no need to write it again.";

    private readonly LiveSessionRegistry _registry;
    private readonly IAgentApprovalBroker _broker;
    private readonly ISessionWorkItems _work;
    private readonly ISessionPlans _plans;
    private readonly ITrackerResolver _trackers;
    private readonly IWorkspaceAgentSync _sync;
    private readonly AutopilotBuilderSessions _sessions;
    private readonly Func<ProjectId, string?> _workspaceIdFor;
    private readonly IAutopilotConcurrencyGate _concurrency;
    private readonly IAutopilotWorktrees? _worktrees;
    private readonly IWorkspaceStore? _workspaceStore;
    private readonly IWorkspaceGit? _workspaceGit;
    private readonly ISessionBranches? _branches;
    private readonly IAutopilotItemStart? _itemStart;
    private readonly ISessionAttemptBases? _attemptBases;
    private readonly Func<AgentDefault> _defaults;
    private readonly TimeSpan _turnTimeout;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _logger;

    public AgentAutopilotBuilder(
        LiveSessionRegistry registry,
        IAgentApprovalBroker broker,
        ISessionWorkItems work,
        ISessionPlans plans,
        ITrackerResolver trackers,
        IWorkspaceAgentSync sync,
        AutopilotBuilderSessions sessions,
        Func<ProjectId, string?> workspaceIdFor,
        IAutopilotConcurrencyGate? concurrency = null,
        IAutopilotWorktrees? worktrees = null,
        IWorkspaceStore? workspaceStore = null,
        IWorkspaceGit? workspaceGit = null,
        ISessionBranches? branches = null,
        TimeSpan? turnTimeout = null,
        Func<DateTimeOffset>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        ILogger<AgentAutopilotBuilder>? logger = null,
        IAutopilotItemStart? itemStart = null,
        ISessionAttemptBases? attemptBases = null,
        Func<AgentDefault>? builderDefaults = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _work = work ?? throw new ArgumentNullException(nameof(work));
        _plans = plans ?? throw new ArgumentNullException(nameof(plans));
        _trackers = trackers ?? throw new ArgumentNullException(nameof(trackers));
        _sync = sync ?? throw new ArgumentNullException(nameof(sync));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _workspaceIdFor = workspaceIdFor ?? throw new ArgumentNullException(nameof(workspaceIdFor));
        _concurrency = concurrency ?? IAutopilotConcurrencyGate.None;
        _worktrees = worktrees;
        _workspaceStore = workspaceStore;
        _workspaceGit = workspaceGit;
        _branches = branches;
        _itemStart = itemStart;
        _attemptBases = attemptBases;
        _defaults = builderDefaults ?? (() => AgentDefault.None);
        _turnTimeout = turnTimeout ?? DefaultTurnTimeout;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
        _logger = logger ?? NullLogger<AgentAutopilotBuilder>.Instance;
    }

    /// <inheritdoc />
    public async Task<AutopilotBuildResult> BuildAsync(
        AutopilotBuilderContext context, WorkItemId item, string prompt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var workItem = await RequireItemAsync(context.Project, item, ct).ConfigureAwait(false);

        var existing = _sessions.SessionFor(item);
        var existingRun = _sessions.RunFor(item);
        LiveAgentSession session;
        AgentStatsSnapshot statsBefore;
        int turnStartIndex;
        if (existing is { } sid && _registry.Find(sid) is { IsRunning: true } live && existingRun == context.RunId)
        {
            // ADR 0031 decision 12 (#305): a previous call already reported NeedsPerson for this exact item
            // and nothing has resolved it since (ClearTurnStart below only runs once a result comes back
            // that is NOT NeedsPerson). This call is a WAITING re-check, never a retry, whether the turn is
            // still blocked on the SAME pending approval, or it settled on a question and a person has since
            // typed their own answer in the session pane (which TurnInFlight alone cannot tell apart from
            // "nobody has answered yet": a Questions document ENDS the turn, so TurnInFlight reads false
            // either way). Sending here would either queue a second turn behind the one already blocked, or
            // layer this item's own canned prompt on top of a real conversational answer; the only correct
            // action in both cases is to wait for whatever is happening to settle, then read it, never send.
            // turnStartIndex stays PINNED to where the turn actually began, so whichever later re-check
            // finally sees it settle reads the WHOLE turn's history (the approval, the commit, all of it),
            // never only what changed since the last look; statsBefore is the LAST OBSERVED stats instead,
            // updated on every check (below), so each call's own cost delta stays purely incremental and a
            // repeated "nothing happened" check never re-counts money already folded into the item.
            if (_sessions.TurnStart(sid) is { } waiting)
            {
                turnStartIndex = waiting.TranscriptIndex;
                statsBefore = waiting.LastObservedStats;
            }
            else
            {
                turnStartIndex = live.Transcript.Count;
                statsBefore = live.Stats;
                if (!await _registry.SendAsync(sid, prompt, ct).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        $"The building session for item #{workItem.Number.ToString(CultureInfo.InvariantCulture)} could not be sent a turn.");
                }
            }

            session = live;
        }
        else
        {
            // A session still bound to THIS item but a DIFFERENT run (necessarily an earlier one: the
            // engine only ever calls BuildAsync for one open run's current item at a time) is #310's own
            // leftover: that run's own ending should have stopped it, but a stop that failed or timed out is
            // swallowed into a WorkspaceReturnNote rather than retried, so it can still be running here. A
            // later run resuming this item (ADR 0031 decision 12, #305, "an answer after parking resumes the
            // item") must start a genuinely FRESH attempt: never send into, or re-check, a session another
            // run owns, so it is stopped here, the same way an earlier ITEM's own leftover session is below.
            if (existing is { } staleSid && _registry.Find(staleSid) is { IsRunning: true })
            {
                _registry.Stop(staleSid);
                await WaitForStoppedAsync(staleSid, ct).ConfigureAwait(false);
                _sessions.ClearTurnStart(staleSid);
            }

            // #308: a run whose effective concurrency cap is more than 1 gives EVERY item its own worktree,
            // never the shared repository workspace (decision 2's own reasoning: a cap-1 run keeps today's
            // behaviour, untouched, resolved only in that branch below, never before this point). Read off
            // the context (resolved once at dispatch, #308), never live Settings: this is a MODE decision
            // (shared workspace or a worktree of its own), and a Settings change made while this run is open
            // must never flip an already-running run between the two.
            var effectiveCap = context.EffectiveConcurrencyCap;
            AutopilotWorktree? worktree = null;
            var worktreeHasLeftovers = false;
            string targetWorkspaceId;
            string targetCopyPath;
            if (effectiveCap > 1)
            {
                // A parallel run MUST give this item its own worktree (ADR 0031 decision 15: the person's
                // own checkout is never touched, and #308 decision 2: never a mix of shared-workspace and
                // worktree items in one run). A missing seam is a wiring fault, never a reason to fall back
                // to the shared workspace, which would stop an earlier item's own session mid-turn to make
                // room here, exactly the mixing decision 15 forbids: it throws, the same shape every other
                // refusal in this method already uses.
                if (_worktrees is null || _workspaceStore is null || _branches is null)
                {
                    throw new InvalidOperationException(
                        $"This run's effective concurrency cap is {effectiveCap.ToString(CultureInfo.InvariantCulture)}, "
                            + "so item #" + workItem.Number.ToString(CultureInfo.InvariantCulture)
                            + " needs its own worktree, but this builder was wired with no worktree seam.");
                }

                // The REPOSITORY the worktree is made from, never _workspaceIdFor's own "most recently opened
                // workspace for this project" lookup (commit review's finding): that lookup orders by when a
                // workspace was last Touched, and this item's own worktree is Touched below (and re-Touched
                // on every WaitingForCapacity retry), so after the FIRST call it would answer the worktree
                // itself, making every later one a worktree OF a worktree. context.RepositoryWorkspaceId is
                // resolved once at dispatch (#310's own BaseWorkspaceId) and never drifts underneath a run.
                var repositoryWorkspaceId = context.RepositoryWorkspaceId is { Length: > 0 } repoId
                    ? repoId
                    : throw new InvalidOperationException(
                        $"This run recorded no repository workspace, so item #{workItem.Number.ToString(CultureInfo.InvariantCulture)} "
                            + "cannot be given its own worktree.");
                var baseBranch = context.BaseBranch is { Length: > 0 } bb
                    ? bb
                    : throw new InvalidOperationException(
                        $"This run recorded no base branch, so item #{workItem.Number.ToString(CultureInfo.InvariantCulture)} "
                            + "cannot be given its own worktree.");
                worktree = await _worktrees.CreateAsync(repositoryWorkspaceId, workItem.Number, baseBranch, ct).ConfigureAwait(false);

                // Backlog 332: read right after the worktree is resolved (new or, on a resumed item, the
                // same one reused), before anything else touches it, so a stopped earlier attempt's own
                // leftovers are told to the builder's first turn rather than discovered by it.
                worktreeHasLeftovers = _workspaceGit is { } workspaceGit
                    && await IsDirtyAsync(workspaceGit, worktree.WorkspaceId, ct).ConfigureAwait(false);

                // Registered BEFORE the session starts (#308 host note point 2), the same Touch the welcome
                // page's own folder-open path already calls for an ordinary workspace: without this, the
                // concurrency gate cannot group this worktree with its repository and counts it alone.
                _workspaceStore.Touch(worktree.WorkspaceId, worktree.Name, worktree.RootPath);

                // Bound BEFORE the cap is even checked (commit review's finding): a refused start (below)
                // still leaves this worktree created and registered, and only this binding lets EndItemAsync
                // find and drop it later. Re-binding after a successful Start (below) is then just recording
                // which session owns it.
                _sessions.BindWorktree(item, repositoryWorkspaceId, worktree.WorkspaceId, workItem.Number);

                _sync.EnsureStarted(worktree.WorkspaceId);
                targetWorkspaceId = worktree.WorkspaceId;
                if (_sync.CopyPathFor(worktree.WorkspaceId) is { Length: > 0 } wtCopy)
                {
                    targetCopyPath = wtCopy;
                }
                else if (StartFailureReason(worktree.WorkspaceId, null) is { Length: > 0 } wtFailure)
                {
                    // #328: a failure recorded on an EARLIER call for this same worktree id (a retried item,
                    // whose worktree id is deterministic by item number) leaves EnsureStarted a no-op and
                    // CopyPathFor null for good (WorkspaceAgentSync removes the failed session), so this is
                    // never transient; the generic throw below would otherwise stop the whole run on a
                    // condition the item can never recover from by itself.
                    return CopyRefusal(wtFailure);
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Worktree '{worktree.WorkspaceId}' has no contained copy yet, so the building agent cannot start.");
                }
            }
            else
            {
                // A cap-1 run's shared workspace (commit review's finding): resolved HERE, never before the
                // cap branch above, so a parallel item's own start can never fail (or wait) on a workspace it
                // does not use at all, including one a worktree's own Touch has already skewed the live
                // project lookup toward, or one a completed prune has already made stale.
                targetWorkspaceId = _workspaceIdFor(context.Project) is { Length: > 0 } sharedId
                    ? sharedId
                    : throw new InvalidOperationException(
                        $"No workspace is bound to project '{context.Project.Value}', so the building agent has nowhere to run.");
                _sync.EnsureStarted(targetWorkspaceId);
                if (_sync.CopyPathFor(targetWorkspaceId) is { Length: > 0 } sharedCopy)
                {
                    targetCopyPath = sharedCopy;
                }
                else if (StartFailureReason(targetWorkspaceId, null) is { Length: > 0 } sharedFailure)
                {
                    // #328: the same reasoning as the worktree branch above, for the shared workspace's own
                    // copy (a failure recorded on an earlier call, before this one ever read its path).
                    return CopyRefusal(sharedFailure);
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Workspace '{targetWorkspaceId}' has no contained copy yet, so the building agent cannot start.");
                }
            }

            // #328: CopyPathFor above answers a path the instant EnsureStarted registers it, whether or not the
            // copy's own first pull (which creates the folder on disk) has actually run yet, on BOTH branches
            // above (worktree and shared workspace alike, the same IWorkspaceAgentSync call pair). Waiting here,
            // once, covers both: never ready in time, or a recorded start failure, fails the item in words
            // naming the copy, and the CLI below is never reached.
            if (await WaitForCopyReadyAsync(targetWorkspaceId, targetCopyPath, ct).ConfigureAwait(false) is { Length: > 0 } notReadyReason)
            {
                return CopyRefusal(notReadyReason);
            }

            if (_registry.RunningFor(targetWorkspaceId) is { } occupying)
            {
                // Two different reasons the workspace is occupied, told apart by whether THIS tracker ever
                // bound the session to an item: a person's own conversation is never touched (the refusal
                // below); an EARLIER item of this same run's own session is not a person's, and it stays
                // alive after that item closes (Done, Stopped or Parked) because nothing else ever stops it.
                // Ending it is what lets a run advance past its first item at all.
                if (_sessions.ItemFor(occupying.Id) is { } previousItem && previousItem != item)
                {
                    _registry.Stop(occupying.Id);
                    await WaitForStoppedAsync(occupying.Id, ct).ConfigureAwait(false);
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Workspace '{targetWorkspaceId}' already has a session running (\"{occupying.Title}\"). The "
                            + "autopilot refuses to start on top of it; stop or finish that conversation first.");
                }
            }

            // #315: a cap-1 run's shared workspace is still on the PREVIOUS item's attempt branch here (#310 returns
            // it only when the run ends), so it is put back on the run's base branch before this item starts, after
            // that item's session above has stopped and its last write-backs have landed. A folder that is not
            // clean is that item's leftovers: refused in words and parked, never carried into this one. A worktree
            // item is untouched: its own branch was already made from the run's base (#308).
            if (worktree is null && context.BaseBranch is { Length: > 0 } runBase && _itemStart is not null
                && await _itemStart.PrepareAsync(targetWorkspaceId, runBase, ct).ConfigureAwait(false) is { Length: > 0 } notReady)
            {
                return new AutopilotBuildResult(
                    CommitMade: false, BranchHeadSha: null, CostUsd: 0m, NeedsPerson: false,
                    Problem: $"Item #{workItem.Number.ToString(CultureInfo.InvariantCulture)} was not started: {notReady}. "
                        + $"Every item starts from the run's base branch '{runBase}', and one item's leftovers are never "
                        + "carried into the next; a person settles the folder, then the item can be resumed.",
                    Refused: true);
            }

            // ADR 0031 decision 16 (#307), made atomic (#308 host note point 3): the lock is held across
            // BOTH the check AND the Start call that follows it, so two concurrent fresh starts for the same
            // repository can never both pass Refuse before either one's session is counted by the registry.
            // Never held across the turn itself (WaitForSettledAsync, below, outside this block): that would
            // serialize an entire building turn per repository, defeating the whole point of running more
            // than one in parallel.
            var repositoryLock = await _concurrency.LockAsync(targetWorkspaceId, ct).ConfigureAwait(false);
            try
            {
                if (_concurrency.Refuse(targetWorkspaceId, context.ConcurrencyCapOverride) is { Length: > 0 } concurrencyRefusal)
                {
                    // Not a failure of the item (#308 host note point 4): nothing started, nothing is spent,
                    // and the engine tries again on a later tick once a slot frees.
                    return new AutopilotBuildResult(
                        CommitMade: false, BranchHeadSha: null, CostUsd: 0m, NeedsPerson: false,
                        Problem: concurrencyRefusal, WaitingForCapacity: true);
                }

                var preamble = worktree is not null ? BuilderWorktreePreamble : BuilderPreamble;
                if (worktreeHasLeftovers)
                {
                    preamble += "\n\n" + ResumedWorktreeNote;
                }

                // Work item #357: read at each start, so a Settings change applies to the next item.
                var defaults = _defaults();
                session = _registry.Start(new SessionStartRequest(
                    targetWorkspaceId,
                    targetCopyPath,
                    preamble + "\n\n" + prompt,
                    Model: BuilderModel(workItem, context.Role, defaults),
                    Title: Title(workItem),
                    FromStudio: true,
                    Effort: BuilderEffort(workItem, defaults)));

                if (worktree is not null)
                {
                    // #308's own decision 3: the worktree's branch IS the attempt branch, so attempt_branch
                    // makes no second one (SessionGitWork.StartAsync's own first line returns whatever is
                    // already recorded for this session). The worktree's own binding was already made above,
                    // before the cap was even checked, so a refused start still leaves it findable.
                    _branches!.Record(session.Id, new SessionBranch(worktree.Branch, worktree.BaseBranch, _clock()));
                }
                else if (context.BaseBranch is { Length: > 0 } attemptBase)
                {
                    // #315: attempt_branch makes this item's branch FROM the run's base and records that base,
                    // whatever the folder has checked out when the agent calls it, so delivery reads the run's
                    // base and never an earlier item's attempt branch.
                    _attemptBases?.Set(session.Id, attemptBase);
                }

                // The engine only ever calls BuildAsync for ONE item at a time per run, so marking, linking
                // and binding happen only once per item, right here, on the turn that actually creates the
                // session.
                _broker.SetAutopilot(session.Id, true);
                _work.Link(session.Id, item, context.Project, LinkedBy);
                if (workItem.RefFor(ArtifactRefRole.Plan) is { } plan)
                {
                    _plans.Link(session.Id, plan.Target, LinkedBy);
                }

                _sessions.Bind(item, session.Id, context.RunId);
            }
            finally
            {
                repositoryLock.Dispose();
            }

            turnStartIndex = session.Transcript.Count;
            statsBefore = session.Stats;
        }

        await WaitForSettledAsync(session, workItem, ct).ConfigureAwait(false);

        var result = ReadResult(session, statsBefore, turnStartIndex, item, workItem.Number);
        if (result.NeedsPerson)
        {
            _sessions.RecordTurnStart(session.Id, new AutopilotTurnStart(turnStartIndex, session.Stats));
        }
        else
        {
            _sessions.ClearTurnStart(session.Id);
        }

        return result;
    }

    /// <summary>How long a prune waits for the worktree's own last edits to reach the host before checking dirty.</summary>
    private static readonly TimeSpan WorktreeSettleTimeout = TimeSpan.FromSeconds(15);

    /// <inheritdoc />
    public async Task<string?> EndItemAsync(AutopilotBuilderContext context, WorkItemId item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var binding = _sessions.WorktreeFor(item);
        if (binding is null && context.EffectiveConcurrencyCap <= 1)
        {
            // A cap-1 run never gives any item a worktree (#308 decision 2): nothing to stop, nothing to
            // prune, and no reason to spend a tracker lookup or a bridge round trip on the common case.
            return null;
        }

        // The item's own session is stopped and WAITED ON first, whether it came from the in-memory binding
        // or (a restart) nothing at all: the parallel path has nothing else that ever stops it (
        // MaybeReturnWorkspaceAsync only runs when the whole RUN ends, and the sequential "earlier item
        // occupying" branch in BuildAsync never fires for a worktree-backed item, since the next item's own
        // target is a different workspace entirely), so without this a worktree would be pruned, or its sync
        // settled, out from under a session still writing to it.
        await StopAsync(item, ct).ConfigureAwait(false);

        if (_worktrees is null)
        {
            _sessions.ClearWorktree(item);
            return null;
        }

        // The bridge's own prune primitive is keyed by (repository workspace id, item number), never by
        // anything only this process remembers, so a restart that lost the in-memory binding still prunes
        // honestly: derive the repository from context.RepositoryWorkspaceId, the SAME dispatch-resolved id
        // BuildAsync itself uses to create the worktree (never _workspaceIdFor's own "most recently opened"
        // lookup, which a Touched worktree would already have skewed), and the item's number from the
        // tracker. A cap-1 item with no binding was never given one; PruneAsync answers NotFound for it
        // harmlessly (#306's own design), but there is nothing useful to prune, so this still only reaches
        // here for a cap-above-1 run.
        var repositoryWorkspaceId = binding?.RepositoryWorkspaceId ?? context.RepositoryWorkspaceId;
        if (repositoryWorkspaceId is not { Length: > 0 })
        {
            _sessions.ClearWorktree(item);
            return null;
        }

        int itemNumber;
        if (binding is { } bound)
        {
            itemNumber = bound.ItemNumber;
        }
        else
        {
            try
            {
                itemNumber = (await RequireItemAsync(context.Project, item, ct).ConfigureAwait(false)).Number;
            }
            catch (InvalidOperationException)
            {
                _sessions.ClearWorktree(item);
                return null;
            }
        }

        try
        {
            // Settle the worktree's own sync session before the bridge reads it as dirty or clean: without
            // this, the dirty check can see a stale "clean" tree while the session's own last edits are
            // still on their way from the agent's contained copy to the host folder.
            if (binding is { } settling)
            {
                await _sync.SettleAsync(settling.WorktreeWorkspaceId, WorktreeSettleTimeout, ct).ConfigureAwait(false);
            }

            var problem = await _worktrees.PruneAsync(repositoryWorkspaceId, itemNumber, ct).ConfigureAwait(false);
            if (problem is { Length: > 0 })
            {
                // Never only a log line: a dirty worktree (or any other prune refusal) is left in place and
                // RETURNED in words (#308 host note point 8, plan @126 point 8), which AutopilotEngine folds
                // onto the run's own record, the same way a thrown exception already is. Still logged too,
                // for the operational trail.
                _logger.LogWarning(
                    "The worktree for item {ItemNumber} could not be pruned: {Problem}", itemNumber, problem);
                return $"item {item.Value}: worktree not pruned: {problem}";
            }

            if (binding is { } pruned)
            {
                // The bridge confirmed the worktree itself is gone: its own sync session must stop too (#308
                // host note point 8), never left running against a host folder that no longer exists, where
                // a LATER attempt for the same item number would otherwise find it already claiming the id.
                // Only after prune, never before: stopping it any earlier could read a stale "clean" tree
                // ahead of SettleAsync's own wait, under-reporting what the agent actually wrote.
                await _sync.StopAsync(pruned.WorktreeWorkspaceId).ConfigureAwait(false);

                // The studio's own record of it must go too, or it lingers in Recent and on the welcome page
                // naming a folder that no longer exists.
                _workspaceStore?.Remove(pruned.WorktreeWorkspaceId);
            }

            // Known, accepted gap, true of the sync stop AND the registration drop alike: a restart that
            // lost the in-memory binding before this runs still prunes the worktree honestly (the bridge is
            // keyed by repository and item number, not by anything this process remembers), but has no way
            // to name the worktree's OWN workspace id, so neither its sync session nor its workspace-store
            // registration is dropped on that specific path; both linger until the same id is reused or
            // removed by hand. The binding != null branch above is the only one that can do either.
            return null;
        }
        finally
        {
            _sessions.ClearWorktree(item);
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(WorkItemId item, CancellationToken ct = default)
    {
        if (_sessions.SessionFor(item) is { } session)
        {
            // Killing a process is not instant (LiveSessionRegistry.Stop only asks it to end), and the
            // workspace-return seam (#310, commit review's fifth pass) checks out the base branch right
            // after this call returns: without waiting for the effect, the session could still be alive, and
            // its own later write-back could land on the base branch. The same wait BuildAsync already uses
            // to free a workspace for the next item's fresh start, reused here for the identical reason.
            _registry.Stop(session);
            await WaitForStoppedAsync(session, ct).ConfigureAwait(false);
        }
    }

    private async Task<WorkItem> RequireItemAsync(ProjectId project, WorkItemId item, CancellationToken ct)
    {
        var tracker = _trackers.For(project);
        if (await tracker.GetAsync(item, ct).ConfigureAwait(false) is not { } workItem)
        {
            throw new InvalidOperationException(
                $"Project '{project.Value}' has no item '{item.Value}' for the building agent to read.");
        }

        return workItem;
    }

    /// <summary>
    /// Waits for a session, just asked to stop, to actually leave the workspace free: a fresh <c>Start</c>
    /// right after must not race <see cref="LiveSessionRegistry"/>'s own one-session-per-workspace check, and
    /// (#310, commit review's fifth pass) <see cref="StopAsync"/> must not report done before the process
    /// genuinely has, since the workspace-return seam checks out the base branch right after. Killing a
    /// process is not instant; the pump only marks it ended once it sees that process's own final event.
    /// </summary>
    private async Task WaitForStoppedAsync(AgentSessionId id, CancellationToken ct)
    {
        var deadline = _clock() + StopWaitTimeout;
        while (_registry.Find(id) is { IsRunning: true })
        {
            if (_clock() >= deadline)
            {
                throw new InvalidOperationException(
                    $"A building session did not stop within {(int)StopWaitTimeout.TotalSeconds} seconds.");
            }

            await _delay(DefaultPollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits until the copy's first pull has settled (#328), named by <see cref="IWorkspaceAgentSync.StatusFor"/>'s
    /// own <see cref="WorkspaceSyncStatus.InitialSyncComplete"/>: <c>EnsureStarted</c> begins the pull in the
    /// background and <c>CopyPathFor</c> answers a path at once, whether or not the folder it names exists on
    /// disk yet, so starting the CLI right after it with no wait can hand it a working directory that is not
    /// there. Null when the copy became ready in time; otherwise the words to fail the item with, naming the
    /// workspace and the copy path, never the CLI's own opaque "no such file or directory". A recorded
    /// <see cref="IWorkspaceAgentSync.StartFailureFor"/> answers at once, never waiting out the bound.
    /// </summary>
    private async Task<string?> WaitForCopyReadyAsync(string workspaceId, string copyPath, CancellationToken ct)
    {
        var deadline = _clock() + CopyReadyTimeout;
        while (true)
        {
            if (StartFailureReason(workspaceId, copyPath) is { Length: > 0 } failure)
            {
                return failure;
            }

            if (_sync.StatusFor(workspaceId) is WorkspaceSyncStatus { InitialSyncComplete: true })
            {
                return null;
            }

            if (_clock() >= deadline)
            {
                return $"The copy for workspace '{workspaceId}' at '{copyPath}' was not ready within "
                    + $"{(int)CopyReadyTimeout.TotalSeconds} seconds, so the building agent was not started.";
            }

            await _delay(DefaultPollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Words to fail the item with when the workspace's copy has a recorded start failure (#328), naming the
    /// copy path too when it is already known (the wait loop, which read it before this), or just the
    /// workspace when it is not (the branch above that read a null <c>CopyPathFor</c> in the first place,
    /// because <c>WorkspaceAgentSync</c> removes a failed copy's session, so there is no path left to name).
    /// Null when nothing is recorded.
    /// </summary>
    private string? StartFailureReason(string workspaceId, string? copyPath) =>
        _sync.StartFailureFor(workspaceId) is { Length: > 0 } failure
            ? $"The copy for workspace '{workspaceId}'" + (copyPath is { Length: > 0 } ? $" at '{copyPath}'" : string.Empty)
                + $" failed to start: {failure}"
            : null;

    /// <summary>The <see cref="AutopilotBuildResult"/> a copy-readiness refusal always has the same shape of (#328).</summary>
    private static AutopilotBuildResult CopyRefusal(string problem) =>
        new(CommitMade: false, BranchHeadSha: null, CostUsd: 0m, NeedsPerson: false, Problem: problem, Refused: true);

    /// <summary>
    /// Whether this worktree already has uncommitted changes (backlog 332): true for a resumed item whose
    /// earlier, stopped attempt left work there, false for a freshly made worktree. Read on the HOST side of
    /// the bridge (the worktree folder itself), never the contained copy, which need not even be ready yet.
    /// False on anything this cannot read: this is only the one-line note told to the builder's first turn,
    /// never the actual default the commit and its review act on, so a read failure here only withholds the
    /// note. <c>SessionGitWork.DefaultPathsAsync</c> asks the same question for real later and does not make
    /// the same choice on a failure: it reports the failure itself, so the commit is refused in words rather
    /// than read as nothing to commit.
    /// <para>
    /// This is the FIRST git call this process ever makes against a freshly created worktree id, and
    /// <c>BridgeGitCommandRunner.IsAvailable</c> caches whatever that first probe answers for the life of the
    /// process (<c>WorkspaceGit</c>'s own remarks). A transient failure here would therefore pin this
    /// worktree's git as unavailable for every later real commit too, not only for this note, which is a
    /// pre-existing property of the runner's caching and not something a read failure here causes by itself;
    /// it is named so a future change to that caching does not have to rediscover it from this call site.
    /// </para>
    /// </summary>
    private static async Task<bool> IsDirtyAsync(IWorkspaceGit git, string worktreeWorkspaceId, CancellationToken ct)
    {
        try
        {
            if (git.For(worktreeWorkspaceId) is not { Git.IsAvailable: true } services)
            {
                return false;
            }

            var status = await services.Tree.GetStatusAsync(ct).ConfigureAwait(false);
            return status.Branch.Length > 0 && status.StagedEntries.Count + status.UnstagedEntries.Count > 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or HttpRequestException)
        {
            return false;
        }
    }

    /// <summary>
    /// Waits until the turn just sent settles: it ends (no longer <see cref="LiveAgentSession.TurnInFlight"/>),
    /// or a tool call parks in front of a person (<see cref="LiveAgentSession.Pending"/> is non-empty). Throws
    /// past <see cref="_turnTimeout"/>, which a real session never ordinarily reaches: every autopilot call
    /// the rule list and the judge do not settle parks at once, it does not keep this loop spinning.
    /// </summary>
    private async Task WaitForSettledAsync(LiveAgentSession session, WorkItem workItem, CancellationToken ct)
    {
        var deadline = _clock() + _turnTimeout;
        while (session.TurnInFlight && session.Pending.Count == 0)
        {
            if (_clock() >= deadline)
            {
                throw new InvalidOperationException(
                    $"The building session for item #{workItem.Number.ToString(CultureInfo.InvariantCulture)} ran "
                        + $"past its {((int)_turnTimeout.TotalMinutes).ToString(CultureInfo.InvariantCulture)} minute "
                        + "bound without finishing a turn.");
            }

            await _delay(DefaultPollInterval, ct).ConfigureAwait(false);
        }
    }

    private AutopilotBuildResult ReadResult(
        LiveAgentSession session, AgentStatsSnapshot statsBefore, int turnStartIndex, WorkItemId item, int itemNumber)
    {
        var cost = Math.Max(0m, session.Stats.CostUsd - statsBefore.CostUsd);

        // Checked first: a call that parked is "waiting on a person" regardless of anything else the turn
        // may also have done, and it is checked before IsRunning because a parked call leaves the turn (and
        // the process) running, waiting. FailedReviewCount is still drained and reported here (#321): a
        // Failed review captured earlier in THIS turn, before the pending call that is making it wait now,
        // must count against the item's ceiling WHILE it waits, not only once the wait eventually settles
        // through NotCommitted, since ADR 0031 decision 6 says whichever limit fires first stops the item,
        // and the engine already fires money and time limits mid-wait (AutopilotEngine's own Settle). A
        // turn with nothing failed yet drains zero, so an ordinary wait is unaffected.
        if (session.Pending.Count > 0)
        {
            var pending = session.Pending[0];
            return new AutopilotBuildResult(
                CommitMade: false,
                BranchHeadSha: null,
                CostUsd: cost,
                NeedsPerson: true,
                // Work item #356: the call in words (a studio tool's sentence, quoted), never its raw mcp__ name.
                Problem: $"The building session is waiting on a person to approve {ParkedCallWords(pending)} (\"{pending.Detail}\").",
                FailedReviewCount: _sessions.TakeFailedReviewCount(item));
        }

        if (!session.IsRunning)
        {
            // Through NotCommitted, not built inline (#321): a Failed review captured earlier in THIS turn
            // (the session died after a refused attempt_commit, before the turn otherwise finished) must
            // still be counted here, or it is left queued under a session a later retry never binds to
            // again (a fresh session starts and is bound in its place), orphaning it for good. A session
            // with nothing queued reads exactly as it did before this call existed.
            return NotCommitted(
                item, cost, "The building session ended before this turn finished"
                    + (session.FailureReason is { Length: > 0 } reason ? ": " + reason : "."));
        }

        var turn = session.Transcript.Skip(turnStartIndex).ToList();
        if (turn.OfType<TurnCompleted>().LastOrDefault()?.Document is { KnownKind: AgentTurnDocumentKind.Questions } document)
        {
            // Same reason as the pending-approval branch above: a Failed review from earlier in this turn
            // must count now, not only once the question is answered and the turn eventually settles.
            return new AutopilotBuildResult(
                CommitMade: false,
                BranchHeadSha: null,
                CostUsd: cost,
                NeedsPerson: true,
                Problem: "The building agent asked a question: " + document.Message.Trim(),
                FailedReviewCount: _sessions.TakeFailedReviewCount(item));
        }

        // The LAST attempt_commit call in the turn is what matters: an earlier failed attempt the agent then
        // retried must not be read as the turn's outcome once a later call superseded it. Both checks below
        // compare against AttemptCommitCliName (the MCP-prefixed name), not the tool's own bare constant: see
        // that field's own remarks for what is measured and what follows from it for each event checked here.
        for (var i = turn.Count - 1; i >= 0; i--)
        {
            if (turn[i] is ToolCallCompleted completed && completed.ToolName == AttemptCommitCliName)
            {
                if (!completed.Success)
                {
                    return NotCommitted(
                        item, cost, "The building agent's attempt_commit call did not succeed: " + completed.Detail.Trim());
                }

                // An EARLIER attempt_commit in this same turn can have failed review before this LATER one
                // either landed a commit or changed nothing (#321): that earlier failure still owes its own
                // count, taken here rather than left for the Reviewing stage, which (on a landed commit) only
                // ever reads the ONE review for the commit that just landed and would otherwise drain and
                // discard it uncounted.
                var failedBeforeThisOne = _sessions.TakeFailedReviewCount(item);
                return TryReadCommitSha(completed.Detail, out var sha) && sha is { Length: > 0 }
                    ? new AutopilotBuildResult(true, sha, cost, false, null, FailedReviewCount: failedBeforeThisOne)
                    : new AutopilotBuildResult(
                        false, null, cost, false, "This building turn changed nothing, so there was nothing to commit.",
                        ReviewFailed: failedBeforeThisOne > 0, FailedReviewCount: failedBeforeThisOne);
            }

            // A commit the reviewer FAILS is refused by the approval broker before attempt_commit ever runs
            // (backlog 243), so the attempt never produces a ToolCallCompleted at all: it shows up here, as an
            // automatic refusal, instead. NotCommitted below is what reads the Failed review this session's
            // AutopilotBuilderSessions already captured for it and turns that into AutopilotBuildResult
            // .ReviewFailed, which is the only way the engine ever learns a review failed with the real seams.
            if (turn[i] is ToolCallRefused refused && refused.ToolName == AttemptCommitCliName)
            {
                return NotCommitted(item, cost, "The building agent's attempt_commit call was refused: " + refused.Reason.Trim());
            }
        }

        return NotCommitted(
            item, cost, $"The building agent's turn for item #{itemNumber.ToString(CultureInfo.InvariantCulture)} ended without committing anything.");
    }

    /// <summary>
    /// What a turn that did not commit reports: ordinarily just the problem, but every Failed commit review
    /// captured for this session since the last read (consumed here, since the Reviewing stage can never be
    /// reached for an attempt the reviewer already refused) is reported as
    /// <see cref="AutopilotBuildResult.ReviewFailed"/> with its own count instead, which is what lets ADR
    /// 0031 decision 6's per-item review ceiling actually count all of them. More than one can be captured
    /// here (#321): a turn can retry <c>attempt_commit</c> more than once before it ends with nothing
    /// committed, each attempt reviewed on its own, and every failed one counts, not only the last.
    /// </summary>
    private AutopilotBuildResult NotCommitted(WorkItemId item, decimal cost, string problem)
    {
        var failed = _sessions.TakeReviews(item).Where(r => r.Outcome == CommitReviewOutcome.Failed).ToList();
        if (failed.Count > 0)
        {
            return new AutopilotBuildResult(
                CommitMade: false, BranchHeadSha: null, CostUsd: cost, NeedsPerson: false,
                Problem: failed[^1].DenialReason, ReviewFailed: true, FailedReviewCount: failed.Count);
        }

        return new AutopilotBuildResult(CommitMade: false, BranchHeadSha: null, CostUsd: cost, NeedsPerson: false, Problem: problem);
    }

    /// <summary>
    /// Reads <c>item.sha</c> out of an <c>attempt_commit</c> tool result (<see cref="AttemptCommitTool"/>'s own
    /// <c>Changed("committed", {sha, files, note})</c> shape). True when the text parsed as that shape at all,
    /// whether or not it carried a sha (a "nothing_to_commit" result parses fine and carries none).
    /// </summary>
    private static bool TryReadCommitSha(string detail, out string? sha)
    {
        sha = null;
        try
        {
            using var json = JsonDocument.Parse(detail);
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty("item", out var item)
                || item.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (item.TryGetProperty("sha", out var shaProp) && shaProp.ValueKind == JsonValueKind.String)
            {
                sha = shaProp.GetString();
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Title(WorkItem item) =>
        "Autopilot: #" + item.Number.ToString(CultureInfo.InvariantCulture) + " " + item.Title;

    /// <summary>
    /// The model the building agent starts on (requested 2026-10-22, model and effort per work item): the item's
    /// own suggestion when it has one, because the person who wrote the item knows how hard it is, otherwise the
    /// dispatch's builder model exactly as before. The dispatch form pre-fills that model from Settings &gt; Agents &gt;
    /// Default models and effort (work item #357), so the default reaches a run through the person's own per-run pick;
    /// a dispatch that names no model at all falls back to the default here. The operator and the commit reviewer
    /// never read the suggestion or the default.
    /// </summary>
    internal static string? BuilderModel(WorkItem item, AutopilotRole builder, AgentDefault? defaults = null) =>
        item.SuggestedModel is { } suggested
            ? suggested.CliId()
            : string.IsNullOrWhiteSpace(builder.Model) ? defaults?.Model : builder.Model;

    /// <summary>
    /// The effort the building agent starts at (work item #357): the item's suggestion, otherwise the autopilot
    /// builder's default effort from Settings, otherwise none (no flag, the CLI's own default). A dispatch has no
    /// effort of its own, so the default is read here rather than carried on the run.
    /// </summary>
    internal static AgentEffort? BuilderEffort(WorkItem item, AgentDefault? defaults = null) =>
        item.SuggestedEffort ?? defaults?.Effort;
}
