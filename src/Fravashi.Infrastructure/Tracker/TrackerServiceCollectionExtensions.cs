using Fravashi.Application.Network;
using Fravashi.Application.Wiki;
using Fravashi.Infrastructure.Tracker.Atlassian;
using Fravashi.Infrastructure.Wiki;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Fravashi.Infrastructure.Tracker;

/// <summary>
/// Registers the Atlassian Confluence reader used by wiki sync. It binds the shared <c>Atlassian</c> config
/// section; an unconfigured site still resolves - the reader simply reports <c>IsConfigured = false</c>.
/// </summary>
public static class TrackerServiceCollectionExtensions
{
    public static IServiceCollection AddTrackerIntegration(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AtlassianConfig>(configuration.GetSection("Atlassian"));

        services.AddHttpClient<AtlassianConfluenceReader>();

        services.AddScoped<IConfluenceReader>(sp => sp.GetRequiredService<AtlassianConfluenceReader>());

        // Backlog 83: the write half. The publisher is scoped because its gateway is a typed HttpClient; the
        // publication store it writes through is a singleton registered with the rest of the persistence.
        services.AddHttpClient<AtlassianConfluencePageGateway>();
        // Backlog 144: an Azure DevOps wiki beside Confluence, the configured one chosen at each call.
        services.AddHttpClient<AzureDevOpsWikiPageGateway>();
        services.AddScoped<IWikiPageGateway, ConfiguredWikiPageGateway>();

        // Backlog 47: the Jira adapter for the provider port, and the two acts that start the machinery item 82
        // built (send an item, sync now). Scoped because the adapter is a typed HttpClient. Which provider a
        // project follows is answered by IProviderGateways and nowhere else.
        services.AddHttpClient<AtlassianJiraGateway>();

        // Backlog 143: Azure DevOps Boards, read from what the board step saved, at each use.
        services.AddScoped<AzureDevOps.AzureDevOpsBoardConnection>();
        services.AddHttpClient<AzureDevOps.AzureDevOpsBoardsGateway>();
        services.AddScoped<Fravashi.Application.Mirroring.IProviderGateways, ConfiguredProviderGateways>();

        // Backlog 232: one outage record for the process, so the board's sync, the wiki's publishing and the wiki
        // mirror's pull name the same "unreachable since" in the same words.
        services.TryAddSingleton(sp => new ProviderOutages(sp.GetRequiredService<TimeProvider>()));
        services.AddScoped(sp => new Fravashi.Application.Mirroring.ProviderSync(
            sp.GetRequiredService<Fravashi.Application.Mirroring.IProviderGateways>(),
            sp.GetRequiredService<Fravashi.Application.WorkItems.ITrackerResolver>(),
            sp.GetRequiredService<Fravashi.Application.Mirroring.IOutboxStore>(),
            sp.GetRequiredService<Fravashi.Application.Mirroring.IMirrorStore>(),
            sp.GetRequiredService<Fravashi.Application.Mirroring.OutboxDrain>(),
            sp.GetRequiredService<Fravashi.Application.Mirroring.ReconcilePass>(),
            sp.GetRequiredService<TimeProvider>(),
            newId: null,
            // The SAME reader the reconcile pass takes (backlog 47/48), from the one registration, so the
            // two sides cannot come to disagree about what this board's statuses are called.
            statuses: sp.GetRequiredService<Func<Fravashi.Application.Mirroring.ProviderStatusMap>>(),
            outages: sp.GetRequiredService<ProviderOutages>()));
        services.AddSingleton<IWikiPublishTarget>(sp => new ConfiguredWikiPublishTarget(sp.GetRequiredService<IConfiguration>()));
        services.AddScoped(sp => new WikiPublisher(
            sp.GetRequiredService<IWikiDocuments>(),
            sp.GetRequiredService<IWikiPublicationStore>(),
            sp.GetRequiredService<IWikiPageGateway>(),
            sp.GetRequiredService<IWikiPublishTarget>(),
            sp.GetRequiredService<TimeProvider>(),
            newId: null,
            // Backlog 142: a project that chose no wiki is refused here too, not only hidden in the page.
            choices: sp.GetService<Fravashi.Application.Projects.IProjectProviders>(),
            outages: sp.GetRequiredService<ProviderOutages>()));

        return services;
    }
}
