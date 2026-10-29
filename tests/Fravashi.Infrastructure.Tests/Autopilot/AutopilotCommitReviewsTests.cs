using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Autopilot;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Autopilot;

/// <summary>
/// The real <see cref="IAutopilotCommitReviews"/> (#309): reads whatever <see cref="AutopilotBuilderSessions"/>
/// already captured for the item's building session, since the review ran synchronously inside the approval
/// broker before the Reviewing stage ever asks.
/// </summary>
public sealed class AutopilotCommitReviewsTests
{
    private static readonly WorkItemId Item1 = new("item-1");
    private static readonly CommitReviewerRole Role = new(true, AgentTool.Claude, "claude-sonnet-5");

    [Fact]
    public async Task A_captured_review_reaches_the_seam()
    {
        var broker = new RaisingBroker();
        var sessions = new AutopilotBuilderSessions(broker);
        var session = AgentSessionId.New();
        sessions.Bind(Item1, session);
        var review = new CommitReview(CommitReviewOutcome.Passed, Role, Summary: "clean");
        broker.Raise(session, review);

        var result = await new AutopilotCommitReviews(sessions).ReviewAsync(Item1);

        result.Should().Be(review);
    }

    [Fact]
    public async Task No_review_captured_yet_throws_rather_than_guessing_a_verdict()
    {
        var sessions = new AutopilotBuilderSessions(new RaisingBroker());

        var act = () => new AutopilotCommitReviews(sessions).ReviewAsync(Item1);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private sealed class RaisingBroker : IAgentApprovalBroker
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
