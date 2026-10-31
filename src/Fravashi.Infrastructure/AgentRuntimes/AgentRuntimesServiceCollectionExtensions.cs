using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.AgentRuntimes.Broker;
using Fravashi.Application.Persistence;
using Fravashi.Application.Variants;
using Fravashi.Infrastructure.AgentRuntimes.Broker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fravashi.Infrastructure.AgentRuntimes;

/// <summary>
/// Registers the agent-runtime services (N1 - N3, S10 - S16): the process command runner, the runtime status
/// service, the headless-credential store, the update-on-launch hosted service, the interactive login proxy,
/// and the board-mapping suggester. Call after persistence (needs <c>ISecretStore</c> / <c>IConfigurationStore</c>).
/// </summary>
public static class AgentRuntimesServiceCollectionExtensions
{
    public static IServiceCollection AddAgentRuntimes(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAgentCommandRunner, ProcessAgentCommandRunner>();

        // Remote credential broker (ADR 0010): bind the client-side broker switch from the `Broker` config section,
        // then fold in the publish-time variant (B4). In the `corp-broker` variant the compiled/image
        // FeatureFlags.BrokerRequired flag forces brokering ON and forbids the local org-key path - so a corp-broker
        // image brokers (and never injects the org key) even if the operator never set Broker:Enabled, while the
        // BaseUrl still comes from deploy config. Off-variant (or when IFeatureFlags is not registered, as in some
        // Infrastructure tests) the config-bound options stand unchanged - the single-user default. The credential
        // store is built through a factory so the composed options reach it without the DI container choosing
        // between the store's constructors.
        services.Configure<BrokerOptions>(configuration.GetSection("Broker"));
        services.AddSingleton(sp =>
        {
            var configured = sp.GetRequiredService<IOptions<BrokerOptions>>().Value;
            var brokerRequired = sp.GetService<IFeatureFlags>()?.Flags.BrokerRequired ?? false;
            // with no broker of its own, an install uses the free tier at its build's service host.
            return configured.ForVariant(brokerRequired).WithFreeTier(Fravashi.Domain.ServiceHost.BrokerBaseUrl);
        });
        services.AddSingleton<IAgentRuntimeCredentialStore>(sp =>
            new AgentRuntimeCredentialStore(
                sp.GetRequiredService<ISecretStore>(),
                sp.GetRequiredService<BrokerOptions>()));

        // Broker token client (B3): mint/refresh/revoke the per-user broker token from the SSO identity. The
        // typed HttpClient talks to the configured broker; the background refresher re-mints before the ~1h TTL
        // lapses. Registered unconditionally but tier-gated at its core (a no-op when BrokerOptions.IsActive is
        // false), exactly like the SSO token refresher - the single-user default spins nothing up.
        services.TryAddSingleton(TimeProvider.System);

        // Backlog 235: one record per process of a broker outage (with its backoff) or a refusal (with the identity it
        // refused). The typed client below is built per use, so this is what carries them from one attempt to the next,
        // and what the refresher, the conversations waiting for a token and the Settings card all read.
        services.TryAddSingleton(sp => new BrokerTokenHealth(sp.GetRequiredService<TimeProvider>()));
        services.AddHttpClient<IBrokerTokenService, BrokerTokenService>();

        // Backlog 377: this person's free-tier usage, as the broker counts it, asked only while the broker carries the CLI.
        services.AddHttpClient<IBrokerUsageReader, BrokerUsageReader>((http, sp) =>
        {
            var credentials = sp.GetService<IAgentRuntimeCredentialStore>();
            return new BrokerUsageReader(
                sp.GetRequiredService<ISecretStore>(),
                sp.GetService<BrokerOptions>() ?? BrokerOptions.Disabled,
                http,
                () => credentials?.BrokerCarries(Fravashi.Domain.AgentSessions.AgentTool.Claude) == true);
        });
        services.AddHostedService<BrokerTokenRefreshHostedService>();

        services.AddSingleton<IAgentRuntimeService, ProcessAgentRuntimeService>();
        services.AddSingleton<IAgentRuntimeStatusReport, InMemoryAgentRuntimeStatusReport>();
        services.AddSingleton<IAgentLoginPipeFactory, ProcessLoginPipeFactory>();
        services.AddSingleton<IAgentLoginProxy, ProcessAgentLoginProxy>();
        // Plan 2d decision 10: the per-project Node PATH lookup (nvs layout on the data volume).
        services.AddSingleton<NodePathProvisioner>();
        // Backlog 10: the on-demand install behind the session pane's banner. The typed client fetches from
        // nodejs.org/dist (or Node:DistributionBaseUrl); a tarball is tens of MB, so the client timeout is
        // the whole download's budget, not a request's. Platform is detected once: outside a Linux container
        // the provisioner refuses with that reason instead of fetching something that cannot run here.
        services.AddHttpClient<INodeProvisioner, NodeProvisioner>((http, sp) =>
        {
            http.Timeout = TimeSpan.FromMinutes(10);
            return new NodeProvisioner(
                http,
                sp.GetRequiredService<NodePathProvisioner>().NvsHome,
                NodeDistributionPlatform.Detect(),
                configuration[NodeProvisioner.BaseUrlKey],
                sp.GetService<ILogger<NodeProvisioner>>());
        });
        services.AddSingleton<NodeProvisionCoordinator>();
        services.AddSingleton<WorkspaceNodeAdvisor>();
        services.AddSingleton<IBoardMappingSuggester, CliBoardMappingSuggester>();

        // Backlog 40: a document's summary, written on demand by an agent run with no tools at all.
        services.AddSingleton<Application.Wiki.IWikiSummaryWriter, Wiki.CliWikiSummaryWriter>();
        services.AddSingleton(sp => new Application.Wiki.WikiSummariser(
            sp.GetRequiredService<Application.Wiki.IWikiDocuments>(),
            sp.GetRequiredService<Application.Wiki.IWikiSummaryWriter>(),
            sp.GetRequiredService<Application.Wiki.IWikiSummaryStore>(),
            sp.GetRequiredService<TimeProvider>()));

        // Update-on-launch (N1/N2/S11): refreshes every embedded CLI at container start, in the background,
        // gated to in-container runs so a developer's host CLIs are never touched.
        services.AddHostedService<AgentRuntimeUpdateService>();
        return services;
    }
}
