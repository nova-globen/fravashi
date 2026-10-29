using System.Text.Json;
using AwesomeAssertions;
using Fravashi.Application.Mirroring;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Mirroring;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Mirroring;
using Fravashi.Infrastructure.Tests.WorkItems;
using Fravashi.Infrastructure.WorkItems;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Mirroring;

/// <summary>
/// Backlog 47, increment 2: the two acts that START the machinery item 82 built. Over the real board and the
/// real mirror and outbox stores, with a provider that remembers what it was sent, so what is asserted is a
/// round trip and not a call count.
/// </summary>
public sealed class ProviderSyncTests : IDisposable
{
    private readonly LocalBoardFixture _board = new();
    private readonly SqliteMirrorStore _mirror;
    private readonly SqliteOutboxStore _outbox;
    private readonly RememberingProvider _provider = new();
    private readonly Gateways _gateways;

    public ProviderSyncTests()
    {
        _mirror = new SqliteMirrorStore(_board.Factory);
        _outbox = new SqliteOutboxStore(_board.Factory);
        _gateways = new Gateways(_provider);
    }

    public void Dispose() => _board.Dispose();

    private ProviderSync Sync(Fravashi.Application.Onboarding.BoardStatusMapping? mapping = null) => new(
        _gateways,
        new LocalTrackerResolver(_board.Factory, _board.Clock),
        _outbox,
        _mirror,
        new OutboxDrain(_outbox, _mirror, _board.Clock),
        new ReconcilePass(_mirror, _outbox, _board.Clock, null, () => ProviderStatusMap.From(mapping ?? Mapping)),
        _board.Clock,
        statuses: () => ProviderStatusMap.From(mapping ?? Mapping));

    private static readonly Fravashi.Application.Onboarding.BoardStatusMapping Mapping = new("jira",
    [
        new Fravashi.Application.Onboarding.StatusMappingEntry("Backlog", "To Do"),
        new Fravashi.Application.Onboarding.StatusMappingEntry("In Progress", "Doing"),
    ]);

    [Fact]
    public async Task An_item_sent_and_synced_exists_on_the_provider_and_is_mirrored_here_under_its_key()
    {
        var item = await _board.CreateAsync(LocalBoardFixture.NewTask("Resume a conversation in place"));

        var queued = await Sync().SendAsync(ProjectId.Default, item.Id);
        queued.Happened.Should().BeTrue(queued.Sentence);
        _provider.Created.Should().BeEmpty("sending only queues; the outbox's own rules are what send");

        var synced = await Sync().SyncNowAsync(ProjectId.Default);

        synced.Happened.Should().BeTrue(synced.Sentence);
        _provider.Created.Should().ContainSingle();
        _provider.Created[0].Fields[nameof(WorkItem.Title)].Should().Be("Resume a conversation in place");
        (await _mirror.GetAsync(ProjectId.Default, item.Id))!.Key.Should().Be("JIRA-1");
    }

    [Fact]
    public async Task An_item_just_created_on_the_provider_takes_the_status_the_provider_gave_it()
    {
        // Measured live against a real Jira on 2026-09-22: an item sent from Triage became a new issue, which Jira
        // opened in "To Do", and the first sync asked the person to choose between them. Nobody had moved
        // anything: the provider assigns the initial status, and there was no history to disagree with.
        var item = await _board.CreateAsync(LocalBoardFixture.NewTask("Sent from triage"));
        item.Status.Should().Be(WorkItemStatus.Triage);
        _provider.OpensIn = "Doing";

        var synced = await Sync().SendAsync(ProjectId.Default, item.Id);
        synced.Happened.Should().BeTrue(synced.Sentence);
        await Sync().SyncNowAsync(ProjectId.Default);

        (await _board.Board.GetAsync(item.Id))!.Status.Should().Be(WorkItemStatus.InProgress);
        (await _mirror.OpenConflictsAsync(ProjectId.Default)).Should().BeEmpty("nothing was in disagreement");
        (await _outbox.OpenAsync(ProjectId.Default)).Should().BeEmpty("and nothing was queued back at the provider");
    }

