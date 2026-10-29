using System.Diagnostics;
using System.Globalization;
using System.Text;
using AngleSharp.Dom;
using Bunit;
using Fravashi.Application.Mockups;
using Fravashi.Web.Components.Mockups;
using Xunit;
using Xunit.Abstractions;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 194: the mockup preview, drawn from the agent-authored trees #50 stored. What a viewport or a state
/// changes is asserted on the drawing, because the drawing is what a person judges a design by.
/// </summary>
public sealed class MockupPreviewTests(ITestOutputHelper output)
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Mockups", "Fixtures", name));

    private static MockupReadResult Read(string json) => MockupReader.Read(json);

    private static IRenderedComponent<MockupPreview> Render(BunitContext ctx, MockupReadResult read) =>
        ctx.Render<MockupPreview>(p => p
            .Add(c => c.Document, read.Document)
            .Add(c => c.Errors, read.Errors));

    private static IElement Node(IRenderedComponent<MockupPreview> cut, string id) =>
        cut.Find($"[data-mockup-id='{id}']");

    [Fact]
    public async Task The_agent_written_directory_is_drawn_screen_by_screen_with_its_sample_data_and_dialog()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx, Read(Fixture("team-directory.attempt-3.json")));

        Assert.Empty(cut.FindAll("[data-testid=mockup-preview-errors]"));
        var window = Node(cut, "directory");
        Assert.Equal("region", window.GetAttribute("role"));
        Assert.Equal("Team directory", window.GetAttribute("aria-label"));
        Assert.Contains("Team directory", window.QuerySelector(".mk-titlebar")!.TextContent);
        Assert.Equal("toolbar", cut.Find("[data-mockup-type=toolbar]").GetAttribute("role"));

        // The table is filled from the "people" data set, one row per record, in the declared columns.
        var table = Node(cut, "people-table");
        Assert.Equal(["Name", "Role", "Office"], table.QuerySelectorAll("th").Select(h => h.TextContent));
        var rows = table.QuerySelectorAll("tbody tr");
        Assert.Equal(4, rows.Length);
        Assert.Equal(["Ada Lovelace", "Engineer", "London"], rows[0].QuerySelectorAll("td").Select(d => d.TextContent));

        // A node with "on" is marked, and the mark says what it would do; nothing is run.
        var add = Node(cut, "add-person").ParentElement!;
        Assert.Contains("mk-acts", add.ClassList);
        Assert.Equal("click: open add-dialog", add.GetAttribute("title"));
        Assert.Equal("true", add.QuerySelector(".mk-acts-badge")!.GetAttribute("aria-hidden"));
        Assert.Equal("select: go to profile", table.ParentElement!.GetAttribute("title"));

        // The dialog is not drawn until somebody chooses to see it.
        Assert.Empty(cut.FindAll("[data-mockup-id=add-dialog]"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=mockup-preview-dialog]").Change(true));
        var dialog = Node(cut, "add-dialog");
        Assert.Equal("dialog", dialog.GetAttribute("role"));

        // A layer of the frame beside the screen, never inside it: drawn in its place, the screen's height cut it
        // off under the Office field (seen in the first evidence screenshot, 2026-10-06).
        Assert.Equal("mockup-preview-frame", dialog.ParentElement!.ParentElement!.GetAttribute("data-testid"));
        Assert.Null(window.QuerySelector("[role=dialog]"));
        Assert.Contains("Cancel", dialog.TextContent);
        Assert.Equal("Add a person", dialog.GetAttribute("aria-label"));
        Assert.Equal("form", Node(cut, "add-form").GetAttribute("role"));
        Assert.Equal(
            ["Engineer", "Researcher", "Manager"],
            Node(cut, "role-field").QuerySelectorAll("option").Select(o => o.TextContent).Where(t => t != "Choose..."));
        Assert.True(Node(cut, "welcome-email").QuerySelector("input[type=checkbox]")!.HasAttribute("checked"));
        Assert.Contains("Name *", Node(cut, "name-field").TextContent);

        // The error message starts hidden, so it is a marked strip, not its words.
        var hidden = cut.Find("[data-state=hidden]");
        Assert.Equal("true", hidden.GetAttribute("aria-hidden"));
        Assert.DoesNotContain("Could not save", hidden.TextContent);

        // The second screen.
        await cut.InvokeAsync(() => cut.Find("[data-testid=mockup-preview-screen]").Change("1"));
        Assert.Empty(cut.FindAll("[data-mockup-id=directory]"));
        Assert.Equal("1", Node(cut, "person-name").GetAttribute("aria-level"));
        Assert.Equal(["Details", "Activity"], cut.FindAll("[data-testid=mockup-preview-tab]").Select(t => t.TextContent.Trim()));
        Assert.Contains("Role: Engineer", cut.Find("[role=tabpanel]").TextContent);
        await cut.InvokeAsync(() => cut.FindAll("[data-testid=mockup-preview-tab]")[1].Click());
        Assert.Equal(
            ["Joined the team", "Updated office"],
            Node(cut, "activity-list").QuerySelectorAll("li").Select(l => l.TextContent.Trim()));
    }

    [Fact]
    public void Every_one_of_the_fifteen_components_is_drawn()
    {
        const string tree = """
            {"mockup": 1, "data": {"files": [{"name": "src", "children": [{"name": "app.cs"}]}]}, "screens": [
              {"type": "window", "id": "w", "text": "Window", "children": [
                {"type": "panel", "text": "Panel", "children": [
                  {"type": "toolbar", "children": [{"type": "button", "text": "Go"}, {"type": "link", "text": "Home", "href": "/"}]},
                  {"type": "form", "children": [{"type": "field", "text": "Name"}]},
                  {"type": "container", "children": [{"type": "text", "text": "Words"}]},
                  {"type": "list", "items": ["one", "two"]},
                  {"type": "tree", "data": "files", "show": "name"},
                  {"type": "table", "columns": ["a"], "items": [{"a": "cell"}]},
                  {"type": "tabs", "children": [{"type": "panel", "text": "Tab"}]},
                  {"type": "image", "text": "a chart", "a11y": {"decorative": true}},
                  {"type": "dialog", "id": "d", "text": "Dialog"}
                ]}
              ]}
            ]}
            """;
        var read = Read(tree);
        Assert.True(read.IsValid, string.Join("\n", read.Errors));

        using var ctx = new BunitContext();
        var cut = Render(ctx, read);
        cut.InvokeAsync(() => cut.Find("[data-testid=mockup-preview-dialog]").Change(true));

        var drawn = cut.FindAll("[data-mockup-type]").Select(e => e.GetAttribute("data-mockup-type")!).ToHashSet();
        Assert.Equal(MockupVocabulary.Names<MockupComponent>().ToHashSet(), drawn);

        // Backlog 290: a link is drawn with the link role and is never an anchor, so nothing in the studio follows it.
        var link = cut.Find("[data-mockup-type=link]");
        Assert.Equal("link", link.GetAttribute("role"));
        Assert.Equal("SPAN", link.TagName);
        Assert.Equal("Home", link.TextContent.Trim());
        Assert.Empty(cut.FindAll("a[href]"));

        // A tree bound to data nests the records under each record.
        var tree0 = cut.Find("[data-mockup-type=tree] [role=tree]");
        Assert.Equal("src", tree0.QuerySelector("[role=treeitem] > .mk-row-text")!.TextContent);
        Assert.Equal("app.cs", tree0.QuerySelector("ul[role=group] > [role=treeitem]")!.TextContent.Trim());
    }

    [Fact]
    public async Task Switching_the_viewport_applies_the_fixtures_narrow_override_and_wide_has_none()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx, Read(Fixture("team-directory.attempt-3.json")));

        string ToolbarLayout() => cut.Find("[data-mockup-type=toolbar] > .mk-body").ClassName!;

        Assert.Contains("mk-layout-row", ToolbarLayout());
        Assert.Contains("width: 1280px", cut.Find("[data-testid=mockup-preview-frame]").GetAttribute("style"));

        await cut.InvokeAsync(() => cut.Find("[data-testid=mockup-preview-viewport]").Change("narrow"));
        Assert.Contains("mk-layout-column", ToolbarLayout());
        Assert.Contains("width: 390px", cut.Find("[data-testid=mockup-preview-frame]").GetAttribute("style"));

        await cut.InvokeAsync(() => cut.Find("[data-testid=mockup-preview-viewport]").Change("wide"));
        Assert.Contains("mk-layout-row", ToolbarLayout());
    }

    [Fact]
    public async Task A_viewport_applies_every_override_it_names_and_nothing_written_for_another()
    {
        const string tree = """
            {"mockup": 1, "viewports": [{"name": "wide", "width": 1200}, {"name": "narrow", "width": 400}, {"name": "tall", "width": 800}],
             "screens": [{"type": "window", "id": "w", "text": "Base title",
               "at": {"wide": {"text": "Wide title"}, "narrow": {"text": "Narrow title", "layout": "row"}},
               "children": [
                 {"type": "text", "id": "a", "text": "a base", "at": {"wide": {"text": "a wide"}, "narrow": {"text": "a narrow"}}},
                 {"type": "text", "id": "b", "text": "b base", "at": {"narrow": {"text": "b narrow"}}},
                 {"type": "button", "id": "c", "text": "c base", "at": {"wide": {"text": "c wide"}}},
                 {"type": "panel", "id": "p", "layout": "column", "at": {"narrow": {"layout": "grid", "cols": 2}}}
               ]}]}
            """;
        var read = Read(tree);
        Assert.True(read.IsValid, string.Join("\n", read.Errors));

        using var ctx = new BunitContext();
        var cut = Render(ctx, read);

        string[] Words() =>
        [
            Node(cut, "w").QuerySelector(".mk-titlebar")!.TextContent,
            Node(cut, "a").TextContent, Node(cut, "b").TextContent, Node(cut, "c").TextContent.Trim(),
        ];

        Assert.Equal(["Wide title", "a wide", "b base", "c wide"], Words());
        Assert.Contains("mk-layout-column", Node(cut, "p").QuerySelector(".mk-body")!.ClassName);

        await cut.InvokeAsync(() => cut.Find("[data-testid=mockup-preview-viewport]").Change("narrow"));
        Assert.Equal(["Narrow title", "a narrow", "b narrow", "c base"], Words());
        Assert.Contains("mk-layout-row", Node(cut, "w").QuerySelector(".mk-body")!.ClassName);
        var grid = Node(cut, "p").QuerySelector(".mk-body")!;
        Assert.Contains("mk-layout-grid", grid.ClassName);
        Assert.Contains("repeat(2,", grid.GetAttribute("style"));

        // A viewport no node names changes nothing.
        await cut.InvokeAsync(() => cut.Find("[data-testid=mockup-preview-viewport]").Change("tall"));
        Assert.Equal(["Base title", "a base", "b base", "c base"], Words());
    }

    [Fact]
    public async Task Switching_a_nodes_state_applies_its_override()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx, Read(Fixture("team-directory.attempt-3.json")));

        IElement Chooser(string path) => cut.Find($"[data-testid=mockup-preview-state][data-node='{path}']");

        const string tablePath = "$.screens[0].children[1]";
        Assert.Equal(4, Node(cut, "people-table").QuerySelectorAll("tbody tr").Length);

        await cut.InvokeAsync(() => Chooser(tablePath).Change("empty"));
        var table = Node(cut, "people-table");
        Assert.Equal("empty", table.GetAttribute("data-state"));
        Assert.Equal("Nobody matches that search.", Assert.Single(table.QuerySelectorAll("tbody tr")).TextContent);

        await cut.InvokeAsync(() => Chooser(tablePath).Change("loading"));
        Assert.Equal("true", Node(cut, "people-table").GetAttribute("aria-busy"));
        Assert.Equal(4, Node(cut, "people-table").QuerySelectorAll("tbody tr").Length);

        await cut.InvokeAsync(() => Chooser(tablePath).Change(string.Empty));
        Assert.False(Node(cut, "people-table").HasAttribute("data-state"));

        // Inside the dialog: the save button's loading text, and the error message shown in its error state.
        await cut.InvokeAsync(() => cut.Find("[data-testid=mockup-preview-dialog]").Change(true));
        const string savePath = "$.screens[0].children[2].children[0].children[5].children[0]";
        Assert.Equal("Save", Node(cut, "save-btn").TextContent.Trim());
        await cut.InvokeAsync(() => Chooser(savePath).Change("loading"));
        Assert.Equal("Saving...", Node(cut, "save-btn").TextContent.Trim());

        const string errorPath = "$.screens[0].children[2].children[0].children[4]";
        Assert.Empty(cut.FindAll("[data-mockup-id=error-message]"));
        await cut.InvokeAsync(() => Chooser(errorPath).Change("error"));
        var message = Node(cut, "error-message");
        Assert.Equal("Could not save this person.", message.TextContent);
        Assert.Equal("assertive", message.GetAttribute("aria-live"));
    }

    [Fact]
    public void A_label_is_the_accessible_name_and_decorative_hides_a_node_from_assistive_technology()
    {
        const string tree = """
            {"mockup": 1, "screens": [{"type": "window", "id": "w", "text": "W", "children": [
              {"type": "field", "id": "f", "input": "search", "placeholder": "Search", "a11y": {"label": "Search people"}},
              {"type": "button", "id": "b", "a11y": {"label": "Refresh"}},
              {"type": "image", "id": "i", "a11y": {"label": "Portrait"}},
              {"type": "image", "id": "j", "a11y": {"decorative": true}},
              {"type": "panel", "id": "p", "text": "Shown", "a11y": {"label": "Named panel"}}
            ]}]}
            """;
        var read = Read(tree);
        Assert.True(read.IsValid, string.Join("\n", read.Errors));

        using var ctx = new BunitContext();
        var cut = Render(ctx, read);

        // On a field the name belongs on the input itself, where assistive technology looks for it.
        Assert.Equal("Search people", Node(cut, "f").QuerySelector("input")!.GetAttribute("aria-label"));
        Assert.False(Node(cut, "f").HasAttribute("aria-label"));
        Assert.Equal("Refresh", Node(cut, "b").GetAttribute("aria-label"));
        Assert.Equal("img", Node(cut, "i").GetAttribute("role"));
        Assert.Equal("Portrait", Node(cut, "i").GetAttribute("aria-label"));
        Assert.Equal("true", Node(cut, "j").GetAttribute("aria-hidden"));
        Assert.Equal("presentation", Node(cut, "j").GetAttribute("role"));
        Assert.Equal("Named panel", Node(cut, "p").GetAttribute("aria-label"));
    }

    [Theory]
    [InlineData("team-directory.attempt-1.json")]
    [InlineData("team-directory.attempt-2.json")]
    public void A_tree_the_reader_refuses_shows_every_error_and_draws_nothing(string fixture)
    {
        var read = Read(Fixture(fixture));
        Assert.False(read.IsValid);

        using var ctx = new BunitContext();
        var cut = Render(ctx, read);

        Assert.Empty(cut.FindAll("[data-testid=mockup-preview-frame]"));
        Assert.Empty(cut.FindAll("[data-mockup-type]"));
        Assert.Contains("nothing is drawn", cut.Find("[data-testid=mockup-preview-errors]").TextContent);
        var shown = cut.FindAll("[data-testid=mockup-error]");
        Assert.Equal(read.Errors.Count, shown.Count);
        for (var i = 0; i < shown.Count; i++)
        {
            Assert.Equal(read.Errors[i].Path, shown[i].QuerySelector("code")!.TextContent);
            Assert.Contains(read.Errors[i].Message, shown[i].TextContent);
        }
    }

    [Fact]
    public async Task Five_thousand_nodes_render_and_switch_viewport_in_measured_time()
    {
        var read = Read(Largest());
        Assert.True(read.IsValid, string.Join("\n", read.Errors.Take(5)));

        using var ctx = new BunitContext();
        var clock = Stopwatch.StartNew();
        var cut = Render(ctx, read);
        var first = clock.Elapsed;
        var drawn = cut.FindAll("[data-mockup-type]").Count;

        clock.Restart();
        await cut.InvokeAsync(() => cut.Find("[data-testid=mockup-preview-viewport]").Change("narrow"));
        var narrow = clock.Elapsed;
        Assert.Equal(50, cut.FindAll("[data-mockup-type=container] > .mk-layout-row").Count);

        clock.Restart();
        await cut.InvokeAsync(() => cut.Find("[data-testid=mockup-preview-viewport]").Change("wide"));
        var wide = clock.Elapsed;

        output.WriteLine(FormattableString.Invariant(
            $"5000 nodes ({drawn} drawn): first render {first.TotalMilliseconds:F0} ms, to narrow {narrow.TotalMilliseconds:F0} ms, back to wide {wide.TotalMilliseconds:F0} ms, markup {cut.Markup.Length} chars"));

        // Generous on purpose: a shared CI runner is slow, and the bound is for "interactive", not a benchmark.
        Assert.True(first < TimeSpan.FromSeconds(10), $"first render took {first}");
        Assert.True(narrow < TimeSpan.FromSeconds(5), $"switching viewport took {narrow}");
    }

    /// <summary>
    /// A tree at the schema's limit of 5000 nodes, overrides included: a window, 50 containers each with a narrow
    /// override and 97 texts, and 49 more texts.
    /// </summary>
    private static string Largest()
    {
        var json = new StringBuilder();
        json.Append("""{"mockup": 1, "viewports": [{"name": "wide", "width": 1280}, {"name": "narrow", "width": 390}], "screens": [{"type": "window", "id": "w", "text": "Big", "children": [""");
        var first = true;
        void Comma()
        {
            if (!first)
            {
                json.Append(',');
            }

            first = false;
        }

        for (var c = 0; c < 50; c++)
        {
            Comma();
            json.Append("""{"type": "container", "layout": "column", "at": {"narrow": {"layout": "row"}}, "children": [""");
            for (var t = 0; t < 97; t++)
            {
                json.Append(t == 0 ? string.Empty : ",");
                json.Append(CultureInfo.InvariantCulture, $$"""{"type": "text", "text": "Line {{c}}.{{t}}"}""");
            }

            json.Append("]}");
        }

        for (var t = 0; t < 49; t++)
        {
            Comma();
            json.Append(CultureInfo.InvariantCulture, $$"""{"type": "text", "text": "Tail {{t}}"}""");
        }

        json.Append("]}]}");
        return json.ToString();
    }
}
