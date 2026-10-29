using AwesomeAssertions;
using Fravashi.Application.Mirroring;
using Fravashi.Domain.Mirroring;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Mirroring;
using Fravashi.Infrastructure.Tests.Persistence;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Mirroring;

/// <summary>
/// The durable half of backlog 82 against a real file-backed database with the production schema. The
/// domain tests argue about the rules; these prove the rules survive being written down and read back,
/// which is the only form of them that matters after a restart.
/// </summary>
public class SqliteMirroringTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 9, 17, 11, 0, 0, TimeSpan.Zero);
    private static readonly WorkItemId Item = new("item-1");
    private static readonly WorkItemId Other = new("item-2");

    private readonly SqliteTestContext _context = new();
    private readonly SqliteOutboxStore _outbox;
    private readonly SqliteMirrorStore _mirror;

    public SqliteMirroringTests()
    {
        _outbox = new SqliteOutboxStore(_context.Factory);
        _mirror = new SqliteMirrorStore(_context.Factory);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private static OutboxEntry Entry(
        string id,
        OutboxKind kind = OutboxKind.Create,
        WorkItemId? item = null,
        OutboxState state = OutboxState.Pending,
        int minutesLater = 0) =>
        new(
            id,
            ProjectId.Default,
            item ?? Item,
            kind,
            """{"Title":"A thing"}""",
            ExpectedVersion: 4,
            State: state,
            Attempts: 0,
            QueuedUtc: Start.AddMinutes(minutesLater),
            LastAttemptUtc: null,
            ResultKey: null,
            Failure: null);

    // ---- the outbox ------------------------------------------------------------------------------

    [Fact]
    public async Task A_queued_write_comes_back_exactly_as_it_went_in()
    {
        await _outbox.QueueAsync(Entry("op-1"));

        var entries = await _outbox.ForItemAsync(ProjectId.Default, Item);

        entries.Should().ContainSingle().Which.Should().Be(Entry("op-1"));
    }

    [Fact]
    public async Task Queueing_one_operation_id_twice_records_one_write()
    {
        await _outbox.QueueAsync(Entry("op-1"));
        var second = await _outbox.QueueAsync(Entry("op-1", OutboxKind.Update));

        (await _outbox.ForItemAsync(ProjectId.Default, Item)).Should().ContainSingle();
        second.Kind.Should().Be(OutboxKind.Create, "the write already recorded is the one that holds");
    }

    [Fact]
    public async Task An_open_queue_leaves_out_what_is_already_settled()
    {
        await _outbox.QueueAsync(Entry("op-1", state: OutboxState.Confirmed));
        await _outbox.QueueAsync(Entry("op-2", state: OutboxState.Abandoned, minutesLater: 1));
        await _outbox.QueueAsync(Entry("op-3", minutesLater: 2));
        await _outbox.QueueAsync(Entry("op-4", state: OutboxState.Failed, minutesLater: 3));

        var open = await _outbox.OpenAsync(ProjectId.Default);

        open.Select(e => e.Id).Should().BeEquivalentTo(["op-3", "op-4"]);
    }

    [Fact]
    public async Task Marking_a_write_records_the_attempt_only_when_the_call_actually_left()
    {
        await _outbox.QueueAsync(Entry("op-1"));

        await _outbox.MarkAsync(ProjectId.Default, "op-1", OutboxState.InFlight, null, null, countAttempt: true, Start);
        await _outbox.MarkAsync(ProjectId.Default, "op-1", OutboxState.Confirmed, "PROJ-7", null, countAttempt: false, Start.AddMinutes(1));

        var entry = (await _outbox.ForItemAsync(ProjectId.Default, Item)).Single();
        entry.State.Should().Be(OutboxState.Confirmed);
        entry.Attempts.Should().Be(1, "the number a person reads is times this reached the provider");
        entry.LastAttemptUtc.Should().Be(Start);
        entry.ResultKey.Should().Be("PROJ-7");
    }

    [Fact]
    public async Task A_result_key_is_never_cleared_by_a_later_mark()
    {
        // The key is the provider's own identity for the item. Losing it would strand every later write.
        await _outbox.QueueAsync(Entry("op-1"));
        await _outbox.MarkAsync(ProjectId.Default, "op-1", OutboxState.Confirmed, "PROJ-7", null, false, Start);

        await _outbox.MarkAsync(ProjectId.Default, "op-1", OutboxState.Failed, null, "Something later went wrong.", false, Start);

        (await _outbox.ForItemAsync(ProjectId.Default, Item)).Single().ResultKey.Should().Be("PROJ-7");
    }

    [Fact]
    public async Task Marking_a_write_nobody_queued_changes_nothing_and_says_so()
    {
        (await _outbox.MarkAsync(ProjectId.Default, "op-missing", OutboxState.Failed, null, null, false, Start))
            .Should().BeFalse();
    }

    [Fact]
    public async Task A_restart_turns_every_write_left_in_flight_into_an_unknown()
    {
        // The durable half of ADR 0018 decision 7. At the moment this runs, nothing can legitimately be in
        // flight: the only process that could have been making the call is the one that just started.
        await _outbox.QueueAsync(Entry("op-1", state: OutboxState.InFlight));
        await _outbox.QueueAsync(Entry("op-2", item: Other, state: OutboxState.InFlight, minutesLater: 1));
        await _outbox.QueueAsync(Entry("op-3", minutesLater: 2));

        (await _outbox.RecoverInFlightAsync()).Should().Be(2);

        var all = await _outbox.OpenAsync(ProjectId.Default);
        all.Where(e => e.Id != "op-3").Should().AllSatisfy(e => e.State.Should().Be(OutboxState.Unresolved));
        all.Single(e => e.Id == "op-3").State.Should().Be(OutboxState.Pending);
        all.First(e => e.Id == "op-1").Failure.Should().Contain("whether the provider acted on it is unknown");
    }

    [Fact]
    public async Task A_second_restart_with_nothing_in_flight_does_nothing()
    {
        await _outbox.QueueAsync(Entry("op-1", state: OutboxState.InFlight));
        await _outbox.RecoverInFlightAsync();

        (await _outbox.RecoverInFlightAsync()).Should().Be(0, "an unknown does not become a second unknown");
    }

    // ---- the mirroring sidecar -------------------------------------------------------------------

    [Fact]
    public async Task An_item_with_no_provider_behind_it_has_no_mirror_row()
    {
        (await _mirror.GetAsync(ProjectId.Default, Item)).Should().BeNull();
        MirrorFreshness.Describe(null, Start).Level.Should().Be(MirrorLevel.LocalOnly);
    }

    [Fact]
    public async Task A_confirmation_is_recorded_and_read_back_whole()
    {
        await _mirror.ConfirmAsync(ProjectId.Default, Item, new ProviderConfirmation("jira", "PROJ-7", "etag-3", 9), Start);

        var state = (await _mirror.GetAsync(ProjectId.Default, Item))!;
        state.Connection.Should().Be("jira");
        state.Key.Should().Be("PROJ-7");
        state.ProviderVersion.Should().Be("etag-3");
        state.LocalVersionAtSync.Should().Be(9);
        state.LastConfirmedUtc.Should().Be(Start);
        state.IsTombstoned.Should().BeFalse();
    }

    [Fact]
    public async Task The_pending_and_stuck_counts_are_counted_rather_than_stored()
    {
        // A denormalised counter drifts the first time a write path forgets to adjust it, and a freshness
        // indicator that says "nothing is waiting" while something is waiting is worse than none at all.
        await _mirror.ConfirmAsync(ProjectId.Default, Item, new ProviderConfirmation("jira", "PROJ-7", null, 1), Start);
        await _outbox.QueueAsync(Entry("op-1"));
        await _outbox.QueueAsync(Entry("op-2", state: OutboxState.InFlight, minutesLater: 1));
        await _outbox.QueueAsync(Entry("op-3", state: OutboxState.Unresolved, minutesLater: 2));
        await _outbox.QueueAsync(Entry("op-4", state: OutboxState.Confirmed, minutesLater: 3));
        await _outbox.QueueAsync(Entry("op-5", item: Other, minutesLater: 4));

        var state = (await _mirror.GetAsync(ProjectId.Default, Item))!;

        state.PendingWrites.Should().Be(2);
        state.StuckWrites.Should().Be(1);
        MirrorFreshness.Describe(state, Start).Level.Should().Be(MirrorLevel.WriteStuck);
    }

    [Fact]
    public async Task A_tombstone_keeps_the_provenance_it_already_had()
    {
        await _mirror.ConfirmAsync(ProjectId.Default, Item, new ProviderConfirmation("jira", "PROJ-7", "etag-3", 9), Start);

        await _mirror.TombstoneAsync(ProjectId.Default, Item, "It was deleted there.", Start.AddHours(1));

        var state = (await _mirror.GetAsync(ProjectId.Default, Item))!;
        state.IsTombstoned.Should().BeTrue();
        state.TombstoneReason.Should().Be("It was deleted there.");
        state.Key.Should().Be("PROJ-7", "the key is provenance and is not what the deletion took away");
        state.LastConfirmedUtc.Should().Be(Start);
    }

    [Fact]
    public async Task An_item_that_was_never_mirrored_can_still_be_tombstoned()
    {
        await _mirror.TombstoneAsync(ProjectId.Default, Item, "It was never there.", Start);

        (await _mirror.GetAsync(ProjectId.Default, Item))!.IsTombstoned.Should().BeTrue();
    }

    [Fact]
    public async Task An_item_that_comes_back_on_the_provider_stops_being_tombstoned()
    {
        // People restore things from a provider's trash. A tombstone left over that would hide a live item
        // behind a deletion notice that is no longer true.
        await _mirror.TombstoneAsync(ProjectId.Default, Item, "It went away.", Start);

        await _mirror.ConfirmAsync(ProjectId.Default, Item, new ProviderConfirmation("jira", "PROJ-7", "etag-4", 10), Start.AddHours(2));

        var state = (await _mirror.GetAsync(ProjectId.Default, Item))!;
        state.IsTombstoned.Should().BeFalse();
        state.TombstoneReason.Should().BeNull();
    }

    [Fact]
    public async Task A_project_reads_back_every_mirrored_item_at_once()
    {
        await _mirror.ConfirmAsync(ProjectId.Default, Item, new ProviderConfirmation("jira", "PROJ-7", null, 1), Start);
        await _mirror.ConfirmAsync(ProjectId.Default, Other, new ProviderConfirmation("jira", "PROJ-8", null, 1), Start);

        (await _mirror.ForProjectAsync(ProjectId.Default)).Should().HaveCount(2);
    }

    // ---- the conflict surface --------------------------------------------------------------------

    private static FieldConflict Conflict(string id, string field = nameof(WorkItem.Title), string? local = "Mine", string? provider = "Theirs") =>
        new(id, Item, field, "Base", local, provider, Start, null, null);

    [Fact]
    public async Task A_conflict_is_recorded_with_all_three_values()
    {
        await _mirror.RecordConflictAsync(ProjectId.Default, Conflict("c-1"));

        var open = await _mirror.OpenConflictsAsync(ProjectId.Default);
        open.Should().ContainSingle().Which.Should().Be(Conflict("c-1"));
    }

    [Fact]
    public async Task The_same_disagreement_seen_again_updates_rather_than_stacking()
    {
        // A sync that runs every minute would otherwise turn one disagreement into a hundred, and a
        // conflict surface with a hundred copies of one question is one nobody opens twice.
        await _mirror.RecordConflictAsync(ProjectId.Default, Conflict("c-1"));
        var again = await _mirror.RecordConflictAsync(ProjectId.Default, Conflict("c-2", provider: "Changed again"));

        var open = await _mirror.OpenConflictsAsync(ProjectId.Default);
        open.Should().ContainSingle();
        open[0].ProviderValue.Should().Be("Changed again");
        again.Id.Should().Be("c-1", "the caller is told which conflict this actually is");
    }

    [Fact]
    public async Task A_disagreement_about_a_different_field_is_a_different_conflict()
    {
        await _mirror.RecordConflictAsync(ProjectId.Default, Conflict("c-1"));
        await _mirror.RecordConflictAsync(ProjectId.Default, Conflict("c-2", nameof(WorkItem.Description)));

        (await _mirror.OpenConflictsAsync(ProjectId.Default)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Settling_a_conflict_takes_it_off_the_surface_and_records_the_choice()
    {
        await _mirror.RecordConflictAsync(ProjectId.Default, Conflict("c-1"));

        (await _mirror.ResolveConflictAsync(ProjectId.Default, "c-1", ConflictChoice.Local, Start.AddMinutes(5)))
            .Should().BeTrue();

        (await _mirror.OpenConflictsAsync(ProjectId.Default)).Should().BeEmpty();
        (await _mirror.GetAsync(ProjectId.Default, Item)).Should().BeNull("settling a conflict does not invent a mirror row");
    }

    [Fact]
    public async Task A_conflict_already_settled_is_not_settled_a_second_time()
    {
        // Re-settling would rewrite what somebody already decided, and that record is the only thing that
        // explains the value that holds now.
        await _mirror.RecordConflictAsync(ProjectId.Default, Conflict("c-1"));
        await _mirror.ResolveConflictAsync(ProjectId.Default, "c-1", ConflictChoice.Local, Start);

        (await _mirror.ResolveConflictAsync(ProjectId.Default, "c-1", ConflictChoice.Provider, Start.AddHours(1)))
            .Should().BeFalse();
    }

    [Fact]
    public async Task A_settled_conflict_no_longer_counts_against_the_item()
    {
        await _mirror.ConfirmAsync(ProjectId.Default, Item, new ProviderConfirmation("jira", "PROJ-7", null, 1), Start);
        await _mirror.RecordConflictAsync(ProjectId.Default, Conflict("c-1"));
        (await _mirror.GetAsync(ProjectId.Default, Item))!.ConflictCount.Should().Be(1);

        await _mirror.ResolveConflictAsync(ProjectId.Default, "c-1", ConflictChoice.Provider, Start);

        (await _mirror.GetAsync(ProjectId.Default, Item))!.ConflictCount.Should().Be(0);
    }

    [Fact]
    public async Task Settling_a_conflict_nobody_recorded_says_so()
    {
        (await _mirror.ResolveConflictAsync(ProjectId.Default, "c-missing", ConflictChoice.Local, Start))
            .Should().BeFalse();
    }
}
