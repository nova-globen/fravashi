using AwesomeAssertions;
using Fravashi.Domain.Mirroring;
using Fravashi.Domain.Tests.WorkItems;
using Fravashi.Domain.WorkItems;
using Xunit;

namespace Fravashi.Domain.Tests.Mirroring;

/// <summary>
/// ADR 0018's "two stores" consequence: "a work item in Jira and a plan in Confluence cannot be created
/// atomically ... dangling links are detected and surfaced, deletions tombstone rather than vanish, and a
/// reconciliation pass can rebuild the link graph. Design that before the first provider adapter ships, not
/// after a user's graph has holes in it."
/// <para>
/// The load-bearing distinction these hold: a reference to a TOMBSTONED item is not a hole. That is the
/// entire argument for tombstones, and a pass that reported the two the same way would either cry wolf on
/// every deletion or quietly remove edges that were still readable.
/// </para>
/// </summary>
public class ReferentialRepairTests
{
    private static IReadOnlySet<WorkItemId> Ids(params WorkItem[] items) => items.Select(i => i.Id).ToHashSet();

    [Fact]
    public void A_graph_where_everything_points_at_something_is_sound()
    {
        var a = WorkItemFixture.Item();
        var b = WorkItemFixture.Item();
        var link = WorkItemFixture.Link(a, WorkItemLinkType.Blocks, b);

        var report = ReferentialRepair.Check([a, b], [link], Ids(a, b), new HashSet<WorkItemId>());

        report.IsSound.Should().BeTrue();
        report.Defects.Should().BeEmpty();
        report.Summary().Should().Be("2 items and 1 links check out.");
    }

    [Fact]
    public void An_empty_report_from_a_pass_that_ran_still_says_what_it_looked_at()
    {
        // Otherwise a pass that never started and a graph with nothing wrong produce the same report, and
        // the dashboard says "all clear" for a reconciliation that is not happening.
        var report = ReferentialRepair.Check([], [], new HashSet<WorkItemId>(), new HashSet<WorkItemId>());

        report.ItemsChecked.Should().Be(0);
        report.Summary().Should().Contain("0 items and 0 links");
    }

    [Fact]
    public void A_link_to_an_item_that_left_no_trace_is_a_hole_in_the_graph()
    {
        var a = WorkItemFixture.Item();
        var gone = new WorkItemId("item-gone");
        var link = WorkItemFixture.Link(a.Id, WorkItemLinkType.Blocks, gone);

        var report = ReferentialRepair.Check([a], [link], Ids(a), new HashSet<WorkItemId>());

        report.IsSound.Should().BeFalse();
        report.Broken.Should().ContainSingle()
            .Which.Should().Match<GraphDefect>(d => d.Kind == DefectKind.LinkTargetMissing && d.Suggested == RepairAction.RemoveLink);
        report.Broken[0].Sentence.Should().Contain("loses the fact that the two were related");
    }

    [Fact]
    public void A_link_to_a_tombstoned_item_is_reported_but_is_not_broken()
    {
        // This is what tombstones buy: the provider deleted the item, and every edge to it can still be read.
        var a = WorkItemFixture.Item();
        var dead = WorkItemFixture.Item();
        var link = WorkItemFixture.Link(a, WorkItemLinkType.Relates, dead);

        var report = ReferentialRepair.Check([a, dead], [link], Ids(a, dead), Ids(dead));

        report.IsSound.Should().BeTrue();
        report.Defects.Should().ContainSingle()
            .Which.Should().Match<GraphDefect>(d => d.Kind == DefectKind.LinkTargetTombstoned && d.Suggested == RepairAction.Keep);
        report.Summary().Should().Contain("reference(s) to tombstoned things");
    }

