using AwesomeAssertions;
using Fravashi.Application.Sessions.Tools;
using Xunit;

namespace Fravashi.Application.Tests.Sessions;

/// <summary>
/// Work item #356: no mcp__ text where a person reads it. A studio tool is named in words, another MCP server's tool as
/// "tool (server)", and a built-in keeps its own name.
/// </summary>
public sealed class StudioToolDisplayNameTests
{
    [Theory]
    [InlineData("mcp__fravashi__work_item_update", "work item update")]
    [InlineData("mcp__other__do_thing", "do thing (other)")]
    [InlineData("Bash", "Bash")]
    [InlineData("Edit", "Edit")]
    [InlineData("", "a tool")]
    public void A_tool_is_named_in_words(string toolName, string expected) =>
        StudioToolDescriptions.DisplayName(toolName).Should().Be(expected);

    [Fact]
    public void A_waiting_studio_call_reads_as_its_approval_sentence_and_a_built_in_as_its_name()
    {
        StudioToolDescriptions.ForPeople(
                "mcp__fravashi__work_item_update",
                new Dictionary<string, string> { ["number"] = "23", ["description"] = "x" })
            .Should().Be("Update #23: description");
        StudioToolDescriptions.ForPeople("Bash", new Dictionary<string, string> { ["command"] = "ls" })
            .Should().Be("Bash");
    }

    [Fact]
    public void Stored_text_with_a_raw_name_is_put_into_words()
    {
        var text = StudioToolDescriptions.Humanize(
            "The building session is waiting on a person to approve mcp__fravashi__work_item_update (\"x\").");

        text.Should().NotContain("mcp__");
        text.Should().Contain("\"work item update\"");
        StudioToolDescriptions.Humanize("approve Bash (\"ls\")").Should().Be("approve Bash (\"ls\")");
    }
}
