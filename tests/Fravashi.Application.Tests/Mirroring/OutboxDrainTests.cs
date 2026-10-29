using AwesomeAssertions;
using Fravashi.Application.Mirroring;
using Fravashi.Domain.Mirroring;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Xunit;

namespace Fravashi.Application.Tests.Mirroring;

/// <summary>
/// Backlog 82's acceptance, in order: an outage degrades the product to read-and-queue rather than stopping
/// it; a crash between "the provider accepted the write" and "we recorded it" does not create a duplicate
/// on retry; and a provider-side deletion leaves what we know about the item intact.
/// </summary>
public class OutboxDrainTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);
    private static readonly WorkItemId Item = new("item-1");
    private static readonly WorkItemId Other = new("item-2");

    private readonly FakeOutboxStore _outbox = new();
    private readonly FakeMirrorStore _mirror = new();
    private readonly FakeProviderGateway _provider = new();
    private readonly FixedClock _clock = new(Start);

    private OutboxDrain Drain() => new(_outbox, _mirror, _clock);

    private async Task<OutboxEntry> QueueAsync(string id, OutboxKind kind = OutboxKind.Create, WorkItemId? item = null, int minutesLater = 0) =>
        await _outbox.QueueAsync(new OutboxEntry(
            id,
            ProjectId.Default,
            item ?? Item,
            kind,
            Payload: """{"Title":"A thing"}""",
            ExpectedVersion: 1,
            State: OutboxState.Pending,
            Attempts: 0,
            QueuedUtc: Start.AddMinutes(minutesLater),
            LastAttemptUtc: null,
            ResultKey: null,
            Failure: null));

    // ---- the ordinary path -----------------------------------------------------------------------

    [Fact]
    public async Task A_queued_write_reaches_the_provider_and_is_recorded_as_confirmed()
    {
        await QueueAsync("op-1");

        var report = await Drain().DrainAsync(_provider, ProjectId.Default);

        report.Confirmed.Should().Be(1);
        _provider.Calls.Should().ContainSingle().Which.Should().Be("create:op-1");
        _outbox.Entry("op-1").State.Should().Be(OutboxState.Confirmed);
        _outbox.Entry("op-1").ResultKey.Should().Be("PROJ-1");
        _mirror.States[Item].Key.Should().Be("PROJ-1");
        _mirror.States[Item].LastConfirmedUtc.Should().Be(Start);
    }

    [Fact]
    public async Task A_drained_queue_does_nothing_and_says_so()
    {
        var report = await Drain().DrainAsync(_provider, ProjectId.Default);

        report.Should().Be(DrainReport.Nothing);
        _provider.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Only_one_write_per_item_goes_out_at_a_time_but_other_items_still_move()
    {
        // Two calls about one item would race over the same expected version. Two calls about two items
        // have no ordering between them to protect.
        await QueueAsync("op-1", OutboxKind.Create, Item, minutesLater: 0);
        await QueueAsync("op-2", OutboxKind.Update, Item, minutesLater: 1);
        await QueueAsync("op-3", OutboxKind.Create, Other, minutesLater: 2);

        await Drain().DrainAsync(_provider, ProjectId.Default);

        _provider.Calls.Should().BeEquivalentTo(["create:op-1", "create:op-3"]);
        _outbox.Entry("op-2").State.Should().Be(OutboxState.Pending);
    }

    [Fact]
    public async Task An_update_behind_a_creation_finds_the_key_the_creation_came_back_with()
    {
        // The mirror row is written by the creation in the same pass, so the second drain has a key to use.
        await QueueAsync("op-1", OutboxKind.Create, Item, minutesLater: 0);
        await QueueAsync("op-2", OutboxKind.Update, Item, minutesLater: 1);

        await Drain().DrainAsync(_provider, ProjectId.Default);
        await Drain().DrainAsync(_provider, ProjectId.Default);

        _provider.Calls.Should().BeEquivalentTo(["create:op-1", "update:PROJ-1"], o => o.WithStrictOrdering());
    }

    // ---- acceptance 1: an outage degrades rather than stops --------------------------------------

    [Fact]
    public async Task A_provider_that_cannot_be_reached_leaves_the_change_queued_rather_than_lost()
    {
        await QueueAsync("op-1");
        _provider.NextWriteResult = ProviderResult<ProviderItem>.Unreachable("No route to jira.");

        var report = await Drain().DrainAsync(_provider, ProjectId.Default);

        report.Requeued.Should().Be(1);
        report.ProviderIsUnreachable.Should().BeTrue();
        report.NeedsAttention.Should().BeFalse("an outage is not something a person has to do anything about");
        _outbox.Entry("op-1").State.Should().Be(OutboxState.Pending);
    }

    [Fact]
    public async Task The_same_change_goes_through_once_the_provider_comes_back()
    {
        await QueueAsync("op-1");
        _provider.NextWriteResult = ProviderResult<ProviderItem>.Unreachable("No route to jira.");
        await Drain().DrainAsync(_provider, ProjectId.Default);

        var report = await Drain().DrainAsync(_provider, ProjectId.Default);

        report.Confirmed.Should().Be(1);
        _outbox.Entry("op-1").State.Should().Be(OutboxState.Confirmed);
        _provider.Calls.Count(c => c.StartsWith("create:", StringComparison.Ordinal)).Should().Be(2,
            "the first attempt provably never left, so sending it again is the correct thing to do");
    }

    [Fact]
    public async Task An_attempt_that_never_left_is_still_counted_as_an_attempt()
    {
        // A person looking at a stuck entry needs to know how many times this has been tried.
        await QueueAsync("op-1");
        _provider.NextWriteResult = ProviderResult<ProviderItem>.Unreachable("No route to jira.");

        await Drain().DrainAsync(_provider, ProjectId.Default);

        _outbox.Entry("op-1").Attempts.Should().Be(1);
        _outbox.Entry("op-1").LastAttemptUtc.Should().Be(Start);
    }

    // ---- acceptance 2: a crash does not make a duplicate -----------------------------------------

    [Fact]
    public async Task A_crash_after_the_provider_accepted_a_creation_does_not_create_it_twice()
    {
        // This is ADR 0018 decision 7 exactly. The provider accepts, Fravashi dies before recording the
        // response, and the naive retry makes a second item that no external key can prevent, because
        // there is no key yet.
        await QueueAsync("op-1");
        _outbox.DieAfterMarking = OutboxState.Confirmed;

        var died = async () => await Drain().DrainAsync(_provider, ProjectId.Default);
        await died.Should().ThrowAsync<ProcessDied>();

        // The only trace a crash can leave is an entry that says "we were about to do this".
        _outbox.Entry("op-1").State.Should().Be(OutboxState.InFlight);

        // Restart.
        var recovered = await Drain().RecoverAsync();
        recovered.Should().Be(1);
        _outbox.Entry("op-1").State.Should().Be(OutboxState.Unresolved);

        var report = await Drain().DrainAsync(_provider, ProjectId.Default);

        report.Settled.Should().Be(1);
        _outbox.Entry("op-1").State.Should().Be(OutboxState.Confirmed);
        _provider.Calls.Count(c => c.StartsWith("create:", StringComparison.Ordinal)).Should().Be(1,
            "the item the first call created was found by its operation id instead of being made again");
        _mirror.States[Item].Key.Should().Be("PROJ-1");
    }

    [Fact]
    public async Task A_call_that_left_and_never_answered_is_treated_exactly_like_a_crash()
    {
        // A timeout is the same state of knowledge as a crash, and an adapter that reported it as
        // "unreachable" because that is simpler would put the duplicate straight back.
        await QueueAsync("op-1");
        _provider.NextWriteResult = ProviderResult<ProviderItem>.Indeterminate("The connection dropped.");

        var first = await Drain().DrainAsync(_provider, ProjectId.Default);
        first.StillUnknown.Should().Be(1);
        _outbox.Entry("op-1").State.Should().Be(OutboxState.Unresolved);

        await Drain().DrainAsync(_provider, ProjectId.Default);

        _outbox.Entry("op-1").State.Should().Be(OutboxState.Confirmed);
        _provider.Calls.Count(c => c.StartsWith("create:", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task A_creation_that_never_landed_is_sent_again_rather_than_left_waiting()
    {
        // The other half of the lookup: a definite "the provider does not have it" means the write never
        // happened, and leaving it for a person would stall a queue for no reason.
        await QueueAsync("op-1");
        _provider.NextWriteResult = ProviderResult<ProviderItem>.Indeterminate("The connection dropped.");
        _provider.LookupResult = ProviderResult<ProviderItem?>.Ok(null);
        await Drain().DrainAsync(_provider, ProjectId.Default);

        _provider.LookupResult = null;
        await Drain().DrainAsync(_provider, ProjectId.Default);

        _outbox.Entry("op-1").State.Should().Be(OutboxState.Confirmed);
    }

    [Fact]
    public async Task A_lookup_that_does_not_answer_leaves_the_write_unknown_rather_than_guessing()
    {
        await QueueAsync("op-1");
        _provider.NextWriteResult = ProviderResult<ProviderItem>.Indeterminate("The connection dropped.");
        await Drain().DrainAsync(_provider, ProjectId.Default);

        _provider.LookupResult = ProviderResult<ProviderItem?>.Unreachable("Still no route to jira.");
        var report = await Drain().DrainAsync(_provider, ProjectId.Default);

        report.StillUnknown.Should().Be(1);
        _outbox.Entry("op-1").State.Should().Be(OutboxState.Unresolved);
    }

    [Fact]
    public async Task A_provider_that_cannot_be_searched_hands_an_unknown_creation_to_a_person()
    {
        _provider.Capabilities = new ProviderCapabilities(CanWrite: true, CanSearchByOperationId: false, CanTombstone: true);
        await QueueAsync("op-1");
        _provider.NextWriteResult = ProviderResult<ProviderItem>.Indeterminate("The connection dropped.");
        await Drain().DrainAsync(_provider, ProjectId.Default);

        var report = await Drain().DrainAsync(_provider, ProjectId.Default);

        report.StillUnknown.Should().Be(1);
        report.NeedsAttention.Should().BeTrue();
        _outbox.Entry("op-1").Failure.Should().Contain("cannot be asked whether it went through");
        _provider.Calls.Count(c => c.StartsWith("create:", StringComparison.Ordinal)).Should().Be(1,
            "guessing that it failed is how the duplicate gets made");
    }

    [Fact]
    public async Task An_unknown_update_settles_itself_without_a_lookup()
    {
        _provider.Capabilities = new ProviderCapabilities(CanWrite: true, CanSearchByOperationId: false, CanTombstone: true);
        await _mirror.ConfirmAsync(ProjectId.Default, Item, new ProviderConfirmation("jira", "PROJ-9", "etag-0", 1), Start);
        await QueueAsync("op-1", OutboxKind.Update);
        _provider.NextWriteResult = ProviderResult<ProviderItem>.Indeterminate("The connection dropped.");
        await Drain().DrainAsync(_provider, ProjectId.Default);

        var report = await Drain().DrainAsync(_provider, ProjectId.Default);

        report.Settled.Should().Be(1);
        _outbox.Entry("op-1").State.Should().Be(OutboxState.Confirmed);
        _provider.Calls.Should().NotContain(c => c.StartsWith("find:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_write_whose_outcome_is_unknown_holds_up_the_rest_of_that_item_and_says_so()
    {
        _provider.Capabilities = new ProviderCapabilities(true, CanSearchByOperationId: false, CanTombstone: true);
        await QueueAsync("op-1", OutboxKind.Create, Item, minutesLater: 0);
        await QueueAsync("op-2", OutboxKind.Transition, Item, minutesLater: 1);
        _provider.NextWriteResult = ProviderResult<ProviderItem>.Indeterminate("The connection dropped.");
        await Drain().DrainAsync(_provider, ProjectId.Default);

        var report = await Drain().DrainAsync(_provider, ProjectId.Default);

        report.Blocked.Should().Be(1);
        _outbox.Entry("op-2").State.Should().Be(OutboxState.Pending);
    }

    // ---- acceptance 3: a deletion leaves what we know intact -------------------------------------

    [Fact]
    public async Task A_provider_that_no_longer_has_the_item_tombstones_it_rather_than_erasing_it()
    {
        await _mirror.ConfirmAsync(ProjectId.Default, Item, new ProviderConfirmation("jira", "PROJ-9", "etag-0", 1), Start);
        await QueueAsync("op-1", OutboxKind.Update);
        _provider.NextWriteResult = ProviderResult<ProviderItem>.Gone("PROJ-9 was deleted on 16 September.");

        await Drain().DrainAsync(_provider, ProjectId.Default);

        var state = _mirror.States[Item];
        state.IsTombstoned.Should().BeTrue();
        state.TombstoneReason.Should().Contain("deleted on 16 September");
        state.Key.Should().Be("PROJ-9", "the key is provenance and survives the deletion");
        _outbox.Entry("op-1").State.Should().Be(OutboxState.Failed);
        _outbox.Entry("op-1").Failure.Should().Contain("nowhere to go");
    }

    [Fact]
    public async Task An_item_that_comes_back_on_the_provider_stops_being_tombstoned()
    {
        // Restoring from a provider's trash is a real thing people do, and a tombstone left over it would
        // hide a live item behind a deletion notice.
        await _mirror.TombstoneAsync(ProjectId.Default, Item, "It went away.", Start);

        await _mirror.ConfirmAsync(ProjectId.Default, Item, new ProviderConfirmation("jira", "PROJ-9", "etag-1", 2), Start.AddHours(1));

        _mirror.States[Item].IsTombstoned.Should().BeFalse();
    }

    // ---- refusals and read-only connections ------------------------------------------------------

    [Fact]
    public async Task A_refusal_that_will_not_change_is_recorded_rather_than_retried_forever()
    {
        await QueueAsync("op-1");
        _provider.NextWriteResult = ProviderResult<ProviderItem>.Refused("'Epic' is not a type on this project.");

        var report = await Drain().DrainAsync(_provider, ProjectId.Default);

        report.Failed.Should().Be(1);
        report.NeedsAttention.Should().BeTrue();
        _outbox.Entry("op-1").State.Should().Be(OutboxState.Failed);
        _outbox.Entry("op-1").Failure.Should().Contain("not a type on this project");
    }

    [Fact]
    public async Task A_connection_configured_for_reading_says_so_instead_of_queueing_forever()
    {
        // The queue would otherwise sit there growing, with nothing anywhere saying why.
        _provider.Capabilities = ProviderCapabilities.ReadOnly;
        await QueueAsync("op-1");

        var report = await Drain().DrainAsync(_provider, ProjectId.Default);

        report.Failed.Should().Be(1);
        _provider.Calls.Should().BeEmpty();
        _outbox.Entry("op-1").Failure.Should().Contain("configured for reading only");
    }

    [Fact]
    public async Task A_change_to_an_item_with_no_key_anywhere_is_refused_rather_than_sent_at_nothing()
    {
        await QueueAsync("op-1", OutboxKind.Update);

        var report = await Drain().DrainAsync(_provider, ProjectId.Default);

        report.Failed.Should().Be(1);
        _outbox.Entry("op-1").Failure.Should().Contain("no key on the provider yet");
        _provider.Calls.Should().BeEmpty();
    }

    // ---- the queue itself ------------------------------------------------------------------------

    [Fact]
    public async Task Queueing_the_same_operation_id_twice_queues_one_write()
    {
        await QueueAsync("op-1");
        await QueueAsync("op-1");

        _outbox.All.Should().ContainSingle();
    }

    [Fact]
    public async Task Recovery_finds_nothing_to_do_when_nothing_was_in_flight()
    {
        await QueueAsync("op-1");

        (await Drain().RecoverAsync()).Should().Be(0);
        _outbox.Entry("op-1").State.Should().Be(OutboxState.Pending);
    }
}
