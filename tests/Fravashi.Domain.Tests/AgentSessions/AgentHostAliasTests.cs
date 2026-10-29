using AwesomeAssertions;
using Fravashi.Domain.AgentSessions;
using Xunit;

namespace Fravashi.Domain.Tests.AgentSessions;

/// <summary>Backlog 187: an argument naming wslc's host alias is an alarm on the approval, whatever the tool.</summary>
public sealed class AgentHostAliasTests
{
    private static Dictionary<string, string> Call(string field, string argument) => new() { [field] = argument };

    [Theory]
    [InlineData("command", "curl http://host.wslc.internal:5432/", "host.wslc.internal")]
    [InlineData("command", "adb connect 169.254.73.254:5037", "169.254.73.254")]
    [InlineData("url", "https://HOST.WSLC.Internal/admin", "host.wslc.internal")]
    [InlineData("prompt", "Check whether the dev server at 169.254.73.254 answers.", "169.254.73.254")]
    public void Bash_webfetch_and_agent_arguments_naming_the_alias_raise_a_loud_line(
        string field, string argument, string spelling)
    {
        var effects = AgentHostAlias.Naming(Call(field, argument));

        effects.Should().ContainSingle();
        effects[0].Field.Should().Be(field);
        effects[0].Value.Should().Be(spelling);
        effects[0].Weight.Should().Be(AgentEffectWeight.Alarm);
        effects[0].Notice.Should().Contain("SERVICE ON THIS PC").And.Contain("wslc's host alias");
    }

    [Fact]
    public void Each_field_that_names_it_is_named()
    {
        var input = new Dictionary<string, string>
        {
            ["description"] = "probe host.wslc.internal",
            ["prompt"] = "curl 169.254.73.254",
            ["subagent_type"] = "general-purpose",
        };

        AgentHostAlias.Naming(input).Select(e => e.Field).Should().BeEquivalentTo(["description", "prompt"]);
    }

    [Theory]
    [InlineData("url", "https://learn.microsoft.com/dotnet/")]
    [InlineData("command", "curl http://169.254.169.254/latest/meta-data/")]
    [InlineData("command", "dotnet test")]
    public void An_ordinary_argument_raises_nothing(string field, string argument) =>
        AgentHostAlias.Naming(Call(field, argument)).Should().BeEmpty();

    private const string Prefix = AgentWorkGate.StudioToolPrefix;

    [Theory]
    [InlineData("work_item_create")]
    [InlineData("work_item_update")]
    [InlineData("work_item_transition")]
    [InlineData("work_item_link")]
    [InlineData("work_item_evidence")]
    [InlineData("work_item_read")]
    [InlineData("work_items_find")]
    [InlineData("work_items_startable")]
    [InlineData("wiki_find")]
    [InlineData("wiki_read")]
    [InlineData("wiki_write")]
    [InlineData("wiki_link")]
    public void A_studio_graph_text_tool_naming_the_alias_raises_nothing(string tool)
    {
        AgentHostAlias.IsExempt(Prefix + tool).Should().BeTrue();
        AgentHostAlias.Naming(Prefix + tool, Call("description", $"probe {AgentHostAlias.Name}"))
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("work_item_scenario_run", "it executes a command rather than only storing text")]
    [InlineData(
        "work_item_scenario_write",
        "its command field is the authoring half of an execution work_item_scenario_run later carries out, "
        + "and the write's own card is the only place a person ever sees that text")]
    [InlineData("Bash", "it is not a studio graph tool at all")]
    [InlineData("WebFetch", "it is not a studio graph tool at all")]
    [InlineData("browser_open", "it is not prefixed, so it is not the real studio tool name")]
    [InlineData("mcp__other__work_item_update", "another server's tool is not this studio's")]
    [InlineData("mcp__fravashi__WORK_ITEM_UPDATE", "matched ordinally and exactly, case included")]
    public void A_tool_that_is_not_on_the_exempt_list_still_raises_the_alarm(string toolName, string why)
    {
        AgentHostAlias.IsExempt(toolName).Should().BeFalse(why);
        AgentHostAlias.Naming(toolName, Call("description", $"probe {AgentHostAlias.Name}"))
            .Should().ContainSingle(why);
    }

    [Fact]
    public void The_exempt_tool_name_naming_it_still_raises_when_misspelled_without_the_prefix()
    {
        // "work_item_update" without the mcp__fravashi__ prefix is a name nobody sends; proving the broker is
        // exempted on the REAL form, not this one, is the point of backlog 191's own warning.
        AgentHostAlias.IsExempt("work_item_update").Should().BeFalse();
        AgentHostAlias.Naming("work_item_update", Call("description", $"probe {AgentHostAlias.Name}"))
            .Should().ContainSingle();
    }

    [Fact]
    public void The_exempt_list_is_closed_and_exact()
    {
        AgentHostAlias.ExemptTools.Should().BeEquivalentTo(
        [
            Prefix + "work_item_create",
            Prefix + "work_item_update",
            Prefix + "work_item_transition",
            Prefix + "work_item_link",
            Prefix + "work_item_evidence",
            Prefix + "work_item_read",
            Prefix + "work_items_find",
            Prefix + "work_items_startable",
            Prefix + "wiki_find",
            Prefix + "wiki_read",
            Prefix + "wiki_write",
            Prefix + "wiki_link",
        ]);

        AgentHostAlias.ExemptTools.Should().NotContain(
            Prefix + "work_item_scenario_run", "it executes a command and can reach outside the graph");

        AgentHostAlias.ExemptTools.Should().NotContain(
            Prefix + "work_item_scenario_write",
            "its command field is authored here and executed later by work_item_scenario_run, which never "
            + "shows that text on its own approval card");
    }
}
