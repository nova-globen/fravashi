using AwesomeAssertions;
using Fravashi.Application.Mirroring;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Mirroring;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;
using Fravashi.Infrastructure.Mirroring;
using Fravashi.Infrastructure.Tests.WorkItems;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Mirroring;

/// <summary>
/// The pull side of backlog 82 against the REAL local board and the real stores, because the thing being
/// proved is a rule about two stores and a fake board could not disagree with anything.
/// <para>
/// ADR 0018 decision 5 in one sentence: "Provider-owned fields reconcile to the provider; Fravashi-owned
/// fields are never overwritten by a sync; and a deletion on the provider side tombstones the item locally
/// rather than destroying its history."
/// </para>
/// </summary>
public class ReconcilePassTests : IDisposable
{
    private readonly LocalBoardFixture _board = new();
    private readonly SqliteMirrorStore _mirror;
    private readonly SqliteOutboxStore _outbox;
    private readonly StubProvider _provider = new();
    private int _ids;

    public ReconcilePassTests()
    {
        _mirror = new SqliteMirrorStore(_board.Factory);
        _outbox = new SqliteOutboxStore(_board.Factory);
    }

    public void Dispose()
    {
        _board.Dispose();
        GC.SuppressFinalize(this);
    }

    private ReconcilePass Pass() => new(
        _mirror,
        _outbox,
        _board.Clock,
        () => "id-" + (++_ids).ToString(System.Globalization.CultureInfo.InvariantCulture));

    private async Task<WorkItem> MirroredAsync(string title = "A task", string? assignee = null)
    {
        var created = await _board.CreateAsync(LocalBoardFixture.NewTask(title));

        // The stub provider holds "To Do", which this board reads as Backlog (backlog 47). A new item starts
        // in Triage, so seeding it there would make every test in this file start from a status the two sides
        // disagree about, which is a different test's subject. The agreement is stated rather than assumed.
        var agreed = await _board.Board.TransitionAsync(
            created.Id, WorkItemStatus.Backlog, null, null, null, created.Version, MutationContext.User);
        agreed.Outcome.Should().Be(TrackerOutcome.Ok, string.Join("; ", agreed.Errors.Select(e => e.Message)));
        var item = agreed.Value;

        if (assignee is not null)
        {
            var patched = await _board.Board.UpdateAsync(item.Id, new WorkItemPatch { Assignee = assignee }, item.Version, MutationContext.User);
            item = patched.Value;
        }

        _provider.Put(item.Id.Value, "PROJ-1", ProviderFields.Read(item));
        var seen = new Dictionary<string, string?>(ProviderFields.Read(item), StringComparer.Ordinal)
        {
            [nameof(WorkItem.Status)] = StableNames.ToName(item.Status),
        };
        await _mirror.ConfirmAsync(
            ProjectId.Default,
            item.Id,
            new ProviderConfirmation("jira", "PROJ-1", "etag-0", item.Version, seen),
            _board.Clock.GetUtcNow());

        return item;
    }

    // ---- nothing moved ---------------------------------------------------------------------------

    [Fact]
    public async Task A_pass_over_a_board_with_no_provider_behind_it_does_nothing()
    {
        await _board.TaskAsync();

        var report = await Pass().RunAsync(_provider, _board.Board);

        report.Should().Be(ReconcileReport.Nothing);
        report.Summary().Should().Contain("nothing mirrored");
    }

    [Fact]
    public async Task A_pass_where_neither_side_moved_writes_nothing()
    {
        await MirroredAsync();

        var report = await Pass().RunAsync(_provider, _board.Board);

        report.Checked.Should().Be(1);
        report.Updated.Should().Be(0);
        report.Queued.Should().Be(0);
        (await _mirror.OpenConflictsAsync(ProjectId.Default)).Should().BeEmpty();
        (await _outbox.OpenAsync(ProjectId.Default)).Should().BeEmpty();
    }

    // ---- one side moved --------------------------------------------------------------------------

    [Fact]
    public async Task A_field_only_the_provider_moved_is_taken()
    {
        var item = await MirroredAsync();
        _provider.Set("PROJ-1", nameof(WorkItem.Title), "Renamed on jira");

        var report = await Pass().RunAsync(_provider, _board.Board);

        report.Updated.Should().Be(1);
        (await _board.Board.GetAsync(item.Id))!.Title.Should().Be("Renamed on jira");
    }

