using Bunit;
using Fravashi.Web.Components.Shell;
using Xunit;

namespace Fravashi.Web.Tests;

public class PageSidebarSectionTests
{
    [Fact]
    public void Renders_the_title_and_body_expanded_by_default()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<PageSidebarSection>(p => p
            .Add(c => c.Title, "Open Editors")
            .Add(c => c.ChildContent, "<span id=\"inner\">body</span>"));

        Assert.Contains("Open Editors", cut.Markup);
        Assert.NotEmpty(cut.FindAll("#inner"));
        Assert.Equal("true", cut.Find(".page-sidebar-section-header").GetAttribute("aria-expanded"));
    }

    [Fact]
    public void Clicking_the_header_collapses_and_reexpands_the_body()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<PageSidebarSection>(p => p
            .Add(c => c.Title, "Search")
            .Add(c => c.ChildContent, "<span id=\"inner\">body</span>"));

        cut.Find(".page-sidebar-section-header").Click();
        Assert.Empty(cut.FindAll("#inner"));
        Assert.Equal("false", cut.Find(".page-sidebar-section-header").GetAttribute("aria-expanded"));

        cut.Find(".page-sidebar-section-header").Click();
        Assert.NotEmpty(cut.FindAll("#inner"));
    }

    [Fact]
    public void Can_start_collapsed()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<PageSidebarSection>(p => p
            .Add(c => c.Title, "Search")
            .Add(c => c.InitiallyCollapsed, true)
            .Add(c => c.ChildContent, "<span id=\"inner\">body</span>"));

        Assert.Empty(cut.FindAll("#inner"));
    }
}
