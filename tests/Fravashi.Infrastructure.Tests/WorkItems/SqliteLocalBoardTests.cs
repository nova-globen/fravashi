using System.Text.Json.Nodes;
using AwesomeAssertions;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Xunit;

namespace Fravashi.Infrastructure.Tests.WorkItems;

/// <summary>
/// Increment B of the canonical artifact model plan: create, read, update, transition and query through the
/// <see cref="ITracker"/> port against the real SQLite store on a temporary database, with the event log, the
/// operation ledger and optimistic concurrency.
/// </summary>
public sealed class SqliteLocalBoardTests : IDisposable
{
    private readonly LocalBoardFixture _fixture = new();

    private ITracker Board => _fixture.Board;

    public void Dispose() => _fixture.Dispose();

    // -------------------------------------------------------------------------------------------------
    // Create and read
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_create_round_trips_every_field_through_the_store()
    {
        var feature = await _fixture.CreateAsync(new NewWorkItem { Level = WorkItemLevel.Feature, Title = "Feature", Description = "Why." });
        var increment = await _fixture.CreateAsync(new NewWorkItem { Level = WorkItemLevel.Increment, Title = "Increment", Description = "What.", Parent = feature.Id });

        var created = await Board.CreateAsync(
            new NewWorkItem
            {
                Level = WorkItemLevel.Task,
                Kind = WorkItemKind.Spike,
                Title = "Find out",
                Description = "The question and what an answer unlocks.",
                Parent = increment.Id,
                Status = WorkItemStatus.Ready,
                Priority = WorkItemPriority.High,
                Assignee = "ada",
                SpikeTimebox = TimeSpan.FromMinutes(90),
                Flags = WorkItemFlags.None.WithDeferred("after the release"),
            },
            new MutationContext(WorkItemActor.Session("s-1"), "op-create"));

        created.Outcome.Should().Be(TrackerOutcome.Ok);
        created.Replayed.Should().BeFalse();
        var item = created.Value;
        var read = await Board.GetAsync(item.Id);

        read.Should().Be(item, "what Create returns is what the store holds");
        read!.Project.Should().Be(ProjectId.Default);
        read.Number.Should().Be(3);
        read.Level.Should().Be(WorkItemLevel.Task);
        read.Kind.Should().Be(WorkItemKind.Spike);
        read.Title.Should().Be("Find out");
        read.Description.Should().Be("The question and what an answer unlocks.");
        read.Parent.Should().Be(increment.Id);
        read.Status.Should().Be(WorkItemStatus.Ready);
        read.Category.Should().Be(StatusCategory.Proposed);
        read.Resolution.Should().BeNull();
        read.Priority.Should().Be(WorkItemPriority.High);
        read.Assignee.Should().Be("ada");
        read.SpikeTimebox.Should().Be(TimeSpan.FromMinutes(90));
        read.Flags.Deferred.Should().BeTrue();
        read.Flags.DeferredReason.Should().Be("after the release");
        read.Version.Should().Be(1);
        read.Rank.Should().Be(1.0);
        read.CreatedUtc.Should().Be(_fixture.Clock.GetUtcNow());
        read.External.Should().BeNull();

        (await Board.GetByNumberAsync(3)).Should().Be(item);
        (await Board.GetByNumberAsync(99)).Should().BeNull();
        (await Board.GetAsync(WorkItemId.New())).Should().BeNull();
    }

    [Fact]
    public async Task Numbers_are_allocated_per_project_independently()
    {
        _fixture.CreateProject("other");
        var other = _fixture.Resolver.For(new ProjectId("other"));

        var a1 = await _fixture.TaskAsync("a1");
        var b1 = await _fixture.CreateAsync(LocalBoardFixture.NewTask("b1"), other);
        var a2 = await _fixture.TaskAsync("a2");
        var a3 = await _fixture.TaskAsync("a3");
        var b2 = await _fixture.CreateAsync(LocalBoardFixture.NewTask("b2"), other);

        new[] { a1.Number, a2.Number, a3.Number }.Should().Equal(1, 2, 3);
        new[] { b1.Number, b2.Number }.Should().Equal(1, 2);
        (await Board.GetByNumberAsync(1))!.Id.Should().Be(a1.Id);
        (await other.GetByNumberAsync(1))!.Id.Should().Be(b1.Id);
        (await Board.GetAsync(b1.Id)).Should().BeNull("a tracker is bound to its project and cannot read another's items");
    }

