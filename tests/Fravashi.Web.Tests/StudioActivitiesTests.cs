using System;
using System.Linq;
using Fravashi.Web.Components.Shell;
using Xunit;

namespace Fravashi.Web.Tests;

public class StudioActivitiesTests
{
    [Fact]
    public void No_activity_advertises_the_dead_autopilot_route()
    {
        // Backlog 70: the Autopilot area was dead scaffolding (no /autopilot route ever existed). The
        // shell must offer no navigation to it, and the path must simply fall back to home.
        var everyActivity = StudioActivities.All
            .Append(StudioActivities.Settings)
            .Append(StudioActivities.Home);

        Assert.DoesNotContain(
            everyActivity.SelectMany(a => a.Routes),
            route => route.Equals("/autopilot", StringComparison.OrdinalIgnoreCase)
                || route.StartsWith("/autopilot/", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(StudioArea.Home, StudioActivities.ForPath("/autopilot").Area);
    }

    [Fact]
    public void StudioArea_no_longer_defines_autopilot() =>
        // Backlog 70: the enum value was removed along with its icon case and explorer entry.
        Assert.DoesNotContain("Autopilot", Enum.GetNames<StudioArea>());

    [Fact]
    public void Root_and_empty_resolve_to_home()
    {
        Assert.Equal(StudioArea.Home, StudioActivities.ForPath("/").Area);
        Assert.Equal(StudioArea.Home, StudioActivities.ForPath(null).Area);
        Assert.Equal(StudioArea.Home, StudioActivities.ForPath(string.Empty).Area);
    }

    [Theory]
    [InlineData("/settings")]
    [InlineData("/settings/environment")]
    public void Settings_route_resolves_to_settings(string path) =>
        Assert.Equal(StudioArea.Settings, StudioActivities.ForPath(path).Area);

    [Fact]
    public void Unknown_path_falls_back_to_home() =>
        Assert.Equal(StudioArea.Home, StudioActivities.ForPath("/nope").Area);

    [Fact]
    public void Welcome_route_resolves_to_home()
    {
        // Backlog 33: /welcome is the merged home surface's alternate route (installed fravashi.exe
        // binaries bake it as the bare-launch URL), so the rail's brand stays highlighted there.
        Assert.Equal(StudioArea.Home, StudioActivities.ForPath("/welcome").Area);
        Assert.Contains("/welcome", StudioActivities.Home.AltRoutes);
    }

    [Fact]
    public void The_graph_the_code_and_the_conversations_are_the_looped_activities()
    {
        // Home is reached via the brand logo and Settings via the bottom gear button (ActivityBar.razor),
        // so neither needs a looped entry. Sessions joined them in plan 2e (decision 3), the Board in
        // backlog 38 and the Wiki in backlog 39: the graph's two projections lead, because they are what the work is
        // described in before any of it is written. Backlog 303's autopilot page left in work item #358.
        Assert.Equal(5, StudioActivities.All.Count);
        Assert.Equal(StudioArea.Board, StudioActivities.All[0].Area);
        Assert.Equal("/board", StudioActivities.All[0].Route);
        Assert.Equal(StudioArea.Docs, StudioActivities.All[1].Area);
        Assert.Equal("/wiki", StudioActivities.All[1].Route);
        Assert.Equal(StudioArea.Code, StudioActivities.All[2].Area);
        Assert.Equal("/code", StudioActivities.All[2].Route);
        Assert.Equal(StudioArea.Git, StudioActivities.All[3].Area);
        Assert.Equal("/git", StudioActivities.All[3].Route);
        Assert.Equal(StudioArea.Sessions, StudioActivities.All[4].Area);
        Assert.Equal("/sessions", StudioActivities.All[4].Route);
        Assert.All(StudioActivities.All, a => Assert.False(a.HasSidebar));
    }

    [Theory]
    [InlineData("/wiki")]
    [InlineData("/wiki/12")]
    public void The_wiki_routes_resolve_to_the_docs_area(string path) =>
        Assert.Equal(StudioArea.Docs, StudioActivities.ForPath(path).Area);

    [Theory]
    [InlineData("/board")]
    [InlineData("/workitems")]
    [InlineData("/workitems/12")]
    public void The_board_and_the_work_item_routes_resolve_to_one_area(string path) =>
        // Backlog 38: the list and the detail are the same page as the board, so the rail must not look
        // like the reader left the area when they open an item.
        Assert.Equal(StudioArea.Board, StudioActivities.ForPath(path).Area);

    [Theory]
    [InlineData("/sessions")]
    [InlineData("/sessions/6f9619ff-8b86-d011-b42d-00c04fc964ff")]
    public void Sessions_route_resolves_to_sessions(string path) =>
        Assert.Equal(StudioArea.Sessions, StudioActivities.ForPath(path).Area);

    [Theory]
    [InlineData("/code")]
    [InlineData("/code/anything")]
    public void Code_route_resolves_to_code(string path) =>
        Assert.Equal(StudioArea.Code, StudioActivities.ForPath(path).Area);

    [Fact]
    public void Home_has_no_sidebar()
    {
        Assert.Equal(StudioArea.Home, StudioActivities.Home.Area);
        Assert.False(StudioActivities.Home.HasSidebar);
    }
}
