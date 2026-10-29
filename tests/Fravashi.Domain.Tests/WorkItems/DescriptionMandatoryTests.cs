using AwesomeAssertions;
using Fravashi.Domain.WorkItems;
using Xunit;
using static Fravashi.Domain.Tests.WorkItems.WorkItemFixture;

namespace Fravashi.Domain.Tests.WorkItems;

/// <summary>Plan section 1.5: the description is mandatory when authored, the title on every item.</summary>
public class DescriptionMandatoryTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public void An_empty_or_whitespace_description_is_refused_at_creation(string description)
    {
        WorkItemRules.ValidateNew(State(description: description))
            .ShouldBeRefusedWith(WorkItemRules.Codes.DescriptionRequired, "description");
    }

    [Fact]
    public void A_missing_description_is_refused_at_creation()
    {
        var state = new WorkItemState
        {
            Id = WorkItemId.New(),
            Project = Projects.ProjectId.Default,
            Number = 1,
            Level = WorkItemLevel.Task,
            Title = "No description given",
        };

        WorkItemRules.ValidateNew(state).ShouldBeRefusedWith(WorkItemRules.Codes.DescriptionRequired, "description");
        WorkItemRules.ValidateNew(state with { Description = null! }).ShouldBeRefusedWith(WorkItemRules.Codes.DescriptionRequired, "description");
    }

    [Fact]
    public void A_create_with_a_description_is_accepted()
    {
        WorkItemRules.ValidateNew(State(description: "Users cannot find the export; add it to the menu.")).ShouldBeValid();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_update_that_sets_the_description_to_empty_is_refused(string description)
    {
        var item = Item();

        WorkItemRules.ValidateUpdate(item, item.ToState() with { Description = description }, null, [])
            .ShouldBeRefusedWith(WorkItemRules.Codes.DescriptionRequired, "description");
    }

    [Fact]
    public void An_update_that_does_not_touch_the_description_is_accepted_on_an_item_that_has_none()
    {
        // Jira and ADO both allow an item without a description; its gap is item 85's readiness concern.
        var mirrored = Item(description: string.Empty);

        var updated = WorkItemRules.ValidateUpdate(mirrored, mirrored.ToState() with { Title = "Clearer title", Assignee = "sam" }, null, [])
            .ShouldBeValid();

        updated.Title.Should().Be("Clearer title");
        updated.Description.Should().BeEmpty();
    }

    [Fact]
    public void An_update_can_give_a_mirrored_item_its_first_description()
    {
        var mirrored = Item(description: string.Empty);

        WorkItemRules.ValidateUpdate(mirrored, mirrored.ToState() with { Description = "Now described." }, null, [])
            .ShouldBeValid().Description.Should().Be("Now described.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void An_empty_title_is_refused_on_every_path(string title)
    {
        var item = Item();
        var mirrored = Item(description: string.Empty);

        WorkItem.Restore(State(title: title)).ShouldBeRefusedWith(WorkItemRules.Codes.TitleRequired, "title");
        WorkItemRules.CheckInvariants(State(title: title)).ShouldBeRefusedWith(WorkItemRules.Codes.TitleRequired, "title");
        WorkItemRules.ValidateNew(State(title: title)).ShouldBeRefusedWith(WorkItemRules.Codes.TitleRequired, "title");
        WorkItemRules.ValidateUpdate(item, item.ToState() with { Title = title }, null, [])
            .ShouldBeRefusedWith(WorkItemRules.Codes.TitleRequired, "title");
        WorkItemRules.ValidateUpdate(mirrored, mirrored.ToState() with { Title = title }, null, [])
            .ShouldBeRefusedWith(WorkItemRules.Codes.TitleRequired, "title");
    }

    [Fact]
    public void A_create_with_both_missing_reports_both_fields()
    {
        var result = WorkItemRules.ValidateNew(State(title: " ", description: " "));

        result.Errors.Select(e => e.Field).Should().Contain(["title", "description"]);
    }
}
