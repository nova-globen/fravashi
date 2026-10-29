using Bunit;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Web.Components.Sessions;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Follow-up filed 2026-10-22 (approval card with no arguments): a call that reaches a person carrying no arguments
/// says so in words, instead of a card with a tool name and nothing under it that reads like one hiding its fields.
/// </summary>
public class ApprovalPromptNoArgumentsTests
{
    private static PendingToolApproval Request(Dictionary<string, string> input) => new(
        "r1",
        AgentSessionId.New(),
        "mcp__fravashi__studio_upgrade",
        string.Empty,
        "t1",
        input,
        DateTimeOffset.UtcNow);

    [Fact]
    public void A_call_with_no_arguments_says_so_on_the_card()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<ApprovalPrompt>(p => p.Add(c => c.Request, Request([])));

        Assert.Equal("This call carries no arguments.",
            cut.Find("[data-testid=approval-no-arguments]").TextContent.Trim());
        Assert.NotEmpty(cut.FindAll("[data-testid=approve]"));
    }

    [Fact]
    public void A_call_with_arguments_does_not_say_it_has_none()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<ApprovalPrompt>(p => p.Add(c => c.Request, Request(new() { ["version"] = "0.9.92" })));

        Assert.Empty(cut.FindAll("[data-testid=approval-no-arguments]"));
    }
}
