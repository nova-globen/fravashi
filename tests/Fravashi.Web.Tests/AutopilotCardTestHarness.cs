using Bunit;
using Fravashi.Application.AgentSync;
using Fravashi.Application.Autopilot;
using Fravashi.Application.Persistence;
using Fravashi.Application.Projects;
using Fravashi.Application.Sessions;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Autopilot;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Autopilot;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.WorkItems;
using Microsoft.Extensions.DependencyInjection;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 303's shared fixture (the Autopilot card's since work item #358): a real local board and run store, and the fakes/stubs the card's own
/// seams need. Split out of <c>AutopilotCardTests</c> for #313's live tier (<c>AutopilotCardLiveTests</c>), which
/// needs the same setup but has to live in its own class: backlog 295's coverage scanner names the
/// ENCLOSING class of any custom FactAttribute usage, so a browser-live test sharing a class with plain
/// bUnit tests reports the whole class as unclear whether it runs in the container.
/// </summary>
public abstract class AutopilotCardTestHarness : IDisposable
{
    protected readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fravashi-autopilot-card-tests", Guid.NewGuid().ToString("N"));
    protected readonly SqliteConnectionFactory _factory;
    protected readonly LocalTrackerResolver _resolver;
    protected readonly ITracker _board;
    protected readonly SqliteAutopilotRunStore _runs;

