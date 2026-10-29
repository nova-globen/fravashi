using Bunit;
using Fravashi.Application.Notifications;
using Fravashi.Infrastructure.Notifications;
using Fravashi.Web.Components.Shell;
using Fravashi.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// The activity rail's bottom action group. Backlog 23 put a second agent-panel toggle here as the
/// fallback for a studio with no window title bar (a dev <c>dotnet run</c> in a browser), because the
/// title bar is drawn only under <c>html.window-host</c> and a panel with no control anywhere would be
/// stranded. Which of the two a user sees is a CSS decision, held by
/// <see cref="StudioStylesheetGuardTests"/>.
/// </summary>
public class ActivityBarTests
{
    private static BunitContext RailContext(AgentPanelState panel)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(panel);
        ctx.Services.AddSingleton<INotificationHistory>(new InMemoryNotificationHistory());
        ctx.Services.AddSingleton<IWaitingForPersonList>(new StubWaitingForPersonList());
        ctx.Services.AddScoped<ThemeState>();
        return ctx;
    }

    [Fact]
    public void The_bottom_group_carries_the_fallback_agent_toggle_while_a_panel_is_there_to_toggle()
    {
        var panel = new AgentPanelState();
        panel.Attach();
        using var ctx = RailContext(panel);

        var cut = ctx.Render<ActivityBar>();

        var toggle = cut.Find(".studio-rail-bottom [data-testid=rail-agent-toggle]");
        // The class is what the stylesheet hides inside the window shell; without it the studio would
        // show two toggles there.
        Assert.Contains("studio-agent-fallback", toggle.GetAttribute("class"));
    }

    [Fact]
    public void Off_the_page_that_draws_the_panel_the_rail_shows_no_agent_toggle()
    {
        using var ctx = RailContext(new AgentPanelState());

        var cut = ctx.Render<ActivityBar>();

        Assert.Empty(cut.FindAll("[data-testid=rail-agent-toggle]"));
    }
}
