using System.Collections.Concurrent;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.WorkItems;

namespace Fravashi.Infrastructure.Autopilot;

/// <summary>
/// Where a still-unresolved <c>NeedsPerson</c> turn began (ADR 0031 decision 12, #305): the transcript index
/// right before it was sent, pinned for as long as the turn stays unresolved so a later re-check reads the
/// WHOLE turn's history once it settles, never only what happened since the last check; and the stats
/// snapshot as of the LAST time this was observed, used only for the cost DELTA a re-check reports, updated
/// on every observation so an unchanged wait reports zero and a turn that kept working while nobody looked
/// reports only what actually ran since the previous look.
/// </summary>
/// <param name="TranscriptIndex">Pinned at send time; never moves while the turn stays unresolved.</param>
/// <param name="LastObservedStats">Updated on every observation, sent or re-checked alike.</param>
public sealed record AutopilotTurnStart(int TranscriptIndex, AgentStatsSnapshot LastObservedStats);

/// <summary>
/// Tracks, for the lifetime of this studio process, which ordinary studio session (ADR 0031 decision 11) is
/// building each marked item, and every commit review (backlog 243) an <c>attempt_commit</c> call for that
/// ITEM produced since the last read, oldest first, kept against the item rather than whichever session
/// happened to be building it at the time (#321: a session can be replaced mid-item, and a review must
/// survive that). The Reviewing stage (#309) reads the latest from here rather than asking the reviewer a
/// second time, because the review already ran synchronously inside the broker's approval of that very
/// commit, before the building turn that made it even finished; the building seam's own "no commit" path
/// (#321) reads every one of them, because a single turn can retry <c>attempt_commit</c> more than once
/// before it ends, each attempt reviewed on its own.
/// <para>
/// <b>Deliberately NOT persisted.</b> A studio restart always stops any run whose current item was mid-
/// Building, -Reviewing, -Delivering or -Verifying (<see cref="Fravashi.Domain.Autopilot.AutopilotItemStages
/// .MayHaveBeenInFlight"/>, via <see cref="Fravashi.Domain.Autopilot.AutopilotDiscipline.OnRestart"/>), so
/// nothing recorded here is ever read again after the process that wrote it ends; keeping it in memory only
/// avoids a schema step for state nothing outlives.
/// </para>
/// </summary>
public sealed class AutopilotBuilderSessions : IDisposable
{
    private readonly IAgentApprovalBroker _broker;
    private readonly ConcurrentDictionary<WorkItemId, AgentSessionId> _sessions = new();
    private readonly ConcurrentDictionary<WorkItemId, Guid> _itemRuns = new();
    private readonly ConcurrentDictionary<AgentSessionId, WorkItemId> _items = new();
    private readonly ConcurrentDictionary<WorkItemId, ConcurrentQueue<CommitReview>> _reviews = new();
    private readonly ConcurrentDictionary<AgentSessionId, AutopilotTurnStart> _turnStarts = new();
    private readonly ConcurrentDictionary<WorkItemId, (string RepositoryWorkspaceId, string WorktreeWorkspaceId, int ItemNumber)> _worktrees = new();

