using AwesomeAssertions;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;
using Xunit;

namespace Fravashi.Domain.Tests.WorkItems;

/// <summary>
/// The authoring rules of the agent layer (canonical artifact model plan, increment C1). They are authoring
/// rather than invariant on purpose (decision D12): a provider's data must always be able to land, and no
/// external tracker enforces a single mockup, a 0 to 100 score or a workspace-relative glob. So each of these
/// refuses a change Fravashi authors, while the same shape restores from a row without complaint.
/// </summary>
public class AgentLayerRulesTests
{
    private static WorkItemState Proposed(Action<WorkItemState>? _ = null) => new()
    {
        Id = WorkItemId.New(),
        Project = new ProjectId("default"),
        Number = 1,
        Level = WorkItemLevel.Increment,
        Title = "Paste an image into the chat",
        Description = "An image pasted into the composer reaches the agent.",
        CreatedUtc = DateTimeOffset.UnixEpoch,
        UpdatedUtc = DateTimeOffset.UnixEpoch,
    };

    private static ArtifactRef Ref(ArtifactRefRole role, string target = "wiki:design-42") =>
        new(AgentLayerEntry.NewId(), role, ArtifactRefKind.Wiki, target);

    [Fact]
    public void A_second_reference_in_a_single_value_role_is_refused()
    {
        var proposed = Proposed() with { Refs = [Ref(ArtifactRefRole.Mockup), Ref(ArtifactRefRole.Mockup, "wiki:other")] };

        var result = WorkItemRules.ValidateNew(proposed);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Code == WorkItemRules.Codes.RefRoleNotRepeatable);
    }

    [Fact]
    public void Many_context_references_are_allowed_because_a_bundle_is_a_list()
    {
        var proposed = Proposed() with
        {
            Refs = [Ref(ArtifactRefRole.Context, "adr:0022"), Ref(ArtifactRefRole.Context, "adr:0023")],
        };

        WorkItemRules.ValidateNew(proposed).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void An_ambiguity_score_outside_the_scale_is_refused(int score)
    {
        var proposed = Proposed() with { Ambiguity = new AmbiguityAssessment(score, "owner", DateTimeOffset.UnixEpoch) };

        var result = WorkItemRules.ValidateNew(proposed);

        result.Errors.Should().ContainSingle(e => e.Code == WorkItemRules.Codes.AmbiguityOutOfRange);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\secrets\\keys.txt")]
    [InlineData("..\\..\\outside\\**")]
    [InlineData("src/../../elsewhere/*.cs")]
    public void A_scope_pattern_that_is_not_relative_to_the_workspace_root_is_refused(string pattern)
    {
        var proposed = Proposed() with
        {
            ScopePatterns = [new ScopePattern(AgentLayerEntry.NewId(), ScopeMode.Allowed, pattern)],
        };

        var result = WorkItemRules.ValidateNew(proposed);

        result.Errors.Should().ContainSingle(e => e.Code == WorkItemRules.Codes.ScopePatternNotRelative);
    }

    [Theory]
    [InlineData("src/**/*.cs")]
    [InlineData("docs/plans/*.md")]
    [InlineData("tests/Fravashi.Web.Tests/**")]
    public void A_workspace_relative_pattern_is_accepted(string pattern)
    {
        var proposed = Proposed() with
        {
            ScopePatterns = [new ScopePattern(AgentLayerEntry.NewId(), ScopeMode.Forbidden, pattern)],
        };

        WorkItemRules.ValidateNew(proposed).IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_change_budget_of_no_files_is_refused_because_no_attempt_could_satisfy_it()
    {
        var proposed = Proposed() with { ChangeBudget = new ChangeBudget(0) };

        WorkItemRules.ValidateNew(proposed).Errors
            .Should().ContainSingle(e => e.Code == WorkItemRules.Codes.ChangeBudgetNotPositive);
    }

    [Fact]
    public void A_blank_criterion_readiness_line_or_scenario_step_is_refused()
    {
        var blankCriterion = Proposed() with
        {
            AcceptanceCriteria = [new AcceptanceCriterion(AgentLayerEntry.NewId(), "  ")],
        };
        var blankStep = Proposed() with
        {
            Scenarios =
            [
                new TestScenario(
                    AgentLayerEntry.NewId(),
                    "Paste an image",
                    [new ScenarioStep(AgentLayerEntry.NewId(), "paste", "   ")]),
            ],
        };

        WorkItemRules.ValidateNew(blankCriterion).Errors
            .Should().ContainSingle(e => e.Code == WorkItemRules.Codes.AgentLayerTextRequired);
        WorkItemRules.ValidateNew(blankStep).Errors
            .Should().ContainSingle(e => e.Code == WorkItemRules.Codes.AgentLayerTextRequired);
    }

    [Fact]
    public void A_person_blocker_without_a_person_or_a_reason_is_refused()
    {
        var proposed = Proposed() with
        {
            PersonBlockers = [new PersonBlocker(AgentLayerEntry.NewId(), "owner", "  ", DateTimeOffset.UnixEpoch, null)],
        };

        WorkItemRules.ValidateNew(proposed).Errors
            .Should().ContainSingle(e => e.Code == WorkItemRules.Codes.PersonBlockerIncomplete);
    }

    [Fact]
    public void A_mirrored_shape_still_restores_because_these_are_authoring_rules_not_invariants()
    {
        // The same state a provider might send: two mockups, a score nobody bounded, an absolute path. It is
        // refused when Fravashi authors it and accepted when a row is read back, which is decision D12's
        // whole point: a provider's data must always be able to land.
        var mirrored = Proposed() with
        {
            Refs = [Ref(ArtifactRefRole.Mockup), Ref(ArtifactRefRole.Mockup, "wiki:other")],
            Ambiguity = new AmbiguityAssessment(999, "some tracker", null),
            ScopePatterns = [new ScopePattern(AgentLayerEntry.NewId(), ScopeMode.Allowed, "/absolute/path")],
        };

        WorkItemRules.ValidateNew(mirrored).IsValid.Should().BeFalse();
        WorkItem.Restore(mirrored).IsValid.Should().BeTrue();
    }

    [Fact]
    public void The_aggregate_answers_which_reference_holds_a_single_value_role()
    {
        var mockup = Ref(ArtifactRefRole.Mockup, "design:home-v3");
        var item = WorkItem.Restore(Proposed() with { Refs = [Ref(ArtifactRefRole.Context), mockup] }).Value;

        item.RefFor(ArtifactRefRole.Mockup).Should().Be(mockup);
        item.RefFor(ArtifactRefRole.ApprovedDesign).Should().BeNull();
    }

    [Fact]
    public void Only_the_uncleared_person_blockers_count_as_still_waiting()
    {
        var waiting = new PersonBlocker(AgentLayerEntry.NewId(), "owner", "the visual spec", DateTimeOffset.UnixEpoch, null);
        var cleared = new PersonBlocker(
            AgentLayerEntry.NewId(), "owner", "the port registration", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1));
        var item = WorkItem.Restore(Proposed() with { PersonBlockers = [waiting, cleared] }).Value;

        item.PersonBlockers.Should().HaveCount(2, "a cleared blocker is history, not a deletion");
        item.ActivePersonBlockers.Should().ContainSingle().Which.Should().Be(waiting);
    }
}
