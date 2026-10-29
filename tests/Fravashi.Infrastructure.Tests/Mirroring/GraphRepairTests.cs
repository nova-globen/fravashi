using AwesomeAssertions;
using Fravashi.Application.Mirroring;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Mirroring;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Mirroring;
using Fravashi.Infrastructure.Tests.WorkItems;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Mirroring;

/// <summary>
/// Referential repair over the REAL board, which is the only place a link can genuinely dangle. The domain
/// tests argue about what a defect means; these prove the pass finds them in a database and that a repair
/// only ever happens because somebody asked for it.
/// </summary>
public class GraphRepairTests : IDisposable
{
    private readonly LocalBoardFixture _board = new();
    private readonly SqliteMirrorStore _mirror;

    public GraphRepairTests() => _mirror = new SqliteMirrorStore(_board.Factory);

    public void Dispose()
    {
        _board.Dispose();
        GC.SuppressFinalize(this);
    }

    private GraphRepair Repair() => new(_mirror);

    [Fact]
    public async Task A_board_whose_links_all_land_is_sound()
    {
        var a = await _board.TaskAsync("One");
        var b = await _board.TaskAsync("Two");
        await _board.Board.LinkAsync(a.Id, WorkItemLinkType.Blocks, b.Id, MutationContext.User);

        var report = await Repair().InspectAsync(_board.Board);

        report.IsSound.Should().BeTrue();
        report.ItemsChecked.Should().Be(2);
        report.LinksChecked.Should().Be(1, "an edge read from both of its ends is still one edge");
        report.Complete.Should().BeTrue();
    }

    [Fact]
    public async Task A_link_to_a_tombstoned_item_is_reported_and_the_graph_is_still_sound()
    {
        // This is the payoff of tombstoning rather than deleting: the edge can still be read.
        var a = await _board.TaskAsync("One");
        var gone = await _board.TaskAsync("Deleted upstream");
        await _board.Board.LinkAsync(a.Id, WorkItemLinkType.Relates, gone.Id, MutationContext.User);
        await _mirror.TombstoneAsync(ProjectId.Default, gone.Id, "It was deleted on jira.", _board.Clock.GetUtcNow());

        var report = await Repair().InspectAsync(_board.Board);

        report.IsSound.Should().BeTrue();
        report.Defects.Should().Contain(d => d.Kind == DefectKind.LinkTargetTombstoned);
    }

    [Fact]
    public async Task A_link_whose_target_left_no_trace_is_a_hole_and_names_the_only_repair()
    {
        var a = await _board.TaskAsync("One");
        var b = await _board.TaskAsync("Two");
        await _board.Board.LinkAsync(a.Id, WorkItemLinkType.Blocks, b.Id, MutationContext.User);

        // Straight into the table, because the port would refuse to author a dangling edge. That is the
        // point: a hole in the graph does not come from this product's own writes, it comes from the other
        // store, and a pass has to find one however it got there.
        _board.Execute("DELETE FROM work_items WHERE id = $id;", ("$id", b.Id.Value));

        var report = await Repair().InspectAsync(_board.Board);

        report.IsSound.Should().BeFalse();
        var defect = report.Broken.Should().ContainSingle().Which;
        defect.Kind.Should().Be(DefectKind.LinkTargetMissing);
        defect.Suggested.Should().Be(RepairAction.RemoveLink);
        defect.Edge.Should().NotBeNull("a repair reads the edge as data, never out of the sentence");
    }

    [Fact]
    public async Task Removing_a_dangling_link_is_something_somebody_asks_for_and_is_told_what_it_cost()
    {
        var a = await _board.TaskAsync("One");
        var b = await _board.TaskAsync("Two");
        await _board.Board.LinkAsync(a.Id, WorkItemLinkType.Blocks, b.Id, MutationContext.User);
        _board.Execute("DELETE FROM work_items WHERE id = $id;", ("$id", b.Id.Value));

        var before = await Repair().InspectAsync(_board.Board);
        before.Broken.Should().ContainSingle("inspecting must not repair anything by itself");

        var outcome = await Repair().ApplyAsync(_board.Board, before.Broken[0]);

        outcome.Repaired.Should().BeTrue();
        outcome.Sentence.Should().Contain("no longer recorded", "a repair that loses something says so");
        (await Repair().InspectAsync(_board.Board)).IsSound.Should().BeTrue();
    }

