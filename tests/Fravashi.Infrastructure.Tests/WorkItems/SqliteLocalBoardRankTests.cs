using AwesomeAssertions;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.WorkItems;
using Xunit;

namespace Fravashi.Infrastructure.Tests.WorkItems;

/// <summary>Board order within a status column (canonical artifact model plan section 5.3, decision D14).</summary>
public sealed class SqliteLocalBoardRankTests : IDisposable
{
    private readonly LocalBoardFixture _fixture = new();

    private ITracker Board => _fixture.Board;

    public void Dispose() => _fixture.Dispose();

    private async Task<IReadOnlyList<string>> ColumnAsync(WorkItemStatus status) =>
        (await Board.QueryAsync(new WorkItemQuery { Statuses = [status] })).Items.Select(i => i.Title).ToList();

    private async Task<(WorkItem A, WorkItem B, WorkItem C)> ThreeAsync()
    {
        var a = await _fixture.TaskAsync("a");
        var b = await _fixture.TaskAsync("b");
        var c = await _fixture.TaskAsync("c");
        return (a, b, c);
    }

    [Fact]
    public async Task A_create_lands_at_the_bottom_of_its_column()
    {
        var (a, b, c) = await ThreeAsync();
        var ready = await _fixture.TaskAsync("ready", WorkItemStatus.Ready);

        new[] { a.Rank, b.Rank, c.Rank }.Should().Equal(1.0, 2.0, 3.0);
        ready.Rank.Should().Be(1.0, "an empty column starts at 1.0");
        (await ColumnAsync(WorkItemStatus.Triage)).Should().Equal("a", "b", "c");
    }

    [Fact]
    public async Task A_move_after_an_anchor_places_the_item_immediately_after_it()
    {
        var (a, _, c) = await ThreeAsync();

        var moved = await Board.MoveAsync(c.Id, a.Id, null, c.Version, LocalBoardFixture.User);

        moved.Outcome.Should().Be(TrackerOutcome.Ok);
        moved.Value.Rank.Should().Be(1.5);
        moved.Value.Version.Should().Be(c.Version + 1);
        (await ColumnAsync(WorkItemStatus.Triage)).Should().Equal("a", "c", "b");
    }

    [Fact]
    public async Task A_move_before_an_anchor_places_the_item_immediately_before_it_and_at_either_end()
    {
        var (a, b, c) = await ThreeAsync();

        (await Board.MoveAsync(c.Id, null, b.Id, c.Version, LocalBoardFixture.User)).IsOk.Should().BeTrue();
        (await ColumnAsync(WorkItemStatus.Triage)).Should().Equal("a", "c", "b");

        var top = await Board.MoveAsync(b.Id, null, a.Id, b.Version, LocalBoardFixture.User);
        top.Value.Rank.Should().Be(0.0, "before the first card is its rank minus one");
        (await ColumnAsync(WorkItemStatus.Triage)).Should().Equal("b", "a", "c");

        var bottom = await Board.MoveAsync(a.Id, (await _fixture.ReloadAsync(c)).Id, null, a.Version, LocalBoardFixture.User);
        bottom.IsOk.Should().BeTrue();
        (await ColumnAsync(WorkItemStatus.Triage)).Should().Equal("b", "c", "a");
    }

    [Fact]
    public async Task With_both_anchors_after_wins_even_when_a_card_has_since_been_placed_between_them()
    {
        var (a, b, c) = await ThreeAsync();
        var d = await _fixture.TaskAsync("d");

        // Both anchors adjacent: the item lands between them.
        (await Board.MoveAsync(d.Id, a.Id, b.Id, d.Version, LocalBoardFixture.User)).IsOk.Should().BeTrue();
        (await ColumnAsync(WorkItemStatus.Triage)).Should().Equal("a", "d", "b", "c");

        // The caller still thinks a and b are adjacent, but d now sits between them: after wins, before is a hint.
        (await Board.MoveAsync(c.Id, a.Id, b.Id, c.Version, LocalBoardFixture.User)).IsOk.Should().BeTrue();
        (await ColumnAsync(WorkItemStatus.Triage)).Should().Equal("a", "c", "d", "b");
    }

