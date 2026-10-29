using Bunit;
using Fravashi.Application.Autopilot;
using Fravashi.Application.Mirroring;
using Fravashi.Application.Persistence;
using Fravashi.Application.Projects;
using Fravashi.Application.Releases;
using Fravashi.Application.Wiki;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Projects;
using Fravashi.Infrastructure.Bridge;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Rendering;
using Fravashi.Infrastructure.Wiki;
using Fravashi.Web.Components.Dashboard;
using Fravashi.Web.Components.Mockups;
using Fravashi.Web.Components.Pages;
using Fravashi.Web.Components.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Item #373, slice 1 (<c>docs/plans/first-open-setup.md</c> section 6): NO SHARED BUCKET. A window on a folder that is
/// not set up shows the one calm "This folder is not set up yet" state on every surface that would show a project's
/// data, with a Set up button, and reads NO project's data at all: every project port here is a substitute that must
/// not have been called. Before this, Stomme's first open showed the default project's 24 items, its wiki, and a
/// live Dispatch button.
/// </summary>
public sealed class FolderNotSetUpTests : IDisposable
{
    private const string Folder = "ws-core";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fravashi-not-set-up-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnectionFactory _factory;

    private readonly List<string> _calls = [];
    private readonly ITrackerResolver _trackers;
    private readonly IWikiLibraries _wikis;
    private readonly IWikiDocuments _documents;
    private readonly IMirrorStore _mirror;
    private readonly IOutboxStore _outbox;
    private readonly IReleaseStores _releases;
    private readonly IAutopilotRunStore _runs;

