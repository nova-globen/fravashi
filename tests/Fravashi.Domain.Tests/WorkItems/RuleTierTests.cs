using AwesomeAssertions;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Xunit;
using static Fravashi.Domain.Tests.WorkItems.WorkItemFixture;

namespace Fravashi.Domain.Tests.WorkItems;

/// <summary>
/// Plan section 1.8: model invariants bind every stored item, authoring rules bind only what Fravashi writes,
/// and the mirrored shapes of section 3.3 are what proves the line was drawn where a provider's data fits.
/// </summary>
public class RuleTierTests
{
    /// <summary>Every rule with the tier plan section 1.8's code table gives it (marked in sections 1.1 to 1.4 and 4.3).</summary>
    private static readonly Dictionary<string, RuleTier> Expected = new(StringComparer.Ordinal)
    {
        // [I]
        ["value_undefined"] = RuleTier.Invariant,
        ["title_required"] = RuleTier.Invariant,
        ["resolution_on_open_item"] = RuleTier.Invariant,
        ["parent_level_not_higher"] = RuleTier.Invariant,
        ["parent_other_project"] = RuleTier.Invariant,
        ["parent_not_found"] = RuleTier.Invariant,
        ["child_level_not_lower"] = RuleTier.Invariant,
        ["link_self"] = RuleTier.Invariant,
        ["link_other_project"] = RuleTier.Invariant,
        ["unknown_storage_name"] = RuleTier.Invariant,

        // [A]
        ["description_required"] = RuleTier.Authoring,
        ["parent_level_not_adjacent"] = RuleTier.Authoring,
        ["child_level_not_adjacent"] = RuleTier.Authoring,
        ["kind_level_not_allowed"] = RuleTier.Authoring,
        ["timebox_requires_spike"] = RuleTier.Authoring,
        ["timebox_not_positive"] = RuleTier.Authoring,
        ["blocked_reason_required"] = RuleTier.Authoring,
        ["flag_on_terminal_item"] = RuleTier.Authoring,
        ["done_resolution_not_completed"] = RuleTier.Authoring,
        ["cancelled_resolution_required"] = RuleTier.Authoring,
        ["cancelled_resolution_invalid"] = RuleTier.Authoring,
        ["duplicate_link_required"] = RuleTier.Authoring,
        ["blocked_by_cycle"] = RuleTier.Authoring,
        ["supersedes_cycle"] = RuleTier.Authoring,
        ["duplicate_chained"] = RuleTier.Authoring,

        // The agent layer (increment C1). Authoring, every one: a provider's data must always land, and no
        // provider enforces a single mockup, a 0 to 100 score or a relative glob (decision D12).
        ["ref_role_not_repeatable"] = RuleTier.Authoring,
        ["ref_target_required"] = RuleTier.Authoring,
        ["ambiguity_out_of_range"] = RuleTier.Authoring,
        ["scope_pattern_not_relative"] = RuleTier.Authoring,
        ["agent_layer_text_required"] = RuleTier.Authoring,
        ["change_budget_not_positive"] = RuleTier.Authoring,
        ["person_blocker_incomplete"] = RuleTier.Authoring,

        // Backlog 298 (ADR 0031 decision 1): the autopilot mark. Authoring, since a provider has no concept
        // of it at all (there is nothing for a mirrored write to be held looser about).
        ["autopilot_mark_requires_ready"] = RuleTier.Authoring,
    };

    [Fact]
    public void Every_rule_is_registered_with_the_tier_the_plan_marks()
    {
        WorkItemRules.Tiers.Should().BeEquivalentTo(Expected);
    }

    [Fact]
    public void Every_declared_code_constant_is_registered()
    {
        var constants = typeof(WorkItemRules.Codes)
            .GetFields()
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        constants.Should().OnlyHaveUniqueItems();
        constants.Should().BeEquivalentTo(WorkItemRules.Tiers.Keys);
    }