    [Fact]
    public async Task A_transition_with_no_anchor_lands_at_the_bottom_and_with_an_anchor_beside_it_in_one_event()
    {
        var first = await _fixture.TaskAsync("first", WorkItemStatus.Ready);
        var second = await _fixture.TaskAsync("second", WorkItemStatus.Ready);
        var (a, b, _) = await ThreeAsync();

        var bottom = await Board.TransitionAsync(a.Id, WorkItemStatus.Ready, null, null, null, a.Version, LocalBoardFixture.User);
        bottom.Value.Rank.Should().Be(3.0);
        (await ColumnAsync(WorkItemStatus.Ready)).Should().Equal("first", "second", "a");

        var beside = await Board.TransitionAsync(b.Id, WorkItemStatus.Ready, null, first.Id, second.Id, b.Version, LocalBoardFixture.User);
        beside.Value.Status.Should().Be(WorkItemStatus.Ready);
        beside.Value.Version.Should().Be(b.Version + 1, "one call, one version");
        (await ColumnAsync(WorkItemStatus.Ready)).Should().Equal("first", "b", "second", "a");

        var history = await Board.HistoryAsync(b.Id);
        history.Select(e => e.Operation).Should().Equal(WorkItemEventOperation.Created, WorkItemEventOperation.Transitioned);
        history[1].ChangesJson.Should().Contain("\"status\"").And.Contain("\"rank\"");
    }

    [Fact]
    public async Task An_anchor_in_another_status_or_the_item_itself_is_invalid_with_the_placement_code()
    {
        var (a, b, _) = await ThreeAsync();
        var ready = await _fixture.TaskAsync("ready", WorkItemStatus.Ready);
        var before = _fixture.StorageSnapshot();

        var otherStatus = await Board.MoveAsync(a.Id, ready.Id, null, a.Version, LocalBoardFixture.User);
        var self = await Board.MoveAsync(a.Id, null, a.Id, a.Version, LocalBoardFixture.User);
        var transitionAnchor = await Board.TransitionAsync(b.Id, WorkItemStatus.Ready, null, a.Id, null, b.Version, LocalBoardFixture.User);

        otherStatus.Errors.Should().ContainSingle().Which.Should().Match<ValidationError>(e =>
            e.Code == TrackerCodes.PlacementAnchorStatus && e.Field == "after" && e.Message.Contains("ready", StringComparison.Ordinal));
        self.Errors.Select(e => e.Code).Should().Equal(TrackerCodes.PlacementAnchorSelf);
        transitionAnchor.Errors.Select(e => e.Code).Should().Equal(TrackerCodes.PlacementAnchorStatus);
        _fixture.StorageSnapshot().Should().Be(before);
    }

    [Fact]
    public async Task A_move_with_no_anchor_is_move_anchor_required()
    {
        var a = await _fixture.TaskAsync("a");

        var refused = await Board.MoveAsync(a.Id, null, null, a.Version, LocalBoardFixture.User);

        refused.Errors.Select(e => e.Code).Should().Equal(TrackerCodes.MoveAnchorRequired);
    }

    [Fact]
    public async Task A_crowded_column_renumbers_without_changing_the_order_or_touching_any_other_item()
    {
        var (a, b, c) = await ThreeAsync();
        var d = await _fixture.TaskAsync("d");
        _fixture.Execute("UPDATE work_items SET rank = 1.0 WHERE id = $a;", ("$a", a.Id.Value));
        _fixture.Execute("UPDATE work_items SET rank = 1.0000000001 WHERE id = $b;", ("$b", b.Id.Value));
        _fixture.Execute("UPDATE work_items SET rank = 1.0000000002 WHERE id = $c;", ("$c", c.Id.Value));
        var eventsBefore = _fixture.Count("SELECT COUNT(*) FROM work_item_events;");

        var moved = await Board.MoveAsync(d.Id, a.Id, null, d.Version, LocalBoardFixture.User);

        moved.IsOk.Should().BeTrue();
        (await ColumnAsync(WorkItemStatus.Triage)).Should().Equal("a", "d", "b", "c");
        var ranks = (await Board.QueryAsync(new WorkItemQuery())).Items.Select(i => i.Rank).ToList();
        ranks.Zip(ranks.Skip(1)).Should().OnlyContain(pair => pair.Second - pair.First >= SqliteLocalBoard.RenumberGap, "the column was spaced out again");
        (await _fixture.ReloadAsync(a)).Version.Should().Be(a.Version);
        (await _fixture.ReloadAsync(b)).Version.Should().Be(b.Version);
        (await _fixture.ReloadAsync(c)).Version.Should().Be(c.Version);
        _fixture.Count("SELECT COUNT(*) FROM work_item_events;").Should().Be(eventsBefore + 1, "only the moved item gets an event");
    }
}
