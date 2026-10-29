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
/// Backlog 198: the inspector edits a node's content and layout at the surface a person uses. Every property each type
/// carries is editable and shows in the PREVIEW at once, one undo takes a change back, a property a type does not carry
/// is never offered, and clearing a field's label leaves the reader's sentence on the node without losing the edit or
/// stopping Save.
/// </summary>
public sealed class DesignInspectorTests : IDisposable
{
    private const string Tree = """
        {"mockup": 1, "title": "People",
         "data": {"people": [{"name": "Ada", "role": "Admin"}, {"name": "Lin", "role": "Member"}]},
         "screens": [
          {"type": "window", "id": "home", "text": "Home", "children": [
            {"type": "toolbar", "id": "bar", "children": [{"type": "button", "id": "go", "text": "Go"}]},
            {"type": "panel", "id": "side", "text": "Side", "children": [{"type": "text", "id": "words", "text": "Hello"}]},
            {"type": "form", "id": "edit", "children": [
              {"type": "field", "id": "name", "text": "Name"},
              {"type": "field", "id": "role", "text": "Role", "input": "select", "options": ["Admin", "Member"]},
              {"type": "field", "id": "ok", "text": "Agree", "input": "checkbox"}]},
            {"type": "container", "id": "box", "children": [{"type": "image", "id": "logo", "text": "Logo", "a11y": {"decorative": true}}]},
            {"type": "tabs", "id": "pages", "children": [{"type": "panel", "id": "one", "text": "One", "children": []}]},
            {"type": "list", "id": "names", "items": ["Ada", "Lin"]},
            {"type": "tree", "id": "folders", "items": ["A"]},
            {"type": "table", "id": "people", "columns": [{"key": "name", "text": "Name"}], "items": [{"name": "Ada"}]},
            {"type": "dialog", "id": "confirm", "text": "Sure?", "children": []}]}]}

        """;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fravashi-tests", Guid.NewGuid().ToString("N"));

    private readonly SqliteWikiLibrary _wiki;

    public DesignInspectorTests()
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

    private const string BoundTableTree = """
        {"mockup": 1, "title": "Team",
         "data": {"team": [{"name": "Ada", "role": "Admin", "office": "A12"}]},
         "screens": [
          {"type": "window", "id": "home", "text": "Home", "children": [
            {"type": "table", "id": "team", "data": "team", "columns": ["name"]}]}]}

        """;

    private async Task<WikiItem> DesignAsync(string json = Tree)
    {
        var created = await _wiki.CreateAsync(new NewWikiItem
        {
            Type = WikiItemType.Design, Title = "People", Body = "The people screen.\n\n```mockup\n" + json + "```\n",
        });
        Assert.True(created.IsValid);
        return created.Value;
    }

