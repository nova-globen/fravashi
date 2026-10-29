using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Autopilot;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Autopilot;

/// <summary>
/// #309's process-local tracker: which session is building each item, and the commit review captured for
/// it. Not persisted on purpose (see the class's own remarks): these tests only cover what happens inside one
/// process's lifetime.
/// </summary>
public sealed class AutopilotBuilderSessionsTests
{
    private static readonly WorkItemId Item1 = new("item-1");
    private static readonly WorkItemId Item2 = new("item-2");
    private static readonly CommitReviewerRole Role = new(true, AgentTool.Claude, "claude-sonnet-5");

    [Fact]
    public void Binding_an_item_makes_its_session_findable_again()
    {
        var broker = new FakeBroker();
        var sessions = new AutopilotBuilderSessions(broker);
        var session = AgentSessionId.New();

        sessions.Bind(Item1, session);

        sessions.SessionFor(Item1).Should().Be(session);
        sessions.SessionFor(Item2).Should().BeNull();
    }

    [Fact]
    public void A_review_for_a_bound_session_is_captured_and_read_exactly_once()
    {
        var broker = new FakeBroker();
        var sessions = new AutopilotBuilderSessions(broker);
        var session = AgentSessionId.New();
        sessions.Bind(Item1, session);
        var review = new CommitReview(CommitReviewOutcome.Passed, Role, Summary: "looks fine");

        broker.Raise(session, review);

        sessions.TakeReview(Item1).Should().Be(review);
        sessions.TakeReview(Item1).Should().BeNull("a review is consumed once, never read again for a later commit");
    }

    /// <summary>
    /// #321: a single turn can call attempt_commit more than once before it ends, each attempt reviewed on
    /// its own. Every one of those reviews must still be there to answer for, oldest first, not only the
    /// latest (which is all a single overwritten slot could ever hold).
    /// </summary>
    [Fact]
    public void Two_reviews_for_the_same_session_before_a_read_are_both_kept_oldest_first()
    {
        var broker = new FakeBroker();
        var sessions = new AutopilotBuilderSessions(broker);
        var session = AgentSessionId.New();
        sessions.Bind(Item1, session);
        var first = new CommitReview(CommitReviewOutcome.Failed, Role, Summary: "first attempt");
        var second = new CommitReview(CommitReviewOutcome.Failed, Role, Summary: "second attempt");

        broker.Raise(session, first);
        broker.Raise(session, second);

        sessions.TakeReviews(Item1).Should().Equal(first, second);
        sessions.TakeReviews(Item1).Should().BeEmpty("both were drained by the read above");
    }

    /// <summary>
    /// #321 (third pass): the review queue is keyed by item so a same-run rebind (a crashed session
    /// restarted mid-wait) survives it, but limits are set per dispatch (ADR 0031 decision 6), and an
    /// earlier run's own unread review must never count against a LATER run's fresh attempt at the same
    /// item (decision 12, #305).
    /// </summary>
    [Fact]
    public void A_rebind_under_a_different_run_drops_the_items_unread_reviews()
    {
        var broker = new FakeBroker();
        var sessions = new AutopilotBuilderSessions(broker);
        var runA = Guid.NewGuid();
        var runB = Guid.NewGuid();
        var firstSession = AgentSessionId.New();
        sessions.Bind(Item1, firstSession, runA);
        broker.Raise(firstSession, new CommitReview(CommitReviewOutcome.Failed, Role, Summary: "run A's own unread failure"));

        var secondSession = AgentSessionId.New();
        sessions.Bind(Item1, secondSession, runB);

        sessions.TakeReviews(Item1).Should().BeEmpty("run A's own review must never count against run B's fresh attempt");
    }

    /// <summary>The same-run counterpart: a rebind that keeps the run id (a crashed session restarted mid-wait, #321's second gap) keeps every review still queued.</summary>
    [Fact]
    public void A_rebind_under_the_same_run_keeps_the_items_unread_reviews()
    {
        var broker = new FakeBroker();
        var sessions = new AutopilotBuilderSessions(broker);
        var run = Guid.NewGuid();
        var firstSession = AgentSessionId.New();
        sessions.Bind(Item1, firstSession, run);
        var review = new CommitReview(CommitReviewOutcome.Failed, Role, Summary: "captured just before the session died");
        broker.Raise(firstSession, review);

        var secondSession = AgentSessionId.New();
        sessions.Bind(Item1, secondSession, run);

        sessions.TakeReviews(Item1).Should().Equal(review);
    }

    [Fact]
    public void A_review_for_a_session_never_bound_here_is_ignored()
    {
        var broker = new FakeBroker();
        var sessions = new AutopilotBuilderSessions(broker);
        var someoneElses = AgentSessionId.New();

        broker.Raise(someoneElses, new CommitReview(CommitReviewOutcome.Passed, Role));

        sessions.TakeReview(Item1).Should().BeNull();
    }

