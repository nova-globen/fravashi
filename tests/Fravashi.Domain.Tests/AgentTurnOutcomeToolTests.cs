using AwesomeAssertions;
using Fravashi.Domain.AgentSessions;
using Xunit;

namespace Fravashi.Domain.Tests;

/// <summary>
/// ADR 0022's terminating document, seen from the approval gate. The obvious way to stop
/// <c>StructuredOutput</c> raising an approval on every single turn was to add it to the read-only
/// allowlist, and that would have been wrong twice over: it is not a read, and that list's only value is
/// how narrow it is. These tests hold the shape of the alternative, which is that the outcome tool is its
/// own thing and matches nothing else.
/// </summary>
public class AgentTurnOutcomeToolTests
{
    [Fact]
    public void The_outcome_tool_is_the_name_the_cli_really_sends() =>
        AgentTurnOutcomeTool.Is(AgentTurnOutcomeTool.Name).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("structuredoutput")]
    [InlineData("STRUCTUREDOUTPUT")]
    [InlineData("StructuredOutput ")]
    [InlineData(" StructuredOutput")]
    [InlineData("StructuredOutput2")]
    [InlineData("mcp__notes__StructuredOutput")]
    [InlineData("Write")]
    [InlineData("Bash")]
    public void Anything_that_is_merely_close_to_it_is_a_different_tool(string? name)
    {
        // Ordinal and exact, because this is the one name answered without asking the user. A tool the
        // studio knows nothing about must not inherit that silence by resembling it: an MCP server can
        // publish any name it likes, and a case-insensitive match would hand it the same free pass.
        AgentTurnOutcomeTool.Is(name).Should().BeFalse();
    }

    [Fact]
    public void It_is_deliberately_not_on_the_read_only_allowlist()
    {
        // The guard against the easy fix. AgentToolSafety is about WHERE a read lands and every entry
        // declares its path fields so AgentReadScope can contain it; the outcome tool reads nothing and
        // names no location, so putting it there would both describe it wrongly and widen the one list in
        // this codebase whose whole job is to stay small.
        AgentToolSafety.IsReadOnly(AgentTurnOutcomeTool.Name).Should().BeFalse();
        AgentToolSafety.ReadOnlyTools.Should().NotContain(AgentTurnOutcomeTool.Name);
        AgentToolSafety.TargetOf(AgentTurnOutcomeTool.Name).Should().BeNull();
    }

    [Fact]
    public void The_read_only_allowlist_still_holds_exactly_the_tools_it_did()
    {
        // A second pair of eyes on "widened nothing else": the whole set, spelled out. A tool added here
        // is a deliberate act with a test to change, not a line that slips in beside an unrelated fix.
        AgentToolSafety.ReadOnlyTools.Should().BeEquivalentTo(
            ["Read", "NotebookRead", "LS", "Glob", "Grep", "ToolSearch", "TaskGet", "TaskList", "TaskOutput"]);
    }
}