    [Fact]
    public async Task A_create_without_a_description_is_invalid_and_writes_nothing()
    {
        var before = _fixture.StorageSnapshot();

        var refused = await Board.CreateAsync(LocalBoardFixture.NewTask(description: "   "), new MutationContext(WorkItemActor.User, "op-1"));

        refused.Outcome.Should().Be(TrackerOutcome.Invalid);
        refused.Errors.Should().ContainSingle().Which.Should().Match<ValidationError>(e => e.Code == WorkItemRules.Codes.DescriptionRequired && e.Field == "description");
        _fixture.StorageSnapshot().Should().Be(before, "no row, no event, no ledger row and no number consumed");
        (await _fixture.TaskAsync()).Number.Should().Be(1);
    }

    [Fact]
    public async Task A_create_under_a_parent_two_levels_up_is_refused_with_the_domain_code()
    {
        var feature = await _fixture.CreateAsync(new NewWorkItem { Level = WorkItemLevel.Feature, Title = "F", Description = "D" });

        var refused = await Board.CreateAsync(LocalBoardFixture.NewTask(parent: feature.Id), MutationContext.User);

        refused.Errors.Select(e => e.Code).Should().Equal(WorkItemRules.Codes.ParentLevelNotAdjacent);
    }

    // -------------------------------------------------------------------------------------------------
    // Update
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_update_round_trips_every_patched_field_and_leaves_the_rest_alone()
    {
        var increment = await _fixture.CreateAsync(new NewWorkItem { Level = WorkItemLevel.Increment, Title = "I", Description = "D" });
        var task = await _fixture.TaskAsync("before");
        _fixture.Clock.Advance(TimeSpan.FromMinutes(5));

        var updated = await Board.UpdateAsync(
            task.Id,
            new WorkItemPatch
            {
                Title = "after",
                Description = "A sharper description.",
                Kind = WorkItemKind.Bug,
                Parent = increment.Id,
                Priority = WorkItemPriority.Critical,
                Assignee = "someone",
                Flags = WorkItemFlags.None.WithBlocked("waiting on the vendor"),
            },
            task.Version,
            MutationContext.User);

        updated.Outcome.Should().Be(TrackerOutcome.Ok);
        var read = await _fixture.ReloadAsync(task);
        read.Should().Be(updated.Value);
        read.Title.Should().Be("after");
        read.Description.Should().Be("A sharper description.");
        read.Kind.Should().Be(WorkItemKind.Bug);
        read.Parent.Should().Be(increment.Id);
        read.Priority.Should().Be(WorkItemPriority.Critical);
        read.Assignee.Should().Be("someone");
        read.Flags.Blocked.Should().BeTrue();
        read.Flags.BlockedReason.Should().Be("waiting on the vendor");
        read.Level.Should().Be(task.Level);
        read.Status.Should().Be(task.Status);
        read.Rank.Should().Be(task.Rank);
        read.Number.Should().Be(task.Number);
        read.Version.Should().Be(task.Version + 1);
        read.UpdatedUtc.Should().Be(_fixture.Clock.GetUtcNow());
        read.CreatedUtc.Should().Be(task.CreatedUtc);

        // Setting a nullable field to null is a change, not "not mentioned".
        var cleared = await Board.UpdateAsync(task.Id, new WorkItemPatch { Assignee = Optional<string?>.Of(null), Parent = Optional<WorkItemId?>.Of(null) }, read.Version, MutationContext.User);
        cleared.Value.Assignee.Should().BeNull();
        cleared.Value.Parent.Should().BeNull();
        cleared.Value.Title.Should().Be("after");
    }

    [Fact]
    public async Task An_update_with_a_stale_version_is_a_conflict_naming_the_current_version()
    {
        var task = await _fixture.TaskAsync();
        (await Board.UpdateAsync(task.Id, new WorkItemPatch { Title = "first" }, task.Version, MutationContext.User)).IsOk.Should().BeTrue();
        var before = _fixture.StorageSnapshot();

        var stale = await Board.UpdateAsync(task.Id, new WorkItemPatch { Title = "second" }, task.Version, MutationContext.User);

        stale.Outcome.Should().Be(TrackerOutcome.Conflict);
        stale.CurrentVersion.Should().Be(2);
        _fixture.StorageSnapshot().Should().Be(before);
        (await Board.UpdateAsync(WorkItemId.New(), new WorkItemPatch(), 1, MutationContext.User)).Outcome.Should().Be(TrackerOutcome.NotFound);
    }