    [Fact]
    public void A_link_whose_source_went_missing_is_found_too()
    {
        // A pass that only followed the forward direction would report half the holes, and which half
        // depends on which way round the edge happened to be normalised.
        var b = WorkItemFixture.Item();
        var gone = new WorkItemId("item-gone");
        var link = WorkItemFixture.Link(gone, WorkItemLinkType.Blocks, b.Id);

        var report = ReferentialRepair.Check([b], [link], Ids(b), new HashSet<WorkItemId>());

        report.Broken.Should().ContainSingle().Which.Target.Should().Be("item-gone");
    }

    [Fact]
    public void An_item_under_a_parent_that_is_not_in_the_graph_is_broken()
    {
        var orphan = WorkItemFixture.Item(WorkItemLevel.Task, parent: new WorkItemId("item-missing"));

        var report = ReferentialRepair.Check([orphan], [], Ids(orphan), new HashSet<WorkItemId>());

        report.Broken.Should().ContainSingle()
            .Which.Should().Match<GraphDefect>(d => d.Kind == DefectKind.ParentMissing && d.Suggested == RepairAction.ClearParent);
    }

    [Fact]
    public void An_item_under_a_tombstoned_parent_still_has_a_hierarchy_that_reads()
    {
        var parent = WorkItemFixture.Item(WorkItemLevel.Feature);
        var child = WorkItemFixture.Item(WorkItemLevel.Task, parent: parent.Id);

        var report = ReferentialRepair.Check([parent, child], [], Ids(parent, child), Ids(parent));

        report.IsSound.Should().BeTrue();
        report.Defects.Should().ContainSingle().Which.Kind.Should().Be(DefectKind.ParentTombstoned);
    }

    [Fact]
    public void An_item_with_no_parent_is_not_a_defect()
    {
        var top = WorkItemFixture.Item(WorkItemLevel.Tier);

        ReferentialRepair.Check([top], [], Ids(top), new HashSet<WorkItemId>()).Defects.Should().BeEmpty();
    }

    // ---- across the two stores -------------------------------------------------------------------

    [Fact]
    public void A_document_referring_to_a_work_item_that_does_not_exist_is_reported()
    {
        var defects = ReferentialRepair.CheckAcrossStores(
            new Dictionary<string, IReadOnlyCollection<int>> { ["the-plan"] = [12, 99] },
            new HashSet<int> { 12 },
            new Dictionary<string, IReadOnlyCollection<string>>(),
            new HashSet<string>());

        defects.Should().ContainSingle()
            .Which.Should().Match<GraphDefect>(d => d.Kind == DefectKind.WikiReferenceMissing && d.Target == "#99");
        defects[0].Sentence.Should().Contain("renders as a broken reference");
    }

    [Fact]
    public void A_work_item_referring_to_a_document_that_is_not_in_the_library_is_reported()
    {
        // The direction that is easy to forget: a page deleted on the provider while its item survives.
        var defects = ReferentialRepair.CheckAcrossStores(
            new Dictionary<string, IReadOnlyCollection<int>>(),
            new HashSet<int>(),
            new Dictionary<string, IReadOnlyCollection<string>> { ["item-1"] = ["the-plan", "gone"] },
            new HashSet<string> { "the-plan" });

        defects.Should().ContainSingle()
            .Which.Should().Match<GraphDefect>(d => d.Kind == DefectKind.DocumentReferenceMissing && d.Target == "gone");
    }

    [Fact]
    public void Nothing_is_repaired_by_looking_at_it()
    {
        // Every suggested action loses information, so a pass that tidied as it went would destroy the
        // evidence of what went wrong. The pass reports; a person or an explicit repair call acts.
        var a = WorkItemFixture.Item();
        var link = WorkItemFixture.Link(a.Id, WorkItemLinkType.Blocks, new WorkItemId("item-gone"));

        var report = ReferentialRepair.Check([a], [link], Ids(a), new HashSet<WorkItemId>());

        report.Broken.Should().NotBeEmpty();
        report.Broken.Should().AllSatisfy(d => d.Suggested.Should().NotBe(RepairAction.None, "a hole should name what could be done about it"));
    }
}