    public AutopilotBuilderSessions(IAgentApprovalBroker broker)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _broker.Reviewed += OnReviewed;
    }

    /// <summary>The session building this item in this process, or null when none has started one yet.</summary>
    public AgentSessionId? SessionFor(WorkItemId item) =>
        _sessions.TryGetValue(item, out var session) ? session : null;

    /// <summary>
    /// Which run bound the session <see cref="SessionFor"/> currently answers for this item, or null when
    /// none has started one yet (ADR 0031 decision 12, #305). A parked item resumed by a LATER run must never
    /// be read as a re-check, or sent a turn into, the session an EARLIER run bound for the same item: this is
    /// what <see cref="AgentAutopilotBuilder.BuildAsync"/> compares against its own call's run id to tell the
    /// two apart.
    /// </summary>
    public Guid? RunFor(WorkItemId item) => _itemRuns.TryGetValue(item, out var run) ? run : null;

    /// <summary>
    /// The item this session is (or was) building in this process, or null for a session this tracker never
    /// bound (a person's own conversation, or one from a different workspace). Used to tell "a person's own
    /// session is occupying the workspace" apart from "an earlier item's own session still is", since only
    /// the autopilot may end the second kind to make room for the next item.
    /// </summary>
    public WorkItemId? ItemFor(AgentSessionId session) =>
        _items.TryGetValue(session, out var item) ? item : null;

    /// <summary>
    /// Records which session is building an item FOR THIS RUN, so a later tick in this process finds it
    /// again, and so a DIFFERENT, later run resuming the same item can tell its own attempt apart from an
    /// earlier run's leftover one (#305). <paramref name="runId"/> defaults to <see cref="Guid.Empty"/> for
    /// callers that only ever bind one item to one session and never exercise cross-run isolation (every
    /// test of a DIFFERENT seam that uses this class as test setup, not <see cref="AgentAutopilotBuilder"/>
    /// itself, which always passes its own call's real run id).
    /// <para>
    /// A rebind under a DIFFERENT run than the one already recorded for this item drops any review still
    /// queued for it (#321, third pass): the review queue is keyed by item so a same-run rebind (a crashed
    /// session restarted mid-wait) can survive it, but limits are set per dispatch (ADR 0031 decision 6),
    /// and an earlier run's unread review belongs to THAT run's own ceiling, never to a later run's fresh
    /// attempt at the same item (decision 12, #305: a later run never reads an earlier run's session state).
    /// A same-run rebind (the run id matches what is already recorded) never clears anything.
    /// </para>
    /// </summary>
    public void Bind(WorkItemId item, AgentSessionId session, Guid runId = default)
    {
        if (_itemRuns.TryGetValue(item, out var previousRun) && previousRun != runId)
        {
            _reviews.TryRemove(item, out _);
        }

        _sessions[item] = session;
        _itemRuns[item] = runId;
        _items[session] = item;
    }

    /// <summary>
    /// The latest review captured for this item's building session, consumed once along with every earlier
    /// one piled up since the last read (see <see cref="TakeReviews"/>): the Reviewing stage reads it exactly
    /// once per commit, and a stale answer must never be read again for a later commit on the same session.
    /// </summary>
    public CommitReview? TakeReview(WorkItemId item)
    {
        var all = TakeReviews(item);
        return all.Count == 0 ? null : all[^1];
    }

    /// <summary>
    /// Every review captured for this ITEM (not its current session, see the remarks below) since the last
    /// time this (or <see cref="TakeReview"/>) drained it, oldest first, consumed once. A turn can retry
    /// <c>attempt_commit</c> more than once before it ends (#321): each retry is reviewed separately by
    /// <see cref="OnReviewed"/>, and a single slot holding only the latest one would silently under-count a
    /// turn with more than one failure in it (every review but the last is overwritten before anything ever
    /// reads it), which is a real way the per-item ceiling can be undercounted, even though it alone does
    /// not explain a turn that ends with NOTHING counted: that needs a turn whose own ReadResult never
    /// reaches a read of this at all (NeedsPerson, the session no longer running, or a commit landing with
    /// no failure counted alongside it, see <see cref="TakeFailedReviewCount"/>).
    /// <para>
    /// Keyed by item, not by session (#321, second pass): a review captured while a turn was waiting on an
    /// UNRELATED pending approval is queued under the session that was building at the time, and if that
    /// session then ends before anyone answers (a crash, or a person ending the conversation), the next
    /// <c>BuildAsync</c> call binds a FRESH session to the same item. A slot keyed by session would leave the
    /// queued review stranded under the dead session's id forever; keyed by item, it survives the rebind.
    /// </para>
    /// </summary>
    public IReadOnlyList<CommitReview> TakeReviews(WorkItemId item)
    {
        if (!_reviews.TryGetValue(item, out var queue))
        {
            return [];
        }

        var drained = new List<CommitReview>();
        while (queue.TryDequeue(out var review))
        {
            drained.Add(review);
        }

        return drained;
    }

    /// <summary>
    /// Removes and counts every FAILED review queued for this item, leaving anything else (most commonly a
    /// single Passed review, for a commit that just landed) queued for <see cref="TakeReview"/> to answer
    /// later. A turn that retries <c>attempt_commit</c>, failing one or more times before it finally
    /// succeeds, still owes every one of those failures a count (#321): they cannot wait for the Reviewing
    /// stage, which only ever reads the ONE review that belongs to the commit that landed
    /// (<c>AutopilotCommitReviews.ReviewAsync</c>), so without this a failure that preceded an eventual
    /// success would be drained there and discarded uncounted.
    /// </summary>
    public int TakeFailedReviewCount(WorkItemId item)
    {
        if (!_reviews.TryGetValue(item, out var queue))
        {
            return 0;
        }

        var kept = new List<CommitReview>();
        var failedCount = 0;
        while (queue.TryDequeue(out var review))
        {
            if (review.Outcome == CommitReviewOutcome.Failed)
            {
                failedCount++;
            }
            else
            {
                kept.Add(review);
            }
        }

        foreach (var review in kept)
        {
            queue.Enqueue(review);
        }

        return failedCount;
    }

    /// <summary>
    /// Where this session's still-unresolved <c>NeedsPerson</c> turn began, or null when its last read was
    /// settled (ADR 0031 decision 12, #305). <see cref="AgentAutopilotBuilder.BuildAsync"/> reads this to
    /// decide whether a call is a fresh send or a re-check that must never send anything.
    /// </summary>
    public AutopilotTurnStart? TurnStart(AgentSessionId session) =>
        _turnStarts.TryGetValue(session, out var start) ? start : null;

    /// <summary>Records where an unresolved turn began, or updates its last-observed stats on a later re-check.</summary>
    public void RecordTurnStart(AgentSessionId session, AutopilotTurnStart start) => _turnStarts[session] = start;

    /// <summary>The turn settled (it is no longer waiting on a person): nothing here is read again for it.</summary>
    public void ClearTurnStart(AgentSessionId session) => _turnStarts.TryRemove(session, out _);

    /// <summary>
    /// Records that this item's current building session runs in its OWN worktree (#308, a parallel run
    /// only), so <see cref="AgentAutopilotBuilder.EndItemAsync"/> can prune and unregister it without
    /// re-resolving anything: the repository it was made from (the bridge's own prune primitive is keyed by
    /// that id and the item number), and the worktree's OWN workspace id (what the studio's workspace store
    /// drops once the bridge confirms the worktree itself is gone).
    /// </summary>
    public void BindWorktree(WorkItemId item, string repositoryWorkspaceId, string worktreeWorkspaceId, int itemNumber) =>
        _worktrees[item] = (repositoryWorkspaceId, worktreeWorkspaceId, itemNumber);

    /// <summary>The worktree this item's building session runs in, or null for a cap-1 (sequential) item.</summary>
    public (string RepositoryWorkspaceId, string WorktreeWorkspaceId, int ItemNumber)? WorktreeFor(WorkItemId item) =>
        _worktrees.TryGetValue(item, out var value) ? value : null;

    /// <summary>The item's worktree record is gone (pruned, or never had one): nothing here is read again for it.</summary>
    public void ClearWorktree(WorkItemId item) => _worktrees.TryRemove(item, out _);

    /// <summary>
    /// True when this session is an autopilot builder running in the git worktree #308 made for its OWN item
    /// (never a cap-1 item, which shares the ordinary workspace, and never a person's own conversation, which
    /// this tracker never bound at all). Backlog 332: that worktree belongs to the ITEM, never to a person, so
    /// everything uncommitted in it when this session commits is this attempt's to commit and review, whether
    /// this exact session wrote it or an earlier, stopped one left it there.
    /// </summary>
    public bool OwnsWorktree(AgentSessionId session) =>
        ItemFor(session) is { } item && WorktreeFor(item) is not null;

    private void OnReviewed(AgentSessionId session, CommitReview review)
    {
        // Only a session this tracker bound is ours to remember: the broker's Reviewed event fires for every
        // agent commit in the studio, not only the autopilot's. Queued by ITEM, never overwritten (#321): a
        // turn that retries attempt_commit more than once reviews each attempt, and every one of them must
        // still be there to answer for once the turn ends, even if the session building it is replaced first
        // (TakeReviews' own remarks have why this is keyed by item and not by session).
        if (_items.TryGetValue(session, out var item))
        {
            _reviews.GetOrAdd(item, static _ => new ConcurrentQueue<CommitReview>()).Enqueue(review);
        }
    }

    public void Dispose() => _broker.Reviewed -= OnReviewed;
}
