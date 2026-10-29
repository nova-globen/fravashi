using Bunit;
using Fravashi.Application.Notifications;
using Fravashi.Infrastructure.Notifications;
using Fravashi.Web.Components.Pages;
using Fravashi.Web.Components.Shell;
using Fravashi.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

public class ShellRenderTests
{
    // The activity bar's bottom group hosts AgentPanelToggle + ThemeToggle + NotificationCenter, so
    // their services are needed to render the bar at all. Nothing here attaches an agent-panel
    // surface, so that toggle draws nothing and the group is theme, notifications, settings.
    private static BunitContext BarContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<INotificationHistory>(new InMemoryNotificationHistory());
        ctx.Services.AddSingleton<IWaitingForPersonList>(new StubWaitingForPersonList());
        ctx.Services.AddSingleton<ThemeState>();
        ctx.Services.AddSingleton<AgentPanelState>();
        ctx.Services.AddScoped<ScratchpadState>();
        return ctx;
    }

    [Fact]
    public void ActivityBar_renders_every_looped_activity_and_the_settings_button()
    {
        using var ctx = BarContext();

        var cut = ctx.Render<ActivityBar>(p => p.Add(c => c.CurrentArea, StudioArea.Home));

        // The Board joined the rail in backlog 38 and the Wiki in backlog 39; the graph's two projections lead,
        // because they are where the work is described before any of it is written. Backlog 303's autopilot page left
        // it in work item #358: the autopilot is Home's Autopilot card.
        var activities = cut.FindAll("a.studio-activity");
        Assert.Equal(6, activities.Count);
        Assert.Equal("/board", activities[0].GetAttribute("href"));
        Assert.Equal("/wiki", activities[1].GetAttribute("href"));
        Assert.Equal("/code", activities[2].GetAttribute("href"));
        Assert.Equal("/git", activities[3].GetAttribute("href"));
        Assert.Equal("/sessions", activities[4].GetAttribute("href"));
        Assert.Equal("/settings", activities[5].GetAttribute("href"));
        Assert.Empty(cut.FindAll("a.studio-activity.active"));
    }

    [Fact]
    public void ActivityBar_marks_the_code_activity_active_on_the_code_area()
    {
        using var ctx = BarContext();

        var cut = ctx.Render<ActivityBar>(p => p.Add(c => c.CurrentArea, StudioArea.Code));

        var active = Assert.Single(cut.FindAll("a.studio-activity.active"));
        Assert.Equal("/code", active.GetAttribute("href"));
    }

    [Fact]
    public void ActivityBar_marks_the_settings_button_active_on_the_settings_area()
    {
        using var ctx = BarContext();

        var cut = ctx.Render<ActivityBar>(p => p.Add(c => c.CurrentArea, StudioArea.Settings));

        Assert.Single(cut.FindAll("a.studio-activity.active"));
    }

    [Fact]
    public void ActivityBar_bottom_group_stacks_theme_notifications_then_settings()
    {
        using var ctx = BarContext();

        var cut = ctx.Render<ActivityBar>(p => p.Add(c => c.CurrentArea, StudioArea.Home));

        var children = cut.Find(".studio-rail-bottom").Children;
        Assert.Equal(3, children.Length);
        Assert.Equal("rail-theme-toggle", children[0].GetAttribute("data-testid"));
        Assert.NotNull(children[1].QuerySelector(".notification-center-button"));
        Assert.Equal("activitybar-settings", children[2].GetAttribute("data-testid"));
    }

    [Fact]
    public void Theme_toggle_cycles_the_mode_and_reflects_it_in_the_label()
    {
        using var ctx = BarContext();
        var theme = ctx.Services.GetRequiredService<ThemeState>();

        var cut = ctx.Render<ThemeToggle>();
        Assert.Contains("System", cut.Find("button").GetAttribute("title"));

        cut.Find("button").Click();

        Assert.NotEqual(Microsoft.FluentUI.AspNetCore.Components.DesignThemeModes.System, theme.Mode);
        Assert.Contains(theme.Mode.ToString(), cut.Find("button").GetAttribute("title"));
    }

    [Fact]
    public void Default_route_loads_the_merged_home_surface()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddNoUpdates();
        ctx.Services.AddSingleton<INotificationHistory>(new InMemoryNotificationHistory());
        ctx.Services.AddUnboundHomeSurface();

        var cut = ctx.Render<Home>();

        // Work item #350: Home is the product's dashboard, with no page bar; its conversation box leads and the cards follow.
        Assert.Empty(cut.FindAll(".studio-page-bar"));
        Assert.NotNull(cut.Find("[data-testid=home-prompt]"));
        Assert.NotNull(cut.Find("[data-testid=updates-card]"));
        Assert.NotNull(cut.Find("[data-testid=autopilot-card]"));
        Assert.NotNull(cut.Find("[data-testid=recent-dispatches]"));
    }
}
