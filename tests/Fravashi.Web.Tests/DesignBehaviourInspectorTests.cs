using System.Text.Json.Nodes;
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
/// Backlog 199: the inspector's states, viewports, interactions, accessibility and sample data, at the surface a person
/// uses. Every one of them in each fixture is visible and editable; a screen with states, a viewport override, a submit
/// that sets a state and goes to another screen, and a labelled image is built without JSON; and the preview shows the
/// override being edited.
/// </summary>
public sealed class DesignBehaviourInspectorTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fravashi-tests", Guid.NewGuid().ToString("N"));

    private readonly SqliteWikiLibrary _wiki;

    public DesignBehaviourInspectorTests()
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

    public static TheoryData<string> Fixtures() =>
        new() { "team-directory.attempt-1.json", "team-directory.attempt-2.json", "team-directory.attempt-3.json", "every-feature.json" };

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Mockups", "Fixtures", name));

    private async Task<WikiItem> DesignAsync(string body)
    {
        var created = await _wiki.CreateAsync(new NewWikiItem { Type = WikiItemType.Design, Title = "Design", Body = body });
        Assert.True(created.IsValid);
        return created.Value;
    }

    private static string Body(string json) => "The design.\n\n```mockup\n" + json + "```\n";

    private BunitContext Context()
    {
        var ctx = new BunitContext();
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

    private static Task SelectPath(IRenderedComponent<DesignEditorPage> cut, string jsonPath) =>
        cut.InvokeAsync(() => cut.Find($"[data-testid=mockup-outline-row][data-path='{jsonPath}']").Click());

    private static Task Select(IRenderedComponent<DesignEditorPage> cut, string id) =>
        cut.InvokeAsync(() => cut.FindAll("[data-testid=mockup-outline-row]")
            .Single(r => r.QuerySelector(".mk-outline-id")?.TextContent == id).Click());

    private static Task SelectType(IRenderedComponent<DesignEditorPage> cut, string type) =>
        cut.InvokeAsync(() => cut.FindAll("[data-testid=mockup-outline-row]")
            .First(r => r.QuerySelector(".mk-outline-type")?.TextContent == type).Click());

    private static Task Insert(IRenderedComponent<DesignEditorPage> cut, string type) =>
        cut.InvokeAsync(() => cut.Find($"[data-testid=mockup-palette-item][data-type={type}]").Click(new MouseEventArgs()));

    private static IElement In(IRenderedComponent<DesignEditorPage> cut, string scope, string testId) =>
        cut.Find($"[data-testid=mockup-inspector] {scope} [data-testid={testId}]");

    private static Task Change(IRenderedComponent<DesignEditorPage> cut, string scope, string testId, object value) =>
        cut.InvokeAsync(() => In(cut, scope, testId).Change(value));

    private static Task Click(IRenderedComponent<DesignEditorPage> cut, string scope, string testId) =>
        cut.InvokeAsync(() => In(cut, scope, testId).Click());

    private static IElement Drawn(IRenderedComponent<DesignEditorPage> cut, string id) =>
        cut.Find($"[data-testid=mockup-preview] [data-mockup-id='{id}']");

    private static string? Chosen(IElement select) => select.QuerySelector("option[selected]")?.GetAttribute("value");

    private static void NoRefusal(IRenderedComponent<DesignEditorPage> cut) =>
        Assert.Empty(cut.FindAll("[data-testid=mockup-refusal]"));

    private static IEnumerable<(string Path, JsonObject Node)> Nodes(JsonObject tree)
    {
        IEnumerable<(string, JsonObject)> Walk(JsonObject node, string path)
        {
            yield return (path, node);
            var children = node["children"] as JsonArray ?? [];
            for (var i = 0; i < children.Count; i++)
            {
                if (children[i] is JsonObject child)
                {
                    foreach (var each in Walk(child, $"{path}.children[{i}]"))
                    {
                        yield return each;
                    }
                }
            }
        }

        var screens = tree["screens"] as JsonArray ?? [];
        return Enumerable.Range(0, screens.Count).Where(i => screens[i] is JsonObject).SelectMany(i => Walk((JsonObject)screens[i]!, $"$.screens[{i}]"));
    }

    // ---- every entry of every fixture is visible ----------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Every_state_override_interaction_a11y_entry_and_data_set_of_a_fixture_is_visible_in_the_inspector(string fixture)
    {
        var json = Fixture(fixture);
        var item = await DesignAsync(Body(json));
        using var ctx = Context();
        var cut = Open(ctx, item);
        var tree = (JsonObject)JsonNode.Parse(json)!;

        foreach (var set in (tree["data"] as JsonObject ?? []).Select(p => p.Key))
        {
            var shown = cut.Find($"[data-testid=inspector-data] [data-testid=inspector-dataset][data-set='{set}']");
            var records = (tree["data"]![set] as JsonArray ?? []).Count;
            Assert.Equal(records, shown.QuerySelectorAll("[data-testid=inspector-data-row]").Length);
        }

        foreach (var viewport in (tree["viewports"] as JsonArray ?? []).Select(v => (string?)v?["name"]).OfType<string>())
        {
            Assert.Single(cut.FindAll($"[data-testid=inspector-viewports] [data-testid=inspector-viewport][data-viewport='{viewport}']"));
        }

        var checkedEntries = 0;
        foreach (var (path, node) in Nodes(tree))
        {
            var entries = new[] { "states", "at", "on", "a11y" }.Where(m => node[m] is JsonObject { Count: > 0 }).ToList();
            if (entries.Count == 0)
            {
                continue;
            }

            await SelectPath(cut, path);
            foreach (var state in (node["states"] as JsonObject ?? []).Select(p => p.Key))
            {
                var row = cut.Find($"[data-testid=inspector-states] [data-testid=inspector-state][data-state='{state}']");
                Assert.True(row.QuerySelector("[data-testid=inspector-state-described]")?.HasAttribute("checked") ?? row.QuerySelector("[data-testid=inspector-state-drop]") is not null,
                    $"{path}: the state {state} is shown as described");
                checkedEntries++;
            }

            if ((string?)node["state"] is { } initial)
            {
                Assert.Equal(initial, Chosen(In(cut, "[data-testid=inspector-states]", "inspector-initial-state")));
            }

            foreach (var viewport in (node["at"] as JsonObject ?? []).Select(p => p.Key))
            {
                var row = cut.Find($"[data-testid=inspector-node-viewports] [data-testid=inspector-at][data-viewport='{viewport}']");
                Assert.True(row.QuerySelector("[data-testid=inspector-at-override]")!.HasAttribute("checked"), $"{path}: the override at {viewport}");
                Assert.NotNull(row.QuerySelector("[data-testid=inspector-at-edit]"));
                checkedEntries++;
            }

            foreach (var (on, action) in node["on"] as JsonObject ?? [])
            {
                var shown = cut.Find($"[data-testid=inspector-events] [data-testid=inspector-event][data-event='{on}']");
                if (action is JsonObject a)
                {
                    Assert.Equal((string?)a["go"], Chosen(shown.QuerySelector("[data-testid=inspector-action-go]")!) is "" or null ? null : Chosen(shown.QuerySelector("[data-testid=inspector-action-go]")!));
                    Assert.Equal((string?)a["open"], Chosen(shown.QuerySelector("[data-testid=inspector-action-open]")!) is "" or null ? null : Chosen(shown.QuerySelector("[data-testid=inspector-action-open]")!));
                    Assert.Equal((string?)a["close"], Chosen(shown.QuerySelector("[data-testid=inspector-action-close]")!) is "" or null ? null : Chosen(shown.QuerySelector("[data-testid=inspector-action-close]")!));
                    var sets = shown.QuerySelectorAll("[data-testid=inspector-action-set]");
                    Assert.Equal((a["set"] as JsonObject ?? []).Select(p => $"{p.Key}={p.Value}"),
                        sets.Select(s => $"{Chosen(s.QuerySelector("[data-testid=inspector-action-set-node]")!)}={Chosen(s.QuerySelector("[data-testid=inspector-action-set-state]")!)}"));
                }

                checkedEntries++;
            }

            if (node["a11y"] is JsonObject a11y)
            {
                var section = cut.Find("[data-testid=inspector-a11y]");
                Assert.Equal((string?)a11y["label"] ?? "", section.QuerySelector("[data-testid=inspector-a11y-label]")!.GetAttribute("value") ?? "");
                Assert.Equal(a11y["heading"]?.ToJsonString(), Chosen(section.QuerySelector("[data-testid=inspector-a11y-heading]")!) is "" or null ? null : Chosen(section.QuerySelector("[data-testid=inspector-a11y-heading]")!));
                Assert.Equal((string?)a11y["live"], Chosen(section.QuerySelector("[data-testid=inspector-a11y-live]")!) is "" or null ? null : Chosen(section.QuerySelector("[data-testid=inspector-a11y-live]")!));
                Assert.Equal(a11y["decorative"]?.ToJsonString() == "true", section.QuerySelector("[data-testid=inspector-a11y-decorative]")!.HasAttribute("checked"));
                checkedEntries++;
            }
        }

        Assert.True(checkedEntries > 0, "the fixture carries something to check");
        Assert.Contains("no edits", cut.Find("[data-testid=mockup-status]").TextContent);
    }

    // ---- editing each kind in the every-feature fixture ---------------------------------------------------------

    [Fact]
    public async Task Each_kind_is_editable_in_the_every_feature_fixture_and_one_undo_takes_each_back()
    {
        var item = await DesignAsync(Body(Fixture("every-feature.json")));
        using var ctx = Context();
        var cut = Open(ctx, item);

        // A state's override, edited with slice 6's own text control; the preview shows the node in that state.
        await Select(cut, "save");
        await cut.InvokeAsync(() => cut.Find("[data-testid=inspector-state][data-state=loading] [data-testid=inspector-state-edit]").Click());
        Assert.Equal("loading", cut.Find("[data-testid=inspector-scope]").GetAttribute("data-key"));
        Assert.Equal("Saving...", In(cut, "[data-property=text]", "inspector-text").GetAttribute("value"));
        Assert.Equal("loading", Drawn(cut, "save").GetAttribute("data-state"));
        await Change(cut, "[data-property=text]", "inspector-text", "Saving now");
        NoRefusal(cut);
        Assert.Equal("Saving now", Drawn(cut, "save").TextContent.Trim());
        await Click(cut, "", "inspector-scope-done");
        Assert.Equal("disabled", Drawn(cut, "save").GetAttribute("data-state"));
        Assert.Equal("Save", Drawn(cut, "save").TextContent.Trim());

        // Dropping a state; the initial state.
        await Change(cut, "[data-state=focused]", "inspector-state-described", false);
        Assert.False(In(cut, "[data-state=focused]", "inspector-state-described").HasAttribute("checked"));
        await Change(cut, "[data-testid=inspector-states]", "inspector-initial-state", "");
        Assert.Null(Drawn(cut, "save").GetAttribute("data-state"));

        // A viewport override; the preview switches to that viewport.
        await Select(cut, "side");
        await cut.InvokeAsync(() => cut.Find("[data-testid=inspector-at][data-viewport=narrow] [data-testid=inspector-at-edit]").Click());
        Assert.Contains("390px", cut.Find("[data-testid=mockup-preview-frame]").GetAttribute("style"));
        await Change(cut, "[data-property=text]", "inspector-text", "Narrow side");
        Assert.Contains("Narrow side", Drawn(cut, "side").TextContent);
        await Click(cut, "", "inspector-scope-done");

        // An interaction.
        await Select(cut, "next");
        await Change(cut, "[data-event=click]", "inspector-action-open", "about");
        NoRefusal(cut);
        Assert.Contains("open about", Drawn(cut, "next").ParentElement!.GetAttribute("title"));

        // Accessibility, with labelled and decorative excluding each other.
        await Select(cut, "logo");
        await Change(cut, "[data-testid=inspector-a11y]", "inspector-a11y-label", "Company logo");
        Assert.False(In(cut, "[data-testid=inspector-a11y]", "inspector-a11y-decorative").HasAttribute("checked"));
        Assert.Equal("Company logo", Drawn(cut, "logo").GetAttribute("aria-label"));
        await Select(cut, "headline");
        await Change(cut, "[data-testid=inspector-a11y]", "inspector-a11y-heading", "3");
        Assert.Equal("3", Drawn(cut, "headline").GetAttribute("aria-level"));

        // Sample data: a bound table re-renders as its data set changes.
        var cell = cut.FindAll("[data-testid=inspector-dataset][data-set=people] [data-testid=inspector-data-cell][data-key=name]")[0];
        await cut.InvokeAsync(() => cell.Change("Ada Lovelace"));
        NoRefusal(cut);
        Assert.Contains("Ada Lovelace", Drawn(cut, "people-table").TextContent);
        Assert.Single(cut.FindAll("[data-testid=inspector-dataset][data-set=people] [data-testid=inspector-data-nested]"),
            e => e.TextContent.Contains("1 nested record", StringComparison.Ordinal));

        // A document viewport.
        await cut.InvokeAsync(() => cut.Find("[data-testid=inspector-viewport][data-viewport=narrow] [data-testid=inspector-viewport-width]").Change("360"));
        NoRefusal(cut);

        // Every one of those was one edit: undoing them all is the tree that was opened.
        for (var i = 0; i < 12 && !cut.Find("[data-testid=mockup-undo]").HasAttribute("disabled"); i++)
        {
            await cut.InvokeAsync(() => cut.Find("[data-testid=mockup-undo]").Click());
        }

        Assert.True(cut.Find("[data-testid=mockup-undo]").HasAttribute("disabled"));
        Assert.Contains("no edits", cut.Find("[data-testid=mockup-status]").TextContent);
    }

    [Fact]
    public async Task Removing_a_bound_data_set_is_refused_naming_the_binding_and_removing_a_viewport_takes_its_overrides()
    {
        var item = await DesignAsync(Body(Fixture("every-feature.json")));
        using var ctx = Context();
        var cut = Open(ctx, item);

        await cut.InvokeAsync(() => cut.Find("[data-testid=inspector-dataset][data-set=people] [data-testid=inspector-dataset-remove]").Click());
        var refusal = cut.Find("[data-testid=mockup-refusal]").TextContent;
        Assert.Contains("people", refusal);
        Assert.Single(cut.FindAll("[data-testid=inspector-dataset][data-set=people]"));

        await cut.InvokeAsync(() => cut.Find("[data-testid=inspector-viewport][data-viewport=narrow] [data-testid=inspector-viewport-remove]").Click());
        NoRefusal(cut);
        await Select(cut, "home");
        Assert.Empty(cut.FindAll("[data-testid=inspector-at][data-viewport=narrow]"));
    }

    // ---- #207: stable set order on rename, canonical key order --------------------------------------------------

    [Fact]
    public async Task Renaming_a_data_set_keeps_its_row_in_place_and_keys_show_canonically_sorted()
    {
        var item = await DesignAsync(Body(Fixture("every-feature.json")));
        using var ctx = Context();
        var cut = Open(ctx, item);

        string[] SetOrder() => cut.FindAll("[data-testid=inspector-data] [data-testid=inspector-dataset]")
            .Select(e => e.GetAttribute("data-set")!).ToArray();

        // The session opens with the stored (canonical, alphabetical) order: folders before people.
        Assert.Equal(["folders", "people"], SetOrder());

        // Renamed to something that would sort AFTER "people": it stays first, not pushed to the end.
        await Change(cut, "[data-set=folders]", "inspector-dataset-name", "zzz");
        NoRefusal(cut);
        Assert.Equal(["zzz", "people"], SetOrder());

        // A set's own keys show sorted, the same order Save will store them in.
        var keys = cut.FindAll("[data-testid=inspector-dataset][data-set=people] [data-testid=inspector-data-key]")
            .Select(e => e.GetAttribute("value")!).ToArray();
        Assert.Equal(["admin", "age", "manager", "name", "reports"], keys);
    }

    // ---- a screen built without JSON --------------------------------------------------------------------------

    [Fact]
    public async Task A_new_screen_gets_states_a_viewport_override_a_submit_that_sets_and_goes_and_a_labelled_image_without_json()
    {
        var item = await DesignAsync(DesignMockup.NewBody());
        using var ctx = Context();
        var cut = Open(ctx, item);

        // Two viewports at document level: wide, then narrow.
        await cut.InvokeAsync(() => cut.Find("[data-testid=inspector-viewport-add]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=inspector-viewport-add]").Click());

        // A form holding a button, which gets an id, words, and a disabled and a loading state.
        await Select(cut, "screen");
        await Insert(cut, "form");
        await Insert(cut, "button");
        await Change(cut, "[data-property=id]", "inspector-id", "submit");
        await Change(cut, "[data-property=text]", "inspector-text", "Send");
        await Change(cut, "[data-state=disabled]", "inspector-state-described", true);
        await Change(cut, "[data-state=loading]", "inspector-state-described", true);
        await cut.InvokeAsync(() => cut.Find("[data-testid=inspector-state][data-state=loading] [data-testid=inspector-state-edit]").Click());
        await Change(cut, "[data-property=text]", "inspector-text", "Sending...");
        Assert.Equal("Sending...", Drawn(cut, "submit").TextContent.Trim());
        await Click(cut, "", "inspector-scope-done");

        // The form lays out in a row at narrow.
        await SelectType(cut, "form");
        await Change(cut, "[data-viewport=narrow]", "inspector-at-override", true);
        await cut.InvokeAsync(() => cut.Find("[data-testid=inspector-at][data-viewport=narrow] [data-testid=inspector-at-edit]").Click());
        await Change(cut, "[data-property=layout]", "inspector-layout", "row");
        Assert.Contains("390px", cut.Find("[data-testid=mockup-preview-frame]").GetAttribute("style"));
        Assert.NotNull(cut.Find("[data-testid=mockup-preview] [data-mockup-type=form] .mk-layout-row"));
        await Click(cut, "", "inspector-scope-done");

        // A second screen.
        await Select(cut, "screen");
        await Insert(cut, "window");

        // The form's submit shows the button loading and goes to the second screen.
        await SelectType(cut, "form");
        await Change(cut, "[data-event=submit]", "inspector-action-go", "screen-2");
        await Click(cut, "[data-event=submit]", "inspector-action-set-add");
        await Change(cut, "[data-event=submit]", "inspector-action-set-node", "submit");
        await Change(cut, "[data-event=submit]", "inspector-action-set-state", "loading");
        NoRefusal(cut);

        // A labelled image on the first screen.
        await Select(cut, "screen");
        await Insert(cut, "image");
        await Change(cut, "[data-testid=inspector-a11y]", "inspector-a11y-label", "A chart of sign-ups by week");
        NoRefusal(cut);

        await cut.InvokeAsync(() => cut.Find("[data-testid=mockup-save]").Click());
        cut.WaitForAssertion(() => Assert.Contains("Saved", cut.Find("[data-testid=mockup-status]").TextContent));

        var stored = await _wiki.GetByNumberAsync(item.Number);
        var block = DesignMockup.Read(stored!.Body).Block!;
        var read = MockupReader.Read(block.Json);
        Assert.Empty(read.Errors);

        var tree = (JsonObject)JsonNode.Parse(block.Json)!;
        Assert.Equal(["wide", "narrow"], tree["viewports"]!.AsArray().Select(v => (string)v!["name"]!));
        var screen = tree["screens"]![0]!;
        var form = screen["children"]![0]!;
        Assert.Equal("""{"narrow":{"layout":"row"}}""", form["at"]!.ToJsonString());
        Assert.Equal("""{"submit":{"go":"screen-2","set":{"submit":"loading"}}}""", form["on"]!.ToJsonString());
        var button = form["children"]![0]!;
        Assert.Equal("submit", (string?)button["id"]);
        Assert.Equal("""{"disabled":{},"loading":{"text":"Sending..."}}""", button["states"]!.ToJsonString());
        var image = screen["children"]!.AsArray().Single(n => (string?)n!["type"] == "image")!;
        Assert.Equal("""{"label":"A chart of sign-ups by week"}""", image["a11y"]!.ToJsonString());
        Assert.Equal("screen-2", (string?)tree["screens"]![1]!["id"]);
    }
}
