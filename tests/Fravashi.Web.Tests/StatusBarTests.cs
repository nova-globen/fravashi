using Bunit;
using Fravashi.Application.Notifications;
using Fravashi.Infrastructure.Notifications;
using Fravashi.Web.Components.Shell;
using Fravashi.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Fravashi.Web.Tests;

public class StatusBarTests
{
    [Fact]
    public void Status_bar_renders_the_area_and_theme_toggle()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<INotificationHistory>(new InMemoryNotificationHistory());
        ctx.Services.AddSingleton<ThemeState>();

        var cut = ctx.Render<StudioStatusBar>(p => p.Add(c => c.AreaLabel, "Dashboard"));

        Assert.Contains("Dashboard", cut.Markup);
        // Theme toggle is present and defaults to System.
        Assert.Contains("studio-theme-toggle", cut.Markup);
        Assert.Contains("System", cut.Markup);
    }

    [Fact]
    public void Status_bar_no_longer_hosts_the_notification_center()
    {
        // UI8 -- moved to the top page bar (see PageBarTests).
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<INotificationHistory>(new InMemoryNotificationHistory());
        ctx.Services.AddSingleton<ThemeState>();

        var cut = ctx.Render<StudioStatusBar>(p => p.Add(c => c.AreaLabel, "Dashboard"));

        Assert.Empty(cut.FindAll(".notification-center-button"));
    }

    [Fact]
    public void Theme_toggle_reflects_a_mode_change_raised_outside_the_component()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<INotificationHistory>(new InMemoryNotificationHistory());
        var theme = new ThemeState();
        ctx.Services.AddSingleton(theme);

        var cut = ctx.Render<StudioStatusBar>(p => p.Add(c => c.AreaLabel, "Dashboard"));

        // FluentDesignTheme restores the persisted mode via the ThemeState setter on circuit start;
        // the status-bar label must follow instead of staying on the initial System.
        theme.Mode = DesignThemeModes.Dark;

        Assert.Contains("Dark", cut.Find(".studio-theme-toggle").TextContent);
    }
}