    [Fact]
    public async Task A_level_change_that_would_strand_a_child_is_refused()
    {
        var increment = await _fixture.CreateAsync(new NewWorkItem { Level = WorkItemLevel.Increment, Title = "I", Description = "D" });
        await _fixture.CreateAsync(LocalBoardFixture.NewTask(parent: increment.Id));

        var refused = await Board.UpdateAsync(increment.Id, new WorkItemPatch { Level = WorkItemLevel.Task }, increment.Version, MutationContext.User);

        refused.Errors.Select(e => e.Code).Should().Contain(WorkItemRules.Codes.ChildLevelNotLower);
    }

    // -------------------------------------------------------------------------------------------------
    // Transition
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_transition_updates_the_status_and_therefore_the_category()
    {
        var task = await _fixture.TaskAsync();
        task.Category.Should().Be(StatusCategory.Proposed);

        var started = await Board.TransitionAsync(task.Id, WorkItemStatus.InProgress, null, null, null, task.Version, MutationContext.User);
        started.Value.Status.Should().Be(WorkItemStatus.InProgress);
        started.Value.Category.Should().Be(StatusCategory.InProgress);
        (await _fixture.ReloadAsync(task)).Category.Should().Be(StatusCategory.InProgress);

        var done = await Board.TransitionAsync(task.Id, WorkItemStatus.Done, null, null, null, started.Value.Version, MutationContext.User);
        var read = await _fixture.ReloadAsync(task);
        read.Should().Be(done.Value);
        read.Status.Should().Be(WorkItemStatus.Done);
        read.Category.Should().Be(StatusCategory.Done);
        read.Resolution.Should().Be(WorkItemResolution.Completed);
        read.Version.Should().Be(3);
        _fixture.Count("SELECT COUNT(*) FROM work_items WHERE status = 'done';").Should().Be(1, "the status is stored by its stable name");
    }

    [Fact]
    public async Task A_domain_transition_refusal_arrives_as_invalid_with_the_domain_code_and_writes_nothing()
    {
        var task = await _fixture.TaskAsync();
        var before = _fixture.StorageSnapshot();

        var refused = await Board.TransitionAsync(task.Id, WorkItemStatus.Cancelled, null, null, null, task.Version, MutationContext.User);

        refused.Outcome.Should().Be(TrackerOutcome.Invalid);
        refused.Errors.Select(e => e.Code).Should().Equal(WorkItemRules.Codes.CancelledResolutionRequired);
        _fixture.StorageSnapshot().Should().Be(before);
    }

    // -------------------------------------------------------------------------------------------------
    // Events and the operation ledger
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_mutation_appends_one_event_with_its_actor_and_a_session_records_its_id()
    {
        var session = new MutationContext(WorkItemActor.Session("session-42"), "op-a");
        var created = (await Board.CreateAsync(LocalBoardFixture.NewTask("t"), session)).Value;
        var updated = (await Board.UpdateAsync(created.Id, new WorkItemPatch { Title = "t2" }, created.Version, MutationContext.User)).Value;
        var moved = (await Board.TransitionAsync(created.Id, WorkItemStatus.Ready, null, null, null, updated.Version, new MutationContext(WorkItemActor.System))).Value;

        var history = await Board.HistoryAsync(created.Id);

        history.Select(e => e.Operation).Should().Equal(WorkItemEventOperation.Created, WorkItemEventOperation.Updated, WorkItemEventOperation.Transitioned);
        history.Select(e => e.ItemVersion).Should().Equal(1, 2, 3);
        history[0].Actor.Should().Be(WorkItemActor.Session("session-42"));
        history[0].OperationId.Should().Be("op-a");
        history[1].Actor.Should().Be(WorkItemActor.User);
        history[1].OperationId.Should().BeNull();
        history[2].Actor.Should().Be(WorkItemActor.System);
        moved.Version.Should().Be(3);

        var changes = JsonNode.Parse(history[1].ChangesJson)!.AsObject();
        changes.Select(p => p.Key).Should().Equal("title");
        changes["title"]!["from"]!.GetValue<string>().Should().Be("t");
        changes["title"]!["to"]!.GetValue<string>().Should().Be("t2");
        _fixture.Count("SELECT COUNT(*) FROM work_item_events WHERE actor_kind = 'session' AND actor_id = 'session-42';").Should().Be(1);
    }

