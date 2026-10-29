using Bunit;
using Fravashi.Application.Notifications;
using Fravashi.Application.Persistence;
using Fravashi.Infrastructure.Notifications;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Web.Components.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 62: Settings &gt; Agents &gt; Spending and limits. What is held here is what a reader cannot work out
/// from three text boxes: that empty means no limit, that a typo is refused rather than quietly becoming
/// no limit, and that what is saved is what the studio will actually read back.
/// </summary>
public class SpendSettingsSectionTests
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
    public void An_unconfigured_studio_shows_three_empty_fields_and_nothing_to_save()
    {
        using var ctx = Context(out _, out _);

        var cut = ctx.Render<SpendSettingsSection>();

        Assert.Equal(string.Empty, cut.Find("[data-testid=spend-limit-session]").GetAttribute("value"));
        Assert.Equal(string.Empty, cut.Find("[data-testid=spend-limit-day]").GetAttribute("value"));
        Assert.Equal(string.Empty, cut.Find("[data-testid=spend-limit-month]").GetAttribute("value"));
        Assert.True(cut.Find("[data-testid=spend-limit-save]").HasAttribute("disabled"));
    }

    [Fact]
    public void The_per_session_ceiling_is_said_to_be_the_agent_clis_and_able_to_overshoot()
    {
        // Decided 2026-09-14 (backlog 25 increment 7): --max-budget-usd stays opt-in, and because
        // the CLI was measured spending about ten times its ceiling, the page says who enforces it and that
        // it is not a hard stop.
        using var ctx = Context(out _, out _);

        var text = ctx.Render<SpendSettingsSection>().Markup;

        Assert.Contains("enforced by the agent CLI", text, StringComparison.Ordinal);
        Assert.Contains("can overshoot", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_stored_limits_are_shown_the_way_they_will_be_read()
    {
        // A stored row nothing can parse is NOT a limit, so showing it in the box would tell the owner
        // they are protected when they are not.
        using var ctx = Context(out var config, out _);
        config.Set(ConfigurationAgentSpendLimits.PerSessionKey, "5.00");
        config.Set(ConfigurationAgentSpendLimits.PerDayKey, "2,50");
        config.Set(ConfigurationAgentSpendLimits.PerMonthKey, "100");

        var cut = ctx.Render<SpendSettingsSection>();

        Assert.Equal("5", cut.Find("[data-testid=spend-limit-session]").GetAttribute("value"));
        Assert.Equal(string.Empty, cut.Find("[data-testid=spend-limit-day]").GetAttribute("value"));
        Assert.Equal("100", cut.Find("[data-testid=spend-limit-month]").GetAttribute("value"));
    }

    [Fact]
    public void Saving_writes_the_three_rows_and_reloads_configuration()
    {
        // The reload is what makes a limit bite the next turn rather than the next restart.
        using var ctx = Context(out var config, out var reloader);
        var cut = ctx.Render<SpendSettingsSection>();

        cut.InvokeAsync(() =>
        {
            cut.Find("[data-testid=spend-limit-session]").Input("5");
            cut.Find("[data-testid=spend-limit-day]").Input("10.50");
            cut.Find("[data-testid=spend-limit-month]").Input("200");
        });
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=spend-limit-save]").HasAttribute("disabled")));
        cut.InvokeAsync(() => cut.Find("[data-testid=spend-limit-save]").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("5", config.Get(ConfigurationAgentSpendLimits.PerSessionKey));
            Assert.Equal("10.5", config.Get(ConfigurationAgentSpendLimits.PerDayKey));
            Assert.Equal("200", config.Get(ConfigurationAgentSpendLimits.PerMonthKey));
            Assert.Equal(1, reloader.ReloadCount);
        });
    }

    [Fact]
    public void Clearing_a_field_clears_the_limit()
    {
        using var ctx = Context(out var config, out _);
        config.Set(ConfigurationAgentSpendLimits.PerDayKey, "10");
        var cut = ctx.Render<SpendSettingsSection>();

        cut.InvokeAsync(() => cut.Find("[data-testid=spend-limit-day]").Input(string.Empty));
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=spend-limit-save]").HasAttribute("disabled")));
        cut.InvokeAsync(() => cut.Find("[data-testid=spend-limit-save]").Click());

        cut.WaitForAssertion(
            () => Assert.Equal(string.Empty, config.Get(ConfigurationAgentSpendLimits.PerDayKey)));
    }

    [Fact]
    public void A_value_that_is_not_an_amount_is_refused_rather_than_saved_as_no_limit()
    {
        // The one direction the reader would never notice: nothing stops, everything looks configured.
        using var ctx = Context(out var config, out var reloader);
        var cut = ctx.Render<SpendSettingsSection>();

        cut.InvokeAsync(() => cut.Find("[data-testid=spend-limit-day]").Input("ten dollars"));
        cut.InvokeAsync(() => cut.Find("[data-testid=spend-limit-save]").Click());

        cut.WaitForAssertion(() => Assert.Contains(
            "is not an amount above zero",
            cut.Find("[data-testid=spend-limit-error]").TextContent,
            StringComparison.Ordinal));
        Assert.Null(config.Get(ConfigurationAgentSpendLimits.PerDayKey));
        Assert.Equal(0, reloader.ReloadCount);
    }

    [Fact]
    public void Zero_is_refused_too_because_it_reads_as_a_limit_and_behaves_as_none()
    {
        using var ctx = Context(out var config, out _);
        var cut = ctx.Render<SpendSettingsSection>();

        cut.InvokeAsync(() => cut.Find("[data-testid=spend-limit-month]").Input("0"));
        cut.InvokeAsync(() => cut.Find("[data-testid=spend-limit-save]").Click());

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=spend-limit-error]")));
        Assert.Null(config.Get(ConfigurationAgentSpendLimits.PerMonthKey));
    }

    [Fact]
    public void The_page_says_the_day_and_month_windows_are_utc()
    {
        // A ceiling that resets at an instant the reader cannot predict is a ceiling they cannot plan
        // around, and UTC midnight is the middle of the owner's evening.
        using var ctx = Context(out _, out _);

        var cut = ctx.Render<SpendSettingsSection>();

        Assert.Contains("UTC", cut.Find("[data-testid=spend-settings]").TextContent, StringComparison.Ordinal);
    }
}