    [Fact]
    public async Task An_item_sent_with_work_already_done_on_it_keeps_its_status_and_the_provider_is_moved_to_it()
    {
        // Backlog 49: a whole imported backlog sent at once, most of it Done. Adopting the provider's opening status
        // would have walked every finished item back to "To Do"; the item's own status is sent instead, in the
        // same press of Sync now.
        var item = await _board.CreateAsync(LocalBoardFixture.NewTask("Already under way", status: WorkItemStatus.Backlog));
        await _board.Board.TransitionAsync(item.Id, WorkItemStatus.InProgress, null, null, null, item.Version, LocalBoardFixture.User);
        _provider.OpensIn = "To Do";

        await Sync().SendAsync(ProjectId.Default, item.Id);
        var synced = await Sync().SyncNowAsync(ProjectId.Default);

        synced.Happened.Should().BeTrue(synced.Sentence);
        (await _board.Board.GetAsync(item.Id))!.Status.Should().Be(WorkItemStatus.InProgress);
        _provider.Transitions.Should().Equal(("JIRA-1", "Doing"));
        (await _mirror.OpenConflictsAsync(ProjectId.Default)).Should().BeEmpty();
        (await _outbox.OpenAsync(ProjectId.Default)).Should().BeEmpty("the transition went in the same act");
    }

    // ---- backlog 146: a crash with a write in flight ------------------------------------------------------

    [Fact]
    public async Task A_creation_left_in_flight_by_a_crash_is_looked_up_at_the_next_sync_and_never_made_twice()
    {
        var item = await _board.CreateAsync(LocalBoardFixture.NewTask("Sent as the process died"));
        await Sync().SendAsync(ProjectId.Default, item.Id);
        var entry = (await _outbox.ForItemAsync(ProjectId.Default, item.Id)).Single();

        // The crash: the entry was committed in flight, the call left and landed, and no answer was ever recorded.
        await _outbox.MarkAsync(ProjectId.Default, entry.Id, Fravashi.Domain.Mirroring.OutboxState.InFlight, null, null, true, _board.Clock.GetUtcNow());
        await _provider.CreateAsync(entry.Payload, entry.Id);

        // Before this item, nothing ran the recovery, and a drain sends nothing for an item with a write in flight.
        await new OutboxRecoveryService(new OutboxDrain(_outbox, _mirror, _board.Clock)).StartAsync(CancellationToken.None);
        await Sync().SyncNowAsync(ProjectId.Default);

        _provider.Created.Should().ContainSingle("the creation that landed was found by its stamp, not sent again");
        (await _mirror.GetAsync(ProjectId.Default, item.Id))!.Key.Should().Be("JIRA-1");

        // And the item is no longer blocked: a later change is sent.
        var stored = (await _board.Board.GetAsync(item.Id))!;
        await _board.Board.UpdateAsync(item.Id, new WorkItemPatch { Title = "Renamed after the crash" }, stored.Version, LocalBoardFixture.User);
        await Sync().SyncNowAsync(ProjectId.Default);
        (await _outbox.OpenAsync(ProjectId.Default)).Should().BeEmpty();
    }

    // ---- backlog 145: moving a project from one provider to another ------------------------------------

    [Fact]
    public async Task A_board_mirrored_to_one_provider_and_following_another_refuses_everything_until_told_to_follow_it()
    {
        var item = await _board.CreateAsync(LocalBoardFixture.NewTask("Was on Jira"));
        await Sync().SendAsync(ProjectId.Default, item.Id);
        await Sync().SyncNowAsync(ProjectId.Default);
        var azure = new RememberingProvider { Connection = "Azure DevOps", KeyPrefix = "" };
        _gateways.Gateway = azure;

        var synced = await Sync().SyncNowAsync(ProjectId.Default);
        var sent = await Sync().SendAllAsync(ProjectId.Default);

        synced.Happened.Should().BeFalse();
        synced.Sentence.Should().Contain("mirrored to Jira (1)").And.Contain("now follows Azure DevOps");
        sent.Happened.Should().BeFalse();
        (await _mirror.GetAsync(ProjectId.Default, item.Id))!.IsTombstoned.Should().BeFalse("nothing looked the Jira key up on Azure DevOps");
        (await Sync().StandingAsync(ProjectId.Default, item.Id))!.Value.Sentence.Should().Be(
            "On Jira as JIRA-1; this board now follows Azure DevOps.");
    }

