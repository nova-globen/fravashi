using Bunit;
using Fravashi.Web.Components.Sessions;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 104 D3, the display half: a tool call's standard error is shown as its own block, so a
/// sub-command that failed only by writing to stderr is visible rather than an empty success.
/// </summary>
public class ToolCallStderrTests
{
    [Fact]
    public void A_call_whose_second_part_failed_on_stderr_shows_the_error_not_an_empty_success()
    {
        using var ctx = new BunitContext();

        // The empty-success shape the fix targets: the exit code did not flip Success, stdout is empty,
        // and everything the user needs to see is on stderr.
        var cut = ctx.Render<ToolCallCard>(p => p
            .Add(c => c.ToolName, "Bash")
            .Add(c => c.Detail, "true && missing-binary")
            .Add(c => c.Success, true)
            .Add(c => c.Output, string.Empty)
            .Add(c => c.Error, "missing-binary: command not found"));

        // Present on screen without a click: stderr opens the card by itself, like a failure does.
        var stderr = cut.Find("[data-testid=tool-call-stderr]");
        Assert.Contains("missing-binary: command not found", stderr.TextContent);
        Assert.Contains("session-tool-err", stderr.ClassList);
    }

    [Fact]
    public void Stderr_sits_beside_stdout_as_a_second_block_not_folded_into_it()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<ToolCallCard>(p => p
            .Add(c => c.ToolName, "Bash")
            .Add(c => c.Detail, "build")
            .Add(c => c.Success, false)
            .Add(c => c.Output, "compiling\n")
            .Add(c => c.Error, "warning: deprecated api"));

        Assert.Contains("compiling", cut.Find("[data-testid=tool-call-output]").TextContent);
        Assert.Contains("warning: deprecated api", cut.Find("[data-testid=tool-call-stderr]").TextContent);
    }

    [Fact]
    public void A_call_that_wrote_nothing_to_stderr_shows_no_stderr_block()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<ToolCallCard>(p => p
            .Add(c => c.ToolName, "Read")
            .Add(c => c.Detail, "notes.txt")
            .Add(c => c.Success, true)
            .Add(c => c.Output, "the file contents"));

        Assert.Empty(cut.FindAll("[data-testid=tool-call-stderr]"));
    }
}
