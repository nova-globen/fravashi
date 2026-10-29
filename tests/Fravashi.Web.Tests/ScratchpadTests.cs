using Bunit;
using Fravashi.Application.Scratchpad;
using Fravashi.Web.Components.Shell;
using Fravashi.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// The scratchpad (backlog 67): its toggle and its panel. The editor itself is JavaScript (Monaco), so these hold
/// the contract between the two halves: what the panel hands the editor, what it saves, and above all that
/// closing it SAVES the final text rather than dropping it.
/// </summary>
public class ScratchpadTests
{
    private sealed class FakeStore : IScratchpadStore
    {
        public string Text { get; set; } = string.Empty;

        public int Saves { get; private set; }

        public string Read() => Text;

        public void Save(string? markdown)
        {
            Text = markdown ?? string.Empty;
            Saves++;
        }
    }

    private static BunitContext Context(out ScratchpadState state, out FakeStore store)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        state = new ScratchpadState();
        store = new FakeStore();
        ctx.Services.AddSingleton(state);
        ctx.Services.AddSingleton<IScratchpadStore>(store);
        return ctx;
    }

    // ---- the toggle --------------------------------------------------------------------------------

    [Fact]
    public void The_toggle_opens_the_scratchpad_and_closes_it_again()
    {
        using var ctx = Context(out var state, out _);
        var cut = ctx.Render<ScratchpadToggle>();

        cut.InvokeAsync(() => cut.Find("[data-testid=titlebar-scratchpad-toggle]").Click());
        cut.WaitForAssertion(() => Assert.True(state.IsOpen));

        cut.InvokeAsync(() => cut.Find("[data-testid=titlebar-scratchpad-toggle]").Click());
        cut.WaitForAssertion(() => Assert.False(state.IsOpen));
    }

    /// <summary>State is carried three ways so none of them is the only one that works: colour, aria, words.</summary>
    [Fact]
    public void The_toggle_says_whether_the_scratchpad_is_open()
    {
        using var ctx = Context(out var state, out _);
        var cut = ctx.Render<ScratchpadToggle>();

        var closed = cut.Find("[data-testid=titlebar-scratchpad-toggle]");
        Assert.Equal("false", closed.GetAttribute("aria-pressed"));
        Assert.Equal("Show the scratchpad", closed.GetAttribute("aria-label"));

        cut.InvokeAsync(state.Toggle);

        cut.WaitForAssertion(() =>
        {
            var open = cut.Find("[data-testid=titlebar-scratchpad-toggle]");
            Assert.Equal("true", open.GetAttribute("aria-pressed"));
            Assert.Equal("Hide the scratchpad", open.GetAttribute("aria-label"));
            Assert.Contains("is-open", open.ClassName);
        });
    }

    // ---- the panel ---------------------------------------------------------------------------------

    [Fact]
    public void The_panel_hands_the_editor_exactly_what_was_saved()
    {
        using var ctx = Context(out _, out var store);
        store.Text = "- [ ] ring the bank\n- [x] **ship** it\n";

        var cut = ctx.Render<ScratchpadPanel>();

        cut.WaitForAssertion(() =>
        {
            var open = Assert.Single(ctx.JSInterop.Invocations, i => i.Identifier == "fravashiScratchpad.open");
            Assert.Equal("- [ ] ring the bank\n- [x] **ship** it\n", open.Arguments[1]);
        });
    }

    [Fact]
    public void What_the_editor_saves_is_stored_as_it_came()
    {
        using var ctx = Context(out _, out var store);
        var cut = ctx.Render<ScratchpadPanel>();

        cut.InvokeAsync(() => cut.Instance.Save("# notes\r\n- [x] done  \n"));

        Assert.Equal("# notes\r\n- [x] done  \n", store.Text);
    }

    /// <summary>
    /// The one that matters most. The editor saves on a quiet moment, so the last half second of typing exists
    /// only in the editor when the panel is closed. Closing asks the editor for its final text and saves it
    /// HERE, while the component can still act; saving through a callback instead could reach a .NET object
    /// that had already been disposed, and the text would be lost without a word.
    /// </summary>
    [Fact]
    public async Task Closing_the_panel_saves_the_editors_final_text()
    {
        using var ctx = Context(out _, out var store);
        store.Text = "old";
        ctx.JSInterop.Setup<string?>("fravashiScratchpad.close").SetResult("old, plus what was typed just now");
        var cut = ctx.Render<ScratchpadPanel>();
        cut.WaitForAssertion(() => Assert.Contains(ctx.JSInterop.Invocations, i => i.Identifier == "fravashiScratchpad.open"));

        await cut.Instance.DisposeAsync();

        Assert.Equal("old, plus what was typed just now", store.Text);
    }

    [Fact]
    public async Task Closing_a_panel_whose_editor_never_opened_leaves_the_text_alone()
    {
        using var ctx = Context(out _, out var store);
        store.Text = "keep me";
        ctx.JSInterop.Setup<string?>("fravashiScratchpad.close").SetResult(null);
        var cut = ctx.Render<ScratchpadPanel>();
        cut.WaitForAssertion(() => Assert.Contains(ctx.JSInterop.Invocations, i => i.Identifier == "fravashiScratchpad.open"));

        await cut.Instance.DisposeAsync();

        Assert.Equal("keep me", store.Text);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public void Every_formatting_button_asks_the_editor_for_its_own_action()
    {
        using var ctx = Context(out _, out _);
        var cut = ctx.Render<ScratchpadPanel>();

        foreach (var action in new[] { "bold", "italic", "link", "bullet", "numbered", "check" })
        {
            cut.InvokeAsync(() => cut.Find($"[data-testid=scratchpad-{action}]").Click());
            cut.WaitForAssertion(() => Assert.Contains(
                ctx.JSInterop.Invocations,
                i => i.Identifier == "fravashiScratchpad.format" && (string?)i.Arguments[0] == action));
        }
    }
}
