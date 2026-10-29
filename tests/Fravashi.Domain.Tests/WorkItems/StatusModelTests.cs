using AwesomeAssertions;
using Fravashi.Domain.WorkItems;
using Xunit;
using static Fravashi.Domain.Tests.WorkItems.WorkItemFixture;

namespace Fravashi.Domain.Tests.WorkItems;

/// <summary>Plan section 1.3 and vision 5.2: seven statuses over four categories.</summary>
public class StatusModelTests
{
    [Theory]
    [InlineData(WorkItemStatus.Triage, StatusCategory.Proposed)]
    [InlineData(WorkItemStatus.Backlog, StatusCategory.Proposed)]
    [InlineData(WorkItemStatus.Ready, StatusCategory.Proposed)]
    [InlineData(WorkItemStatus.InProgress, StatusCategory.InProgress)]
    [InlineData(WorkItemStatus.InReview, StatusCategory.InProgress)]
    [InlineData(WorkItemStatus.Done, StatusCategory.Done)]
    [InlineData(WorkItemStatus.Cancelled, StatusCategory.Cancelled)]
    public void Each_status_maps_to_its_category(WorkItemStatus status, StatusCategory category)
    {
        status.Category().Should().Be(category);
    }

    [Fact]
    public void There_are_exactly_seven_statuses_and_four_categories()
    {
        Enum.GetValues<WorkItemStatus>().Should().HaveCount(7).And.BeEquivalentTo(WorkItemStatuses.All);
        Enum.GetValues<StatusCategory>().Should().HaveCount(4).And.BeEquivalentTo(StatusCategories.All);
        WorkItemStatuses.All.Select(s => s.Category()).Distinct().Should().HaveCount(4);
    }

    [Theory]
    [InlineData(StatusCategory.Proposed, WorkItemStatus.Backlog)]
    [InlineData(StatusCategory.InProgress, WorkItemStatus.InProgress)]
    [InlineData(StatusCategory.Done, WorkItemStatus.Done)]
    [InlineData(StatusCategory.Cancelled, WorkItemStatus.Cancelled)]
    public void Each_category_has_a_default_status_inside_it(StatusCategory category, WorkItemStatus expected)
    {
        category.DefaultStatus().Should().Be(expected);
        category.DefaultStatus().Category().Should().Be(category);
    }

    [Fact]
    public void Only_done_and_cancelled_are_terminal()
    {
        WorkItemStatuses.All.Where(s => s.IsTerminal()).Should().BeEquivalentTo([WorkItemStatus.Done, WorkItemStatus.Cancelled]);
        WorkItemStatuses.All.Where(s => s.IsOpen()).Should().HaveCount(5);
    }

    [Fact]
    public void The_category_is_derived_from_the_status_on_the_aggregate()
    {
        var item = Item(status: WorkItemStatus.InReview);

        item.Category.Should().Be(StatusCategory.InProgress);
        typeof(WorkItemState).GetProperty("Category").Should().BeNull("the category is never stored");
    }

    [Fact]
    public void Board_order_is_the_order_of_section_1_3()
    {
        WorkItemStatuses.All.Should().ContainInOrder(
            WorkItemStatus.Triage, WorkItemStatus.Backlog, WorkItemStatus.Ready, WorkItemStatus.InProgress,
            WorkItemStatus.InReview, WorkItemStatus.Done, WorkItemStatus.Cancelled);
    }

    [Fact]
    public void Deferred_is_not_a_resolution()
    {
        Enum.GetNames<WorkItemResolution>().Should().BeEquivalentTo(["Completed", "WontDo", "Duplicate", "CannotReproduce"]);
    }

    [Fact]
    public void An_undefined_status_is_refused_as_an_invariant()
    {
        WorkItem.Restore(State(status: (WorkItemStatus)99)).ShouldBeRefusedWith(WorkItemRules.Codes.ValueUndefined, "status");
    }
}
