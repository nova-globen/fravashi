using AwesomeAssertions;
using Fravashi.Domain.Autopilot;
using Fravashi.Domain.WorkItems;
using Xunit;
using static Fravashi.Domain.Tests.WorkItems.WorkItemFixture;

namespace Fravashi.Domain.Tests.Autopilot;

/// <summary>
/// ADR 0031 decision 1: "Only Ready items a person has MARKED for autopilot, one at a time in board order.
/// Nothing it was not handed."
/// </summary>
public sealed class AutopilotSelectionTests
{
    private static WorkItem Marked(WorkItemStatus status, double rank, int number) =>
        Restored(State(status: status) with { MarkedForAutopilot = true, Rank = rank, Number = number });

    private static WorkItem Unmarked(WorkItemStatus status, double rank, int number) =>
        Restored(State(status: status) with { MarkedForAutopilot = false, Rank = rank, Number = number });

    [Fact]
    public void Selects_only_marked_ready_items()
    {
        var markedReady = Marked(WorkItemStatus.Ready, 1, 1);
        var readyNotMarked = Unmarked(WorkItemStatus.Ready, 2, 2);
        var markedInProgress = Marked(WorkItemStatus.InProgress, 3, 3);

        var selected = AutopilotSelection.MarkedReadyInBoardOrder([markedReady, readyNotMarked, markedInProgress]);

        selected.Should().ContainSingle().Which.Should().Be(markedReady);
    }

    [Fact]
    public void Orders_by_rank_then_number()
    {
        var third = Marked(WorkItemStatus.Ready, 3, 1);
        var first = Marked(WorkItemStatus.Ready, 1, 5);
        var secondByRankTieBrokenByNumber = Marked(WorkItemStatus.Ready, 1, 2);

        var selected = AutopilotSelection.MarkedReadyInBoardOrder([third, first, secondByRankTieBrokenByNumber]);

        selected.Should().Equal(secondByRankTieBrokenByNumber, first, third);
    }

    [Fact]
    public void Nothing_marked_selects_nothing()
    {
        var items = new[] { Unmarked(WorkItemStatus.Ready, 1, 1), Unmarked(WorkItemStatus.Backlog, 2, 2) };

        AutopilotSelection.MarkedReadyInBoardOrder(items).Should().BeEmpty();
    }

    // ---- ParkedInBoardOrder (ADR 0031 decision 12, #305: resuming a parked item in a later run) -----------------

    private static AutopilotRunItem ParkedQueueEntry(WorkItem item, int order) =>
        new(item.Id, order, AutopilotItemStage.Parked, 0m, 0, null, null, "needs a decision");

    [Fact]
    public void Selects_an_item_the_last_run_parked_that_is_still_marked_and_still_in_progress()
    {
        var item = Marked(WorkItemStatus.InProgress, 1, 1);

        var selected = AutopilotSelection.ParkedInBoardOrder([item], [ParkedQueueEntry(item, 0)]);

        selected.Should().ContainSingle().Which.Should().Be(item);
    }

    [Fact]
    public void Excludes_an_item_a_person_unmarked()
    {
        var item = Unmarked(WorkItemStatus.InProgress, 1, 1);

        AutopilotSelection.ParkedInBoardOrder([item], [ParkedQueueEntry(item, 0)]).Should().BeEmpty();
    }

    [Fact]
    public void Excludes_an_item_whose_status_moved_away_from_in_progress()
    {
        var item = Marked(WorkItemStatus.InReview, 1, 1);

        AutopilotSelection.ParkedInBoardOrder([item], [ParkedQueueEntry(item, 0)]).Should().BeEmpty();
    }

    [Fact]
    public void Excludes_an_item_the_last_run_did_not_park()
    {
        var parked = Marked(WorkItemStatus.InProgress, 1, 1);
        var neverRan = Marked(WorkItemStatus.InProgress, 2, 2);
        var queue = new[] { ParkedQueueEntry(parked, 0), new AutopilotRunItem(neverRan.Id, 1, AutopilotItemStage.Done, 0m, 0, null, "sha", null) };

        var selected = AutopilotSelection.ParkedInBoardOrder([parked, neverRan], queue);

        selected.Should().ContainSingle().Which.Should().Be(parked);
    }

    [Fact]
    public void Orders_resumed_items_by_rank_then_number()
    {
        var third = Marked(WorkItemStatus.InProgress, 3, 1);
        var first = Marked(WorkItemStatus.InProgress, 1, 5);
        var secondByRankTieBrokenByNumber = Marked(WorkItemStatus.InProgress, 1, 2);
        var queue = new[]
        {
            ParkedQueueEntry(third, 0), ParkedQueueEntry(first, 1), ParkedQueueEntry(secondByRankTieBrokenByNumber, 2),
        };

        var selected = AutopilotSelection.ParkedInBoardOrder([third, first, secondByRankTieBrokenByNumber], queue);

        selected.Should().Equal(secondByRankTieBrokenByNumber, first, third);
    }

    [Fact]
    public void A_project_that_has_never_run_selects_nothing()
    {
        var item = Marked(WorkItemStatus.InProgress, 1, 1);

        AutopilotSelection.ParkedInBoardOrder([item], null).Should().BeEmpty();
    }

    /// <summary>
    /// A resumed item (#305 point 3) can itself be stranded: its OWN resuming run can stop (a run limit,
    /// "stop now", a restart that cannot resume) while an EARLIER sibling in that same queue is still open
    /// and current, before this item is ever reached. It is left Queued with StartedUtc still null, never
    /// named Parked by any run, so without reading this shape too it would be marked, In Progress, and
    /// permanently invisible to every later dispatch.
    /// </summary>
    [Fact]
    public void Selects_an_item_left_queued_and_untouched_because_an_earlier_sibling_was_still_current_when_the_run_stopped()
    {
        var blockedBy = Marked(WorkItemStatus.InProgress, 1, 1);
        var neverReached = Marked(WorkItemStatus.InProgress, 2, 2);
        var queue = new AutopilotRunItem[]
        {
            new(blockedBy.Id, 0, AutopilotItemStage.Building, 10m, 0, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null, null),
            AutopilotRunItem.Queue(neverReached.Id, 1),
        };

        var selected = AutopilotSelection.ParkedInBoardOrder([blockedBy, neverReached], queue);

        selected.Should().ContainSingle().Which.Should().Be(neverReached);
    }

    [Fact]
    public void An_item_that_actually_started_is_not_read_as_merely_queued_and_untouched()
    {
        var item = Marked(WorkItemStatus.InProgress, 1, 1);
        var queue = new[]
        {
            new AutopilotRunItem(item.Id, 0, AutopilotItemStage.Building, 0m, 0, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null, null),
        };

        AutopilotSelection.ParkedInBoardOrder([item], queue).Should().BeEmpty("StartedUtc is set, so this item was reached, not stranded");
    }
}