    [Fact]
    public async Task A_reference_that_is_honest_is_not_something_that_can_be_repaired()
    {
        var a = await _board.TaskAsync("One");
        var gone = await _board.TaskAsync("Deleted upstream");
        await _board.Board.LinkAsync(a.Id, WorkItemLinkType.Relates, gone.Id, MutationContext.User);
        await _mirror.TombstoneAsync(ProjectId.Default, gone.Id, "It was deleted on jira.", _board.Clock.GetUtcNow());

        var report = await Repair().InspectAsync(_board.Board);
        var outcome = await Repair().ApplyAsync(_board.Board, report.Defects.Single(d => d.Kind == DefectKind.LinkTargetTombstoned));

        outcome.Repaired.Should().BeFalse();
        outcome.Sentence.Should().Contain("nothing to repair");
    }

    [Fact]
    public async Task An_item_under_a_parent_that_is_gone_can_be_detached_when_somebody_asks()
    {
        var parent = await _board.CreateAsync(new NewWorkItem
        {
            Level = WorkItemLevel.Feature,
            Title = "A feature",
            Description = "The problem and the outcome.",
        });
        // An Increment, not a Task: the hierarchy is Tier then Feature then Increment then Task, and the
        // board refuses a parent that is not exactly one level up.
        var child = await _board.CreateAsync(new NewWorkItem
        {
            Level = WorkItemLevel.Increment,
            Title = "An increment",
            Description = "The problem and the outcome.",
            Parent = parent.Id,
        });
        _board.Execute("DELETE FROM work_items WHERE id = $id;", ("$id", parent.Id.Value));

        var report = await Repair().InspectAsync(_board.Board);
        var defect = report.Broken.Should().ContainSingle().Which;
        defect.Kind.Should().Be(DefectKind.ParentMissing);

        var outcome = await Repair().ApplyAsync(_board.Board, defect);

        outcome.Repaired.Should().BeTrue();
        (await _board.Board.GetAsync(child.Id))!.Parent.Should().BeNull();
    }

    [Fact]
    public async Task A_defect_about_an_item_that_has_since_gone_refuses_rather_than_throwing()
    {
        var parent = await _board.CreateAsync(new NewWorkItem
        {
            Level = WorkItemLevel.Feature,
            Title = "A feature",
            Description = "The problem and the outcome.",
        });
        // An Increment, not a Task: the hierarchy is Tier then Feature then Increment then Task, and the
        // board refuses a parent that is not exactly one level up.
        var child = await _board.CreateAsync(new NewWorkItem
        {
            Level = WorkItemLevel.Increment,
            Title = "An increment",
            Description = "The problem and the outcome.",
            Parent = parent.Id,
        });
        _board.Execute("DELETE FROM work_items WHERE id = $id;", ("$id", parent.Id.Value));
        var defect = (await Repair().InspectAsync(_board.Board)).Broken[0];

        _board.Execute("DELETE FROM work_items WHERE id = $id;", ("$id", child.Id.Value));
        var outcome = await Repair().ApplyAsync(_board.Board, defect);

        outcome.Repaired.Should().BeFalse();
        outcome.Sentence.Should().Contain("no longer in this graph");
    }

    [Fact]
    public async Task An_empty_board_produces_a_report_that_says_it_looked()
    {
        var report = await Repair().InspectAsync(_board.Board);

        report.ItemsChecked.Should().Be(0);
        report.Summary().Should().Contain("0 items and 0 links");
        report.Summary().Should().NotContain("could not see the whole graph");
    }
}
