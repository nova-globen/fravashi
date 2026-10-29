using AwesomeAssertions;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Xunit;
using static Fravashi.Domain.Tests.WorkItems.WorkItemFixture;

namespace Fravashi.Domain.Tests.WorkItems;

/// <summary>Plan section 1.1: the containment <c>Tier &gt; Feature &gt; Increment &gt; Task</c>.</summary>
public class ContainmentRulesTests
{
    public static TheoryData<WorkItemLevel, WorkItemLevel, bool, bool> EveryPair()
    {
        // child, parent, accepted when authored (exactly one up), accepted as an invariant (strictly higher)
        var data = new TheoryData<WorkItemLevel, WorkItemLevel, bool, bool>();
        foreach (var child in WorkItemLevels.All)
        {
            foreach (var parent in WorkItemLevels.All)
            {
                data.Add(child, parent, parent.Depth() == child.Depth() - 1, parent.Depth() < child.Depth());
            }
        }

        return data;
    }

    [Fact]
    public void There_are_sixteen_level_pairs()
    {
        EveryPair().Should().HaveCount(16);
    }

    [Theory]
    [MemberData(nameof(EveryPair))]
    public void Every_level_and_parent_level_pair_is_judged_as_section_1_1_says(
        WorkItemLevel child, WorkItemLevel parentLevel, bool authoredOk, bool invariantOk)
    {
        var parent = Item(parentLevel);
        var state = State(child, parent: parent.Id);

        var invariant = WorkItemRules.CheckInvariants(state, parent);
        var authored = WorkItemRules.ValidateNew(state, parent);

        invariant.IsValid.Should().Be(invariantOk);
        authored.IsValid.Should().Be(authoredOk);
        if (!invariantOk)
        {
            invariant.ShouldBeRefusedWith(WorkItemRules.Codes.ParentLevelNotHigher, "parent");
            authored.ShouldBeRefusedWith(WorkItemRules.Codes.ParentLevelNotHigher, "parent");
        }
        else if (!authoredOk)
        {
            authored.ShouldBeRefusedWith(WorkItemRules.Codes.ParentLevelNotAdjacent, "parent");
        }
    }

    [Theory]
    [InlineData(WorkItemLevel.Tier)]
    [InlineData(WorkItemLevel.Feature)]
    [InlineData(WorkItemLevel.Increment)]
    [InlineData(WorkItemLevel.Task)]
    public void A_parent_is_optional_at_every_level(WorkItemLevel level)
    {
        WorkItemRules.ValidateNew(State(level)).ShouldBeValid();
    }

    [Fact]
    public void The_allowed_parent_level_is_exactly_one_up()
    {
        WorkItemLevel.Tier.AllowedParentLevel().Should().BeNull();
        WorkItemLevel.Feature.AllowedParentLevel().Should().Be(WorkItemLevel.Tier);
        WorkItemLevel.Increment.AllowedParentLevel().Should().Be(WorkItemLevel.Feature);
        WorkItemLevel.Task.AllowedParentLevel().Should().Be(WorkItemLevel.Increment);
    }

    [Fact]
    public void A_cross_project_parent_is_refused_by_both_tiers()
    {
        var parent = Item(WorkItemLevel.Increment, project: new ProjectId("other"));
        var state = State(WorkItemLevel.Task, parent: parent.Id);

        WorkItemRules.CheckInvariants(state, parent).ShouldBeRefusedWith(WorkItemRules.Codes.ParentOtherProject, "parent");
        WorkItemRules.ValidateNew(state, parent).ShouldBeRefusedWith(WorkItemRules.Codes.ParentOtherProject, "parent");
    }

    [Fact]
    public void A_named_parent_that_is_not_supplied_is_refused()
    {
        var state = State(WorkItemLevel.Task, parent: new WorkItemId("missing"));

        WorkItemRules.CheckInvariants(state).ShouldBeRefusedWith(WorkItemRules.Codes.ParentNotFound, "parent");
        WorkItemRules.CheckInvariants(state, Item(WorkItemLevel.Increment)).ShouldBeRefusedWith(WorkItemRules.Codes.ParentNotFound, "parent");
    }

    [Fact]
    public void An_item_cannot_be_its_own_parent()
    {
        var state = State(WorkItemLevel.Task);
        state = state with { Parent = state.Id };

        WorkItem.Restore(state).ShouldBeRefusedWith(WorkItemRules.Codes.ParentLevelNotHigher, "parent");
    }

    [Fact]
    public void Restore_checks_only_the_item_local_invariants_and_the_relational_ones_need_the_parent()
    {
        // Plan section 1.8: the relation to a parent is not decidable from the item alone, so Restore admits an
        // item whose parent id names a lower item or an item in another project; the checks given that parent
        // refuse it.
        var task = Item(WorkItemLevel.Task);
        var elsewhere = Item(WorkItemLevel.Feature, project: new ProjectId("elsewhere"));
        var tierUnderTask = State(WorkItemLevel.Tier, parent: task.Id);
        var incrementElsewhere = State(WorkItemLevel.Increment, parent: elsewhere.Id);

        WorkItem.Restore(tierUnderTask).IsValid.Should().BeTrue();
        WorkItem.Restore(incrementElsewhere).IsValid.Should().BeTrue();
        WorkItemRules.CheckInvariants(tierUnderTask, task).ShouldBeRefusedWith(WorkItemRules.Codes.ParentLevelNotHigher);
        WorkItemRules.CheckInvariants(incrementElsewhere, elsewhere).ShouldBeRefusedWith(WorkItemRules.Codes.ParentOtherProject);
        WorkItemRules.CheckInvariants(tierUnderTask).ShouldBeRefusedWith(WorkItemRules.Codes.ParentNotFound);
    }

