using AwesomeAssertions;
using Fravashi.Domain.AgentSessions;
using Xunit;

namespace Fravashi.Domain.Tests.AgentSessions;

/// <summary>
/// Work item #333: the commit reviewer's three failure classes live here as the one source both
/// <c>AgentCommitReviewer.Instructions</c> and <see cref="AgentFirstTurnBriefing"/> read, so the two can
/// never drift apart.
/// </summary>
public sealed class CommitReviewFailureClassesTests
{
    [Fact]
    public void All_three_classes_are_in_the_reviewers_own_order() =>
        CommitReviewFailureClasses.All.Should().Equal(
            CommitReviewFailureClasses.AppliedOrRecorded,
            CommitReviewFailureClasses.SafetyPropertyClaimed,
            CommitReviewFailureClasses.ClaimWiderThanEvidence);

    [Fact]
    public void Each_short_name_is_the_classs_title_clause_up_to_its_first_full_stop()
    {
        CommitReviewFailureClasses.ShortNames.Should().Equal(
            "A test that asserts an operation was APPLIED or RECORDED rather than what it DOES",
            "A SAFETY PROPERTY CLAIMED WITHOUT PROOF",
            "A CLAIM WIDER THAN ITS EVIDENCE");
    }
}
