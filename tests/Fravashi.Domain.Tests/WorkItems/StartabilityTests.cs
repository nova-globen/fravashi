using AwesomeAssertions;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;
using Xunit;

namespace Fravashi.Domain.Tests.WorkItems;

/// <summary>
/// "What can start now", worked out in the domain (canonical artifact model plan section 4.4). An item
/// qualifies when its status is Ready, neither flag is set, every item blocking it is Done, and no person is
/// still being waited on. Everything that does not qualify carries EVERY failing reason, because a board that
/// says only the first one sends the reader round the loop again.
/// </summary>
public class StartabilityTests
{
    private static WorkItem Item(
        WorkItemStatus status = WorkItemStatus.Ready,
        WorkItemFlags? flags = null,
        IReadOnlyList<PersonBlocker>? people = null,
        IReadOnlyList<ReadinessItem>? readiness = null,
        AmbiguityAssessment? ambiguity = null,
        IReadOnlyList<AcceptanceCriterion>? criteria = null) =>
        WorkItem.Restore(new WorkItemState
        {
            Id = WorkItemId.New(),
            Project = ProjectId.Default,
            Number = 1,
            Level = WorkItemLevel.Increment,
            Title = "An item",
            Description = "Why it exists.",
            Status = status,
            Resolution = status is WorkItemStatus.Done ? WorkItemResolution.Completed
                : status is WorkItemStatus.Cancelled ? WorkItemResolution.WontDo
                : null,
            Flags = flags ?? WorkItemFlags.None,
            PersonBlockers = people ?? [],
            Readiness = readiness ?? [],
            Ambiguity = ambiguity,
            AcceptanceCriteria = criteria ?? [new AcceptanceCriterion("c1", "It works.")],
            CreatedUtc = DateTimeOffset.UnixEpoch,
            UpdatedUtc = DateTimeOffset.UnixEpoch,
        }).Value;

    private static PersonBlocker Waiting(string person = "owner", DateTimeOffset? cleared = null) =>
        new(AgentLayerEntry.NewId(), person, "a decision", DateTimeOffset.UnixEpoch, cleared);

    [Fact]
    public void A_ready_item_with_no_blockers_can_start()
    {
        var verdict = Startability.Evaluate(Item(), []);

        verdict.CanStart.Should().BeTrue();
        verdict.Reasons.Should().BeEmpty();
        verdict.EffectivelyBlocked.Should().BeFalse();
    }

    [Theory]
    [InlineData(WorkItemStatus.Triage)]
    [InlineData(WorkItemStatus.Backlog)]
    [InlineData(WorkItemStatus.InProgress)]
    [InlineData(WorkItemStatus.InReview)]
    [InlineData(WorkItemStatus.Done)]
    [InlineData(WorkItemStatus.Cancelled)]
    public void An_item_that_is_not_ready_cannot_start(WorkItemStatus status)
    {
        var verdict = Startability.Evaluate(Item(status), []);

        verdict.CanStart.Should().BeFalse();
        verdict.Reasons.Should().Contain(StartBlocker.NotReady);
    }

    [Fact]
    public void A_blocker_that_is_not_done_holds_the_item_and_is_named()
    {
        var blocker = Item(WorkItemStatus.InProgress);

        var verdict = Startability.Evaluate(Item(), [blocker]);

        verdict.CanStart.Should().BeFalse();
        verdict.Reasons.Should().Contain(StartBlocker.OpenBlocker);
        verdict.OpenBlockers.Should().ContainSingle().Which.Should().Be(blocker.Id);
        verdict.EffectivelyBlocked.Should().BeTrue();
    }

    [Fact]
    public void A_cancelled_blocker_still_blocks()
    {
        // Decision D9: an abandoned dependency did not deliver what this item needs. Unlinking it is a
        // deliberate act, and until someone performs it the item is not startable.
        var verdict = Startability.Evaluate(Item(), [Item(WorkItemStatus.Cancelled)]);

        verdict.CanStart.Should().BeFalse();
        verdict.Reasons.Should().Contain(StartBlocker.OpenBlocker);
    }

    [Fact]
    public void A_done_blocker_does_not_block()
    {
        Startability.Evaluate(Item(), [Item(WorkItemStatus.Done)]).CanStart.Should().BeTrue();
    }

