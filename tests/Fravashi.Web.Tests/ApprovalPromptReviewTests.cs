using Bunit;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Web.Components.Sessions;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 243: a commit card says what the commit reviewer role did before the person was asked. A pass shows the
/// role, runtime, model and the reviewer's summary; "not reviewed" (the role off, a review with no verdict) is drawn
/// like a warning and never reads like a review.
/// </summary>
public class ApprovalPromptReviewTests
{
    private static readonly CommitReviewerRole Role = CommitReviewerSetting.Default;

    private static PendingToolApproval Commit(CommitReview? review) => new(
        "r1",
        AgentSessionId.New(),
        "mcp__fravashi__git_commit",
        "feat: x",
        "t1",
        new Dictionary<string, string> { ["message"] = "feat: x" },
        DateTimeOffset.UtcNow,
        Review: review);

    [Fact]
    public void A_pass_shows_the_role_runtime_model_and_the_reviewers_summary()
    {
        using var ctx = new BunitContext();
        var conversation = AgentSessionId.New();
        var cut = ctx.Render<ApprovalPrompt>(p => p.Add(c => c.Request, Commit(new CommitReview(
            CommitReviewOutcome.Passed, Role, Summary: "Checked all three criteria.", Conversation: conversation,
            Duration: TimeSpan.FromSeconds(95), Tokens: 41_000))));

        var block = cut.Find("[data-testid=approval-review]");
        Assert.Contains("session-approval-review-pass", block.ClassList);
        Assert.Equal("Reviewed by commit reviewer (Claude Code/claude-fable-5-1): pass",
            cut.Find("[data-testid=approval-review-line]").TextContent.Trim());
        Assert.Equal("Checked all three criteria.", cut.Find("[data-testid=approval-review-summary]").TextContent.Trim());
        var meta = cut.Find("[data-testid=approval-review-meta]").TextContent;
        Assert.Contains(conversation.Value.ToString("D"), meta);
        Assert.Contains("41,000 tokens", meta);
        Assert.NotEmpty(cut.FindAll("[data-testid=approve]"));
    }

    [Fact]
    public void With_the_role_off_the_card_says_not_reviewed_like_a_warning()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<ApprovalPrompt>(p => p.Add(c => c.Request, Commit(CommitReview.NotReviewed(Role with { Enabled = false }))));

        Assert.Contains("session-approval-review-none", cut.Find("[data-testid=approval-review]").ClassList);
        Assert.Equal("Not reviewed: the commit reviewer role is off in Settings > Agents > Commit reviewer.",
            cut.Find("[data-testid=approval-review-line]").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid=approval-review-summary]"));
    }

    [Fact]
    public void A_review_that_timed_out_says_so_and_never_shows_a_summary_as_if_it_passed()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<ApprovalPrompt>(p => p.Add(c => c.Request, Commit(new CommitReview(
            CommitReviewOutcome.TimedOut, Role, Summary: "half a thought", Problem: "It ran past its 10 minute bound and was stopped."))));

        var line = cut.Find("[data-testid=approval-review-line]").TextContent;
        Assert.StartsWith("Not reviewed:", line.Trim());
        Assert.Contains("10 minute bound", line);
        Assert.Empty(cut.FindAll("[data-testid=approval-review-summary]"));
    }

    [Fact]
    public void A_card_with_no_review_shows_no_review_block()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<ApprovalPrompt>(p => p.Add(c => c.Request, Commit(null)));

        Assert.Empty(cut.FindAll("[data-testid=approval-review]"));
    }
}
