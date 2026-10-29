using Fravashi.Web.Services;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Fravashi.Web.Tests;

public class ThemeStateTests
{
    [Fact]
    public void Defaults_to_system()
    {
        var theme = new ThemeState();

        Assert.Equal(DesignThemeModes.System, theme.Mode);
    }

    [Fact]
    public void Cycle_walks_system_light_dark_and_back()
    {
        var theme = new ThemeState();

        Assert.Equal(DesignThemeModes.Light, theme.Cycle());
        Assert.Equal(DesignThemeModes.Dark, theme.Cycle());
        Assert.Equal(DesignThemeModes.System, theme.Cycle());
    }

    [Fact]
    public void Cycle_raises_changed()
    {
        var theme = new ThemeState();
        var raised = 0;
        theme.Changed += () => raised++;

        theme.Cycle();

        Assert.Equal(1, raised);
    }

    [Fact]
    public void Setting_mode_raises_changed_so_the_persisted_mode_restore_re_renders_subscribers()
    {
        var theme = new ThemeState();
        var raised = 0;
        theme.Changed += () => raised++;

        // FluentDesignTheme restores the persisted mode through the bound property setter.
        theme.Mode = DesignThemeModes.Dark;

        Assert.Equal(1, raised);
        Assert.Equal(DesignThemeModes.Dark, theme.Mode);
    }

    [Fact]
    public void Setting_the_same_mode_does_not_raise_changed()
    {
        var theme = new ThemeState();
        var raised = 0;
        theme.Changed += () => raised++;

        theme.Mode = DesignThemeModes.System;

        Assert.Equal(0, raised);
    }
}
