using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Tests.AgentRuntimes;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Work item #357: the default models and effort as the settings table holds them. Only the closed sets ever come
/// back out, so a hand-edited row can never put a free string on an agent's command line.
/// </summary>
public sealed class AgentDefaultsSettingTests
{
    [Fact]
    public void An_install_that_set_nothing_reads_as_default_everywhere()
    {
        AgentDefaultsSetting.Read(new FakeConfigurationStore()).Should().Be(AgentDefaults.None);
    }

    [Fact]
    public void What_is_written_is_read_back()
    {
        var config = new FakeConfigurationStore();
        var defaults = new AgentDefaults(
            new AgentDefault("sonnet", AgentEffort.High),
            new AgentDefault("claude-haiku-4-5", null),
            new AgentDefault(null, AgentEffort.Max));

        AgentDefaultsSetting.Write(config, defaults);

        AgentDefaultsSetting.Read(config).Should().Be(defaults);
        config.Values[AgentDefaultsSetting.NewConversationEffortKey].Should().Be("high", "the CLI's own word is stored");
        config.Values.Should().NotContainKey(AgentDefaultsSetting.StudioAssistantEffortKey, "Default is no row at all");
        config.Values.Should().NotContainKey(AgentDefaultsSetting.AutopilotBuilderModelKey);
    }

    [Theory]
    [InlineData("gpt-5", null)]
    [InlineData("default", null)]
    [InlineData("  ", null)]
    [InlineData(" opus ", "opus")]
    [InlineData("claude-opus-5-5", "claude-opus-5-5")]
    public void Only_a_model_the_session_menu_offers_is_read(string stored, string? expected)
    {
        var config = new FakeConfigurationStore();
        config.Set(AgentDefaultsSetting.NewConversationModelKey, stored);

        AgentDefaultsSetting.Read(config).NewConversation.Model.Should().Be(expected);
    }

    [Theory]
    [InlineData("ultra")]
    [InlineData("High")]
    [InlineData("")]
    public void An_effort_outside_the_closed_set_reads_as_default(string stored)
    {
        var config = new FakeConfigurationStore();
        config.Set(AgentDefaultsSetting.AutopilotBuilderEffortKey, stored);

        AgentDefaultsSetting.Read(config).AutopilotBuilder.Effort.Should().BeNull();
    }

    [Fact]
    public void Every_default_model_is_one_the_session_model_menu_offers()
    {
        AgentDefaultsSetting.ModelIds.Should().NotContain("default");
        foreach (var alias in AgentModelAliases.All)
        {
            AgentDefaultsSetting.ModelIds.Should().Contain(alias.CliId(), "a work item's suggestion is always a choice here too");
        }
    }
}