    [Fact]
    public async Task Following_the_new_provider_keeps_the_old_one_as_history_and_the_board_can_be_sent_there()
    {
        var item = await _board.CreateAsync(LocalBoardFixture.NewTask("Moves over"));
        await Sync().SendAsync(ProjectId.Default, item.Id);
        await Sync().SyncNowAsync(ProjectId.Default);
        var azure = new RememberingProvider { Connection = "Azure DevOps", KeyPrefix = "" };
        _gateways.Gateway = azure;

        var followed = await Sync().FollowAsync(ProjectId.Default);

        followed.Happened.Should().BeTrue(followed.Sentence);
        followed.Sentence.Should().StartWith("1 item(s) that were on Jira keep that as history");
        (await _mirror.GetAsync(ProjectId.Default, item.Id)).Should().BeNull();
        (await Sync().StandingAsync(ProjectId.Default, item.Id))!.Value.Sentence.Should().Be(
            "Not on Azure DevOps. It was on Jira as JIRA-1.");
        _provider.Created.Should().ContainSingle("the move changes nothing on the old provider");

        (await Sync().SendAllAsync(ProjectId.Default)).Happened.Should().BeTrue();
        await Sync().SyncNowAsync(ProjectId.Default);

        azure.Created.Should().ContainSingle();
        (await _mirror.GetAsync(ProjectId.Default, item.Id))!.Connection.Should().Be("Azure DevOps");
    }

    [Fact]
    public async Task Send_everything_queues_each_item_not_on_the_provider_once()
    {
        var first = await _board.CreateAsync(LocalBoardFixture.NewTask("Already there"));
        await Sync().SendAsync(ProjectId.Default, first.Id);
        await Sync().SyncNowAsync(ProjectId.Default);
        var second = await _board.CreateAsync(LocalBoardFixture.NewTask("Second"));
        var third = await _board.CreateAsync(LocalBoardFixture.NewTask("Third"));

        var queued = await Sync().SendAllAsync(ProjectId.Default);

        queued.Happened.Should().BeTrue(queued.Sentence);
        queued.Sentence.Should().StartWith("2 item(s) are queued for Jira");
        (await _outbox.ForItemAsync(ProjectId.Default, second.Id)).Should().ContainSingle();
        (await _outbox.ForItemAsync(ProjectId.Default, third.Id)).Should().ContainSingle();

        var again = await Sync().SendAllAsync(ProjectId.Default);
        again.Happened.Should().BeFalse();
        again.Sentence.Should().Contain("already on Jira or waiting");

        await Sync().SyncNowAsync(ProjectId.Default);
        _provider.Created.Select(c => c.Fields[nameof(WorkItem.Title)]).Should().Equal("Already there", "Second", "Third");
    }

    [Fact]
    public async Task A_status_difference_on_an_item_nobody_just_created_is_still_a_question()
    {
        // The rule above is about creation only. An item mirrored without a status base by an older build
        // keeps asking, because there "who moved it" really has no answer.
        var item = await _board.CreateAsync(LocalBoardFixture.NewTask("Mirrored long ago"));
        _provider.PutDirect("JIRA-9", ProviderFields.Read(item), "Doing");
        await _mirror.ConfirmAsync(
            ProjectId.Default,
            item.Id,
            new ProviderConfirmation("Jira", "JIRA-9", "v1", item.Version, ProviderFields.Read(item)),
            _board.Clock.GetUtcNow());

        await Sync().SyncNowAsync(ProjectId.Default);

        (await _board.Board.GetAsync(item.Id))!.Status.Should().Be(WorkItemStatus.Triage);
        (await _mirror.OpenConflictsAsync(ProjectId.Default)).Should().ContainSingle();
    }

