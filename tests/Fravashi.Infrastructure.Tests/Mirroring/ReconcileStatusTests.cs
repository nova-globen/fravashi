using AwesomeAssertions;
using Fravashi.Application.Mirroring;
using Fravashi.Application.Onboarding;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Mirroring;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Mirroring;
using Fravashi.Infrastructure.Tests.WorkItems;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Mirroring;

/// <summary>
/// Backlog 47: the provider's STATUS, which the reconcile pass carried no opinion about until now. It is a
/// transition rather than a field, so it cannot ride the field loop, but it is settled by the same three-way
/// reading: who moved it since the last sync decides what happens, and both moving is a question for a person.
/// </summary>
public sealed class ReconcileStatusTests : IDisposable
{
    private readonly LocalBoardFixture _board = new();
    private readonly SqliteMirrorStore _mirror;
    private readonly SqliteOutboxStore _outbox;
    private readonly StatusProvider _provider = new();
    private int _ids;

    public ReconcileStatusTests()
    {
        _mirror = new SqliteMirrorStore(_board.Factory);
        _outbox = new SqliteOutboxStore(_board.Factory);
    }

    public void Dispose() => _board.Dispose();

    private static readonly BoardStatusMapping Mapping = new("jira",
    [
        new StatusMappingEntry("Backlog", "To Do"),
        new StatusMappingEntry("In Progress", "Doing"),
        new StatusMappingEntry("Done", "Shipped"),
    ]);

    private ReconcilePass Pass(BoardStatusMapping? mapping = null) => new(
        _mirror,
        _outbox,
        _board.Clock,
        () => "id-" + (++_ids).ToString(System.Globalization.CultureInfo.InvariantCulture),
        () => ProviderStatusMap.From(mapping ?? Mapping));

    /// <summary>An item mirrored while both sides genuinely agree it is in the backlog.</summary>
    private async Task<WorkItem> MirroredAsync(NewWorkItem? seed = null)
    {
        var created = await _board.CreateAsync(seed ?? LocalBoardFixture.NewTask("A task"));

        // A new item starts in Triage, and the provider in these tests says "To Do", which this board's
        // mapping reads as Backlog. Seeding the two sides DISAGREEING would make every test start from a
        // conflict, so the item is moved to where the provider already is.
        var agreed = await _board.Board.TransitionAsync(
            created.Id, WorkItemStatus.Backlog, null, null, null, created.Version, MutationContext.User);
        agreed.Outcome.Should().Be(TrackerOutcome.Ok, string.Join("; ", agreed.Errors.Select(e => e.Message)));
        var item = agreed.Value;

        var fields = new Dictionary<string, string?>(ProviderFields.Read(item), StringComparer.Ordinal)
        {
            [nameof(WorkItem.Status)] = StableNames.ToName(item.Status),
        };
        _provider.Put("PROJ-1", ProviderFields.Read(item), "To Do", "new");
        await _mirror.ConfirmAsync(
            ProjectId.Default,
            item.Id,
            new ProviderConfirmation("jira", "PROJ-1", "etag-0", item.Version, fields),
            _board.Clock.GetUtcNow());
        return item;
    }

    private async Task<WorkItem> ReloadAsync(WorkItem item) => (await _board.Board.GetAsync(item.Id))!;

