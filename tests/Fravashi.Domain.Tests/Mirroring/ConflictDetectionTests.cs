using AwesomeAssertions;
using Fravashi.Domain.Mirroring;
using Fravashi.Domain.WorkItems;
using Xunit;

namespace Fravashi.Domain.Tests.Mirroring;

/// <summary>
/// ADR 0018 decision 5: "conflict is surfaced, never silently resolved. 'The provider wins' without
/// qualification is data loss wearing the costume of a policy, and it lets a provider-side deletion erase
/// provenance and evidence we cannot recreate. Provider-owned fields reconcile to the provider;
/// Fravashi-owned fields are never overwritten by a sync."
/// </summary>
public class ConflictDetectionTests
{
    [Fact]
    public void Neither_side_moving_is_not_a_conflict_and_not_a_write()
    {
        ConflictDetection.Compare(nameof(WorkItem.Title), "Same", "Same", "Same")
            .Should().Be(ConflictVerdict.Unchanged);
    }

    [Fact]
    public void Only_we_moved_so_the_provider_has_to_be_told()
    {
        ConflictDetection.Compare(nameof(WorkItem.Title), "Old", "New here", "Old")
            .Should().Be(ConflictVerdict.LocalOnly);
    }

    [Fact]
    public void Only_the_provider_moved_so_our_copy_follows()
    {
        ConflictDetection.Compare(nameof(WorkItem.Title), "Old", "Old", "New there")
            .Should().Be(ConflictVerdict.ProviderOnly);
    }

    [Fact]
    public void Both_moving_to_the_same_value_is_agreement_rather_than_a_fight()
    {
        // Asking a person to choose between two identical answers teaches them to click through the conflict
        // surface without reading it, and then the one that matters gets clicked through too.
        ConflictDetection.Compare(nameof(WorkItem.Title), "Old", "Agreed", "Agreed")
            .Should().Be(ConflictVerdict.Converged);
    }

    [Fact]
    public void Both_moving_to_different_values_is_the_conflict_a_person_settles()
    {
        ConflictDetection.Compare(nameof(WorkItem.Title), "Old", "Mine", "Theirs")
            .Should().Be(ConflictVerdict.Conflict);
    }

    [Fact]
    public void A_field_Fravashi_owns_is_not_something_a_sync_has_an_opinion_about()
    {
        // The change budget is the clearest case: no tracker has one, so a sync would see "we have a value,
        // they have nothing" and read it as a deletion. That is how an agent field set gets erased by a
        // provider that never carried it.
        ConflictDetection.Compare(nameof(WorkItem.ChangeBudget), "10", "12", null)
            .Should().Be(ConflictVerdict.NotTheProviders);

        ConflictDetection.Compare(nameof(WorkItem.Readiness), "a", "b", "c")
            .Should().Be(ConflictVerdict.NotTheProviders);
    }

    [Theory]
    [InlineData(nameof(WorkItem.Title))]
    [InlineData(nameof(WorkItem.Description))]
    [InlineData(nameof(WorkItem.Status))]
    [InlineData(nameof(WorkItem.Assignee))]
    [InlineData(nameof(WorkItem.Priority))]
    public void The_fields_a_tracker_genuinely_has_are_the_ones_that_can_conflict(string field)
    {
        ConflictDetection.Compare(field, "base", "mine", "theirs").Should().Be(ConflictVerdict.Conflict);
    }

    [Fact]
    public void An_empty_value_and_no_value_are_the_same_value()
    {
        // A tracker that answers with an empty description for one that was never set has not changed it.
        // Treating that as a move would manufacture a conflict on the first sync of half a board.
        ConflictDetection.Compare(nameof(WorkItem.Description), null, string.Empty, null)
            .Should().Be(ConflictVerdict.Unchanged);

        ConflictDetection.Compare(nameof(WorkItem.Description), string.Empty, null, string.Empty)
            .Should().Be(ConflictVerdict.Unchanged);
    }

    [Fact]
    public void A_first_sync_with_no_recorded_base_still_says_something_usable()
    {
        // With no base, both sides look moved. Different values are a real conflict and a person should see
        // it rather than have one side quietly win on the very first reconciliation.
        ConflictDetection.Compare(nameof(WorkItem.Title), null, "Mine", "Theirs")
            .Should().Be(ConflictVerdict.Conflict);
    }

    [Fact]
    public void A_field_nobody_classified_is_a_programming_fault_rather_than_a_silent_pass()
    {
        var act = () => ConflictDetection.Compare("NotAField", "a", "b", "c");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_recorded_conflict_states_itself_as_a_question_a_person_can_answer()
    {
        var conflict = new FieldConflict(
            "c1",
            new WorkItemId("item-1"),
            nameof(WorkItem.Title),
            "Was this",
            "Now this",
            "Or this",
            DateTimeOffset.UnixEpoch,
            Choice: null,
            ResolvedUtc: null);

        conflict.IsOpen.Should().BeTrue();
        conflict.Question("jira").Should()
            .Contain("'Now this' here")
            .And.Contain("'Or this' on jira")
            .And.Contain("'Was this' when they last agreed");
    }

    [Fact]
    public void A_conflict_over_a_value_that_was_previously_absent_says_so_rather_than_quoting_nothing()
    {
        var conflict = new FieldConflict(
            "c1",
            new WorkItemId("item-1"),
            nameof(WorkItem.Assignee),
            BaseValue: null,
            LocalValue: "me",
            ProviderValue: string.Empty,
            DateTimeOffset.UnixEpoch,
            Choice: null,
            ResolvedUtc: null);

        conflict.Question("jira").Should().Contain("'nothing' on jira").And.NotContain("when they last agreed");
    }
}
