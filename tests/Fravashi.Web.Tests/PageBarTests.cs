using Bunit;
using Fravashi.Web.Components.Shell;
using Xunit;

namespace Fravashi.Web.Tests;

public class PageBarTests
{
    [Fact]
    public void Renders_the_title_without_any_notification_center()
    {
        // Notifications and the theme toggle live in the activity rail's bottom group now, not here.
        using var ctx = new BunitContext();

        var cut = ctx.Render<PageBar>(p => p.Add(c => c.Title, "Dashboard"));

        Assert.Equal("Dashboard", cut.Find(".studio-page-bar-title").TextContent);
        Assert.Empty(cut.FindAll(".notification-center-button"));
    }

    [Fact]
    public void The_title_is_the_one_level_one_heading_by_role_and_name()
    {
        // Backlog 291: a screen reader moves by headings, and a design check finds a window's title as one.
        // A heading of level one is an h1 (implicit role) or anything with role heading and aria-level 1.
        using var ctx = new BunitContext();

        var cut = ctx.Render<PageBar>(p => p
            .Add(c => c.Title, "Not found")
            .Add(c => c.ChildContent, "<div class=\"settings-nav\">tabs</div>"));

        var headings = cut.FindAll("h1, [role=heading][aria-level=\"1\"]");
        var heading = Assert.Single(headings);
        Assert.Equal("Not found", heading.TextContent.Trim());
        Assert.Null(heading.GetAttribute("aria-hidden"));
        Assert.Contains("studio-page-bar-title", heading.ClassList);
    }

    [Fact]
    public void Renders_optional_child_content_as_the_page_nav()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<PageBar>(p => p
            .Add(c => c.Title, "Settings")
            .Add(c => c.ChildContent, "<div class=\"settings-nav\">tabs</div>"));

        Assert.NotEmpty(cut.FindAll(".settings-nav"));
    }

    [Fact]
    public void Renders_no_extra_nav_element_when_no_child_content_is_given()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<PageBar>(p => p.Add(c => c.Title, "Dashboard"));

        Assert.Empty(cut.FindAll(".settings-nav"));
    }
}
