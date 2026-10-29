using Bunit;
using Fravashi.Web.Components.Shell;
using Xunit;

namespace Fravashi.Web.Tests;

public class PageSplitTests
{
    [Fact]
    public void Renders_the_sidebar_and_body_fragments_with_a_resize_handle_between_them()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<PageSplit>(p => p
            .Add(c => c.SidebarLabel, "Example sections")
            .Add(c => c.Sidebar, "<a class=\"page-sidebar-item\">One</a>")
            .Add(c => c.Body, "<p>Body content</p>"));

        Assert.NotEmpty(cut.FindAll(".page-sidebar-item"));
        Assert.Contains("Body content", cut.Markup);

        var handle = cut.Find(".page-split-handle");
        Assert.Equal("separator", handle.GetAttribute("role"));
        Assert.Equal("vertical", handle.GetAttribute("aria-orientation"));
        Assert.Equal("0", handle.GetAttribute("tabindex"));
    }

    [Fact]
    public void Applies_extra_sidebar_and_body_css_classes_alongside_the_base_ones()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<PageSplit>(p => p
            .Add(c => c.Sidebar, "<span>nav</span>")
            .Add(c => c.Body, "<span>content</span>")
            .Add(c => c.SidebarCssClass, "framework-sidebar")
            .Add(c => c.BodyCssClass, "framework-detail"));

        var sidebar = cut.Find("nav");
        var body = cut.Find(".page-body");
        Assert.Contains("page-sidebar", sidebar.GetAttribute("class"));
        Assert.Contains("framework-sidebar", sidebar.GetAttribute("class"));
        Assert.Contains("framework-detail", body.GetAttribute("class"));
    }

    [Fact]
    public void Sets_the_sidebar_aria_label_and_test_ids()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<PageSplit>(p => p
            .Add(c => c.SidebarLabel, "Example sections")
            .Add(c => c.Sidebar, "<span>nav</span>")
            .Add(c => c.Body, "<span>content</span>")
            .Add(c => c.SidebarTestId, "example-sidebar")
            .Add(c => c.BodyTestId, "example-body"));

        Assert.Equal("Example sections", cut.Find("nav").GetAttribute("aria-label"));
        Assert.NotEmpty(cut.FindAll("[data-testid=example-sidebar]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=example-body]"));
    }

    // ---- work item #364: collapse, reset, a quarter of the window at most ----------------------------

    [Fact]
    public void The_panel_carries_a_collapse_chevron_and_a_strip_with_an_expand_button()
    {
        // Collapsed is a per-window preference applied on <html> before paint, so both controls are always
        // rendered and the stylesheet shows the one that applies: the panel can never be lost.
        using var ctx = new BunitContext();

        var cut = ctx.Render<PageSplit>(p => p
            .Add(c => c.Sidebar, "<span>nav</span>")
            .Add(c => c.Body, "<span>content</span>"));

        var collapse = cut.Find("nav .page-sidebar-foot [data-testid=page-split-collapse]");
        Assert.Equal("collapse", collapse.GetAttribute("data-page-split-toggle"));
        Assert.Equal("true", collapse.GetAttribute("aria-expanded"));
        Assert.Contains("Ctrl+B", collapse.GetAttribute("aria-label"), StringComparison.Ordinal);
        Assert.Equal("button", collapse.GetAttribute("type"));

        var expand = cut.Find(".page-split > .page-split-strip [data-testid=page-split-expand]");
        Assert.Equal("expand", expand.GetAttribute("data-page-split-toggle"));
        Assert.Equal("false", expand.GetAttribute("aria-expanded"));
        Assert.Contains("Ctrl+B", expand.GetAttribute("aria-label"), StringComparison.Ordinal);

        // The chevron follows the page's own sidebar content, so it never displaces it.
        var nav = cut.Find("nav");
        Assert.Equal("page-sidebar-foot", nav.Children[^1].GetAttribute("class"));
    }

    [Fact]
    public void The_stylesheet_caps_the_panel_at_a_quarter_of_the_window_and_hides_it_when_collapsed()
    {
        var css = File.ReadAllText(Path.Combine(WebProject(), "wwwroot", "studio.css"));

        Assert.Contains("clamp(180px, var(--page-sidebar-width, 264px), min(480px, 25vw))", css, StringComparison.Ordinal);
        Assert.Contains("html[data-page-sidebar=\"collapsed\"] .page-split > .page-sidebar", css, StringComparison.Ordinal);
        Assert.Contains("html[data-page-sidebar=\"collapsed\"] .page-split > .page-split-strip", css, StringComparison.Ordinal);
    }

    [Fact]
    public void A_work_item_title_keeps_a_measure_of_its_own_and_the_actions_wrap_below_it()
    {
        // The board's work item page sits in this split's body; at a third of the window the status picker and
        // buttons squeezed the title to one word per line.
        var css = File.ReadAllText(Path.Combine(WebProject(), "wwwroot", "studio.css"));

        Assert.Contains(".work-item-detail-head { flex-wrap: wrap;", css, StringComparison.Ordinal);
        Assert.Contains("flex: 1 1 28rem; min-width: min(100%, 28rem);", css, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shipped_scripts_wire_collapse_reset_and_the_window_share()
    {
        // bunit cannot reach a handler that lives in JS, so this reads the shipped files: every control above
        // is inert without its delegated listener, and a missing listener is silent.
        var web = WebProject();
        var split = File.ReadAllText(Path.Combine(web, "wwwroot", "pageSplit.js"));
        var bootstrap = File.ReadAllText(Path.Combine(web, "wwwroot", "bootstrap.js"));

        Assert.Contains("MAX_SHARE = 0.25", split, StringComparison.Ordinal);
        Assert.Contains("addEventListener('dblclick'", split, StringComparison.Ordinal);
        Assert.Contains("DEFAULT_WIDTH = 264", split, StringComparison.Ordinal);
        Assert.Contains("[data-page-split-toggle]", split, StringComparison.Ordinal);
        Assert.Contains("fravashi-page-sidebar-collapsed", split, StringComparison.Ordinal);
        Assert.Contains(".monaco-editor", split, StringComparison.Ordinal);

        Assert.Contains("fravashi-page-sidebar-collapsed", bootstrap, StringComparison.Ordinal);
        Assert.Contains("'data-page-sidebar', 'collapsed'", bootstrap, StringComparison.Ordinal);
    }

    private static string WebProject()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Fravashi.sln")))
        {
            root = root.Parent;
        }

        Assert.True(root is not null, "the test must be able to locate the repository root (Fravashi.sln)");
        return Path.Combine(root!.FullName, "src", "Fravashi.Web");
    }
}
