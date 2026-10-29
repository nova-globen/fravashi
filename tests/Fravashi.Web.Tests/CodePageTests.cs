using Bunit;
using Bunit.TestDoubles;
using Fravashi.Application.Notifications;
using Fravashi.Application.Repository;
using Fravashi.Domain.Repository;
using Fravashi.Infrastructure.Notifications;
using Fravashi.Web.Components.Code;
using Fravashi.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

public sealed partial class CodePageTests : IDisposable
{
    // The agent panel's governance strip reads what a conversation is bound to from the graph (backlog 18),
    // so the panel needs the same real, empty stores production composes rather than a stand-in for them.
    private readonly string _graphDirectory = Path.Combine(
        Path.GetTempPath(), "fravashi-codepage-tests", Guid.NewGuid().ToString("N"));
    private Fravashi.Infrastructure.Persistence.SqliteConnectionFactory? _graph;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_graphDirectory))
            {
                Directory.Delete(_graphDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp folder.
        }
    }

    private void AddGraphServices(IServiceCollection services)
    {
        if (_graph is null)
        {
            Directory.CreateDirectory(_graphDirectory);
            _graph = new Fravashi.Infrastructure.Persistence.SqliteConnectionFactory(
                new Fravashi.Infrastructure.Persistence.SqliteStoreOptions(
                    Path.Combine(_graphDirectory, "graph.db"), null, Pooling: false));
            Fravashi.Infrastructure.Persistence.SqliteSchema.EnsureCreated(_graph);
        }

        var trackers = new Fravashi.Infrastructure.WorkItems.LocalTrackerResolver(_graph, TimeProvider.System);
        var work = new Fravashi.Infrastructure.Sessions.SqliteSessionWorkItems(_graph, TimeProvider.System);
        var plans = new Fravashi.Infrastructure.Sessions.SqliteSessionPlans(_graph, TimeProvider.System);
        var wiki = new Fravashi.Infrastructure.Wiki.SqliteWikiLibrary(_graph, TimeProvider.System, Fravashi.Domain.Projects.ProjectId.Default);
        services.AddSingleton<Fravashi.Application.WorkItems.ITrackerResolver>(trackers);
        // Backlog 121: a page follows its window's project; these tests have one project.
        services.AddSingleton<Fravashi.Application.Projects.ICurrentProject>(Fravashi.Application.Projects.SingleProject.Instance);
        services.AddSingleton<Fravashi.Application.Projects.IProjectStore>(new OneProjectStore());
        services.AddSingleton<Fravashi.Application.Sessions.ISessionWorkItems>(work);
        services.AddSingleton<Fravashi.Application.Sessions.ISessionPlans>(plans);
        // Backlog 121: a page asks for its project's wiki, and what holds an id asks for the by-id view.
        services.AddSingleton<Fravashi.Application.Wiki.IWikiLibraries>(new OneWiki(wiki));
        services.AddSingleton<Fravashi.Application.Wiki.IWikiDocuments>(wiki);
        services.AddSingleton<Fravashi.Application.Sessions.ISessionGovernance>(
            new Fravashi.Infrastructure.Sessions.SessionGovernance(
                work, plans, trackers, wiki,
                new Fravashi.Infrastructure.Sessions.SqliteSessionChangedFiles(_graph, TimeProvider.System)));
    }

    private sealed class FakeReader : IRepositoryReader
    {
        public bool IsConfigured { get; init; } = true;

        /// <summary>Per-path disk timestamps; bump one to simulate an external change.</summary>
        public Dictionary<string, DateTimeOffset> Stamps { get; } = [];

        /// <summary>Per-path content overrides; unset paths read as "content of {path}".</summary>
        public Dictionary<string, string> Contents { get; } = [];

        /// <summary>Paths that read as missing (externally deleted).</summary>
        public HashSet<string> Missing { get; } = [];

        /// <summary>Paths whose read reports the too-large truncation placeholder.</summary>
        public HashSet<string> Truncated { get; } = [];

        /// <summary>Files that appear at the root after the page loaded (made on the host, backlog 257).</summary>
        public List<string> AddedRootFiles { get; } = [];

        /// <summary>When set, every tree read fails with it (models a bridge that refuses).</summary>
        public Exception? TreeFailure { get; init; }

        /// <summary>
        /// When set, the root listing waits for it (backlog 265). The real bridge answers asynchronously, so
        /// the page's first render, and the session restore it starts, run BEFORE OnParametersSet; a
        /// synchronous fake hides that order.
        /// </summary>
        public TaskCompletionSource? RootGate { get; set; }

        /// <summary>Per-path gates a file read waits for, so a test decides which of two opens finishes last.</summary>
        public Dictionary<string, TaskCompletionSource> ReadGates { get; } = [];

        public string RootPath => IsConfigured ? "/repos/acme" : string.Empty;

        public async Task<RepositoryNode> GetTreeAsync(string relativePath = "", CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(relativePath) && RootGate is { } gate)
            {
                await gate.Task;
            }

            return await ListAsync(relativePath);
        }

        private Task<RepositoryNode> ListAsync(string relativePath)
        {
            if (TreeFailure is not null)
            {
                throw TreeFailure;
            }

            if (string.IsNullOrEmpty(relativePath))
            {
                return Task.FromResult(RepositoryNode.Directory("acme", "", [
                    RepositoryNode.Directory("src", "src", []),
                    RepositoryNode.File("README.md", "README.md"),
                    RepositoryNode.File("logo.png", "logo.png"),
                    RepositoryNode.File("tool.exe", "tool.exe"),
                    .. AddedRootFiles.Select(name => RepositoryNode.File(name, name)),
                ]));
            }

            return Task.FromResult(RepositoryNode.Directory("src", "src", [
                RepositoryNode.File("Program.cs", "src/Program.cs"),
            ]));
        }

        public async Task<RepositoryFile?> ReadFileAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            if (ReadGates.TryGetValue(relativePath, out var gate))
            {
                await gate.Task;
            }

            return await ReadNowAsync(relativePath);
        }

        private Task<RepositoryFile?> ReadNowAsync(string relativePath)
        {
            if (Missing.Contains(relativePath))
            {
                return Task.FromResult<RepositoryFile?>(null);
            }

            var stamp = Stamps.TryGetValue(relativePath, out var s) ? s : DateTimeOffset.UnixEpoch;
            if (Truncated.Contains(relativePath))
            {
                return Task.FromResult<RepositoryFile?>(
                    new RepositoryFile(relativePath, "(file too large to preview)", "", stamp, IsTruncated: true));
            }

            return Task.FromResult<RepositoryFile?>(relativePath == "tool.exe"
                ? new RepositoryFile(relativePath, "MZ\0\0binary", "", stamp)
                : new RepositoryFile(relativePath, Contents.GetValueOrDefault(relativePath, "content of " + relativePath), "", stamp));
        }

        public Task<byte[]?> ReadBytesAsync(string relativePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]?>([1, 2, 3, 4]);
    }

    private sealed class FakeWriter : IRepositoryWriter
    {
        public List<(string Path, string Content, DateTimeOffset? Expected)> Saves { get; } = [];

        public RepositorySaveResult NextSaveResult { get; set; } = new(true, LastWriteUtc: DateTimeOffset.UtcNow);

        public List<(string Directory, string Name)> CreatedFiles { get; } = [];

        public Task<RepositoryWriteResult> CreateFileAsync(string directoryPath, string name, CancellationToken cancellationToken = default)
        {
            CreatedFiles.Add((directoryPath, name));
            return Task.FromResult(RepositoryWriteResult.Ok);
        }

        public Task<RepositoryWriteResult> CreateDirectoryAsync(string directoryPath, string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(RepositoryWriteResult.Ok);

        public Task<RepositoryWriteResult> CopyAsync(string sourcePath, string targetDirectoryPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(RepositoryWriteResult.Ok);

        public Task<RepositoryWriteResult> MoveAsync(string sourcePath, string targetDirectoryPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(RepositoryWriteResult.Ok);

        public List<(string Path, string NewName)> Renames { get; } = [];

        public Task<RepositoryWriteResult> RenameAsync(string path, string newName, CancellationToken cancellationToken = default)
        {
            Renames.Add((path, newName));
            return Task.FromResult(RepositoryWriteResult.Ok);
        }

        public async Task<RepositorySaveResult> SaveFileAsync(string relativePath, Stream content, DateTimeOffset? expectedLastWriteUtc, CancellationToken cancellationToken = default)
        {
            using var reader = new StreamReader(content);
            Saves.Add((relativePath, await reader.ReadToEndAsync(cancellationToken), expectedLastWriteUtc));
            return NextSaveResult;
        }
    }

    private sealed class FakeTrash : IRepositoryTrash
    {
        public List<IReadOnlyList<string>> Deleted { get; } = [];

        public List<string> Restored { get; } = [];

        public TrashResult NextResult { get; set; } = new(true, ZipFileName: "2026-07-26_1200_[main].zip");

        public Task<TrashResult> DeleteToTrashAsync(IReadOnlyList<string> relativePaths, CancellationToken cancellationToken = default)
        {
            Deleted.Add(relativePaths);
            return Task.FromResult(NextResult);
        }

        public Task<TrashResult> RestoreAsync(string zipFileName, CancellationToken cancellationToken = default)
        {
            Restored.Add(zipFileName);
            return Task.FromResult(new TrashResult(true, ZipFileName: zipFileName));
        }
    }

    private sealed class FakeNotifier : IRepositoryChangeNotifier
    {
        private readonly List<Action<RepositoryChange>> _handlers = [];

        public IDisposable Subscribe(Action<RepositoryChange> handler)
        {
            _handlers.Add(handler);
            return new Subscription(this, handler);
        }

        public void Raise(RepositoryChange change)
        {
            foreach (var handler in _handlers.ToList())
            {
                handler(change);
            }
        }

        private sealed class Subscription(FakeNotifier owner, Action<RepositoryChange> handler) : IDisposable
        {
            public void Dispose() => owner._handlers.Remove(handler);
        }
    }

    private sealed class StubSearchService : Fravashi.Application.Search.IRepositorySearchService
    {
        public Task<Fravashi.Application.Search.SearchResultSet> SearchAsync(
            Fravashi.Application.Search.SearchQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(Fravashi.Application.Search.SearchResultSet.Empty("test"));

        public Task<Fravashi.Application.Search.ReplaceResult> ReplaceAsync(
            Fravashi.Application.Search.SearchQuery query, string replacement, IReadOnlyList<string> relativePaths,
            (int Line, int Column)? onlyAt = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Fravashi.Application.Search.ReplaceResult(0, 0));

        public Task<Fravashi.Application.Search.ReplaceResult> RestoreAsync(
            IReadOnlyList<Fravashi.Application.Search.ReplacedFile> files, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Fravashi.Application.Search.ReplaceResult(0, 0));
    }

    /// <summary>A client buffer for the save pipeline (stands in for Monaco's model bytes).</summary>
    private sealed class FakeContentStream(byte[] bytes) : Microsoft.JSInterop.IJSStreamReference
    {
        public FakeContentStream(string content)
            : this(System.Text.Encoding.UTF8.GetBytes(content))
        {
        }

        private readonly byte[] _bytes = bytes;

        public long Length => _bytes.Length;

        public ValueTask<Stream> OpenReadStreamAsync(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Stream>(new MemoryStream(_bytes));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private const string IconManifest = """
        { "iconDefinitions": { "file": { "iconPath": "./../icons/file.svg" } },
          "fileExtensions": {}, "fileNames": {}, "folderNames": {}, "folderNamesExpanded": {},
          "file": "file", "folder": "folder", "folderExpanded": "folder-open" }
        """;

    private BunitContext PageContext(bool configured = true, Exception? treeFailure = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        // The page takes a clock now, so relative time can be tested and a demo install fixed (backlog 71).
        ctx.Services.AddSingleton(TimeProvider.System);
        ctx.Services.AddSingleton(new FakeReader { IsConfigured = configured, TreeFailure = treeFailure });
        ctx.Services.AddSingleton(new FakeWriter());
        ctx.Services.AddSingleton(new FakeTrash());
        ctx.Services.AddSingleton(new FakeNotifier());
        ctx.Services.AddSingleton<IRepositoryReader>(sp => sp.GetRequiredService<FakeReader>());
        ctx.Services.AddSingleton<IRepositoryWriter>(sp => sp.GetRequiredService<FakeWriter>());
        ctx.Services.AddSingleton<IRepositoryTrash>(sp => sp.GetRequiredService<FakeTrash>());
        ctx.Services.AddSingleton<IRepositoryChangeNotifier>(sp => sp.GetRequiredService<FakeNotifier>());
        ctx.Services.AddSingleton(new Fravashi.Infrastructure.Rendering.MarkdownPreviewRenderer());
        ctx.Services.AddSingleton(FileIconCatalog.FromJson(IconManifest));
        ctx.Services.AddScoped<CodeWorkspace>();
        // Backlog 264: the window's connection; a test models a reconnection by calling ConnectionUp itself.
        ctx.Services.AddScoped<CircuitConnection>();
        // Backlog 23: the agent panel's open state lives outside the page that draws it.
        ctx.Services.AddScoped<AgentPanelState>();
        ctx.Services.AddScoped<ScratchpadState>();
        ctx.Services.AddSingleton<INotificationHistory>(new InMemoryNotificationHistory());
        ctx.Services.AddSingleton<Fravashi.Application.Search.IRepositorySearchService>(new StubSearchService());
        // Line blame stays quiet in these tests: git is unavailable, so the chip never renders.
        ctx.Services.AddSingleton<Fravashi.Application.Git.IGitService>(new UnavailableGitService());
        // Plan 2b-ii: workspace selection/recents plumbing; unconfigured = local mode, no bridge IO.
        ctx.Services.AddScoped<Fravashi.Infrastructure.Bridge.WorkspaceSelection>();
        var bridgeConfig = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var bridgeContext = new Fravashi.Infrastructure.Bridge.BridgeWorkspaceContext(bridgeConfig);
        ctx.Services.AddSingleton(bridgeContext);
        ctx.Services.AddSingleton(new Fravashi.Infrastructure.Bridge.BridgeClient(new HttpClient(), bridgeContext));
        ctx.Services.AddSingleton<Fravashi.Application.Persistence.IWorkspaceStore>(new FakeWorkspaceStore());
        // Plan 2d: the agent-copy warmer the page pings on init; a recording no-op here.
        ctx.Services.AddSingleton(new FakeWorkspaceAgentSync());
        ctx.Services.AddSingleton<Fravashi.Application.AgentSync.IWorkspaceAgentSync>(
            sp => sp.GetRequiredService<FakeWorkspaceAgentSync>());
        // Plan 2e Task 7: the agent panel beside the editor needs the studio's session registry.
        ctx.Services.AddSingleton(new RecordingSessionRunner());
        ctx.Services.AddSingleton<Fravashi.Application.Sessions.IAgentSessionRunner>(
            sp => sp.GetRequiredService<RecordingSessionRunner>());
        ctx.Services.AddSingleton<Fravashi.Application.Sessions.ISessionStore>(new ForgetfulSessionStore());
        ctx.Services.AddSingleton<Fravashi.Application.Sessions.IAgentBrowser>(new FakeAgentBrowser());
        ctx.Services.AddSingleton<Fravashi.Application.Sessions.IAgentApprovalBroker>(
            new Fravashi.Infrastructure.Sessions.AgentApprovalBroker(TimeSpan.FromSeconds(5)));
        ctx.Services.AddSingleton(sp => new Fravashi.Infrastructure.Sessions.LiveSessionRegistry(
            sp.GetRequiredService<Fravashi.Application.Sessions.IAgentSessionRunner>(),
            sp.GetRequiredService<Fravashi.Application.Sessions.ISessionStore>(),
            sp.GetRequiredService<Fravashi.Application.Sessions.IAgentApprovalBroker>()));
        // Backlog 10: the pane's Node banner; silent here because the copy path is never a real folder.
        ctx.Services.AddSingleton(new Fravashi.Infrastructure.AgentRuntimes.WorkspaceNodeAdvisor(
            new Fravashi.Infrastructure.AgentRuntimes.NodePathProvisioner(bridgeConfig), new FakeNodeCommandRunner()));
        ctx.Services.AddSingleton(new Fravashi.Infrastructure.AgentRuntimes.NodeProvisionCoordinator(new FakeNodeProvisioner()));
        AddGraphServices(ctx.Services);
        return ctx;
    }

    private sealed class FakeWorkspaceAgentSync : Fravashi.Application.AgentSync.IWorkspaceAgentSync
    {
        public List<string> Started { get; } = [];

        /// <summary>The contained copy's path; null models a dev host with no workspaces volume.</summary>
        public string? CopyPath { get; set; }

        public void EnsureStarted(string workspaceId) => Started.Add(workspaceId);
        public string? CopyPathFor(string workspaceId) => CopyPath;
        public object? StatusFor(string workspaceId) => null;
    }

    /// <summary>A session runner that only records what it was asked to run.</summary>
    private sealed class RecordingSessionRunner : Fravashi.Application.Sessions.IAgentSessionRunner
    {
        private readonly System.Threading.Channels.Channel<Fravashi.Domain.AgentSessions.AgentEvent> _events =
            System.Threading.Channels.Channel.CreateUnbounded<Fravashi.Domain.AgentSessions.AgentEvent>();

        public Fravashi.Application.Sessions.AgentSessionRequest? LastRequest { get; private set; }

        /// <summary>Work item #366: an event from the agent, as the CLI's stream would deliver it.</summary>
        public void Emit(Fravashi.Domain.AgentSessions.AgentEvent agentEvent) => _events.Writer.TryWrite(agentEvent);

        public IAsyncEnumerable<Fravashi.Domain.AgentSessions.AgentEvent> RunAsync(
            Fravashi.Application.Sessions.AgentSessionRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return _events.Reader.ReadAllAsync(cancellationToken);
        }

        public Task<bool> SendAsync(
            Fravashi.Domain.AgentSessions.AgentSessionId sessionId,
            string text,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public bool Stop(Fravashi.Domain.AgentSessions.AgentSessionId sessionId) => true;

        public bool ChangeModel(Fravashi.Domain.AgentSessions.AgentSessionId sessionId, string? model) => true;
    }

    private sealed class ForgetfulSessionStore : Fravashi.Application.Sessions.ISessionStore
    {
        public void Save(Fravashi.Application.Sessions.SessionRecord record)
        {
        }

        public Fravashi.Application.Sessions.SessionRecord? Find(Fravashi.Domain.AgentSessions.AgentSessionId id) => null;

        public IReadOnlyList<Fravashi.Application.Sessions.SessionRecord> ForWorkspace(string workspaceId, int max = 50) => [];

        public IReadOnlyList<Fravashi.Application.Sessions.SessionRecord> Recent(int max = 10) => [];

        public void Append(
            Fravashi.Domain.AgentSessions.AgentSessionId id,
            IEnumerable<Fravashi.Domain.AgentSessions.AgentEvent> events)
        {
        }

        public IReadOnlyList<Fravashi.Domain.AgentSessions.AgentEvent> Transcript(
            Fravashi.Domain.AgentSessions.AgentSessionId id) => [];

        public void Delete(Fravashi.Domain.AgentSessions.AgentSessionId id)
        {
        }

        public Fravashi.Domain.AgentSessions.SessionUsageTotals ReadUsageTotals(
            DateTimeOffset dayStartUtc, DateTimeOffset monthStartUtc) =>
            Fravashi.Domain.AgentSessions.SessionUsageTotals.Empty;

        // Backlog 36: the Code page carries no insight card, so these answer emptily like the rest.
        public IReadOnlyList<Fravashi.Domain.Insights.SessionDay> ReadSessionDays(
            DateOnly fromDayUtc, DateOnly toDayUtc) => [];

        public IReadOnlyList<Fravashi.Domain.Insights.DayCount> ReadTurnDays(
            DateOnly fromDayUtc, DateOnly toDayUtc) => [];

        public Fravashi.Domain.Insights.DurationStats ReadSessionDurations(DateTimeOffset fromUtc) =>
            Fravashi.Domain.Insights.DurationStats.None;
    }

    private sealed class FakeWorkspaceStore : Fravashi.Application.Persistence.IWorkspaceStore
    {
        public IReadOnlyList<Fravashi.Application.Persistence.WorkspaceRecord> Recent(int max = 10) => [];
        public Fravashi.Application.Persistence.WorkspaceRecord? Get(string id) => null;
        public void Touch(string id, string name, string rootPath)
        {
        }

        public void Remove(string id)
        {
        }
    }

    [Fact]
    public void Renders_the_three_sidebar_sections_and_no_page_bar()
    {
        // The Code page deliberately omits the page bar so the editor gets the full height.
        using var ctx = PageContext();

        var cut = ctx.Render<CodePage>();

        Assert.Empty(cut.FindAll(".studio-page-bar"));
        Assert.NotEmpty(cut.FindAll("[data-testid=code-open-editors]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=code-search]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=code-tree]"));
    }

    [Fact]
    public void Without_the_page_bar_the_page_still_has_exactly_one_level_one_heading()
    {
        // Backlog 291: the page bar's title is every other page's h1, so this page draws its own.
        using var ctx = PageContext();

        var cut = ctx.Render<CodePage>();

        var heading = Assert.Single(cut.FindAll("h1, [role=heading][aria-level=\"1\"]"));
        Assert.Equal("Code", heading.TextContent.Trim());
        // Not an h1: FocusOnNavigate (Selector "h1") would focus it on arrival and take focus from the editor.
        Assert.Empty(cut.FindAll("h1"));
    }

    [Fact]
    public void Open_editors_and_search_start_collapsed_and_the_tree_section_carries_the_repo_name()
    {
        using var ctx = PageContext();

        var cut = ctx.Render<CodePage>();

        Assert.Equal("false",
            cut.Find("[data-testid=code-open-editors] .page-sidebar-section-header").GetAttribute("aria-expanded"));
        Assert.Equal("false",
            cut.Find("[data-testid=code-search] .page-sidebar-section-header").GetAttribute("aria-expanded"));
        var tree = cut.Find("[data-testid=code-tree] .page-sidebar-section-header");
        Assert.Equal("true", tree.GetAttribute("aria-expanded"));
        Assert.Contains("acme", tree.TextContent);
    }

    [Fact]
    public void Tree_bridge_404_shows_an_honest_message_not_the_http_status()
    {
        // Backlog 14: the host bridge does not know this workspace. The user used to see the raw
        // "Response status code does not indicate success: 404 (Not Found)".
        using var ctx = PageContext(treeFailure: new HttpRequestException(
            "Response status code does not indicate success: 404 (Not Found).",
            inner: null,
            System.Net.HttpStatusCode.NotFound));

        var cut = ctx.Render<CodePage>();

        var error = cut.Find("[data-testid=code-tree-error]").TextContent;
        Assert.DoesNotContain("404", error);
        Assert.DoesNotContain("status code", error);
        Assert.Contains("Open with Fravashi", error);
    }

    [Fact]
    public void Unconfigured_repository_shows_the_honest_hint_instead_of_a_tree()
    {
        using var ctx = PageContext(configured: false);

        var cut = ctx.Render<CodePage>();

        Assert.NotEmpty(cut.FindAll("[data-testid=code-tree-unconfigured]"));
        Assert.Contains("No repository is configured", cut.Markup);
        Assert.Empty(cut.FindAll(".code-file"));
    }

    [Fact]
    public void Selecting_a_file_in_the_tree_navigates_to_its_route()
    {
        using var ctx = PageContext();
        var cut = ctx.Render<CodePage>();
        var nav = ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();

        cut.Find(".code-file").Click();

        Assert.EndsWith("/code/README.md", nav.Uri);
    }

    [Fact]
    public void A_file_route_opens_a_tab_an_open_editor_entry_and_breadcrumbs()
    {
        using var ctx = PageContext();

        var cut = ctx.Render<CodePage>(p => p.Add(c => c.FilePath, "src/Program.cs"));

        Assert.Single(cut.FindAll(".code-tab"));
        Assert.Contains("Program.cs", cut.Find(".code-tab").TextContent);
        Assert.NotEmpty(cut.FindAll("[data-testid=code-breadcrumbs]"));
        Assert.Empty(cut.FindAll("[data-testid=code-editor-empty]"));
    }

    [Fact]
    public void An_image_route_renders_the_image_preview_not_the_editor()
    {
        using var ctx = PageContext();

        var cut = ctx.Render<CodePage>(p => p.Add(c => c.FilePath, "logo.png"));

        var preview = cut.Find("[data-testid=code-image-preview] img");
        Assert.StartsWith("data:image/png;base64,", preview.GetAttribute("src"));
        Assert.Contains("code-editor-host-hidden", cut.Find(".code-editor-host").GetAttribute("class"));
    }

    [Fact]
    public void A_binary_route_shows_the_binary_notice()
    {
        using var ctx = PageContext();

        var cut = ctx.Render<CodePage>(p => p.Add(c => c.FilePath, "tool.exe"));

        Assert.NotEmpty(cut.FindAll("[data-testid=code-binary-notice]"));
        Assert.Contains("binary or too large", cut.Markup);
    }

    [Fact]
    public void Closing_the_last_tab_navigates_back_to_the_bare_code_route()
    {
        using var ctx = PageContext();
        var cut = ctx.Render<CodePage>(p => p.Add(c => c.FilePath, "README.md"));
        var nav = ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();

        cut.Find(".code-tab .code-tab-close").Click();

        Assert.Empty(cut.FindAll(".code-tab"));
        Assert.EndsWith("/code", nav.Uri);
    }

    [Fact]
    public void Expanding_a_directory_lazy_loads_its_children()
    {
        using var ctx = PageContext();
        var cut = ctx.Render<CodePage>();

        Assert.DoesNotContain("Program.cs", cut.Markup);
        cut.Find(".code-dir").Click();

        Assert.Contains("Program.cs", cut.Markup);
    }

    [Fact]
    public void Tree_section_header_offers_refresh_and_collapse_all()
    {
        using var ctx = PageContext();
        var cut = ctx.Render<CodePage>();

        Assert.NotEmpty(cut.FindAll("[data-testid=code-tree-refresh]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=code-tree-collapse-all]"));

        // Collapse-all folds an expanded directory back up.
        cut.Find(".code-dir").Click();
        Assert.Contains("Program.cs", cut.Markup);
        cut.Find("[data-testid=code-tree-collapse-all]").Click();
        Assert.DoesNotContain("Program.cs", cut.Markup);
    }

    [Fact]
    public void Right_clicking_a_tab_opens_its_context_menu_and_pin_moves_it_to_the_pinned_row()
    {
        using var ctx = PageContext();
        var cut = ctx.Render<CodePage>(p => p.Add(c => c.FilePath, "README.md"));

        Assert.Empty(cut.FindAll(".code-tabs-pinned-row"));
        cut.Find(".code-tab").TriggerEvent("oncontextmenu", new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        var menu = cut.Find("[data-testid=code-context-menu]");
        Assert.Contains("Close Others", menu.TextContent);
        Assert.Contains("Close All But Pinned", menu.TextContent);

        cut.FindAll(".code-context-item").First(b => b.TextContent == "Pin").Click();

        Assert.NotEmpty(cut.FindAll(".code-tabs-pinned-row"));
        Assert.NotEmpty(cut.FindAll(".code-tab.pinned"));
    }

    [Fact]
    public void Right_clicking_a_folder_offers_creation_clipboard_and_path_items()
    {
        using var ctx = PageContext();
        var cut = ctx.Render<CodePage>();

        cut.Find(".code-dir").TriggerEvent("oncontextmenu", new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        var menu = cut.Find("[data-testid=code-context-menu]");
        Assert.Contains("New File…", menu.TextContent);
        Assert.Contains("New Folder…", menu.TextContent);
        Assert.Contains("Copy Relative Path", menu.TextContent);
        // Nothing on the clipboard yet, so Paste is disabled.
        Assert.True(cut.FindAll(".code-context-item").First(b => b.TextContent == "Paste").HasAttribute("disabled"));
    }

    [Fact]
    public void New_file_prompt_creates_and_navigates_to_the_file()
    {
        using var ctx = PageContext();
        var cut = ctx.Render<CodePage>();
        var nav = ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();

        cut.Find(".code-dir").TriggerEvent("oncontextmenu", new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        cut.FindAll(".code-context-item").First(b => b.TextContent == "New File…").Click();
        cut.Find("[data-testid=code-prompt-name]").Input("fresh.cs");
        cut.Find("[data-testid=code-prompt-create]").Click();

        Assert.EndsWith("/code/src/fresh.cs", nav.Uri);
    }

    // ---- Backlog 27: the name prompts own the keyboard ----

    /// <summary>Opens the New File prompt from a folder's context menu and returns the page.</summary>
    private static void OpenNewFilePrompt(IRenderedComponent<CodePage> cut)
    {
        cut.Find(".code-dir").TriggerEvent("oncontextmenu", new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        cut.InvokeAsync(() => cut.FindAll(".code-context-item").First(b => b.TextContent == "New File…").Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-prompt-name]")));
    }

    /// <summary>Opens the Rename prompt for README.md from its context menu.</summary>
    private static void OpenRenamePrompt(IRenderedComponent<CodePage> cut)
    {
        cut.FindAll(".code-row.code-file").First(r => r.TextContent.Contains("README.md")).ContextMenu();
        cut.WaitForAssertion(() => Assert.Contains("Rename…", cut.Markup));
        cut.InvokeAsync(() => cut.FindAll(".code-context-item").First(b => b.TextContent == "Rename…").Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-rename-name]")));
    }

    private static void AssertFocused(BunitContext ctx, string selector, bool selectAll)
    {
        var calls = ctx.JSInterop.Invocations["fravashiCode.focusElement"];
        Assert.Contains(calls, call =>
            call.Arguments.Count == 2
            && (call.Arguments[0] as string) == selector
            && Equals(call.Arguments[1], selectAll));
    }

    [Fact]
    public void Opening_the_new_file_prompt_focuses_its_name_box()
    {
        using var ctx = PageContext();
        var cut = ctx.Render<CodePage>();

        OpenNewFilePrompt(cut);

        // An empty box: focus only, there is nothing to select.
        cut.WaitForAssertion(() => AssertFocused(ctx, "[data-testid=code-prompt-name]", selectAll: false));
    }

    [Fact]
    public void Enter_in_the_new_file_box_creates_the_file_without_touching_the_button()
    {
        using var ctx = PageContext();
        var cut = ctx.Render<CodePage>();
        var nav = ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();

        OpenNewFilePrompt(cut);
        cut.Find("[data-testid=code-prompt-name]").Input("fresh.cs");
        cut.InvokeAsync(() => cut.Find("[data-testid=code-prompt-name]")
            .KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" }));

        cut.WaitForAssertion(() => Assert.EndsWith("/code/src/fresh.cs", nav.Uri));
    }

    [Fact]
    public void Escape_dismisses_the_new_file_prompt()
    {
        using var ctx = PageContext();
        var cut = ctx.Render<CodePage>();

        OpenNewFilePrompt(cut);
        cut.InvokeAsync(() => cut.Find("[data-testid=code-prompt-name]")
            .KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" }));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=code-prompt-name]")));
    }

    [Fact]
    public void Opening_the_rename_prompt_focuses_and_selects_the_current_name()
    {
        using var ctx = PageContext();
        var cut = ctx.Render<CodePage>();

        OpenRenamePrompt(cut);

        // Pre-filled with the current name, so typing has to replace it rather than append to it.
        cut.WaitForAssertion(() => AssertFocused(ctx, "[data-testid=code-rename-name]", selectAll: true));
    }

    [Fact]
    public void Enter_in_the_rename_box_renames_without_touching_the_button()
    {
        using var ctx = PageContext();
        var cut = ctx.Render<CodePage>();
        var writer = ctx.Services.GetRequiredService<FakeWriter>();

        OpenRenamePrompt(cut);
        cut.Find("[data-testid=code-rename-name]").Input("CHANGES.md");
        cut.InvokeAsync(() => cut.Find("[data-testid=code-rename-name]")
            .KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" }));

        cut.WaitForAssertion(() => Assert.Single(writer.Renames));
        Assert.Equal(("README.md", "CHANGES.md"), writer.Renames[0]);
    }

    [Fact]
    public void Escape_dismisses_the_rename_prompt_without_renaming()
    {
        using var ctx = PageContext();
        var cut = ctx.Render<CodePage>();
        var writer = ctx.Services.GetRequiredService<FakeWriter>();

        OpenRenamePrompt(cut);
        cut.InvokeAsync(() => cut.Find("[data-testid=code-rename-name]")
            .KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" }));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=code-rename-name]")));
        Assert.Empty(writer.Renames);
    }

    [Fact]
    public void Rename_prompt_routes_through_the_writer()
    {
        using var ctx = PageContext();
        var cut = ctx.Render<CodePage>();
        var writer = ctx.Services.GetRequiredService<FakeWriter>();

        cut.FindAll(".code-row.code-file").First(r => r.TextContent.Contains("README.md")).ContextMenu();
        cut.WaitForAssertion(() => Assert.Contains("Rename…", cut.Markup));
        cut.InvokeAsync(() => cut.FindAll(".code-context-item").First(b => b.TextContent == "Rename…").Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-rename-name]")));
        cut.Find("[data-testid=code-rename-name]").Input("CHANGES.md");
        cut.InvokeAsync(() => cut.Find("[data-testid=code-rename-confirm]").Click());

        cut.WaitForAssertion(() => Assert.Single(writer.Renames));
        Assert.Equal(("README.md", "CHANGES.md"), writer.Renames[0]);
    }

    [Fact]
    public void Delete_confirms_with_the_trash_explanation_then_archives_through_the_trash()
    {
        using var ctx = PageContext();
        var cut = ctx.Render<CodePage>();
        var trash = ctx.Services.GetRequiredService<FakeTrash>();

        cut.FindAll(".code-row.code-file").First(r => r.TextContent.Contains("README.md")).ContextMenu();
        cut.WaitForAssertion(() => Assert.Contains("Delete", cut.Markup));
        cut.InvokeAsync(() => cut.FindAll(".code-context-item").First(b => b.TextContent == "Delete").Click());

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-delete-confirm]")));
        Assert.Contains("trash", cut.Find("[data-testid=code-delete-confirm]").TextContent);
        Assert.Empty(trash.Deleted); // nothing happens before the confirm

        cut.InvokeAsync(() => cut.Find("[data-testid=code-delete-confirm-go]").Click());

        cut.WaitForAssertion(() => Assert.Single(trash.Deleted));
        Assert.Equal(["README.md"], trash.Deleted[0]);
    }

    [Fact]
    public void The_explorer_undo_reverses_a_rename()
    {
        using var ctx = PageContext();
        var cut = ctx.Render<CodePage>();
        var writer = ctx.Services.GetRequiredService<FakeWriter>();

        Assert.True(cut.Find("[data-testid=code-undo-fileop]").HasAttribute("disabled"));

        cut.FindAll(".code-row.code-file").First(r => r.TextContent.Contains("README.md")).ContextMenu();
        cut.InvokeAsync(() => cut.FindAll(".code-context-item").First(b => b.TextContent == "Rename…").Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-rename-name]")));
        cut.Find("[data-testid=code-rename-name]").Input("CHANGES.md");
        cut.InvokeAsync(() => cut.Find("[data-testid=code-rename-confirm]").Click());
        cut.WaitForAssertion(() => Assert.Single(writer.Renames));

        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=code-undo-fileop]").HasAttribute("disabled")));
        cut.InvokeAsync(() => cut.Find("[data-testid=code-undo-fileop]").Click());

        cut.WaitForAssertion(() => Assert.Equal(2, writer.Renames.Count));
        Assert.Equal(("CHANGES.md", "README.md"), writer.Renames[1]);
        Assert.True(cut.Find("[data-testid=code-undo-fileop]").HasAttribute("disabled")); // one level deep
    }

    [Fact]
    public void The_explorer_undo_restores_a_deletion_from_its_trash_zip()
    {
        using var ctx = PageContext();
        var cut = ctx.Render<CodePage>();
        var trash = ctx.Services.GetRequiredService<FakeTrash>();

        cut.FindAll(".code-row.code-file").First(r => r.TextContent.Contains("README.md")).ContextMenu();
        cut.InvokeAsync(() => cut.FindAll(".code-context-item").First(b => b.TextContent == "Delete").Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-delete-confirm-go]")));
        cut.InvokeAsync(() => cut.Find("[data-testid=code-delete-confirm-go]").Click());
        cut.WaitForAssertion(() => Assert.Single(trash.Deleted));

        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=code-undo-fileop]").HasAttribute("disabled")));
        cut.InvokeAsync(() => cut.Find("[data-testid=code-undo-fileop]").Click());

        cut.WaitForAssertion(() => Assert.Equal(["2026-07-26_1200_[main].zip"], trash.Restored));
    }

    [Fact]
    public void Close_all_takes_pinned_tabs_too_and_returns_to_the_empty_editor()
    {
        using var ctx = PageContext();
        var cut = ctx.Render<CodePage>(p => p.Add(c => c.FilePath, "README.md"));
        var nav = ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();

        cut.Find(".code-tab").TriggerEvent("oncontextmenu", new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        cut.InvokeAsync(() => cut.FindAll(".code-context-item").First(b => b.TextContent == "Pin").Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".code-tabs-pinned-row")));

        cut.InvokeAsync(() => cut.Find("[data-testid=code-open-editors] .page-sidebar-section-header").Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-close-all]")));
        cut.InvokeAsync(() => cut.Find("[data-testid=code-close-all]").Click());

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".code-tab")));
        Assert.EndsWith("/code", nav.Uri);
    }

    [Fact]
    public void Markdown_files_get_a_preview_toggle_that_renders_html()
    {
        using var ctx = PageContext();
        var cut = ctx.Render<CodePage>(p => p.Add(c => c.FilePath, "README.md"));

        var toggle = cut.Find("[data-testid=code-preview-toggle]");
        toggle.Click();

        Assert.NotEmpty(cut.FindAll("[data-testid=code-md-preview]"));
        Assert.Contains("content of README.md", cut.Find("[data-testid=code-md-preview]").TextContent);
    }

    [Fact]
    public void Opening_a_nested_file_reveals_its_folder_even_after_collapse_all()
    {
        using var ctx = PageContext();

        // The route opens src/Program.cs without the folder ever being clicked: the ancestor chain
        // must expand so the file is visible in the tree (and again after Collapse All + reopen).
        var cut = ctx.Render<CodePage>(p => p.Add(c => c.FilePath, "src/Program.cs"));

        Assert.Contains("Program.cs", cut.Find("[data-testid=code-tree]").TextContent);
        cut.Find("[data-testid=code-tree-collapse-all]").Click();
        Assert.DoesNotContain("Program.cs", cut.Find("[data-testid=code-tree]").TextContent);
    }

    [Fact]
    public void Ctrl_tab_switcher_lists_open_editors_in_mru_order_and_commits_on_release()
    {
        using var ctx = PageContext();
        var cut = ctx.Render<CodePage>(p => p.Add(c => c.FilePath, "README.md"));
        var nav = ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var workspace = ctx.Services.GetRequiredService<CodeWorkspace>();
        workspace.OpenAt("logo.png", "logo.png");

        // First step selects the previous document; the popup lists both files.
        cut.InvokeAsync(() => cut.Instance.HotkeySwitcherStep(1));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-switcher]")));
        Assert.Equal(2, cut.FindAll(".code-switcher-item").Count);
        Assert.NotEmpty(cut.FindAll(".code-switcher-item.active"));

        cut.InvokeAsync(() => cut.Instance.HotkeySwitcherCommit());
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=code-switcher]")));
        Assert.EndsWith("/code/README.md", nav.Uri);
    }

    [Fact]
    public void Non_previewable_files_have_no_preview_toggle()
    {
        using var ctx = PageContext();
        var cut = ctx.Render<CodePage>(p => p.Add(c => c.FilePath, "src/Program.cs"));

        Assert.Empty(cut.FindAll("[data-testid=code-preview-toggle]"));
    }

    // ---- Phase 2: editing, save, external-change sync (ADR 0016) ----

    [Fact]
    public async Task Dirty_buffers_mark_the_tab_and_enable_save_all()
    {
        using var ctx = PageContext();
        var cut = ctx.Render<CodePage>(p => p.Add(c => c.FilePath, "README.md"));

        // Header actions render only while the section is expanded (VS Code behavior).
        cut.Find("[data-testid=code-open-editors] .page-sidebar-section-header").Click();
        Assert.True(cut.Find("[data-testid=code-save-all]").HasAttribute("disabled"));

        await cut.InvokeAsync(() => cut.Instance.SetDirty("README.md", true));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".code-tab.dirty")));
        Assert.False(cut.Find("[data-testid=code-save-all]").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Ctrl_s_streams_the_buffer_to_the_writer_with_the_known_timestamp()
    {
        using var ctx = PageContext();
        var reader = ctx.Services.GetRequiredService<FakeReader>();
        var writer = ctx.Services.GetRequiredService<FakeWriter>();
        var opened = DateTimeOffset.UtcNow.AddMinutes(-1);
        reader.Stamps["README.md"] = opened;
        ctx.JSInterop.Setup<Microsoft.JSInterop.IJSStreamReference?>("fravashiCode.getContent", _ => true)
            .SetResult(new FakeContentStream("edited content"));
        var cut = ctx.Render<CodePage>(p => p.Add(c => c.FilePath, "README.md"));

        await cut.InvokeAsync(() => cut.Instance.SetDirty("README.md", true));
        await cut.InvokeAsync(() => cut.Instance.HotkeySave());

        cut.WaitForAssertion(() => Assert.Single(writer.Saves));
        Assert.Equal("README.md", writer.Saves[0].Path);
        Assert.Equal("edited content", writer.Saves[0].Content);
        Assert.Equal(opened, writer.Saves[0].Expected);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".code-tab.dirty")));
    }

    // ---- pasting an image into markdown (backlog 26) ----

    private static readonly byte[] PastedPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 5, 5];

    [Fact]
    public async Task An_image_pasted_into_markdown_is_saved_beside_the_file_and_its_link_inserted()
    {
        using var ctx = PageContext();
        var writer = ctx.Services.GetRequiredService<FakeWriter>();
        ctx.JSInterop.Setup<Microsoft.JSInterop.IJSStreamReference?>("fravashiImagePaste.take", _ => true)
            .SetResult(new FakeContentStream(PastedPng));
        var insert = ctx.JSInterop.Setup<bool>("fravashiCode.insertPastedImage", _ => true);
        insert.SetResult(true);
        var cut = ctx.Render<CodePage>(p => p.Add(c => c.FilePath, "docs/guide.md"));

        await cut.InvokeAsync(() => cut.Instance.EditorImagePasted("docs/guide.md", "img7", PastedPng.Length));

        cut.WaitForAssertion(() => Assert.Single(insert.Invocations));
        var (directory, name) = Assert.Single(writer.CreatedFiles);
        Assert.Equal("docs", directory);
        Assert.Matches(@"^pasted-image-\d{8}-\d{6}\.png$", name);
        Assert.Equal($"docs/{name}", Assert.Single(writer.Saves).Path);
        Assert.Null(writer.Saves[0].Expected);
        var arguments = insert.Invocations.Single().Arguments;
        Assert.Equal("img7", arguments[0]);
        Assert.Equal($"![{name[..^4]}]({name})", arguments[1]);
    }

    [Fact]
    public async Task A_pasted_image_that_is_not_for_markdown_or_too_large_writes_nothing()
    {
        using var ctx = PageContext();
        var writer = ctx.Services.GetRequiredService<FakeWriter>();
        var cut = ctx.Render<CodePage>(p => p.Add(c => c.FilePath, "README.md"));

        await cut.InvokeAsync(() => cut.Instance.EditorImagePasted("src/Program.cs", "img1", 10));
        await cut.InvokeAsync(() => cut.Instance.EditorImagePasted("README.md", "img2", MarkdownImagePaster.MaxBytes + 1L));

        Assert.Empty(writer.CreatedFiles);
        Assert.Empty(writer.Saves);
        Assert.DoesNotContain(ctx.JSInterop.Invocations, i => i.Identifier == "fravashiImagePaste.take");
        Assert.Equal(2, ctx.JSInterop.Invocations.Count(i => i.Identifier == "fravashiCode.forgetPastedImage"));
    }

    [Fact]
    public async Task A_conflicting_save_surfaces_the_banner_instead_of_overwriting()
    {
        using var ctx = PageContext();
        var writer = ctx.Services.GetRequiredService<FakeWriter>();
        writer.NextSaveResult = RepositorySaveResult.Conflicted;
        ctx.JSInterop.Setup<Microsoft.JSInterop.IJSStreamReference?>("fravashiCode.getContent", _ => true)
            .SetResult(new FakeContentStream("mine"));
        var cut = ctx.Render<CodePage>(p => p.Add(c => c.FilePath, "README.md"));

        await cut.InvokeAsync(() => cut.Instance.SetDirty("README.md", true));
        await cut.InvokeAsync(() => cut.Instance.HotkeySave());

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-file-alert]")));
        Assert.Contains("changed on disk", cut.Find("[data-testid=code-file-alert]").TextContent);
        Assert.NotEmpty(cut.FindAll(".code-tab.dirty"));
    }

    [Fact]
    public void An_external_change_to_a_clean_open_file_reloads_it_silently()
    {
        using var ctx = PageContext();
        var reader = ctx.Services.GetRequiredService<FakeReader>();
        var notifier = ctx.Services.GetRequiredService<FakeNotifier>();
        reader.Stamps["README.md"] = DateTimeOffset.UtcNow.AddMinutes(-1);
        var cut = ctx.Render<CodePage>(p => p.Add(c => c.FilePath, "README.md"));

        reader.Stamps["README.md"] = DateTimeOffset.UtcNow;
        reader.Contents["README.md"] = "changed outside";
        notifier.Raise(new RepositoryChange(RepositoryChangeKind.ChangedOrCreated, "README.md"));

        cut.WaitForAssertion(() =>
            ctx.JSInterop.VerifyInvoke("fravashiCode.applyExternalContent"), TimeSpan.FromSeconds(3));
        Assert.Empty(cut.FindAll("[data-testid=code-file-alert]"));
    }

    [Fact]
    public async Task An_external_change_to_a_dirty_open_file_asks_instead_of_clobbering()
    {
        using var ctx = PageContext();
        var reader = ctx.Services.GetRequiredService<FakeReader>();
        var notifier = ctx.Services.GetRequiredService<FakeNotifier>();
        reader.Stamps["README.md"] = DateTimeOffset.UtcNow.AddMinutes(-1);
        var cut = ctx.Render<CodePage>(p => p.Add(c => c.FilePath, "README.md"));

        await cut.InvokeAsync(() => cut.Instance.SetDirty("README.md", true));
        reader.Stamps["README.md"] = DateTimeOffset.UtcNow;
        notifier.Raise(new RepositoryChange(RepositoryChangeKind.ChangedOrCreated, "README.md"));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-alert-reload]")), TimeSpan.FromSeconds(3));
        Assert.NotEmpty(cut.FindAll("[data-testid=code-alert-overwrite]"));
    }

    [Fact]
    public void An_external_delete_of_an_open_file_offers_close_or_keep()
    {
        using var ctx = PageContext();
        var notifier = ctx.Services.GetRequiredService<FakeNotifier>();
        var cut = ctx.Render<CodePage>(p => p.Add(c => c.FilePath, "README.md"));

        notifier.Raise(new RepositoryChange(RepositoryChangeKind.Deleted, "README.md"));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-alert-close]")), TimeSpan.FromSeconds(3));
        Assert.NotEmpty(cut.FindAll("[data-testid=code-alert-keep]"));
        Assert.Contains("deleted on disk", cut.Find("[data-testid=code-file-alert]").TextContent);
    }

    [Fact]
    public void After_the_change_stream_may_have_lost_events_the_explorer_shows_a_file_made_in_the_gap()
    {
        // Backlog 257: the file was created on the host while the bridge stream was down, so no event ever named it; the
        // "anything changed" change the subscriber sends when the stream returns is what makes the explorer show it.
        using var ctx = PageContext();
        var reader = ctx.Services.GetRequiredService<FakeReader>();
        var notifier = ctx.Services.GetRequiredService<FakeNotifier>();
        var cut = ctx.Render<CodePage>();
        cut.WaitForAssertion(() => Assert.Contains("README.md", cut.Find(".code-tree-body").TextContent));
        Assert.DoesNotContain("made-in-the-gap.cs", cut.Markup);

        reader.AddedRootFiles.Add("made-in-the-gap.cs");
        notifier.Raise(new RepositoryChange(RepositoryChangeKind.ChangedOrCreated, string.Empty));

        cut.WaitForAssertion(
            () => Assert.Contains("made-in-the-gap.cs", cut.Find(".code-tree-body").TextContent), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task After_the_change_stream_may_have_lost_events_every_open_buffer_is_checked_and_an_unsaved_one_is_kept()
    {
        // Backlog 257: an empty path means "anything may have changed" (the bridge stream came back after a gap, or a
        // watcher overflowed). The tree alone used to be refreshed; now every open buffer is checked by the same rules
        // as one external edit: a clean one reloads, one with unsaved edits gets the banner and is never replaced.
        using var ctx = PageContext();
        var reader = ctx.Services.GetRequiredService<FakeReader>();
        var notifier = ctx.Services.GetRequiredService<FakeNotifier>();
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);
        reader.Stamps["README.md"] = before;
        reader.Stamps["src/Program.cs"] = before;
        var cut = ctx.Render<CodePage>(p => p.Add(c => c.FilePath, "README.md"));
        cut.Render(p => p.Add(c => c.FilePath, "src/Program.cs"));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".code-tab").Count));
        await cut.InvokeAsync(() => cut.Instance.SetDirty("src/Program.cs", true));

        // Both files changed on the host while nobody was told.
        reader.Stamps["README.md"] = DateTimeOffset.UtcNow;
        reader.Contents["README.md"] = "changed in the gap";
        reader.Stamps["src/Program.cs"] = DateTimeOffset.UtcNow;
        reader.Contents["src/Program.cs"] = "also changed in the gap";
        notifier.Raise(new RepositoryChange(RepositoryChangeKind.ChangedOrCreated, string.Empty));

        cut.WaitForAssertion(
            () => Assert.Contains(ctx.JSInterop.Invocations, i => i.Identifier == "fravashiCode.applyExternalContent"
                && Equals(i.Arguments[0], "README.md") && Equals(i.Arguments[1], "changed in the gap")),
            TimeSpan.FromSeconds(3));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-alert-reload]")), TimeSpan.FromSeconds(3));
        Assert.Contains("changed on disk", cut.Find("[data-testid=code-file-alert]").TextContent);
        Assert.DoesNotContain(ctx.JSInterop.Invocations, i => i.Identifier == "fravashiCode.applyExternalContent"
            && Equals(i.Arguments[0], "src/Program.cs"));
        Assert.NotEmpty(cut.FindAll(".code-tab.dirty"));
    }

    [Fact]
    public async Task A_reload_the_window_never_received_is_delivered_when_its_connection_is_back()
    {
        // Backlog 264, measured on v0.9.69: the look-again read the changed
        // file of a clean buffer, but the window was disconnected, the apply call failed and nothing looked again.
        using var ctx = PageContext();
        var logs = new RecordingLogger();
        ctx.Services.AddSingleton<Microsoft.Extensions.Logging.ILogger<CodePage>>(logs);
        var reader = ctx.Services.GetRequiredService<FakeReader>();
        var notifier = ctx.Services.GetRequiredService<FakeNotifier>();
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);
        reader.Stamps["README.md"] = before;
        reader.Stamps["src/Program.cs"] = before;
        var apply = ctx.JSInterop.SetupVoid("fravashiCode.applyExternalContent", _ => true);
        var cut = ctx.Render<CodePage>(p => p.Add(c => c.FilePath, "README.md"));
        cut.Render(p => p.Add(c => c.FilePath, "src/Program.cs"));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".code-tab").Count));
        await cut.InvokeAsync(() => cut.Instance.SetDirty("src/Program.cs", true));
        var connection = ctx.Services.GetRequiredService<CircuitConnection>();

        // The window's connection drops; both files change on the host; the bridge's look-again runs and the apply call
        // is lost on the way to the window.
        connection.ConnectionDown();
        reader.Stamps["README.md"] = DateTimeOffset.UtcNow;
        reader.Contents["README.md"] = "buffer v2";
        reader.Stamps["src/Program.cs"] = DateTimeOffset.UtcNow;
        reader.Contents["src/Program.cs"] = "also changed in the gap";
        notifier.Raise(new RepositoryChange(RepositoryChangeKind.ChangedOrCreated, string.Empty));
        cut.WaitForAssertion(() => Assert.Single(apply.Invocations), TimeSpan.FromSeconds(3));
        apply.SetCanceled();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-alert-reload]")), TimeSpan.FromSeconds(3));

        // The window connects again: the clean buffer is applied, the unsaved one is still only bannered.
        apply.SetVoidResult();
        await cut.InvokeAsync(connection.ConnectionUp);

        cut.WaitForAssertion(() => Assert.Equal(2, apply.Invocations.Count), TimeSpan.FromSeconds(3));
        Assert.All(apply.Invocations, i =>
        {
            Assert.Equal("README.md", i.Arguments[0]);
            Assert.Equal("buffer v2", i.Arguments[1]);
        });
        Assert.Contains("changed on disk", cut.Find("[data-testid=code-file-alert]").TextContent);
        Assert.NotEmpty(cut.FindAll(".code-tab.dirty"));

        // Delivered means recorded: a further reconnection finds the buffer in step and sends nothing.
        await cut.InvokeAsync(connection.ConnectionUp);
        await Task.Delay(200);
        Assert.Equal(2, apply.Invocations.Count);

        // The disconnection is expected: no warning carrying an exception (a stack trace in the log).
        Assert.DoesNotContain(logs.Entries, e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Warning && e.Exception is not null);
    }

    [Fact]
    public void A_reload_not_confirmed_while_the_connection_is_reported_up_is_sent_again_shortly()
    {
        // Backlog 264: the server can still report the connection up when the window has already gone (or the window is
        // slow), so no reconnection will follow; the page looks again by itself after the apply bound.
        using var ctx = PageContext();
        var reader = ctx.Services.GetRequiredService<FakeReader>();
        var notifier = ctx.Services.GetRequiredService<FakeNotifier>();
        reader.Stamps["README.md"] = DateTimeOffset.UtcNow.AddMinutes(-1);
        var apply = ctx.JSInterop.SetupVoid("fravashiCode.applyExternalContent", _ => true);
        var cut = ctx.Render<CodePage>(p => p.Add(c => c.FilePath, "README.md"));

        reader.Stamps["README.md"] = DateTimeOffset.UtcNow;
        reader.Contents["README.md"] = "buffer v2";
        notifier.Raise(new RepositoryChange(RepositoryChangeKind.ChangedOrCreated, "README.md"));
        cut.WaitForAssertion(() => Assert.Single(apply.Invocations), TimeSpan.FromSeconds(3));
        apply.SetCanceled();
        apply.SetVoidResult();

        cut.WaitForAssertion(
            () => Assert.Equal(2, apply.Invocations.Count), TimeSpan.FromSeconds(10)); // the page waits its 5 s bound first
        Assert.Equal("buffer v2", apply.Invocations.Last().Arguments[1]);
    }

    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger<CodePage>
    {
        public System.Collections.Concurrent.ConcurrentQueue<(Microsoft.Extensions.Logging.LogLevel Level, Exception? Exception, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, exception, formatter(state, exception)));
    }

    [Fact]
    public async Task Closing_a_dirty_tab_asks_before_discarding()
    {
        using var ctx = PageContext();
        var cut = ctx.Render<CodePage>(p => p.Add(c => c.FilePath, "README.md"));
        await cut.InvokeAsync(() => cut.Instance.SetDirty("README.md", true));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".code-tab.dirty")));
        cut.Find(".code-tab .code-tab-close").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-close-confirm]")));
        Assert.NotEmpty(cut.FindAll(".code-tab")); // still open, nothing discarded yet

        cut.Find("[data-testid=code-close-discard]").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".code-tab")));
    }

    [Fact]
    public void Truncated_files_never_reach_an_editable_buffer()
    {
        // A truncated read is a placeholder, not the file; editing it would destroy the real content.
        using var ctx = PageContext();
        var reader = ctx.Services.GetRequiredService<FakeReader>();
        reader.Truncated.Add("README.md");

        var cut = ctx.Render<CodePage>(p => p.Add(c => c.FilePath, "README.md"));

        Assert.NotEmpty(cut.FindAll("[data-testid=code-binary-notice]"));
    }

    [Fact]
    public void File_rows_and_tabs_offer_a_history_action_that_routes_to_the_git_page()
    {
        using var ctx = PageContext();
        var cut = ctx.Render<CodePage>(p => p.Add(c => c.FilePath, "README.md"));
        var nav = ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();

        // Tree file row context menu.
        cut.Find(".code-row.code-file").ContextMenu();
        cut.WaitForAssertion(() => Assert.Contains("History", cut.Markup));
        var history = cut.FindAll(".code-context-item").First(e => e.TextContent.Trim() == "History");
        history.Click();

        cut.WaitForAssertion(() => Assert.Contains("/git/history/", nav.Uri));
    }

    // ---- Hot exit (ADR 0016 addendum): unsaved buffers survive close/crash ----

    [Fact]
    public void Restored_backups_mark_their_tabs_dirty_before_the_files_are_even_opened()
    {
        using var ctx = PageContext();
        var session = """{"Open":[{"P":"README.md","N":"README.md"},{"P":"src/Program.cs","N":"Program.cs"}],"Active":"README.md"}""";
        ctx.JSInterop.Setup<string?>("fravashiCode.loadWorkspace").SetResult(session);
        ctx.JSInterop.Setup<CodePage.BackupEntry[]?>("fravashiCode.listBackups")
            .SetResult([new CodePage.BackupEntry("src/Program.cs", DateTimeOffset.UnixEpoch.ToString("O"))]);

        var cut = ctx.Render<CodePage>();

        // src/Program.cs was never activated this session, yet its hot-exit backup marks it dirty.
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".code-tab.dirty")));
        var dirtyTab = cut.FindAll(".code-tab.dirty").Single();
        Assert.Contains("Program.cs", dirtyTab.TextContent);
    }

    [Fact]
    public void A_backup_based_on_an_older_disk_state_raises_the_conflict_banner_on_open()
    {
        using var ctx = PageContext();
        var reader = ctx.Services.GetRequiredService<FakeReader>();
        reader.Stamps["README.md"] = DateTimeOffset.UtcNow; // the disk moved on while the app was closed
        ctx.JSInterop.Setup<CodePage.BackupOpenResult?>("fravashiCode.open", _ => true)
            .SetResult(new CodePage.BackupOpenResult(true, DateTimeOffset.UnixEpoch.ToString("O")));

        var cut = ctx.Render<CodePage>(p => p.Add(c => c.FilePath, "README.md"));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-file-alert]")), TimeSpan.FromSeconds(3));
        Assert.Contains("changed on disk", cut.Find("[data-testid=code-file-alert]").TextContent);
        Assert.NotEmpty(cut.FindAll(".code-tab.dirty"));
    }

    [Fact]
    public void Orphaned_backups_for_closed_tabs_are_cleared_on_restore()
    {
        using var ctx = PageContext();
        var session = """{"Open":[{"P":"README.md","N":"README.md"}],"Active":"README.md"}""";
        ctx.JSInterop.Setup<string?>("fravashiCode.loadWorkspace").SetResult(session);
        ctx.JSInterop.Setup<CodePage.BackupEntry[]?>("fravashiCode.listBackups")
            .SetResult([new CodePage.BackupEntry("gone/file.cs", null)]);
        var clear = ctx.JSInterop.SetupVoid("fravashiCode.clearBackup", "gone/file.cs");

        var cut = ctx.Render<CodePage>();

        cut.WaitForAssertion(() => Assert.NotEmpty(clear.Invocations));
        Assert.Empty(cut.FindAll(".code-tab.dirty"));
    }

    // ---- the address against the stored session (backlog 265) ----------------------------------
    //
    // These hold the root listing open so the page's first render, and the session restore it starts,
    // run before OnParametersSet, as they do against the real bridge. bunit does not prerender, so they
    // say nothing about the prerender instance (which never restores a session: it has no JS).

    private const string StoredAActive = """{"Open":[{"P":"a.txt","N":"a.txt"}],"Active":"a.txt"}""";

    private static string LastEditorOpen(BunitContext ctx) =>
        (string)ctx.JSInterop.Invocations["fravashiCode.open"].Last().Arguments[0]!;

    private static string LastEditorContent(BunitContext ctx) =>
        (string)ctx.JSInterop.Invocations["fravashiCode.open"].Last().Arguments[1]!;

    private static string LastSavedSession(BunitContext ctx) =>
        (string)ctx.JSInterop.Invocations["fravashiCode.saveWorkspace"].Last().Arguments[0]!;

    /// <summary>
    /// Renders /code/{route} with the root listing held, so the restore runs first, then lets the listing
    /// finish (OnParametersSet) and only after that a.txt's read. If the page opens the stored tab a.txt
    /// at all, that open therefore finishes LAST, which is the order that put the editor on the stored tab
    /// before backlog 265's fix (read from the code and reproduced here; the live 11 and 12 scripts showed
    /// only the outcome, the editor and the stored session ending on a while the address named b). With
    /// the fix a route naming b never reads a.txt, so the gate is released with nobody waiting on it.
    /// </summary>
    private static async Task<IRenderedComponent<CodePage>> RenderRouteWithTheListingHeldAsync(
        BunitContext ctx, string? route)
    {
        var reader = ctx.Services.GetRequiredService<FakeReader>();
        reader.RootGate = new TaskCompletionSource();
        var aRead = new TaskCompletionSource();
        reader.ReadGates["a.txt"] = aRead;

        var cut = route is null
            ? ctx.Render<CodePage>()
            : ctx.Render<CodePage>(p => p.Add(c => c.FilePath, route));

        // The restore has run (it asks for backups after hydrating) while the listing is still out.
        cut.WaitForAssertion(() => Assert.NotEmpty(ctx.JSInterop.Invocations["fravashiCode.listBackups"]));
        await cut.InvokeAsync(() => reader.RootGate.SetResult());
        await cut.InvokeAsync(() => aRead.SetResult());
        return cut;
    }

    [Fact]
    public async Task A_link_to_a_file_shows_that_file_whatever_tab_the_stored_session_had_active()
    {
        using var ctx = PageContext();
        ctx.JSInterop.Setup<string?>("fravashiCode.loadWorkspace").SetResult(StoredAActive);

        var cut = await RenderRouteWithTheListingHeldAsync(ctx, "b.txt");

        cut.WaitForAssertion(() => Assert.Contains("b.txt", cut.Find(".code-tab.active").TextContent));
        Assert.Equal("b.txt", LastEditorOpen(ctx));
        Assert.Equal("content of b.txt", LastEditorContent(ctx));
        Assert.Contains("\"Active\":\"b.txt\"", LastSavedSession(ctx));
        // The stored tab stays open beside the new one.
        Assert.Contains(cut.FindAll(".code-tab"), t => t.TextContent.Contains("a.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_link_to_a_file_shows_it_when_the_stored_active_file_is_gone()
    {
        using var ctx = PageContext();
        ctx.Services.GetRequiredService<FakeReader>().Missing.Add("a.txt");
        ctx.JSInterop.Setup<string?>("fravashiCode.loadWorkspace").SetResult(StoredAActive);

        var cut = await RenderRouteWithTheListingHeldAsync(ctx, "b.txt");

        cut.WaitForAssertion(() => Assert.Contains("b.txt", cut.Find(".code-tab.active").TextContent));
        Assert.Empty(cut.FindAll("[data-testid=code-missing-notice]"));
        Assert.Equal("b.txt", LastEditorOpen(ctx));
        Assert.Equal("content of b.txt", LastEditorContent(ctx));
        Assert.Contains("\"Active\":\"b.txt\"", LastSavedSession(ctx));
    }

    [Fact]
    public async Task An_in_app_link_to_a_file_wins_over_the_tab_this_window_had_active()
    {
        // The workspace is circuit-scoped, so after /code/a, /git and an in-app link the page meets it
        // with a already active.
        using var ctx = PageContext();
        ctx.Services.GetRequiredService<CodeWorkspace>().OpenAt("a.txt", "a.txt");
        ctx.JSInterop.Setup<string?>("fravashiCode.loadWorkspace").SetResult(StoredAActive);

        var cut = await RenderRouteWithTheListingHeldAsync(ctx, "b.txt");

        cut.WaitForAssertion(() => Assert.Contains("b.txt", cut.Find(".code-tab.active").TextContent));
        Assert.Equal("b.txt", LastEditorOpen(ctx));
        Assert.Equal("content of b.txt", LastEditorContent(ctx));
        Assert.Contains("\"Active\":\"b.txt\"", LastSavedSession(ctx));
    }

    [Fact]
    public async Task A_refresh_on_the_stored_active_file_keeps_it()
    {
        using var ctx = PageContext();
        ctx.JSInterop.Setup<string?>("fravashiCode.loadWorkspace").SetResult(StoredAActive);

        var cut = await RenderRouteWithTheListingHeldAsync(ctx, "a.txt");

        cut.WaitForAssertion(() => Assert.Equal("a.txt", LastEditorOpen(ctx)));
        Assert.Contains("a.txt", cut.Find(".code-tab.active").TextContent);
        Assert.Contains("\"Active\":\"a.txt\"", LastSavedSession(ctx));
    }

    [Fact]
    public async Task Without_a_file_in_the_address_the_stored_active_tab_is_shown_and_named_in_place()
    {
        using var ctx = PageContext();
        ctx.JSInterop.Setup<string?>("fravashiCode.loadWorkspace").SetResult(StoredAActive);
        var nav = ctx.Services.GetRequiredService<BunitNavigationManager>();

        var cut = await RenderRouteWithTheListingHeldAsync(ctx, route: null);

        cut.WaitForAssertion(() => Assert.Equal("a.txt", LastEditorOpen(ctx)));
        Assert.Contains("a.txt", cut.Find(".code-tab.active").TextContent);
        Assert.EndsWith("/code/a.txt", nav.Uri, StringComparison.Ordinal);
        Assert.True(nav.History.First().Options.ReplaceHistoryEntry);
    }

    [Fact]
    public async Task An_open_requested_later_wins_even_when_an_earlier_one_finishes_last()
    {
        // The ticket in OpenFromPathAsync: a.txt's two opens (OnParametersSet's and the restore's) wait
        // on its read while the address moves to b.txt, whose open finishes at once. a.txt's reads then
        // finish LAST and must not stage over b.txt.
        using var ctx = PageContext();
        var aRead = new TaskCompletionSource();
        ctx.Services.GetRequiredService<FakeReader>().ReadGates["a.txt"] = aRead;
        var cut = ctx.Render<CodePage>(p => p.Add(c => c.FilePath, "a.txt"));
        cut.WaitForAssertion(() => Assert.NotEmpty(ctx.JSInterop.Invocations["fravashiCode.listBackups"]));

        cut.Render(p => p.Add(c => c.FilePath, "b.txt"));
        cut.WaitForAssertion(() => Assert.Equal("b.txt", LastEditorOpen(ctx)));
        await cut.InvokeAsync(() => aRead.SetResult());

        cut.WaitForAssertion(() => Assert.Contains("b.txt", cut.Find(".code-tab.active").TextContent));
        Assert.Equal("b.txt", LastEditorOpen(ctx));
        Assert.Equal("content of b.txt", LastEditorContent(ctx));
        Assert.Contains("\"Active\":\"b.txt\"", LastSavedSession(ctx));
    }

    [Fact]
    public async Task A_page_left_while_it_restored_never_takes_the_editor_or_moves_the_window()
    {
        // Item #373, the v0.9.100 check: another project's window loaded /code, the landing moved it to the setup, and the Code
        // page it left finished restoring its stored tab afterwards. It navigated the window back to its file, and it
        // went on to open its file into the window's ONE editor (codeEditor.js) from a host that was no longer on
        // screen, beside the page that then drew the file. Left mid-restore, a page does neither.
        using var ctx = PageContext();
        ctx.JSInterop.Setup<string?>("fravashiCode.loadWorkspace").SetResult(StoredAActive);
        var reader = ctx.Services.GetRequiredService<FakeReader>();
        var aRead = new TaskCompletionSource();
        reader.ReadGates["a.txt"] = aRead;
        var nav = ctx.Services.GetRequiredService<BunitNavigationManager>();
        nav.NavigateTo("/code");

        var cut = ctx.Render<CodePage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(ctx.JSInterop.Invocations["fravashiCode.listBackups"]));

        // The window moves to another page while the stored tab's read is still out.
        nav.NavigateTo("/setup", replace: true);
        await ctx.DisposeComponentsAsync();
        await ctx.Renderer.Dispatcher.InvokeAsync(() => aRead.SetResult());
        await Task.Delay(100);

        Assert.EndsWith("/setup", nav.Uri, StringComparison.Ordinal);
        Assert.Empty(ctx.JSInterop.Invocations["fravashiCode.init"]);
        Assert.Empty(ctx.JSInterop.Invocations["fravashiCode.open"]);
    }

    // ---- the agent panel beside the editor (plan 2e Task 7) ---------------------------------

    [Fact]
    public void The_agent_panel_stays_out_of_the_way_until_it_is_opened()
    {
        using var ctx = PageContext();

        var cut = ctx.Render<CodePage>();

        Assert.Empty(cut.FindAll("[data-testid=code-session-panel]"));
        // Backlog 23: and nothing stands in for it inside the split either. The tall right-edge rail
        // that used to live here took 26px off the editor for as long as the panel stayed shut.
        Assert.Empty(cut.FindAll(".code-session-rail"));
        Assert.Empty(cut.FindAll("[data-testid=code-session-open]"));
    }

    [Fact]
    public void Opening_the_agent_panel_shows_it_beside_the_editor_with_a_way_back_to_the_area()
    {
        using var ctx = PageContext();
        ctx.Services.GetRequiredService<Fravashi.Infrastructure.Bridge.WorkspaceSelection>().Capture("w1");
        ctx.Services.GetRequiredService<FakeWorkspaceAgentSync>().CopyPath = "/workspaces/w1";
        var panel = ctx.Services.GetRequiredService<AgentPanelState>();
        var cut = ctx.Render<CodePage>();

        cut.InvokeAsync(panel.Open);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-session-panel]")));
        Assert.NotEmpty(cut.FindAll("[data-testid=session-pane]"));
        Assert.Equal("/sessions", cut.Find("[data-testid=code-session-open-area]").GetAttribute("href"));
    }

    [Fact]
    public void The_panel_opened_on_a_workspace_with_no_conversation_says_what_a_session_is()
    {
        // Backlog 22, the second of the two surfaces that can be empty (the first is the Sessions area,
        // held in SessionsUiTests). Opening the panel for the first time is where a user meets the agent,
        // so it is where the trust question gets answered: a copy of the repository, every change asks,
        // and Git history untouched. The panel used to open onto the bare words "No sessions yet."
        using var ctx = PageContext();
        ctx.Services.GetRequiredService<Fravashi.Infrastructure.Bridge.WorkspaceSelection>().Capture("w1");
        ctx.Services.GetRequiredService<FakeWorkspaceAgentSync>().CopyPath = "/workspaces/w1";
        var panel = ctx.Services.GetRequiredService<AgentPanelState>();
        var cut = ctx.Render<CodePage>();

        cut.InvokeAsync(panel.Open);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=session-empty]")));
        var empty = cut.Find("[data-testid=session-empty]").TextContent;
        Assert.Contains("copy of this repository", empty, StringComparison.Ordinal);
        Assert.Contains("Edits only what you approve", empty, StringComparison.Ordinal);
        Assert.Contains("Git history stays untouched", empty, StringComparison.Ordinal);
        Assert.Single(cut.FindAll("[data-testid=session-empty-start]"));
        Assert.DoesNotContain("No sessions yet", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void A_session_started_from_the_panel_runs_in_this_workspaces_contained_copy()
    {
        using var ctx = PageContext();
        ctx.Services.GetRequiredService<Fravashi.Infrastructure.Bridge.WorkspaceSelection>().Capture("w1");
        ctx.Services.GetRequiredService<FakeWorkspaceAgentSync>().CopyPath = "/workspaces/w1";
        var runner = ctx.Services.GetRequiredService<RecordingSessionRunner>();
        var panel = ctx.Services.GetRequiredService<AgentPanelState>();
        var cut = ctx.Render<CodePage>();
        cut.InvokeAsync(panel.Open);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=composer-input]")));

        cut.Find("[data-testid=composer-input]").Input("explain this project");
        cut.Find("[data-testid=send-prompt]").Click();

        // The session starts on a background pump (LiveSessionRegistry runs it on a dedicated
        // LongRunning thread, off the thread pool, precisely so a busy machine cannot leave it queued),
        // so this awaits the real condition (LastRequest is set as the runner's first statement) rather
        // than asserting immediately. The generous cap is for a loaded machine running the whole
        // solution at once (backlog 107, matched to the AgentSync family's 15s budget); do not shorten.
        cut.WaitForAssertion(() => Assert.NotNull(runner.LastRequest), TimeSpan.FromSeconds(15));
        Assert.Equal("w1", runner.LastRequest!.WorkspaceId);
        Assert.Equal("/workspaces/w1", runner.LastRequest.WorkingDirectory);
        Assert.Equal("explain this project", runner.LastRequest.Prompt);
    }

    [Fact]
    public void Without_a_contained_copy_the_panel_refuses_to_run_an_agent()
    {
        // Containment is absolute (plan 2d decision 2): the host tree is never an agent's cwd.
        using var ctx = PageContext();
        ctx.Services.GetRequiredService<Fravashi.Infrastructure.Bridge.WorkspaceSelection>().Capture("w1");
        var panel = ctx.Services.GetRequiredService<AgentPanelState>();
        var cut = ctx.Render<CodePage>();

        cut.InvokeAsync(panel.Open);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=session-no-copy]")));
        Assert.Empty(cut.FindAll("[data-testid=composer-input]"));
    }

    [Fact]
    public void Closing_the_agent_panel_gives_the_editor_its_width_back()
    {
        using var ctx = PageContext();
        var panel = ctx.Services.GetRequiredService<AgentPanelState>();
        var cut = ctx.Render<CodePage>();
        cut.InvokeAsync(panel.Open);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-session-close]")));

        cut.InvokeAsync(() => cut.Find("[data-testid=code-session-close]").Click());

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=code-session-panel]")));
        Assert.Empty(cut.FindAll(".code-body-split.with-session"));
    }

    // ---- the panel is driven from outside the page that draws it (backlog 23) ------------------

    [Fact]
    public void The_page_announces_the_panel_so_chrome_outside_it_has_something_to_toggle()
    {
        using var ctx = PageContext();
        var panel = ctx.Services.GetRequiredService<AgentPanelState>();
        Assert.False(panel.IsAvailable);

        var cut = ctx.Render<CodePage>();

        cut.WaitForAssertion(() => Assert.True(panel.IsAvailable));
    }

    [Fact]
    public void A_control_outside_the_page_opens_and_closes_the_agent_panel()
    {
        using var ctx = PageContext();
        ctx.Services.GetRequiredService<Fravashi.Infrastructure.Bridge.WorkspaceSelection>().Capture("w1");
        ctx.Services.GetRequiredService<FakeWorkspaceAgentSync>().CopyPath = "/workspaces/w1";
        var panel = ctx.Services.GetRequiredService<AgentPanelState>();
        var cut = ctx.Render<CodePage>();

        cut.InvokeAsync(panel.Toggle);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-session-panel]")));

        cut.InvokeAsync(panel.Toggle);

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=code-session-panel]")));
    }

    [Fact]
    public void Opening_the_panel_from_outside_still_warms_the_contained_copy()
    {
        // Whoever opens the panel, the copy an agent would run in has to be syncing before the
        // composer appears; the warming moved with the state and is not the opener's business.
        using var ctx = PageContext();
        ctx.Services.GetRequiredService<Fravashi.Infrastructure.Bridge.WorkspaceSelection>().Capture("w1");
        var sync = ctx.Services.GetRequiredService<FakeWorkspaceAgentSync>();
        sync.CopyPath = "/workspaces/w1";
        var panel = ctx.Services.GetRequiredService<AgentPanelState>();
        var cut = ctx.Render<CodePage>();
        sync.Started.Clear();

        cut.InvokeAsync(panel.Open);

        cut.WaitForAssertion(() => Assert.Contains("w1", sync.Started));
    }

    [Fact]
    public void Closing_the_panel_from_its_own_header_is_seen_by_the_control_outside()
    {
        using var ctx = PageContext();
        var panel = ctx.Services.GetRequiredService<AgentPanelState>();
        var cut = ctx.Render<CodePage>();
        cut.InvokeAsync(panel.Open);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=code-session-close]")));

        cut.InvokeAsync(() => cut.Find("[data-testid=code-session-close]").Click());

        cut.WaitForAssertion(() => Assert.False(panel.IsOpen));
    }}
