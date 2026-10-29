using Fravashi.Application.Onboarding;
using Microsoft.Extensions.DependencyInjection;

namespace Fravashi.Infrastructure.Onboarding;

/// <summary>
/// Registers the onboarding access probes. Each probe gets its own <see cref="HttpClient"/> (via
/// <c>AddHttpClient</c>); the git-based probes share a single <see cref="GitLsRemoteRunner"/>. The probes
/// take credentials per call (onboarding has not yet persisted or reloaded configuration when they run), so
/// they set authorization per request rather than from <c>IOptions</c>.
/// </summary>
public static class OnboardingServiceCollectionExtensions
{
    public static IServiceCollection AddOnboardingProbes(this IServiceCollection services)
    {
        services.AddSingleton<GitLsRemoteRunner>();
        services.AddSingleton<GitWikiRoundTripRunner>();
        services.AddHttpClient<IWikiAccessProbe, WikiAccessProbe>();
        services.AddHttpClient<IBoardAccessProbe, BoardAccessProbe>();
        services.AddHttpClient<IBoardStatesProbe, BoardStatesProbe>();
        return services;
    }
}