    [Fact]
    public void An_uncleared_person_blocker_holds_the_item_and_names_the_person()
    {
        var verdict = Startability.Evaluate(Item(people: [Waiting("owner")]), []);

        verdict.CanStart.Should().BeFalse();
        verdict.Reasons.Should().Contain(StartBlocker.PersonBlocker);
        verdict.WaitingOnPeople.Should().ContainSingle().Which.Should().Be("owner");
    }

    [Fact]
    public void A_cleared_person_blocker_does_not_hold_it()
    {
        var cleared = Waiting(cleared: DateTimeOffset.UnixEpoch.AddDays(1));

        Startability.Evaluate(Item(people: [cleared]), []).CanStart.Should().BeTrue();
    }

    [Fact]
    public void Every_failing_reason_is_reported_not_only_the_first()
    {
        var item = Item(
            status: WorkItemStatus.Backlog,
            flags: new WorkItemFlags(true, "waiting on the design", true, "next quarter"),
            people: [Waiting()]);

        var verdict = Startability.Evaluate(item, [Item(WorkItemStatus.InReview)]);

        verdict.Reasons.Should().BeEquivalentTo(
        [
            StartBlocker.NotReady,
            StartBlocker.BlockedFlag,
            StartBlocker.DeferredFlag,
            StartBlocker.OpenBlocker,
            StartBlocker.PersonBlocker,
        ]);
    }

    [Fact]
    public void Unchecked_readiness_and_a_high_ambiguity_score_are_advisories_not_exclusions()
    {
        // Decision D10: an advisory is worth knowing and never a reason to hold the item back, because the
        // alternative is a board that hides work until a checklist nobody owns is complete.
        var item = Item(
            readiness: [new ReadinessItem("r1", "The API shape is agreed.", false)],
            ambiguity: new AmbiguityAssessment(80, "owner", DateTimeOffset.UnixEpoch));

        var verdict = Startability.Evaluate(item, []);

        verdict.CanStart.Should().BeTrue();
        verdict.Advisories.Should().BeEquivalentTo([StartAdvisory.ReadinessUnchecked, StartAdvisory.Ambiguous]);
    }

    [Fact]
    public void An_item_with_no_acceptance_criteria_can_start_but_says_there_is_no_done_test()
    {
        var verdict = Startability.Evaluate(Item(criteria: []), []);

        verdict.CanStart.Should().BeTrue();
        verdict.Advisories.Should().Contain(StartAdvisory.NoAcceptanceCriteria);
    }

    [Fact]
    public void A_low_ambiguity_score_is_not_an_advisory()
    {
        var item = Item(ambiguity: new AmbiguityAssessment(
            AmbiguityAssessment.WorkableCeiling, "owner", DateTimeOffset.UnixEpoch));

        Startability.Evaluate(item, []).Advisories.Should().NotContain(StartAdvisory.Ambiguous);
    }

    [Fact]
    public void A_mirrored_cycle_needs_no_special_case_because_each_member_has_an_open_blocker()
    {
        var first = Item();
        var second = Item();

        Startability.Evaluate(first, [second]).Reasons.Should().Contain(StartBlocker.OpenBlocker);
        Startability.Evaluate(second, [first]).Reasons.Should().Contain(StartBlocker.OpenBlocker);
    }

    [Fact]
    public void The_blocked_flag_alone_makes_an_item_effectively_blocked()
    {
        var item = Item(flags: new WorkItemFlags(true, "the vendor has not answered", false, null));

        var verdict = Startability.Evaluate(item, []);

        verdict.EffectivelyBlocked.Should().BeTrue();
        verdict.Reasons.Should().Contain(StartBlocker.BlockedFlag);
    }

    [Fact]
    public void A_deferred_item_is_held_but_is_not_effectively_blocked()
    {
        // Deferred is a choice about WHEN, not a statement that something is in the way (plan section 4.4).
        var verdict = Startability.Evaluate(Item(flags: new WorkItemFlags(false, null, true, "next quarter")), []);

        verdict.CanStart.Should().BeFalse();
        verdict.Reasons.Should().Contain(StartBlocker.DeferredFlag);
        verdict.EffectivelyBlocked.Should().BeFalse();
    }

    [Fact]
    public void Every_reason_and_advisory_has_a_sentence()
    {
        foreach (var reason in Enum.GetValues<StartBlocker>())
        {
            Startability.Describe(reason).Should().NotBeNullOrWhiteSpace();
        }

        foreach (var advisory in Enum.GetValues<StartAdvisory>())
        {
            Startability.Describe(advisory).Should().NotBeNullOrWhiteSpace();
        }
    }
}
