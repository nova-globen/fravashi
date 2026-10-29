using AngleSharp.Dom;
using Bunit;
using Fravashi.Application.Mockups;
using Fravashi.Application.Wiki;
using Fravashi.Domain.Wiki;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Wiki;
using Fravashi.Web.Components.Mockups;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 196: the mockup editor's outline and save, at the surface a person uses. Every outline action works by
/// button and by key, is undoable, and shows in the preview at once; a save with no edits writes nothing; a stale
/// save is refused with the choice to reload; and the prose around the tree is never touched.
/// </summary>
public sealed class DesignEditorPageTests : IDisposable
{
    private const string Before = "# Home\n\nThe home screen, as #12 asked.\n\n";
    private const string After = "\nWhat the tree cannot say.\n";

    private const string Tree = """
        {"mockup": 1, "screens": [{"type": "window", "id": "home", "text": "Home", "children": [
          {"type": "toolbar", "id": "bar", "children": [
            {"type": "button", "id": "a", "text": "A"},
            {"type": "button", "id": "b", "text": "B"}]},
          {"type": "panel", "id": "p", "text": "P", "children": []},
          {"type": "text", "id": "t", "text": "T"}]}]}

        """;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fravashi-tests", Guid.NewGuid().ToString("N"));

    private readonly SqliteWikiLibrary _wiki;