    private BunitContext Context()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IWikiLibraries>(new OneWiki(_wiki));
        ctx.Services.AddSingleton<Fravashi.Application.Projects.ICurrentProject>(Fravashi.Application.Projects.SingleProject.Instance);
        ctx.Services.AddSingleton<Fravashi.Application.Projects.IProjectStore>(new OneProjectStore());
        return ctx;
    }

    private async Task<(BunitContext Ctx, IRenderedComponent<DesignEditorPage> Cut, WikiItem Item)> OpenAsync(string json = Tree)
    {
        var item = await DesignAsync(json);
        var ctx = Context();
        ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().NavigateTo($"http://localhost/wiki/{item.Number}/design");
        return (ctx, ctx.Render<DesignEditorPage>(p => p.Add(c => c.Number, item.Number)), item);
    }

    private static Task Select(IRenderedComponent<DesignEditorPage> cut, string id) =>
        cut.InvokeAsync(() => cut.FindAll("[data-testid=mockup-outline-row]")
            .Single(r => r.QuerySelector(".mk-outline-id")?.TextContent == id).Click());

    private static IElement Control(IRenderedComponent<DesignEditorPage> cut, string testId, int index = 0) =>
        cut.FindAll($"[data-testid=mockup-inspector] [data-testid={testId}]")[index];

    private static Task Edit(IRenderedComponent<DesignEditorPage> cut, string testId, object value, int index = 0) =>
        cut.InvokeAsync(() => Control(cut, testId, index).Change(value));

    private static Task Press(IRenderedComponent<DesignEditorPage> cut, string testId, int index = 0) =>
        cut.InvokeAsync(() => Control(cut, testId, index).Click());

    private static Task Undo(IRenderedComponent<DesignEditorPage> cut) =>
        cut.InvokeAsync(() => cut.Find("[data-testid=design-editor]").KeyDown(new KeyboardEventArgs { Key = "z", CtrlKey = true }));

    /// <summary>What the PREVIEW draws for a node: what a person sees, not what the model says.</summary>
    private static IElement Drawn(IRenderedComponent<DesignEditorPage> cut, string id) =>
        cut.Find($"[data-testid=mockup-preview] [data-mockup-id='{id}']");

    private static string[] Offered(IRenderedComponent<DesignEditorPage> cut) =>
        [.. cut.FindAll("[data-testid=mockup-inspector] [data-testid=inspector-property]").Select(e => e.GetAttribute("data-property")!)];

    // ---- what is offered -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_node_is_offered_exactly_the_content_properties_its_type_carries_and_nothing_else()
    {
        var (ctx, cut, _) = await OpenAsync();
        using var _ctx = ctx;

        var expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["window"] = ["id", "text", "layout"],
            ["toolbar"] = ["id", "text", "layout"],
            ["panel"] = ["id", "text", "layout"],
            ["form"] = ["id", "text", "layout"],
            ["container"] = ["id", "text", "layout"],
            ["dialog"] = ["id", "text", "layout"],
            ["tabs"] = ["id", "text"],
            ["field"] = ["id", "text", "input", "value", "placeholder", "required"],
            ["button"] = ["id", "text"],
            ["text"] = ["id", "text"],
            ["image"] = ["id", "text"],
            ["list"] = ["id", "text", "data", "items"],
            ["tree"] = ["id", "text", "data", "items"],
            ["table"] = ["id", "text", "data", "columns", "items"],
        };

        var rows = cut.FindAll("[data-testid=mockup-outline-row]").Select(r => (Id: r.QuerySelector(".mk-outline-id")!.TextContent, Type: r.QuerySelector(".mk-outline-type")!.TextContent)).ToList();
        Assert.Equal(14, rows.Select(r => r.Type).Distinct().Count());
        foreach (var (id, type) in rows)
        {
            await Select(cut, id);
            var offered = Offered(cut);
            var component = Enum.Parse<MockupComponent>(type, ignoreCase: true);
            Assert.All(offered, p => Assert.Contains(p, MockupVocabulary.PropertiesOf(component)));
            if (id == "role")
            {
                Assert.Equal(expected[type].Append("options").Order(StringComparer.Ordinal), offered.Order(StringComparer.Ordinal));
                continue;
            }

            Assert.Equal(expected[type].Order(StringComparer.Ordinal), offered.Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task Grid_columns_options_and_show_appear_only_once_they_mean_something()
    {
        var (ctx, cut, _) = await OpenAsync();
        using var _ctx = ctx;

        await Select(cut, "home");
        Assert.DoesNotContain("cols", Offered(cut));
        await Edit(cut, "inspector-layout", "grid");
        Assert.Contains("cols", Offered(cut));

        await Select(cut, "name");
        Assert.DoesNotContain("options", Offered(cut));
        await Edit(cut, "inspector-input", "radio");
        Assert.Contains("options", Offered(cut));

        await Select(cut, "names");
        Assert.DoesNotContain("show", Offered(cut));
        await Edit(cut, "inspector-source", "people");
        Assert.Contains("show", Offered(cut));
        Assert.DoesNotContain("items", Offered(cut));
    }

    // ---- every property, edited and drawn -----------------------------------------------------------------------

    [Fact]
    public async Task Id_text_layout_and_cols_of_containers_show_in_the_preview_and_one_undo_takes_each_back()
    {
        var (ctx, cut, _) = await OpenAsync();
        using var _ctx = ctx;

        await Select(cut, "home");
        await Edit(cut, "inspector-text", "Directory");
        Assert.Equal("Directory", Drawn(cut, "home").QuerySelector(".mk-titlebar")!.TextContent);
        await Undo(cut);
        Assert.Equal("Home", Drawn(cut, "home").QuerySelector(".mk-titlebar")!.TextContent);

        await Edit(cut, "inspector-layout", "grid");
        await Edit(cut, "inspector-cols", "3");
        Assert.Contains("repeat(3,", Drawn(cut, "home").QuerySelector(".mk-body")!.GetAttribute("style"));
        await Edit(cut, "inspector-layout", "row");
        Assert.Contains("mk-layout-row", Drawn(cut, "home").QuerySelector(".mk-body")!.ClassName);
        Assert.DoesNotContain("cols", Offered(cut));

        foreach (var id in new[] { "bar", "side", "edit", "box", "confirm" })
        {
            await Select(cut, id);
            await Edit(cut, "inspector-layout", "row");
            Assert.Contains("mk-layout-row", Drawn(cut, id).QuerySelector(".mk-body")!.ClassName);
        }

        await Select(cut, "side");
        await Edit(cut, "inspector-text", "Aside");
        Assert.Equal("Aside", Drawn(cut, "side").QuerySelector(".mk-caption")!.TextContent);

        await Edit(cut, "inspector-id", "aside");
        Assert.NotNull(Drawn(cut, "aside"));
        Assert.Equal("aside", cut.Find("[data-testid=mockup-outline-row][aria-selected=true] .mk-outline-id").TextContent);

        await Select(cut, "confirm");
        await Edit(cut, "inspector-text", "Really?");
        Assert.Equal("Really?", Drawn(cut, "confirm").QuerySelector(".mk-titlebar")!.TextContent);

        await Select(cut, "one");
        await Edit(cut, "inspector-text", "First");
        Assert.Contains(cut.FindAll("[data-testid=mockup-preview] .mk-tab"), t => t.TextContent.Trim() == "First");

        await Select(cut, "pages");
        await Edit(cut, "inspector-id", "sections");
        Assert.NotNull(Drawn(cut, "sections"));
    }

    [Fact]
    public async Task Words_of_a_button_a_text_and_an_image_show_in_the_preview()
    {
        var (ctx, cut, _) = await OpenAsync();
        using var _ctx = ctx;

        await Select(cut, "go");
        await Edit(cut, "inspector-text", "Submit");
        Assert.Equal("Submit", Drawn(cut, "go").TextContent.Trim());

        await Select(cut, "words");
        await Edit(cut, "inspector-text", "Welcome");
        Assert.Equal("Welcome", Drawn(cut, "words").TextContent.Trim());

        await Select(cut, "logo");
        await Edit(cut, "inspector-text", "Company logo");
        Assert.Equal("Company logo", Drawn(cut, "logo").QuerySelector(".mk-image-caption")!.TextContent);
    }

    [Fact]
    public async Task A_fields_label_input_options_value_placeholder_and_required_show_in_the_preview()
    {
        var (ctx, cut, _) = await OpenAsync();
        using var _ctx = ctx;

        await Select(cut, "name");
        await Edit(cut, "inspector-text", "Full name");
        await Edit(cut, "inspector-placeholder", "Ada Lovelace");
        await Edit(cut, "inspector-value", "Grace");
        await Edit(cut, "inspector-required", true);
        var field = Drawn(cut, "name");
        Assert.Equal("Full name *", field.QuerySelector(".mk-label-text")!.TextContent);
        Assert.Equal("Ada Lovelace", field.QuerySelector("input")!.GetAttribute("placeholder"));
        Assert.Equal("Grace", field.QuerySelector("input")!.GetAttribute("value"));

        await Edit(cut, "inspector-input", "email");
        Assert.Equal("email", Drawn(cut, "name").QuerySelector("input")!.GetAttribute("type"));

        await Select(cut, "role");
        await Edit(cut, "inspector-value", "Member");
        Assert.Equal("Member", Drawn(cut, "role").QuerySelector("option[selected]")!.TextContent);

        // Rename the chosen option: the value follows it, in one edit.
        await Edit(cut, "inspector-option-text", "Members", index: 1);
        Assert.Equal(["Admin", "Members"], Drawn(cut, "role").QuerySelectorAll("option").Select(o => o.TextContent).ToArray());
        Assert.Equal("Members", Drawn(cut, "role").QuerySelector("option[selected]")!.TextContent);
        await Undo(cut);
        Assert.Equal("Member", Drawn(cut, "role").QuerySelector("option[selected]")!.TextContent);

        await Press(cut, "inspector-option-add");
        await Press(cut, "inspector-option-up", index: 2);
        Assert.Equal(["Admin", "Option 3", "Member"], Drawn(cut, "role").QuerySelectorAll("option").Select(o => o.TextContent).ToArray());
        await Press(cut, "inspector-option-remove", index: 0);
        Assert.Equal(["Option 3", "Member"], Drawn(cut, "role").QuerySelectorAll("option").Select(o => o.TextContent).ToArray());

        await Edit(cut, "inspector-input", "radio");
        Assert.Equal(2, Drawn(cut, "role").QuerySelectorAll("input[type=radio]").Length);

        await Select(cut, "ok");
        await Edit(cut, "inspector-value", "true");
        Assert.True(Drawn(cut, "ok").QuerySelector("input[type=checkbox]")!.HasAttribute("checked"));
    }

    [Fact]
    public async Task A_lists_and_a_trees_items_and_their_data_set_show_in_the_preview()
    {
        var (ctx, cut, _) = await OpenAsync();
        using var _ctx = ctx;

        await Select(cut, "names");
        await Edit(cut, "inspector-items", "Grace\nKatherine\n\nDorothy");
        Assert.Equal(["Grace", "Katherine", "Dorothy"], Drawn(cut, "names").QuerySelectorAll(".mk-row-text").Select(e => e.TextContent).ToArray());
        await Edit(cut, "inspector-text", "People");
        Assert.Equal("People", Drawn(cut, "names").QuerySelector(".mk-caption")!.TextContent);

        await Edit(cut, "inspector-source", "people");
        Assert.Equal(["Ada", "Lin"], Drawn(cut, "names").QuerySelectorAll(".mk-row-text").Select(e => e.TextContent).ToArray());
        await Edit(cut, "inspector-show", "role");
        Assert.Equal(["Admin", "Member"], Drawn(cut, "names").QuerySelectorAll(".mk-row-text").Select(e => e.TextContent).ToArray());
        await Edit(cut, "inspector-source", "");
        Assert.Contains("items", Offered(cut));

        await Select(cut, "folders");
        await Edit(cut, "inspector-items", "Docs\n  Plans\n  Specs\nCode");
        var tree = Drawn(cut, "folders");
        Assert.Equal(["Plans", "Specs"], tree.QuerySelectorAll("li li .mk-row-text").Select(e => e.TextContent).ToArray());
        Assert.Equal("Code", tree.QuerySelectorAll("ul[role=tree] > li > .mk-row-text")[1].TextContent);
    }

    [Fact]
    public async Task A_tables_columns_rows_and_data_set_show_in_the_preview()
    {
        var (ctx, cut, _) = await OpenAsync();
        using var _ctx = ctx;

        await Select(cut, "people");
        await Edit(cut, "inspector-column-heading", "Who");
        Assert.Equal(["Who"], Drawn(cut, "people").QuerySelectorAll("th").Select(e => e.TextContent).ToArray());

        await Press(cut, "inspector-column-add");
        await Edit(cut, "inspector-column-heading", "Team", index: 1);
        await Edit(cut, "inspector-cell", "Analytical", index: 1);
        Assert.Equal(["Who", "Team"], Drawn(cut, "people").QuerySelectorAll("th").Select(e => e.TextContent).ToArray());
        Assert.Equal(["Ada", "Analytical"], Drawn(cut, "people").QuerySelectorAll("tbody td").Select(e => e.TextContent).ToArray());

        await Edit(cut, "inspector-column-key", "team", index: 1);
        Assert.Equal(["Ada", "Analytical"], Drawn(cut, "people").QuerySelectorAll("tbody td").Select(e => e.TextContent).ToArray());

        await Press(cut, "inspector-column-down", index: 0);
        Assert.Equal(["Team", "Who"], Drawn(cut, "people").QuerySelectorAll("th").Select(e => e.TextContent).ToArray());

        await Press(cut, "inspector-row-add");
        await Edit(cut, "inspector-cell", "Grace", index: 3);
        Assert.Equal(2, Drawn(cut, "people").QuerySelectorAll("tbody tr").Length);
        await Press(cut, "inspector-row-remove", index: 0);
        Assert.Equal(["", "Grace"], Drawn(cut, "people").QuerySelectorAll("tbody td").Select(e => e.TextContent).ToArray());

        await Press(cut, "inspector-column-remove", index: 0);
        await Edit(cut, "inspector-source", "people");
        Assert.Equal(["Ada", "Lin"], Drawn(cut, "people").QuerySelectorAll("tbody td").Select(e => e.TextContent).ToArray());
    }

    [Fact]
    public async Task A_table_bound_to_data_adds_the_sets_own_next_key_and_says_when_every_key_is_shown()
    {
        // The session canonicalises a valid tree (MockupWriter sorts a record's keys, @14), so the key order here
        // is name, office, role, not the order they were declared in BoundTableTree.
        var (ctx, cut, _) = await OpenAsync(BoundTableTree);
        using var _ctx = ctx;

        await Select(cut, "team");
        Assert.Equal(["name"], Drawn(cut, "team").QuerySelectorAll("th").Select(e => e.TextContent).ToArray());
        Assert.False(Control(cut, "inspector-column-add").HasAttribute("disabled"));

        await Press(cut, "inspector-column-add");
        Assert.Equal(["name", "office"], Drawn(cut, "team").QuerySelectorAll("th").Select(e => e.TextContent).ToArray());
        Assert.False(Control(cut, "inspector-column-add").HasAttribute("disabled"));

        await Press(cut, "inspector-column-add");
        Assert.Equal(["name", "office", "role"], Drawn(cut, "team").QuerySelectorAll("th").Select(e => e.TextContent).ToArray());
        Assert.True(Control(cut, "inspector-column-add").HasAttribute("disabled"));
        Assert.NotEmpty(cut.FindAll("[data-testid=inspector-column-add-help]"));

        await Undo(cut);
        Assert.Equal(["name", "office"], Drawn(cut, "team").QuerySelectorAll("th").Select(e => e.TextContent).ToArray());
        Assert.False(Control(cut, "inspector-column-add").HasAttribute("disabled"));
    }

    [Fact]
    public async Task The_designs_title_is_edited_from_the_inspector()
    {
        var (ctx, cut, item) = await OpenAsync();
        using var _ctx = ctx;

        await Edit(cut, "inspector-title", "Team directory");
        await cut.InvokeAsync(() => cut.Find("[data-testid=mockup-save]").Click());

        var stored = DesignMockup.Read((await _wiki.GetByNumberAsync(item.Number))!.Body);
        Assert.Equal("Team directory", stored.Tree!.Document!.Title);
    }

    // ---- an incomplete edit, and a refused one ------------------------------------------------------------------

    [Fact]
    public async Task Clearing_a_fields_label_shows_the_readers_sentence_on_the_node_keeps_the_edit_and_saves()
    {
        var (ctx, cut, item) = await OpenAsync();
        using var _ctx = ctx;

        await Select(cut, "name");
        await Edit(cut, "inspector-text", "");

        Assert.Contains("A field needs \"text\"", cut.Find("[data-testid=mockup-inspector-errors]").TextContent);
        var row = cut.FindAll("[data-testid=mockup-outline-row]").Single(r => r.QuerySelector(".mk-outline-id")?.TextContent == "name");
        Assert.NotNull(row.QuerySelector("[data-testid=mockup-outline-error]"));
        Assert.Equal(string.Empty, Control(cut, "inspector-text").GetAttribute("value") ?? string.Empty);
        Assert.Empty(cut.FindAll("[data-testid=mockup-refusal]"));

        await cut.InvokeAsync(() => cut.Find("[data-testid=mockup-save]").Click());

        var stored = (await _wiki.GetByNumberAsync(item.Number))!;
        Assert.Equal(item.Version + 1, stored.Version);
        var read = DesignMockup.Read(stored.Body);
        Assert.Contains(read.Errors, e => e.Message.StartsWith("A field needs \"text\"", StringComparison.Ordinal));
        Assert.Contains("\"id\": \"name\"", read.Block!.Json);
    }

    [Fact]
    public async Task A_refused_change_says_why_in_the_readers_words_and_the_control_shows_the_tree_again()
    {
        var (ctx, cut, _) = await OpenAsync();
        using var _ctx = ctx;

        await Select(cut, "home");
        await Edit(cut, "inspector-layout", "grid");
        await Edit(cut, "inspector-cols", "2");
        await Edit(cut, "inspector-cols", "13");

        Assert.Contains("1 to 12", cut.Find("[data-testid=mockup-refusal]").TextContent);
        Assert.Equal("2", Control(cut, "inspector-cols").GetAttribute("value"));

        await Edit(cut, "inspector-id", "");
        Assert.Contains("A screen needs an \"id\"", cut.Find("[data-testid=mockup-refusal]").TextContent);
        Assert.Equal("home", Control(cut, "inspector-id").GetAttribute("value"));
    }
}