    [Fact]
    public void No_review_yet_for_a_bound_item_reads_as_null()
    {
        var broker = new FakeBroker();
        var sessions = new AutopilotBuilderSessions(broker);
        sessions.Bind(Item1, AgentSessionId.New());

        sessions.TakeReview(Item1).Should().BeNull();
    }

    /// <summary>
    /// Backlog 332: true only once BOTH the item-\>session and the item-\>worktree bindings exist; a session
    /// this tracker never bound at all (a person's own conversation) and a bound session whose item has no
    /// worktree (a cap-1 item, sharing the ordinary workspace) are both false.
    /// </summary>
    [Fact]
    public void OwnsWorktree_is_true_only_once_the_session_and_the_worktree_are_both_bound_to_the_same_item()
    {
        var broker = new FakeBroker();
        var sessions = new AutopilotBuilderSessions(broker);
        var worktreeSession = AgentSessionId.New();
        var sharedSession = AgentSessionId.New();
        var strangerSession = AgentSessionId.New();
        sessions.Bind(Item1, worktreeSession);
        sessions.BindWorktree(Item1, "repo-w1", "w1-worktree-1", 1);
        sessions.Bind(Item2, sharedSession);

        sessions.OwnsWorktree(worktreeSession).Should().BeTrue();
        sessions.OwnsWorktree(sharedSession).Should().BeFalse("item 2 was never given a worktree");
        sessions.OwnsWorktree(strangerSession).Should().BeFalse("this tracker never bound this session at all");
    }

    [Fact]
    public void Disposing_stops_listening_for_reviews()
    {
        var broker = new FakeBroker();
        var sessions = new AutopilotBuilderSessions(broker);
        var session = AgentSessionId.New();
        sessions.Bind(Item1, session);

        sessions.Dispose();
        broker.Raise(session, new CommitReview(CommitReviewOutcome.Passed, Role));

        sessions.TakeReview(Item1).Should().BeNull();
    }

    /// <summary>A broker that answers nothing real: only <see cref="Reviewed"/> is ever used here.</summary>
    private sealed class FakeBroker : IAgentApprovalBroker
    {
#pragma warning disable CS0067 // never raised: this fake only ever exercises Reviewed
        public event Action<PendingToolApproval>? Requested;
        public event Action<PendingToolApproval, ToolApprovalDecision>? Resolved;
        public event Action<AutomaticToolRefusal>? Refused;
        public event Action<TrustedToolCall>? Trusted;
        public event Action<AutopilotAllowedCall>? AutopilotAllowed;
        public event Action<AgentSessionId>? Released;
#pragma warning restore CS0067
        public event Action<AgentSessionId, CommitReview>? Reviewed;

        public void Raise(AgentSessionId session, CommitReview review) => Reviewed?.Invoke(session, review);

        public string RegisterSession(
            AgentSessionId sessionId, string? workspaceRoot = null, bool typedTurns = false,
            string? workspaceId = null, string? memoryRoot = null, string? memoryTarget = null,
            string? toolResultsRoot = null) =>
            throw new NotSupportedException();

        public void ReleaseSession(AgentSessionId sessionId) => throw new NotSupportedException();

        public void SetToolResultsRoot(AgentSessionId sessionId, string? toolResultsRoot) => throw new NotSupportedException();

        public void HoldToReview(AgentSessionId sessionId) => throw new NotSupportedException();

        public void HoldToGraphOnly(AgentSessionId sessionId) => throw new NotSupportedException();

        public bool IsGraphOnly(AgentSessionId sessionId) => false;

        public void HoldToAssistant(AgentSessionId sessionId) => throw new NotSupportedException();

        public bool IsAssistant(AgentSessionId sessionId) => false;

        public void SetAutopilot(AgentSessionId sessionId, bool autopilot) => throw new NotSupportedException();

        public bool IsAutopilot(AgentSessionId sessionId) => throw new NotSupportedException();

        public AgentSessionId? ResolveSecret(string? secret) => throw new NotSupportedException();

        public Task<ToolApprovalDecision> RequestAsync(
            AgentSessionId sessionId, string toolName, string? toolUseId, AgentToolInput input,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public bool Answer(string requestId, bool approved, string reason) => throw new NotSupportedException();

        public IReadOnlyList<PendingToolApproval> PendingFor(AgentSessionId sessionId) => throw new NotSupportedException();

        public ToolApprovalDecision Refuse(AgentSessionId sessionId, string toolName, string detail, string reason) =>
            throw new NotSupportedException();
    }
}
