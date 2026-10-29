using Fravashi.Web.Services;
using Xunit;

namespace Fravashi.Web.Tests;

public class CodeWorkspaceTests
{
    private static CodeWorkspace WithTabs(params string[] paths)
    {
        var ws = new CodeWorkspace();
        foreach (var path in paths)
        {
            ws.OpenAt(path, path);
        }

        return ws;
    }

    [Fact]
    public void Pinning_moves_a_tab_between_the_pinned_and_unpinned_views()
    {
        var ws = WithTabs("a", "b", "c");

        ws.SetPinned("b", true);

        Assert.Equal(["b"], ws.Pinned.Select(f => f.Path));
        Assert.Equal(["a", "c"], ws.Unpinned.Select(f => f.Path));

        ws.SetPinned("b", false);
        Assert.Empty(ws.Pinned);
    }

    [Fact]
    public void Close_others_keeps_the_target_and_pinned_tabs()
    {
        var ws = WithTabs("a", "b", "c", "d");
        ws.SetPinned("a", true);

        ws.CloseOthers("c");

        Assert.Equal(["a", "c"], ws.Open.Select(f => f.Path));
        Assert.Equal("c", ws.ActivePath);
    }

    [Fact]
    public void Close_to_the_right_only_touches_unpinned_tabs_after_the_target()
    {
        var ws = WithTabs("a", "b", "c", "d", "e");
        ws.SetPinned("e", true);
        ws.Activate("d");

        ws.CloseToTheRight("b");

        // c and d close (unpinned, right of b); pinned e survives. The active tab d was closed, so the
        // right-click target becomes active.
        Assert.Equal(["a", "b", "e"], ws.Open.Select(f => f.Path));
        Assert.Equal("b", ws.ActivePath);
    }

    [Fact]
    public void Close_all_but_pinned_leaves_only_pins_and_fixes_the_active_tab()
    {
        var ws = WithTabs("a", "b", "c");
        ws.SetPinned("a", true);
        ws.Activate("c");

        ws.CloseAllButPinned();

        Assert.Equal(["a"], ws.Open.Select(f => f.Path));
        Assert.Equal("a", ws.ActivePath);
    }

    [Fact]
    public void Cycle_wraps_forward_and_backward()
    {
        var ws = WithTabs("a", "b", "c");
        ws.Activate("c");

        Assert.Equal("a", ws.Cycle(1));
        Assert.Equal("b", ws.Cycle(-1));
    }

    [Fact]
    public void Recent_first_tracks_activation_order()
    {
        var ws = WithTabs("a", "b", "c");

        ws.Activate("a");

        // MRU: a (just touched), c (last opened), b.
        Assert.Equal(["a", "c", "b"], ws.RecentFirst.Select(f => f.Path));

        ws.Close("a");
        Assert.Equal(["c", "b"], ws.RecentFirst.Select(f => f.Path));
    }

    [Fact]
    public void Hydrate_restores_pins()
    {
        var ws = new CodeWorkspace();

        ws.Hydrate([new CodeWorkspace.OpenFile("a", "a", IsPinned: true), new CodeWorkspace.OpenFile("b", "b")], "b");

        Assert.True(ws.Open.Single(f => f.Path == "a").IsPinned);
        Assert.Equal("b", ws.ActivePath);
    }

    // ---- the preview tab (work item #366) ----

    [Fact]
    public void A_preview_replaces_the_previous_preview_in_place_and_says_which()
    {
        var ws = WithTabs("a");

        Assert.Null(ws.OpenAsPreview("b", "b"));
        ws.OpenAt("c", "c");
        var replaced = ws.OpenAsPreview("d", "d");

        Assert.Equal("b", replaced);
        Assert.Equal(["a", "d", "c"], ws.Open.Select(f => f.Path));
        Assert.Equal("d", ws.PreviewPath);
        Assert.Equal("d", ws.ActivePath);
        Assert.DoesNotContain(ws.RecentFirst, f => f.Path == "b");
    }

    [Fact]
    public void A_file_already_open_keeps_its_ordinary_tab_when_followed()
    {
        var ws = WithTabs("a", "b");

        Assert.Null(ws.OpenAsPreview("a", "a"));

        Assert.Equal("a", ws.ActivePath);
        Assert.Null(ws.PreviewPath);
        Assert.Equal(2, ws.Open.Count);
    }

    [Fact]
    public void Promoting_or_pinning_a_preview_makes_it_an_ordinary_tab_that_the_next_preview_leaves_alone()
    {
        var ws = WithTabs("a");
        ws.OpenAsPreview("b", "b");

        ws.Promote("b");
        Assert.Null(ws.PreviewPath);
        Assert.Null(ws.OpenAsPreview("c", "c"));

        ws.SetPinned("c", true);
        Assert.Null(ws.PreviewPath);
        Assert.Null(ws.OpenAsPreview("d", "d"));

        Assert.Equal(["a", "b", "c", "d"], ws.Open.Select(f => f.Path));
    }
}
