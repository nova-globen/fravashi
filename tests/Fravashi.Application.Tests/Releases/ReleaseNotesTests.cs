using AwesomeAssertions;
using Fravashi.Application.Releases;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Xunit;

namespace Fravashi.Application.Tests.Releases;

/// <summary>Work item #368: the notes are drafted from the items by plain rules, the same items always giving the same draft.</summary>
public class ReleaseNotesTests
{
    private static WorkItem Item(int number, string title, WorkItemLevel level = WorkItemLevel.Task, WorkItemKind kind = WorkItemKind.Standard) =>
        WorkItem.Restore(new WorkItemState
        {
            Id = new WorkItemId("item-" + number),
            Project = ProjectId.Default,
            Number = number,
            Level = level,
            Kind = kind,
            Title = title,
            Status = WorkItemStatus.Done,
            Resolution = WorkItemResolution.Completed,
        }).Value;

    [Theory]
    [InlineData("Board drag fails", WorkItemLevel.Task, WorkItemKind.Bug, ReleaseNoteGroup.Fix)]
    [InlineData("fix: the board drops a card", WorkItemLevel.Task, WorkItemKind.Standard, ReleaseNoteGroup.Fix)]
    [InlineData("Fix(web) the toggle", WorkItemLevel.Increment, WorkItemKind.Standard, ReleaseNoteGroup.Fix)]
    [InlineData("Fixes the sync", WorkItemLevel.Feature, WorkItemKind.Standard, ReleaseNoteGroup.Fix)]
    [InlineData("Fixture data for tests", WorkItemLevel.Task, WorkItemKind.Standard, ReleaseNoteGroup.Other)]
    [InlineData("A release option on the board", WorkItemLevel.Feature, WorkItemKind.Standard, ReleaseNoteGroup.Feature)]
    [InlineData("The Done column archive", WorkItemLevel.Increment, WorkItemKind.Standard, ReleaseNoteGroup.Feature)]
    [InlineData("feat: a releases list", WorkItemLevel.Task, WorkItemKind.Standard, ReleaseNoteGroup.Feature)]
    [InlineData("Should we cache tags", WorkItemLevel.Increment, WorkItemKind.Spike, ReleaseNoteGroup.Other)]
    [InlineData("Rename a class", WorkItemLevel.Task, WorkItemKind.Standard, ReleaseNoteGroup.Other)]
    [InlineData("Platform", WorkItemLevel.Tier, WorkItemKind.Standard, ReleaseNoteGroup.Other)]
    public void Each_item_is_classified_by_its_kind_its_title_and_its_level(
        string title, WorkItemLevel level, WorkItemKind kind, ReleaseNoteGroup expected)
    {
        ReleaseNotes.Classify(Item(1, title, level, kind)).Should().Be(expected);
    }

    [Fact]
    public void The_draft_groups_features_fixes_and_other_with_one_line_per_item_by_number()
    {
        var draft = ReleaseNotes.Draft(
            "v0.9.97",
            [
                Item(368, "A release option on the board", WorkItemLevel.Feature),
                Item(12, "fix(web): the toggle  sticks", WorkItemLevel.Task),
                Item(3, "Rename a class"),
                Item(40, "Archive the Done column", WorkItemLevel.Increment),
            ]);

        draft.Should().Be(
            "# Release v0.9.97\n\n"
            + "## Features\n\n- Archive the Done column (#40)\n- A release option on the board (#368)\n\n"
            + "## Fixes\n\n- The toggle sticks (#12)\n\n"
            + "## Other changes\n\n- Rename a class (#3)\n");
    }

    [Fact]
    public void An_empty_group_is_left_out_and_no_items_says_so()
    {
        ReleaseNotes.Draft("v1.0.0", [Item(1, "Rename a class")]).Should().NotContain("## Features").And.NotContain("## Fixes");
        ReleaseNotes.Draft("v1.0.0", []).Should().Contain("No work items.");
    }

    [Fact]
    public void The_same_items_in_any_order_give_the_same_draft()
    {
        var a = Item(1, "One", WorkItemLevel.Feature);
        var b = Item(2, "fix: two");
        ReleaseNotes.Draft("v1.0.0", [a, b]).Should().Be(ReleaseNotes.Draft("v1.0.0", [b, a]));
    }
}