    [Fact]
    public void An_unregistered_code_has_no_tier()
    {
        var act = () => WorkItemRules.TierOf("made_up");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_cancelled_item_with_no_resolution_is_a_valid_mirror_and_refused_when_authored()
    {
        // ADO's Removed state carries no resolution.
        var state = State(status: WorkItemStatus.Cancelled);

        WorkItemRules.CheckInvariants(state).ShouldBeValid();
        WorkItemRules.ValidateNew(state).ShouldBeRefusedWith(WorkItemRules.Codes.CancelledResolutionRequired);
    }

    [Fact]
    public void A_blocked_flag_with_no_reason_is_a_valid_mirror_and_refused_when_authored()
    {
        var state = State(flags: new WorkItemFlags(true, null, false, null));

        WorkItemRules.CheckInvariants(state).ShouldBeValid();
        WorkItemRules.ValidateNew(state).ShouldBeRefusedWith(WorkItemRules.Codes.BlockedReasonRequired);
    }

    [Fact]
    public void A_duplicate_with_no_duplicates_link_is_a_valid_mirror_and_refused_when_authored()
    {
        // A Jira issue resolved Duplicate with no link.
        var state = State(status: WorkItemStatus.Cancelled, resolution: WorkItemResolution.Duplicate);

        WorkItemRules.CheckInvariants(state).ShouldBeValid();
        WorkItemRules.ValidateNew(state).ShouldBeRefusedWith(WorkItemRules.Codes.DuplicateLinkRequired);
    }

    [Fact]
    public void A_task_directly_under_a_feature_is_a_valid_mirror_and_refused_when_authored()
    {
        var feature = Item(WorkItemLevel.Feature);
        var state = State(WorkItemLevel.Task, parent: feature.Id);

        WorkItemRules.CheckInvariants(state, feature).ShouldBeValid();
        WorkItemRules.ValidateNew(state, feature).ShouldBeRefusedWith(WorkItemRules.Codes.ParentLevelNotAdjacent);
    }

    [Fact]
    public void A_bug_on_a_feature_is_a_valid_mirror_and_refused_when_authored()
    {
        var state = State(WorkItemLevel.Feature, WorkItemKind.Bug);

        WorkItemRules.CheckInvariants(state).ShouldBeValid();
        WorkItemRules.ValidateNew(state).ShouldBeRefusedWith(WorkItemRules.Codes.KindLevelNotAllowed);
    }

    [Fact]
    public void An_empty_description_is_a_valid_mirror_and_refused_when_authored()
    {
        var state = State(description: string.Empty);

        WorkItemRules.CheckInvariants(state).ShouldBeValid();
        WorkItemRules.ValidateNew(state).ShouldBeRefusedWith(WorkItemRules.Codes.DescriptionRequired);
    }

    [Fact]
    public void A_flag_on_a_done_item_is_a_valid_mirror_and_refused_when_authored()
    {
        var state = State(status: WorkItemStatus.Done, resolution: WorkItemResolution.Completed, flags: WorkItemFlags.None.WithBlocked("Stuck"));

        WorkItemRules.CheckInvariants(state).ShouldBeValid();
        WorkItemRules.ValidateNew(state).ShouldBeRefusedWith(WorkItemRules.Codes.FlagOnTerminalItem);
        WorkItemRules.ValidateFlags(Restored(state), WorkItemFlags.None.WithDeferred(null))
            .ShouldBeRefusedWith(WorkItemRules.Codes.FlagOnTerminalItem);
    }

    [Fact]
    public void A_blocks_cycle_is_a_valid_mirror_and_refused_when_authored()
    {
        var a = Item();
        var b = Item();
        var existing = Link(a, WorkItemLinkType.Blocks, b);

        WorkItemRules.CheckLinkInvariants(b, WorkItemLinkType.Blocks, a).ShouldBeValid();
        WorkItemRules.ValidateLink(b, WorkItemLinkType.Blocks, a, [existing]).ShouldBeRefusedWith(WorkItemRules.Codes.BlockedByCycle);
    }

    [Fact]
    public void A_cross_project_parent_is_refused_by_both_tiers()
    {
        var parent = Item(WorkItemLevel.Increment, project: new ProjectId("elsewhere"));
        var state = State(WorkItemLevel.Task, parent: parent.Id);

        WorkItemRules.CheckInvariants(state, parent).ShouldBeRefusedWith(WorkItemRules.Codes.ParentOtherProject);
        WorkItemRules.ValidateNew(state, parent).ShouldBeRefusedWith(WorkItemRules.Codes.ParentOtherProject);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_title_is_refused_by_both_tiers(string title)
    {
        var state = State(title: title);

        WorkItem.Restore(state).ShouldBeRefusedWith(WorkItemRules.Codes.TitleRequired);
        WorkItemRules.CheckInvariants(state).ShouldBeRefusedWith(WorkItemRules.Codes.TitleRequired);
        WorkItemRules.ValidateNew(state).ShouldBeRefusedWith(WorkItemRules.Codes.TitleRequired);
    }

    [Fact]
    public void A_resolution_on_an_open_item_is_refused_by_both_tiers()
    {
        var state = State(status: WorkItemStatus.InProgress, resolution: WorkItemResolution.Completed);

        WorkItemRules.CheckInvariants(state).ShouldBeRefusedWith(WorkItemRules.Codes.ResolutionOnOpenItem);
        WorkItemRules.ValidateNew(state).ShouldBeRefusedWith(WorkItemRules.Codes.ResolutionOnOpenItem);
    }

    [Fact]
    public void A_tier_whose_parent_is_a_task_is_refused_by_both_tiers()
    {
        var task = Item(WorkItemLevel.Task);
        var state = State(WorkItemLevel.Tier, parent: task.Id);

        WorkItemRules.CheckInvariants(state, task).ShouldBeRefusedWith(WorkItemRules.Codes.ParentLevelNotHigher);
        WorkItemRules.ValidateNew(state, task).ShouldBeRefusedWith(WorkItemRules.Codes.ParentLevelNotHigher);
    }

    [Fact]
    public void A_link_across_projects_and_a_self_link_are_refused_by_both_tiers()
    {
        var here = Item();
        var there = Item(project: new ProjectId("elsewhere"));

        WorkItemRules.CheckLinkInvariants(here, WorkItemLinkType.Relates, there).ShouldBeRefusedWith(WorkItemRules.Codes.LinkOtherProject);
        WorkItemRules.ValidateLink(here, WorkItemLinkType.Relates, there, []).ShouldBeRefusedWith(WorkItemRules.Codes.LinkOtherProject);
        WorkItemRules.CheckLinkInvariants(here, WorkItemLinkType.Blocks, here).ShouldBeRefusedWith(WorkItemRules.Codes.LinkSelf);
        WorkItemRules.ValidateLink(here, WorkItemLinkType.Blocks, here, []).ShouldBeRefusedWith(WorkItemRules.Codes.LinkSelf);
    }

    [Fact]
    public void Every_refusal_the_authoring_tier_adds_over_the_invariants_is_an_authoring_code()
    {
        // The mirrored shapes above, collected: whatever the authoring check says beyond the invariant check
        // must carry an authoring code, or a provider write would be refused by a rule it never had to meet.
        var feature = Item(WorkItemLevel.Feature);
        var shapes = new (WorkItemState State, WorkItem? Parent)[]
        {
            (State(status: WorkItemStatus.Cancelled), null),
            (State(flags: new WorkItemFlags(true, null, false, null)), null),
            (State(status: WorkItemStatus.Cancelled, resolution: WorkItemResolution.Duplicate), null),
            (State(WorkItemLevel.Task, parent: feature.Id), feature),
            (State(WorkItemLevel.Feature, WorkItemKind.Bug), null),
            (State(description: " "), null),
            (State(status: WorkItemStatus.Done, resolution: WorkItemResolution.Completed, flags: WorkItemFlags.None.WithBlocked("x")), null),
        };

        foreach (var (state, parent) in shapes)
        {
            WorkItemRules.CheckInvariants(state, parent).IsValid.Should().BeTrue();
            var authored = WorkItemRules.ValidateNew(state, parent);
            authored.IsValid.Should().BeFalse();
            authored.Errors.Should().OnlyContain(e => WorkItemRules.TierOf(e.Code) == RuleTier.Authoring);
        }
    }
}