    [Fact]
    public async Task A_field_only_we_moved_is_queued_to_be_sent()
    {
        var item = await MirroredAsync();
        await _board.Board.UpdateAsync(item.Id, new WorkItemPatch { Title = "Renamed here" }, item.Version, MutationContext.User);

        var report = await Pass().RunAsync(_provider, _board.Board);

        report.Queued.Should().Be(1);
        var queued = (await _outbox.OpenAsync(ProjectId.Default)).Should().ContainSingle().Which;
        queued.Kind.Should().Be(OutboxKind.Update);
        queued.Payload.Should().Contain("Renamed here");
    }

    [Fact]
    public async Task A_second_pass_after_one_side_moved_has_nothing_left_to_do()
    {
        // The base is re-recorded from what the provider now holds, so the field that was just reconciled
        // does not look moved all over again on the very next run.
        var item = await MirroredAsync();
        _provider.Set("PROJ-1", nameof(WorkItem.Title), "Renamed on jira");
        await Pass().RunAsync(_provider, _board.Board);

        var second = await Pass().RunAsync(_provider, _board.Board);

        second.Updated.Should().Be(0);
        second.Queued.Should().Be(0);
        second.Conflicts.Should().Be(0);
    }

    // ---- both sides moved ------------------------------------------------------------------------

    [Fact]
    public async Task A_field_both_sides_moved_is_recorded_as_a_question_and_neither_value_is_overwritten()
    {
        var item = await MirroredAsync();
        await _board.Board.UpdateAsync(item.Id, new WorkItemPatch { Title = "Mine" }, item.Version, MutationContext.User);
        _provider.Set("PROJ-1", nameof(WorkItem.Title), "Theirs");

        var report = await Pass().RunAsync(_provider, _board.Board);

        report.Conflicts.Should().Be(1);
        report.Updated.Should().Be(0);
        report.Queued.Should().Be(0);

        (await _board.Board.GetAsync(item.Id))!.Title.Should().Be("Mine", "nothing is overwritten while a person has not decided");
        var conflict = (await _mirror.OpenConflictsAsync(ProjectId.Default)).Should().ContainSingle().Which;
        conflict.Field.Should().Be(nameof(WorkItem.Title));
        conflict.LocalValue.Should().Be("Mine");
        conflict.ProviderValue.Should().Be("Theirs");
        conflict.BaseValue.Should().Be("A task", "the base is what lets a reader tell which side moved");
    }

