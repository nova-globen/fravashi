using Fravashi.Application.AgentSync;
using Fravashi.Application.Notifications;
using Fravashi.Application.Persistence;
using Fravashi.Application.Updates;
using Fravashi.Application.Wiki;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Bridge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Fravashi.Web.Tests;

/// <summary>Registers no-op update services so a page that renders the update surfaces (Home) can be tested.</summary>
internal static class UpdateTestDoubles
{
    public static void AddNoUpdates(this IServiceCollection services, ProductUpdateState? state = null)
    {
        services.AddSingleton<IToolUpdateService>(new StubToolUpdateService());
        services.AddSingleton<IProductUpdateStatus>(new StubProductUpdateStatus(state ?? ProductUpdateState.None));
        services.AddSingleton<IProductInfo>(new StubProductInfo());
        services.AddSingleton<IWikiSyncService>(new StubWikiSyncService());
    }

    /// <summary>
    /// The workspace/bridge seams the merged home surface injects (backlog 33), all unconfigured:
    /// no bridge coordinates, no active workspace, an empty recents store.
    /// </summary>
    public static void AddUnboundHomeSurface(this IServiceCollection services)
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        var context = new BridgeWorkspaceContext(configuration);
        services.AddSingleton<IWorkspaceStore>(new EmptyWorkspaceStore());
        services.AddSingleton(context);
        services.AddSingleton(new BridgeClient(new HttpClient(), context));
        services.AddLogging();
        // Backlog 34: the home surface also carries the recent-conversations card, so its seams are
        // part of rendering Home at all.
        services.AddSessionSurface();
        // Backlog 36: and the four insight cards, which read a clock, the product-event store and git.
        services.AddInsightCards();
        // Backlog 304: the waiting-for-you panel, part of rendering Home at all.
        services.AddSingleton<IWaitingForPersonList>(new StubWaitingForPersonList());
        // Work item #350: the conversation box at the top of Home starts a session in the folder's contained copy.
        services.AddSingleton<IWorkspaceAgentSync>(new StubWorkspaceAgentSync());
        // Work item #359: the box shows the studio assistant's thread inline, and a transcript renders markdown.
        services.AddSingleton(new Fravashi.Infrastructure.Rendering.MarkdownPreviewRenderer());
        // Work item #360: Home reads and saves its card layout in the settings table. A test that drives the layout
        // registers its own store first and keeps it.
        services.TryAddSingleton<IConfigurationStore>(new FakeConfigurationStore());
    }
}

/// <summary>Work item #350: a folder whose contained copy is ready at a fixed path, for Home's conversation box.</summary>
internal sealed class StubWorkspaceAgentSync(string? copyPath = "/workspaces/test") : IWorkspaceAgentSync
{
    public List<string> Started { get; } = [];

    public void EnsureStarted(string workspaceId) => Started.Add(workspaceId);

    public string? CopyPathFor(string workspaceId) => copyPath;

    public object? StatusFor(string workspaceId) => null;
}

/// <summary>Backlog 304: nothing waiting, for a page that renders the panel or the notification bell but is not testing it.</summary>
internal sealed class StubWaitingForPersonList : IWaitingForPersonList
{
    public Task<IReadOnlyList<WaitingForPersonEntry>> CurrentAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<WaitingForPersonEntry>>([]);
}

internal sealed class EmptyWorkspaceStore : IWorkspaceStore
{
    public IReadOnlyList<WorkspaceRecord> Recent(int max = 10) => [];
    public WorkspaceRecord? Get(string id) => null;
    public void Touch(string id, string name, string rootPath) { }
    public void Remove(string id) { }
}

internal sealed class StubWikiSyncService : IWikiSyncService
{
    public event Action? Changed { add { } remove { } }
    public WikiSyncState State => WikiSyncState.Empty;
    public bool IsConfigured => false;
    public Task EnsureStartedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SyncNowAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class StubToolUpdateService : IToolUpdateService
{
    public event Action? Changed { add { } remove { } }
    public DateTimeOffset? LastCheckedUtc => null;
    public bool IsChecking => false;
    public IReadOnlyList<ToolUpdateInfo> Available => [];
    public ToolUpdateProgress? ProgressFor(AgentTool tool) => null;
    public Task<IReadOnlyList<ToolUpdateInfo>> CheckNowAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ToolUpdateInfo>>([]);
    public Task UpdateAsync(AgentTool tool, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class StubProductUpdateStatus(ProductUpdateState state) : IProductUpdateStatus
{
    public ProductUpdateState Current => state;
}

internal sealed class StubProductInfo : IProductInfo
{
    public string Version => "1.0.0";
    public string Variant => "full";
}
