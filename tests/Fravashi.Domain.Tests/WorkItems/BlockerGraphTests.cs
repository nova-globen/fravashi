using AwesomeAssertions;
using Fravashi.Domain.WorkItems;
using Xunit;
using static Fravashi.Domain.Tests.WorkItems.WorkItemFixture;

namespace Fravashi.Domain.Tests.WorkItems;

/// <summary>Plan section 4.3 refusals 2 and 3: cycles in blocks and in supersedes.</summary>
public class BlockerGraphTests
{
    private static WorkItemId Id(string value) => new(value);

    private static WorkItemLink Blocks(string from, string to) => Link(Id(from), WorkItemLinkType.Blocks, Id(to));

    [Fact]
    public void A_direct_cycle_is_found()
    {
        var edges = new[] { Blocks("a", "b") };

        var cycle = BlockerGraph.FindCycle(edges, Blocks("b", "a"));

        cycle.Should().NotBeNull();
        cycle!.Select(i => i.Value).Should().Equal("b", "a", "b");
        BlockerGraph.WouldCreateCycle(edges, Blocks("b", "a")).Should().BeTrue();
    }

    [Fact]
    public void A_long_cycle_is_found_with_its_path_in_order()
    {
        var edges = new[] { Blocks("b", "c"), Blocks("c", "d"), Blocks("d", "e"), Blocks("e", "a") };

        var cycle = BlockerGraph.FindCycle(edges, Blocks("a", "b"));

        cycle!.Select(i => i.Value).Should().Equal("a", "b", "c", "d", "e", "a");
    }

    [Fact]
    public void A_diamond_is_not_a_cycle()
    {
        var edges = new[] { Blocks("a", "b"), Blocks("a", "c"), Blocks("b", "d") };

        BlockerGraph.WouldCreateCycle(edges, Blocks("c", "d")).Should().BeFalse();
    }

    [Fact]
    public void A_disconnected_graph_is_not_a_cycle()
    {
        var edges = new[] { Blocks("a", "b"), Blocks("x", "y"), Blocks("y", "z") };

        BlockerGraph.WouldCreateCycle(edges, Blocks("b", "x")).Should().BeFalse();
        BlockerGraph.WouldCreateCycle([], Blocks("a", "b")).Should().BeFalse();
    }

    [Fact]
    public void A_supersedes_cycle_is_found_and_other_types_are_not_followed()
    {
        var supersedes = Link(Id("a"), WorkItemLinkType.Supersedes, Id("b"));
        var candidate = Link(Id("b"), WorkItemLinkType.Supersedes, Id("a"));

        BlockerGraph.WouldCreateCycle([supersedes], candidate).Should().BeTrue();

        // A blocks edge a -> b does not make b supersedes a a cycle.
        BlockerGraph.WouldCreateCycle([Blocks("a", "b")], candidate).Should().BeFalse();
    }

    [Fact]
    public void A_graph_that_already_holds_a_cycle_terminates()
    {
        // A mirrored blocks cycle, seeded as a provider said it.
        var edges = new[] { Blocks("a", "b"), Blocks("b", "c"), Blocks("c", "a") };

        BlockerGraph.WouldCreateCycle(edges, Blocks("c", "x")).Should().BeFalse();
        BlockerGraph.WouldCreateCycle(edges.Append(Blocks("x", "a")), Blocks("c", "x")).Should().BeTrue();
    }

    [Fact]
    public void Relates_and_duplicates_are_not_checked_for_cycles()
    {
        var act = () => BlockerGraph.FindCycle([], Link(Id("a"), WorkItemLinkType.Relates, Id("b")));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void An_authored_blocking_cycle_is_refused_naming_the_items()
    {
        var a = Item();
        var b = Item();
        var c = Item();
        var edges = new[] { Link(b, WorkItemLinkType.Blocks, c), Link(c, WorkItemLinkType.Blocks, a) };

        var result = WorkItemRules.ValidateLink(b, WorkItemLinkType.BlockedBy, a, edges, id => "#" + id.Value);

        result.ShouldBeRefusedWith(WorkItemRules.Codes.BlockedByCycle, "link");
        var message = result.Errors.Single().Message;
        message.Should().Contain("#" + a.Id.Value).And.Contain("#" + b.Id.Value).And.Contain("#" + c.Id.Value);
    }

    [Fact]
    public void An_authored_supersedes_cycle_is_refused()
    {
        var a = Item();
        var b = Item();

        WorkItemRules.ValidateLink(a, WorkItemLinkType.SupersededBy, b, [Link(a, WorkItemLinkType.Supersedes, b)])
            .ShouldBeRefusedWith(WorkItemRules.Codes.SupersedesCycle, "link");
    }
}
