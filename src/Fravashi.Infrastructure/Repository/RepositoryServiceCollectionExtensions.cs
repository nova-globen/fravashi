using Fravashi.Application.Repository;
using Fravashi.Infrastructure.Bridge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Fravashi.Infrastructure.Repository;

/// <summary>
/// DI for the repository seams. Since plan 2b-i each interface is a MODE-CHECKED factory: the
/// bridge arm serves an active host workspace (<see cref="BridgeWorkspaceContext.IsBridgeActive"/>),
/// the local arm is the container clone exactly as before. Both arms register as concrete types so
/// either resolves; the factories run per circuit, so a mode change applies to new circuits (the
/// workspace UX in 2b-ii reloads the page on switch). The write gate stays process-wide (ADR 0016),
/// and the watcher stays a concrete singleton behind the dispatching notifier.
/// </summary>
public static class RepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddRepositoryExplorer(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RepositoryReaderOptions>(configuration.GetSection("Repository"));

        services.AddScoped<LocalRepositoryReader>();
        services.AddScoped<LocalRepositoryWriter>();
        services.AddScoped<RepositoryTrash>();
        services.AddScoped<Search.RepositorySearchService>();

        services.AddScoped<IRepositoryReader>(sp =>
            sp.GetRequiredService<BridgeWorkspaceContext>().IsBridgeActive
                ? sp.GetRequiredService<BridgeRepositoryReader>()
                : sp.GetRequiredService<LocalRepositoryReader>());
        services.AddScoped<IRepositoryWriter>(sp =>
            sp.GetRequiredService<BridgeWorkspaceContext>().IsBridgeActive
                ? sp.GetRequiredService<BridgeRepositoryWriter>()
                : sp.GetRequiredService<LocalRepositoryWriter>());
        services.AddScoped<IRepositoryTrash>(sp =>
            sp.GetRequiredService<BridgeWorkspaceContext>().IsBridgeActive
                ? sp.GetRequiredService<BridgeRepositoryTrash>()
                : sp.GetRequiredService<RepositoryTrash>());
        services.AddScoped<Application.Search.IRepositorySearchService>(sp =>
            sp.GetRequiredService<BridgeWorkspaceContext>().IsBridgeActive
                ? sp.GetRequiredService<BridgeSearchAdapter>()
                : sp.GetRequiredService<Search.RepositorySearchService>());

        // Item #373: the setup's import scan, for the folder this window shows.
        services.AddScoped<Application.Migration.IRepositoryScans, WindowRepositoryScans>();

        services.AddSingleton<IRepositoryWriteGate, RepositoryWriteGate>();

        services.AddSingleton<RepositoryChangeWatcher>();
        // Backlog 261: the notifier is scoped so it follows the circuit's workspace; the per-workspace streams live
        // in the singleton registry so they outlive circuits and are shared between windows on one workspace.
        services.AddSingleton<BridgeChangeSubscribers>();
        services.AddScoped<IRepositoryChangeNotifier, WorkspaceChangeNotifier>();
        return services;
    }
}
