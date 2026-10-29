using Fravashi.Application.Evidence;
using Fravashi.Application.Sessions;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fravashi.Infrastructure.Evidence;

/// <summary>
/// Wires the evidence lifecycle (backlog 75). Register AFTER <c>AddAgentSessions</c>: the lifecycle asks the
/// live session registry whether a session is running and forgets conversations through it, so a list that is
/// open on another surface drops a deleted conversation at once.
/// </summary>
public static class EvidenceServiceCollectionExtensions
{
    public static IServiceCollection AddEvidenceLifecycle(this IServiceCollection services)
    {
        services.AddSingleton<IEvidenceLifecycle>(sp =>
        {
            var registry = sp.GetRequiredService<LiveSessionRegistry>();
            return new EvidenceLifecycle(
                sp.GetRequiredService<DataDirectory>(),
                sp.GetRequiredService<ISqliteConnectionFactory>(),
                sp.GetRequiredService<ISessionStore>(),
                sp.GetRequiredService<CliTranscriptScrubber>(),
                anySessionRunning: () => registry.Sessions.Any(s => s.IsRunning),
                forgetSession: registry.Delete,
                logger: sp.GetService<ILogger<EvidenceLifecycle>>());
        });

        return services;
    }
}
