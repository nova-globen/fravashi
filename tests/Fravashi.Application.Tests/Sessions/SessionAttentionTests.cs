using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Xunit;

namespace Fravashi.Application.Tests.Sessions;

/// <summary>
/// Backlog 244: the vocabulary the operator's attention is counted in. A tool's kind is decided by its name alone, and
/// the one line every surface shows says what a person was asked apart from what the studio decided on its own.
/// </summary>
public sealed class SessionAttentionTests
{
    private const string Studio = AgentWorkGate.StudioToolPrefix;

    [Theory]
    [InlineData("Edit", AttentionToolKind.Edit)]
    [InlineData("MultiEdit", AttentionToolKind.Edit)]
    [InlineData("NotebookEdit", AttentionToolKind.Edit)]
    [InlineData("Write", AttentionToolKind.Write)]
    [InlineData("Bash", AttentionToolKind.Shell)]
    [InlineData("Read", AttentionToolKind.Read)]
    [InlineData("Grep", AttentionToolKind.Read)]
    [InlineData(Studio + "git_commit", AttentionToolKind.Commit)]
    [InlineData(Studio + "git_push", AttentionToolKind.Git)]
    [InlineData(Studio + "git_tag", AttentionToolKind.Git)]
    [InlineData(Studio + "attempt_branch", AttentionToolKind.Git)]
    [InlineData(Studio + "attempt_commit", AttentionToolKind.Commit)]
    [InlineData(Studio + "git_diff", AttentionToolKind.Read)]
    [InlineData(Studio + "work_item_transition", AttentionToolKind.StatusMove)]
    [InlineData(Studio + "work_item_update", AttentionToolKind.GraphWrite)]
    [InlineData(Studio + "work_item_evidence", AttentionToolKind.GraphWrite)]
    [InlineData(Studio + "wiki_write", AttentionToolKind.GraphWrite)]
    [InlineData(Studio + "design_approve", AttentionToolKind.GraphWrite)]
    [InlineData(Studio + "work_item_read", AttentionToolKind.Read)]
    [InlineData(Studio + "session_attention", AttentionToolKind.Read)]
    [InlineData(Studio + "work_item_scenario_run", AttentionToolKind.Shell)]
    [InlineData(Studio + "browser_open", AttentionToolKind.Browser)]
    [InlineData(Studio + "browser_fill", AttentionToolKind.Browser)]
    [InlineData(Studio + "studio_upgrade", AttentionToolKind.Other)]
    [InlineData("Agent", AttentionToolKind.Other)]
    [InlineData("mcp__other__git_commit", AttentionToolKind.Other)]
    [InlineData("", AttentionToolKind.Other)]
    public void A_tools_kind_is_decided_by_its_name(string tool, AttentionToolKind kind) =>
        AttentionToolKinds.Of(tool).Should().Be(kind);

    [Theory]
    [InlineData(0, "0 s")]
    [InlineData(45, "45 s")]
    [InlineData(60, "1 min")]
    [InlineData(260, "4 min 20 s")]
    [InlineData(3780, "1 h 3 min")]
    public void A_wait_is_said_in_words(int seconds, string said) =>
        SessionAttention.Duration(TimeSpan.FromSeconds(seconds)).Should().Be(said);

    [Fact]
    public void Nothing_counted_says_so_plainly() =>
        SessionAttention.Empty.Line.Should().Be("0 approvals asked");

    [Fact]
    public void Every_stored_name_reads_back_as_itself()
    {
        foreach (var kind in Enum.GetValues<AttentionToolKind>())
        {
            AttentionNames.KindFrom(AttentionNames.Of(kind)).Should().Be(kind);
        }

        foreach (var outcome in Enum.GetValues<AttentionOutcome>())
        {
            AttentionNames.OutcomeFrom(AttentionNames.Of(outcome)).Should().Be(outcome);
        }

        foreach (var review in Enum.GetValues<CommitReviewOutcome>())
        {
            AttentionNames.ReviewFrom(AttentionNames.Of(review)).Should().Be(review);
        }

        AttentionNames.OutcomeFrom("approved_by_magic").Should().BeNull();
    }

    [Fact]
    public void The_opening_prompt_is_not_guidance_and_every_later_turn_is()
    {
        var session = AgentSessionId.New();
        var at = DateTimeOffset.UnixEpoch;

        SessionAttention.From([AttentionEntry.Turn(session, at)]).GuidanceTurns.Should().Be(0);
        SessionAttention.From(
                [AttentionEntry.Turn(session, at), AttentionEntry.Turn(session, at), AttentionEntry.Turn(session, at)])
            .GuidanceTurns.Should().Be(2);
    }

    [Fact]
    public void Calls_the_studio_decided_on_its_own_are_beside_the_headline_and_never_inside_it()
    {
        var session = AgentSessionId.New();
        var at = DateTimeOffset.UnixEpoch;
        var counted = SessionAttention.From(
        [
            AttentionEntry.Decision(session, AttentionToolKind.Read, AttentionOutcome.Auto, TimeSpan.Zero, at),
            AttentionEntry.Decision(session, AttentionToolKind.Read, AttentionOutcome.Auto, TimeSpan.Zero, at),
            AttentionEntry.Decision(session, AttentionToolKind.Shell, AttentionOutcome.Refused, TimeSpan.Zero, at),
            AttentionEntry.Decision(session, AttentionToolKind.Commit, AttentionOutcome.Approved, TimeSpan.FromSeconds(9), at),
            AttentionEntry.ReviewOf(session, CommitReviewOutcome.Failed, at),
            AttentionEntry.ReviewOf(session, CommitReviewOutcome.Passed, at),
        ]);

        counted.Asked.Should().Be(1);
        counted.AskedByKind.Should().ContainSingle().Which.Key.Should().Be(AttentionToolKind.Commit);
        counted.Line.Should().Be(
            "1 approval asked: 1 approved; waited 9 s in all; 2 auto, 1 refused without asking; 2 reviews: 1 pass, 1 fail");
        counted.ByKind.Should().Be("commit 1");
    }

    [Fact]
    public void An_autopilot_decision_is_counted_apart_from_a_person_and_from_the_ordinary_rules()
    {
        var session = AgentSessionId.New();
        var at = DateTimeOffset.UnixEpoch;
        var counted = SessionAttention.From(
        [
            AttentionEntry.Decision(session, AttentionToolKind.Shell, AttentionOutcome.AutopilotRule, TimeSpan.Zero, at),
            AttentionEntry.Decision(session, AttentionToolKind.StatusMove, AttentionOutcome.AutopilotRule, TimeSpan.Zero, at),
            AttentionEntry.Decision(session, AttentionToolKind.Shell, AttentionOutcome.AutopilotJudge, TimeSpan.Zero, at),
            AttentionEntry.Decision(session, AttentionToolKind.Commit, AttentionOutcome.Approved, TimeSpan.FromSeconds(1), at),
        ]);

        counted.Asked.Should().Be(1);
        counted.ByRule.Should().Be(2);
        counted.ByJudge.Should().Be(1);
        counted.Line.Should().Be(
            "1 approval asked: 1 approved; waited 1 s in all; 2 by rule, 1 by judge");
    }

    [Fact]
    public void The_two_autopilot_outcomes_have_their_own_words()
    {
        SessionAttention.Words(AttentionOutcome.AutopilotRule).Should().Be("by rule");
        SessionAttention.Words(AttentionOutcome.AutopilotJudge).Should().Be("by judge");
    }
}
