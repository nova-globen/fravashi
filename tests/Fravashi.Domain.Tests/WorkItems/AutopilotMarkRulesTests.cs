using AwesomeAssertions;
using Fravashi.Domain.WorkItems;
using Xunit;
using static Fravashi.Domain.Tests.WorkItems.WorkItemFixture;

namespace Fravashi.Domain.Tests.WorkItems;

/// <summary>
/// ADR 0031 decision 1 (backlog 298): a person marks an item for autopilot only while it is Ready, a mark
/// may be removed from any status, and the mark does not survive the item reaching a terminal status.
/// </summary>
public class AutopilotMarkRulesTests
{
    [Fact]
    public void Marking_a_ready_item_is_accepted()
    {
        var item = Item(status: WorkItemStatus.Ready);
        var proposed = item.ToState() with { MarkedForAutopilot = true };

        var updated = WorkItemRules.ValidateUpdate(item, proposed, null, []).ShouldBeValid();

        updated.MarkedForAutopilot.Should().BeTrue();
    }

    [Theory]
    [InlineData(WorkItemStatus.Triage)]
    [InlineData(WorkItemStatus.Backlog)]
    [InlineData(WorkItemStatus.InProgress)]
    [InlineData(WorkItemStatus.InReview)]
    public void Marking_a_non_ready_open_item_is_refused(WorkItemStatus status)
    {
        var item = Item(status: status);
        var proposed = item.ToState() with { MarkedForAutopilot = true };

        WorkItemRules.ValidateUpdate(item, proposed, null, [])
            .ShouldBeRefusedWith(WorkItemRules.Codes.AutopilotMarkRequiresReady, "markedForAutopilot");
    }

    [Theory]
    [InlineData(WorkItemStatus.Triage)]
    [InlineData(WorkItemStatus.Backlog)]
    [InlineData(WorkItemStatus.Ready)]
    [InlineData(WorkItemStatus.InProgress)]
    [InlineData(WorkItemStatus.InReview)]
    public void Unmarking_is_accepted_from_every_open_status(WorkItemStatus status)
    {
        var item = Item(status: status);
        var marked = item.ToState() with { MarkedForAutopilot = true };
        var alreadyMarked = Restored(marked);

        var proposed = alreadyMarked.ToState() with { MarkedForAutopilot = false };
        var updated = WorkItemRules.ValidateUpdate(alreadyMarked, proposed, null, []).ShouldBeValid();

        updated.MarkedForAutopilot.Should().BeFalse();
    }

    [Fact]
    public void Marking_the_same_value_twice_is_idempotent()
    {
        var item = Item(status: WorkItemStatus.Ready);
        var marked = Restored(item.ToState() with { MarkedForAutopilot = true });

        var proposed = marked.ToState() with { MarkedForAutopilot = true };
        WorkItemRules.ValidateUpdate(marked, proposed, null, []).ShouldBeValid().MarkedForAutopilot.Should().BeTrue();
    }

    [Fact]
    public void The_mark_survives_an_open_to_open_transition()
    {
        // The acceptance's other half: "a mark survives until the item is done or unmarked", so a move
        // that lands nowhere terminal (the engine's own ready -> in_progress -> in_review path) must not
        // clear it the way landing on Done or Cancelled does.
        var marked = Restored(Item(status: WorkItemStatus.Ready).ToState() with { MarkedForAutopilot = true });

        var inProgress = WorkItemRules.ApplyTransition(marked, WorkItemStatus.InProgress, null, []).ShouldBeValid();
        inProgress.MarkedForAutopilot.Should().BeTrue();

        var inReview = WorkItemRules.ApplyTransition(inProgress, WorkItemStatus.InReview, null, []).ShouldBeValid();
        inReview.MarkedForAutopilot.Should().BeTrue();
    }

    [Theory]
    [InlineData(WorkItemStatus.Done, null)]
    [InlineData(WorkItemStatus.Cancelled, WorkItemResolution.WontDo)]
    public void Entering_a_terminal_status_clears_the_mark(WorkItemStatus to, WorkItemResolution? resolution)
    {
        var marked = Restored(Item(status: WorkItemStatus.InProgress).ToState() with { MarkedForAutopilot = true });

        var closed = WorkItemRules.ApplyTransition(marked, to, resolution, []).ShouldBeValid();

        closed.MarkedForAutopilot.Should().BeFalse();
    }
}
