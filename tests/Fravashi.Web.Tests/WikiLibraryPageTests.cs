using Bunit;
using Fravashi.Application.Mirroring;
using Fravashi.Application.Wiki;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Projects;
using Fravashi.Domain.Wiki;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Rendering;
using Fravashi.Infrastructure.Wiki;
using Fravashi.Infrastructure.WorkItems;
using Fravashi.Web.Components.Pages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 39's acceptance at the surface a person uses: documents of every type created, edited, linked and
/// browsed, references that lead somewhere real, and a reference to nothing that says so.
/// </summary>
public sealed class WikiLibraryPageTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fravashi-tests", Guid.NewGuid().ToString("N"));

    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWikiLibrary _wiki;
    private readonly LocalTrackerResolver _trackers;
    private readonly ITracker _board;
    private readonly PageSite _site = new();
    private readonly ScriptedAgent _agent = new();

    public WikiLibraryPageTests()
    {
        Directory.CreateDirectory(_directory);
        _factory = new SqliteConnectionFactory(
            new SqliteStoreOptions(Path.Combine(_directory, "test.db"), null, Pooling: false));
        SqliteSchema.EnsureCreated(_factory);
        _wiki = new SqliteWikiLibrary(_factory, TimeProvider.System, Fravashi.Domain.Projects.ProjectId.Default);
        _trackers = new LocalTrackerResolver(_factory, TimeProvider.System);
        _board = _trackers.For(ProjectId.Default);
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

    private BunitContext Context(string url, Action<IServiceCollection>? more = null)
    {
        var ctx = new BunitContext();
        // Backlog 121: a page asks for its project's wiki, and what holds an id asks for the by-id view.
        ctx.Services.AddSingleton<Fravashi.Application.Wiki.IWikiLibraries>(new OneWiki(_wiki));
        ctx.Services.AddSingleton<Fravashi.Application.Wiki.IWikiDocuments>(_wiki);
        ctx.Services.AddSingleton<ITrackerResolver>(_trackers);
        // Backlog 121: a page follows its window's project; these tests have one project.
        ctx.Services.AddSingleton<Fravashi.Application.Projects.ICurrentProject>(Fravashi.Application.Projects.SingleProject.Instance);
        ctx.Services.AddSingleton<Fravashi.Application.Projects.IProjectStore>(new OneProjectStore());
        ctx.Services.AddSingleton(new WikiReferenceResolver(_trackers, new OneWiki(_wiki)));
        ctx.Services.AddSingleton(new WikiMarkdownRenderer());
        ctx.Services.AddSingleton(new WikiPublisher(
            _wiki, new SqliteWikiPublicationStore(_factory), _site, new FixedTarget(), TimeProvider.System));
        ctx.Services.AddSingleton(new WikiSummariser(
            _wiki, _agent, new SqliteWikiSummaryStore(_factory), TimeProvider.System));
        // The page sizes its sidebar sections through sections.js, as the Code page does.
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        more?.Invoke(ctx.Services);
        ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().NavigateTo(url);
        return ctx;
    }

    private async Task<WikiItem> WikiAsync(string title, WikiItemType type = WikiItemType.Draft, string body = "")
    {
        var created = await _wiki.CreateAsync(new NewWikiItem { Type = type, Title = title, Body = body });
        Assert.True(created.IsValid, string.Join("; ", created.Errors.Select(e => e.Code)));
        return created.Value;
    }

    private async Task<WorkItem> ItemAsync(string title)
    {
        var created = await _board.CreateAsync(
            new NewWorkItem { Level = WorkItemLevel.Task, Title = title, Description = "Why it exists." },
            MutationContext.User);
        Assert.Equal(TrackerOutcome.Ok, created.Outcome);
        return created.Value;
    }

    // ---- browsing ------------------------------------------------------------------------------

    // ---- backlog 52: a design from a drawing ---------------------------------------------------------

    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    /// <summary>The page with what "New design from a drawing" needs: a workspace with a copy, the registry, the starter.</summary>
    private BunitContext DrawingContext(Fravashi.Application.AgentRuntimes.AgentRuntimeDescriptor? runtime = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [Fravashi.Infrastructure.Bridge.BridgeWorkspaceContext.ActiveIdKey] = "w1",
            })
            .Build();
        var ctx = Context("http://localhost/wiki", services =>
        {
            services.AddSingleton(new Fravashi.Infrastructure.Bridge.BridgeWorkspaceContext(configuration));
            services.AddSingleton<Fravashi.Application.AgentSync.IWorkspaceAgentSync>(new CopySync());
            services.AddSessionSurface();
            services.AddSingleton(sp => new Fravashi.Infrastructure.Sessions.DesignFromDrawingStarter(
                sp.GetRequiredService<Fravashi.Infrastructure.Sessions.LiveSessionRegistry>(), runtime));
        });
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    [Fact]
    public async Task Find_stays_usable_and_gives_the_list_no_band_of_height_it_does_not_need()
    {
        await WikiAsync("Alpha notes");
        await WikiAsync("Beta plan");
        using var ctx = Context("http://localhost/wiki");
        var cut = ctx.Render<WikiLibraryPage>();

        Assert.Equal("true", cut.Find("[data-testid=wiki-list-section] .page-sidebar-section-header").GetAttribute("aria-expanded"));
        Assert.Contains("wiki-sidebar", cut.Find("[data-testid=wiki-sidebar]").ClassName);

        await cut.InvokeAsync(() => cut.Find("[data-testid=wiki-search]").Change("Alpha"));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=wiki-entry]")));

        // Work item #365: the empty band under Find was sections.js giving Find a fixed share of the height. The page no
        // longer asks for shares; Find is as tall as its controls and the list takes the rest (studio.css).
        Assert.DoesNotContain(ctx.JSInterop.Invocations, i => i.Identifier == "fravashiSections.apply");
    }

    [Fact]
    public async Task New_design_from_a_drawing_starts_a_conversation_carrying_the_drawing_and_writes_no_document()
    {
        using var ctx = DrawingContext();
        var nav = ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var cut = ctx.Render<WikiLibraryPage>();

        await cut.InvokeAsync(() => cut.Find("[data-testid=new-design-from-drawing]").Click());
        Assert.True(cut.Find("[data-testid=drawing-start]").HasAttribute("disabled"), "nothing to start from yet");

        cut.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>()
            .UploadFiles(InputFileContent.CreateFromBinary(PngBytes, "drawing.png", contentType: "image/png"));
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=drawing-chosen] img")));
        await cut.InvokeAsync(() => cut.Find("[data-testid=drawing-note]").Input("A sign-up form."));
        await cut.InvokeAsync(() => cut.Find("[data-testid=drawing-start]").Click());

        var registry = ctx.Services.GetRequiredService<Fravashi.Infrastructure.Sessions.LiveSessionRegistry>();
        var session = Assert.Single(registry.Sessions);
        Assert.EndsWith($"/sessions/{session.Id.Value:D}", nav.Uri, StringComparison.Ordinal);
        Assert.Equal("w1", session.WorkspaceId);

        var attached = Assert.Single(session.Transcript.OfType<Fravashi.Domain.AgentSessions.UserImagesAttached>());
        Assert.Equal(Convert.ToBase64String(PngBytes), Assert.Single(attached.Images).Base64);
        var prompt = Assert.Single(session.Transcript.OfType<Fravashi.Domain.AgentSessions.MessageEmitted>());
        Assert.Equal(Fravashi.Domain.AgentSessions.TranscriptRole.StudioPrompt, prompt.Role);
        Assert.Equal(Fravashi.Application.Mockups.DesignFromDrawing.Instruction("A sign-up form."), prompt.Text);

        // The design is the agent's to write, as a tree; the studio stored nothing in the wiki, image or otherwise.
        Assert.Empty(await _wiki.ListAsync(new WikiItemQuery { Limit = 1000 }));
    }

    [Fact]
    public async Task A_runtime_that_cannot_see_images_is_said_in_place_of_the_chooser()
    {
        var blind = Fravashi.Infrastructure.Sessions.ConversationRuntime.Descriptor with { SeesImages = false };
        using var ctx = DrawingContext(blind);
        var cut = ctx.Render<WikiLibraryPage>();

        await cut.InvokeAsync(() => cut.Find("[data-testid=new-design-from-drawing]").Click());

        Assert.Equal(
            Fravashi.Application.Mockups.DesignFromDrawing.CannotSeeImages(blind),
            cut.Find("[data-testid=drawing-unavailable]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=drawing-file]"));
        Assert.Empty(cut.FindAll("[data-testid=drawing-start]"));
        Assert.Empty(ctx.Services.GetRequiredService<Fravashi.Infrastructure.Sessions.LiveSessionRegistry>().Sessions);
    }

    [Fact]
    public async Task A_file_that_is_not_an_image_is_refused_in_the_attachments_own_words()
    {
        using var ctx = DrawingContext();
        var cut = ctx.Render<WikiLibraryPage>();
        await cut.InvokeAsync(() => cut.Find("[data-testid=new-design-from-drawing]").Click());

        cut.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>()
            .UploadFiles(InputFileContent.CreateFromText("not a picture", "notes.png", contentType: "image/png"));

        cut.WaitForAssertion(() => Assert.Contains(
            "Only PNG, JPEG, GIF and WebP images can be sent to the agent.",
            cut.Find("[data-testid=drawing-error]").TextContent,
            StringComparison.Ordinal));
        Assert.True(cut.Find("[data-testid=drawing-start]").HasAttribute("disabled"));
    }

    [Fact]
    public void A_studio_prompt_is_paired_with_the_drawing_recorded_before_it()
    {
        var drawing = Fravashi.Domain.AgentSessions.AgentImageAttachment.TryCreate(PngBytes, out _)!;
        var now = DateTimeOffset.UtcNow;
        IReadOnlyList<Fravashi.Domain.AgentSessions.AgentEvent> transcript =
        [
            new Fravashi.Domain.AgentSessions.UserImagesAttached(now, [drawing]),
            new Fravashi.Domain.AgentSessions.MessageEmitted(now, Fravashi.Domain.AgentSessions.TranscriptRole.StudioPrompt, "Turn it into a design."),
        ];

        Assert.Same(drawing, Assert.Single(Fravashi.Web.Components.Sessions.TranscriptImages.For(transcript, 1)!));
    }

    private sealed class CopySync : Fravashi.Application.AgentSync.IWorkspaceAgentSync
    {
        public void EnsureStarted(string workspaceId) { }

        public string? CopyPathFor(string workspaceId) => "/workspaces/" + workspaceId;

        public object? StatusFor(string workspaceId) => null;
    }

    // ---- backlog 197: a design built from nothing ----------------------------------------------------

    [Fact]
    public async Task A_screen_is_built_from_New_design_with_the_palette_alone_and_saves_as_a_valid_tree()
    {
        using var ctx = Context("http://localhost/wiki");
        // NavigationLock asks the browser to confirm leaving while the editor has edits; no browser here.
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var nav = ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var wiki = ctx.Render<WikiLibraryPage>();

        await wiki.InvokeAsync(() => wiki.Find("[data-testid=new-design]").Click());

        var number = int.Parse(nav.Uri.Split('/')[^2], System.Globalization.CultureInfo.InvariantCulture);
        Assert.EndsWith($"/wiki/{number}/design", nav.Uri, StringComparison.Ordinal);
        var created = (await _wiki.GetByNumberAsync(number))!;
        Assert.Equal(WikiItemType.Design, created.Type);
        Assert.Equal("New design", created.Title);

        var cut = ctx.Render<Fravashi.Web.Components.Mockups.DesignEditorPage>(p => p.Add(c => c.Number, number));
        Assert.Equal(["window"], Types(cut));

        await cut.InvokeAsync(() => Row(cut, "window").Click());
        await Insert(cut, "toolbar");
        await Place(cut, "after");
        await Insert(cut, "form");
        await Place(cut, "auto");
        await Insert(cut, "field");
        await Insert(cut, "field");
        await Insert(cut, "field");
        await Insert(cut, "button");
        await cut.InvokeAsync(() => Row(cut, "form").Click());
        await Place(cut, "after");
        await Insert(cut, "table");

        Assert.Equal(["window", "toolbar", "form", "field", "field", "field", "button", "table"], Types(cut));
        Assert.Equal("table", cut.Find("[data-testid=mockup-outline-row][aria-selected=true] .mk-outline-type").TextContent);

        await cut.InvokeAsync(() => cut.Find("[data-testid=mockup-save]").Click());

        var stored = (await _wiki.GetByNumberAsync(number))!;
        Assert.Equal(created.Version + 1, stored.Version);
        var read = Fravashi.Application.Mockups.DesignMockup.Read(stored.Body);
        Assert.True(read.IsValid, string.Join("; ", read.Errors));
        var screen = read.Tree!.Document!.Screens.Single();
        Assert.Equal(["toolbar", "form", "table"], screen.Children!.Select(c => Name(c.Type)));
        Assert.Equal(["field", "field", "field", "button"], screen.Children![1].Children!.Select(c => Name(c.Type)));
        Assert.StartsWith(Fravashi.Application.Mockups.DesignMockup.NewBody()[..20], stored.Body, StringComparison.Ordinal);

        static string Name(Fravashi.Application.Mockups.MockupComponent type) =>
            Fravashi.Application.Mockups.MockupVocabulary.Name(type);
    }

    private static string[] Types(IRenderedComponent<Fravashi.Web.Components.Mockups.DesignEditorPage> cut) =>
        cut.FindAll("[data-testid=mockup-outline-row] .mk-outline-type").Select(e => e.TextContent).ToArray();

    private static AngleSharp.Dom.IElement Row(IRenderedComponent<Fravashi.Web.Components.Mockups.DesignEditorPage> cut, string type) =>
        cut.FindAll("[data-testid=mockup-outline-row]").First(r => r.QuerySelector(".mk-outline-type")!.TextContent == type);

    private static Task Insert(IRenderedComponent<Fravashi.Web.Components.Mockups.DesignEditorPage> cut, string type) =>
        cut.InvokeAsync(() => cut.Find($"[data-testid=mockup-palette-item][data-type={type}]").Click());

    private static Task Place(IRenderedComponent<Fravashi.Web.Components.Mockups.DesignEditorPage> cut, string place) =>
        cut.InvokeAsync(() => cut.Find("[data-testid=mockup-palette-place]").Change(place));

    // ---- backlog 49: the documents as a tree ---------------------------------------------------------

    [Fact]
    public async Task Documents_are_grouped_by_kind_with_counts_and_a_group_opens_to_its_newest_first()
    {
        // Work item #365: groups by kind replace the folder tree in the list (the tree stays in the breadcrumb).
        var older = await WikiAsync("Older plan", WikiItemType.Plan);
        await WikiAsync("Newer plan", WikiItemType.Plan);
        await WikiAsync("A screenshot", WikiItemType.Evidence);
        await Task.Delay(20);
        var edited = await _wiki.UpdateAsync(older.Id, new WikiItemEdit { Body = "Now the newest." }, older.Version);
        Assert.True(edited.IsValid);
        using var ctx = Context("http://localhost/wiki");

        var cut = ctx.Render<WikiLibraryPage>();

        var groups = cut.FindAll("[data-testid=wiki-group]");
        Assert.Equal(["plan", "evidence"], groups.Select(g => g.GetAttribute("data-type") ?? string.Empty).ToArray());
        Assert.Equal(["2", "1"], cut.FindAll("[data-testid=wiki-group-count]").Select(c => c.TextContent).ToArray());
        Assert.Empty(cut.FindAll("[data-testid=wiki-entry]"));

        await cut.InvokeAsync(() => cut.FindAll("[data-testid=wiki-group-toggle]")[0].Click());

        cut.WaitForAssertion(() => Assert.Equal(
            ["Older plan", "Newer plan"], cut.FindAll(".wiki-entry-title").Select(e => e.TextContent).ToArray()));
        Assert.Equal("Older plan", cut.Find("[data-testid=wiki-entry]").GetAttribute("title"));
        Assert.Equal("true", cut.FindAll("[data-testid=wiki-group-toggle]")[0].GetAttribute("aria-expanded"));

        await cut.InvokeAsync(() => cut.FindAll("[data-testid=wiki-group-toggle]")[0].Click());
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=wiki-entry]")));
    }

    [Fact]
    public async Task The_open_documents_group_is_open_and_the_entry_marked()
    {
        var plan = await WikiAsync("The plan", WikiItemType.Plan);
        await WikiAsync("Evidence", WikiItemType.Evidence);
        using var ctx = Context($"http://localhost/wiki/{plan.Number}");

        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, plan.Number));

        var entry = Assert.Single(cut.FindAll("[data-testid=wiki-entry]"));
        Assert.Contains("selected", entry.ClassName);
        Assert.Contains("The plan", entry.TextContent);
    }

    [Fact]
    public async Task Publish_everything_sends_the_whole_wiki_and_says_what_happened()
    {
        var folder = await WikiAsync("plans", WikiItemType.Reference, "The plans.");
        await _wiki.CreateAsync(new NewWikiItem { Type = WikiItemType.Plan, Title = "A plan", Body = "Steps.", ParentId = folder.Id });
        using var ctx = Context("http://localhost/wiki");
        var cut = ctx.Render<WikiLibraryPage>();

        await cut.InvokeAsync(() => cut.Find("[data-testid=wiki-publish-all]").Click());

        cut.WaitForAssertion(() => Assert.Equal(
            "2 new, 0 updated, 0 unchanged, 0 not published.",
            cut.Find("[data-testid=wiki-publish-all-said]").TextContent.Trim()));
        Assert.Equal(2, _site.Pages.Count);
    }

    [Fact]
    public async Task With_no_wiki_that_can_be_written_to_nothing_offers_to_publish_everything()
    {
        _site.IsConfigured = false;
        await WikiAsync("Anything");
        using var ctx = Context("http://localhost/wiki");

        var cut = ctx.Render<WikiLibraryPage>();

        Assert.Empty(cut.FindAll("[data-testid=wiki-publish-all]"));
    }

    [Fact]
    public async Task A_document_opened_shows_where_it_sits_and_its_folder_open()
    {
        var folder = await WikiAsync("plans", WikiItemType.Reference);
        var child = (await _wiki.CreateAsync(new NewWikiItem { Type = WikiItemType.Plan, Title = "A plan", ParentId = folder.Id })).Value;
        using var ctx = Context($"http://localhost/wiki/{child.Number}");

        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, child.Number));

        cut.WaitForAssertion(() => Assert.Contains("plans", cut.Find("[data-testid=wiki-breadcrumb]").TextContent));
        Assert.Contains("A plan", cut.FindAll(".wiki-entry-title").Select(e => e.TextContent));
    }

    [Fact]
    public async Task A_document_can_be_moved_under_another_from_the_editor_but_never_under_itself()
    {
        var folder = await WikiAsync("design", WikiItemType.Reference);
        var page = await WikiAsync("Threat model", WikiItemType.Reference);
        using var ctx = Context($"http://localhost/wiki/{page.Number}");
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, page.Number));

        await cut.InvokeAsync(() => cut.Find("[data-testid=wiki-edit]").Click());
        var options = cut.FindAll("[data-testid=wiki-parent] option").Select(o => o.GetAttribute("value")).ToList();
        Assert.Contains(folder.Id, options);
        Assert.DoesNotContain(page.Id, options);

        await cut.InvokeAsync(() => cut.Find("[data-testid=wiki-parent]").Change(folder.Id));
        await cut.InvokeAsync(() => cut.Find("[data-testid=wiki-save]").Click());

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=wiki-editor]")));
        Assert.Equal(folder.Id, (await _wiki.GetAsync(page.Id))!.ParentId);
    }
    [Fact]
    public async Task Every_document_is_listed_and_the_type_filter_narrows_it()
    {
        await WikiAsync("The release plan", WikiItemType.Plan);
        await WikiAsync("A stray thought", WikiItemType.Draft);

        using var ctx = Context("http://localhost/wiki");
        var cut = ctx.Render<WikiLibraryPage>();

        Assert.Equal(2, cut.FindAll("[data-testid=wiki-group]").Count);

        await cut.InvokeAsync(() => cut.Find("[data-testid=wiki-type-filter]").Change("plan"));

        cut.WaitForAssertion(() =>
        {
            var entries = cut.FindAll("[data-testid=wiki-entry]");
            Assert.Single(entries);
            Assert.Contains("The release plan", entries[0].TextContent);
        });
    }

    [Fact]
    public async Task The_search_narrows_by_body_as_well_as_title()
    {
        await WikiAsync("Notes", WikiItemType.Draft, "Everything about the release pipeline.");
        await WikiAsync("Other notes", WikiItemType.Draft, "Nothing relevant.");

        using var ctx = Context("http://localhost/wiki");
        var cut = ctx.Render<WikiLibraryPage>();

        await cut.InvokeAsync(() => cut.Find("[data-testid=wiki-search]").Change("release pipeline"));

        cut.WaitForAssertion(() =>
        {
            var entries = cut.FindAll("[data-testid=wiki-entry]");
            Assert.Single(entries);
            Assert.Contains("Notes", entries[0].TextContent);
        });
    }

    [Fact]
    public async Task A_document_opened_by_number_shows_its_body()
    {
        var page = await WikiAsync("How the gate works", WikiItemType.Governance, "It is **not** an apology.");

        using var ctx = Context($"http://localhost/wiki/{page.Number}");
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, page.Number));

        var body = cut.Find("[data-testid=wiki-article-body]").InnerHtml;
        Assert.Contains("<strong>not</strong>", body);
        Assert.Contains("Governance", cut.Find("[data-testid=wiki-article-type]").TextContent);
    }

    [Fact]
    public void A_number_nothing_answers_to_says_so()
    {
        using var ctx = Context("http://localhost/wiki/99");
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, 99));

        Assert.NotEmpty(cut.FindAll("[data-testid=wiki-missing]"));
    }

    // ---- references ----------------------------------------------------------------------------

    [Fact]
    public async Task A_reference_to_a_real_work_item_becomes_a_link_into_the_board()
    {
        var item = await ItemAsync("Wire the approval prompt");
        var page = await WikiAsync("Why", WikiItemType.Governance, $"This explains #{item.Number}.");

        using var ctx = Context($"http://localhost/wiki/{page.Number}");
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, page.Number));

        var body = cut.Find("[data-testid=wiki-article-body]").InnerHtml;
        Assert.Contains($"href=\"/workitems/{item.Number}\"", body);
        Assert.Contains("Wire the approval prompt", body);
    }

    [Fact]
    public async Task A_reference_to_another_document_becomes_a_link_into_the_wiki()
    {
        var target = await WikiAsync("The target");
        var page = await WikiAsync("The pointer", WikiItemType.Draft, $"See @{target.Number}.");

        using var ctx = Context($"http://localhost/wiki/{page.Number}");
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, page.Number));

        Assert.Contains(
            $"href=\"/wiki/{target.Number}\"", cut.Find("[data-testid=wiki-article-body]").InnerHtml);
    }

    [Fact]
    public async Task A_reference_to_something_that_does_not_exist_is_visibly_broken()
    {
        var page = await WikiAsync("Wrong", WikiItemType.Draft, "This points at #4321, which is nothing.");

        using var ctx = Context($"http://localhost/wiki/{page.Number}");
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, page.Number));

        var body = cut.Find("[data-testid=wiki-article-body]").InnerHtml;
        Assert.Contains(WikiMarkdownRenderer.BrokenClass, body);
        Assert.DoesNotContain("href=\"/workitems/4321\"", body);
    }

    [Fact]
    public async Task What_points_at_a_document_is_shown_on_the_document_it_points_at()
    {
        var target = await WikiAsync("The target");
        await WikiAsync("The pointer", WikiItemType.Draft, $"See @{target.Number}.");

        using var ctx = Context($"http://localhost/wiki/{target.Number}");
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, target.Number));

        var backlinks = cut.FindAll("[data-testid=wiki-backlink]");
        Assert.Single(backlinks);
        Assert.Contains("The pointer", backlinks[0].TextContent);
    }

    // ---- work item #365: the right-hand column ------------------------------------------------------

    [Fact]
    public async Task A_work_item_and_a_wiki_reference_each_preview_on_the_right_with_an_open_button_and_the_document_stays()
    {
        var item = await ItemAsync("Wire the approval prompt");
        var target = await WikiAsync("The target", WikiItemType.Reference, "What the target says.");
        var page = await WikiAsync("The pointer", WikiItemType.Draft, $"This explains #{item.Number} and @{target.Number}.");
        using var ctx = Context($"http://localhost/wiki/{page.Number}");
        var nav = ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, page.Number));
        Assert.Contains(ctx.JSInterop.Invocations, i => i.Identifier == "fravashiWiki.attach");

        // What wikiPage.js hands over on a plain click of the link.
        await cut.InvokeAsync(() => cut.Instance.OpenReference($"#{item.Number}"));

        cut.WaitForAssertion(() => Assert.Equal("Wire the approval prompt", cut.Find("[data-testid=ref-preview-title]").TextContent));
        Assert.Contains("docked", cut.Find("[data-testid=ref-preview]").ClassName);
        Assert.Empty(cut.FindAll("[data-testid=wiki-outline]"));
        Assert.Single(cut.FindAll("[data-testid=wiki-reader-main] [data-testid=wiki-article]"));
        Assert.Contains("This explains", cut.Find("[data-testid=wiki-article-body]").TextContent);
        Assert.EndsWith($"/wiki/{page.Number}", nav.Uri, StringComparison.Ordinal);

        await cut.InvokeAsync(() => cut.Instance.OpenReference($"@{target.Number}"));

        cut.WaitForAssertion(() => Assert.Equal("The target", cut.Find("[data-testid=ref-preview-title]").TextContent));
        Assert.Contains("What the target says.", cut.Find("[data-testid=ref-preview-excerpt]").TextContent);
        Assert.EndsWith($"/wiki/{page.Number}", nav.Uri, StringComparison.Ordinal);

        await cut.InvokeAsync(() => cut.Find("[data-testid=ref-preview-open]").Click());
        Assert.EndsWith($"/wiki/{target.Number}", nav.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Closing_the_preview_brings_back_the_documents_own_navigation()
    {
        var target = await WikiAsync("The target");
        var page = await WikiAsync("The pointer", WikiItemType.Draft, $"# Top\n\nSee @{target.Number}.");
        using var ctx = Context($"http://localhost/wiki/{page.Number}");
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, page.Number));

        await cut.InvokeAsync(() => cut.Instance.OpenReference($"@{target.Number}"));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=ref-preview]")));
        await cut.InvokeAsync(() => cut.Find("[data-testid=ref-preview-close]").Click());

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=wiki-outline]")));
        Assert.Empty(cut.FindAll("[data-testid=ref-preview]"));
    }

    [Fact]
    public async Task An_open_document_lists_its_headings_and_a_click_scrolls_to_one()
    {
        var page = await WikiAsync("Plan", WikiItemType.Plan, "# Why\n\nBecause.\n\n## How it is built\n\nLike this.\n");
        using var ctx = Context($"http://localhost/wiki/{page.Number}");
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, page.Number));

        var entries = cut.FindAll("[data-testid=wiki-outline-entry]");
        Assert.Equal(["Why", "How it is built"], entries.Select(e => e.TextContent).ToArray());

        await cut.InvokeAsync(() => cut.FindAll("[data-testid=wiki-outline-entry]")[1].Click());

        var scroll = Assert.Single(ctx.JSInterop.Invocations, i => i.Identifier == "fravashiWiki.scrollTo");
        var id = Assert.IsType<string>(scroll.Arguments[1]);
        Assert.Contains($"id=\"{id}\"", cut.Find("[data-testid=wiki-article-body]").InnerHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Linked_from_lists_the_documents_and_the_work_items_that_name_this_one()
    {
        var target = await WikiAsync("The plan", WikiItemType.Plan);
        await WikiAsync("A pointer", WikiItemType.Draft, $"See @{target.Number}.");
        var mentioning = await _board.CreateAsync(
            new NewWorkItem { Level = WorkItemLevel.Task, Title = "Mentions it", Description = $"Read @{target.Number} first." },
            MutationContext.User);
        var planned = await _board.CreateAsync(
            new NewWorkItem
            {
                Level = WorkItemLevel.Task,
                Title = "Planned by it",
                Description = "Nothing written.",
                Refs = [new Fravashi.Domain.WorkItems.AgentLayer.ArtifactRef(
                    "plan-1", Fravashi.Domain.WorkItems.AgentLayer.ArtifactRefRole.Plan,
                    Fravashi.Domain.WorkItems.AgentLayer.ArtifactRefKind.Wiki, target.Number.ToString(System.Globalization.CultureInfo.InvariantCulture))],
            },
            MutationContext.User);
        await ItemAsync("Unrelated");
        using var ctx = Context($"http://localhost/wiki/{target.Number}");

        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, target.Number));

        Assert.Contains("A pointer", Assert.Single(cut.FindAll("[data-testid=wiki-backlink]")).TextContent);
        var items = cut.FindAll("[data-testid=wiki-backlink-item]");
        Assert.Equal(
            new[] { mentioning.Value.Number, planned.Value.Number }.Order().Select(n => "#" + n).ToArray(),
            items.Select(i => i.GetAttribute("data-ref")!).Order().ToArray());
        Assert.All(items, i => Assert.Contains(WikiMarkdownRenderer.ResolvedClass, i.ClassName));
    }

    [Fact]
    public async Task A_file_path_in_a_document_previews_the_start_of_the_file_with_a_button_to_the_editor()
    {
        var page = await WikiAsync("Where", WikiItemType.Reference, "The look lives in `src/Fravashi.Web/wwwroot/studio.css`.");
        var reader = new OneFileReader("src/Fravashi.Web/wwwroot/studio.css", ":root {\n    --surface: white;\n}\n");
        using var ctx = Context($"http://localhost/wiki/{page.Number}", s => s.AddSingleton<Fravashi.Application.Repository.IRepositoryReader>(reader));
        var nav = ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, page.Number));

        var link = cut.Find("[data-testid=wiki-article-body] a.wiki-file-ref");
        Assert.Equal("src/Fravashi.Web/wwwroot/studio.css", link.GetAttribute("data-path"));

        await cut.InvokeAsync(() => cut.Instance.OpenFile(link.GetAttribute("data-path")!));

        cut.WaitForAssertion(() => Assert.Contains("--surface: white;", cut.Find("[data-testid=ref-preview-file]").TextContent));
        Assert.Equal("studio.css", cut.Find("[data-testid=ref-preview-title]").TextContent);

        await cut.InvokeAsync(() => cut.Find("[data-testid=ref-preview-open]").Click());
        Assert.EndsWith("/code/src/Fravashi.Web/wwwroot/studio.css", nav.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_empty_reading_pane_lists_the_recently_changed_documents()
    {
        await WikiAsync("First", WikiItemType.Plan);
        await Task.Delay(20);
        await WikiAsync("Second", WikiItemType.Evidence);
        using var ctx = Context("http://localhost/wiki");
        var nav = ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();

        var cut = ctx.Render<WikiLibraryPage>();

        var recent = cut.FindAll("[data-testid=wiki-recent]");
        Assert.Equal(2, recent.Count);
        Assert.Contains("Second", recent[0].TextContent);
        Assert.Empty(cut.FindAll("[data-testid=wiki-aside]"));

        await cut.InvokeAsync(() => cut.FindAll("[data-testid=wiki-recent]")[1].Click());
        Assert.EndsWith("/wiki/1", nav.Uri, StringComparison.Ordinal);
    }

    private sealed class OneFileReader(string path, string content) : Fravashi.Application.Repository.IRepositoryReader
    {
        public bool IsConfigured => true;

        public string RootPath => "/workspace";

        public Task<Fravashi.Domain.Repository.RepositoryNode> GetTreeAsync(string relativePath = "", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Fravashi.Domain.Repository.RepositoryFile?> ReadFileAsync(string relativePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(relativePath == path ? new Fravashi.Domain.Repository.RepositoryFile(path, content, "css") : null);

        public Task<byte[]?> ReadBytesAsync(string relativePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]?>(null);
    }

    [Fact]
    public async Task A_document_nothing_points_at_says_so_rather_than_showing_an_empty_heading()
    {
        var page = await WikiAsync("Alone");

        using var ctx = Context($"http://localhost/wiki/{page.Number}");
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, page.Number));

        Assert.NotEmpty(cut.FindAll("[data-testid=wiki-no-backlinks]"));
    }

    // ---- authoring -----------------------------------------------------------------------------

    [Fact]
    public async Task A_document_can_be_created_from_the_page()
    {
        using var ctx = Context("http://localhost/wiki");
        var cut = ctx.Render<WikiLibraryPage>();

        await cut.InvokeAsync(() => cut.Find("[data-testid=new-wiki-item]").Click());
        await cut.InvokeAsync(() =>
        {
            cut.Find("[data-testid=wiki-title]").Input("A new architecture note");
            cut.Find("[data-testid=wiki-type]").Change("architecture");
            cut.Find("[data-testid=wiki-body-input]").Input("How it is built.");
        });
        await cut.InvokeAsync(() => cut.Find("[data-testid=wiki-save]").Click());

        var stored = await _wiki.ListAsync(new WikiItemQuery());
        var created = Assert.Single(stored);
        Assert.Equal("A new architecture note", created.Title);
        Assert.Equal(WikiItemType.Architecture, created.Type);
        Assert.Equal("How it is built.", created.Body);
    }

    [Fact]
    public async Task A_document_with_no_title_shows_the_models_own_refusal()
    {
        using var ctx = Context("http://localhost/wiki");
        var cut = ctx.Render<WikiLibraryPage>();

        await cut.InvokeAsync(() => cut.Find("[data-testid=new-wiki-item]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=wiki-save]").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.NotEmpty(cut.FindAll("[data-testid=wiki-error]"));
            Assert.NotEmpty(cut.FindAll("[data-testid=wiki-editor]"));
        });

        Assert.Empty(await _wiki.ListAsync(new WikiItemQuery()));
    }

    [Fact]
    public async Task A_document_can_be_edited_and_the_change_is_stored()
    {
        var page = await WikiAsync("The old title", WikiItemType.Draft, "The old body.");

        using var ctx = Context($"http://localhost/wiki/{page.Number}");
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, page.Number));

        await cut.InvokeAsync(() => cut.Find("[data-testid=wiki-edit]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=wiki-title]").Input("The new title"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=wiki-save]").Click());

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=wiki-article]")));

        var stored = (await _wiki.GetByNumberAsync(page.Number))!;
        Assert.Equal("The new title", stored.Title);
        Assert.Equal("The old body.", stored.Body);
    }

    [Fact]
    public async Task A_second_branding_document_is_refused_with_the_models_own_sentence()
    {
        await WikiAsync("Brand", WikiItemType.Branding);

        using var ctx = Context("http://localhost/wiki");
        var cut = ctx.Render<WikiLibraryPage>();

        await cut.InvokeAsync(() => cut.Find("[data-testid=new-wiki-item]").Click());
        await cut.InvokeAsync(() =>
        {
            cut.Find("[data-testid=wiki-title]").Input("Brand again");
            cut.Find("[data-testid=wiki-type]").Change("branding");
        });
        await cut.InvokeAsync(() => cut.Find("[data-testid=wiki-save]").Click());

        cut.WaitForAssertion(() =>
            Assert.Contains("already has a branding item", cut.Find("[data-testid=wiki-error]").TextContent));
    }

    [Fact]
    public async Task A_branding_document_holds_the_projects_colours_logos_and_type()
    {
        // The acceptance asks for a branding item that holds these. It is a document like any other, which
        // is the point: the project's look is written down where everything else about the project is.
        var brand = await WikiAsync(
            "Brand",
            WikiItemType.Branding,
            "## Colours\n\n- Accent `#3b6ef5`\n\n## Logos\n\n- `assets/logo.svg`\n\n## Type\n\n- Inter\n");

        using var ctx = Context($"http://localhost/wiki/{brand.Number}");
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, brand.Number));

        var body = cut.Find("[data-testid=wiki-article-body]").TextContent;
        Assert.Contains("Colours", body);
        Assert.Contains("assets/logo.svg", body);
        Assert.Contains("Inter", body);
    }

    // ---- designs (backlog 193) ----------------------------------------------------------------

    private const string Tree =
        """{"mockup": 1, "title": "Sign in", "screens": [{"type": "window", "id": "home", "text": "Home", "children": [{"type": "text", "text": "Ticket #3 is open"}]}]}""";

    private static string Design(string tree) => $"Before the tree.\n\n```mockup\n{tree}\n```\n\nAfter the tree.\n";

    [Fact]
    public async Task A_design_can_be_created_from_the_page_and_is_offered_wherever_types_are_listed()
    {
        using var ctx = Context("http://localhost/wiki");
        var cut = ctx.Render<WikiLibraryPage>();

        Assert.Contains("design", cut.FindAll("[data-testid=wiki-type-filter] option").Select(o => o.GetAttribute("value")));

        await cut.InvokeAsync(() => cut.Find("[data-testid=new-wiki-item]").Click());
        Assert.Contains("design", cut.FindAll("[data-testid=wiki-type] option").Select(o => o.GetAttribute("value")));
        await cut.InvokeAsync(() =>
        {
            cut.Find("[data-testid=wiki-title]").Input("Sign in screen");
            cut.Find("[data-testid=wiki-type]").Change("design");
            cut.Find("[data-testid=wiki-body-input]").Input(Design(Tree));
        });
        await cut.InvokeAsync(() => cut.Find("[data-testid=wiki-save]").Click());

        var created = Assert.Single(await _wiki.ListAsync(new WikiItemQuery()));
        Assert.Equal(WikiItemType.Design, created.Type);
    }

    [Fact]
    public async Task A_design_shows_its_prose_with_the_preview_where_the_block_was_and_the_json_one_click_away()
    {
        var design = await WikiAsync("Sign in screen", WikiItemType.Design, Design(Tree));

        using var ctx = Context($"http://localhost/wiki/{design.Number}");
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, design.Number));

        var body = cut.Find("[data-testid=wiki-article-body]");
        var text = body.TextContent;
        Assert.True(text.IndexOf("Before the tree.", StringComparison.Ordinal) < text.IndexOf("Mockup: Sign in", StringComparison.Ordinal));
        Assert.True(text.IndexOf("Mockup: Sign in", StringComparison.Ordinal) < text.IndexOf("After the tree.", StringComparison.Ordinal));

        // Backlog 194: the block's place holds the tree drawn.
        var preview = cut.Find("[data-testid=mockup-figure] [data-testid=mockup-preview-frame]");
        Assert.Equal("Home", preview.QuerySelector("[data-mockup-id=home]")!.GetAttribute("aria-label"));
        Assert.Contains("Ticket #3 is open", preview.TextContent);
        Assert.Contains("\"id\": \"home\"", cut.Find("[data-testid=mockup-json]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=mockup-errors]"));

        // The JSON is not markdown: "#3" inside it is neither a link nor a broken reference.
        Assert.DoesNotContain(WikiMarkdownRenderer.BrokenClass, body.InnerHtml);
        Assert.DoesNotContain("/workitems/3", body.InnerHtml);

        // Backlog 196: a design with its one block opens in the mockup editor.
        Assert.Equal($"/wiki/{design.Number}/design", cut.Find("[data-testid=wiki-design-edit]").GetAttribute("href"));
    }

    [Fact]
    public async Task A_design_whose_tree_the_reader_refuses_is_saved_and_shows_every_error_with_its_path()
    {
        var broken = """{"mockup": 1, "screens": [{"type": "window", "id": "home", "colour": "red"}]}""";
        var design = await WikiAsync("Broken", WikiItemType.Design, Design(broken));

        using var ctx = Context($"http://localhost/wiki/{design.Number}");
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, design.Number));

        var expected = Fravashi.Application.Mockups.MockupReader.Read(broken).Errors;
        Assert.NotEmpty(expected);
        var shown = cut.FindAll("[data-testid=mockup-error]");
        Assert.Equal(expected.Count, shown.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Path, shown[i].QuerySelector("code")!.TextContent);
            Assert.Contains(expected[i].Message, shown[i].TextContent);
        }

        // Backlog 194: listed once, in the block's place, instead of a half-drawn preview.
        Assert.Empty(cut.FindAll("[data-testid=mockup-errors]"));
        Assert.Contains("nothing is drawn", cut.Find("[data-testid=mockup-figure] [data-testid=mockup-preview-errors]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=mockup-preview-frame]"));
        Assert.Contains("colour", cut.Find("[data-testid=mockup-json]").TextContent);
    }

    [Theory]
    [InlineData("Only prose, no tree.", "no mockup block")]
    [InlineData("```mockup\n{}\n```\n\n```mockup\n{}\n```\n", "2 mockup blocks")]
    public async Task A_design_with_no_block_or_two_is_saved_shows_why_and_still_shows_what_was_written(string body, string says)
    {
        var design = await WikiAsync("Draft design", WikiItemType.Design, body);

        using var ctx = Context($"http://localhost/wiki/{design.Number}");
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, design.Number));

        Assert.Contains(says, cut.Find("[data-testid=mockup-errors]").TextContent);
        Assert.Equal("body", cut.Find("[data-testid=mockup-error] code").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=mockup-figure]"));
        Assert.Contains(body.Contains("```", StringComparison.Ordinal) ? "{}" : "Only prose", cut.Find("[data-testid=wiki-article-body]").TextContent);

        // Backlog 196: with no one block there is nothing for the mockup editor to open.
        Assert.Empty(cut.FindAll("[data-testid=wiki-design-edit]"));
    }

    [Fact]
    public async Task A_document_of_another_type_with_a_mockup_block_is_just_markdown()
    {
        var note = await WikiAsync("A note", WikiItemType.Draft, Design(Tree));

        using var ctx = Context($"http://localhost/wiki/{note.Number}");
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, note.Number));

        Assert.Empty(cut.FindAll("[data-testid=mockup-figure]"));
        Assert.Empty(cut.FindAll("[data-testid=mockup-errors]"));
    }

    // ---- prototypes (backlog 211) --------------------------------------------------------------

    private static string Prototype(int designNumber, long designVersion, string overlay = "[]") =>
        "A generated page.\n\n```prototype\n"
        + $$"""{"prototype": 1, "design": {"number": {{designNumber}}, "version": {{designVersion}}}, "overlay": {{overlay}}}"""
        + "\n```\n";

    [Fact]
    public async Task A_prototype_shows_its_designs_pinned_version_and_not_changed_since_when_it_has_not_moved_on()
    {
        var design = await WikiAsync("Sign in screen", WikiItemType.Design, Design(Tree));
        var prototype = await WikiAsync("Sign in prototype", WikiItemType.Prototype, Prototype(design.Number, design.Version));

        using var ctx = Context($"http://localhost/wiki/{prototype.Number}");
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, prototype.Number));

        var link = cut.Find("[data-testid=prototype-design-link]");
        Assert.Contains($"@{design.Number}", link.TextContent);
        Assert.Contains("Sign in screen", link.TextContent);
        Assert.Contains($"Pinned at version {design.Version}", cut.Find("[data-testid=prototype-design-version]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=prototype-design-changed]"));
    }

    [Fact]
    public async Task A_prototype_says_the_design_has_changed_since_once_it_has_moved_on()
    {
        var design = await WikiAsync("Sign in screen", WikiItemType.Design, Design(Tree));
        var prototype = await WikiAsync("Sign in prototype", WikiItemType.Prototype, Prototype(design.Number, design.Version));

        var updated = await _wiki.UpdateAsync(design.Id, new WikiItemEdit { Body = Design(Tree) + "\nAn edit.\n" }, design.Version);
        Assert.True(updated.IsValid);

        using var ctx = Context($"http://localhost/wiki/{prototype.Number}");
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, prototype.Number));

        Assert.Contains($"Pinned at version {design.Version}", cut.Find("[data-testid=prototype-design-version]").TextContent);
        Assert.Contains("changed since", cut.Find("[data-testid=prototype-design-changed]").TextContent);
        Assert.Contains(updated.Value.Version.ToString(), cut.Find("[data-testid=prototype-design-changed]").TextContent);
    }

    [Fact]
    public async Task A_prototypes_overlay_is_listed_in_words()
    {
        var design = await WikiAsync("Sign in screen", WikiItemType.Design, Design(Tree));
        var overlay = """[{"op": "addClass", "anchor": {"id": "home"}, "class": "is-ready"}]""";
        var prototype = await WikiAsync(
            "Sign in prototype", WikiItemType.Prototype, Prototype(design.Number, design.Version, overlay));

        using var ctx = Context($"http://localhost/wiki/{prototype.Number}");
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, prototype.Number));

        var items = cut.FindAll("[data-testid=prototype-overlay-item]");
        Assert.Single(items);
        Assert.Contains("is-ready", items[0].TextContent);
        Assert.Contains("home", items[0].TextContent);
    }

    [Fact]
    public async Task A_prototype_whose_document_the_reader_refuses_shows_every_error_with_its_path()
    {
        var broken = """{"prototype": 1, "design": {"number": 999, "version": 1}, "overlay": []}""";
        var prototype = await WikiAsync(
            "Broken prototype", WikiItemType.Prototype, $"A generated page.\n\n```prototype\n{broken}\n```\n");

        using var ctx = Context($"http://localhost/wiki/{prototype.Number}");
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, prototype.Number));

        var shown = cut.FindAll("[data-testid=prototype-error]");
        Assert.Single(shown);
        Assert.Equal("$.design.number", shown[0].QuerySelector("code")!.TextContent);
        Assert.Contains("999", shown[0].TextContent);
        Assert.Empty(cut.FindAll("[data-testid=prototype-section]"));
    }

    // ---- the prototype's page in its sandboxed frame (backlog 213, plan @38) ----------------------

    private const string TwoScreens =
        """{"mockup": 1, "title": "Sign in", "viewports": [{"name": "wide", "width": 1280}, {"name": "narrow", "width": 390}], "screens": [{"type": "window", "id": "home", "text": "Home", "children": [{"type": "button", "id": "next", "text": "Next", "on": {"click": {"go": "details"}}}]}, {"type": "window", "id": "details", "text": "Details", "children": []}]}""";

    private async Task<(WikiItem Design, WikiItem Prototype)> PrototypeOfTwoScreensAsync(string overlay = "[]")
    {
        var design = await WikiAsync("Sign in screen", WikiItemType.Design, Design(TwoScreens));
        var prototype = await WikiAsync(
            "Sign in prototype", WikiItemType.Prototype, Prototype(design.Number, design.Version, overlay));
        return (design, prototype);
    }

    private static string Sha256(string text) =>
        "sha256-" + Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    [Fact]
    public async Task A_prototypes_frame_is_sandboxed_to_scripts_alone_and_carries_the_pages_policy()
    {
        var (_, prototype) = await PrototypeOfTwoScreensAsync();

        using var ctx = Context($"http://localhost/wiki/{prototype.Number}");
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, prototype.Number));

        var frame = cut.Find("iframe[data-testid=prototype-frame]");

        // Scripts only, for the page's own wiring: no allow-same-origin, so the frame's origin is opaque and
        // nothing in it reaches the studio; no top navigation, popups, forms or modals.
        Assert.Equal("allow-scripts", frame.GetAttribute("sandbox"));
        Assert.Equal(
            "default-src 'none'; script-src '" + Fravashi.Application.Prototypes.PrototypeRenderer.ScriptHash
            + "'; style-src 'unsafe-inline'; img-src data:; base-uri 'none'; form-action 'none'",
            frame.GetAttribute("csp"));
        Assert.Equal("no-referrer", frame.GetAttribute("referrerpolicy"));
        Assert.Null(frame.GetAttribute("src"));

        // The srcdoc's policy is the head's first element and is the frame's own, and the hash in it is the
        // hash of the one script the srcdoc carries.
        var page = frame.GetAttribute("srcdoc")!;
        var head = "<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta http-equiv=\"Content-Security-Policy\" content=\"";
        Assert.StartsWith(head, page, StringComparison.Ordinal);
        var policy = System.Net.WebUtility.HtmlDecode(page[head.Length..page.IndexOf('"', head.Length)]);
        Assert.Equal(frame.GetAttribute("csp"), policy);
        var scripts = System.Text.RegularExpressions.Regex.Matches(
            page, "<script>(.*?)</script>", System.Text.RegularExpressions.RegexOptions.Singleline);
        var script = Assert.Single(scripts).Groups[1].Value;
        Assert.Contains($"script-src '{Sha256(script)}'", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", policy.Split(';').Single(d => d.Trim().StartsWith("script-src", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Choosing_a_viewport_resizes_the_frame_to_its_width()
    {
        var (_, prototype) = await PrototypeOfTwoScreensAsync();

        using var ctx = Context($"http://localhost/wiki/{prototype.Number}");
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, prototype.Number));

        Assert.Equal("width: 100%", cut.Find("[data-testid=prototype-frame]").GetAttribute("style"));

        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-viewer-viewport]").Change("narrow"));
        cut.WaitForAssertion(() =>
            Assert.Equal("width: 390px", cut.Find("[data-testid=prototype-frame]").GetAttribute("style")));

        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-viewer-viewport]").Change("wide"));
        cut.WaitForAssertion(() =>
            Assert.Equal("width: 1280px", cut.Find("[data-testid=prototype-frame]").GetAttribute("style")));

        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-viewer-viewport]").Change(""));
        cut.WaitForAssertion(() =>
            Assert.Equal("width: 100%", cut.Find("[data-testid=prototype-frame]").GetAttribute("style")));
    }

    [Fact]
    public async Task Choosing_a_screen_renders_the_page_starting_on_it()
    {
        var (_, prototype) = await PrototypeOfTwoScreensAsync();

        using var ctx = Context($"http://localhost/wiki/{prototype.Number}");
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, prototype.Number));

        const string HomeShown = "<section data-mk-anchor=\"home\" class=\"mk-screen\" aria-label=\"Home\" data-mk-screen=\"screen\">";
        const string DetailsShown = "<section data-mk-anchor=\"details\" class=\"mk-screen\" aria-label=\"Details\" data-mk-screen=\"screen\">";
        var first = cut.Find("[data-testid=prototype-frame]").GetAttribute("srcdoc")!;
        Assert.Contains(HomeShown, first, StringComparison.Ordinal);
        Assert.DoesNotContain(DetailsShown, first, StringComparison.Ordinal);

        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-viewer-screen]").Change("1"));
        cut.WaitForAssertion(() =>
        {
            var page = cut.Find("[data-testid=prototype-frame]").GetAttribute("srcdoc")!;
            Assert.Contains(DetailsShown, page, StringComparison.Ordinal);
            Assert.DoesNotContain(HomeShown, page, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Reload_from_the_design_renders_what_the_prototype_says_now()
    {
        var (_, prototype) = await PrototypeOfTwoScreensAsync();

        using var ctx = Context($"http://localhost/wiki/{prototype.Number}");
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, prototype.Number));
        Assert.DoesNotContain("Go on", cut.Find("[data-testid=prototype-frame]").GetAttribute("srcdoc")!, StringComparison.Ordinal);

        var body = prototype.Body.Replace(
            "\"overlay\": []",
            "\"overlay\": [{\"op\": \"setText\", \"anchor\": {\"id\": \"next\"}, \"text\": \"Go on\"}]",
            StringComparison.Ordinal);
        Assert.True((await _wiki.UpdateAsync(prototype.Id, new WikiItemEdit { Body = body }, prototype.Version)).IsValid);

        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-viewer-reload]").Click());
        cut.WaitForAssertion(() =>
            Assert.Contains("Go on", cut.Find("[data-testid=prototype-frame]").GetAttribute("srcdoc")!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_page_is_rendered_from_the_design_at_its_pinned_version_not_the_newest()
    {
        var (design, prototype) = await PrototypeOfTwoScreensAsync();
        var moved = await _wiki.UpdateAsync(
            design.Id, new WikiItemEdit { Body = Design(TwoScreens.Replace("\"Next\"", "\"Onward\"", StringComparison.Ordinal)) }, design.Version);
        Assert.True(moved.IsValid);

        using var ctx = Context($"http://localhost/wiki/{prototype.Number}");
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, prototype.Number));

        var page = cut.Find("[data-testid=prototype-frame]").GetAttribute("srcdoc")!;
        Assert.Contains(">Next</button>", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Onward", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_design_whose_tree_is_not_valid_at_the_pinned_version_says_so_instead_of_a_frame()
    {
        var design = await WikiAsync("Sign in screen", WikiItemType.Design, Design("""{"mockup": 1, "screens": []}"""));
        var prototype = await WikiAsync("Sign in prototype", WikiItemType.Prototype, Prototype(design.Number, design.Version));

        using var ctx = Context($"http://localhost/wiki/{prototype.Number}");
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, prototype.Number));

        Assert.Contains($"version {design.Version} is not valid", cut.Find("[data-testid=prototype-viewer-problem]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=prototype-frame]"));
    }

    [Fact]
    public async Task Overlay_operations_that_found_no_place_are_listed()
    {
        var (_, prototype) = await PrototypeOfTwoScreensAsync(
            """[{"op": "addClass", "anchor": {"id": "nowhere"}, "class": "lost"}, {"op": "addClass", "anchor": {"id": "next"}, "class": "found"}]""");

        using var ctx = Context($"http://localhost/wiki/{prototype.Number}");
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, prototype.Number));

        var unplaced = Assert.Single(cut.FindAll("[data-testid=prototype-unplaced-item]"));
        Assert.Contains("nowhere", unplaced.TextContent, StringComparison.Ordinal);
        Assert.Contains("lost", unplaced.TextContent, StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll("[data-testid=prototype-unexpressed-none]"));
    }

    [Fact]
    public async Task An_overlay_fragment_carrying_an_onerror_handler_is_refused_by_name_and_no_frame_renders_it()
    {
        // The guard refuses <img src=x/onerror=...> (backlog 213 closed the slash bypass), so the page shows the
        // reader's error with its path. A fragment the guard misses lands, and the policy stands against it
        // (PrototypePageTests.A_fragment_the_guard_misses_lands_and_the_policy_is_what_stands_against_it).
        var (_, prototype) = await PrototypeOfTwoScreensAsync(
            """[{"op": "insertHtml", "anchor": {"id": "next"}, "placement": "after", "html": "<img src=x/onerror=alert(1)>"}]""");

        using var ctx = Context($"http://localhost/wiki/{prototype.Number}");
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, prototype.Number));

        var error = Assert.Single(cut.FindAll("[data-testid=prototype-error]"));
        Assert.Equal("$.overlay[0].html", error.QuerySelector("code")!.TextContent);
        Assert.Contains("onerror", error.TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=prototype-frame]"));
    }

    // ---- direct edits on the page (backlog 214, plan @39) ------------------------------------------

    private const string Attach = "fravashiPrototypeEdit.attach";
    private const string Detach = "fravashiPrototypeEdit.detach";

    /// <summary>
    /// The wiki page on a prototype, with the edit listener's two calls set up and everything else strict, so a JS
    /// call this slice did not mean to make fails the test.
    /// </summary>
    private (BunitContext Ctx, IRenderedComponent<WikiLibraryPage> Cut) PrototypePage(WikiItem prototype)
    {
        var ctx = Context($"http://localhost/wiki/{prototype.Number}");
        ctx.JSInterop.Setup<int>(Attach, _ => true).SetResult(41);
        ctx.JSInterop.SetupVoid(Detach, _ => true).SetVoidResult();
        return (ctx, ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, prototype.Number)));
    }

    private static Fravashi.Web.Components.Prototypes.PrototypeViewer Viewer(IRenderedComponent<WikiLibraryPage> cut) =>
        cut.FindComponent<Fravashi.Web.Components.Prototypes.PrototypeViewer>().Instance;

    /// <summary>What the page's script would post after a click, as prototypeEdit.js hands it over.</summary>
    private static Task PageSelects(IRenderedComponent<WikiLibraryPage> cut, string anchor) =>
        cut.InvokeAsync(() => Viewer(cut).SelectFromPage(anchor));

    private static string Frame(IRenderedComponent<WikiLibraryPage> cut) =>
        cut.Find("[data-testid=prototype-frame]").GetAttribute("srcdoc")!;

    private static Task Style(IRenderedComponent<WikiLibraryPage> cut, string property, string value) =>
        cut.InvokeAsync(() => cut.Find($"[data-testid=prototype-edit-style][data-property={property}]").Change(value));

    private async Task<Fravashi.Application.Prototypes.PrototypeDocument> StoredAsync(WikiItem prototype)
    {
        var stored = (await _wiki.GetAsync(prototype.Id))!;
        var read = await Fravashi.Application.Prototypes.PrototypeBody.ReadAsync(stored.Body, _wiki);
        Assert.True(read.IsValid, string.Join("; ", read.Errors));
        return read.Document!;
    }

    [Fact]
    public async Task Selecting_editing_text_and_a_style_undoing_redoing_and_saving_each_work()
    {
        var (_, prototype) = await PrototypeOfTwoScreensAsync();
        var (ctx, cut) = PrototypePage(prototype);
        using var _ = ctx;
        var viewing = Frame(cut);

        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-edit]").Click());

        // Edit mode: an edit render, carrying the same one script and the same policy, and one listener attached.
        cut.WaitForAssertion(() => Assert.Single(ctx.JSInterop.Invocations[Attach]));
        var editing = Frame(cut);
        Assert.StartsWith("<!DOCTYPE html>\n<html lang=\"en\" data-mk-edit=\"edit\">", editing, StringComparison.Ordinal);
        var script = Assert.Single(System.Text.RegularExpressions.Regex.Matches(
            editing, "<script>(.*?)</script>", System.Text.RegularExpressions.RegexOptions.Singleline)).Groups[1].Value;
        Assert.Equal(Fravashi.Application.Prototypes.PrototypeRenderer.Script, script);
        Assert.Contains(script, viewing, StringComparison.Ordinal);
        Assert.Equal(Fravashi.Application.Prototypes.PrototypeRenderer.ContentSecurityPolicy,
            cut.Find("[data-testid=prototype-frame]").GetAttribute("csp"));
        Assert.Contains($"script-src '{Sha256(script)}'", editing, StringComparison.Ordinal);
        Assert.Equal("allow-scripts", cut.Find("[data-testid=prototype-frame]").GetAttribute("sandbox"));
        Assert.NotEmpty(cut.FindAll("[data-testid=prototype-select-hint]"));

        // Selecting: the page posts the anchor it hit.
        await PageSelects(cut, "next");
        cut.WaitForAssertion(() => Assert.Equal("button next", cut.Find("[data-testid=prototype-selected]").TextContent));
        Assert.Contains("[data-mk-anchor=\"next\"] { outline: 2px solid", Frame(cut), StringComparison.Ordinal);
        Assert.Equal("Next", cut.Find("[data-testid=prototype-edit-text]").GetAttribute("value"));

        // Editing the text and a style, each one operation.
        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-edit-text]").Change("Go on"));
        cut.WaitForAssertion(() => Assert.Contains(">Go on</button>", Frame(cut), StringComparison.Ordinal));
        await Style(cut, "color", "#c62828");
        cut.WaitForAssertion(() => Assert.Contains("style=\"color: #c62828\">Go on</button>", Frame(cut), StringComparison.Ordinal));
        Assert.Contains("Unsaved edits", cut.Find("[data-testid=prototype-edit-status]").TextContent, StringComparison.Ordinal);

        // Undo takes the style back, redo puts it back.
        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-undo]").Click());
        cut.WaitForAssertion(() => Assert.DoesNotContain("color: #c62828", Frame(cut), StringComparison.Ordinal));
        Assert.Contains(">Go on</button>", Frame(cut), StringComparison.Ordinal);
        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-redo]").Click());
        cut.WaitForAssertion(() => Assert.Contains("style=\"color: #c62828\">Go on</button>", Frame(cut), StringComparison.Ordinal));

        // Saving writes the two operations against the version opened.
        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-save]").Click());
        cut.WaitForAssertion(() => Assert.Contains(
            $"Saved as version {prototype.Version + 1}", cut.Find("[data-testid=prototype-edit-status]").TextContent, StringComparison.Ordinal));
        var stored = await StoredAsync(prototype);
        Assert.Equal(2, stored.Overlay.Count);
        var text = Assert.IsType<Fravashi.Application.Prototypes.PrototypeSetTextOperation>(stored.Overlay[0]);
        Assert.Equal(("next", "Go on"), (text.Anchor.Id, text.Text));
        var css = Assert.IsType<Fravashi.Application.Prototypes.PrototypeSetCssOperation>(stored.Overlay[1]);
        Assert.Equal("#c62828", css.Properties["color"]);
        Assert.StartsWith("A generated page.\n\n```prototype\n", (await _wiki.GetAsync(prototype.Id))!.Body, StringComparison.Ordinal);
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=prototype-overlay-item]").Count));
    }

    [Fact]
    public async Task A_style_the_panel_refuses_is_said_in_words_and_adds_nothing()
    {
        var (_, prototype) = await PrototypeOfTwoScreensAsync();
        var (ctx, cut) = PrototypePage(prototype);
        using var _ = ctx;
        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-edit]").Click());
        await PageSelects(cut, "next");
        var before = Frame(cut);

        await Style(cut, "color", "red; background: url(https://example.com/x)");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=prototype-edit-refusal]")));
        Assert.Equal(before, Frame(cut));
        Assert.True(cut.Find("[data-testid=prototype-undo]").HasAttribute("disabled"));
    }

    [Fact]
    public async Task A_page_rule_is_added_as_one_operation()
    {
        var (_, prototype) = await PrototypeOfTwoScreensAsync();
        var (ctx, cut) = PrototypePage(prototype);
        using var _ = ctx;
        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-edit]").Click());

        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-page-rule]").Change(".mk-button { border-radius: 1rem; }"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-page-rule-add]").Click());
        cut.WaitForAssertion(() => Assert.Contains(".mk-button { border-radius: 1rem; }", Frame(cut), StringComparison.Ordinal));

        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-save]").Click());
        cut.WaitForAssertion(() => Assert.Contains("Saved as version", cut.Find("[data-testid=prototype-edit-status]").TextContent, StringComparison.Ordinal));
        var rule = Assert.IsType<Fravashi.Application.Prototypes.PrototypePageCssOperation>(Assert.Single((await StoredAsync(prototype)).Overlay));
        Assert.Equal(".mk-button { border-radius: 1rem; }", rule.Rule);
    }

    [Fact]
    public async Task A_save_with_no_edits_writes_nothing()
    {
        var (_, prototype) = await PrototypeOfTwoScreensAsync();
        var (ctx, cut) = PrototypePage(prototype);
        using var _ = ctx;
        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-edit]").Click());

        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-save]").Click());

        cut.WaitForAssertion(() => Assert.Contains("Nothing to save", cut.Find("[data-testid=prototype-edit-status]").TextContent, StringComparison.Ordinal));
        Assert.Equal(prototype.Version, (await _wiki.GetAsync(prototype.Id))!.Version);
    }

    [Fact]
    public async Task A_save_on_a_stale_version_is_refused_with_a_choice_to_reload()
    {
        var (_, prototype) = await PrototypeOfTwoScreensAsync();
        var (ctx, cut) = PrototypePage(prototype);
        using var _ = ctx;
        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-edit]").Click());
        await PageSelects(cut, "next");
        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-edit-text]").Change("Mine"));

        // Somebody else saves the prototype in between.
        var theirs = prototype.Body.Replace(
            "\"overlay\": []", "\"overlay\": [{\"op\": \"setText\", \"anchor\": {\"id\": \"next\"}, \"text\": \"Theirs\"}]", StringComparison.Ordinal);
        var moved = await _wiki.UpdateAsync(prototype.Id, new WikiItemEdit { Body = theirs }, prototype.Version);
        Assert.True(moved.IsValid);

        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-save]").Click());

        var stale = cut.WaitForElement("[data-testid=prototype-stale]");
        Assert.Contains($"you opened version {prototype.Version}, it is now version {moved.Value.Version}", stale.TextContent, StringComparison.Ordinal);
        Assert.Equal(theirs, (await _wiki.GetAsync(prototype.Id))!.Body);
        Assert.Contains(">Mine</button>", Frame(cut), StringComparison.Ordinal);

        // Reloading drops my edit and opens what is stored now, still in Edit mode.
        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-reload-discard]").Click());
        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("[data-testid=prototype-stale]"));
            Assert.Contains(">Theirs</button>", Frame(cut), StringComparison.Ordinal);
        });
        Assert.True(cut.Find("[data-testid=prototype-undo]").HasAttribute("disabled"));

        // And a save now is written against the newer version.
        await PageSelects(cut, "next");
        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-edit-text]").Change("Agreed"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-save]").Click());
        cut.WaitForAssertion(() => Assert.Contains(
            $"Saved as version {moved.Value.Version + 1}", cut.Find("[data-testid=prototype-edit-status]").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_anchor_the_render_did_not_emit_selects_nothing()
    {
        var (_, prototype) = await PrototypeOfTwoScreensAsync();
        var (ctx, cut) = PrototypePage(prototype);
        using var _ = ctx;
        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-edit]").Click());
        var before = Frame(cut);

        foreach (var invented in new[] { "nowhere", "", "/screens/9", "next\" onclick=\"x", "<script>" })
        {
            await PageSelects(cut, invented);
        }

        Assert.Empty(cut.FindAll("[data-testid=prototype-selected]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=prototype-select-hint]"));
        Assert.Equal(before, Frame(cut));
    }

    // ---- a node's parts (backlog 238, plan @52) ----------------------------------------------------

    [Fact]
    public async Task A_click_on_a_screens_title_selects_the_title_and_its_edits_reach_only_the_heading()
    {
        var (_, prototype) = await PrototypeOfTwoScreensAsync();
        var (ctx, cut) = PrototypePage(prototype);
        using var _ = ctx;
        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-edit]").Click());

        // What the page's script posts for a click on the heading, as prototypeEdit.js hands it over.
        await cut.InvokeAsync(() => Viewer(cut).SelectFromPage("home", "title", null));

        cut.WaitForAssertion(() => Assert.Equal("title of window home", cut.Find("[data-testid=prototype-selected]").TextContent));
        Assert.Contains("[data-mk-part-of=\"home\"][data-mk-part=\"title\"] { outline: 2px solid", Frame(cut), StringComparison.Ordinal);
        Assert.DoesNotContain("[data-mk-anchor=\"home\"] { outline", Frame(cut), StringComparison.Ordinal);
        Assert.Equal("Home", cut.Find("[data-testid=prototype-edit-text]").GetAttribute("value"));

        await Style(cut, "font-size", "1.6rem");
        cut.WaitForAssertion(() => Assert.Contains(
            "<h1 data-mk-part-of=\"home\" data-mk-part=\"title\" style=\"font-size: 1.6rem\">Home</h1>", Frame(cut), StringComparison.Ordinal));
        Assert.Contains("<section data-mk-anchor=\"home\" class=\"mk-screen\" aria-label=\"Home\" data-mk-screen=\"screen\">", Frame(cut), StringComparison.Ordinal);
        Assert.Equal("1.6rem", cut.Find("[data-testid=prototype-edit-style][data-property=font-size]").GetAttribute("value"));

        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-edit-text]").Change("Welcome"));
        cut.WaitForAssertion(() => Assert.Contains(">Welcome</h1>", Frame(cut), StringComparison.Ordinal));

        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-save]").Click());
        cut.WaitForAssertion(() => Assert.Contains("Saved as version", cut.Find("[data-testid=prototype-edit-status]").TextContent, StringComparison.Ordinal));
        var stored = await StoredAsync(prototype);
        Assert.All(stored.Overlay, op => Assert.Equal(
            new Fravashi.Application.Prototypes.PrototypeAnchor("home", null, Fravashi.Application.Verification.DesignPart.Title),
            Fravashi.Application.Prototypes.PrototypeRenderer.AnchorOf(op)));
        Assert.Equal(2, stored.Overlay.Count);
    }

    [Fact]
    public async Task A_part_the_render_did_not_draw_selects_nothing()
    {
        var (_, prototype) = await PrototypeOfTwoScreensAsync();
        var (ctx, cut) = PrototypePage(prototype);
        using var _ = ctx;
        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-edit]").Click());
        var before = Frame(cut);

        foreach (var (anchor, part, column) in new (string, string?, string?)[]
        {
            ("next", "title", null),
            ("home", "caption", null),
            ("home", "heading", null),
            ("home", "node", null),
            ("home", "title", "email"),
            ("home", "columnHeader", "name"),
            ("nowhere", "title", null),
        })
        {
            await cut.InvokeAsync(() => Viewer(cut).SelectFromPage(anchor, part, column));
        }

        Assert.Empty(cut.FindAll("[data-testid=prototype-selected]"));
        Assert.Equal(before, Frame(cut));
    }

    [Fact]
    public async Task Nothing_is_heard_from_the_page_while_it_is_not_being_edited()
    {
        var (_, prototype) = await PrototypeOfTwoScreensAsync();
        var (ctx, cut) = PrototypePage(prototype);
        using var _ = ctx;
        var before = Frame(cut);

        await PageSelects(cut, "next");

        Assert.Equal(before, Frame(cut));
        Assert.Empty(cut.FindAll("[data-testid=prototype-panel]"));
        Assert.Empty(ctx.JSInterop.Invocations[Attach]);
        Assert.DoesNotContain("data-mk-edit=\"edit\"", before, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_listener_is_attached_once_on_Edit_and_removed_on_Done_and_when_the_page_goes()
    {
        var (_, prototype) = await PrototypeOfTwoScreensAsync();
        var (ctx, cut) = PrototypePage(prototype);
        using var _ = ctx;

        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-edit]").Click());
        await PageSelects(cut, "next");
        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-edit-text]").Change("Go on"));
        cut.WaitForAssertion(() => Assert.Contains(">Go on</button>", Frame(cut), StringComparison.Ordinal));
        Assert.Single(ctx.JSInterop.Invocations[Attach]);

        // Done with an unsaved edit asks first; leaving without saving ends Edit and removes the listener.
        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-edit-done]").Click());
        Assert.NotNull(cut.Find("[data-testid=prototype-leave]"));
        Assert.Empty(ctx.JSInterop.Invocations[Detach]);
        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-leave-discard]").Click());
        cut.WaitForAssertion(() => Assert.Single(ctx.JSInterop.Invocations[Detach]));
        Assert.Equal(41, ctx.JSInterop.Invocations[Detach][0].Arguments[0]);
        Assert.DoesNotContain("Go on", Frame(cut), StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=prototype-panel]"));

        // Edit again, then the page goes away: the listener goes with it.
        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-edit]").Click());
        cut.WaitForAssertion(() => Assert.Equal(2, ctx.JSInterop.Invocations[Attach].Count));
        await ctx.DisposeComponentsAsync();
        Assert.Equal(2, ctx.JSInterop.Invocations[Detach].Count);
    }

    // ---- regenerating and settling what no longer places (backlog 216, plan @41) ----------------------

    /// <summary>TwoScreens with the anchored button taken out of the first screen.</summary>
    private static readonly string TwoScreensWithoutNext = TwoScreens.Replace(
        """[{"type": "button", "id": "next", "text": "Next", "on": {"click": {"go": "details"}}}]""", "[]", StringComparison.Ordinal);

    private const string LostClass = """[{"op": "addClass", "anchor": {"id": "nowhere"}, "class": "lost"}]""";

    private static bool Carries(string page, string anchor, string cls) =>
        System.Text.RegularExpressions.Regex.IsMatch(page, $"data-mk-anchor=\"{anchor}\" class=\"[^\"]*\\b{cls}\\b");

    [Fact]
    public async Task Regenerate_is_offered_for_a_newer_design_moves_the_pin_and_lists_what_no_longer_places_still_stored()
    {
        Assert.DoesNotContain("\"id\": \"next\"", TwoScreensWithoutNext, StringComparison.Ordinal);
        var (design, prototype) = await PrototypeOfTwoScreensAsync(
            """[{"op": "setText", "anchor": {"id": "next"}, "text": "Go on"}, {"op": "addClass", "anchor": {"id": "home"}, "class": "kept"}]""");

        // Not offered while the design has not moved on.
        var (first, firstCut) = PrototypePage(prototype);
        using (first)
        {
            Assert.Empty(firstCut.FindAll("[data-testid=prototype-regenerate]"));
        }

        var moved = await _wiki.UpdateAsync(design.Id, new WikiItemEdit { Body = Design(TwoScreensWithoutNext) }, design.Version);
        Assert.True(moved.IsValid);

        var (ctx, cut) = PrototypePage(prototype);
        using var _ = ctx;
        Assert.Contains(">Go on</button>", Frame(cut), StringComparison.Ordinal);
        var regenerate = cut.Find("[data-testid=prototype-regenerate]");
        Assert.Contains($"version {moved.Value.Version}", regenerate.TextContent, StringComparison.Ordinal);

        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-regenerate]").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("1 direct edit no longer finds its place", cut.Find("[data-testid=prototype-regenerate-status]").TextContent, StringComparison.Ordinal);
            Assert.Contains($"Pinned at version {moved.Value.Version}", cut.Find("[data-testid=prototype-design-version]").TextContent, StringComparison.Ordinal);
        });
        Assert.Empty(cut.FindAll("[data-testid=prototype-regenerate]"));
        Assert.Empty(cut.FindAll("[data-testid=prototype-design-changed]"));
        Assert.DoesNotContain("Go on", Frame(cut), StringComparison.Ordinal);
        Assert.True(Carries(Frame(cut), "home", "kept"), "the operation whose node the design kept is still applied");
        var unplaced = Assert.Single(cut.FindAll("[data-testid=prototype-unplaced-item]"));
        Assert.Contains("Go on", unplaced.TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=prototype-panel]"));

        // Still stored, the pin moved, nothing dropped.
        var stored = await StoredAsync(prototype);
        Assert.Equal(moved.Value.Version, stored.Design.Version);
        Assert.Equal(2, stored.Overlay.Count);
        Assert.Equal("Go on", Assert.IsType<Fravashi.Application.Prototypes.PrototypeSetTextOperation>(stored.Overlay[0]).Text);
        Assert.Equal(prototype.Version + 1, (await _wiki.GetAsync(prototype.Id))!.Version);
    }

    [Fact]
    public async Task Re_anchoring_moves_the_operation_to_the_element_picked_in_the_page_and_is_undoable_before_save()
    {
        var (_, prototype) = await PrototypeOfTwoScreensAsync(LostClass);
        var (ctx, cut) = PrototypePage(prototype);
        using var _ = ctx;
        Assert.Single(cut.FindAll("[data-testid=prototype-unplaced-item]"));

        // Re-anchor starts Edit (the pick is a click in the edit render) and names what it is moving.
        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-unplaced-reanchor]").Click());
        cut.WaitForAssertion(() => Assert.Single(ctx.JSInterop.Invocations[Attach]));
        Assert.Contains("lost", cut.Find("[data-testid=prototype-reanchor-pick]").TextContent, StringComparison.Ordinal);

        // An anchor the render did not emit moves nothing and keeps the pick open.
        await PageSelects(cut, "nowhere");
        Assert.NotEmpty(cut.FindAll("[data-testid=prototype-reanchor-pick]"));

        // The page's one selection message picks the element.
        await PageSelects(cut, "next");
        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("[data-testid=prototype-reanchor-pick]"));
            Assert.Empty(cut.FindAll("[data-testid=prototype-unplaced-item]"));
            Assert.True(Carries(Frame(cut), "next", "lost"));
        });
        Assert.Equal("button next", cut.Find("[data-testid=prototype-selected]").TextContent);
        Assert.Contains("Unsaved edits", cut.Find("[data-testid=prototype-edit-status]").TextContent, StringComparison.Ordinal);

        // Undo puts it back where it was, unplaced; redo moves it again.
        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-undo]").Click());
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=prototype-unplaced-item]")));
        Assert.False(Carries(Frame(cut), "next", "lost"));
        Assert.Equal(prototype.Version, (await _wiki.GetAsync(prototype.Id))!.Version);
        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-redo]").Click());
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=prototype-unplaced-item]")));

        // Save stores it on its new anchor.
        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-save]").Click());
        cut.WaitForAssertion(() => Assert.Contains("Saved as version", cut.Find("[data-testid=prototype-edit-status]").TextContent, StringComparison.Ordinal));
        var stored = Assert.IsType<Fravashi.Application.Prototypes.PrototypeAddClassOperation>(Assert.Single((await StoredAsync(prototype)).Overlay));
        Assert.Equal(("next", "lost"), (stored.Anchor.Id, stored.Class));
    }

    [Fact]
    public async Task A_cancelled_re_anchor_moves_nothing_and_the_next_click_only_selects()
    {
        var (_, prototype) = await PrototypeOfTwoScreensAsync(LostClass);
        var (ctx, cut) = PrototypePage(prototype);
        using var _ = ctx;

        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-unplaced-reanchor]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-reanchor-cancel]").Click());
        await PageSelects(cut, "next");

        cut.WaitForAssertion(() => Assert.Equal("button next", cut.Find("[data-testid=prototype-selected]").TextContent));
        Assert.Single(cut.FindAll("[data-testid=prototype-unplaced-item]"));
        Assert.True(cut.Find("[data-testid=prototype-undo]").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Dropping_removes_the_operation_and_is_undoable_before_save()
    {
        var (_, prototype) = await PrototypeOfTwoScreensAsync(
            """[{"op": "addClass", "anchor": {"id": "nowhere"}, "class": "lost"}, {"op": "addClass", "anchor": {"id": "next"}, "class": "found"}]""");
        var (ctx, cut) = PrototypePage(prototype);
        using var _ = ctx;

        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-unplaced-drop]").Click());

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=prototype-unplaced-item]")));
        Assert.NotEmpty(cut.FindAll("[data-testid=prototype-panel]"));
        Assert.Equal(prototype.Version, (await _wiki.GetAsync(prototype.Id))!.Version);

        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-undo]").Click());
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=prototype-unplaced-item]")));
        Assert.Contains("Version", cut.Find("[data-testid=prototype-edit-status]").TextContent, StringComparison.Ordinal);

        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-unplaced-drop]").Click());
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=prototype-unplaced-item]")));
        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-save]").Click());
        cut.WaitForAssertion(() => Assert.Contains("Saved as version", cut.Find("[data-testid=prototype-edit-status]").TextContent, StringComparison.Ordinal));

        var kept = Assert.IsType<Fravashi.Application.Prototypes.PrototypeAddClassOperation>(Assert.Single((await StoredAsync(prototype)).Overlay));
        Assert.Equal("found", kept.Class);
        Assert.True(Carries(Frame(cut), "next", "found"));
    }

    [Fact]
    public async Task A_save_after_a_regenerate_landed_elsewhere_is_refused_as_stale()
    {
        var (design, prototype) = await PrototypeOfTwoScreensAsync(
            """[{"op": "setText", "anchor": {"id": "next"}, "text": "Go on"}, {"op": "addClass", "anchor": {"id": "nowhere"}, "class": "lost"}]""");
        var moved = await _wiki.UpdateAsync(design.Id, new WikiItemEdit { Body = Design(TwoScreensWithoutNext) }, design.Version);
        Assert.True(moved.IsValid);
        var (ctx, cut) = PrototypePage(prototype);
        using var _ = ctx;

        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-unplaced-drop]").Click());
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=prototype-unplaced-item]")));
        Assert.Empty(cut.FindAll("[data-testid=prototype-regenerate]"));

        // Somebody regenerates the same prototype (an agent's prototype_render, another window).
        var regenerated = await Fravashi.Application.Prototypes.PrototypeRegenerate.RunAsync(
            _wiki, prototype, await StoredAsync(prototype), moved.Value);
        Assert.Equal(Fravashi.Application.Prototypes.PrototypeRegenerateOutcome.Regenerated, regenerated.Outcome);
        var theirs = (await _wiki.GetAsync(prototype.Id))!.Body;

        await cut.InvokeAsync(() => cut.Find("[data-testid=prototype-save]").Click());

        cut.WaitForElement("[data-testid=prototype-stale]");
        Assert.Equal(theirs, (await _wiki.GetAsync(prototype.Id))!.Body);
        Assert.Equal(2, (await StoredAsync(prototype)).Overlay.Count);
    }

    // ---- publishing (backlog 83) --------------------------------------------------------------

    [Fact]
    public async Task A_summary_is_written_when_somebody_asks_and_never_by_opening_or_saving_the_page()
    {
        // Backlog 40 (decided 2026-09-21: "a button to do so"). Every summary is a paid agent call.
        var doc = await WikiAsync("Rollout", body: "Staging first, then production.");

        using var ctx = Context($"http://localhost/wiki/{doc.Number}");
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, doc.Number));

        Assert.Contains("No summary yet", cut.Find("[data-testid=wiki-summary-state]").TextContent);
        Assert.Equal(0, _agent.Calls);

        await cut.InvokeAsync(() => cut.Find("[data-testid=wiki-summarise]").Click());

        cut.WaitForAssertion(() =>
            Assert.Equal("How the rollout is staged.", cut.Find("[data-testid=wiki-summary-text]").TextContent));
        Assert.Contains("for this version", cut.Find("[data-testid=wiki-summary-state]").TextContent);
        Assert.Equal(1, _agent.Calls);
    }

    [Fact]
    public async Task With_no_agent_that_can_write_one_the_button_is_off_and_says_why()
    {
        _agent.Unavailable = "No agent runtime is signed in.";
        var doc = await WikiAsync("Rollout", body: "Text.");

        using var ctx = Context($"http://localhost/wiki/{doc.Number}");
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, doc.Number));

        Assert.True(cut.Find("[data-testid=wiki-summarise]").HasAttribute("disabled"));
        Assert.Contains("No agent runtime", cut.Find("[data-testid=wiki-summary-unavailable]").TextContent);
    }

    [Fact]
    public async Task A_refused_summary_is_said_on_the_page()
    {
        _agent.Refusal = "The agent did not answer in time. Nothing was changed.";
        var doc = await WikiAsync("Rollout", body: "Text.");

        using var ctx = Context($"http://localhost/wiki/{doc.Number}");
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, doc.Number));
        await cut.InvokeAsync(() => cut.Find("[data-testid=wiki-summarise]").Click());

        cut.WaitForAssertion(() =>
            Assert.Contains("did not answer in time", cut.Find("[data-testid=wiki-summary-refusal]").TextContent));
        Assert.Empty(cut.FindAll("[data-testid=wiki-summary-text]"));
    }

    [Fact]
    public async Task With_no_wiki_connected_a_document_says_nothing_about_publishing()
    {
        _site.IsConfigured = false;
        var doc = await WikiAsync("Quiet", body: "Text.");

        using var ctx = Context($"http://localhost/wiki/{doc.Number}");
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, doc.Number));

        Assert.Empty(cut.FindAll("[data-testid=wiki-publication]"));
    }

    [Fact]
    public async Task Publishing_is_a_button_and_the_page_then_says_where_the_document_stands()
    {
        var doc = await WikiAsync("Rollout", body: "Text.");

        using var ctx = Context($"http://localhost/wiki/{doc.Number}");
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, doc.Number));

        Assert.Contains("Not published", cut.Find("[data-testid=wiki-publication-state]").TextContent);
        Assert.Empty(_site.Pages);

        await cut.InvokeAsync(() => cut.Find("[data-testid=wiki-publish]").Click());

        cut.WaitForAssertion(() =>
            Assert.Contains("holds this version", cut.Find("[data-testid=wiki-publication-state]").TextContent));
        Assert.Single(_site.Pages);
        Assert.Empty(cut.FindAll("[data-testid=wiki-publish]"));
    }

    [Fact]
    public async Task A_page_edited_on_the_site_offers_the_two_ways_out_and_no_publish_button()
    {
        var doc = await WikiAsync("Rollout", body: "Mine.");

        using var ctx = Context($"http://localhost/wiki/{doc.Number}");
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, doc.Number));
        await cut.InvokeAsync(() => cut.Find("[data-testid=wiki-publish]").Click());
        cut.WaitForAssertion(() => Assert.Single(_site.Pages));

        var id = _site.Pages.Keys.Single();
        _site.Pages[id] = _site.Pages[id] with { Body = "<p>Theirs.</p>", Version = 2 };
        await cut.InvokeAsync(() => cut.Find("[data-testid=wiki-publication-check]").Click());

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=wiki-take-site]")));
        Assert.NotEmpty(cut.FindAll("[data-testid=wiki-overwrite-site]"));
        Assert.Empty(cut.FindAll("[data-testid=wiki-publish]"));

        await cut.InvokeAsync(() => cut.Find("[data-testid=wiki-take-site]").Click());

        cut.WaitForAssertion(() =>
            Assert.Contains("Theirs.", cut.Find("[data-testid=wiki-article-body]").TextContent));
        Assert.NotEmpty(cut.FindAll("[data-testid=wiki-restore-replaced]"));
    }

    [Fact]
    public async Task The_state_and_its_detail_read_as_two_sentences_not_one_run_together()
    {
        // Seen in the installed v0.9.31 on 2026-09-23: "nothing is recreated unasked.Confluence holds page ...",
        // because Blazor trims the space at the edge of an element's content.
        var doc = await WikiAsync("Rollout", body: "Mine.");
        using var ctx = Context($"http://localhost/wiki/{doc.Number}");
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, doc.Number));
        await cut.InvokeAsync(() => cut.Find("[data-testid=wiki-publish]").Click());
        cut.WaitForAssertion(() => Assert.Single(_site.Pages));

        _site.Pages.Clear();
        await cut.InvokeAsync(() => cut.Find("[data-testid=wiki-publication-check]").Click());

        cut.WaitForAssertion(() =>
        {
            var state = cut.Find("[data-testid=wiki-publication-state]").TextContent;
            Assert.Contains("no such page", state);
            Assert.DoesNotMatch(@"\.[A-Za-z]", state);
        });
    }

    [Fact]
    public async Task A_site_that_cannot_be_reached_is_named_with_since_when_and_the_line_goes_once_it_answers()
    {
        // Backlog 232: the panel says since when the site has not answered, beside the state, and stops once it does.
        var doc = await WikiAsync("Rollout", body: "Text.");
        var outages = new Fravashi.Application.Network.ProviderOutages(new Noon());
        using var ctx = Context($"http://localhost/wiki/{doc.Number}", services =>
        {
            services.AddSingleton(new WikiPublisher(
                _wiki, new SqliteWikiPublicationStore(_factory), _site, new FixedTarget(), TimeProvider.System, outages: outages));
            // Backlog 246: the person's zone and day the line is said in.
            services.AddSingleton(PersonZones.Utc());
            services.AddSingleton<TimeProvider>(new Noon());
        });
        var cut = ctx.Render<WikiLibraryPage>(p => p.Add(c => c.Number, doc.Number));
        Assert.Empty(cut.FindAll("[data-testid=wiki-publication-outage]"));

        _site.Down = new Fravashi.Application.Network.NetworkFailure(
            Fravashi.Application.Network.NetworkFailureKind.NameNotResolved, "test");
        await cut.InvokeAsync(() => cut.Find("[data-testid=wiki-publish]").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(
                "Confluence unreachable since 12:00 (name not resolved). This document is served from Fravashi, and nothing is sent until the site answers.",
                cut.Find("[data-testid=wiki-publication-outage]").TextContent.Trim());
            Assert.Contains("Not sent", cut.Find("[data-testid=wiki-publication-state]").TextContent);
        });

        _site.Down = null;
        await cut.InvokeAsync(() => cut.Find("[data-testid=wiki-publish]").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("[data-testid=wiki-publication-outage]"));
            Assert.Contains("holds this version", cut.Find("[data-testid=wiki-publication-state]").TextContent);
        });
        Assert.Single(_site.Pages);
    }

    private sealed class Noon : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class FixedTarget : IWikiPublishTarget
    {
        public string? Space => "TEAM";
    }

    /// <summary>A site that always answers, for the surface tests. The misbehaving one lives with the publisher's own tests.</summary>
    private sealed class PageSite : IWikiPageGateway
    {
        public Dictionary<string, SitePage> Pages { get; } = [];

        public string Provider => "Confluence";

        public bool IsConfigured { get; set; } = true;

        /// <summary>Backlog 232: while set, a creation never leaves, for this reason.</summary>
        public Fravashi.Application.Network.NetworkFailure? Down { get; set; }

        public Task<ProviderResult<SitePage>> CreateAsync(string space, string title, string body, string? parentPageId = null, CancellationToken ct = default)
        {
            if (Down is { } failure)
            {
                return Task.FromResult(ProviderResult<SitePage>.Unreachable("Confluence could not be reached.") with { Network = failure });
            }

            var id = (Pages.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            Pages[id] = new SitePage(id, title, 1, body, null);
            return Task.FromResult(ProviderResult<SitePage>.Ok(Pages[id]));
        }

        public Task<ProviderResult<SitePage>> UpdateAsync(string pageId, string title, string body, int newVersion, CancellationToken ct = default)
        {
            Pages[pageId] = new SitePage(pageId, title, newVersion, body, null);
            return Task.FromResult(ProviderResult<SitePage>.Ok(Pages[pageId]));
        }

        public Task<ProviderResult<SitePage>> GetAsync(string pageId, CancellationToken ct = default) =>
            Task.FromResult(Pages.TryGetValue(pageId, out var page)
                ? ProviderResult<SitePage>.Ok(page)
                : ProviderResult<SitePage>.Gone("no such page"));

        public Task<ProviderResult<SitePage?>> FindByTitleAsync(string space, string title, CancellationToken ct = default) =>
            Task.FromResult(ProviderResult<SitePage?>.Ok(Pages.Values.FirstOrDefault(p => p.Title == title)));
    }

    private sealed class ScriptedAgent : IWikiSummaryWriter
    {
        public int Calls { get; private set; }

        public string? Unavailable { get; set; }

        public string? Refusal { get; set; }

        public Task<string?> UnavailableBecauseAsync(CancellationToken ct = default) => Task.FromResult(Unavailable);

        public Task<WikiSummaryDraft> WriteAsync(WikiItem item, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Refusal is { } why
                ? WikiSummaryDraft.Refused(why)
                : new WikiSummaryDraft("How the rollout is staged.", "the agent", null));
        }
    }
}
