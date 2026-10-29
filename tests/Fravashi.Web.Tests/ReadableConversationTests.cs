using Bunit;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Wiki;
using Fravashi.Infrastructure.Rendering;
using Fravashi.Web.Components.Sessions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Work item #351: a conversation a person can read. References are links found by the wiki's own parser (code
/// untouched), a studio tool's row says what it did with its raw name one click away, and an approval card says in one
/// sentence what the call will do while keeping everything it showed before.
/// </summary>
public class ReadableConversationTests
{
    // ---- references --------------------------------------------------------------------------------------

    [Fact]
    public void References_in_markdown_become_links_and_code_is_left_alone()
    {
        var linked = SessionReferences.LinkMarkdown(
            "Work item #343 created under @159, see !12.\n\nNot `#7` in code.\n\n```\n#8 in a block\n```\n");

        Assert.Contains("[#343](/workitems/343", linked, StringComparison.Ordinal);
        Assert.Contains("[@159](/wiki/159", linked, StringComparison.Ordinal);
        Assert.Contains("[!12](/git/pr/12", linked, StringComparison.Ordinal);
        Assert.Contains("`#7`", linked, StringComparison.Ordinal);
        Assert.Contains("\n#8 in a block\n", linked, StringComparison.Ordinal);
        Assert.DoesNotContain("/workitems/7", linked, StringComparison.Ordinal);
        Assert.DoesNotContain("/workitems/8", linked, StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_text_splits_into_words_and_references()
    {
        var pieces = SessionReferences.Split("Linked @159 to #343");

        Assert.Equal(["Linked ", "@159", " to ", "#343"], pieces.Select(p => p.Text));
        Assert.Equal(new WikiReference(WikiReferenceKind.WikiItem, 159), pieces[1].Reference);
        Assert.Null(pieces[0].Reference);
    }

    [Fact]
    public void Only_exactly_one_reference_parses()
    {
        Assert.Equal(new WikiReference(WikiReferenceKind.WorkItem, 343), SessionReferences.Parse(" #343 "));
        Assert.Null(SessionReferences.Parse("#343 and more"));
        Assert.Null(SessionReferences.Parse("abc#343"));
    }

    private static BunitContext Context()
    {
        var ctx = new BunitContext();
        ctx.Services.AddSingleton(new MarkdownPreviewRenderer());
        return ctx;
    }

    [Fact]
    public void An_agent_message_links_its_references_but_not_those_in_code()
    {
        using var ctx = Context();
        var cut = ctx.Render<ChatBubble>(p => p
            .Add(c => c.Role, TranscriptRole.Assistant)
            .Add(c => c.Text, "Work item #343 created. Run `git log #9` and:\n\n```\n#10\n```\n")
            .Add(c => c.Timestamp, DateTimeOffset.UtcNow));

        var links = cut.FindAll("a.wiki-ref");
        Assert.Single(links);
        Assert.Equal("#343", links[0].TextContent);
        Assert.Equal("/workitems/343", links[0].GetAttribute("href"));
        Assert.Contains("#10", cut.Find("[data-testid=code-block]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void A_persons_own_words_stay_verbatim_with_their_references_linked()
    {
        using var ctx = Context();
        var cut = ctx.Render<ChatBubble>(p => p
            .Add(c => c.Role, TranscriptRole.User)
            .Add(c => c.Text, "Look at #12 and **this**")
            .Add(c => c.Timestamp, DateTimeOffset.UtcNow));

        var body = cut.Find(".session-turn-body");
        Assert.Equal("Look at #12 and **this**", body.TextContent);
        Assert.Equal("/workitems/12", cut.Find("a.wiki-ref").GetAttribute("href"));
    }

    // ---- tool rows ---------------------------------------------------------------------------------------

    [Fact]
    public void A_studio_tool_row_reads_as_words_and_opens_to_its_raw_name_and_arguments()
    {
        using var ctx = Context();
        var cut = ctx.Render<ToolCallCard>(p => p
            .Add(c => c.ToolName, "mcp__fravashi__session_work_link")
            .Add(c => c.Detail, string.Empty)
            .Add(c => c.Arguments, new Dictionary<string, string> { ["number"] = "343" })
            .Add(c => c.Success, true)
            .Add(c => c.Output, "{\"linked\":true}"));

        var label = cut.Find("[data-testid=tool-call-label]");
        Assert.Equal("Linked this conversation to #343", label.TextContent.Trim());
        Assert.Equal("/workitems/343", label.QuerySelector("a.wiki-ref")!.GetAttribute("href"));
        Assert.DoesNotContain("mcp__fravashi__", cut.Find("[data-testid=tool-call-toggle]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=tool-call-raw]"));

        cut.Find("[data-testid=tool-call-toggle]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("mcp__fravashi__session_work_link", cut.Find("[data-testid=tool-call-raw]").TextContent, StringComparison.Ordinal);
            Assert.Contains("number: 343", cut.Find("[data-testid=tool-call-arguments]").TextContent, StringComparison.Ordinal);
            Assert.NotEmpty(cut.FindAll("[data-testid=tool-call-output]"));
        });
    }

    [Fact]
    public void A_built_in_tool_row_keeps_its_name_and_subject()
    {
        using var ctx = Context();
        var cut = ctx.Render<ToolCallCard>(p => p
            .Add(c => c.ToolName, "Read")
            .Add(c => c.Detail, "/workspaces/w1/src/a.cs")
            .Add(c => c.Success, true)
            .Add(c => c.Output, "text"));

        Assert.Empty(cut.FindAll("[data-testid=tool-call-label]"));
        Assert.Equal("Read", cut.Find(".session-tool-name").TextContent);
        Assert.Contains("a.cs", cut.Find(".session-tool-arg").TextContent, StringComparison.Ordinal);
    }

    // ---- approval cards ----------------------------------------------------------------------------------

    private static PendingToolApproval Request(string tool, Dictionary<string, string> input, string detail) => new(
        "r1", AgentSessionId.New(), tool, detail, "t1", input, DateTimeOffset.UtcNow);

    [Fact]
    public void An_approval_card_says_what_the_call_will_do_and_keeps_what_it_showed()
    {
        using var ctx = Context();
        var cut = ctx.Render<ApprovalPrompt>(p => p.Add(c => c.Request, Request(
            "mcp__fravashi__work_item_transition",
            new Dictionary<string, string> { ["number"] = "343", ["version"] = "4", ["status"] = "in_progress" },
            "343")));

        Assert.Equal("Move #343 to In Progress", cut.Find("[data-testid=approval-sentence]").TextContent.Trim());
        Assert.Equal("343", cut.Find(".session-approval-detail").TextContent.Trim());
        // Work item #356: the header names the tool in words; the raw name stays in the raw view.
        Assert.Equal("work item transition", cut.Find(".session-approval-tool").TextContent.Trim());
        Assert.Contains("mcp__fravashi__work_item_transition", cut.Find("[data-testid=approval-raw-name]").TextContent);
        Assert.Contains("status:\nin_progress", cut.Find(".session-approval-body pre").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void A_built_in_tools_card_has_no_sentence_and_is_otherwise_unchanged()
    {
        using var ctx = Context();
        var cut = ctx.Render<ApprovalPrompt>(p => p.Add(c => c.Request, Request(
            "Bash", new Dictionary<string, string> { ["command"] = "dotnet build" }, "dotnet build")));

        Assert.Empty(cut.FindAll("[data-testid=approval-sentence]"));
        Assert.Equal("dotnet build", cut.Find(".session-approval-body pre").TextContent);
    }

    // ---- the preview, on its own -------------------------------------------------------------------------

    [Fact]
    public void A_preview_without_a_tracker_says_what_it_cannot_show_and_still_opens_the_page()
    {
        using var ctx = Context();
        var state = new ReferencePreviewState();
        var cut = ctx.Render<ReferencePreviewPanel>(p => p.Add(c => c.State, state));
        Assert.Empty(cut.FindAll("[data-testid=ref-preview]"));

        cut.InvokeAsync(() => state.Show(new WikiReference(WikiReferenceKind.WikiItem, 159)));

        cut.WaitForAssertion(() => Assert.Contains(
            "cannot be read here", cut.Find("[data-testid=ref-preview-missing]").TextContent, StringComparison.Ordinal));
        cut.Find("[data-testid=ref-preview-open]").Click();
        Assert.EndsWith("/wiki/159", ctx.Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }
}
