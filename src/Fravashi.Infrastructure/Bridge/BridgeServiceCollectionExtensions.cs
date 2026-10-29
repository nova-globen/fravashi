using Fravashi.Application.AgentSync;
using Fravashi.Infrastructure.AgentSync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fravashi.Infrastructure.Bridge;

/// <summary>
/// DI for the bridge-backed workspace seams (plan 2b-i). Registers the context, the typed HTTP
/// client, and every bridge implementation as its CONCRETE type; the interface selection lives in
/// the repository/git registration factories, which pick the bridge arm only when
/// <see cref="BridgeWorkspaceContext.IsBridgeActive"/>.
/// </summary>
public static class BridgeServiceCollectionExtensions
{
    public static IServiceCollection AddBridgeWorkspaces(this IServiceCollection services)
    {
        // Uncaptured default; the WEB host overrides this registration with a factory that
        // captures the initial request's ?ws= query (IHttpContextAccessor lives with ASP.NET,
        // which this library deliberately does not reference).
        services.AddScoped<WorkspaceSelection>();
        services.AddScoped<BridgeWorkspaceContext>();

        // Backlog 121: the project follows the workspace. Scoped like the workspace context it reads, so each
        // window answers for its own folder.
        services.AddScoped<Fravashi.Application.Projects.ICurrentProject, WorkspaceCurrentProject>();
        services.AddHttpClient<BridgeClient>();
        services.AddScoped<BridgeRepositoryReader>();
        services.AddScoped<BridgeRepositoryWriter>();
        services.AddScoped<BridgeRepositoryTrash>();
        services.AddScoped<BridgeSearchAdapter>();
        services.AddScoped<BridgeGitCommandRunner>();
        // Backlog 43: the host CLI's own conversation history for the active workspace, read-only.
        services.AddScoped<Application.Sessions.ICliConversationSource, BridgeCliConversationSource>();
        // Backlog 252: whether the bridge answers from inside the container, asked at start and by Settings >
        // Environment, and said in words (the firewall) when it does not. Silent with no bridge configured.
        services.AddSingleton<IBridgeReachProbe>(_ => new HttpBridgeReachProbe());
        services.AddSingleton(sp => new BridgeReachMonitor(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IBridgeReachProbe>(),
            sp.GetService<Fravashi.Infrastructure.Persistence.DataDirectory>(),
            sp.GetService<ILogger<BridgeReachMonitor>>()));
        services.AddHostedService(sp => new BridgeReachHostedService(
            sp.GetRequiredService<BridgeReachMonitor>(), sp.GetService<ILogger<BridgeReachHostedService>>()));
        // Plan 2d: the contained agents' workspace-copy engine. Singleton (sessions outlive
        // circuits); optional deps resolved leniently so headless test hosts stay wire-light.
        services.AddSingleton<IWorkspaceAgentSync>(sp => new WorkspaceAgentSync(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetService<Fravashi.Application.Notifications.INotificationHistory>(),
            sp.GetService<ILogger<WorkspaceAgentSync>>(),
            // Backlog 108: resolved at each write-back, leniently like the rest, so a host with no credential
            // store simply has nothing to look for.
            () => Fravashi.Infrastructure.Sessions.AgentHeldCredentials.From(
                sp.GetService<Fravashi.Application.AgentRuntimes.IAgentRuntimeCredentialStore>(),
                sp.GetService<Fravashi.Application.Projects.IProjectEnvironment>())));
        return services;
    }
}
