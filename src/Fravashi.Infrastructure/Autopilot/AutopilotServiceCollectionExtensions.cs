using Fravashi.Application.Autopilot;
using Fravashi.Application.Git;
using Fravashi.Application.Persistence;
using Fravashi.Application.Sessions;
using Fravashi.Infrastructure.Ci;
using Fravashi.Infrastructure.Git;
using Fravashi.Infrastructure.Sessions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace Fravashi.Infrastructure.Autopilot;

/// <summary>
/// Wires the autopilot's real seams and the host that ticks them (#309). Must run after
/// <c>SessionsServiceCollectionExtensions.AddAgentSessions</c> (it needs <see cref="LiveSessionRegistry"/>,
/// <see cref="IAgentApprovalBroker"/> and the already-registered <see cref="IAutopilotOperator"/>) and after
/// the persistence registrations that provide <see cref="IAutopilotRunStore"/> and
/// <c>IWorkspaceStore</c>.
/// </summary>
public static class AutopilotServiceCollectionExtensions
{
    public static IServiceCollection AddAutopilotEngine(this IServiceCollection services)
    {
        // Process-local session tracking and captured commit reviews (never persisted: a restart stops any
        // run whose current item was mid-Building, -Reviewing, -Delivering or -Verifying before this could
        // ever be read again).
        services.AddSingleton<AutopilotBuilderSessions>();

        // Backlog 307: the concurrency cap's Settings value, the memory read, and the gate that combines them
        // with a dispatch's own (lowering-only) override. Registered before the builder, which consults the
        // gate right before a fresh session starts.
        services.AddSingleton<IAutopilotConcurrencySettings>(
            sp => new ConfigurationAutopilotConcurrencySettings(sp.GetRequiredService<IConfiguration>()));
        // #308: the container's own cgroup figures, read fresh, falling back to the GC estimate (#307) where
        // they cannot be read (a host or container without cgroup v2 memory files, unmeasured here).
        // A factory, never AddSingleton<I, T>: CgroupMemoryAvailability's own fallback parameter is this SAME
        // interface, which the container would otherwise try to resolve from its own single registration.
        services.AddSingleton<IAutopilotMemoryAvailability>(_ => new CgroupMemoryAvailability(new RuntimeMemoryAvailability()));
        services.AddSingleton<IAutopilotConcurrencyGate>(sp => new AutopilotConcurrencyGate(
            sp.GetRequiredService<LiveSessionRegistry>(),
            sp.GetRequiredService<IWorkspaceStore>(),
            sp.GetRequiredService<IAutopilotConcurrencySettings>(),
            sp.GetRequiredService<IAutopilotMemoryAvailability>(),
            sp.GetService<ILogger<AutopilotConcurrencyGate>>()));

        // #308: the host bridge's own worktree primitive (#306), for a parallel run's own per-item isolation.
        services.AddSingleton<IAutopilotWorktrees>(sp => new BridgeAutopilotWorktrees(sp.GetRequiredService<IConfiguration>()));

        services.AddSingleton<IAutopilotBuilder>(sp => new AgentAutopilotBuilder(
            sp.GetRequiredService<LiveSessionRegistry>(),
            sp.GetRequiredService<IAgentApprovalBroker>(),
            sp.GetRequiredService<ISessionWorkItems>(),
            sp.GetRequiredService<ISessionPlans>(),
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>(),
            sp.GetRequiredService<Application.AgentSync.IWorkspaceAgentSync>(),
            sp.GetRequiredService<AutopilotBuilderSessions>(),
            // The same project-to-workspace lookup #300 uses for the operator (@117's stated limitation: the
            // most recently opened workspace bound to the project). Never the one a PARALLEL item's own
            // worktree is made from (#308, commit review's finding): Touch stamps a worktree as the newest
            // thing opened for the project, which would make this very lookup answer the worktree itself on
            // every call after the first. AgentAutopilotBuilder reads AutopilotBuilderContext
            // .RepositoryWorkspaceId (AutopilotRun.BaseWorkspaceId, resolved once at dispatch) for that.
            // Backlog 319: and never a worktree at all, which NewestFolderIn skips.
            project => Application.Projects.WorkspaceProjects.NewestFolderIn(sp.GetRequiredService<IWorkspaceStore>(), project),
            sp.GetRequiredService<IAutopilotConcurrencyGate>(),
            sp.GetRequiredService<IAutopilotWorktrees>(),
            sp.GetRequiredService<IWorkspaceStore>(),
            // Backlog 332: told to a resumed builder at its first turn, when its worktree is not clean.
            sp.GetService<IWorkspaceGit>(),
            sp.GetRequiredService<ISessionBranches>(),
            logger: sp.GetService<ILogger<AgentAutopilotBuilder>>(),
            itemStart: sp.GetRequiredService<IAutopilotItemStart>(),
            attemptBases: sp.GetRequiredService<ISessionAttemptBases>(),
            // Work item #357: the autopilot builder's default model and effort, read from Settings at each start.
            builderDefaults: () => sp.GetService<IConfigurationStore>() is { } config
                ? AgentDefaultsSetting.Read(config).AutopilotBuilder
                : AgentDefault.None));

        services.AddSingleton<IAutopilotCommitReviews>(sp => new AutopilotCommitReviews(
            sp.GetRequiredService<AutopilotBuilderSessions>()));

        services.AddSingleton<IAutopilotDelivery>(sp => new AgentAutopilotDelivery(
            sp.GetRequiredService<AutopilotBuilderSessions>(),
            sp.GetRequiredService<SessionGit>(),
            sp.GetRequiredService<ISessionBranches>(),
            sp.GetRequiredService<Application.Projects.IProjectDelivery>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<Func<GitHostAccess>>(),
            sp.GetRequiredService<ForgeRuns>(),
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>(),
            sp.GetRequiredService<Application.AgentSync.IWorkspaceAgentSync>(),
            sp.GetRequiredService<Application.WorkItems.ScenarioRunner>()));

        // Backlog 310: the dispatch's own dirty check, and returning the workspace to the branch its run was
        // dispatched from once the run ends. ONE instance behind both seams, registered once, so the dirty
        // check resolves a project's workspace through the same lookup the builder and operator seams already
        // use. The lookup answers "the most recently opened workspace", which can change while a run is open
        // (a person can open a different folder bound to the same project), so the RETURN never re-asks it:
        // CheckAsync hands back the resolved id in AutopilotWorkspaceState.WorkspaceId, the dispatcher records
        // it on the run alongside BaseBranch, and ReturnAsync takes that exact id (commit review #310, third
        // pass), never a project to re-resolve.
        services.AddSingleton(sp => new GitAutopilotWorkspaceReturn(
            project => Application.Projects.WorkspaceProjects.NewestFolderIn(
                sp.GetRequiredService<Application.Persistence.IWorkspaceStore>(), project),
            sp.GetRequiredService<IWorkspaceGit>(),
            sp.GetRequiredService<Application.AgentSync.IWorkspaceAgentSync>()));
        services.AddSingleton<IAutopilotWorkspaceStatus>(sp => sp.GetRequiredService<GitAutopilotWorkspaceReturn>());
        services.AddSingleton<IAutopilotWorkspaceReturn>(sp => sp.GetRequiredService<GitAutopilotWorkspaceReturn>());
        // #315: the same instance puts the shared workspace back on the run's base branch between items.
        services.AddSingleton<IAutopilotItemStart>(sp => sp.GetRequiredService<GitAutopilotWorkspaceReturn>());

        services.AddSingleton(sp => new AutopilotEngine(
            sp.GetRequiredService<IAutopilotOperator>(),
            sp.GetRequiredService<IAutopilotBuilder>(),
            sp.GetRequiredService<IAutopilotCommitReviews>(),
            sp.GetRequiredService<IAutopilotDelivery>(),
            sp.GetRequiredService<IAutopilotRunStore>(),
            sp.GetService<TimeProvider>(),
            sp.GetRequiredService<IAutopilotWorkspaceReturn>()));

        services.AddSingleton<AutopilotTickGate>();
        services.AddHostedService<AutopilotEngineHost>();

        return services;
    }
}
