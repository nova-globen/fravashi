using Bunit;
using Fravashi.Domain.AgentSessions;
using Fravashi.Web.Components.Sessions;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 127 (decided 2026-09-21), at the surface: a gate refusal used to fill about fifteen lines of
/// a narrow panel with the paragraph written for the MODEL, tool names and all, and then say it again in the
/// grey note underneath. The card now says it once, shortly, in a person's voice.
/// </summary>
public sealed class GateRefusalVoiceTests
{
    [Fact]
    public void A_tool_card_whose_result_is_the_gates_refusal_says_it_shortly_and_not_in_the_models_words()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<ToolCallCard>(p => p
            .Add(c => c.ToolName, "Edit")
            .Add(c => c.Detail, "src/Greeter.cs")
            .Add(c => c.Success, false)
            .Add(c => c.Output, "PreToolUse:Edit hook blocked: " + AgentWorkGate.UnlinkedSentence));

        var shown = cut.Find("[data-testid=tool-call-output]").TextContent;
        Assert.Contains("not linked to a work item yet", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("mcp__fravashi__", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing to apologise for", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ordinary_result_is_shown_exactly_as_it_always_was()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<ToolCallCard>(p => p
            .Add(c => c.ToolName, "Bash")
            .Add(c => c.Detail, "ls")
            .Add(c => c.Success, false)
            .Add(c => c.Output, "ls: cannot access 'nope': No such file or directory"));

        Assert.Contains("cannot access 'nope'", cut.Find("[data-testid=tool-call-output]").TextContent, StringComparison.Ordinal);
    }
}
