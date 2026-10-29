using Bunit;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Rendering;
using Fravashi.Web.Components.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 110, the per-block half: every message and every code block carries a copy control, and what
/// it copies is the text the component was GIVEN. The exact string matters more than the control does:
/// the defect this replaces is a chat surface people read code in with no way to get the code out, and a
/// button that copies the rendered HTML (entities decoded, markup interleaved) would be the same defect
/// wearing a button.
///
/// The clipboard write itself is asserted at the interop boundary, which is as far as bunit can see.
/// Whether WebView2's clipboard accepts the write is the owner's window check, recorded as owed.
/// </summary>
public class TranscriptCopyTests
{
    private const string CopyCall = TranscriptClipboard.CopyFunction;

    private static BunitContext Context(CopyOutcome? outcome = null)
    {
        var ctx = new BunitContext();
        ctx.Services.AddSingleton(new MarkdownPreviewRenderer());
        ctx.JSInterop.Setup<CopyOutcome?>(CopyCall, _ => true)
            .SetResult(outcome ?? new CopyOutcome(true, null));
        return ctx;
    }

    private static IRenderedComponent<ChatBubble> Bubble(BunitContext ctx, TranscriptRole role, string text) =>
        ctx.Render<ChatBubble>(p => p
            .Add(c => c.Role, role)
            .Add(c => c.Text, text)
            .Add(c => c.Timestamp, DateTimeOffset.UtcNow));

    private static string CopiedText(BunitContext ctx) =>
        Assert.IsType<string>(ctx.JSInterop.Invocations[CopyCall].Single().Arguments[0]);

    [Fact]
    public async Task A_user_turn_offers_a_control_that_copies_its_text_exactly()
    {
        using var ctx = Context();
        const string text = "run the tests\nthen tell me what broke";
        var cut = Bubble(ctx, TranscriptRole.User, text);

        await cut.InvokeAsync(() => cut.Find("[data-testid=copy-message]").Click());

        cut.WaitForAssertion(() => Assert.Single(ctx.JSInterop.Invocations[CopyCall]));
        Assert.Equal(text, CopiedText(ctx));
    }

