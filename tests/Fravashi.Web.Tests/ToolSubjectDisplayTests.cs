using Bunit;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Web.Components.Sessions;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 103, the display half: the store now carries the RAW subject, so shortening happens at
/// render time, per <see cref="AgentToolSubject.Shorten"/>'s contract (head AND tail survive, the
/// cut says how much went missing). These drive the two leaf components that show a subject.
/// </summary>
public class ToolSubjectDisplayTests
{
    [Fact]
    public void A_raw_500_char_command_renders_shortened_with_head_and_tail()
    {
        using var ctx = new BunitContext();
        var raw = "head-marker " + new string('x', 480) + " tail-marker";

        var cut = ctx.Render<ToolCallCard>(p => p
            .Add(c => c.ToolName, "Bash")
            .Add(c => c.Detail, raw)
            .Add(c => c.Success, true));

        var shown = cut.Find(".session-tool-arg").TextContent;
        Assert.True(shown.Length <= AgentToolSubject.MaxLength, $"rendered subject was {shown.Length} chars");
        Assert.StartsWith("head-marker", shown);
        Assert.EndsWith("tail-marker", shown);
        Assert.Contains("more]", shown);
    }

    [Fact]
    public void A_raw_padded_path_renders_with_both_ends_so_the_cut_cannot_disguise_the_destination()
    {
        using var ctx = new BunitContext();
        var padded = "/workspaces/w1/src" + string.Concat(Enumerable.Repeat("/aaaa", 60))
            + string.Concat(Enumerable.Repeat("/..", 63)) + "/data/keys/key-8c2f.xml";

        var cut = ctx.Render<ToolCallCard>(p => p
            .Add(c => c.ToolName, "Write")
            .Add(c => c.Detail, padded)
            .Add(c => c.Success, true));

        var shown = cut.Find(".session-tool-arg").TextContent;
        Assert.True(shown.Length <= AgentToolSubject.MaxLength);
        Assert.StartsWith("/workspaces/w1/src", shown);
        Assert.EndsWith("key-8c2f.xml", shown);
    }

    [Fact]
    public void The_approval_prompt_shows_a_shortened_subject_derived_from_the_raw_detail()
    {
        using var ctx = new BunitContext();
        var padded = "/workspaces/w1/src" + string.Concat(Enumerable.Repeat("/aaaa", 60))
            + string.Concat(Enumerable.Repeat("/..", 63)) + "/data/keys/key-8c2f.xml";
        var request = new PendingToolApproval(
            "req-1",
            AgentSessionId.New(),
            "Write",
            padded,
            "toolu_1",
            new Dictionary<string, string>(),
            DateTimeOffset.UtcNow);

        var cut = ctx.Render<ApprovalPrompt>(p => p.Add(c => c.Request, request));

        var shown = cut.Find(".session-approval-detail").TextContent;
        Assert.True(shown.Length <= AgentToolSubject.MaxLength, $"rendered subject was {shown.Length} chars");
        Assert.StartsWith("/workspaces/w1/src", shown);
        Assert.EndsWith("key-8c2f.xml", shown);
    }

    [Fact]
    public void A_call_with_no_content_argument_shows_every_argument_so_a_tracker_rewrite_can_be_read()
    {
        // Backlog 167: the second self-hosting trial's work_item_update carried a 4,500 character description and
        // the card showed only the capped subject line.
        using var ctx = new BunitContext();
        var description = "**Built 2026-09-29.** " + string.Concat(Enumerable.Repeat("The one place is StudioContainer. ", 120))
            + "Owed: a release.";
        var request = new PendingToolApproval(
            "req-2",
            AgentSessionId.New(),
            "mcp__fravashi__work_item_update",
            description,
            "toolu_2",
            new Dictionary<string, string> { ["number"] = "160", ["description"] = description, ["nextAction"] = "Release." },
            DateTimeOffset.UtcNow);

        var cut = ctx.Render<ApprovalPrompt>(p => p.Add(c => c.Request, request));

        var body = cut.Find(".session-approval-body pre").TextContent;
        Assert.Contains("description:\n" + description, body);
        Assert.Contains("nextAction:\nRelease.", body);
        Assert.Contains("number:\n160", body);
    }
}
