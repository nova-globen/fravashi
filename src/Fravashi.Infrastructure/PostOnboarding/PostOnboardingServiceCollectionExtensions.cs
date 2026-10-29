using Fravashi.Application.Git;
using Fravashi.Application.Wiki;
using Fravashi.Infrastructure.Git;
using Fravashi.Infrastructure.Wiki;
using Microsoft.Extensions.DependencyInjection;

namespace Fravashi.Infrastructure.PostOnboarding;

/// <summary>
/// Registers the F2 post-onboarding services (ADR 0005 / vision O3-O10): the git-process seam, the
/// wiki source resolver + DB-backed sync engine, and the on-start trigger. Call after persistence
/// and the tracker integration (needs the SQLite stores, <c>IConfluenceReader</c>, and
/// <c>INotificationHistory</c>). (The repository clone service and its <c>RepoWorkspace</c>
/// retired with the in-container clone model, plan 2c decision 6.)
/// </summary>
public static class PostOnboardingServiceCollectionExtensions
{
    public static IServiceCollection AddPostOnboardingSync(this IServiceCollection services)
    {
        // IHttpClientFactory for the generic HTTP wiki source (idempotent, safe if already added).
        services.AddHttpClient();

        // The git seam (the wiki round-trip sources shell out through it).
        services.AddSingleton<IGitProcessRunner, GitProcessRunner>();

        // Wiki: the resolver is scoped (it wraps the scoped Confluence reader); the sync engine is a singleton
        // that opens a scope per pull.
        services.AddScoped<IWikiSourceResolver, WikiSourceResolver>();
        services.AddSingleton<IWikiSyncService, WikiSyncService>();

        // The on-start trigger.
        services.AddHostedService<PostOnboardingTasksService>();
        return services;
    }
}
