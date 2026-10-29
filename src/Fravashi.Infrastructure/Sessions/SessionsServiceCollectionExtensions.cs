using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.AgentSync;
using Fravashi.Application.Network;
using Fravashi.Application.Sessions;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.AgentRuntimes;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Sessions.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>
/// Wires the agent-session machinery (plan 2e): the approval broker the studio answers tool calls
/// through, and the writer that lays down each session's hook files on the data volume.
/// </summary>
public static class SessionsServiceCollectionExtensions
{
    /// <summary>The model API host the agent CLI calls, which is what an outage is waited out against (backlog 229).</summary>
    public static readonly NetworkEndpoint AgentModelHost = new("api.anthropic.com", 443);

    /// <summary>
    /// What the reachability probe asks about (backlog 229, plan @55): the host the agent CLI really calls. When the
    /// broker carries the runtime that is the broker's (the same test <c>AgentRuntimeCredentialStore</c> uses to point
    /// the CLI there), otherwise the model API's. Probing anything else could say the network is back while the
    /// model call still fails.
    /// </summary>
    public static NetworkEndpoint AgentModelEndpoint(BrokerOptions broker)
    {
        ArgumentNullException.ThrowIfNull(broker);
        return broker.IsActive && Uri.TryCreate(broker.BaseUrl!.Trim(), UriKind.Absolute, out var url) && url.Host.Length > 0
            ? new NetworkEndpoint(url.Host, url.Port)
            : AgentModelHost;
    }

    /// <summary>
    /// The broker token as the live sessions see it (backlog 235), or null for a wiring without the broker client. It
    /// applies only where the broker carries the runtime, by the same test <c>AgentRuntimeCredentialStore</c> uses to
    /// hand the CLI the broker's bearer. The token service is resolved per call (a typed HTTP client must not be held).
    /// </summary>
    private static BrokerCredentialRenewal? BrokerCredentialsFor(IServiceProvider sp)
    {
        var broker = sp.GetService<BrokerOptions>() ?? BrokerOptions.Disabled;
        var health = sp.GetService<Application.AgentRuntimes.Broker.BrokerTokenHealth>();
        if (health is null || sp.GetService<Application.AgentRuntimes.Broker.IBrokerTokenService>() is null)
        {
            return null;
        }

        var credentials = sp.GetService<IAgentRuntimeCredentialStore>();
        return new BrokerCredentialRenewal(
            sp.GetRequiredService<Application.AgentRuntimes.Broker.IBrokerTokenService>,
            health,
            () => credentials?.BrokerCarries(AgentTool.Claude)
                  ?? (broker.IsActive && AgentRuntimeCatalog.For(AgentTool.Claude).SupportsBrokering));
    }