    [Fact]
    public async Task Replaying_an_operation_id_is_ok_replayed_and_writes_nothing()
    {
        var context = new MutationContext(WorkItemActor.User, "op-create");
        var first = await Board.CreateAsync(LocalBoardFixture.NewTask("once"), context);
        var update = new MutationContext(WorkItemActor.User, "op-update");
        var updated = await Board.UpdateAsync(first.Value.Id, new WorkItemPatch { Title = "twice?" }, 1, update);
        var before = _fixture.StorageSnapshot();

        var replayedCreate = await Board.CreateAsync(LocalBoardFixture.NewTask("once"), context);
        var replayedUpdate = await Board.UpdateAsync(first.Value.Id, new WorkItemPatch { Title = "twice?" }, 1, update);

        replayedCreate.Outcome.Should().Be(TrackerOutcome.Ok);
        replayedCreate.Replayed.Should().BeTrue();
        replayedCreate.Value.Id.Should().Be(first.Value.Id);
        replayedUpdate.Replayed.Should().BeTrue();
        replayedUpdate.Value.Should().Be(updated.Value, "a replay returns the current state, even with the version it was first sent with");
        _fixture.StorageSnapshot().Should().Be(before, "no second row, no second event, still one ledger row per operation");
        _fixture.Count("SELECT COUNT(*) FROM work_items;").Should().Be(1);
        _fixture.Count("SELECT COUNT(*) FROM work_item_operations;").Should().Be(2);
    }

    [Fact]
    public async Task Reusing_an_operation_id_for_a_different_operation_or_item_is_invalid()
    {
        var a = (await Board.CreateAsync(LocalBoardFixture.NewTask("a"), new MutationContext(WorkItemActor.User, "op-a"))).Value;
        var b = await _fixture.TaskAsync("b");
        await Board.UpdateAsync(a.Id, new WorkItemPatch { Title = "a2" }, a.Version, new MutationContext(WorkItemActor.User, "op-u"));
        var before = _fixture.StorageSnapshot();

        var otherOperation = await Board.UpdateAsync(a.Id, new WorkItemPatch { Title = "x" }, 2, new MutationContext(WorkItemActor.User, "op-a"));
        var otherItem = await Board.UpdateAsync(b.Id, new WorkItemPatch { Title = "x" }, b.Version, new MutationContext(WorkItemActor.User, "op-u"));

        otherOperation.Errors.Select(e => e.Code).Should().Equal(TrackerCodes.OperationIdReused);
        otherItem.Errors.Select(e => e.Code).Should().Equal(TrackerCodes.OperationIdReused);
        _fixture.StorageSnapshot().Should().Be(before);
    }

    [Fact]
    public async Task A_conflicted_call_records_no_ledger_row_so_a_retry_with_the_same_id_succeeds()
    {
        var task = await _fixture.TaskAsync();
        await Board.UpdateAsync(task.Id, new WorkItemPatch { Title = "someone else" }, task.Version, MutationContext.User);
        var context = new MutationContext(WorkItemActor.User, "op-retry");

        var conflicted = await Board.UpdateAsync(task.Id, new WorkItemPatch { Title = "mine" }, task.Version, context);
        conflicted.Outcome.Should().Be(TrackerOutcome.Conflict);
        _fixture.Count("SELECT COUNT(*) FROM work_item_operations;").Should().Be(0);

        var retried = await Board.UpdateAsync(task.Id, new WorkItemPatch { Title = "mine" }, conflicted.CurrentVersion!.Value, context);
        retried.Outcome.Should().Be(TrackerOutcome.Ok);
        retried.Replayed.Should().BeFalse();
        retried.Value.Title.Should().Be("mine");
        _fixture.Count("SELECT COUNT(*) FROM work_item_operations WHERE operation_id = 'op-retry';").Should().Be(1);
    }

