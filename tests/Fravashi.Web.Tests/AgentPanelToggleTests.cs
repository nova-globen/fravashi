using Bunit;
using Fravashi.Application.Git;
using Fravashi.Application.Notifications;
using Fravashi.Infrastructure.Notifications;
using Fravashi.Web.Components.Shell;
using Fravashi.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 23: the agent panel's control moved out of the split and into the window chrome, beside
/// the window buttons. These drive the control itself; where it sits in the title bar and how the
/// browser-hosted fallback is gated are held by <see cref="WindowTitleBarTests"/>,
/// <see cref="ActivityBarTests"/> and <see cref="StudioStylesheetGuardTests"/>.
/// </summary>
public class AgentPanelToggleTests
{
    private static BunitContext ToggleContext(out AgentPanelState panel)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        panel = new AgentPanelState();
        ctx.Services.AddSingleton(panel);
        return ctx;
    }

    private static IRenderedComponent<AgentPanelToggle> Render(BunitContext ctx) =>
        ctx.Render<AgentPanelToggle>(p => p
            .Add(c => c.Class, "studio-titlebar-agentbtn")
            .Add(c => c.TestId, "titlebar-agent-toggle"));

    [Fact]
    public void With_no_surface_drawing_the_panel_there_is_nothing_to_toggle_and_no_button()
    {
        using var ctx = ToggleContext(out _);

        var cut = Render(ctx);

        Assert.Empty(cut.FindAll("[data-testid=titlebar-agent-toggle]"));
    }

    [Fact]
    public void Clicking_the_control_opens_the_panel_and_clicking_again_hides_it()
    {
        using var ctx = ToggleContext(out var panel);
        panel.Attach();
        var cut = Render(ctx);

        cut.InvokeAsync(() => cut.Find("[data-testid=titlebar-agent-toggle]").Click());
        cut.WaitForAssertion(() => Assert.True(panel.IsOpen));

        cut.InvokeAsync(() => cut.Find("[data-testid=titlebar-agent-toggle]").Click());
        cut.WaitForAssertion(() => Assert.False(panel.IsOpen));
    }

    [Fact]
    public void The_control_says_whether_the_panel_is_open()
    {
        using var ctx = ToggleContext(out var panel);
        panel.Attach();
        var cut = Render(ctx);

        var closed = cut.Find("[data-testid=titlebar-agent-toggle]");
        Assert.Equal("false", closed.GetAttribute("aria-pressed"));
        Assert.Equal("Show the agent panel", closed.GetAttribute("aria-label"));
        Assert.Equal("Show the agent panel", closed.GetAttribute("title"));
        Assert.DoesNotContain("is-open", closed.GetAttribute("class"));

        cut.InvokeAsync(panel.Open);

        cut.WaitForAssertion(() =>
        {
            var open = cut.Find("[data-testid=titlebar-agent-toggle]");
            Assert.Equal("true", open.GetAttribute("aria-pressed"));
            Assert.Equal("Hide the agent panel", open.GetAttribute("aria-label"));
            Assert.Equal("Hide the agent panel", open.GetAttribute("title"));
            Assert.Contains("is-open", open.GetAttribute("class"));
        });
    }

    [Fact]
    public void The_control_follows_the_panel_when_something_else_closes_it()
    {
        // The panel's own header carries a close button; the chrome control must not go on claiming
        // the panel is showing after it.
        using var ctx = ToggleContext(out var panel);
        panel.Attach();
        panel.Open();
        var cut = Render(ctx);
        Assert.Equal("true", cut.Find("[data-testid=titlebar-agent-toggle]").GetAttribute("aria-pressed"));

        cut.InvokeAsync(panel.Close);

        cut.WaitForAssertion(() =>
            Assert.Equal("false", cut.Find("[data-testid=titlebar-agent-toggle]").GetAttribute("aria-pressed")));
    }

    [Fact]
    public void The_control_disappears_when_the_surface_that_draws_the_panel_goes_away()
    {
        using var ctx = ToggleContext(out var panel);
        panel.Attach();
        var cut = Render(ctx);
        Assert.NotEmpty(cut.FindAll("[data-testid=titlebar-agent-toggle]"));

        cut.InvokeAsync(panel.Detach);

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=titlebar-agent-toggle]")));
    }

    [Fact]
    public void The_title_bar_puts_the_control_immediately_left_of_the_window_buttons()
    {
        // "Beside the window buttons" is the decided placement (backlog 23): always the same spot,
        // and inside .studio-titlebar-controls, which is already opted out of the drag region.
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IGitService>(new UnavailableGitService());
        ctx.Services.AddSingleton<INotificationHistory>(new InMemoryNotificationHistory());
        // The bar also names the active workspace in its title; none is bound here.
        ctx.Services.AddSingleton(new Fravashi.Infrastructure.Bridge.BridgeWorkspaceContext(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()));
        ctx.Services.AddSingleton<Fravashi.Application.Persistence.IWorkspaceStore>(new EmptyWorkspaceStore());
        var panel = new AgentPanelState();
        panel.Attach();
        ctx.Services.AddSingleton(panel);
        ctx.Services.AddSingleton(new ScratchpadState());
        ctx.Services.AddSingleton(new StartDialogState());
        using (ctx)
        {
            var cut = ctx.Render<WindowTitleBar>();

            // Backlog 23 put this control immediately left of the window buttons. Backlog 67 (owner,
            // 2026-09-23) then put the scratchpad's button "next to the minimize button", and both cannot be
            // immediately left. The newer, explicit placement wins: agent toggle, then scratchpad toggle,
            // then the window buttons. The agent toggle is still first in the group and still beside the
            // window chrome, which is what backlog 23 was protecting (always in the same place, never over
            // editor content).
            var group = cut.Find(".studio-titlebar-controls");
            var children = group.Children.ToList();
            Assert.Equal("titlebar-agent-toggle", children[0].GetAttribute("data-testid"));
            Assert.Equal("titlebar-scratchpad-toggle", children[1].GetAttribute("data-testid"));
            Assert.All(children.Skip(2), c => Assert.Contains("studio-caption-btn", c.GetAttribute("class")));
        }
    }
}
