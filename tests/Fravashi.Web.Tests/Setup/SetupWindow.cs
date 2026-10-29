using Bunit;
using Fravashi.Application.Git;
using Fravashi.Application.Migration;
using Fravashi.Application.Persistence;
using Fravashi.Application.Projects;
using Fravashi.Application.Sessions;
using Fravashi.Application.Setup;
using Fravashi.Application.Wiki;
using Fravashi.Application.WorkItems;
using Fravashi.Infrastructure.Bridge;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Repository;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.Wiki;
using Fravashi.Infrastructure.WorkItems;
using Fravashi.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Fravashi.Web.Tests.Setup;

/// <summary>
/// A window on a folder opened for the first time (item #373), over the REAL stores in a temporary SQLite file and a
/// temporary folder on disk for the import to scan. Nothing is a stand-in except git, which a test gives when it wants
/// the Source step to see a repository.
/// </summary>
internal sealed class SetupWindow : IDisposable
{
    public const string FolderId = "ws-core";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fravashi-setup-" + Guid.NewGuid().ToString("N"));

    public SetupWindow(string folderName = "Stomme", string? formerProjectId = null)
    {
        Directory.CreateDirectory(_directory);
        Repository = Path.Combine(_directory, "repo");
        Directory.CreateDirectory(Repository);
        Factory = new SqliteConnectionFactory(new SqliteStoreOptions(Path.Combine(_directory, "test.db"), null, Pooling: false));
        SqliteSchema.EnsureCreated(Factory);
        Workspaces = new SqliteWorkspaceStore(Factory);
        Workspaces.Touch(FolderId, folderName, Repository);
        if (formerProjectId is not null)
        {
            using var connection = Factory.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE workspaces SET former_project_id = $f WHERE id = $id;";
            command.Parameters.AddWithValue("$f", formerProjectId);
            command.Parameters.AddWithValue("$id", FolderId);
            command.ExecuteNonQuery();
        }

        Projects = new SqliteProjectStore(Factory, TimeProvider.System);
        Settings = new SqliteConfigurationStore(Factory);
        Providers = new SqliteProjectProviders(Factory);
        Trust = new SqliteWorkspaceTrust(Factory);
        Drafts = new SetupDraftStore(Settings);
        Trackers = new LocalTrackerResolver(Factory, TimeProvider.System);
        Wikis = new SqliteWikiLibraries(Factory, TimeProvider.System);
        Binding = new WorkspaceProjectBinding(Projects, Workspaces, id => RunningIn.Contains(id));
        Setup = new FolderSetup(Projects, Workspaces, Binding, Providers, Trust, Settings, Drafts, new RepositoryImport(Trackers, Wikis));
    }

    public string Repository { get; }

    public SqliteConnectionFactory Factory { get; }

    public SqliteWorkspaceStore Workspaces { get; }

    public SqliteProjectStore Projects { get; }

    public SqliteConfigurationStore Settings { get; }

    public SqliteProjectProviders Providers { get; }

    public SqliteWorkspaceTrust Trust { get; }

    public SetupDraftStore Drafts { get; }

    public LocalTrackerResolver Trackers { get; }

    public SqliteWikiLibraries Wikis { get; }

    public WorkspaceProjectBinding Binding { get; }

    public FolderSetup Setup { get; }

    /// <summary>Folders with a conversation running, which the binding refuses to move.</summary>
    public HashSet<string> RunningIn { get; } = [];

    /// <summary>The scan's source; null is a walk of <see cref="Repository"/>.</summary>
    public IRepositoryScans? Scans { get; set; }

    /// <summary>Git for the Source step; null is "git cannot be reached from this window".</summary>
    public IWorkspaceGit? Git { get; set; }

    /// <summary>Writes a file into the folder the import scans.</summary>
    public void Write(string relative, string text)
    {
        var path = Path.Combine(Repository, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>The bunit context of the window, navigated to <paramref name="url"/>.</summary>
    public BunitContext Context(string url = "/setup")
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [BridgeWorkspaceContext.UrlKey] = "http://127.0.0.1:9",
            [BridgeWorkspaceContext.TokenKey] = "t",
            [BridgeWorkspaceContext.ActiveIdKey] = FolderId,
        }).Build();
        var workspace = new BridgeWorkspaceContext(configuration);
        ctx.Services.AddSingleton(workspace);
        ctx.Services.AddSingleton<IWorkspaceStore>(Workspaces);
        ctx.Services.AddSingleton<ICurrentProject>(new WorkspaceCurrentProject(workspace, Workspaces));
        ctx.Services.AddSingleton<IProjectStore>(Projects);
        ctx.Services.AddSingleton<IConfigurationStore>(Settings);
        ctx.Services.AddSingleton<IProviderConnectionStore>(new SqliteProviderConnectionStore(Factory));
        ctx.Services.AddSingleton<IProjectProviders>(Providers);
        ctx.Services.AddSingleton<IWorkspaceTrust>(Trust);
        ctx.Services.AddSingleton<ISetupDraftStore>(Drafts);
        ctx.Services.AddSingleton<IProjectContents>(new SqliteProjectContents(Factory));
        ctx.Services.AddSingleton<ITrackerResolver>(Trackers);
        ctx.Services.AddSingleton<IWikiLibraries>(Wikis);
        ctx.Services.AddSingleton(Binding);
        ctx.Services.AddSingleton(Setup);
        ctx.Services.AddSingleton(Scans ?? new FolderScans(Repository));
        ctx.Services.AddScoped<SetupScan>();
        if (Git is not null)
        {
            ctx.Services.AddSingleton(Git);
        }

        ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().NavigateTo(url);
        return ctx;
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
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The scan over the temporary folder, walked (no git), as a folder that is not a repository is.</summary>
    private sealed class FolderScans(string root) : IRepositoryScans
    {
        public Task<RepositoryScan?> ForWindowAsync(CancellationToken ct = default) =>
            Task.FromResult<RepositoryScan?>(new RepositoryScan(new LocalRepositoryFiles(root, null)));
    }
}