    public static IServiceCollection AddAgentSessions(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // One broker for the process: a session's pending approvals must outlive the circuit that
        // started them (decision 6, the window can close while a change waits).
        // Backlog 44: what each session is working on, in the studio's own database, and the gate that reads
        // it. The broker takes the store rather than looking it up, so a build with no tracker simply has no
        // gate instead of denying every change in a product whose board is not there.
        services.AddSingleton<ISessionWorkItems>(sp => new SqliteSessionWorkItems(
            sp.GetRequiredService<Persistence.ISqliteConnectionFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILogger<SqliteSessionWorkItems>>()));

        services.AddSingleton<ISessionPlans>(sp => new SqliteSessionPlans(
            sp.GetRequiredService<Persistence.ISqliteConnectionFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILogger<SqliteSessionPlans>>()));

        // Backlog 85: the readiness facts and the change accounting behind the budget.
        services.AddSingleton<ISessionChangedFiles>(sp => new SqliteSessionChangedFiles(
            sp.GetRequiredService<Persistence.ISqliteConnectionFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILogger<SqliteSessionChangedFiles>>()));

        services.AddSingleton<ISessionGovernance>(sp => new SessionGovernance(
            sp.GetRequiredService<ISessionWorkItems>(),
            sp.GetRequiredService<ISessionPlans>(),
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>(),
            sp.GetRequiredService<Application.Wiki.IWikiDocuments>(),
            sp.GetRequiredService<ISessionChangedFiles>(),
            sp.GetService<ILogger<SessionGovernance>>()));

        services.AddSingleton(sp => new QuickChange(
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>(),
            sp.GetRequiredService<Application.Wiki.IWikiLibraries>(),
            sp.GetRequiredService<ISessionWorkItems>(),
            sp.GetRequiredService<ISessionPlans>()));

        // Backlog 88: the role each session runs under, and the studio-tool grants that follow from it.
        services.AddSingleton<ISessionRoles>(sp => new SqliteSessionRoles(
            sp.GetRequiredService<Persistence.ISqliteConnectionFactory>(),
            sp.GetService<ILogger<SqliteSessionRoles>>()));

        // ADR 0028 (backlog 152): the trust level a person chose per workspace, read live by the broker.
        services.TryAddSingleton<IWorkspaceTrust>(sp => new SqliteWorkspaceTrust(
            sp.GetRequiredService<Persistence.ISqliteConnectionFactory>(),
            sp.GetService<ILogger<SqliteWorkspaceTrust>>()));

        // Backlog 244: the operator's attention per conversation, beside the transcript in the studio's database.
        services.TryAddSingleton<ISessionAttention>(sp => new SqliteSessionAttention(
            sp.GetRequiredService<Persistence.ISqliteConnectionFactory>(),
            sp.GetService<ILogger<SqliteSessionAttention>>()));

        // Backlog 299 (ADR 0031 decision 8): the judge for an autopilot session's Bash calls. The key lives in the
        // secret store like every provider credential; no key means the adapter is still constructed but every call
        // throws no_key, which AutopilotCommandJudge turns into Unavailable, which asks. Resolved at each call, like
        // the reviewer above, because the key can be saved or rotated while a session runs.
        services.AddHttpClient<Judge.TypeSafeAutopilotJudge>();
        services.TryAddSingleton(new Judge.TypeSafeAutopilotJudgeOptions());
        services.TryAddSingleton(new Application.Sessions.AutopilotJudgeOptions());
        services.AddSingleton(sp => new Application.Sessions.AutopilotCommandJudge(
            sp.GetRequiredService<Judge.TypeSafeAutopilotJudge>(),
            sp.GetRequiredService<Application.Sessions.AutopilotJudgeOptions>(),
            sp.GetRequiredService<TimeProvider>()));

        services.AddSingleton<IAgentApprovalBroker>(sp => new AgentApprovalBroker(
            work: sp.GetRequiredService<ISessionWorkItems>(),
            plans: sp.GetRequiredService<ISessionPlans>(),
            governance: sp.GetRequiredService<ISessionGovernance>(),
            roles: sp.GetRequiredService<ISessionRoles>(),
            heldCredentials: () => AgentHeldCredentials.From(
                sp.GetService<IAgentRuntimeCredentialStore>(),
                sp.GetService<Application.Projects.IProjectEnvironment>()),
            trust: sp.GetRequiredService<IWorkspaceTrust>(),
            // Backlog 312: so a worktree session's trust reads its repository folder's, never its own id's.
            workspaceStore: sp.GetRequiredService<Application.Persistence.IWorkspaceStore>(),
            // Backlog 243: resolved at each commit, because the reviewer runs conversations and the runner needs this.
            reviewer: () => sp.GetService<ICommitReviewer>(),
            attention: sp.GetRequiredService<ISessionAttention>(),
            hooks: sp.GetService<IWorkspaceHooksFolder>(),
            configIncludes: sp.GetService<IWorkspaceConfigIncludes>(),
            // Backlog 299: resolved at each call, like the reviewer, so a key saved mid-session applies to the
            // next Bash call rather than needing a restart.
            judge: () => sp.GetService<Application.Sessions.AutopilotCommandJudge>(),
            // Backlog 299: the title of the item(s) the session is working on (the judge's state) and each
            // bound item's board number mapped to its CURRENT status (the forward-status-move rule's
            // own-item, one-genuine-step check). A session working on none, or a tracker call that fails,
            // answers the empty context; the broker itself catches anything this throws, so a throw here is
            // only ever a belt on top of that brace, never relied on alone.
            autopilotContext: async (sessionId, ct) =>
            {
                var work = sp.GetRequiredService<ISessionWorkItems>().For(sessionId);
                if (work.Count == 0)
                {
                    return AutopilotSessionContext.Empty;
                }

                var trackers = sp.GetRequiredService<Application.WorkItems.ITrackerResolver>();
                var titles = new List<string>();
                var statuses = new Dictionary<int, string>();
                foreach (var item in work)
                {
                    if (await trackers.For(item.Project).GetAsync(item.Item, ct).ConfigureAwait(false) is { } found)
                    {
                        titles.Add(found.Title);
                        statuses[found.Number] = Domain.WorkItems.StableNames.ToName(found.Status);
                    }
                }

                return new AutopilotSessionContext(string.Join("; ", titles), statuses);
            },
            // Backlog 299: the session's own recorded attempt branch, a synchronous fact (ISessionBranches is
            // in-memory/SQLite, never a git call).
            autopilotAttemptBranch: sessionId => sp.GetRequiredService<ISessionBranches>().For(sessionId)?.Branch,
            // Backlog 299: the workspace's actual current branch, a real git call through the session's own
            // pinned git (SessionGit), never a circuit's scoped seams (backlog 150's rule for every git reach here).
            autopilotCurrentBranch: async (sessionId, ct) =>
                sp.GetRequiredService<SessionGit>().For(sessionId) is { } services
                    ? await services.Git.GetCurrentBranchAsync(ct).ConfigureAwait(false)
                    : null,
            // Backlog 322: the run's own base branch for a not-yet-started item, a synchronous fact
            // (ISessionAttemptBases is in-memory, never a git call), read only for attempt_branch.
            autopilotKnownBase: sessionId => sp.GetRequiredService<ISessionAttemptBases>().For(sessionId),
            // Follow-up filed 2026-10-22 (approval card with no arguments): each studio tool's own declared schema,
            // resolved at the call, so a call its schema could not accept is refused instead of put on a card.
            studioToolSchema: name => sp.GetServices<Application.Sessions.Tools.IStudioTool>()
                .FirstOrDefault(tool => string.Equals(tool.Name, name, StringComparison.Ordinal))?.InputSchemaJson));

        // Backlog 288: the folder the host's git runs hooks from, asked through the workspace's pinned bridge runner.
        services.TryAddSingleton<IWorkspaceHooksFolder>(sp => new HostGitHooksFolder(
            sp.GetRequiredService<Application.Git.IWorkspaceGit>(),
            logger: sp.GetService<ILogger<HostGitHooksFolder>>()));

        // Backlog 289: the work-tree files the host's git config includes, asked through the same bridge runner.
        services.TryAddSingleton<IWorkspaceConfigIncludes>(sp => new HostGitConfigIncludes(
            sp.GetRequiredService<Application.Git.IWorkspaceGit>(),
            logger: sp.GetService<ILogger<HostGitConfigIncludes>>()));

        // Backlog 243: the commit reviewer role. The role is read from the settings table at each commit, so a change in
        // Settings > Agents > Commit reviewer applies to the next one. Only Claude has a conversation runner in this
        // version; a runtime returning from docs/v2.md section 1 brings its runner here and nothing else changes.
        services.AddSingleton<ICommitReviewer>(sp => new AgentCommitReviewer(
            () => sp.GetService<Application.Persistence.IConfigurationStore>() is { } settings
                ? CommitReviewerSetting.Read(settings)
                : CommitReviewerSetting.Default,
            tool => tool == AgentTool.Claude ? sp.GetRequiredService<IAgentSessionRunner>() : null,
            sp.GetRequiredService<IAgentApprovalBroker>(),
            sp.GetRequiredService<ISessionStore>(),
            sp.GetRequiredService<SessionGit>(),
            // Backlog 332: the same default attempt_commit uses, so the reviewer and the commit it reviews
            // can never disagree about which paths belong to this attempt.
            (session, ct) => sp.GetRequiredService<Git.SessionGitWork>().DefaultPathsAsync(session, ct),
            sp.GetRequiredService<ISessionWorkItems>(),
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>(),
            sp.GetRequiredService<ISessionPlans>(),
            sp.GetService<Application.Wiki.IWikiDocuments>(),
            logger: sp.GetService<ILogger<AgentCommitReviewer>>(),
            // Backlog 295: the same copy the runner would resolve, so the container-coverage scan reads the real
            // test sources rather than nothing.
            copyFor: id => sp.GetService<IWorkspaceAgentSync>()?.CopyPathFor(id)));

        // Backlog 300: the autopilot operator, a read-only advising conversation following the commit reviewer's
        // own pattern. Registered so later slices that resolve IAutopilotOperator find a real implementation;
        // nothing here constructs AutopilotEngine or starts it ticking (#301's job). A project-to-workspace
        // lookup is its own narrow seam here, because the operator has no running builder session to borrow a
        // workspace id from: it takes the MOST RECENTLY OPENED workspace bound to the project (IWorkspaceStore
        // .Recent is newest-first), so a project with several workspaces gets whichever was opened last, which
        // can differ between steps. A stated limitation @117 accepts until a later slice needs more than one; a
        // project with no workspace at all throws (nowhere to run is "no answer").
        services.AddSingleton<Application.Autopilot.IAutopilotOperator>(sp => new AgentAutopilotOperator(
            tool => tool == AgentTool.Claude ? sp.GetRequiredService<IAgentSessionRunner>() : null,
            sp.GetRequiredService<IAgentApprovalBroker>(),
            sp.GetRequiredService<ISessionStore>(),
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>(),
            sp.GetRequiredService<Application.Wiki.IWikiDocuments>(),
            project => Application.Projects.WorkspaceProjects.NewestFolderIn(
                sp.GetRequiredService<Application.Persistence.IWorkspaceStore>(), project),
            logger: sp.GetService<ILogger<AgentAutopilotOperator>>(),
            copyFor: id => sp.GetService<IWorkspaceAgentSync>()?.CopyPathFor(id)));

        services.AddSingleton(sp => new SessionHookWriter(
            sp.GetRequiredService<DataDirectory>().Path,
            ApprovalUrlFor(configuration),
            ToolServerUrlFor(configuration)));

        // Backlog 84: the studio's own tool server (ADR 0019). One ledger and one server for the process, because
        // idempotency and session binding are process-wide facts. The foundation ships one harmless read-only tool;
        // items 41 and 42 register theirs as further IStudioTool singletons.
        services.AddSingleton<IToolCallLedger>(sp => new FileToolCallLedger(sp.GetRequiredService<DataDirectory>().Path));
        // Backlog 157: fravashi.exe's upgrade operation, and the upgrade a conversation schedules for the end of its turn.
        // The check fetches the release manifest on the host, so the client waits a minute at most.
        services.AddHttpClient<Application.Updates.IStudioUpgrades, Updates.StudioUpgradeClient>(
            client => client.Timeout = TimeSpan.FromSeconds(60));
        services.AddSingleton(sp => new StudioUpgradeSchedule(sp.GetRequiredService<DataDirectory>().Path));
        services.AddSingleton<IStudioTool>(sp => new StudioVersionTool(
            sp.GetService<Application.Updates.IProductInfo>() ?? new Updates.ProductInfo(),
            sp.GetService<Application.Updates.IStudioUpgrades>()));
        services.AddSingleton<IStudioTool>(sp => new StudioUpgradeTool(
            sp.GetRequiredService<Application.Updates.IStudioUpgrades>(), sp.GetRequiredService<StudioUpgradeSchedule>()));

        // Backlog 41: the work-item tools. Registered only when a tracker is wired, because a studio with no
        // board would otherwise list tools that answer nothing. Each is its own type so the grant below can
        // cover retrieval without covering mutation (threat model requirement 1).
        services.AddSingleton<IStudioTool>(sp => new WorkItemFindTool(
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>(),
            sp.GetRequiredService<Application.WorkItems.IWorkItemChanges>()));
        services.AddSingleton<IStudioTool>(sp => new WorkItemReadTool(
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>(),
            sp.GetRequiredService<Application.WorkItems.IWorkItemChanges>()));
        services.AddSingleton<IStudioTool>(sp => new WorkItemStartableTool(
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>(),
            sp.GetRequiredService<Application.WorkItems.IWorkItemChanges>()));
        // Work item #368: what this project released, read-only.
        services.AddSingleton<IStudioTool>(sp => new WorkItemReleasesTool(
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>(),
            sp.GetRequiredService<Application.WorkItems.IWorkItemChanges>(),
            sp.GetRequiredService<Application.Releases.IReleaseStores>()));
        services.AddSingleton<IStudioTool>(sp => new WorkItemCreateTool(
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>(),
            sp.GetRequiredService<Application.WorkItems.IWorkItemChanges>()));
        services.AddSingleton<IStudioTool>(sp => new WorkItemUpdateTool(
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>(),
            sp.GetRequiredService<Application.WorkItems.IWorkItemChanges>()));
        services.AddSingleton<IStudioTool>(sp => new WorkItemTransitionTool(
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>(),
            sp.GetRequiredService<Application.WorkItems.IWorkItemChanges>()));
        services.AddSingleton<IStudioTool>(sp => new WorkItemEvidenceTool(
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>(),
            sp.GetRequiredService<Application.WorkItems.IWorkItemChanges>()));
        services.AddSingleton<IStudioTool>(sp => new WorkItemLinkTool(
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>(),
            sp.GetRequiredService<Application.WorkItems.IWorkItemChanges>()));

        // Backlog 42: the wiki's tools, beside the board's. Retrieval and mutation are separate types here
        // too, for the same reason: the grant is enforced per tool.
        services.AddSingleton<IStudioTool>(sp => new WikiFindTool(
            sp.GetRequiredService<Application.Wiki.IWikiLibraries>(),
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>(),
            sp.GetService<Application.Wiki.IWikiMeaningSearch>(),
            sp.GetService<Application.Wiki.IWikiSummaryStore>()));
        services.AddSingleton<IStudioTool>(sp => new WikiReadTool(
            sp.GetRequiredService<Application.Wiki.IWikiLibraries>(),
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>()));
        services.AddSingleton<IStudioTool>(sp => new WikiWriteTool(
            sp.GetRequiredService<Application.Wiki.IWikiLibraries>(),
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>()));
        services.AddSingleton<IStudioTool>(sp => new WikiLinkTool(
            sp.GetRequiredService<Application.Wiki.IWikiLibraries>(),
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>()));

        // Backlog 193: checking a mockup tree against the reader before it is saved in a design document.
        services.AddSingleton<IStudioTool>(sp => new MockupCheckTool(
            sp.GetRequiredService<Application.Wiki.IWikiLibraries>(),
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>()));

        // Backlog 208: the format itself (schema and behaviour-vocabulary guide), for a project with no
        // schema reference of its own, which is every user's project. No wiki dependency, on purpose.
        services.AddSingleton<IStudioTool>(new MockupSchemaTool());

        // Backlog 211: checking a prototype document, including the pinned design it names, against the
        // reader before it is saved.
        services.AddSingleton<IStudioTool>(sp => new PrototypeCheckTool(
            sp.GetRequiredService<Application.Wiki.IWikiLibraries>(),
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>()));

        // Backlog 215: an agent renders a design as a prototype and adjusts it through the same overlay a person
        // edits on the page. Creating runs unattended; refreshing an existing one and every adjustment ask.
        services.AddSingleton<IStudioTool>(sp => new PrototypeRenderTool(
            sp.GetRequiredService<Application.Wiki.IWikiLibraries>(),
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>()));
        services.AddSingleton<IStudioTool>(sp => new PrototypeAdjustTool(
            sp.GetRequiredService<Application.Wiki.IWikiLibraries>(),
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>()));

        // Backlog 44: the two tools that let a session see and clear gate G1 for itself.
        services.AddSingleton<IStudioTool>(sp => new SessionWorkReadTool(
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>(),
            sp.GetRequiredService<Application.WorkItems.IWorkItemChanges>(),
            sp.GetRequiredService<ISessionWorkItems>()));
        services.AddSingleton<IStudioTool>(sp => new SessionWorkLinkTool(
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>(),
            sp.GetRequiredService<Application.WorkItems.IWorkItemChanges>(),
            sp.GetRequiredService<ISessionWorkItems>()));

        // Backlog 45: the tool that clears gate G2, and the one read that says what a session is bound to.
        services.AddSingleton<IStudioTool>(sp => new SessionPlanLinkTool(
            sp.GetRequiredService<Application.Wiki.IWikiLibraries>(),
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>(),
            sp.GetRequiredService<ISessionPlans>()));
        services.AddSingleton<IStudioTool>(sp => new SessionGovernanceReadTool(
            sp.GetRequiredService<Application.Wiki.IWikiLibraries>(),
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>(),
            sp.GetRequiredService<ISessionWorkItems>(),
            sp.GetRequiredService<ISessionPlans>(),
            sp.GetRequiredService<ISessionRoles>()));

        // Backlog 244: the operator's attention, for an agent writing an outcome note.
        services.AddSingleton<IStudioTool>(sp => new SessionAttentionTool(
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>(),
            sp.GetRequiredService<Application.WorkItems.IWorkItemChanges>(),
            sp.GetRequiredService<ISessionAttention>()));

        // Backlog 86: the branch an attempt runs on, and the commit it produces. Registered only where a
        // git surface exists; a studio with no work tree simply has no attempt branching rather than a
        // tool that fails whenever it is called.
        services.AddSingleton<ISessionBranches>(sp => new SqliteSessionBranches(
            sp.GetRequiredService<Persistence.ISqliteConnectionFactory>(),
            sp.GetService<ILogger<SqliteSessionBranches>>()));

        // Backlog 150: a session's git is the git of ITS workspace, reached through the bridge pinned to that
        // workspace id, never a circuit's scoped seams (a tool call's request carries no window, so those fell
        // through to a fallback nothing populates and every attempt answered "no git work tree here").
        services.TryAddSingleton(sp => new SessionGit(
            sp.GetRequiredService<ISessionStore>(),
            sp.GetRequiredService<Application.Git.IWorkspaceGit>(),
            sp.GetService<Application.AgentSync.IWorkspaceAgentSync>()));

        // #315: the branch an autopilot item's attempt is made from, set by the builder for the session it starts.
        services.TryAddSingleton<ISessionAttemptBases, InMemorySessionAttemptBases>();

        services.AddSingleton(sp => new Git.SessionGitWork(
            sp.GetRequiredService<SessionGit>(),
            sp.GetRequiredService<ISessionChangedFiles>(),
            sp.GetRequiredService<ISessionBranches>(),
            sp.GetRequiredService<ISessionWorkItems>(),
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILogger<Git.SessionGitWork>>(),
            sp.GetRequiredService<ISessionAttemptBases>(),
            // Backlog 332: which session owns a worktree, so attempt_commit's default path set (and the
            // commit reviewer's, below) is the worktree's own when one applies. Optional: a build that never
            // wires the autopilot (a test, or a slice that does not need it) simply never takes that branch.
            sp.GetService<Autopilot.AutopilotBuilderSessions>()));

        services.AddSingleton<IStudioTool>(sp => new AttemptStartTool(
            sp.GetRequiredService<Application.Wiki.IWikiLibraries>(),
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>(),
            sp.GetRequiredService<Git.SessionGitWork>()));
        services.AddSingleton<IStudioTool>(sp => new AttemptCommitTool(
            sp.GetRequiredService<Application.Wiki.IWikiLibraries>(),
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>(),
            sp.GetRequiredService<Git.SessionGitWork>()));

        // Backlog 150: what an agent reaches git with. The reads run unattended (AgentGraphTools), the commit
        // and the push ask, and all of them run on the host in the session's own workspace folder.
        services.AddSingleton<IStudioTool>(sp => new GitStatusTool(sp.GetRequiredService<SessionGit>()));
        services.AddSingleton<IStudioTool>(sp => new GitLogTool(sp.GetRequiredService<SessionGit>()));
        services.AddSingleton<IStudioTool>(sp => new GitShowTool(sp.GetRequiredService<SessionGit>()));
        services.AddSingleton<IStudioTool>(sp => new GitDiffTool(sp.GetRequiredService<SessionGit>()));
        services.AddSingleton<IStudioTool>(sp => new GitBlameTool(sp.GetRequiredService<SessionGit>()));
        services.AddSingleton<IStudioTool>(sp => new GitCommitTool(
            sp.GetRequiredService<SessionGit>(),
            sp.GetRequiredService<Git.SessionGitWork>()));
        services.AddSingleton<IStudioTool>(sp => new GitPushTool(sp.GetRequiredService<SessionGit>()));

        // Backlog 153: the CI half of a release act. The reads run unattended; the tag asks like a commit does.
        services.AddHttpClient<Ci.ForgeRuns>();
        services.AddSingleton<IStudioTool>(sp => new CiRunsTool(
            sp.GetRequiredService<SessionGit>(), sp.GetRequiredService<Ci.ForgeRuns>()));
        services.AddSingleton<IStudioTool>(sp => new CiRunLogTool(
            sp.GetRequiredService<SessionGit>(), sp.GetRequiredService<Ci.ForgeRuns>()));
        services.AddSingleton<IStudioTool>(sp => new GitTagTool(sp.GetRequiredService<SessionGit>()));

        // Backlog 159: the agent browser, one headless Chromium for the process and one page per session. The bridge's and
        // the app runner's own addresses are refused to it; a session's page is let go when its process ends.
        services.TryAddSingleton<IAgentBrowser>(sp =>
        {
            var configuration = sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
            var browser = new Browser.PlaywrightAgentBrowser(
                StudioBrowserPolicy(configuration),
                jitless: StudioBrowserJitless(configuration),
                sp.GetService<ILogger<Browser.PlaywrightAgentBrowser>>());
            sp.GetRequiredService<IAgentApprovalBroker>().Released += session => _ = browser.CloseAsync(session);
            return browser;
        });

        // Backlog 223: the design check, a browser of its own per check under the same policy and launch as the agent's.
        services.TryAddSingleton<Fravashi.Application.Verification.IDesignCheck>(sp =>
        {
            var configuration = sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
            return new Verification.PlaywrightDesignCheck(
                StudioBrowserPolicy(configuration),
                jitless: StudioBrowserJitless(configuration),
                sp.GetService<ILogger<Verification.PlaywrightDesignCheck>>());
        });
        services.AddSingleton<IStudioTool>(sp => new BrowserOpenTool(sp.GetRequiredService<IAgentBrowser>()));
        services.AddSingleton<IStudioTool>(sp => new BrowserReadTool(sp.GetRequiredService<IAgentBrowser>()));
        services.AddSingleton<IStudioTool>(sp => new BrowserClickTool(sp.GetRequiredService<IAgentBrowser>()));
        services.AddSingleton<IStudioTool>(sp => new BrowserFillTool(sp.GetRequiredService<IAgentBrowser>()));
        services.AddSingleton<IStudioTool>(sp => new BrowserPressTool(sp.GetRequiredService<IAgentBrowser>()));
        services.AddSingleton<IStudioTool>(sp => new BrowserScreenshotTool(sp.GetRequiredService<IAgentBrowser>()));
        services.AddSingleton<IStudioTool>(sp => new BrowserCloseTool(sp.GetRequiredService<IAgentBrowser>()));

        // Backlog 55: authoring a scenario, and running one. The runner is handed the session's contained
        // copy through the broker, which is the one place that knows it; a scenario runs there or nowhere.
        // Backlog 87: a scenario that uses the application gets one, built and run by the host's app runner. The
        // client waits on a build for up to the app runner's own ceiling, so it has no HttpClient timeout of its own.
        services.AddHttpClient<Application.WorkItems.IApplicationRuns, AppRuns.AppRunnerClient>(
            client => client.Timeout = Timeout.InfiniteTimeSpan);
        // Backlog 224: a run on an item with an approved design checks the application against the pinned design, and
        // keeps the report, consoles, logs and screenshots as verification artifacts.
        services.TryAddSingleton<Fravashi.Application.Verification.IVerificationArtifacts>(sp =>
            new Verification.VerificationArtifactStore(
                sp.GetRequiredService<DataDirectory>(),
                logger: sp.GetService<ILogger<Verification.VerificationArtifactStore>>()));
        services.AddSingleton(sp => new Fravashi.Application.Verification.DesignVerification(
            sp.GetRequiredService<Application.Wiki.DesignApproval>(),
            sp.GetRequiredService<Fravashi.Application.Verification.IDesignCheck>(),
            sp.GetRequiredService<Fravashi.Application.Verification.IVerificationArtifacts>()));
        services.AddSingleton(sp => new Application.WorkItems.ScenarioRunner(
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>(),
            sp.GetRequiredService<Application.AgentRuntimes.IAgentCommandRunner>(),
            sp.GetRequiredService<Application.WorkItems.IApplicationRuns>(),
            // Backlog 154: a step gets the project's variables too.
            project => sp.GetService<Application.Projects.IProjectEnvironment>()?.Resolve(project)
                ?? new Dictionary<string, string>(),
            sp.GetRequiredService<Fravashi.Application.Verification.DesignVerification>()));
        services.AddSingleton<IStudioTool>(sp => new ScenarioWriteTool(
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>(),
            sp.GetRequiredService<Application.WorkItems.IWorkItemChanges>()));
        services.AddSingleton<IStudioTool>(sp => new ScenarioRunTool(
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>(),
            sp.GetRequiredService<Application.WorkItems.IWorkItemChanges>(),
            sp.GetRequiredService<Application.WorkItems.ScenarioRunner>(),
            session => (sp.GetRequiredService<IAgentApprovalBroker>() as AgentApprovalBroker)?.RootFor(session)));

        // Backlog 54: the design gate.
        services.AddSingleton(sp => new Application.Wiki.DesignApproval(
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>(),
            sp.GetRequiredService<Application.Wiki.IWikiLibraries>()));
        services.AddSingleton<IStudioTool>(sp => new DesignApproveTool(
            sp.GetRequiredService<Application.Wiki.IWikiLibraries>(),
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>(),
            sp.GetRequiredService<Application.Wiki.DesignApproval>()));

        // Backlog 85: the quick-change path, as one call.
        services.AddSingleton<IStudioTool>(sp => new QuickChangeTool(
            sp.GetRequiredService<Application.Wiki.IWikiLibraries>(),
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>(),
            sp.GetRequiredService<QuickChange>()));

        // Work item #359: the files a work item carries, in the data directory, and the studio assistant's tools. The
        // registry is resolved lazily (it takes the tool server, which takes these tools), and the autopilot's
        // dispatcher is scoped (the Web project registers it), so the dispatch tool asks a scope for it per call.
        services.TryAddSingleton<Application.WorkItems.IWorkItemAttachments>(sp => new WorkItems.FileWorkItemAttachments(
            sp.GetRequiredService<Persistence.ISqliteConnectionFactory>(),
            sp.GetRequiredService<DataDirectory>(),
            logger: sp.GetService<ILogger<WorkItems.FileWorkItemAttachments>>()));
        services.AddSingleton<IStudioTool>(sp => new SessionStartBuildingTool(
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>(),
            sp.GetRequiredService<Application.WorkItems.IWorkItemChanges>(),
            sp.GetRequiredService<IAgentApprovalBroker>(),
            sp.GetRequiredService<ISessionStore>(),
            () => sp.GetRequiredService<LiveSessionRegistry>(),
            sp.GetService<IWorkspaceAgentSync>(),
            sp.GetRequiredService<ISessionWorkItems>(),
            sp.GetRequiredService<ISessionPlans>(),
            () => sp.GetService<Application.Persistence.IConfigurationStore>() is { } config
                ? AgentDefaultsSetting.Read(config).NewConversation
                : AgentDefault.None));
        services.AddSingleton<IStudioTool>(sp => new AutopilotDispatchTool(
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>(),
            sp.GetRequiredService<Application.WorkItems.IWorkItemChanges>(),
            sp.GetRequiredService<IAgentApprovalBroker>(),
            sp.GetRequiredService<IServiceScopeFactory>()));
        services.AddSingleton<IStudioTool>(sp => new WorkItemAttachImageTool(
            sp.GetRequiredService<Application.WorkItems.ITrackerResolver>(),
            sp.GetRequiredService<Application.WorkItems.IWorkItemChanges>(),
            sp.GetRequiredService<ISessionStore>(),
            sp.GetRequiredService<Application.WorkItems.IWorkItemAttachments>()));

        // Backlog 88: what a session may do with the studio's own tools now follows from its ROLE, which
        // is what item 41 left as a fixed default for exactly this to replace.
        // Backlog 121: a session acts on the project of the workspace it runs in, resolved per call.
        services.TryAddSingleton<Application.Projects.ISessionProjects>(sp => new Application.Projects.SessionProjects(
            sp.GetRequiredService<ISessionStore>(),
            sp.GetRequiredService<Application.Persistence.IWorkspaceStore>()));

        // Backlog 121: choosing a folder's project, refused while that folder has a conversation running.
        services.TryAddSingleton(sp => new Application.Projects.WorkspaceProjectBinding(
            sp.GetRequiredService<Application.Projects.IProjectStore>(),
            sp.GetRequiredService<Application.Persistence.IWorkspaceStore>(),
            // Work item #352: a graph-only conversation writes that project's tracker, so it holds the folder's project too.
            workspaceId => sp.GetRequiredService<LiveSessionRegistry>().AnyRunningFor(workspaceId)));

        // Item #373: the setup's one write, through the same binding, provider, trust and import ports as Settings.
        services.TryAddSingleton(sp => new Application.Setup.FolderSetup(
            sp.GetRequiredService<Application.Projects.IProjectStore>(),
            sp.GetRequiredService<Application.Persistence.IWorkspaceStore>(),
            sp.GetRequiredService<Application.Projects.WorkspaceProjectBinding>(),
            sp.GetRequiredService<Application.Projects.IProjectProviders>(),
            sp.GetRequiredService<IWorkspaceTrust>(),
            sp.GetRequiredService<Application.Persistence.IConfigurationStore>(),
            sp.GetRequiredService<Application.Setup.ISetupDraftStore>(),
            sp.GetRequiredService<Application.Migration.RepositoryImport>()));

        services.TryAddSingleton<IStudioToolGrants>(sp =>
            new RoleStudioToolGrants(sp.GetRequiredService<ISessionRoles>()));

        services.AddSingleton(sp => new StudioToolServer(
            sp.GetRequiredService<IAgentApprovalBroker>(),
            sp.GetServices<IStudioTool>(),
            sp.GetRequiredService<IToolCallLedger>(),
            (sp.GetService<Application.Updates.IProductInfo>() ?? new Updates.ProductInfo()).Version,
            logger: sp.GetService<ILogger<StudioToolServer>>(),
            grants: sp.GetRequiredService<IStudioToolGrants>(),
            projects: sp.GetRequiredService<Application.Projects.ISessionProjects>()));

        // Backlog 69: projects the stored Settings > Framework artifacts into each session's config home so
        // they actually change agent behaviour. Registered only when an artifact store is wired; a build
        // without one keeps sessions working with no framework projection at all.
        services.AddSingleton(sp => sp.GetService<Application.Framework.IFrameworkArtifactStore>() is { } store
            ? new SessionFrameworkProjector(
                sp.GetRequiredService<DataDirectory>().Path,
                store,
                sp.GetService<ILogger<SessionFrameworkProjector>>(),
                // Backlog 183: a stored subscription token is handed to every child, so no copy of the CLI's own
                // refreshable sign-in is seeded beside it.
                seedSignInFile: sp.GetService<Application.AgentRuntimes.IAgentRuntimeCredentialStore>() is { } credentials
                    ? () => !credentials.HasSubscriptionToken(Domain.AgentSessions.AgentTool.Claude)
                    : null)
            : null!);

        // Backlog 116: the CLI's own conversation files are redacted in place when a session ends and on
        // every studio start (the hosted service), never deleted, because Continue resumes from them.
        services.AddSingleton(sp => new CliTranscriptScrubber(
            sp.GetRequiredService<DataDirectory>().Path,
            logger: sp.GetService<ILogger<CliTranscriptScrubber>>()));
        services.AddHostedService<CliTranscriptScrubService>();

        // Also a singleton: it OWNS the live CLI processes, which outlive any one circuit.
        services.AddSingleton<IAgentSessionRunner>(sp => new ClaudeSessionRunner(
            sp.GetRequiredService<IAgentApprovalBroker>(),
            sp.GetRequiredService<SessionHookWriter>(),
            sp.GetService<IWorkspaceAgentSync>(),
            sp.GetService<IAgentRuntimeCredentialStore>(),
            sp.GetService<NodePathProvisioner>(),
            // Resolved inside the factory, so the registration order of AddAgentRuntimes and
            // AddAgentSessions does not matter; GetService keeps a wiring without runtimes working.
            runtimeReport: sp.GetService<IAgentRuntimeStatusReport>(),
            logger: sp.GetService<ILogger<ClaudeSessionRunner>>(),
            framework: sp.GetService<SessionFrameworkProjector>(),
            // Backlog 74: opt-in and off by default; GetService keeps a wiring without telemetry working.
            telemetry: sp.GetService<Application.Telemetry.IProductEventRecorder>(),
            transcripts: sp.GetRequiredService<CliTranscriptScrubber>(),
            // Backlog 154: the session's project's variables, resolved by the workspace the session runs in (its
            // repository folder's project for a worktree, backlog 319; none at all when that cannot be placed).
            workspaceEnvironment: workspaceId =>
                (Application.Projects.WorkspaceProjects.For(
                        sp.GetRequiredService<Application.Persistence.IWorkspaceStore>(), workspaceId) is { } project
                    ? sp.GetService<Application.Projects.IProjectEnvironment>()?.Resolve(project)
                    : null)
                ?? new Dictionary<string, string>()));

        services.AddSingleton<ISessionStore, SqliteSessionStore>();

        // Backlog 43: parses of conversations run outside the studio, kept per file version across circuits.
        services.AddSingleton<ImportedConversationCatalog>();

        // Backlog 62: the owner's spend ceilings, over the live configuration rather than a snapshot, so
        // saving one in Settings > Agents > Spending and limits takes effect on the next turn.
        services.AddSingleton<IAgentSpendLimitSource>(
            sp => new ConfigurationAgentSpendLimits(sp.GetRequiredService<IConfiguration>()));

        // The agent CLI's conversation files on the data volume: resume is offered only while one exists, and
        // deleting a conversation removes them (decided 2026-09-15).
        services.AddSingleton(sp => new AgentConversationFiles(
            sp.GetRequiredService<DataDirectory>().Path,
            logger: sp.GetService<ILogger<AgentConversationFiles>>()));

        // Backlog 229: is the agent's model host reachable. It is asked only while a conversation waits for the
        // network, never on a schedule, so a studio with nothing waiting sends no probe at all.
        services.TryAddSingleton<INetworkReachability>(sp => new Network.NetworkReachability(
            AgentModelEndpoint(sp.GetService<BrokerOptions>() ?? BrokerOptions.Disabled),
            new Network.SocketNetworkConnector(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System));

        // The registry is the studio's own list of live sessions: a singleton so both UI surfaces
        // watch the same object and a closed window leaves the agent running (decision 6).
        services.AddSingleton(sp => new LiveSessionRegistry(
            sp.GetRequiredService<IAgentSessionRunner>(),
            sp.GetRequiredService<ISessionStore>(),
            sp.GetRequiredService<IAgentApprovalBroker>(),
            sp.GetService<IWorkspaceAgentSync>(),
            logger: sp.GetService<ILogger<LiveSessionRegistry>>(),
            documentKinds: TypedTurnKindsFor(configuration),
            telemetry: sp.GetService<Application.Telemetry.IProductEventRecorder>(),
            limits: sp.GetRequiredService<IAgentSpendLimitSource>(),
            conversations: sp.GetRequiredService<AgentConversationFiles>(),
            toolServer: sp.GetRequiredService<StudioToolServer>(),
            toolLedger: sp.GetRequiredService<IToolCallLedger>(),
            work: sp.GetRequiredService<ISessionWorkItems>(),
            plans: sp.GetRequiredService<ISessionPlans>(),
            governance: sp.GetRequiredService<ISessionGovernance>(),
            roles: sp.GetRequiredService<ISessionRoles>(),
            branches: sp.GetRequiredService<ISessionBranches>(),
            upgrades: sp.GetService<Application.Updates.IStudioUpgrades>(),
            upgradeSchedule: sp.GetService<StudioUpgradeSchedule>(),
            // Backlog 218: read once, here, at the registry's own construction - the earliest moment this
            // process can read it, and before anything settles a conversation this restart orphaned.
            restartReason: StudioRestartReason.ConsumeOnce(sp.GetRequiredService<DataDirectory>().Path),
            reachability: sp.GetRequiredService<INetworkReachability>(),
            credentials: BrokerCredentialsFor(sp),
            attention: sp.GetRequiredService<ISessionAttention>()));

        // Backlog 52: "New design from a drawing" on /wiki starts its conversation through the registry above.
        services.AddSingleton(sp => new DesignFromDrawingStarter(sp.GetRequiredService<LiveSessionRegistry>()));

        return services;
    }

    /// <summary>
    /// The document kinds this studio asks every agent turn to end in (ADR 0022), or none at all.
    /// <para>
    /// <b>On unless the settings row <c>Sessions:TypedTurns</c> says <c>false</c></b> (since 2026-09-14,
    /// backlog 25 increment 6). It was off by default until then for a measured reason: a turn whose five
    /// validation attempts run out ends with the CLI exiting 1, and this runner holds ONE process per
    /// conversation, so that turn took the whole session with it. That is closed now: the session ends
    /// RESUMABLE (<see cref="SessionEnded.Resumable"/>) and the pane offers Continue, which resumes the same
    /// conversation, and a turn that answered in prose instead of a document is marked untyped in the
    /// transcript rather than presented as typed.
    /// </para>
    /// <para>
    /// To switch it off: set <c>Sessions:TypedTurns</c> to <c>false</c> in the settings table (or
    /// <c>Sessions__TypedTurns=false</c> in the container's environment). Only an explicit false turns it
    /// off; an absent, blank or unreadable value leaves the default standing, because the default is the
    /// shipping protocol and a typo in a row must not quietly switch it away.
    /// </para>
    /// </summary>
    public static IReadOnlyList<AgentTurnDocumentKind> TypedTurnKindsFor(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return bool.TryParse(configuration["Sessions:TypedTurns"], out var enabled) && !enabled
            ? []
            : AgentTurnDocumentSchema.DefaultKinds;
    }

    /// <summary>
    /// Where the session's MCP config points the CLI for the studio's tool server (backlog 84). An explicit
    /// <c>Sessions:ToolServerUrl</c> wins; otherwise it is the approval endpoint's own origin, because both are
    /// the same studio on the same loopback and a host-side dev run that moved one moved both.
    /// </summary>
    public static string ToolServerUrlFor(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = configuration["Sessions:ToolServerUrl"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        var approval = ApprovalUrlFor(configuration);
        return Uri.TryCreate(approval, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority) + StudioToolServer.Route
            : "http://127.0.0.1:8080" + StudioToolServer.Route;
    }

    /// <summary>
    /// Where the hook script posts. The studio is reached over the container's own loopback on the
    /// port it serves (<c>ASPNETCORE_HTTP_PORTS</c>, 8080 in the image); an explicit
    /// <c>Sessions:ApprovalUrl</c> wins, for a host-side dev run on another port.
    /// </summary>
    public static string ApprovalUrlFor(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = configuration["Sessions:ApprovalUrl"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        var ports = configuration["ASPNETCORE_HTTP_PORTS"];
        var port = ports?
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        return $"http://127.0.0.1:{(string.IsNullOrWhiteSpace(port) ? "8080" : port)}{AgentApprovalEndpoint.Route}";
    }

    /// <summary>
    /// Where a browser the studio starts may go (backlog 159): the bridge's, the app runner's and the upgrade operation's
    /// own addresses are refused, with everything else <see cref="Browser.BrowserUrlPolicy"/> refuses.
    /// </summary>
    private static Browser.BrowserUrlPolicy StudioBrowserPolicy(IConfiguration configuration) =>
        new([configuration["FRAVASHI_BRIDGE_URL"], configuration["FRAVASHI_APPRUNNER_URL"], configuration["FRAVASHI_UPGRADE_URL"]]);

    /// <summary>V8 runs without its JIT unless <c>Browser:AllowJit</c> is <c>true</c>.</summary>
    private static bool StudioBrowserJitless(IConfiguration configuration) =>
        !string.Equals(configuration["Browser:AllowJit"], "true", StringComparison.OrdinalIgnoreCase);
}