    [Fact]
    public async Task A_fenced_block_gets_its_own_control_and_copies_the_code_without_the_fences()
    {
        using var ctx = Context();
        const string markdown = "Here is the fix:\n\n```csharp\nvar x = 1;\nif (x > 0) { Run(); }\n```\n\nThat is all.";
        var cut = Bubble(ctx, TranscriptRole.Assistant, markdown);

        Assert.Single(cut.FindAll("[data-testid=code-block]"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=copy-code]").Click());

        cut.WaitForAssertion(() => Assert.Single(ctx.JSInterop.Invocations[CopyCall]));

        // The exact string, including that the fence markers and the info string are gone and the line
        // break inside the block is kept.
        Assert.Equal("var x = 1;\nif (x > 0) { Run(); }", CopiedText(ctx));
    }

    [Fact]
    public async Task The_message_control_on_the_same_turn_copies_the_raw_markdown_fences_and_all()
    {
        using var ctx = Context();
        const string markdown = "Here is the fix:\n\n```csharp\nvar x = 1;\n```\n\nThat is all.";
        var cut = Bubble(ctx, TranscriptRole.Assistant, markdown);

        await cut.InvokeAsync(() => cut.Find("[data-testid=copy-message]").Click());

        cut.WaitForAssertion(() => Assert.Single(ctx.JSInterop.Invocations[CopyCall]));

        // The message control copies the MESSAGE, which is the markdown the agent wrote. Handing over
        // the flattened prose instead would lose the block boundaries the reader can see on screen.
        Assert.Equal(markdown, CopiedText(ctx));
    }

    [Fact]
    public async Task Every_code_block_in_one_message_gets_its_own_control()
    {
        using var ctx = Context();
        const string markdown = "one:\n\n```\nalpha\n```\n\ntwo:\n\n```\nbeta\n```";
        var cut = Bubble(ctx, TranscriptRole.Assistant, markdown);

        var controls = cut.FindAll("[data-testid=copy-code]");
        Assert.Equal(2, controls.Count);

        await cut.InvokeAsync(() => cut.FindAll("[data-testid=copy-code]")[1].Click());

        cut.WaitForAssertion(() => Assert.Single(ctx.JSInterop.Invocations[CopyCall]));
        Assert.Equal("beta", CopiedText(ctx));
    }

    [Fact]
    public void A_block_inside_a_numbered_list_gets_a_control_too_and_the_list_survives()
    {
        // An agent putting a command inside a numbered list is an everyday shape, and the split cannot cut
        // the source there without breaking the list apart. So the control is rendered INTO the list's
        // HTML carrying the block's source; sessionCopy.js answers that click, because markup delivered as
        // a MarkupString has no Blazor handlers.
        using var ctx = Context();
        const string markdown = "Steps:\n\n1. run this:\n\n   ```sh\n   make test\n   ```\n\n2. done";
        var cut = Bubble(ctx, TranscriptRole.Assistant, markdown);

        Assert.Single(cut.FindAll("[data-testid=code-block]"));
        Assert.Single(cut.FindAll("[data-testid=copy-code]"));

        var control = cut.Find("[data-testid=copy-code]");
        Assert.Equal("make test", control.GetAttribute("data-copy-source"));

        // The list is still one list with both its items, which is what cutting the source would cost.
        Assert.Equal(2, cut.FindAll("ol > li").Count);
    }

    [Fact]
    public void The_two_kinds_of_block_in_one_message_are_counted_and_drawn_the_same_way()
    {
        using var ctx = Context();
        const string markdown = "```sh\nalpha\n```\n\n- then:\n\n  ```sh\n  beta\n  ```";
        var cut = Bubble(ctx, TranscriptRole.Assistant, markdown);

        Assert.Equal(2, cut.FindAll("[data-testid=code-block]").Count);
        Assert.Equal(2, cut.FindAll("[data-testid=copy-code]").Count);
        Assert.Equal(2, cut.FindAll(".session-code-bar .session-copy").Count);
    }

    [Fact]
    public void The_shipped_script_answers_the_click_on_a_nested_blocks_control()
    {
        // bunit cannot click through to a handler that lives in JS, so this reads the shipped file: the
        // control above is inert without the delegated listener, and a missing listener is silent.
        var script = File.ReadAllText(Path.Combine(WebProject(), "wwwroot", "sessionCopy.js"));

        Assert.Contains("data-copy-source", script, StringComparison.Ordinal);
        Assert.Contains("addEventListener('click'", script, StringComparison.Ordinal);
        Assert.Contains("fravashiSessionCopy.copy", script, StringComparison.Ordinal);
    }

    [Fact]
    public void A_message_with_no_code_shows_a_message_control_and_no_code_control()
    {
        using var ctx = Context();
        var cut = Bubble(ctx, TranscriptRole.Assistant, "no code here, just **prose**.");

        Assert.Single(cut.FindAll("[data-testid=copy-message]"));
        Assert.Empty(cut.FindAll("[data-testid=copy-code]"));
        Assert.Contains("<strong>prose</strong>", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_control_says_Copied_once_the_script_confirms_the_write()
    {
        using var ctx = Context();
        var cut = Bubble(ctx, TranscriptRole.User, "some words");

        Assert.Equal("Copy", cut.Find("[data-testid=copy-message]").TextContent.Trim());
        await cut.InvokeAsync(() => cut.Find("[data-testid=copy-message]").Click());

        cut.WaitForAssertion(
            () => Assert.Equal("Copied", cut.Find("[data-testid=copy-message]").TextContent.Trim()));
    }

    [Fact]
    public async Task A_control_says_why_not_when_the_script_reports_a_failure()
    {
        // A button that says "Copied" when nothing was copied is worse than no button: the reader walks
        // away believing they have the text.
        using var ctx = Context(new CopyOutcome(false, "the browser refused the copy"));
        var cut = Bubble(ctx, TranscriptRole.User, "some words");

        await cut.InvokeAsync(() => cut.Find("[data-testid=copy-message]").Click());

        cut.WaitForAssertion(
            () => Assert.Equal(
                "the browser refused the copy", cut.Find("[data-testid=copy-failed]").TextContent.Trim()));
        Assert.Equal("Copy", cut.Find("[data-testid=copy-message]").TextContent.Trim());
    }

    /// <summary>
    /// The studio's project folder on disk. The shipped assets are read the way
    /// TranscriptScrollAnchoringTests reads them.
    /// </summary>
    private static string WebProject()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Fravashi.sln")))
        {
            root = root.Parent;
        }

        Assert.True(root is not null, "the test must be able to locate the repository root (Fravashi.sln)");
        return Path.Combine(root!.FullName, "src", "Fravashi.Web");
    }

    [Fact]
    public void The_script_the_controls_call_is_shipped_and_loaded_by_the_page()
    {
        // bunit fakes the interop, so nothing above this notices that the module is missing from the
        // page: in a real window every control would throw "fravashiSessionCopy is not defined".
        var web = WebProject();
        var script = Path.Combine(web, "wwwroot", "sessionCopy.js");
        Assert.True(File.Exists(script), $"the studio should ship the clipboard module at {script}");

        var source = File.ReadAllText(script);
        Assert.Contains("navigator.clipboard.writeText", source, StringComparison.Ordinal);
        Assert.Contains("execCommand", source, StringComparison.Ordinal);

        var app = File.ReadAllText(Path.Combine(web, "Components", "App.razor"));
        Assert.Contains("sessionCopy.js", app, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_redacted_message_copies_what_is_on_screen_and_not_the_secret_behind_it()
    {
        // Backlog 102 meets this one: the display redacts, so the clipboard has to redact too or one
        // paste undoes it.
        const string fakeKey = "sk-" + "ant-api03-Fake0123456789Fake0123456789Fake0123456789FakeAA";
        using var ctx = Context();
        var cut = Bubble(ctx, TranscriptRole.User, "the key was " + fakeKey);

        await cut.InvokeAsync(() => cut.Find("[data-testid=copy-message]").Click());

        cut.WaitForAssertion(() => Assert.Single(ctx.JSInterop.Invocations[CopyCall]));
        Assert.DoesNotContain(fakeKey, CopiedText(ctx), StringComparison.Ordinal);
        Assert.Contains("[redacted:sk-ant-api-key]", CopiedText(ctx), StringComparison.Ordinal);
    }
}
