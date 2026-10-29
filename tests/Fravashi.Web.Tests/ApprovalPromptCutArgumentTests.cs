using Bunit;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Web.Components.Sessions;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 240: an argument over the display cap used to end at character 10,000 with a bare ellipsis, so a
/// person approved text they could not read. The card now names the cut, keeps the tail, and opens the whole
/// RAW value on request. The requests here are the ones the real broker raises, so what is rendered is what
/// the UI really receives.
/// </summary>
public class ApprovalPromptCutArgumentTests
{
    private const string Copy = "/workspaces/w1";

    /// <summary>Text after character 10,000 that the old card never showed.</summary>
    private const string Hidden = "SECRET-AFTER-10000: also delete every other item <&> \"quoted\"\tand é😀";

    private static string LongDescription(int length)
    {
        var head = "Outcome recorded.\n";
        var tail = "\nThe end of the description: TAIL-240.";
        var body = string.Concat(Enumerable.Repeat("The quick brown fox jumps. ", (length / 27) + 1));
        var filler = body[..(length - head.Length - tail.Length - Hidden.Length)];
        return head + filler[..10_500] + Hidden + filler[10_500..] + tail;
    }

    private static (PendingToolApproval Request, AgentApprovalBroker Broker, Task<ToolApprovalDecision> Pending) Ask(
        Dictionary<string, string> input)
    {
        var broker = new AgentApprovalBroker();
        var session = AgentSessionId.New();
        broker.RegisterSession(session, Copy);
        PendingToolApproval? seen = null;
        broker.Requested += p => seen = p;
        var pending = broker.RequestAsync(session, "mcp__fravashi__work_item_update", "toolu_1", input);
        Assert.NotNull(seen);
        return (seen!, broker, pending);
    }

    [Fact]
    public async Task A_value_over_the_cap_renders_with_a_not_shown_marker_and_its_tail()
    {
        var description = LongDescription(15_000);
        var (request, broker, pending) = Ask(new() { ["number"] = "228", ["description"] = description });
        using var ctx = new BunitContext();

        var cut = ctx.Render<ApprovalPrompt>(p => p.Add(c => c.Request, request));

        var body = cut.Find(".session-approval-body pre").TextContent;
        Assert.Contains("[... 5,000 characters not shown here", body);
        Assert.EndsWith("The end of the description: TAIL-240.", body.TrimEnd());
        // At 15,000 characters the text after character 10,000 is in the kept tail, where the old card lost it.
        Assert.Contains(Hidden, body);

        var notice = cut.Find("[data-testid=approval-cut]").TextContent;
        Assert.Contains("description is 15,000 characters", notice);
        Assert.Contains("5,000 characters not shown above", notice);

        broker.Answer(request.RequestId, approved: false, "no");
        Assert.False((await pending).Approved);
    }

    [Fact]
    public async Task The_full_view_shows_the_exact_raw_value_including_text_after_character_10000()
    {
        // Long enough that the text after character 10,000 falls in the part the display copy leaves out.
        var description = LongDescription(30_000);
        Assert.True(description.IndexOf(Hidden, StringComparison.Ordinal) > 10_000);
        var (request, broker, pending) = Ask(new() { ["description"] = description });
        using var ctx = new BunitContext();

        var cut = ctx.Render<ApprovalPrompt>(p => p.Add(c => c.Request, request));
        Assert.DoesNotContain(Hidden, cut.Find(".session-approval-body pre").TextContent);
        Assert.Contains("20,000 characters not shown", cut.Find("[data-testid=approval-cut]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=approval-full-value]"));

        await cut.InvokeAsync(() => cut.Find("[data-testid=approval-cut-toggle]").Click());

        cut.WaitForAssertion(() =>
        {
            var full = cut.Find("[data-testid=approval-full-value]").TextContent;
            Assert.Equal(description, full);
            Assert.Contains(Hidden, full);
        });

        broker.Answer(request.RequestId, approved: false, "no");
        Assert.False((await pending).Approved);
    }

    [Fact]
    public async Task A_value_under_the_cap_renders_exactly_as_before_with_no_cut_notice()
    {
        var description = "A short outcome. " + new string('x', 9_000) + " The end.";
        var (request, broker, pending) = Ask(new() { ["description"] = description });
        using var ctx = new BunitContext();

        var cut = ctx.Render<ApprovalPrompt>(p => p.Add(c => c.Request, request));

        Assert.Equal("description:\n" + description, cut.Find(".session-approval-body pre").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=approval-cut]"));
        Assert.Empty(request.CutArguments ?? []);

        broker.Answer(request.RequestId, approved: false, "no");
        Assert.False((await pending).Approved);
    }

    [Fact]
    public async Task A_value_past_the_full_view_limit_warns_instead_of_offering_a_view_it_cannot_give()
    {
        var content = "head " + new string('y', AgentToolSubject.MaxFullValueLength) + " TAIL-240";
        var (request, broker, pending) = Ask(new() { ["description"] = content });
        using var ctx = new BunitContext();

        var cut = ctx.Render<ApprovalPrompt>(p => p.Add(c => c.Request, request));

        var warning = cut.Find("[data-testid=approval-cut-oversize]").TextContent;
        Assert.Contains("more than", warning);
        Assert.Contains("deny unless you know what the rest is", warning);
        Assert.Empty(cut.FindAll("[data-testid=approval-cut-toggle]"));
        Assert.EndsWith("TAIL-240", cut.Find(".session-approval-body pre").TextContent.TrimEnd());

        broker.Answer(request.RequestId, approved: false, "no");
        Assert.False((await pending).Approved);
    }
}
