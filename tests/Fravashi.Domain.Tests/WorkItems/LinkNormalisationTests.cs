using AwesomeAssertions;
using Fravashi.Domain.WorkItems;
using Xunit;
using static Fravashi.Domain.Tests.WorkItems.WorkItemFixture;

namespace Fravashi.Domain.Tests.WorkItems;

/// <summary>Plan sections 4.1 and 4.2: one edge, two readings, kept symmetric by construction.</summary>
public class LinkNormalisationTests
{
    private static readonly WorkItemId A = new("a");
    private static readonly WorkItemId B = new("b");

    [Fact]
    public void A_blocked_by_b_is_the_same_edge_as_b_blocks_a()
    {
        var read = Link(A, WorkItemLinkType.BlockedBy, B);
        var forward = Link(B, WorkItemLinkType.Blocks, A);

        read.Should().Be(forward);
        read.Source.Should().Be(B);
        read.Type.Should().Be(WorkItemLinkType.Blocks);
        read.Target.Should().Be(A);
    }

    [Theory]
    [InlineData(WorkItemLinkType.DuplicatedBy, WorkItemLinkType.Duplicates)]
    [InlineData(WorkItemLinkType.SupersededBy, WorkItemLinkType.Supersedes)]
    public void The_other_inverse_readings_normalise_to_their_forward_edge(WorkItemLinkType inverse, WorkItemLinkType forward)
    {
        Link(A, inverse, B).Should().Be(Link(B, forward, A));
    }

    [Fact]
    public void Relates_is_order_insensitive_and_stored_in_ordinal_order()
    {
        var ab = Link(A, WorkItemLinkType.Relates, B);
        var ba = Link(B, WorkItemLinkType.Relates, A);

        ab.Should().Be(ba);
        ab.Source.Should().Be(A);
        ab.Target.Should().Be(B);

        // Ordinal, not culture: "Z" sorts before "a".
        Link(new WorkItemId("a"), WorkItemLinkType.Relates, new WorkItemId("Z")).Source.Value.Should().Be("Z");
    }

    [Theory]
    [InlineData(WorkItemLinkType.Blocks)]
    [InlineData(WorkItemLinkType.BlockedBy)]
    [InlineData(WorkItemLinkType.Relates)]
    [InlineData(WorkItemLinkType.Duplicates)]
    [InlineData(WorkItemLinkType.DuplicatedBy)]
    [InlineData(WorkItemLinkType.Supersedes)]
    [InlineData(WorkItemLinkType.SupersededBy)]
    public void A_self_link_is_refused_with_every_type(WorkItemLinkType type)
    {
        WorkItemLink.Normalise(A, type, A).ShouldBeRefusedWith(WorkItemRules.Codes.LinkSelf, "link");
    }

    [Fact]
    public void Each_item_reads_its_own_end_of_the_edge()
    {
        var edge = Link(A, WorkItemLinkType.BlockedBy, B);

        edge.ReadingFrom(A).Should().Be(WorkItemLinkType.BlockedBy);
        edge.ReadingFrom(B).Should().Be(WorkItemLinkType.Blocks);
        edge.OtherEnd(A).Should().Be(B);
        edge.OtherEnd(B).Should().Be(A);
        edge.Touches(A).Should().BeTrue();
        edge.Touches(new WorkItemId("c")).Should().BeFalse();
    }

    [Fact]
    public void Inverse_names_pair_up_and_only_forward_types_are_stored()
    {
        foreach (var type in WorkItemLinkTypes.All)
        {
            type.Inverse().Inverse().Should().Be(type);
            type.ToForward().IsForward().Should().BeTrue();
        }

        WorkItemLinkTypes.All.Where(t => t.IsForward()).Should().BeEquivalentTo(WorkItemLinkTypes.Forward);
        WorkItemLinkTypes.All.Where(t => t.IsSymmetric()).Should().Equal(WorkItemLinkType.Relates);
        WorkItemLinkType.Blocks.Inverse().Should().Be(WorkItemLinkType.BlockedBy);
        WorkItemLinkType.Duplicates.Inverse().Should().Be(WorkItemLinkType.DuplicatedBy);
        WorkItemLinkType.Supersedes.Inverse().Should().Be(WorkItemLinkType.SupersededBy);
    }

    [Fact]
    public void Linking_an_edge_that_already_exists_is_valid_in_either_reading()
    {
        var a = Item();
        var b = Item();
        var existing = Link(a, WorkItemLinkType.Blocks, b);

        WorkItemRules.ValidateLink(b, WorkItemLinkType.BlockedBy, a, [existing]).ShouldBeValid().Should().Be(existing);
        WorkItemRules.ValidateLink(a, WorkItemLinkType.Blocks, b, [existing]).ShouldBeValid().Should().Be(existing);
    }

    [Fact]
    public void A_chained_duplicate_is_refused_naming_the_original()
    {
        var original = Item();
        var first = Item(status: WorkItemStatus.Cancelled, resolution: WorkItemResolution.Duplicate);
        var second = Item();
        var firstEdge = Link(first, WorkItemLinkType.Duplicates, original);

        var result = WorkItemRules.ValidateLink(second, WorkItemLinkType.Duplicates, first, [firstEdge], id => "#" + id.Value);

        result.ShouldBeRefusedWith(WorkItemRules.Codes.DuplicateChained, "link");
        result.Errors.Single().Message.Should().Contain("#" + original.Id.Value);

        // Stated from the other end, the same refusal.
        WorkItemRules.ValidateLink(first, WorkItemLinkType.DuplicatedBy, second, [firstEdge])
            .ShouldBeRefusedWith(WorkItemRules.Codes.DuplicateChained);

        // Pointing at the live original is fine.
        WorkItemRules.ValidateLink(second, WorkItemLinkType.Duplicates, original, [firstEdge]).ShouldBeValid();
    }

    [Fact]
    public void A_relates_link_is_never_a_cycle()
    {
        var a = Item();
        var b = Item();

        WorkItemRules.ValidateLink(b, WorkItemLinkType.Relates, a, [Link(a, WorkItemLinkType.Blocks, b)]).ShouldBeValid();
    }
}
