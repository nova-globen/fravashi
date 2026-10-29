using Bunit;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Web.Components.Sessions;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 220, the card: Deny can carry the person's reason, typed in a field that Deny and Enter send and
/// Approve never reads.
/// </summary>
public class ApprovalReasonTests
{
    private static PendingToolApproval Request() => new(
        "req-1",
        AgentSessionId.New(),
        "mcp__fravashi__git_commit",
        "feat: something",
        "toolu_1",
        new Dictionary<string, string> { ["message"] = "feat: something" },
        DateTimeOffset.UtcNow);

    private static (IRenderedComponent<ApprovalPrompt> Cut, List<(string RequestId, bool Approved, string? Reason)> Answers)
        Card(BunitContext ctx)
    {
        var answers = new List<(string RequestId, bool Approved, string? Reason)>();
        var cut = ctx.Render<ApprovalPrompt>(p => p
            .Add(c => c.Request, Request())
            .Add(c => c.Answer, EventCallback.Factory.Create<(string, bool, string?)>(new object(), answers.Add)));
        return (cut, answers);
    }

    [Fact]
    public void Deny_sends_the_typed_reason()
    {
        using var ctx = new BunitContext();
        var (cut, answers) = Card(ctx);

        cut.Find("[data-testid=deny-reason]").Input("the message needs a scope");
        cut.Find("[data-testid=deny]").Click();

        Assert.Equal([("req-1", false, (string?)"the message needs a scope")], answers);
    }

    [Fact]
    public void Enter_in_the_reason_field_denies_with_it()
    {
        using var ctx = new BunitContext();
        var (cut, answers) = Card(ctx);

        var field = cut.Find("[data-testid=deny-reason]");
        field.Input("split it in two");
        cut.Find("[data-testid=deny-reason]").KeyDown(key: "Enter");

        Assert.Equal([("req-1", false, (string?)"split it in two")], answers);
    }

    [Fact]
    public void Another_key_in_the_reason_field_answers_nothing()
    {
        using var ctx = new BunitContext();
        var (cut, answers) = Card(ctx);

        cut.Find("[data-testid=deny-reason]").KeyDown(key: "a");

        Assert.Empty(answers);
    }

    [Fact]
    public void Approve_is_unchanged_and_never_sends_the_reason()
    {
        using var ctx = new BunitContext();
        var (cut, answers) = Card(ctx);

        cut.Find("[data-testid=deny-reason]").Input("typed, then changed my mind");
        cut.Find("[data-testid=approve]").Click();

        Assert.Equal([("req-1", true, (string?)null)], answers);
    }

    [Fact]
    public void Deny_with_nothing_typed_sends_no_reason()
    {
        using var ctx = new BunitContext();
        var (cut, answers) = Card(ctx);

        cut.Find("[data-testid=deny]").Click();

        Assert.Single(answers);
        Assert.False(answers[0].Approved);
        Assert.True(string.IsNullOrEmpty(answers[0].Reason));
    }

    [Fact]
    public void The_reason_field_is_a_keyboard_reachable_text_input_capped_at_the_servers_limit()
    {
        using var ctx = new BunitContext();
        var (cut, _) = Card(ctx);

        var field = cut.Find("[data-testid=deny-reason]");

        Assert.Equal("input", field.TagName, ignoreCase: true);
        Assert.Equal("text", field.GetAttribute("type"));
        Assert.Null(field.GetAttribute("tabindex"));
        Assert.Equal(ToolApprovalDecision.MaxUserReasonLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            field.GetAttribute("maxlength"));
    }
}