    // -------------------------------------------------------------------------------------------------
    // Query
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Query_filters_by_every_dimension_and_orders_by_status_then_rank_then_number()
    {
        var feature = await _fixture.CreateAsync(new NewWorkItem { Level = WorkItemLevel.Feature, Title = "Search feature", Description = "Needle in the description" });
        var increment = await _fixture.CreateAsync(new NewWorkItem { Level = WorkItemLevel.Increment, Kind = WorkItemKind.Bug, Title = "Bug", Description = "D", Parent = feature.Id, Status = WorkItemStatus.InReview });
        var ready = await _fixture.CreateAsync(LocalBoardFixture.NewTask("ready one", status: WorkItemStatus.Ready) with { Parent = increment.Id, Assignee = "ann" });
        var blocked = await _fixture.CreateAsync(LocalBoardFixture.NewTask("blocked one") with { Flags = WorkItemFlags.None.WithBlocked("vendor") });
        var deferred = await _fixture.CreateAsync(LocalBoardFixture.NewTask("deferred one") with { Flags = WorkItemFlags.None.WithDeferred(null) });
        var triage2 = await _fixture.TaskAsync("triage two");
        (await Board.LinkAsync(blocked.Id, WorkItemLinkType.Blocks, ready.Id, MutationContext.User)).IsOk.Should().BeTrue();

        async Task<IReadOnlyList<int>> Numbers(WorkItemQuery query) => (await Board.QueryAsync(query)).Items.Select(i => i.Number).ToList();

        // Triage column (feature, blocked, deferred, triage2 by rank), then Ready, then In Review.
        (await Numbers(new WorkItemQuery())).Should().Equal(feature.Number, blocked.Number, deferred.Number, triage2.Number, ready.Number, increment.Number);
        (await Numbers(new WorkItemQuery { Statuses = [WorkItemStatus.Ready] })).Should().Equal(ready.Number);
        (await Numbers(new WorkItemQuery { Categories = [StatusCategory.InProgress] })).Should().Equal(increment.Number);
        (await Numbers(new WorkItemQuery { Levels = [WorkItemLevel.Feature, WorkItemLevel.Increment] })).Should().Equal(feature.Number, increment.Number);
        (await Numbers(new WorkItemQuery { Kinds = [WorkItemKind.Bug] })).Should().Equal(increment.Number);
        (await Numbers(new WorkItemQuery { Parent = increment.Id })).Should().Equal(ready.Number);
        (await Numbers(new WorkItemQuery { NoParent = true })).Should().Equal(feature.Number, blocked.Number, deferred.Number, triage2.Number);
        (await Numbers(new WorkItemQuery { Flags = FlagFilter.Blocked })).Should().Equal(blocked.Number);
        (await Numbers(new WorkItemQuery { Flags = FlagFilter.Deferred })).Should().Equal(deferred.Number);
        (await Numbers(new WorkItemQuery { Flags = FlagFilter.Either })).Should().Equal(blocked.Number, deferred.Number);
        (await Numbers(new WorkItemQuery { Flags = FlagFilter.Neither })).Should().Equal(feature.Number, triage2.Number, ready.Number, increment.Number);
        (await Numbers(new WorkItemQuery { Assignee = "ann" })).Should().Equal(ready.Number);
        (await Numbers(new WorkItemQuery { Text = "NEEDLE" })).Should().Equal(feature.Number);
        (await Numbers(new WorkItemQuery { Text = "one" })).Should().Equal(blocked.Number, deferred.Number, ready.Number);
        (await Numbers(new WorkItemQuery { Text = "100%" })).Should().BeEmpty("a LIKE wildcard in the text is matched literally");
        (await Numbers(new WorkItemQuery { LinkedTo = new LinkedToFilter(blocked.Id, WorkItemLinkType.Blocks) })).Should().Equal(ready.Number);
        (await Numbers(new WorkItemQuery { LinkedTo = new LinkedToFilter(ready.Id, WorkItemLinkType.BlockedBy) })).Should().Equal(blocked.Number);
        (await Numbers(new WorkItemQuery { Statuses = [WorkItemStatus.Triage], Flags = FlagFilter.Neither, Levels = [WorkItemLevel.Task] })).Should().Equal(triage2.Number);
    }