    [Fact]
    public async Task Both_sides_landing_on_the_same_value_is_agreement_rather_than_a_question()
    {
        var item = await MirroredAsync();
        await _board.Board.UpdateAsync(item.Id, new WorkItemPatch { Title = "Agreed" }, item.Version, MutationContext.User);
        _provider.Set("PROJ-1", nameof(WorkItem.Title), "Agreed");

        var report = await Pass().RunAsync(_provider, _board.Board);

        report.Conflicts.Should().Be(0);
        (await _mirror.OpenConflictsAsync(ProjectId.Default)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_same_disagreement_seen_on_a_later_pass_is_still_one_question()
    {
        var item = await MirroredAsync();
        await _board.Board.UpdateAsync(item.Id, new WorkItemPatch { Title = "Mine" }, item.Version, MutationContext.User);
        _provider.Set("PROJ-1", nameof(WorkItem.Title), "Theirs");

        await Pass().RunAsync(_provider, _board.Board);
        await Pass().RunAsync(_provider, _board.Board);

        (await _mirror.OpenConflictsAsync(ProjectId.Default)).Should().ContainSingle(
            "a sync that runs every minute must not turn one disagreement into a hundred");
    }

    // ---- what a sync must not touch --------------------------------------------------------------

    [Fact]
    public async Task A_field_Fravashi_owns_is_not_touched_even_though_the_provider_has_nothing_there()
    {
        // The clearest case in ADR 0018 decision 5: no tracker carries a change budget, so a sync that
        // compared it would see "we have a value, they have nothing" and read it as a deletion.
        var item = await _board.CreateAsync(LocalBoardFixture.NewTask());
        var withBudget = await _board.Board.UpdateAsync(
            item.Id,
            new WorkItemPatch { NextAction = "Write the adapter." },
            item.Version,
            MutationContext.User);
        withBudget.Outcome.Should().Be(TrackerOutcome.Ok);

        _provider.Put(item.Id.Value, "PROJ-1", ProviderFields.Read(withBudget.Value));
        await _mirror.ConfirmAsync(
            ProjectId.Default,
            item.Id,
            new ProviderConfirmation("jira", "PROJ-1", "etag-0", withBudget.Value.Version, ProviderFields.Read(withBudget.Value)),
            _board.Clock.GetUtcNow());

        await Pass().RunAsync(_provider, _board.Board);

        (await _board.Board.GetAsync(item.Id))!.NextAction.Should().Be("Write the adapter.");
    }

    [Fact]
    public async Task A_field_the_provider_does_not_carry_at_all_is_left_alone()
    {
        // Absent means "I have no opinion about this", and treating that as "set it to nothing" is how a
        // sync empties a description on the first pass against a tracker with a narrower field set.
        var item = await MirroredAsync(assignee: "someone");
        _provider.Remove("PROJ-1", nameof(WorkItem.Assignee));

        await Pass().RunAsync(_provider, _board.Board);

        (await _board.Board.GetAsync(item.Id))!.Assignee.Should().Be("someone");
    }

    // ---- deletion and outage ---------------------------------------------------------------------

    [Fact]
    public async Task An_item_the_provider_no_longer_has_is_tombstoned_with_its_history_intact()
    {
        var item = await MirroredAsync();
        await _board.Board.RecordEvidenceAsync(
            item.Id,
            new NewEvidence
            {
                Kind = EvidenceKind.TestRun,
                Result = EvidenceResult.Pass,
                Summary = "The check passed.",
            },
            MutationContext.User);
        _provider.Delete("PROJ-1");

        var report = await Pass().RunAsync(_provider, _board.Board);

        report.Tombstoned.Should().Be(1);
        (await _mirror.GetAsync(ProjectId.Default, item.Id))!.IsTombstoned.Should().BeTrue();
        (await _board.Board.GetAsync(item.Id)).Should().NotBeNull("the item is kept, not destroyed");
        (await _board.Board.EvidenceAsync(item.Id)).Should().ContainSingle("evidence is what a deletion must never take");
    }

    [Fact]
    public async Task An_item_already_tombstoned_is_not_asked_about_again()
    {
        var item = await MirroredAsync();
        _provider.Delete("PROJ-1");
        await Pass().RunAsync(_provider, _board.Board);

        var second = await Pass().RunAsync(_provider, _board.Board);

        second.Checked.Should().Be(0, "asking again would give the same answer on every pass forever");
        second.Tombstoned.Should().Be(0);
    }

    [Fact]
    public async Task A_provider_that_stops_answering_stops_the_pass_rather_than_hammering_it()
    {
        await MirroredAsync();
        _provider.Unreachable = true;

        var report = await Pass().RunAsync(_provider, _board.Board);

        report.Unreachable.Should().BeTrue();
        report.Summary().Should().Contain("nothing was lost");
        _provider.Gets.Should().Be(1, "one refusal is enough to know the rest will refuse too");
    }

    /// <summary>A provider whose contents the test sets directly. Only the reads the pass uses are real.</summary>
    private sealed class StubProvider : IProviderGateway
    {
        private readonly Dictionary<string, Dictionary<string, string?>> _items = [];

        public string Connection => "jira";

        public ProviderCapabilities Capabilities { get; } = new(true, true, true);

        public bool Unreachable { get; set; }

        public int Gets { get; private set; }

        public void Put(string _, string key, IReadOnlyDictionary<string, string?> fields) =>
            _items[key] = new Dictionary<string, string?>(fields, StringComparer.Ordinal);

        public void Set(string key, string field, string? value) => _items[key][field] = value;

        public void Remove(string key, string field) => _items[key].Remove(field);

        public void Delete(string key) => _items.Remove(key);

        public Task<ProviderResult<ProviderItem>> GetAsync(string key, CancellationToken ct = default)
        {
            Gets++;
            if (Unreachable)
            {
                return Task.FromResult(ProviderResult<ProviderItem>.Unreachable("No route to jira."));
            }

            return Task.FromResult(_items.TryGetValue(key, out var fields)
                ? ProviderResult<ProviderItem>.Ok(new ProviderItem(key, "etag-1", fields, "To Do"))
                : ProviderResult<ProviderItem>.Gone($"{key} was deleted there."));
        }

        public Task<ProviderResult<ProviderItem>> CreateAsync(string payload, string operationId, CancellationToken ct = default) =>
            throw new NotSupportedException("The pull pass never writes.");

        public Task<ProviderResult<ProviderItem>> UpdateAsync(string key, string payload, string? providerVersion, string operationId, CancellationToken ct = default) =>
            throw new NotSupportedException("The pull pass never writes.");

        public Task<ProviderResult<ProviderItem>> TransitionAsync(string key, string payload, string? providerVersion, string operationId, CancellationToken ct = default) =>
            throw new NotSupportedException("The pull pass never writes.");

        public Task<ProviderResult<ProviderItem?>> FindByOperationIdAsync(string operationId, CancellationToken ct = default) =>
            throw new NotSupportedException("The pull pass never looks up an operation.");
    }
}
