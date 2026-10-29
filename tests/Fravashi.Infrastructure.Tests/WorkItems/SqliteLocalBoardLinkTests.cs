using AwesomeAssertions;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Xunit;

namespace Fravashi.Infrastructure.Tests.WorkItems;

/// <summary>
/// Links through the port against the real store (canonical artifact model plan section 4): one stored edge per
/// relationship read from both ends, the authoring refusals leaving storage unchanged, and versions and events on
/// both items.
/// </summary>
public sealed class SqliteLocalBoardLinkTests : IDisposable
{
    private readonly LocalBoardFixture _fixture = new();

    private ITracker Board => _fixture.Board;

    public void Dispose() => _fixture.Dispose();

    private long EdgeCount => _fixture.Count("SELECT COUNT(*) FROM work_item_links;");

    [Fact]
    public async Task Both_readings_of_a_blocking_link_are_one_stored_edge_read_symmetrically_from_both_items()
    {
        var blocker = await _fixture.TaskAsync("blocker");
        var blocked = await _fixture.TaskAsync("blocked");

        var viaBlockedBy = await Board.LinkAsync(blocked.Id, WorkItemLinkType.BlockedBy, blocker.Id, LocalBoardFixture.User);
        var viaBlocks = await Board.LinkAsync(blocker.Id, WorkItemLinkType.Blocks, blocked.Id, LocalBoardFixture.User);

        viaBlockedBy.Value.Should().Be(viaBlocks.Value);
        EdgeCount.Should().Be(1, "linking the same edge twice, in either reading, is one row");
        _fixture.Count("SELECT COUNT(*) FROM work_item_links WHERE source_id = (SELECT id FROM work_items WHERE title = 'blocker') AND type = 'blocks';").Should().Be(1);

        var fromBlocker = (await Board.LinksAsync(blocker.Id)).Should().ContainSingle().Subject;
        var fromBlocked = (await Board.LinksAsync(blocked.Id)).Should().ContainSingle().Subject;
        fromBlocker.Should().Be(fromBlocked);
        fromBlocker.ReadingFrom(blocker.Id).Should().Be(WorkItemLinkType.Blocks);
        fromBlocked.ReadingFrom(blocked.Id).Should().Be(WorkItemLinkType.BlockedBy);

        var blockedBy = await Board.QueryAsync(new WorkItemQuery { LinkedTo = new LinkedToFilter(blocked.Id, WorkItemLinkType.BlockedBy) });
        var blocks = await Board.QueryAsync(new WorkItemQuery { LinkedTo = new LinkedToFilter(blocker.Id, WorkItemLinkType.Blocks) });
        blockedBy.Items.Select(i => i.Id).Should().Equal(blocker.Id);
        blocks.Items.Select(i => i.Id).Should().Equal(blocked.Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unlinking_either_reading_removes_the_edge_from_both_items(bool fromTheBlockedEnd)
    {
        var blocker = await _fixture.TaskAsync("blocker");
        var blocked = await _fixture.TaskAsync("blocked");
        await Board.LinkAsync(blocker.Id, WorkItemLinkType.Blocks, blocked.Id, LocalBoardFixture.User);

        var removed = fromTheBlockedEnd
            ? await Board.UnlinkAsync(blocked.Id, WorkItemLinkType.BlockedBy, blocker.Id, LocalBoardFixture.User)
            : await Board.UnlinkAsync(blocker.Id, WorkItemLinkType.Blocks, blocked.Id, LocalBoardFixture.User);

        removed.Value.Should().BeTrue();
        EdgeCount.Should().Be(0);
        (await Board.LinksAsync(blocker.Id)).Should().BeEmpty();
        (await Board.LinksAsync(blocked.Id)).Should().BeEmpty();
        (await Board.UnlinkAsync(blocker.Id, WorkItemLinkType.Blocks, blocked.Id, LocalBoardFixture.User)).Value.Should().BeFalse("there is nothing left to remove");
    }

    [Fact]
    public async Task Relates_is_stored_once_whichever_end_states_it()
    {
        var a = await _fixture.TaskAsync("a");
        var b = await _fixture.TaskAsync("b");

        await Board.LinkAsync(a.Id, WorkItemLinkType.Relates, b.Id, LocalBoardFixture.User);
        await Board.LinkAsync(b.Id, WorkItemLinkType.Relates, a.Id, LocalBoardFixture.User);

        EdgeCount.Should().Be(1);
        (await Board.UnlinkAsync(b.Id, WorkItemLinkType.Relates, a.Id, LocalBoardFixture.User)).Value.Should().BeTrue();
        EdgeCount.Should().Be(0);
    }

    [Fact]
    public async Task A_direct_blocking_cycle_is_refused_and_leaves_storage_unchanged()
    {
        var a = await _fixture.TaskAsync("a");
        var b = await _fixture.TaskAsync("b");
        await Board.LinkAsync(a.Id, WorkItemLinkType.Blocks, b.Id, LocalBoardFixture.User);
        var before = _fixture.StorageSnapshot();

        var refused = await Board.LinkAsync(a.Id, WorkItemLinkType.BlockedBy, b.Id, new MutationContext(WorkItemActor.User, "op-cycle"));

        refused.Outcome.Should().Be(TrackerOutcome.Invalid);
        refused.Errors.Should().ContainSingle().Which.Code.Should().Be(WorkItemRules.Codes.BlockedByCycle);
        _fixture.StorageSnapshot().Should().Be(before, "no edge, no version bump, no event and no ledger row");
    }

    [Fact]
    public async Task A_cycle_through_three_items_is_refused_naming_the_items_and_leaves_storage_unchanged()
    {
        var a = await _fixture.TaskAsync("a");
        var b = await _fixture.TaskAsync("b");
        var c = await _fixture.TaskAsync("c");
        await Board.LinkAsync(a.Id, WorkItemLinkType.Blocks, b.Id, LocalBoardFixture.User);
        await Board.LinkAsync(b.Id, WorkItemLinkType.Blocks, c.Id, LocalBoardFixture.User);
        var before = _fixture.StorageSnapshot();

        var refused = await Board.LinkAsync(c.Id, WorkItemLinkType.Blocks, a.Id, LocalBoardFixture.User);

        var error = refused.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(WorkItemRules.Codes.BlockedByCycle);
        error.Message.Should().Contain("#3 -> #1 -> #2 -> #3");
        _fixture.StorageSnapshot().Should().Be(before);

        // The same three items in a line are fine: a diamond or a chain is not a cycle.
        (await Board.LinkAsync(a.Id, WorkItemLinkType.Blocks, c.Id, LocalBoardFixture.User)).IsOk.Should().BeTrue();
    }

    [Fact]
    public async Task A_supersedes_cycle_is_refused()
    {
        var a = await _fixture.TaskAsync("a");
        var b = await _fixture.TaskAsync("b");
        await Board.LinkAsync(a.Id, WorkItemLinkType.Supersedes, b.Id, LocalBoardFixture.User);
        var before = _fixture.StorageSnapshot();

        var refused = await Board.LinkAsync(a.Id, WorkItemLinkType.SupersededBy, b.Id, LocalBoardFixture.User);

        refused.Errors.Select(e => e.Code).Should().Equal(WorkItemRules.Codes.SupersedesCycle);
        _fixture.StorageSnapshot().Should().Be(before);
    }

    [Fact]
    public async Task A_cross_project_link_and_a_self_link_are_refused()
    {
        _fixture.CreateProject("other");
        var mine = await _fixture.TaskAsync("mine");
        var theirs = await _fixture.CreateAsync(LocalBoardFixture.NewTask("theirs"), _fixture.Resolver.For(new ProjectId("other")));
        var before = _fixture.StorageSnapshot();

        (await Board.LinkAsync(mine.Id, WorkItemLinkType.Relates, theirs.Id, LocalBoardFixture.User)).Errors.Select(e => e.Code)
            .Should().Equal(WorkItemRules.Codes.LinkOtherProject);
        (await Board.LinkAsync(mine.Id, WorkItemLinkType.Blocks, mine.Id, LocalBoardFixture.User)).Errors.Select(e => e.Code)
            .Should().Equal(WorkItemRules.Codes.LinkSelf);
        (await Board.LinkAsync(theirs.Id, WorkItemLinkType.Relates, mine.Id, LocalBoardFixture.User)).Outcome
            .Should().Be(TrackerOutcome.NotFound, "the source must be an item of the board's own project");
        _fixture.StorageSnapshot().Should().Be(before);
    }

    [Fact]
    public async Task A_chained_duplicate_is_refused_naming_the_original()
    {
        var original = await _fixture.TaskAsync("original");
        var duplicate = await _fixture.TaskAsync("duplicate");
        var third = await _fixture.TaskAsync("third");
        await Board.LinkAsync(duplicate.Id, WorkItemLinkType.Duplicates, original.Id, LocalBoardFixture.User);
        var current = await _fixture.ReloadAsync(duplicate);
        (await Board.TransitionAsync(duplicate.Id, WorkItemStatus.Cancelled, WorkItemResolution.Duplicate, null, null, current.Version, LocalBoardFixture.User))
            .IsOk.Should().BeTrue();
        var before = _fixture.StorageSnapshot();

        var refused = await Board.LinkAsync(third.Id, WorkItemLinkType.Duplicates, duplicate.Id, LocalBoardFixture.User);

        var error = refused.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(WorkItemRules.Codes.DuplicateChained);
        error.Message.Should().Contain("#1");
        _fixture.StorageSnapshot().Should().Be(before);
    }

    [Fact]
    public async Task Unlinking_the_only_duplicates_edge_of_an_item_resolved_duplicate_is_refused()
    {
        var original = await _fixture.TaskAsync("original");
        var duplicate = await _fixture.TaskAsync("duplicate");
        await Board.LinkAsync(duplicate.Id, WorkItemLinkType.Duplicates, original.Id, LocalBoardFixture.User);
        var current = await _fixture.ReloadAsync(duplicate);
        await Board.TransitionAsync(duplicate.Id, WorkItemStatus.Cancelled, WorkItemResolution.Duplicate, null, null, current.Version, LocalBoardFixture.User);
        var before = _fixture.StorageSnapshot();

        var refused = await Board.UnlinkAsync(original.Id, WorkItemLinkType.DuplicatedBy, duplicate.Id, LocalBoardFixture.User);

        refused.Errors.Select(e => e.Code).Should().Equal(WorkItemRules.Codes.DuplicateLinkRequired);
        _fixture.StorageSnapshot().Should().Be(before);
    }

    [Fact]
    public async Task A_link_bumps_both_versions_without_taking_one_and_writes_one_event_on_each_item()
    {
        var a = await _fixture.TaskAsync("a");
        var b = await _fixture.TaskAsync("b");
        await Board.UpdateAsync(b.Id, new WorkItemPatch { Title = "b2" }, b.Version, LocalBoardFixture.User);

        var linked = await Board.LinkAsync(a.Id, WorkItemLinkType.Blocks, b.Id, new MutationContext(WorkItemActor.Session("s-9"), "op-link"));

        linked.IsOk.Should().BeTrue();
        (await _fixture.ReloadAsync(a)).Version.Should().Be(2);
        (await _fixture.ReloadAsync(b)).Version.Should().Be(3);
        var aEvents = await Board.HistoryAsync(a.Id);
        var bEvents = await Board.HistoryAsync(b.Id);
        aEvents[^1].Operation.Should().Be(WorkItemEventOperation.Linked);
        bEvents[^1].Operation.Should().Be(WorkItemEventOperation.Linked);
        aEvents[^1].OperationId.Should().Be("op-link");
        bEvents[^1].OperationId.Should().Be("op-link");
        aEvents[^1].ItemVersion.Should().Be(2);
        bEvents[^1].ItemVersion.Should().Be(3);
        aEvents[^1].Actor.Should().Be(WorkItemActor.Session("s-9"));
        _fixture.Count("SELECT COUNT(*) FROM work_item_operations WHERE operation_id = 'op-link';").Should().Be(1, "one ledger row for the operation, however many items it touches");
        _fixture.Count("SELECT COUNT(*) FROM work_item_links WHERE created_by = 'session:s-9';").Should().Be(1);

        var before = _fixture.StorageSnapshot();
        var replay = await Board.LinkAsync(b.Id, WorkItemLinkType.BlockedBy, a.Id, new MutationContext(WorkItemActor.Session("s-9"), "op-link"));
        replay.Replayed.Should().BeTrue();
        replay.Value.Should().Be(linked.Value);
        _fixture.StorageSnapshot().Should().Be(before, "the replay writes nothing");

        (await Board.UnlinkAsync(a.Id, WorkItemLinkType.Blocks, b.Id, new MutationContext(WorkItemActor.User, "op-link"))).Errors.Select(e => e.Code)
            .Should().Equal(TrackerCodes.OperationIdReused);
    }

    [Fact]
    public async Task The_cycle_check_terminates_on_a_mirrored_blocks_cycle_seeded_straight_into_the_table()
    {
        var x = _fixture.SeedRaw(1, title: "x", description: "d");
        var y = _fixture.SeedRaw(2, title: "y", description: "d");
        _fixture.SeedEdge(x, "blocks", y);
        _fixture.SeedEdge(y, "blocks", x);
        var z = await _fixture.TaskAsync("z");

        var intoTheCycle = await Board.LinkAsync(z.Id, WorkItemLinkType.BlockedBy, x, LocalBoardFixture.User);
        var outOfTheCycle = await Board.LinkAsync(y, WorkItemLinkType.BlockedBy, z.Id, LocalBoardFixture.User);

        intoTheCycle.IsOk.Should().BeTrue("x blocking z closes nothing, even though x and y already block each other");
        outOfTheCycle.Errors.Select(e => e.Code).Should().Equal(WorkItemRules.Codes.BlockedByCycle);
        (await Board.LinksAsync(x)).Should().HaveCount(3);
    }
}