    [Theory]
    [InlineData(99)]
    [InlineData(-1)]
    public void A_level_change_to_an_undefined_level_is_refused_as_an_invariant(int undefined)
    {
        var task = Item(WorkItemLevel.Task);

        var result = WorkItemRules.ValidateParent(task, (WorkItemLevel)undefined, null, []);

        result.ShouldBeRefusedWith(WorkItemRules.Codes.ValueUndefined, "level");
        WorkItemRules.TierOf(WorkItemRules.Codes.ValueUndefined).Should().Be(RuleTier.Invariant);
    }

    [Fact]
    public void A_level_and_parent_change_still_checks_the_item_local_invariants()
    {
        // An item restored without its parent can only be judged locally; a change of its placement re-judges both.
        var increment = Item(WorkItemLevel.Increment);
        var task = Item(WorkItemLevel.Task);

        var result = WorkItemRules.ValidateParent(task, (WorkItemLevel)99, increment, []);

        result.ShouldBeRefusedWith(WorkItemRules.Codes.ValueUndefined, "level");
        result.Errors.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void A_level_change_is_refused_when_an_existing_child_would_no_longer_be_one_level_down()
    {
        var increment = Item(WorkItemLevel.Increment);
        var task = Item(WorkItemLevel.Task, parent: increment.Id);

        // Increment to Feature: the Task child is still lower, so only the authoring step breaks.
        WorkItemRules.ValidateParent(increment, WorkItemLevel.Feature, null, [task])
            .ShouldBeRefusedWith(WorkItemRules.Codes.ChildLevelNotAdjacent, "level");
    }

    [Fact]
    public void A_level_change_is_refused_outright_when_an_existing_child_would_no_longer_be_lower()
    {
        var increment = Item(WorkItemLevel.Increment);
        var task = Item(WorkItemLevel.Task, parent: increment.Id);

        var result = WorkItemRules.ValidateParent(increment, WorkItemLevel.Task, null, [task]);

        result.ShouldBeRefusedWith(WorkItemRules.Codes.ChildLevelNotLower, "level");
        WorkItemRules.TierOf(WorkItemRules.Codes.ChildLevelNotLower).Should().Be(RuleTier.Invariant);
        WorkItemRules.CheckInvariants(increment.ToState() with { Level = WorkItemLevel.Task }, null, [task])
            .ShouldBeRefusedWith(WorkItemRules.Codes.ChildLevelNotLower);
    }

    [Fact]
    public void A_level_change_is_refused_when_the_item_would_no_longer_be_one_level_under_its_parent()
    {
        var feature = Item(WorkItemLevel.Feature);
        var increment = Item(WorkItemLevel.Increment, parent: feature.Id);

        WorkItemRules.ValidateParent(increment, WorkItemLevel.Task, feature, [])
            .ShouldBeRefusedWith(WorkItemRules.Codes.ParentLevelNotAdjacent, "parent");
    }

    [Fact]
    public void Re_parenting_a_task_under_a_feature_is_refused_and_under_an_increment_accepted()
    {
        var feature = Item(WorkItemLevel.Feature);
        var increment = Item(WorkItemLevel.Increment);
        var task = Item(WorkItemLevel.Task);

        WorkItemRules.ValidateParent(task, WorkItemLevel.Task, feature, []).ShouldBeRefusedWith(WorkItemRules.Codes.ParentLevelNotAdjacent);
        WorkItemRules.ValidateParent(task, WorkItemLevel.Task, increment, []).IsValid.Should().BeTrue();
        WorkItemRules.ValidateParent(task, WorkItemLevel.Task, null, []).IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_parent_only_change_does_not_re_judge_a_mirrored_child_placement()
    {
        // A mirrored Feature holding a Task directly (allowed by the invariant) gets a new Tier parent.
        var tier = Item(WorkItemLevel.Tier);
        var feature = Item(WorkItemLevel.Feature);
        var task = Item(WorkItemLevel.Task, parent: feature.Id);

        WorkItemRules.ValidateParent(feature, WorkItemLevel.Feature, tier, [task]).IsValid.Should().BeTrue();
    }

    [Fact]
    public void An_update_that_moves_the_parent_is_judged_by_the_parent_rules()
    {
        var feature = Item(WorkItemLevel.Feature);
        var task = Item(WorkItemLevel.Task);

        WorkItemRules.ValidateUpdate(task, task.ToState() with { Parent = feature.Id }, feature, [])
            .ShouldBeRefusedWith(WorkItemRules.Codes.ParentLevelNotAdjacent, "parent");
    }

    [Fact]
    public void The_parent_chain_cannot_contain_a_cycle_because_every_parent_is_strictly_higher()
    {
        // Rule 5: there is no hierarchy cycle check, so assert the property. Walking up from any level through
        // valid parents strictly decreases the depth, so a chain is at most four long and never revisits a level.
        foreach (var child in WorkItemLevels.All)
        {
            foreach (var parent in WorkItemLevels.All)
            {
                var parentItem = Item(parent);
                var accepted = WorkItemRules.CheckInvariants(State(child, parent: parentItem.Id), parentItem).IsValid;
                if (accepted)
                {
                    parent.Depth().Should().BeLessThan(child.Depth());
                }
            }
        }

        // The shortest possible cycle, two items at the same level parenting each other, is refused.
        var a = State(WorkItemLevel.Feature);
        var b = Restored(State(WorkItemLevel.Feature, parent: a.Id));
        WorkItemRules.CheckInvariants(a with { Parent = b.Id }, b).ShouldBeRefusedWith(WorkItemRules.Codes.ParentLevelNotHigher);
    }
}
