using AwesomeAssertions;
using Fravashi.Domain.WorkItems;
using Xunit;
using static Fravashi.Domain.Tests.WorkItems.WorkItemFixture;

namespace Fravashi.Domain.Tests.WorkItems;

/// <summary>Plan section 1.2: Spike and Bug are kinds, orthogonal to the level.</summary>
public class KindRulesTests
{
    [Theory]
    [InlineData(WorkItemKind.Spike, WorkItemLevel.Tier)]
    [InlineData(WorkItemKind.Spike, WorkItemLevel.Feature)]
    [InlineData(WorkItemKind.Bug, WorkItemLevel.Tier)]
    [InlineData(WorkItemKind.Bug, WorkItemLevel.Feature)]
    public void A_spike_or_bug_is_refused_above_increment_level(WorkItemKind kind, WorkItemLevel level)
    {
        WorkItemRules.ValidateNew(State(level, kind)).ShouldBeRefusedWith(WorkItemRules.Codes.KindLevelNotAllowed, "kind");
    }

    [Theory]
    [InlineData(WorkItemKind.Spike, WorkItemLevel.Increment)]
    [InlineData(WorkItemKind.Spike, WorkItemLevel.Task)]
    [InlineData(WorkItemKind.Bug, WorkItemLevel.Increment)]
    [InlineData(WorkItemKind.Bug, WorkItemLevel.Task)]
    [InlineData(WorkItemKind.Standard, WorkItemLevel.Tier)]
    [InlineData(WorkItemKind.Standard, WorkItemLevel.Feature)]
    [InlineData(WorkItemKind.Standard, WorkItemLevel.Increment)]
    [InlineData(WorkItemKind.Standard, WorkItemLevel.Task)]
    public void Every_other_kind_and_level_is_accepted(WorkItemKind kind, WorkItemLevel level)
    {
        WorkItemRules.ValidateNew(State(level, kind)).ShouldBeValid();
    }

    [Fact]
    public void A_timebox_is_refused_on_a_non_spike()
    {
        WorkItemRules.ValidateNew(State(WorkItemLevel.Task, WorkItemKind.Bug, timebox: TimeSpan.FromHours(4)))
            .ShouldBeRefusedWith(WorkItemRules.Codes.TimeboxRequiresSpike, "spikeTimebox");
        WorkItemRules.ValidateNew(State(WorkItemLevel.Task, WorkItemKind.Standard, timebox: TimeSpan.FromHours(4)))
            .ShouldBeRefusedWith(WorkItemRules.Codes.TimeboxRequiresSpike, "spikeTimebox");
    }

    [Fact]
    public void A_spike_timebox_is_optional_and_must_be_positive()
    {
        WorkItemRules.ValidateNew(State(WorkItemLevel.Task, WorkItemKind.Spike)).ShouldBeValid().SpikeTimebox.Should().BeNull();
        WorkItemRules.ValidateNew(State(WorkItemLevel.Task, WorkItemKind.Spike, timebox: TimeSpan.FromHours(2)))
            .ShouldBeValid().SpikeTimebox.Should().Be(TimeSpan.FromHours(2));
        WorkItemRules.ValidateNew(State(WorkItemLevel.Task, WorkItemKind.Spike, timebox: TimeSpan.Zero))
            .ShouldBeRefusedWith(WorkItemRules.Codes.TimeboxNotPositive, "spikeTimebox");
    }

    [Fact]
    public void Changing_a_bug_task_to_a_feature_is_refused_by_the_kind_rule()
    {
        var bug = Item(WorkItemLevel.Task, WorkItemKind.Bug);

        WorkItemRules.ValidateUpdate(bug, bug.ToState() with { Level = WorkItemLevel.Feature }, null, [])
            .ShouldBeRefusedWith(WorkItemRules.Codes.KindLevelNotAllowed, "kind");
    }

    [Fact]
    public void Changing_a_spike_to_standard_while_keeping_its_timebox_is_refused()
    {
        var spike = Restored(State(WorkItemLevel.Task, WorkItemKind.Spike, timebox: TimeSpan.FromHours(1)));

        WorkItemRules.ValidateUpdate(spike, spike.ToState() with { Kind = WorkItemKind.Standard }, null, [])
            .ShouldBeRefusedWith(WorkItemRules.Codes.TimeboxRequiresSpike);
    }

    [Fact]
    public void A_mirrored_bug_feature_can_still_have_its_title_edited()
    {
        var mirrored = Item(WorkItemLevel.Feature, WorkItemKind.Bug);

        WorkItemRules.ValidateUpdate(mirrored, mirrored.ToState() with { Title = "Renamed" }, null, [])
            .ShouldBeValid().Title.Should().Be("Renamed");
    }

    [Fact]
    public void An_undefined_kind_is_refused_as_an_invariant()
    {
        WorkItem.Restore(State(kind: (WorkItemKind)42)).ShouldBeRefusedWith(WorkItemRules.Codes.ValueUndefined, "kind");
    }
}