    [Fact]
    public async Task Names_wiki_document_finds_a_plan_reference_and_a_written_mention_and_nothing_that_only_looks_like_one()
    {
        // Work item #365: the wiki's "Linked from" asks which items name a document.
        var planned = await _fixture.CreateAsync(LocalBoardFixture.NewTask("planned") with
        {
            Refs = [new Fravashi.Domain.WorkItems.AgentLayer.ArtifactRef(
                "ref-1", Fravashi.Domain.WorkItems.AgentLayer.ArtifactRefRole.Plan, Fravashi.Domain.WorkItems.AgentLayer.ArtifactRefKind.Wiki, "12")],
        });
        var designed = await _fixture.CreateAsync(LocalBoardFixture.NewTask("designed") with
        {
            Refs = [new Fravashi.Domain.WorkItems.AgentLayer.ArtifactRef(
                "ref-2", Fravashi.Domain.WorkItems.AgentLayer.ArtifactRefRole.Context, Fravashi.Domain.WorkItems.AgentLayer.ArtifactRefKind.Design, "@12")],
        });
        var middle = await _fixture.CreateAsync(LocalBoardFixture.NewTask("middle", "See @12, the plan."));
        var start = await _fixture.CreateAsync(LocalBoardFixture.NewTask("start", "@12 says how."));
        var end = await _fixture.CreateAsync(LocalBoardFixture.NewTask("in the title @12", "Nothing here."));
        await _fixture.CreateAsync(LocalBoardFixture.NewTask("longer", "See @123 instead."));
        await _fixture.CreateAsync(LocalBoardFixture.NewTask("mail", "Write to someone@12 now."));
        await _fixture.CreateAsync(LocalBoardFixture.NewTask("doubled", "Not #@12 either."));
        await _fixture.CreateAsync(LocalBoardFixture.NewTask("other file", "A file ref.") with
        {
            Refs = [new Fravashi.Domain.WorkItems.AgentLayer.ArtifactRef(
                "ref-3", Fravashi.Domain.WorkItems.AgentLayer.ArtifactRefRole.Context, Fravashi.Domain.WorkItems.AgentLayer.ArtifactRefKind.File, "12")],
        });

        var found = (await Board.QueryAsync(new WorkItemQuery { NamesWikiDocument = 12, Limit = 500 })).Items.Select(i => i.Number);

        found.Should().BeEquivalentTo([planned.Number, designed.Number, middle.Number, start.Number, end.Number]);
    }

    [Fact]
    public async Task Keyset_paging_returns_every_item_exactly_once_in_query_order()
    {
        var statuses = new[] { WorkItemStatus.Ready, WorkItemStatus.Triage, WorkItemStatus.InProgress };
        for (var i = 0; i < 23; i++)
        {
            await _fixture.TaskAsync("item " + i.ToString(System.Globalization.CultureInfo.InvariantCulture), statuses[i % statuses.Length]);
        }

        // Two items sharing a rank in one column: number breaks the tie, so the cursor still moves strictly forward.
        _fixture.Execute("UPDATE work_items SET rank = 3.0 WHERE number IN (1, 4);");
        var expected = (await Board.QueryAsync(new WorkItemQuery { Limit = 500 })).Items.Select(i => i.Id).ToList();

        var seen = new List<WorkItemId>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await Board.QueryAsync(new WorkItemQuery { Limit = 5, Cursor = cursor });
            page.Items.Count.Should().BeLessThanOrEqualTo(5);
            seen.AddRange(page.Items.Select(i => i.Id));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null);

        expected.Should().HaveCount(23);
        seen.Should().Equal(expected);
        pages.Should().Be(5);
    }

    // -------------------------------------------------------------------------------------------------
    // Mirrored shapes (plan section 3.3)
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_mirrored_row_reads_back_unchanged_and_an_authored_title_update_does_not_rejudge_it()
    {
        // An ADO Removed item as Cancelled with no resolution, no description, and a Blocked flag with no reason:
        // every authoring rule would refuse it, and the invariants admit it.
        var id = _fixture.SeedRaw(1, status: "cancelled", description: "", blocked: true, blockedReason: null);

        var read = await Board.GetAsync(id);
        read.Should().NotBeNull();
        read!.Status.Should().Be(WorkItemStatus.Cancelled);
        read.Resolution.Should().BeNull();
        read.Description.Should().BeEmpty();
        read.Flags.Blocked.Should().BeTrue();
        read.Flags.BlockedReason.Should().BeNull();

        var renamed = await Board.UpdateAsync(id, new WorkItemPatch { Title = "Renamed" }, read.Version, MutationContext.User);

        renamed.Outcome.Should().Be(TrackerOutcome.Ok, string.Join("; ", renamed.Errors.Select(e => e.Code)));
        var after = await Board.GetAsync(id);
        after!.Title.Should().Be("Renamed");
        after.Resolution.Should().BeNull();
        after.Description.Should().BeEmpty();
        after.Flags.Blocked.Should().BeTrue();
    }
}
