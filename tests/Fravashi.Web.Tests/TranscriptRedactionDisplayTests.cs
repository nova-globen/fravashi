using Bunit;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Rendering;
using Fravashi.Web.Components.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 102, the display half: a stored row the startup scrub has never seen (or a live event on its
/// way to the store) is still never RENDERED raw. The same redactor the persistence path uses runs at
/// the leaf display components, so every surface that shows transcript text is covered at once.
/// </summary>
public class TranscriptRedactionDisplayTests
{
    private const string FakeKey = "sk-" + "ant-api03-Fake0123456789Fake0123456789Fake0123456789FakeAA";

    [Fact]
    public void A_tool_result_holding_an_env_dump_renders_redacted()
    {
        using var ctx = new BunitContext();
        // Key last: a key line mid-dump is the joined-run swallow acceptance, pinned in
        // TranscriptRedactorTests; this test is about the display path, not the shape.
        var dump = "HOME=/root\nPATH=/usr/bin\nANTHROPIC_API_KEY=" + FakeKey;

        // Success=false opens the output by itself, so the pre is on screen without a click.
        var cut = ctx.Render<ToolCallCard>(p => p
            .Add(c => c.ToolName, "Bash")
            .Add(c => c.Detail, "env")
            .Add(c => c.Success, false)
            .Add(c => c.Output, dump));

        Assert.DoesNotContain(FakeKey, cut.Markup);
        Assert.Contains(
            "ANTHROPIC_API_KEY=[redacted:ANTHROPIC_API_KEY]",
            cut.Find("[data-testid=tool-call-output]").TextContent);
    }

    [Fact]
    public void A_tool_calls_argument_line_renders_redacted()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<ToolCallCard>(p => p
            .Add(c => c.ToolName, "Bash")
            .Add(c => c.Detail, "export ANTHROPIC_API_KEY=" + FakeKey)
            .Add(c => c.Success, true));

        Assert.DoesNotContain(FakeKey, cut.Markup);
    }

    [Fact]
    public void A_message_bubble_never_shows_a_raw_key()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddSingleton(new MarkdownPreviewRenderer());

        var cut = ctx.Render<ChatBubble>(p => p
            .Add(c => c.Role, TranscriptRole.Assistant)
            .Add(c => c.Text, "the environment held " + FakeKey + " at the time")
            .Add(c => c.Timestamp, DateTimeOffset.UtcNow));

        Assert.DoesNotContain(FakeKey, cut.Markup);
        Assert.Contains("[redacted:sk-ant-api-key]", cut.Markup);
    }

    [Fact]
    public void Ordinary_code_in_a_tool_result_renders_untouched()
    {
        using var ctx = new BunitContext();
        var code = "var TOKEN = ReadToken(); // d788bbf3c1a94be00e2f5cf05a1c6c02e6b7a4d9";

        var cut = ctx.Render<ToolCallCard>(p => p
            .Add(c => c.ToolName, "Read")
            .Add(c => c.Detail, "Program.cs")
            .Add(c => c.Success, false)
            .Add(c => c.Output, code));

        Assert.Equal(code, cut.Find("[data-testid=tool-call-output]").TextContent);
    }
}