    [Fact]
    public async Task A_status_only_the_provider_moved_moves_the_item_here()
    {
        var item = await MirroredAsync();
        _provider.Move("PROJ-1", "Doing", "indeterminate");

        var report = await Pass().RunAsync(_provider, _board.Board);

        (await ReloadAsync(item)).Status.Should().Be(WorkItemStatus.InProgress);
        report.Updated.Should().Be(1);
        (await _mirror.OpenConflictsAsync(ProjectId.Default)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_provider_moving_an_item_with_unproven_criteria_to_done_is_not_refused_here()
    {
        // Requested 2026-10-22 (evidence on Done): the rule judges a person's or an agent's move. The
        // provider already holds Done, so refusing it here would only leave the two stores disagreeing.
        var item = await MirroredAsync(LocalBoardFixture.NewTask("A task") with
        {
            AcceptanceCriteria = [new Fravashi.Domain.WorkItems.AgentLayer.AcceptanceCriterion(string.Empty, "It works.")],
        });
        _provider.Move("PROJ-1", "Shipped", "done");

        await Pass().RunAsync(_provider, _board.Board);

        (await ReloadAsync(item)).Status.Should().Be(WorkItemStatus.Done);
        (await _mirror.OpenConflictsAsync(ProjectId.Default)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_status_only_we_moved_is_queued_to_the_provider_in_its_own_words()
    {
        var item = await MirroredAsync();
        var moved = await _board.Board.TransitionAsync(
            item.Id, WorkItemStatus.InProgress, null, null, null, item.Version, MutationContext.User);
        moved.Outcome.Should().Be(TrackerOutcome.Ok);

        var report = await Pass().RunAsync(_provider, _board.Board);

        report.Queued.Should().Be(1);
        var queued = (await _outbox.OpenAsync(ProjectId.Default)).Single();
        queued.Kind.Should().Be(OutboxKind.Transition);
        queued.Payload.Should().Contain("Doing", "the provider is asked in the name this board mapped for it");
    }

    [Fact]
    public async Task A_status_this_board_moved_into_a_column_the_provider_names_three_ways_is_a_question()
    {
        // Backlog 48. Nothing here can settle which of somebody's three finished states "Done" means, and this
        // pass has nobody to ask, so it records the question instead of sending its best guess.
        var item = await MirroredAsync();
        await _board.Board.TransitionAsync(
            item.Id, WorkItemStatus.InReview, null, null, null, item.Version, MutationContext.User);

        var report = await Pass(Several).RunAsync(_provider, _board.Board);

        report.Queued.Should().Be(0);
        (await _outbox.OpenAsync(ProjectId.Default)).Should().BeEmpty("a guess is exactly what is not sent");
        var conflict = (await _mirror.OpenConflictsAsync(ProjectId.Default)).Single();
        conflict.Field.Should().Be(nameof(WorkItem.Status));
        conflict.ProviderValue.Should().Be("backlog", "the value slots hold values; the names are a note");
        conflict.Note.Should().Contain("Resolved").And.Contain("Awaiting sign-off")
            .And.Contain("more than one state", "a question has to say why it is being asked");
        (await ReloadAsync(item)).Status.Should().Be(WorkItemStatus.InReview, "the local move stands either way");
    }

    private static readonly BoardStatusMapping Several = new("jira",
    [
        new StatusMappingEntry("Backlog", "To Do"),
        new StatusMappingEntry("In Review", "Resolved"),
        new StatusMappingEntry("In Review", "Awaiting sign-off"),
    ]);

    [Fact]
    public async Task Keeping_our_status_on_a_degraded_reading_queues_it_rather_than_asking_again()
    {
        // Settling rebases the agreed value onto one of the conflict's recorded values, so a RAW provider
        // name recorded where a canonical one belongs would match neither side on the next pass and the
        // question would come back for ever, instead of sending the status the person said they wanted.
        var item = await MirroredAsync();
        await _board.Board.TransitionAsync(
            item.Id, WorkItemStatus.InProgress, null, null, null, item.Version, MutationContext.User);
        _provider.Move("PROJ-1", "Shipped To Customer", "done");
        await Pass().RunAsync(_provider, _board.Board);
        var conflict = (await _mirror.OpenConflictsAsync(ProjectId.Default)).Single();
        conflict.ProviderValue.Should().Be("done", "it is what the name was understood to mean");
        conflict.Note.Should().Contain("Shipped To Customer", "and the raw name is not lost");

        await _mirror.ResolveConflictAsync(
            ProjectId.Default, conflict.Id, ConflictChoice.Local, _board.Clock.GetUtcNow());
        await Pass().RunAsync(_provider, _board.Board);

        (await ReloadAsync(item)).Status.Should().Be(WorkItemStatus.InProgress);
        (await _outbox.OpenAsync(ProjectId.Default)).Single().Payload.Should().Contain("Doing");
    }

    [Fact]
    public async Task Settling_a_disagreement_for_the_provider_is_carried_out_at_the_next_sync()
    {
        // The sync page says so in as many words: "The provider's value holds. It will be written here at
        // the next sync." Nothing read a settled conflict, so the row vanished and the same question came
        // back on the next pass, for ever.
        var item = await MirroredAsync();
        await _board.Board.TransitionAsync(
            item.Id, WorkItemStatus.InProgress, null, null, null, item.Version, MutationContext.User);
        _provider.Move("PROJ-1", "Shipped", "done");
        await Pass().RunAsync(_provider, _board.Board);
        var conflict = (await _mirror.OpenConflictsAsync(ProjectId.Default)).Single();

        await _mirror.ResolveConflictAsync(
            ProjectId.Default, conflict.Id, ConflictChoice.Provider, _board.Clock.GetUtcNow());
        await Pass().RunAsync(_provider, _board.Board);

        (await ReloadAsync(item)).Status.Should().Be(WorkItemStatus.Done, "the provider's value was chosen");
        (await _mirror.OpenConflictsAsync(ProjectId.Default)).Should().BeEmpty("and the question is answered");
    }

    [Fact]
    public async Task Settling_a_disagreement_for_this_board_queues_it_to_the_provider_at_the_next_sync()
    {
        var item = await MirroredAsync();
        await _board.Board.TransitionAsync(
            item.Id, WorkItemStatus.InProgress, null, null, null, item.Version, MutationContext.User);
        _provider.Move("PROJ-1", "Shipped", "done");
        await Pass().RunAsync(_provider, _board.Board);
        var conflict = (await _mirror.OpenConflictsAsync(ProjectId.Default)).Single();

        await _mirror.ResolveConflictAsync(
            ProjectId.Default, conflict.Id, ConflictChoice.Local, _board.Clock.GetUtcNow());
        await Pass().RunAsync(_provider, _board.Board);

        (await ReloadAsync(item)).Status.Should().Be(WorkItemStatus.InProgress, "this board's value was chosen");
        (await _outbox.OpenAsync(ProjectId.Default)).Single().Payload.Should().Contain("Doing");
        (await _mirror.OpenConflictsAsync(ProjectId.Default)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_pass_asks_the_workflow_the_same_question_the_board_does_and_settles_on_the_same_answer()
    {
        // Backlog 48. The board narrows an ambiguous move by what the site's workflow really offers, and
        // often that settles it. A sync must reach the same answer: a move that would have been sent
        // without asking must not become a question because it arrived this way instead of through a drag.
        var item = await MirroredAsync();
        await _board.Board.TransitionAsync(
            item.Id, WorkItemStatus.InReview, null, null, null, item.Version, MutationContext.User);
        _provider.Offers = ["Awaiting sign-off", "Doing"];

        var report = await Pass(Several).RunAsync(_provider, _board.Board);

        report.Queued.Should().Be(1);
        (await _outbox.OpenAsync(ProjectId.Default)).Single().Payload.Should().Contain("Awaiting sign-off");
        (await _mirror.OpenConflictsAsync(ProjectId.Default)).Should().BeEmpty("the workflow answered it");
    }

    [Fact]
    public async Task A_status_both_sides_moved_is_a_question_for_a_person_and_nothing_moves()
    {
        var item = await MirroredAsync();
        await _board.Board.TransitionAsync(
            item.Id, WorkItemStatus.InProgress, null, null, null, item.Version, MutationContext.User);
        _provider.Move("PROJ-1", "Shipped", "done");

        var report = await Pass().RunAsync(_provider, _board.Board);

        report.Conflicts.Should().Be(1);
        var conflict = (await _mirror.OpenConflictsAsync(ProjectId.Default)).Single();
        conflict.Field.Should().Be(nameof(WorkItem.Status));
        (await ReloadAsync(item)).Status.Should().Be(WorkItemStatus.InProgress, "neither side wins by itself");
        (await _outbox.OpenAsync(ProjectId.Default)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_status_the_provider_calls_done_is_read_as_done_and_reported()
    {
        var item = await MirroredAsync();
        _provider.Move("PROJ-1", "Shipped To Customer", "done");

        var report = await Pass().RunAsync(_provider, _board.Board);

        (await ReloadAsync(item)).Status.Should().Be(WorkItemStatus.Done);
        report.Updated.Should().Be(1);
        // Degrading is not silent: it is written where every other thing needing attention is written. The
        // VALUE recorded is what the name was understood to mean, because settling rebases onto it; the raw
        // name the provider used lives in the note, which is what that sentence is for.
        var noted = (await _mirror.OpenConflictsAsync(ProjectId.Default)).Should().ContainSingle().Subject;
        noted.ProviderValue.Should().Be("done");
        noted.Note.Should().Contain("Shipped To Customer").And.Contain("Board and status mapping");
    }

    [Fact]
    public async Task An_unknown_status_with_no_category_moves_nothing_and_is_reported()
    {
        var item = await MirroredAsync();
        _provider.Move("PROJ-1", "Bespoke", null);

        await Pass().RunAsync(_provider, _board.Board);

        (await ReloadAsync(item)).Status.Should().Be(WorkItemStatus.Backlog);
        (await _mirror.OpenConflictsAsync(ProjectId.Default))
            .Should().ContainSingle(c => c.ProviderValue!.Contains("Bespoke", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_state_the_stored_mapping_lists_under_two_statuses_is_a_question_and_moves_nothing()
    {
        // Backlog 181. A mapping stored before confirming refused this used to read "Closed" as whichever entry
        // came last, here Cancelled, and the pass would have cancelled somebody's finished work without a word.
        var item = await MirroredAsync();
        _provider.Move("PROJ-1", "Closed", "done");
        var twice = new BoardStatusMapping("jira",
        [
            new StatusMappingEntry("Backlog", "To Do"),
            new StatusMappingEntry("Done", "Closed"),
            new StatusMappingEntry("Cancelled", "Closed"),
        ]);

        var report = await Pass(twice).RunAsync(_provider, _board.Board);

        (await ReloadAsync(item)).Status.Should().Be(WorkItemStatus.Backlog, "nothing is picked");
        report.Updated.Should().Be(0);
        report.Conflicts.Should().Be(1);
        (await _outbox.OpenAsync(ProjectId.Default)).Should().BeEmpty();
        var asked = (await _mirror.OpenConflictsAsync(ProjectId.Default)).Should().ContainSingle().Subject;
        asked.Field.Should().Be(nameof(WorkItem.Status));
        asked.ProviderValue.Should().Be("Closed", "nothing understood it, so the raw name is the value");
        asked.Note.Should().Contain("'Closed'").And.Contain("Done").And.Contain("Cancelled").And.Contain("Board and status mapping");
    }

    [Fact]
    public async Task A_move_the_board_refuses_is_recorded_rather_than_swallowed()
    {
        // Cancelling needs a resolution the provider cannot give, so the board refuses; that is a mapping
        // problem somebody has to see, not a no-op.
        var item = await MirroredAsync();
        _provider.Move("PROJ-1", "Removed", "removed");

        await Pass().RunAsync(_provider, _board.Board);

        (await ReloadAsync(item)).Status.Should().Be(WorkItemStatus.Cancelled);
        (await ReloadAsync(item)).Resolution.Should().Be(WorkItemResolution.WontDo);
    }

    [Fact]
    public async Task A_provider_that_says_nothing_about_status_changes_nothing()
    {
        var item = await MirroredAsync();
        _provider.Move("PROJ-1", null, null);

        var report = await Pass().RunAsync(_provider, _board.Board);

        (await ReloadAsync(item)).Status.Should().Be(WorkItemStatus.Backlog);
        report.Conflicts.Should().Be(0);
        (await _mirror.OpenConflictsAsync(ProjectId.Default)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_item_mirrored_by_a_build_that_ignored_status_learns_the_base_when_the_two_sides_agree()
    {
        // Every row mirrored by an earlier build has no status in its base. Where the two sides already
        // agree, that is nothing to report: the base is learned and the item is untouched.
        var created = await _board.CreateAsync(LocalBoardFixture.NewTask("Mirrored by an older build"));
        var agreed = await _board.Board.TransitionAsync(
            created.Id, WorkItemStatus.Backlog, null, null, null, created.Version, MutationContext.User);
        var item = agreed.Value;
        _provider.Put("OLD-1", ProviderFields.Read(item), "To Do", "new");
        await _mirror.ConfirmAsync(
            ProjectId.Default,
            item.Id,
            new ProviderConfirmation("jira", "OLD-1", "etag-0", item.Version, ProviderFields.Read(item)),
            _board.Clock.GetUtcNow());

        var first = await Pass().RunAsync(_provider, _board.Board);

        first.Conflicts.Should().Be(0);
        first.Updated.Should().Be(0);
        first.Queued.Should().Be(0);
        (await _mirror.OpenConflictsAsync(ProjectId.Default)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_item_mirrored_by_that_build_whose_sides_disagree_is_a_question_rather_than_a_winner()
    {
        // No base means "who moved it" has no answer. Adopting the provider would move work somebody is
        // doing; keeping ours would quietly send it back the other way. So it is asked, not decided.
        var item = await _board.CreateAsync(LocalBoardFixture.NewTask("Mirrored by an older build"));
        _provider.Put("OLD-1", ProviderFields.Read(item), "Doing", "indeterminate");
        await _mirror.ConfirmAsync(
            ProjectId.Default,
            item.Id,
            new ProviderConfirmation("jira", "OLD-1", "etag-0", item.Version, ProviderFields.Read(item)),
            _board.Clock.GetUtcNow());

        var first = await Pass().RunAsync(_provider, _board.Board);

        first.Conflicts.Should().Be(1);
        (await ReloadAsync(item)).Status.Should().Be(WorkItemStatus.Triage, "nothing moved");
        var asked = (await _mirror.OpenConflictsAsync(ProjectId.Default)).Single();
        asked.Field.Should().Be(nameof(WorkItem.Status));
        asked.LocalValue.Should().Be("triage");
        asked.ProviderValue.Should().Be("in_progress");
    }

    [Fact]
    public async Task The_same_status_seen_twice_is_not_a_change_the_second_time()
    {
        var item = await MirroredAsync();
        _provider.Move("PROJ-1", "Doing", "indeterminate");
        await Pass().RunAsync(_provider, _board.Board);

        var second = await Pass().RunAsync(_provider, _board.Board);

        second.Updated.Should().Be(0, "the base moved with the first pass");
        second.Queued.Should().Be(0);
        (await ReloadAsync(item)).Status.Should().Be(WorkItemStatus.InProgress);
    }

    /// <summary>A provider whose one item's fields and status the test sets directly.</summary>
    private sealed class StatusProvider : IProviderGateway
    {
        private readonly Dictionary<string, (Dictionary<string, string?> Fields, string? Status, string? Category)> _items = [];

        public string Connection => "jira";

        public ProviderCapabilities Capabilities => new(true, true, true, CanOfferTransitions: Offers is not null);

        /// <summary>What this provider's workflow offers, or null when it cannot be asked at all.</summary>
        public List<string>? Offers { get; set; }

        public Task<ProviderResult<IReadOnlyList<string>>> OfferedTransitionsAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(ProviderResult<IReadOnlyList<string>>.Ok(Offers ?? []));

        public void Put(string key, IReadOnlyDictionary<string, string?> fields, string? status, string? category) =>
            _items[key] = (new Dictionary<string, string?>(fields, StringComparer.Ordinal), status, category);

        public void Move(string key, string? status, string? category)
        {
            var (fields, _, _) = _items[key];
            _items[key] = (fields, status, category);
        }

        public Task<ProviderResult<ProviderItem>> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(_items.TryGetValue(key, out var it)
                ? ProviderResult<ProviderItem>.Ok(new ProviderItem(key, "etag-1", it.Fields, it.Status, it.Category))
                : ProviderResult<ProviderItem>.Gone(key + " is gone."));

        public Task<ProviderResult<ProviderItem>> CreateAsync(string payload, string operationId, CancellationToken ct = default) =>
            throw new NotSupportedException("The pull pass never creates.");

        public Task<ProviderResult<ProviderItem>> UpdateAsync(string key, string payload, string? providerVersion, string operationId, CancellationToken ct = default) =>
            throw new NotSupportedException("The pull pass never writes.");

        public Task<ProviderResult<ProviderItem>> TransitionAsync(string key, string payload, string? providerVersion, string operationId, CancellationToken ct = default) =>
            throw new NotSupportedException("The pull pass queues a transition; it does not send one.");

        public Task<ProviderResult<ProviderItem?>> FindByOperationIdAsync(string operationId, CancellationToken ct = default) =>
            throw new NotSupportedException("The pull pass never looks up an operation.");
    }
}