    protected AutopilotCardTestHarness()
    {
        Directory.CreateDirectory(_directory);
        _factory = new SqliteConnectionFactory(new SqliteStoreOptions(Path.Combine(_directory, "test.db"), null, Pooling: false));
        SqliteSchema.EnsureCreated(_factory);
        _resolver = new LocalTrackerResolver(_factory, TimeProvider.System);
        _board = _resolver.For(ProjectId.Default);
        _runs = new SqliteAutopilotRunStore(_factory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    protected async Task<WorkItem> CreateItemAsync(string title)
    {
        var created = await _board.CreateAsync(
            new NewWorkItem { Level = WorkItemLevel.Task, Title = title, Description = "The problem and the outcome.", Status = WorkItemStatus.Ready },
            MutationContext.User);
        return created.Value;
    }

    /// <summary>A broker that answers nothing real: AutopilotBuilderSessions only ever subscribes to Reviewed.</summary>
    protected sealed class FakeBroker : IAgentApprovalBroker
    {
#pragma warning disable CS0067 // never raised: nothing here exercises a review
        public event Action<PendingToolApproval>? Requested;
        public event Action<PendingToolApproval, ToolApprovalDecision>? Resolved;
        public event Action<AutomaticToolRefusal>? Refused;
        public event Action<TrustedToolCall>? Trusted;
        public event Action<AutopilotAllowedCall>? AutopilotAllowed;
        public event Action<AgentSessionId>? Released;
        public event Action<AgentSessionId, CommitReview>? Reviewed;
#pragma warning restore CS0067

        public string RegisterSession(
            AgentSessionId sessionId, string? workspaceRoot = null, bool typedTurns = false,
            string? workspaceId = null, string? memoryRoot = null, string? memoryTarget = null,
            string? toolResultsRoot = null) =>
            throw new NotSupportedException();

        public void ReleaseSession(AgentSessionId sessionId) => throw new NotSupportedException();

        public void SetToolResultsRoot(AgentSessionId sessionId, string? toolResultsRoot) => throw new NotSupportedException();

        public void HoldToReview(AgentSessionId sessionId) => throw new NotSupportedException();

        public void HoldToGraphOnly(AgentSessionId sessionId) => throw new NotSupportedException();

        public bool IsGraphOnly(AgentSessionId sessionId) => false;

        public void HoldToAssistant(AgentSessionId sessionId) => throw new NotSupportedException();

        public bool IsAssistant(AgentSessionId sessionId) => false;

        public void SetAutopilot(AgentSessionId sessionId, bool autopilot) => throw new NotSupportedException();

        public bool IsAutopilot(AgentSessionId sessionId) => throw new NotSupportedException();

        public AgentSessionId? ResolveSecret(string? secret) => throw new NotSupportedException();

        public Task<ToolApprovalDecision> RequestAsync(
            AgentSessionId sessionId, string toolName, string? toolUseId, AgentToolInput input,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public bool Answer(string requestId, bool approved, string reason) => throw new NotSupportedException();

        public IReadOnlyList<PendingToolApproval> PendingFor(AgentSessionId sessionId) => throw new NotSupportedException();

        public ToolApprovalDecision Refuse(AgentSessionId sessionId, string toolName, string detail, string reason) =>
            throw new NotSupportedException();
    }

    protected sealed class FakeSync : IWorkspaceAgentSync
    {
        public void EnsureStarted(string workspaceId)
        {
        }

        public string? CopyPathFor(string workspaceId) => $"/workspaces/{workspaceId}";

        public object? StatusFor(string workspaceId) => null;
    }

    /// <summary>What SessionGovernanceStrip needs to name a workspace; empty is enough for these tests.</summary>
    protected sealed class FakeWorkspaceStore : IWorkspaceStore
    {
        public IReadOnlyList<WorkspaceRecord> Recent(int max = 10) => [];

        public WorkspaceRecord? Get(string id) => new(id, id, $"/workspaces/{id}", DateTimeOffset.UtcNow);

        public void Touch(string id, string name, string rootPath)
        {
        }

        public void Remove(string id)
        {
        }
    }

    /// <summary>The four seams Stop never calls, each a throwing stub, plus a builder whose StopAsync just records.</summary>
    protected sealed class StubOperator : IAutopilotOperator
    {
        public Task<AutopilotPrompt> WritePromptAsync(AutopilotOperatorContext context, WorkItemId item, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AutopilotAdvice> AdviseOnFailureAsync(AutopilotOperatorContext context, WorkItemId item, string failure, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AutopilotFollowUpDrafts> DraftFollowUpsAsync(AutopilotOperatorContext context, WorkItemId item, string findings, CancellationToken ct = default) => throw new NotSupportedException();
    }

    protected sealed class StubBuilder : IAutopilotBuilder
    {
        public List<WorkItemId> Stopped { get; } = [];
        public Task<AutopilotBuildResult> BuildAsync(AutopilotBuilderContext context, WorkItemId item, string prompt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StopAsync(WorkItemId item, CancellationToken ct = default) { Stopped.Add(item); return Task.CompletedTask; }

        public Task<string?> EndItemAsync(AutopilotBuilderContext context, WorkItemId item, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    protected sealed class StubReviews : IAutopilotCommitReviews
    {
        public Task<CommitReview> ReviewAsync(WorkItemId item, CancellationToken ct = default) => throw new NotSupportedException();
    }

    protected sealed class StubDelivery : IAutopilotDelivery
    {
        public Task<AutopilotDeliveryResult> DeliverAsync(AutopilotDeliveryContext context, WorkItemId item, string branchHeadSha, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AutopilotVerifyResult> VerifyAsync(AutopilotDeliveryContext context, WorkItemId item, string branchHeadSha, CancellationToken ct = default) => throw new NotSupportedException();
    }

    protected sealed class FixedReadiness(AutopilotDeliveryReadiness answer) : IAutopilotDeliveryReadiness
    {
        public Task<AutopilotDeliveryReadiness> CheckAsync(CancellationToken ct = default) => Task.FromResult(answer);
    }

    protected sealed class FixedWorkspaceStatus(AutopilotWorkspaceState answer) : IAutopilotWorkspaceStatus
    {
        public Task<AutopilotWorkspaceState> CheckAsync(ProjectId project, CancellationToken ct = default) => Task.FromResult(answer);
    }

    protected BunitContext Context(AutopilotBuilderSessions? builderSessions = null)
    {
        var ctx = new BunitContext();
        ctx.Services.AddSingleton<ICurrentProject>(SingleProject.Instance);
        ctx.Services.AddSingleton(TimeProvider.System);
        ctx.Services.AddSingleton<IAutopilotRunStore>(_runs);
        ctx.Services.AddSingleton<ITrackerResolver>(_resolver);
        ctx.Services.AddSingleton<IWorkspaceAgentSync>(new FakeSync());
        ctx.Services.AddSingleton(builderSessions ?? new AutopilotBuilderSessions(new FakeBroker()));
        ctx.Services.AddSingleton(new AutopilotEngine(new StubOperator(), new StubBuilder(), new StubReviews(), new StubDelivery(), _runs));
        // AutopilotCard resolves the live session registry (RunningWorkspaceId's own fallback), even for a
        // test whose running item has no bound session yet.
        ctx.Services.AddSingleton(new LiveSessionRegistry(
            new SilentSessionRunner(), new RecordingSessionStore(), new AgentApprovalBroker(TimeSpan.FromSeconds(5))));

        // AutopilotDispatchForm's own dependencies: reached only while no run is open, but AutopilotCard renders
        // that branch whenever a run has none (including right after a stop), so they are always registered.
        var delivery = new SqliteProjectDelivery(_factory);
        ctx.Services.AddSingleton<IProjectDelivery>(delivery);
        ctx.Services.AddSingleton<ISecretStore>(new FakeSecretStore());
        ctx.Services.AddSingleton<IAutopilotConcurrencySettings>(IAutopilotConcurrencySettings.Default);
        ctx.Services.AddSingleton(new AutopilotDispatcher(
            _resolver, delivery, new FixedReadiness(new AutopilotDeliveryReadiness(true, "github.com")),
            new FixedWorkspaceStatus(new AutopilotWorkspaceState("main", 0, WorkspaceId: "w1")), _runs, TimeProvider.System));

        return ctx;
    }
}
