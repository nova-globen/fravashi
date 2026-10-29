using AwesomeAssertions;
using Fravashi.Domain.Mirroring;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Xunit;

namespace Fravashi.Domain.Tests.Mirroring;

/// <summary>
/// ADR 0018 decision 7, which is the sharpest sentence in that document: "remote writes are at-least-once:
/// if the provider accepts a creation and the studio dies before recording the response, a naive retry
/// creates a duplicate that an external key cannot prevent, since there is no key yet".
/// <para>
/// Every test here exists to keep one of these true: a crash can only leave a write in flight; an in-flight
/// write found after a restart is never retried blindly; and a write nobody can settle blocks the item's
/// queue rather than being stepped over.
/// </para>
/// </summary>
public class OutboxDisciplineTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 17, 9, 0, 0, TimeSpan.Zero);
    private static readonly WorkItemId Item = new("item-1");

    private static OutboxEntry Entry(
        string id,
        OutboxState state,
        OutboxKind kind = OutboxKind.Update,
        int minutesLater = 0,
        WorkItemId? item = null) =>
        new(
            id,
            ProjectId.Default,
            item ?? Item,
            kind,
            Payload: "{}",
            ExpectedVersion: 1,
            State: state,
            Attempts: 0,
            QueuedUtc: Start.AddMinutes(minutesLater),
            LastAttemptUtc: null,
            ResultKey: null,
            Failure: null);

    // ---- what a restart means --------------------------------------------------------------------

    [Fact]
    public void An_in_flight_write_found_after_a_restart_becomes_unknown_rather_than_pending()
    {
        // This is the whole mechanism. Moving it back to Pending would send it again, and a creation the
        // provider already accepted would be made twice with no key able to stop it.
        var recovered = OutboxDiscipline.OnRestart(Entry("op-1", OutboxState.InFlight, OutboxKind.Create));

        recovered.State.Should().Be(OutboxState.Unresolved);
        recovered.Failure.Should().Contain("whether the provider acted on it is unknown");
    }

    [Theory]
    [InlineData(OutboxState.Pending)]
    [InlineData(OutboxState.Confirmed)]
    [InlineData(OutboxState.Failed)]
    [InlineData(OutboxState.Abandoned)]
    [InlineData(OutboxState.Unresolved)]
    public void A_restart_does_not_disturb_a_write_whose_outcome_was_already_settled(OutboxState state)
    {
        // A settled fact survives a process stopping. Only the in-flight window is ambiguous.
        OutboxDiscipline.OnRestart(Entry("op-1", state)).Should().Be(Entry("op-1", state));
    }

    // ---- what may be sent next -------------------------------------------------------------------

    [Fact]
    public void The_oldest_pending_write_for_an_item_is_the_one_that_goes()
    {
        var next = OutboxDiscipline.NextToSend(
        [
            Entry("op-2", OutboxState.Pending, minutesLater: 5),
            Entry("op-1", OutboxState.Pending, minutesLater: 1),
        ]);

        next!.Id.Should().Be("op-1");
    }

    [Fact]
    public void Writes_already_settled_are_stepped_over_rather_than_blocking()
    {
        var next = OutboxDiscipline.NextToSend(
        [
            Entry("op-1", OutboxState.Confirmed, minutesLater: 1),
            Entry("op-2", OutboxState.Abandoned, minutesLater: 2),
            Entry("op-3", OutboxState.Pending, minutesLater: 3),
        ]);

        next!.Id.Should().Be("op-3");
    }

    [Fact]
    public void Nothing_is_sent_while_a_write_for_the_same_item_is_already_in_flight()
    {
        // Two calls about one item would race over the same expected version, and whichever lost would come
        // back as a conflict that nobody caused.
        OutboxDiscipline.NextToSend(
        [
            Entry("op-1", OutboxState.InFlight, minutesLater: 1),
            Entry("op-2", OutboxState.Pending, minutesLater: 2),
        ]).Should().BeNull();
    }

    [Fact]
    public void A_write_whose_outcome_is_unknown_blocks_every_later_write_for_that_item()
    {
        // The later write is a transition. Applying it while the creation before it might or might not have
        // landed is how a provider ends up in a state nobody asked for.
        OutboxDiscipline.NextToSend(
        [
            Entry("op-1", OutboxState.Unresolved, OutboxKind.Create, minutesLater: 1),
            Entry("op-2", OutboxState.Pending, OutboxKind.Transition, minutesLater: 2),
        ]).Should().BeNull();
    }

    [Fact]
    public void A_failed_write_blocks_the_item_too_rather_than_being_stepped_over()
    {
        OutboxDiscipline.NextToSend(
        [
            Entry("op-1", OutboxState.Failed, minutesLater: 1),
            Entry("op-2", OutboxState.Pending, minutesLater: 2),
        ]).Should().BeNull();
    }

    [Fact]
    public void An_item_with_nothing_queued_offers_nothing()
    {
        OutboxDiscipline.NextToSend([]).Should().BeNull();
        OutboxDiscipline.NextToSend([Entry("op-1", OutboxState.Confirmed)]).Should().BeNull();
    }

    [Fact]
    public void Two_writes_queued_in_the_same_instant_are_still_ordered()
    {
        // Timestamps collide. Falling back to the operation id keeps the order stable rather than arbitrary,
        // which matters because the queue is drained repeatedly and has to make the same choice each time.
        var next = OutboxDiscipline.NextToSend(
        [
            Entry("op-b", OutboxState.Pending),
            Entry("op-a", OutboxState.Pending),
        ]);

        next!.Id.Should().Be("op-a");
    }

    // ---- what can be settled without a person ----------------------------------------------------

    [Fact]
    public void An_unknown_creation_can_be_settled_when_the_provider_can_be_asked_about_the_operation_id()
    {
        OutboxDiscipline.CanBeSettledAutomatically(Entry("op-1", OutboxState.Unresolved, OutboxKind.Create), providerCanSearchByOperationId: true)
            .Should().BeTrue();
    }

    [Fact]
    public void An_unknown_creation_against_a_provider_that_cannot_be_searched_is_a_genuine_unknown()
    {
        // The tempting alternatives are to retry and hope, or to assume it failed. Both invent an answer to
        // a question that has not been asked, and one of them makes the duplicate.
        OutboxDiscipline.CanBeSettledAutomatically(Entry("op-1", OutboxState.Unresolved, OutboxKind.Create), providerCanSearchByOperationId: false)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(OutboxKind.Update)]
    [InlineData(OutboxKind.Transition)]
    public void An_unknown_update_settles_itself_because_only_a_creation_can_be_made_twice(OutboxKind kind)
    {
        // These carry the DESIRED VALUE rather than a delta, by construction: there is no append and no
        // increment in this queue. Applying one a second time lands in the same place whatever the provider
        // did the first time, so the unknown does not have to be resolved before retrying.
        OutboxDiscipline.CanBeSettledAutomatically(Entry("op-1", OutboxState.Unresolved, kind), providerCanSearchByOperationId: false)
            .Should().BeTrue();
    }

    [Fact]
    public void Only_an_unknown_write_is_a_candidate_for_being_settled_that_way()
    {
        OutboxDiscipline.CanBeSettledAutomatically(Entry("op-1", OutboxState.Pending), true).Should().BeFalse();
        OutboxDiscipline.CanBeSettledAutomatically(Entry("op-1", OutboxState.Failed), true).Should().BeFalse();
        OutboxDiscipline.CanBeSettledAutomatically(Entry("op-1", OutboxState.InFlight), true).Should().BeFalse();
    }

    [Fact]
    public void What_a_person_is_asked_names_the_operation_and_the_choice_they_have()
    {
        var sentence = OutboxDiscipline.UnresolvedSentence(Entry("op-1", OutboxState.Unresolved, OutboxKind.Create), "jira");

        sentence.Should().Contain("create this item on jira")
            .And.Contain("Check there")
            .And.Contain("send it again");
    }

    // ---- what counts as waiting ------------------------------------------------------------------

    [Theory]
    [InlineData(OutboxState.Pending, true, false)]
    [InlineData(OutboxState.InFlight, true, false)]
    [InlineData(OutboxState.Unresolved, false, true)]
    [InlineData(OutboxState.Confirmed, false, false)]
    [InlineData(OutboxState.Failed, false, true)]
    [InlineData(OutboxState.Abandoned, false, false)]
    public void A_write_is_either_on_its_way_or_waiting_for_a_person_and_never_both(OutboxState state, bool waiting, bool needsAPerson)
    {
        // The freshness indicator counts both, so an entry in both categories would be reported twice, and
        // an unresolved write would keep saying "waiting to reach jira" about something that is not going.
        var entry = Entry("op-1", state);

        entry.IsPendingWrite.Should().Be(waiting);
        entry.NeedsAPerson.Should().Be(needsAPerson);
        (entry.IsPendingWrite && entry.NeedsAPerson).Should().BeFalse();
    }
}