    public FolderNotSetUpTests()
    {
        Directory.CreateDirectory(_directory);
        _factory = new SqliteConnectionFactory(new SqliteStoreOptions(Path.Combine(_directory, "test.db"), null, Pooling: false));
        SqliteSchema.EnsureCreated(_factory);
        _trackers = CallRecorder.For<ITrackerResolver>(_calls);
        _wikis = CallRecorder.For<IWikiLibraries>(_calls);
        _documents = CallRecorder.For<IWikiDocuments>(_calls);
        _mirror = CallRecorder.For<IMirrorStore>(_calls);
        _outbox = CallRecorder.For<IOutboxStore>(_calls);
        _releases = CallRecorder.For<IReleaseStores>(_calls);
        _runs = CallRecorder.For<IAutopilotRunStore>(_calls);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>The window of a folder opened for the first time: recorded, named Stomme, in no project.</summary>
    private BunitContext Window(string url)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [BridgeWorkspaceContext.UrlKey] = "http://127.0.0.1:9",
            [BridgeWorkspaceContext.TokenKey] = "t",
            [BridgeWorkspaceContext.ActiveIdKey] = Folder,
        }).Build();
        var workspace = new BridgeWorkspaceContext(configuration);
        var workspaces = new SqliteWorkspaceStore(_factory);
        workspaces.Touch(Folder, "Stomme", @"C:\Repos\Stomme");

        ctx.Services.AddSingleton(workspace);
        ctx.Services.AddSingleton<IWorkspaceStore>(workspaces);
        ctx.Services.AddSingleton<ICurrentProject>(new WorkspaceCurrentProject(workspace, workspaces));
        ctx.Services.AddSingleton<IProjectStore>(new SqliteProjectStore(_factory, TimeProvider.System));
        ctx.Services.AddSingleton(_trackers);
        ctx.Services.AddSingleton(_wikis);
        ctx.Services.AddSingleton(_documents);
        ctx.Services.AddSingleton(_mirror);
        ctx.Services.AddSingleton(_outbox);
        ctx.Services.AddSingleton(_releases);
        ctx.Services.AddSingleton(_runs);
        ctx.Services.AddSingleton<IWorkItemChanges>(new WorkItemChanges());
        ctx.Services.AddSingleton(TimeProvider.System);
        ctx.Services.AddSingleton(new WikiMarkdownRenderer());
        ctx.Services.AddSingleton(new WikiReferenceResolver(_trackers, _wikis));
        ctx.Services.AddSingleton(new DesignApproval(_trackers, _wikis));
        ctx.Services.AddSingleton(new GraphRepair(_mirror, _wikis));
        ctx.Services.AddSingleton(TestProviderSync.Over(_factory));
        ctx.Services.AddSingleton(new WikiPublisher(
            _documents,
            CallRecorder.For<IWikiPublicationStore>(_calls),
            CallRecorder.For<IWikiPageGateway>(_calls),
            CallRecorder.For<IWikiPublishTarget>(_calls),
            TimeProvider.System));
        ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().NavigateTo(url);
        return ctx;
    }

    /// <summary>Not one call reached any port that holds a project's data.</summary>
    private void NothingOfAnyProjectWasRead() => Assert.Empty(_calls);

    private static void ShowsTheState<T>(IRenderedComponent<T> cut, string surface)
        where T : Microsoft.AspNetCore.Components.IComponent
    {
        var state = cut.Find("[data-testid=folder-not-set-up]");
        Assert.Equal(surface, state.GetAttribute("data-surface"));
        Assert.Equal("This folder is not set up yet", cut.Find(".folder-setup-title").TextContent.Trim());
        Assert.Contains("Stomme is not part of a project yet", cut.Find("[data-testid=folder-not-set-up-lede]").TextContent);
        var button = cut.Find("[data-testid=folder-set-up]");
        Assert.Equal("Set up Stomme", button.TextContent.Trim());
        Assert.StartsWith(FolderNotSetUp.SetupPath, button.GetAttribute("href"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_board_shows_the_state_names_no_project_and_reads_nothing()
    {
        using var ctx = Window("/workitems");
        var cut = ctx.Render<WorkItemsPage>();

        ShowsTheState(cut, FolderNotSetUp.Surfaces.Board);
        Assert.Equal("Work items", cut.Find(".studio-page-bar-title").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid=new-work-item]"));
        Assert.Empty(cut.FindAll("[data-testid=board-sidebar]"));
        NothingOfAnyProjectWasRead();
    }

    [Fact]
    public void The_same_board_on_a_folder_in_a_project_does_read_it_so_the_check_above_is_not_empty()
    {
        // The guard against a vacuous pass: with the folder bound, the very same wiring is asked for the project's board.
        using var ctx = Window("/workitems");
        var projects = ctx.Services.GetRequiredService<IProjectStore>();
        projects.BindWorkspace(Folder, projects.Create("Stomme").Id);

        Assert.ThrowsAny<Exception>(() => ctx.Render<WorkItemsPage>());

        Assert.Contains(_calls, c => c.StartsWith(nameof(ITrackerResolver), StringComparison.Ordinal));
    }

    [Fact]
    public void The_wiki_shows_the_state_and_reads_nothing()
    {
        using var ctx = Window("/wiki");
        var cut = ctx.Render<WikiLibraryPage>();

        ShowsTheState(cut, FolderNotSetUp.Surfaces.Wiki);
        Assert.Equal("Wiki", cut.Find(".studio-page-bar-title").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid=new-wiki-item]"));
        NothingOfAnyProjectWasRead();
    }

    [Fact]
    public void Releases_show_the_state_with_no_New_release_and_read_nothing()
    {
        using var ctx = Window("/releases");
        var cut = ctx.Render<ReleasesPage>();

        ShowsTheState(cut, FolderNotSetUp.Surfaces.Releases);
        Assert.Empty(cut.FindAll("[data-testid=releases-new]"));
        NothingOfAnyProjectWasRead();
    }

    [Fact]
    public void Provider_sync_shows_the_state_and_reads_nothing()
    {
        using var ctx = Window("/sync");
        var cut = ctx.Render<ProviderSyncPage>();

        ShowsTheState(cut, FolderNotSetUp.Surfaces.ProviderSync);
        Assert.Empty(cut.FindAll("[data-testid=sync-body]"));
        NothingOfAnyProjectWasRead();
    }

    [Fact]
    public void The_design_editor_shows_the_state_and_reads_nothing()
    {
        using var ctx = Window("/wiki/3/design");
        var cut = ctx.Render<DesignEditorPage>(p => p.Add(c => c.Number, 3));

        ShowsTheState(cut, FolderNotSetUp.Surfaces.Design);
        NothingOfAnyProjectWasRead();
    }

    [Theory]
    [InlineData(HomeLayout.Conversation)]
    [InlineData(HomeLayout.Autopilot)]
    [InlineData(HomeLayout.Progress)]
    [InlineData(HomeLayout.RecentDispatches)]
    [InlineData(HomeLayout.Wiki)]
    public void A_project_card_on_Home_draws_nothing_at_all(string card)
    {
        // Nothing drawn means no component made, so no Autopilot card and no Dispatch button exist to press.
        using var ctx = Window("/");
        var cut = ctx.Render<HomeCardContent>(p => p.Add(c => c.Id, card));

        Assert.Equal(string.Empty, cut.Markup.Trim());
        Assert.Contains(card, HomeCardContent.ProjectCards);
        NothingOfAnyProjectWasRead();
    }

    [Fact]
    public void The_autopilot_card_itself_draws_nothing_and_reads_no_run()
    {
        using var ctx = Window("/");
        var cut = ctx.Render<AutopilotCard>();

        Assert.Empty(cut.FindAll("[data-testid=autopilot-card]"));
        Assert.Empty(cut.FindAll("button"));
        NothingOfAnyProjectWasRead();
    }

    [Fact]
    public void Home_leads_with_the_state_and_keeps_the_cards_that_read_every_folder()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddNoUpdates();
        ctx.Services.AddSingleton<Fravashi.Application.Notifications.INotificationHistory>(
            new Fravashi.Infrastructure.Notifications.InMemoryNotificationHistory());
        ctx.Services.AddUnboundHomeSurface();
        ctx.Services.AddSingleton<ICurrentProject>(new NoProject());
        ctx.Services.AddSingleton(_runs);
        ctx.Services.AddSingleton(_trackers);
        ctx.Services.AddSingleton(_wikis);

        var cut = ctx.Render<Home>();

        Assert.Equal(FolderNotSetUp.Surfaces.Home, cut.Find("[data-testid=folder-not-set-up]").GetAttribute("data-surface"));
        Assert.Empty(cut.FindAll("[data-testid=home-prompt]"));
        Assert.Empty(cut.FindAll("[data-testid=autopilot-card]"));
        Assert.Empty(cut.FindAll("[data-testid=progress-card]"));
        Assert.Empty(cut.FindAll("[data-testid=recent-dispatches]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=home-card-live-sessions] .dash-panel"));
        NothingOfAnyProjectWasRead();
    }

    [Fact]
    public void Every_surface_has_its_own_line_and_the_inline_size_is_one_line_with_the_button()
    {
        using var ctx = Window("/");
        var cut = ctx.Render<FolderNotSetUp>(p => p
            .Add(c => c.Inline, true)
            .Add(c => c.Surface, FolderNotSetUp.Surfaces.Sessions));

        Assert.Contains("Stomme is not set up yet.", cut.Find(".folder-setup-inline-text").TextContent);
        Assert.Contains("earlier conversations are still here to read", cut.Markup);
        Assert.Equal("Set up Stomme", cut.Find("[data-testid=folder-set-up]").TextContent.Trim());
        Assert.Equal("note", cut.Find("[data-testid=folder-not-set-up]").GetAttribute("role"));
        foreach (var surface in new[]
        {
            FolderNotSetUp.Surfaces.Board, FolderNotSetUp.Surfaces.Wiki, FolderNotSetUp.Surfaces.Sessions,
            FolderNotSetUp.Surfaces.Autopilot, FolderNotSetUp.Surfaces.Releases, FolderNotSetUp.Surfaces.ProviderSync,
            FolderNotSetUp.Surfaces.Import, FolderNotSetUp.Surfaces.Design, FolderNotSetUp.Surfaces.Settings,
        })
        {
            Assert.False(string.IsNullOrWhiteSpace(FolderNotSetUp.SurfaceLine(surface)), surface);
        }
    }

    [Fact]
    public void The_page_size_is_a_labelled_region_whose_heading_is_an_h2()
    {
        using var ctx = Window("/");
        var cut = ctx.Render<FolderNotSetUp>(p => p.Add(c => c.Surface, FolderNotSetUp.Surfaces.Board));

        var section = cut.Find("section[data-testid=folder-not-set-up]");
        var heading = cut.Find("h2.folder-setup-title");
        Assert.Equal(heading.GetAttribute("id"), section.GetAttribute("aria-labelledby"));
    }

    /// <summary>
    /// A stand-in for a project port that records every call made on it (and answers nothing useful), so a test can say
    /// that NOTHING reached it. Built on DispatchProxy, so no mocking library is needed here.
    /// </summary>
    public class CallRecorder : System.Reflection.DispatchProxy
    {
        private List<string> _calls = [];
        private string _name = string.Empty;

        public static T For<T>(List<string> calls)
            where T : class
        {
            var proxy = Create<T, CallRecorder>();
            var recorder = (CallRecorder)(object)proxy;
            recorder._calls = calls;
            recorder._name = typeof(T).Name;
            return proxy;
        }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            // An event subscription is how a page listens, not a read of anything.
            if (targetMethod is { Name: var name } && (name.StartsWith("add_", StringComparison.Ordinal) || name.StartsWith("remove_", StringComparison.Ordinal)))
            {
                return null;
            }

            _calls.Add($"{_name}.{targetMethod?.Name}");
            throw new InvalidOperationException($"{_name}.{targetMethod?.Name} was called for a folder that is not set up.");
        }
    }

    /// <summary>A window whose folder is in no project.</summary>
    private sealed class NoProject : ICurrentProject
    {
        public ProjectId? Id => null;
    }
}
