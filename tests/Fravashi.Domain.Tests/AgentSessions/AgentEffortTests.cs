using AwesomeAssertions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.WorkItems;
using Xunit;

namespace Fravashi.Domain.Tests.AgentSessions;

/// <summary>Requested 2026-10-22 (model and effort per work item): the closed sets that become CLI flags.</summary>
public class AgentEffortTests
{
    [Fact]
    public void The_effort_levels_are_exactly_the_ones_the_pinned_cli_documents_lowest_first()
    {
        // Read from the pinned 2.1.280's own --help: "--effort <level>" with low, medium, high, xhigh, max.
        AgentEfforts.All.Select(e => e.CliValue()).Should().Equal("low", "medium", "high", "xhigh", "max");
        AgentEfforts.All.Should().BeEquivalentTo(Enum.GetValues<AgentEffort>());
    }

    [Theory]
    [InlineData("low", AgentEffort.Low)]
    [InlineData("xhigh", AgentEffort.XHigh)]
    [InlineData("max", AgentEffort.Max)]
    public void A_cli_value_reads_back_as_its_level(string value, AgentEffort effort)
    {
        AgentEfforts.Parse(value).Should().Be(effort);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("High")]
    [InlineData("ultra")]
    [InlineData("high; rm -rf /")]
    public void Anything_else_is_default_never_a_guessed_level(string? value)
    {
        AgentEfforts.Parse(value).Should().BeNull();
    }

    [Fact]
    public void An_undefined_level_never_reaches_the_command_line()
    {
        var act = () => ((AgentEffort)42).CliValue();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void The_model_aliases_are_the_three_families_and_read_back_exactly()
    {
        AgentModelAliases.All.Select(a => a.CliId()).Should().Equal("haiku", "sonnet", "opus");
        AgentModelAliases.Parse("opus").Should().Be(AgentModelAlias.Opus);
        AgentModelAliases.Parse("claude-opus-5-5").Should().BeNull("a dated id is not one of the suggestable aliases");
    }

    [Fact]
    public void A_work_item_holding_an_undefined_suggestion_cannot_exist()
    {
        var state = new WorkItemState
        {
            Id = new WorkItemId("i-1"),
            Project = Projects.ProjectId.Default,
            Number = 1,
            Level = WorkItemLevel.Task,
            Title = "Suggests nonsense",
            SuggestedEffort = (AgentEffort)42,
        };

        var restored = WorkItem.Restore(state);
        restored.IsValid.Should().BeFalse();
        restored.Errors.Should().ContainSingle(e => e.Field == "suggestedEffort");
    }
}
