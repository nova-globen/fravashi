using AwesomeAssertions;
using Fravashi.Domain.AgentSessions;
using Xunit;

namespace Fravashi.Domain.Tests.AgentSessions;

/// <summary>
/// Work item #333: a brand new building session's first turn carries the work gate's steps and the commit
/// reviewer's failure classes, kept short because every word is re-sent with every call for the rest of the
/// conversation.
/// </summary>
public sealed class AgentFirstTurnBriefingTests
{
    [Fact]
    public void The_note_carries_the_gates_steps_verbatim() =>
        AgentFirstTurnBriefing.Note.Should().Contain(AgentWorkGate.FirstTurnSteps);

    [Fact]
    public void The_note_carries_every_reviewer_failure_classs_short_name_from_the_shared_source()
    {
        foreach (var shortName in CommitReviewFailureClasses.ShortNames)
        {
            AgentFirstTurnBriefing.Note.Should().Contain(shortName);
        }
    }

    [Fact]
    public void The_note_asks_to_check_the_change_against_the_reviewers_classes_before_committing()
    {
        var note = AgentFirstTurnBriefing.Note;
        note.Should().Contain(AgentFirstTurnBriefing.CheckBeforeCommit);
        note.IndexOf(AgentFirstTurnBriefing.CheckBeforeCommit, StringComparison.Ordinal).Should().BeLessThan(
            note.IndexOf(CommitReviewFailureClasses.ShortNames[0], StringComparison.Ordinal),
            "the instruction to check comes before the classes it is checking against");
    }

    /// <summary>
    /// Commit review's findings (two rounds): an unconditional "name or create the work item" sentence would
    /// misdirect an autopilot building session, which <c>AgentAutopilotBuilder.BuildAsync</c> starts already
    /// linked to its item (always) and its plan (only when the item carries one, lines 455 to 458), into redoing
    /// a link it already has; and a sentence claiming such a session "already has both" would be false, and
    /// would invite it to skip a plan it does not have. The gate's steps must be a condition, never a claim
    /// about what any particular session already holds.
    /// </summary>
    [Fact]
    public void The_gates_steps_do_not_misdirect_an_already_linked_session()
    {
        AgentWorkGate.FirstTurnSteps.Should().Contain("If it is not already linked")
            .And.Contain("session_governance_read")
            .And.NotContain("already has both");
    }

    /// <summary>
    /// A bound chosen for this item (plan @150), not a measured claim: every word of this note is re-sent with
    /// every call, so a future edit that quietly grows it should fail a test rather than only show up in a
    /// token bill.
    /// </summary>
    [Fact]
    public void The_note_stays_short()
    {
        AgentFirstTurnBriefing.Note.Length.Should().BeLessThanOrEqualTo(900);
        (AgentGitInBash.OpeningNote + " " + AgentFirstTurnBriefing.Note).Length.Should().BeLessThanOrEqualTo(1300);
    }
}
