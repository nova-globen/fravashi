using Bunit;
using Fravashi.Application.Notifications;
using Fravashi.Application.Persistence;
using Fravashi.Infrastructure.Autopilot;
using Fravashi.Infrastructure.Notifications;
using Fravashi.Web.Components.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// ADR 0031 decision 16, #307: Settings &gt; Agents &gt; Autopilot concurrency, the cap a dispatch can only ever
/// lower, never raise. Shaped after <c>SpendSettingsSectionTests</c>.
/// </summary>
public sealed class AutopilotConcurrencySectionTests
{
    private static BunitContext Context(out FakeConfigurationStore config, out FakeConfigurationReloader reloader)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        config = new FakeConfigurationStore();
        reloader = new FakeConfigurationReloader();
        ctx.Services.AddSingleton<IConfigurationStore>(config);
        ctx.Services.AddSingleton<IConfigurationReloader>(reloader);
        ctx.Services.AddSingleton<INotificationHistory>(new InMemoryNotificationHistory());
        return ctx;
    }

    [Fact]
    public void An_unconfigured_studio_shows_the_shipped_default()
    {
        using var ctx = Context(out _, out _);

        var cut = ctx.Render<AutopilotConcurrencySection>();

        Assert.Equal("2", cut.Find("[data-testid=autopilot-concurrency-cap]").GetAttribute("value"));
        Assert.True(cut.Find("[data-testid=autopilot-concurrency-save]").HasAttribute("disabled"));
    }

    [Fact]
    public void A_stored_cap_is_shown_the_way_it_will_be_read()
    {
        using var ctx = Context(out var config, out _);
        config.Set(ConfigurationAutopilotConcurrencySettings.CapKey, "4");

        var cut = ctx.Render<AutopilotConcurrencySection>();

        Assert.Equal("4", cut.Find("[data-testid=autopilot-concurrency-cap]").GetAttribute("value"));
    }

    [Fact]
    public void An_unparseable_stored_cap_reads_back_as_the_default()
    {
        using var ctx = Context(out var config, out _);
        config.Set(ConfigurationAutopilotConcurrencySettings.CapKey, "not a number");

        var cut = ctx.Render<AutopilotConcurrencySection>();

        Assert.Equal("2", cut.Find("[data-testid=autopilot-concurrency-cap]").GetAttribute("value"));
    }

    [Fact]
    public void Saving_writes_the_cap_and_reloads_configuration()
    {
        using var ctx = Context(out var config, out var reloader);
        var cut = ctx.Render<AutopilotConcurrencySection>();

        cut.InvokeAsync(() => cut.Find("[data-testid=autopilot-concurrency-cap]").Input("5"));
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=autopilot-concurrency-save]").HasAttribute("disabled")));
        cut.InvokeAsync(() => cut.Find("[data-testid=autopilot-concurrency-save]").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("5", config.Get(ConfigurationAutopilotConcurrencySettings.CapKey));
            Assert.Equal(1, reloader.ReloadCount);
        });
    }

    [Fact]
    public void A_non_positive_cap_is_refused_rather_than_saved()
    {
        using var ctx = Context(out var config, out var reloader);
        var cut = ctx.Render<AutopilotConcurrencySection>();

        cut.InvokeAsync(() => cut.Find("[data-testid=autopilot-concurrency-cap]").Input("0"));
        cut.InvokeAsync(() => cut.Find("[data-testid=autopilot-concurrency-save]").Click());

        cut.WaitForAssertion(() => Assert.Contains(
            "whole number above zero",
            cut.Find("[data-testid=autopilot-concurrency-error]").TextContent,
            StringComparison.Ordinal));
        Assert.Null(config.Get(ConfigurationAutopilotConcurrencySettings.CapKey));
        Assert.Equal(0, reloader.ReloadCount);
    }

    [Fact]
    public void A_value_that_is_not_a_number_is_refused()
    {
        using var ctx = Context(out var config, out _);
        var cut = ctx.Render<AutopilotConcurrencySection>();

        cut.InvokeAsync(() => cut.Find("[data-testid=autopilot-concurrency-cap]").Input("two"));
        cut.InvokeAsync(() => cut.Find("[data-testid=autopilot-concurrency-save]").Click());

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=autopilot-concurrency-error]")));
        Assert.Null(config.Get(ConfigurationAutopilotConcurrencySettings.CapKey));
    }
}