    [Fact]
    public async Task Pressing_send_twice_queues_one_creation_not_two()
    {
        var item = await _board.CreateAsync(LocalBoardFixture.NewTask("Only once"));

        (await Sync().SendAsync(ProjectId.Default, item.Id)).Happened.Should().BeTrue();
        var again = await Sync().SendAsync(ProjectId.Default, item.Id);

        again.Happened.Should().BeFalse();
        again.Sentence.Should().Contain("already waiting");
        (await _outbox.ForItemAsync(ProjectId.Default, item.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task An_item_already_on_the_provider_is_not_sent_again_and_the_sentence_names_its_key()
    {
        var item = await _board.CreateAsync(LocalBoardFixture.NewTask("Already there"));
        await Sync().SendAsync(ProjectId.Default, item.Id);
        await Sync().SyncNowAsync(ProjectId.Default);

        var again = await Sync().SendAsync(ProjectId.Default, item.Id);

        again.Happened.Should().BeFalse();
        again.Sentence.Should().Contain("JIRA-1");
        _provider.Created.Should().ContainSingle();
    }

    [Fact]
    public async Task A_change_made_on_the_provider_arrives_here_at_the_next_sync()
    {
        var item = await _board.CreateAsync(LocalBoardFixture.NewTask("Title as sent"));
        await Sync().SendAsync(ProjectId.Default, item.Id);
        await Sync().SyncNowAsync(ProjectId.Default);

        _provider.Edit("JIRA-1", nameof(WorkItem.Title), "Title as edited on the site");
        var synced = await Sync().SyncNowAsync(ProjectId.Default);

        synced.Sentence.Should().Contain("1 updated here");
        (await _board.Board.GetAsync(item.Id))!.Title.Should().Be("Title as edited on the site");
    }

    [Fact]
    public async Task With_the_provider_unreachable_nothing_is_lost_and_the_sentence_says_so()
    {
        var item = await _board.CreateAsync(LocalBoardFixture.NewTask("Waits for the network"));
        await Sync().SendAsync(ProjectId.Default, item.Id);
        _provider.Unreachable = true;

        var synced = await Sync().SyncNowAsync(ProjectId.Default);

        synced.Happened.Should().BeFalse();
        synced.Sentence.Should().Contain("could not be reached").And.Contain("nothing was lost");
        (await _outbox.OpenAsync(ProjectId.Default)).Should().ContainSingle(entry => entry.State == OutboxState.Pending);

        _provider.Unreachable = false;
        (await Sync().SyncNowAsync(ProjectId.Default)).Happened.Should().BeTrue();
        _provider.Created.Should().ContainSingle("the queued creation went once the provider answered");
    }

    [Fact]
    public async Task A_project_that_follows_no_provider_is_told_so_and_nothing_is_queued()
    {
        _gateways.Configured = false;
        var item = await _board.CreateAsync(LocalBoardFixture.NewTask("Local only"));

        (await Sync().SendAsync(ProjectId.Default, item.Id)).Sentence.Should().Contain("follows no provider");
        (await Sync().SyncNowAsync(ProjectId.Default)).Sentence.Should().Contain("follows no provider");
        Sync().ConnectionFor(ProjectId.Default).Should().BeNull();
        (await _outbox.OpenAsync(ProjectId.Default)).Should().BeEmpty();
    }

    // ---- backlog 47 increment 5 and backlog 48: a move this board makes reaches the provider -------------

    /// <summary>An item that exists on the provider, which is the only kind a move can be sent about.</summary>
    private async Task<WorkItem> MirroredAsync(string title = "Already there")
    {
        var item = await _board.CreateAsync(LocalBoardFixture.NewTask(title));
        await Sync().SendAsync(ProjectId.Default, item.Id);
        await Sync().SyncNowAsync(ProjectId.Default);
        return (await _board.Board.GetAsync(item.Id))!;
    }

    [Fact]
    public async Task A_move_into_a_column_the_provider_names_once_is_queued_in_that_name()
    {
        var item = await MirroredAsync();

        var moved = await Sync().MovedAsync(ProjectId.Default, item.Id, WorkItemStatus.InProgress);

        moved.Outcome.Should().Be(ProviderMoveOutcome.Queued);
        moved.Sentence.Should().Contain("'Doing'");
        var queued = (await _outbox.OpenAsync(ProjectId.Default)).Single();
        queued.Kind.Should().Be(OutboxKind.Transition);
        queued.Payload.Should().Contain("Doing");
    }

    [Fact]
    public async Task A_move_into_a_column_the_provider_names_three_ways_asks_which_one_and_queues_nothing()
    {
        var item = await MirroredAsync();

        var moved = await Sync(Several).MovedAsync(ProjectId.Default, item.Id, WorkItemStatus.Done);

        moved.Outcome.Should().Be(ProviderMoveOutcome.NeedsAChoice);
        moved.Choices.Should().Equal("Shipped", "Closed");
        moved.Sentence.Should().Contain("more than one state");
        (await _outbox.OpenAsync(ProjectId.Default)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_state_the_person_picks_is_the_one_queued()
    {
        var item = await MirroredAsync();

        var moved = await Sync(Several).MovedAsync(ProjectId.Default, item.Id, WorkItemStatus.Done, "Closed");

        moved.Outcome.Should().Be(ProviderMoveOutcome.Queued);
        (await _outbox.OpenAsync(ProjectId.Default)).Single().Payload.Should().Contain("Closed");
    }

    [Fact]
    public async Task A_state_nobody_offered_is_refused_rather_than_passed_through_to_the_provider()
    {
        var item = await MirroredAsync();

        var moved = await Sync(Several).MovedAsync(ProjectId.Default, item.Id, WorkItemStatus.Done, "Deleted");

        moved.Outcome.Should().Be(ProviderMoveOutcome.NeedsAChoice);
        moved.Sentence.Should().Contain("not one of the states offered");
        (await _outbox.OpenAsync(ProjectId.Default)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_move_of_an_item_the_provider_has_never_seen_sends_nothing_and_says_nothing()
    {
        var item = await _board.CreateAsync(LocalBoardFixture.NewTask("Local only so far"));

        var moved = await Sync().MovedAsync(ProjectId.Default, item.Id, WorkItemStatus.InProgress);

        moved.Outcome.Should().Be(ProviderMoveOutcome.NothingToSend);
        (await _outbox.OpenAsync(ProjectId.Default)).Should().BeEmpty("sending it there is its own act");
    }

    [Fact]
    public async Task A_move_on_a_board_that_follows_no_provider_is_simply_nothing()
    {
        var item = await MirroredAsync();
        _gateways.Configured = false;

        (await Sync().MovedAsync(ProjectId.Default, item.Id, WorkItemStatus.Done)).Outcome
            .Should().Be(ProviderMoveOutcome.NothingToSend);
    }

    /// <summary>The provider with a workflow that can be asked, which is what a real tracker has.</summary>
    private RememberingProvider WithWorkflow(params string[] offers)
    {
        _provider.Capabilities = _provider.Capabilities with { CanOfferTransitions = true };
        _provider.Offers = [.. offers];
        return _provider;
    }

    [Fact]
    public async Task When_the_workflow_leaves_one_of_the_mapped_names_reachable_nothing_is_asked()
    {
        // The mapping says Done can be Shipped or Closed; the site's workflow offers only Closed from here.
        // There is nothing left to ask, so it is not asked.
        var item = await MirroredAsync();
        WithWorkflow("Closed", "In Progress");

        var moved = await Sync(Several).MovedAsync(ProjectId.Default, item.Id, WorkItemStatus.Done);

        moved.Outcome.Should().Be(ProviderMoveOutcome.Queued);
        (await _outbox.OpenAsync(ProjectId.Default)).Single().Payload.Should().Contain("Closed");
    }

    [Fact]
    public async Task A_person_is_never_offered_a_state_the_workflow_would_refuse()
    {
        var item = await MirroredAsync();
        WithWorkflow("Shipped", "Closed", "Abandoned");

        var moved = await Sync(Several).MovedAsync(ProjectId.Default, item.Id, WorkItemStatus.Done);

        moved.Outcome.Should().Be(ProviderMoveOutcome.NeedsAChoice);
        moved.Choices.Should().Equal("Shipped", "Closed");
        moved.Choices.Should().NotContain("Abandoned", "it reads as nothing like Done on this board");
    }

    [Fact]
    public async Task When_nothing_the_workflow_offers_reads_as_that_column_its_own_states_are_offered_and_said()
    {
        var item = await MirroredAsync();
        WithWorkflow("Blocked", "Escalated");

        var moved = await Sync(Several).MovedAsync(ProjectId.Default, item.Id, WorkItemStatus.Done);

        moved.Outcome.Should().Be(ProviderMoveOutcome.NeedsAChoice);
        moved.Choices.Should().Equal("Blocked", "Escalated");
        moved.Sentence.Should().Contain("Nothing Jira offers").And.Contain("yours to say");
    }

    [Fact]
    public async Task A_workflow_that_offers_nothing_at_all_queues_nothing_and_says_the_move_stays_here()
    {
        var item = await MirroredAsync();
        WithWorkflow();

        var moved = await Sync(Several).MovedAsync(ProjectId.Default, item.Id, WorkItemStatus.Done);

        moved.Outcome.Should().Be(ProviderMoveOutcome.NeedsAChoice);
        moved.Choices.Should().BeEmpty();
        moved.Sentence.Should().Contain("offers no other state");
        (await _outbox.OpenAsync(ProjectId.Default)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_workflow_that_cannot_be_reached_falls_back_to_the_mapping_and_says_so()
    {
        var item = await MirroredAsync();
        WithWorkflow("Closed");
        _provider.OfferRefusal = "No route to the site.";

        var moved = await Sync(Several).MovedAsync(ProjectId.Default, item.Id, WorkItemStatus.Done);

        moved.Outcome.Should().Be(ProviderMoveOutcome.NeedsAChoice);
        moved.Choices.Should().Equal(
            ["Shipped", "Closed"], "the mapping is what there was before it could ask");
        moved.Sentence.Should().Contain("could not be asked").And.Contain("No route to the site.");
    }

    [Fact]
    public async Task A_state_the_workflow_offers_but_the_person_was_not_offered_is_still_refused()
    {
        var item = await MirroredAsync();
        WithWorkflow("Shipped", "Closed", "Abandoned");

        var moved = await Sync(Several).MovedAsync(ProjectId.Default, item.Id, WorkItemStatus.Done, "Abandoned");

        moved.Outcome.Should().Be(ProviderMoveOutcome.NeedsAChoice);
        moved.Sentence.Should().Contain("not one of the states offered");
        (await _outbox.OpenAsync(ProjectId.Default)).Should().BeEmpty();
    }

    private static readonly Fravashi.Application.Onboarding.BoardStatusMapping Several = new("jira",
    [
        new Fravashi.Application.Onboarding.StatusMappingEntry("In Progress", "Doing"),
        new Fravashi.Application.Onboarding.StatusMappingEntry("Done", "Shipped"),
        new Fravashi.Application.Onboarding.StatusMappingEntry("Done", "Closed"),
    ]);

    private sealed class Gateways(IProviderGateway gateway) : IProviderGateways
    {
        public bool Configured { get; set; } = true;

        /// <summary>The provider the project follows; replaced to model the project moving to another one.</summary>
        public IProviderGateway Gateway { get; set; } = gateway;

        public IProviderGateway? For(ProjectId project) => Configured ? Gateway : null;
    }

    /// <summary>A provider that keeps what it was sent, so a round trip can be read back out of it.</summary>
    private sealed class RememberingProvider : IProviderGateway
    {
        private readonly Dictionary<string, ProviderItem> _byOperation = [];
        private int _next = 1;
        private int _version = 1;

        public string Connection { get; init; } = "Jira";

        /// <summary>What the keys this provider makes start with.</summary>
        public string KeyPrefix { get; init; } = "JIRA-";

        public ProviderCapabilities Capabilities { get; set; } = new(true, true, true);

        /// <summary>What this provider's workflow offers for any item, or null when it cannot be asked.</summary>
        public List<string>? Offers { get; set; }

        /// <summary>Why asking fails, when it does.</summary>
        public string? OfferRefusal { get; set; }

        public Task<ProviderResult<IReadOnlyList<string>>> OfferedTransitionsAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(OfferRefusal is { } why
                ? ProviderResult<IReadOnlyList<string>>.Unreachable(why)
                : ProviderResult<IReadOnlyList<string>>.Ok(Offers ?? []));

        /// <summary>The status this provider opens a newly created item in, as a real tracker does.</summary>
        public string OpensIn { get; set; } = "To Do";

        /// <summary>Puts an item there without anybody creating it, for a mirrored-long-ago case.</summary>
        public void PutDirect(string key, IReadOnlyDictionary<string, string?> fields, string status) =>
            _byOperation["direct:" + key] = new ProviderItem(key, "v1", fields, status, null);

        public bool Unreachable { get; set; }

        public List<ProviderItem> Created { get; } = [];

        public void Edit(string key, string field, string? value)
        {
            var (operation, item) = _byOperation.Single(pair => pair.Value.Key == key);
            var fields = new Dictionary<string, string?>(item.Fields, StringComparer.Ordinal) { [field] = value };
            _byOperation[operation] = item with { Fields = fields, ProviderVersion = "v" + (++_version) };
        }

        public Task<ProviderResult<ProviderItem>> CreateAsync(string payload, string operationId, CancellationToken ct = default)
        {
            if (Unreachable)
            {
                return Task.FromResult(ProviderResult<ProviderItem>.Unreachable("No route."));
            }

            var fields = JsonSerializer.Deserialize<Dictionary<string, string?>>(payload)!;
            var item = new ProviderItem(KeyPrefix + _next++, "v1", fields, OpensIn);
            _byOperation[operationId] = item;
            Created.Add(item);
            return Task.FromResult(ProviderResult<ProviderItem>.Ok(item));
        }

        public Task<ProviderResult<ProviderItem>> GetAsync(string key, CancellationToken ct = default)
        {
            if (Unreachable)
            {
                return Task.FromResult(ProviderResult<ProviderItem>.Unreachable("No route."));
            }

            var item = _byOperation.Values.FirstOrDefault(i => i.Key == key);
            return Task.FromResult(item is null
                ? ProviderResult<ProviderItem>.Gone(key + " is not there.")
                : ProviderResult<ProviderItem>.Ok(item));
        }

        public Task<ProviderResult<ProviderItem?>> FindByOperationIdAsync(string operationId, CancellationToken ct = default) =>
            Task.FromResult(ProviderResult<ProviderItem?>.Ok(_byOperation.GetValueOrDefault(operationId)));

        public Task<ProviderResult<ProviderItem>> UpdateAsync(string key, string payload, string? providerVersion, string operationId, CancellationToken ct = default) =>
            GetAsync(key, ct);

        /// <summary>Every transition sent, by key and the native status it asked for.</summary>
        public List<(string Key, string? Status)> Transitions { get; } = [];

        public Task<ProviderResult<ProviderItem>> TransitionAsync(string key, string payload, string? providerVersion, string operationId, CancellationToken ct = default)
        {
            var status = JsonSerializer.Deserialize<Dictionary<string, string?>>(payload)?.GetValueOrDefault("NativeStatus");
            Transitions.Add((key, status));
            var held = _byOperation.FirstOrDefault(p => p.Value.Key == key);
            if (held.Value is not null && status is not null)
            {
                _byOperation[held.Key] = held.Value with { NativeStatus = status };
            }

            return GetAsync(key, ct);
        }
    }
}