    public DesignEditorPageTests()
    {
        Directory.CreateDirectory(_directory);
        var factory = new SqliteConnectionFactory(new SqliteStoreOptions(Path.Combine(_directory, "test.db"), null, Pooling: false));
        SqliteSchema.EnsureCreated(factory);
        _wiki = new SqliteWikiLibrary(factory, TimeProvider.System, Fravashi.Domain.Projects.ProjectId.Default);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // every-feature.json (#199) uses every feature of @14, so the round trip covers states, overrides, interactions,
    // accessibility and data sets the agent-written attempts happen not to use.
    public static TheoryData<string> Fixtures() =>
        new() { "team-directory.attempt-1.json", "team-directory.attempt-2.json", "team-directory.attempt-3.json", "every-feature.json" };

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Mockups", "Fixtures", name));

    private static string Body(string json) => Before + "```mockup\n" + json + "```\n" + After;

    private async Task<WikiItem> DesignAsync(string json = Tree, WikiItemType type = WikiItemType.Design)
    {
        var created = await _wiki.CreateAsync(new NewWikiItem { Type = type, Title = "Home", Body = Body(json) });
        Assert.True(created.IsValid, string.Join("; ", created.Errors.Select(e => e.Code)));
        return created.Value;
    }

    private BunitContext Context()
    {
        var ctx = new BunitContext();
        // NavigationLock asks the browser to confirm leaving while there are edits; no browser here.
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IWikiLibraries>(new OneWiki(_wiki));
        ctx.Services.AddSingleton<Fravashi.Application.Projects.ICurrentProject>(Fravashi.Application.Projects.SingleProject.Instance);
        ctx.Services.AddSingleton<Fravashi.Application.Projects.IProjectStore>(new OneProjectStore());
        return ctx;
    }

    private static IRenderedComponent<DesignEditorPage> Open(BunitContext ctx, WikiItem item)
    {
        ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().NavigateTo($"http://localhost/wiki/{item.Number}/design");
        return ctx.Render<DesignEditorPage>(p => p.Add(c => c.Number, item.Number));
    }

    private static IElement Row(IRenderedComponent<DesignEditorPage> cut, string id) =>
        cut.FindAll("[data-testid=mockup-outline-row]").Single(r => r.QuerySelector(".mk-outline-id")?.TextContent == id);

    private static Task Select(IRenderedComponent<DesignEditorPage> cut, string id) => cut.InvokeAsync(() => Row(cut, id).Click());

    private static Task Press(IRenderedComponent<DesignEditorPage> cut, string key, bool alt = false, bool shift = false) =>
        cut.InvokeAsync(() => cut.Find("[data-testid=mockup-outline]").KeyDown(new KeyboardEventArgs { Key = key, AltKey = alt, ShiftKey = shift }));

    private static Task Ctrl(IRenderedComponent<DesignEditorPage> cut, string key, bool shift = false) =>
        cut.InvokeAsync(() => cut.Find("[data-testid=design-editor]").KeyDown(new KeyboardEventArgs { Key = key, CtrlKey = true, ShiftKey = shift }));

    private static Task Click(IRenderedComponent<DesignEditorPage> cut, string testId) =>
        cut.InvokeAsync(() => cut.Find($"[data-testid={testId}]").Click());

    /// <summary>The ids the PREVIEW draws under a node, in order: what a person sees, not what the model says.</summary>
    private static string[] Drawn(IRenderedComponent<DesignEditorPage> cut, string parentId) =>
        cut.Find($"[data-testid=mockup-preview] [data-mockup-id='{parentId}']")
            .QuerySelectorAll("[data-mockup-id]").Select(e => e.GetAttribute("data-mockup-id")!).ToArray();

    private static string Selected(IRenderedComponent<DesignEditorPage> cut) =>
        cut.Find("[data-testid=mockup-outline-row][aria-selected=true] .mk-outline-id").TextContent;

    private static string Announced(IRenderedComponent<DesignEditorPage> cut) =>
        cut.Find("[data-testid=mockup-announce]").TextContent.Trim();

    // ---- round trip ----------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Each_agent_written_fixture_opens_and_saving_it_with_no_edits_leaves_its_version_unchanged(string fixture)
    {
        var item = await DesignAsync(Fixture(fixture));
        using var ctx = Context();
        var cut = Open(ctx, item);

        Assert.NotEmpty(cut.FindAll("[data-testid=mockup-outline-row]"));
        Assert.Empty(cut.FindAll("[data-testid=design-editor-cannot-open]"));

        await Click(cut, "mockup-save");

        cut.WaitForAssertion(() => Assert.Contains("Nothing to save", cut.Find("[data-testid=mockup-status]").TextContent));
        var stored = await _wiki.GetByNumberAsync(item.Number);
        Assert.Equal(item.Version, stored!.Version);
        Assert.Equal(item.Body, stored.Body);
    }

    [Fact]
    public async Task A_saved_edit_replaces_only_the_block_with_the_canonical_tree_and_the_next_save_writes_nothing()
    {
        var agentWritten = Fixture("team-directory.attempt-3.json");
        var item = await DesignAsync(agentWritten);
        using var ctx = Context();
        var cut = Open(ctx, item);

        await Select(cut, "people-table");
        await Click(cut, "mockup-move-up");
        await Click(cut, "mockup-save");

        var stored = (await _wiki.GetByNumberAsync(item.Number))!;
        Assert.Equal(item.Version + 1, stored.Version);
        Assert.StartsWith(Before + "```mockup\n", stored.Body, StringComparison.Ordinal);
        Assert.EndsWith("```\n" + After, stored.Body, StringComparison.Ordinal);
        var read = DesignMockup.Read(stored.Body);
        Assert.True(read.IsValid);
        Assert.Equal(MockupWriter.Write(read.Tree!.Document!), read.Block!.Json);
        Assert.Equal(["people-table", "toolbar"], read.Tree.Document!.Screens[0].Children!.Take(2).Select(c => c.Id ?? "toolbar"));
        Assert.Contains($"Saved as version {stored.Version}", cut.Find("[data-testid=mockup-status]").TextContent);

        await Click(cut, "mockup-save");
        Assert.Equal(stored.Version, (await _wiki.GetByNumberAsync(item.Number))!.Version);
    }

    [Fact]
    public async Task A_save_against_a_version_somebody_else_moved_is_refused_and_reload_opens_what_is_stored()
    {
        var item = await DesignAsync();
        using var ctx = Context();
        var cut = Open(ctx, item);

        // Somebody else removes the text node and saves first.
        var theirs = Body(Tree.Replace(",\n  {\"type\": \"text\", \"id\": \"t\", \"text\": \"T\"}", string.Empty, StringComparison.Ordinal));
        Assert.NotEqual(Body(Tree), theirs);
        Assert.True((await _wiki.UpdateAsync(item.Id, new WikiItemEdit { Body = theirs }, item.Version)).IsValid);

        await Select(cut, "a");
        await Click(cut, "mockup-move-down");
        await Click(cut, "mockup-save");

        var stale = cut.Find("[data-testid=mockup-stale]");
        Assert.Contains("you opened version 1, it is now version 2", stale.TextContent);
        Assert.Contains("Nothing was written", stale.TextContent);
        Assert.Equal(theirs, (await _wiki.GetByNumberAsync(item.Number))!.Body);

        await Click(cut, "mockup-reload");

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=mockup-stale]")));
        Assert.DoesNotContain(cut.FindAll(".mk-outline-id"), e => e.TextContent == "t");
        Assert.Equal(["bar", "a", "b", "p"], Drawn(cut, "home"));
        Assert.True(cut.Find("[data-testid=mockup-undo]").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Leaving_with_unsaved_edits_asks_first()
    {
        var item = await DesignAsync();
        using var ctx = Context();
        var cut = Open(ctx, item);
        var nav = ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();

        await Select(cut, "t");
        await Click(cut, "mockup-remove");
        await cut.InvokeAsync(() => nav.NavigateTo("/wiki"));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=mockup-leave]")));
        Assert.EndsWith($"/wiki/{item.Number}/design", nav.Uri, StringComparison.Ordinal);

        await Click(cut, "mockup-leave-stay");
        Assert.Empty(cut.FindAll("[data-testid=mockup-leave]"));

        await cut.InvokeAsync(() => nav.NavigateTo("/wiki"));
        await Click(cut, "mockup-leave-discard");
        Assert.EndsWith("/wiki", nav.Uri, StringComparison.Ordinal);
        Assert.Equal(item.Version, (await _wiki.GetByNumberAsync(item.Number))!.Version);
    }

    [Fact]
    public async Task With_no_edits_leaving_asks_nothing()
    {
        var item = await DesignAsync();
        using var ctx = Context();
        var cut = Open(ctx, item);
        var nav = ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();

        await cut.InvokeAsync(() => nav.NavigateTo("/wiki"));

        Assert.EndsWith("/wiki", nav.Uri, StringComparison.Ordinal);
    }

    // ---- outline actions by button -------------------------------------------------------------------------------

    [Fact]
    public async Task Reorder_indent_outdent_and_delete_by_button_each_show_in_the_preview_and_undo_and_redo()
    {
        var item = await DesignAsync();
        using var ctx = Context();
        var cut = Open(ctx, item);
        Assert.Equal(["a", "b"], Drawn(cut, "bar"));

        // Move up.
        await Select(cut, "b");
        await Click(cut, "mockup-move-up");
        Assert.Equal(["b", "a"], Drawn(cut, "bar"));
        Assert.Equal("b", Selected(cut));
        Assert.Equal("b moved up, now 1 of 2 at level 3.", Announced(cut));
        await Click(cut, "mockup-undo");
        Assert.Equal(["a", "b"], Drawn(cut, "bar"));
        Assert.Equal("b", Selected(cut)); // the selection follows the node back to where it was
        await Click(cut, "mockup-redo");
        Assert.Equal(["b", "a"], Drawn(cut, "bar"));
        await Click(cut, "mockup-undo");

        // Move down.
        await Select(cut, "a");
        await Click(cut, "mockup-move-down");
        Assert.Equal(["b", "a"], Drawn(cut, "bar"));
        await Click(cut, "mockup-undo");
        Assert.Equal(["a", "b"], Drawn(cut, "bar"));

        // Indent: the text goes into the panel before it.
        await Select(cut, "t");
        await Click(cut, "mockup-indent");
        Assert.Equal(["t"], Drawn(cut, "p"));
        Assert.Equal("t indented into p, now 1 of 1 at level 3.", Announced(cut));
        await Click(cut, "mockup-undo");
        Assert.Empty(Drawn(cut, "p"));
        await Click(cut, "mockup-redo");
        Assert.Equal(["t"], Drawn(cut, "p"));

        // Outdent: and back out, to just after the panel.
        await Click(cut, "mockup-outdent");
        Assert.Empty(Drawn(cut, "p"));
        Assert.Equal(["bar", "a", "b", "p", "t"], Drawn(cut, "home"));
        Assert.Equal("t", Selected(cut));
        await Click(cut, "mockup-undo");
        Assert.Equal(["t"], Drawn(cut, "p"));
        await Click(cut, "mockup-undo");

        // Delete: the toolbar goes with its buttons.
        await Select(cut, "bar");
        await Click(cut, "mockup-remove");
        Assert.Equal(["p", "t"], Drawn(cut, "home"));
        Assert.Equal("bar deleted.", Announced(cut));
        await Click(cut, "mockup-undo");
        Assert.Equal(["bar", "a", "b", "p", "t"], Drawn(cut, "home"));
        await Click(cut, "mockup-redo");
        Assert.Equal(["p", "t"], Drawn(cut, "home"));
    }

    [Fact]
    public async Task A_move_the_reader_refuses_is_shown_and_announced_in_its_words_and_changes_nothing()
    {
        var item = await DesignAsync();
        using var ctx = Context();
        var cut = Open(ctx, item);

        // A button holds no children.
        await Select(cut, "b");
        await Click(cut, "mockup-indent");

        var refusal = cut.Find("[data-testid=mockup-refusal]").TextContent;
        var announced = Announced(cut);
        Assert.StartsWith("Not done: ", announced, StringComparison.Ordinal);
        Assert.Contains(announced["Not done: ".Length..], refusal, StringComparison.Ordinal);
        Assert.Equal(["a", "b"], Drawn(cut, "bar"));
        Assert.True(cut.Find("[data-testid=mockup-undo]").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Buttons_that_mean_nothing_where_the_node_is_are_disabled()
    {
        var item = await DesignAsync();
        using var ctx = Context();
        var cut = Open(ctx, item);

        Assert.True(cut.Find("[data-testid=mockup-move-up]").HasAttribute("disabled"));

        await Select(cut, "a");
        Assert.True(cut.Find("[data-testid=mockup-move-up]").HasAttribute("disabled"));
        Assert.True(cut.Find("[data-testid=mockup-indent]").HasAttribute("disabled"));
        Assert.False(cut.Find("[data-testid=mockup-move-down]").HasAttribute("disabled"));
        Assert.False(cut.Find("[data-testid=mockup-outdent]").HasAttribute("disabled"));
    }

    // ---- outline actions by key ----------------------------------------------------------------------------------

    [Fact]
    public async Task Reorder_indent_outdent_delete_undo_and_redo_by_keyboard()
    {
        var item = await DesignAsync();
        using var ctx = Context();
        var cut = Open(ctx, item);

        // Arrows choose: home, bar, a, b.
        await Press(cut, "ArrowDown");
        Assert.Equal("home", Selected(cut));
        await Press(cut, "End");
        Assert.Equal("t", Selected(cut));
        await Press(cut, "Home");
        await Press(cut, "ArrowDown");
        await Press(cut, "ArrowDown");
        await Press(cut, "ArrowDown");
        Assert.Equal("b", Selected(cut));
        Assert.Equal("mk-row-0-0-1", cut.Find("[data-testid=mockup-outline]").GetAttribute("aria-activedescendant"));

        // Alt+Up and Alt+Down move.
        await Press(cut, "ArrowUp", alt: true);
        Assert.Equal(["b", "a"], Drawn(cut, "bar"));
        await Press(cut, "ArrowDown", alt: true);
        Assert.Equal(["a", "b"], Drawn(cut, "bar"));

        // Ctrl+Z and Ctrl+Y (and Ctrl+Shift+Z) undo and redo.
        await Ctrl(cut, "z");
        Assert.Equal(["b", "a"], Drawn(cut, "bar"));
        await Ctrl(cut, "y");
        Assert.Equal(["a", "b"], Drawn(cut, "bar"));
        await Ctrl(cut, "z");
        await Ctrl(cut, "Z", shift: true);
        Assert.Equal(["a", "b"], Drawn(cut, "bar"));

        // Tab indents and Shift+Tab outdents; Alt+Right and Alt+Left do the same.
        await Select(cut, "t");
        await Press(cut, "Tab");
        Assert.Equal(["t"], Drawn(cut, "p"));
        await Press(cut, "Tab", shift: true);
        Assert.Empty(Drawn(cut, "p"));
        await Press(cut, "ArrowRight", alt: true);
        Assert.Equal(["t"], Drawn(cut, "p"));
        await Press(cut, "ArrowLeft", alt: true);
        Assert.Empty(Drawn(cut, "p"));

        // Delete removes, and selects what is nearest.
        await Press(cut, "Delete");
        Assert.Equal(["bar", "a", "b", "p"], Drawn(cut, "home"));
        Assert.Equal("p", Selected(cut));
        await Ctrl(cut, "z");
        Assert.Equal(["bar", "a", "b", "p", "t"], Drawn(cut, "home"));

        // Escape lets go, and Tab then belongs to the browser: nothing moves.
        await Press(cut, "Escape");
        Assert.Null(cut.Find("[data-testid=mockup-outline]").GetAttribute("aria-activedescendant"));
        await Press(cut, "Tab");
        Assert.Equal(["bar", "a", "b", "p", "t"], Drawn(cut, "home"));
    }

    [Fact]
    public async Task Ctrl_s_saves()
    {
        var item = await DesignAsync();
        using var ctx = Context();
        var cut = Open(ctx, item);

        await Select(cut, "t");
        await Press(cut, "Delete");
        await Ctrl(cut, "s");

        Assert.Equal(item.Version + 1, (await _wiki.GetByNumberAsync(item.Number))!.Version);
    }

    // ---- selection both ways -------------------------------------------------------------------------------------

    [Fact]
    public async Task Selecting_in_the_outline_selects_in_the_preview_and_the_reverse()
    {
        var item = await DesignAsync();
        using var ctx = Context();
        var cut = Open(ctx, item);

        await Select(cut, "p");
        var drawn = cut.Find("[data-testid=mockup-preview] [data-mockup-id=p]");
        Assert.Equal("true", drawn.GetAttribute("data-selected"));
        Assert.Contains("mk-selected", drawn.ClassList);
        Assert.Single(cut.FindAll("[data-testid=mockup-preview] [data-selected=true]"));

        await cut.InvokeAsync(() => cut.Find("[data-testid=mockup-preview] [data-mockup-id=b]").Click());

        Assert.Equal("b", Selected(cut));
        Assert.Equal("true", cut.Find("[data-testid=mockup-preview] [data-mockup-id=b]").GetAttribute("data-selected"));
        Assert.Contains("b", cut.Find("[data-testid=mockup-inspector]").TextContent);
    }

    [Fact]
    public async Task Selecting_a_node_inside_a_dialog_shows_the_dialog_in_the_preview()
    {
        var item = await DesignAsync(Fixture("team-directory.attempt-3.json"));
        using var ctx = Context();
        var cut = Open(ctx, item);
        Assert.Empty(cut.FindAll("[data-testid=mockup-preview] [data-mockup-id=add-form]"));

        await Select(cut, "add-form");

        Assert.Equal("true", cut.Find("[data-testid=mockup-preview] [data-mockup-id=add-form]").GetAttribute("data-selected"));
    }

    [Fact]
    public async Task An_edit_keeps_the_viewport_the_preview_was_showing()
    {
        var item = await DesignAsync(Fixture("team-directory.attempt-3.json"));
        using var ctx = Context();
        var cut = Open(ctx, item);
        await cut.InvokeAsync(() => cut.Find("[data-testid=mockup-preview-viewport]").Change("narrow"));
        Assert.Contains("390px", cut.Find("[data-testid=mockup-preview-frame]").GetAttribute("style"));

        await Select(cut, "add-person");
        await Press(cut, "ArrowDown", alt: true);
        Assert.Equal(["search", "refresh", "add-person"], Drawn(cut, "directory").Take(3));

        Assert.Contains("390px", cut.Find("[data-testid=mockup-preview-frame]").GetAttribute("style"));
    }

    // ---- what cannot be opened -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_design_with_two_blocks_is_not_opened_and_says_why()
    {
        var created = await _wiki.CreateAsync(new NewWikiItem
        {
            Type = WikiItemType.Design,
            Title = "Two",
            Body = "```mockup\n{}\n```\n\n```mockup\n{}\n```\n",
        });
        using var ctx = Context();
        var cut = Open(ctx, created.Value);

        Assert.Contains("2 mockup blocks", cut.Find("[data-testid=design-editor-cannot-open]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=mockup-outline]"));
    }

    [Fact]
    public async Task A_document_that_is_not_a_design_is_not_opened()
    {
        var item = await DesignAsync(type: WikiItemType.Plan);
        using var ctx = Context();
        var cut = Open(ctx, item);

        Assert.Contains("not a design", cut.Find("[data-testid=design-editor-not-design]").TextContent);
    }

    [Fact]
    public async Task An_invalid_tree_opens_with_its_errors_marked_in_the_outline()
    {
        var item = await DesignAsync(Fixture("team-directory.attempt-1.json"));
        using var ctx = Context();
        var cut = Open(ctx, item);

        Assert.NotEmpty(cut.FindAll("[data-testid=mockup-outline-error]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=mockup-preview-errors]"));
    }

    // ---- the palette (#197) --------------------------------------------------------------------------------------

    private static IElement PaletteItem(IRenderedComponent<DesignEditorPage> cut, string type) =>
        cut.Find($"[data-testid=mockup-palette-item][data-type={type}]");

    private static Task Insert(IRenderedComponent<DesignEditorPage> cut, string type, bool ctrl = false, bool alt = false) =>
        cut.InvokeAsync(() => PaletteItem(cut, type).Click(new MouseEventArgs { CtrlKey = ctrl, AltKey = alt }));

    private static string[] Outline(IRenderedComponent<DesignEditorPage> cut) =>
        cut.FindAll("[data-testid=mockup-outline-row]")
            .Select(r => r.QuerySelector(".mk-outline-id")?.TextContent ?? r.QuerySelector(".mk-outline-type")!.TextContent)
            .ToArray();

    [Fact]
    public async Task An_inserted_node_is_selected_announced_drawn_and_taken_back_by_one_undo()
    {
        var item = await DesignAsync();
        using var ctx = Context();
        var cut = Open(ctx, item);
        var opened = Outline(cut);

        await Select(cut, "p");
        await Insert(cut, "dialog");

        Assert.Equal(["home", "bar", "a", "b", "p", "t", "dialog"], Outline(cut));
        Assert.Equal("dialog", Selected(cut));
        Assert.Equal("dialog inserted, now 4 of 4 at level 2.", Announced(cut));

        // Auto with a dialog selected: inside it, and the preview draws it there at once.
        await Insert(cut, "text");
        Assert.Equal(["home", "bar", "a", "b", "p", "t", "dialog", "text"], Outline(cut));
        Assert.Equal("text \"Text\" inserted, now 1 of 1 at level 3.", Announced(cut));
        Assert.Contains("Text", cut.Find("[data-testid=mockup-preview] [data-mockup-id='dialog']").TextContent);

        await Ctrl(cut, "z");
        await Ctrl(cut, "z");

        Assert.Equal(opened, Outline(cut));
        Assert.True(cut.Find("[data-testid=mockup-undo]").HasAttribute("disabled"));
        Assert.Contains("no edits", cut.Find("[data-testid=mockup-status]").TextContent);
    }

    [Fact]
    public async Task Every_palette_type_has_a_glyph_and_an_inserted_link_shows_its_own_in_the_outline_and_its_target_in_the_inspector()
    {
        // Backlog 290: the link is a known component everywhere the editor names one, never the "?" of an unknown type.
        var item = await DesignAsync();
        using var ctx = Context();
        var cut = Open(ctx, item);

        var glyphs = cut.FindAll("[data-testid=mockup-palette-item] .mk-outline-glyph").Select(g => g.TextContent).ToArray();
        Assert.Equal(MockupPalette.Components.Count, glyphs.Length);
        Assert.DoesNotContain("?", glyphs);

        await Select(cut, "p");
        await Insert(cut, "link");

        var row = cut.Find("[data-testid=mockup-outline-row][aria-selected=true]");
        Assert.Equal("link", row.QuerySelector(".mk-outline-type")!.TextContent);
        Assert.Equal("Lk", row.QuerySelector(".mk-outline-glyph")!.TextContent);
        Assert.NotNull(cut.Find("[data-testid=inspector-href]"));
    }

    [Fact]
    public async Task Ctrl_click_inserts_inside_and_Alt_click_before_and_a_refused_place_says_why_and_changes_nothing()
    {
        var item = await DesignAsync();
        using var ctx = Context();
        var cut = Open(ctx, item);

        await Select(cut, "a");
        await Insert(cut, "text", ctrl: true);

        Assert.Contains("A button holds no children, so nothing goes inside it.", cut.Find("[data-testid=mockup-refusal]").TextContent);
        Assert.StartsWith("Not inserted:", Announced(cut));
        Assert.True(cut.Find("[data-testid=mockup-undo]").HasAttribute("disabled"));

        await Insert(cut, "text", alt: true);
        Assert.Equal(["home", "bar", "text", "a", "b", "p", "t"], Outline(cut));
        Assert.Empty(cut.FindAll("[data-testid=mockup-refusal]"));

        await Select(cut, "p");
        await Insert(cut, "field", ctrl: true);
        Assert.Equal(["home", "bar", "text", "a", "b", "p", "field", "t"], Outline(cut));
    }

    // #207: inside tabs, a new panel at Auto is a new tab after the one selected, not nested inside it; Ctrl+click
    // still reaches nesting it.
    [Fact]
    public async Task Auto_inserts_a_panel_after_a_selected_tab_as_a_new_tab_and_ctrl_click_still_nests_it()
    {
        const string tabsTree = """
            {"mockup": 1, "screens": [{"type": "window", "id": "home", "text": "Home", "children": [
              {"type": "tabs", "children": [
                {"type": "panel", "id": "details", "text": "Details", "children": [
                  {"type": "text", "id": "note", "text": "Note"}]}]}]}]}

            """;
        var item = await DesignAsync(tabsTree);
        using var ctx = Context();
        var cut = Open(ctx, item);

        await Select(cut, "details");
        await Insert(cut, "panel");

        Assert.Equal("panel \"Tab 2\" inserted, now 2 of 2 at level 3.", Announced(cut));
        var landed = cut.Find("[data-testid=mockup-outline-row][aria-selected=true]");
        Assert.Equal("$.screens[0].children[0].children[1]", landed.GetAttribute("data-path"));

        await Ctrl(cut, "z");
        await Select(cut, "details");
        await Insert(cut, "panel", ctrl: true);

        Assert.Equal("panel \"Panel\" inserted, now 2 of 2 at level 4.", Announced(cut));
    }

    [Fact]
    public async Task A_type_that_cannot_go_there_stays_reachable_and_says_why()
    {
        var item = await DesignAsync();
        using var ctx = Context();
        var cut = Open(ctx, item);

        // Nothing selected: only a new screen has a place.
        var button = PaletteItem(cut, "button");
        Assert.Equal("true", button.GetAttribute("aria-disabled"));
        Assert.False(button.HasAttribute("disabled"));
        var why = cut.Find($"#{button.GetAttribute("aria-describedby")}").TextContent;
        Assert.StartsWith("Choose where the button goes first", why);
        Assert.Equal(why, button.GetAttribute("title"));
        Assert.Null(PaletteItem(cut, "window").GetAttribute("aria-disabled"));

        await cut.InvokeAsync(() => PaletteItem(cut, "button").Focus());
        Assert.Equal($"button: {why}", cut.Find("[data-testid=mockup-palette-hint]").TextContent.Trim());

        // Choosing the place changes what is offered: inside a text node, nothing.
        await Select(cut, "t");
        await cut.InvokeAsync(() => cut.Find("[data-testid=mockup-palette-place]").Change("inside"));
        Assert.All(cut.FindAll("[data-testid=mockup-palette-item]"), e => Assert.Equal("true", e.GetAttribute("aria-disabled")));
        await cut.InvokeAsync(() => cut.Find("[data-testid=mockup-palette-place]").Change("after"));
        Assert.Null(PaletteItem(cut, "button").GetAttribute("aria-disabled"));
        Assert.Null(PaletteItem(cut, "window").GetAttribute("aria-disabled"));

        await Insert(cut, "window");
        Assert.Equal(["home", "bar", "a", "b", "p", "t", "screen"], Outline(cut));
        Assert.Equal("screen", Selected(cut));
    }
}
