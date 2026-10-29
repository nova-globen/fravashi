using System.Threading.Channels;
using Bunit;
using Fravashi.Application.AgentSync;
using Fravashi.Application.Notifications;
using Fravashi.Application.Persistence;
using Fravashi.Application.Sessions;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Insights;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;
using Fravashi.Infrastructure.Bridge;
using Fravashi.Infrastructure.Rendering;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Web.Components.Pages;
using Fravashi.Web.Components.Sessions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Plan 2e Task 6: the session surface. These render against the REAL registry, broker and approval
/// policy (only the CLI itself is a stand-in), so approving in the UI really resolves the call the
/// agent is blocked on.
/// </summary>
public class SessionsUiTests : IDisposable
{
    private const string Workspace = "w1";

    /// <summary>Item #373: a folder that is not set up.</summary>
    private const string NotSetUp = "w-unset";
    private const string CopyPath = "/workspaces/w1";

    // ADR 0028: declared BEFORE the broker on purpose, because field initialisers run in textual order and the
    // broker reads the folder's trust level through it. The same instance is what the strip's picker writes.
    private readonly InMemoryTrust _trust = new();
    private readonly FakeAgentBrowser _browser = new();
    private readonly AgentApprovalBroker _broker;
    private readonly FakeRunner _runner = new();
    private readonly InMemorySessionStore _store = new();

    // Backlog 229: the model host's reachability, asked only while a conversation waits for the network.
    private readonly FakeReachability _reachability = new();
    private readonly LiveSessionRegistry _registry;

    // Backlog 44: the page carries the strip saying what the conversation is working on, so rendering it
    // needs a board and a link store. They are the REAL ones over a throwaway database, like the registry
    // and the broker above, because a fake here would let the page drift away from what it does in the
    // studio without any test noticing.
    private readonly string _graphDirectory =
        Path.Combine(Path.GetTempPath(), "fravashi-tests", Guid.NewGuid().ToString("N"));

    private readonly Fravashi.Infrastructure.Persistence.SqliteConnectionFactory _graph;
    private readonly Fravashi.Infrastructure.WorkItems.LocalTrackerResolver _trackers;
    private readonly SqliteSessionWorkItems _work;
    private readonly SqliteSessionPlans _sessionPlans;
    private readonly Fravashi.Infrastructure.Wiki.SqliteWikiLibrary _wikiLibrary;

    public SessionsUiTests()
    {
        _broker = new AgentApprovalBroker(TimeSpan.FromSeconds(5), trust: _trust);
        _registry = new LiveSessionRegistry(_runner, _store, _broker, reachability: _reachability);

        Directory.CreateDirectory(_graphDirectory);
        _graph = new Fravashi.Infrastructure.Persistence.SqliteConnectionFactory(
            new Fravashi.Infrastructure.Persistence.SqliteStoreOptions(
                Path.Combine(_graphDirectory, "graph.db"), null, Pooling: false));
        Fravashi.Infrastructure.Persistence.SqliteSchema.EnsureCreated(_graph);
        _trackers = new Fravashi.Infrastructure.WorkItems.LocalTrackerResolver(_graph, TimeProvider.System);
        _work = new SqliteSessionWorkItems(_graph, TimeProvider.System);
        _sessionPlans = new SqliteSessionPlans(_graph, TimeProvider.System);
        _wikiLibrary = new Fravashi.Infrastructure.Wiki.SqliteWikiLibrary(_graph, TimeProvider.System, Fravashi.Domain.Projects.ProjectId.Default);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _registry.Dispose();
        _broker.Dispose();
        try
        {
            Directory.Delete(_graphDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp folder.
        }
    }

    // ---- doubles ------------------------------------------------------------------------

    private sealed class FakeRunner : IAgentSessionRunner
    {
        private Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>();

        /// <summary>
        /// Work item #362: when set, Stop ends the stream the way the real runner does when the child exits (a cancelled
        /// SessionEnded, then the end of the stream), so the session stops
        /// running; the next run gets a fresh stream. Off by default, which is what every older test was written for.
        /// </summary>
        public bool CompleteOnStop { get; set; }

        public AgentSessionRequest? LastRequest { get; private set; }

        public List<string> Sent { get; } = [];

        public bool Stopped { get; private set; }

        public IAsyncEnumerable<AgentEvent> RunAsync(AgentSessionRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return _events.Reader.ReadAllAsync(cancellationToken);
        }

        public Task<bool> SendAsync(AgentSessionId sessionId, string text, CancellationToken cancellationToken = default)
        {
            Sent.Add(text);
            return Task.FromResult(true);
        }

        public List<IReadOnlyList<AgentImageAttachment>> SentImages { get; } = [];

        public Task<bool> SendAsync(
            AgentSessionId sessionId,
            string text,
            IReadOnlyList<AgentImageAttachment> images,
            CancellationToken cancellationToken = default)
        {
            Sent.Add(text);
            SentImages.Add(images);
            return Task.FromResult(true);
        }

        public bool Stop(AgentSessionId sessionId)
        {
            Stopped = true;
            if (CompleteOnStop)
            {
                var ended = _events;
                _events = Channel.CreateUnbounded<AgentEvent>();
                ended.Writer.TryWrite(new SessionEnded(DateTimeOffset.UtcNow, AgentSessionState.Cancelled, null));
                ended.Writer.TryComplete();
            }

            return true;
        }

        public List<string?> ModelChanges { get; } = [];

        public bool ChangeModel(AgentSessionId sessionId, string? model)
        {
            ModelChanges.Add(model);
            return true;
        }

        public void Emit(AgentEvent e) => _events.Writer.TryWrite(e);
    }

    /// <summary>A probe whose answer the test sets; unreachable until told otherwise.</summary>
    private sealed class FakeReachability : Fravashi.Application.Network.INetworkReachability
    {
        private static readonly Fravashi.Application.Network.NetworkEndpoint Api = new("api.anthropic.com", 443);

        public bool Reachable { get; set; }

        public int Probes { get; private set; }

        public Fravashi.Application.Network.NetworkReachabilityState Current { get; private set; } = new(Api, null, null, null, null);

        public event Action<Fravashi.Application.Network.NetworkReachabilityState>? Changed;

        public Task<Fravashi.Application.Network.NetworkReachabilityState> ProbeNowAsync(CancellationToken cancellationToken = default)
        {
            Probes++;
            var now = DateTimeOffset.UtcNow;
            Current = new(
                Api,
                Reachable,
                now,
                Reachable ? null : new Fravashi.Application.Network.NetworkFailure(Fravashi.Application.Network.NetworkFailureKind.NameNotResolved, "EAI_AGAIN"),
                now + TimeSpan.FromMinutes(1));
            Changed?.Invoke(Current);
            return Task.FromResult(Current);
        }
    }

    private sealed class InMemorySessionStore : ISessionStore
    {
        private readonly Dictionary<AgentSessionId, SessionRecord> _records = [];
        private readonly Dictionary<AgentSessionId, List<AgentEvent>> _events = [];

        public void Save(SessionRecord record) => _records[record.Id] = record;

        public SessionRecord? Find(AgentSessionId id) => _records.GetValueOrDefault(id);

        public IReadOnlyList<SessionRecord> ForWorkspace(string workspaceId, int max = 50) =>
            [.. _records.Values.Where(r => r.WorkspaceId == workspaceId).OrderByDescending(r => r.StartedUtc)];

        public IReadOnlyList<SessionRecord> Recent(int max = 10) =>
            [.. _records.Values.OrderByDescending(r => r.StartedUtc).Take(max)];

        public void Append(AgentSessionId id, IEnumerable<AgentEvent> events)
        {
            if (!_events.TryGetValue(id, out var list))
            {
                _events[id] = list = [];
            }

            list.AddRange(events.Where(e => AgentEventJson.KindOf(e) is not null));
        }

        public IReadOnlyList<AgentEvent> Transcript(AgentSessionId id) =>
            _events.TryGetValue(id, out var list) ? [.. list] : [];

        public void Delete(AgentSessionId id)
        {
            _records.Remove(id);
            _events.Remove(id);
        }

        public SessionUsageTotals ReadUsageTotals(DateTimeOffset dayStartUtc, DateTimeOffset monthStartUtc)
        {
            var today = UsageTotal.Zero;
            var month = UsageTotal.Zero;
            var all = UsageTotal.Zero;
            foreach (var (id, record) in _records)
            {
                var last = (_events.GetValueOrDefault(id) ?? []).OfType<StatsUpdated>().LastOrDefault()?.Stats;
                var cost = last?.CostUsd ?? 0m;
                var tokens = last?.TotalTokens ?? 0L;
                all = new UsageTotal(all.CostUsd + cost, all.TotalTokens + tokens, all.Sessions + 1);
                if (record.StartedUtc >= monthStartUtc)
                {
                    month = new UsageTotal(month.CostUsd + cost, month.TotalTokens + tokens, month.Sessions + 1);
                }

                if (record.StartedUtc >= dayStartUtc)
                {
                    today = new UsageTotal(today.CostUsd + cost, today.TotalTokens + tokens, today.Sessions + 1);
                }
            }

            return new SessionUsageTotals(today, month, all);
        }

        // Backlog 36. This fixture drives the sessions PANEL, which shows none of the home insights, so
        // the three aggregates answer emptily rather than duplicating RecordingSessionStore's arithmetic.
        public IReadOnlyList<SessionDay> ReadSessionDays(DateOnly fromDayUtc, DateOnly toDayUtc) => [];

        public IReadOnlyList<DayCount> ReadTurnDays(DateOnly fromDayUtc, DateOnly toDayUtc) => [];

        public DurationStats ReadSessionDurations(DateTimeOffset fromUtc) => DurationStats.None;
    }

    /// <summary>
    /// The governance strip names the folder the conversation is bound to, which is the only thing a
    /// session is really bound to today (backlog 44 and 45 are not built).
    /// </summary>
    private sealed class FakeWorkspaces : IWorkspaceStore
    {
        public IReadOnlyList<WorkspaceRecord> Recent(int max = 10) => [Record];

        public WorkspaceRecord? Get(string id) => id switch
        {
            Workspace => Record,
            NotSetUp => Unset,
            _ => null,
        };

        public void Touch(string id, string name, string rootPath) { }

        public void Remove(string id) { }

        // Item #373: a folder is in no project until it is put in one; these tests' folder is in the default project.
        private static WorkspaceRecord Record { get; } =
            new(Workspace, "widgets", @"C:\src\widgets", DateTimeOffset.UtcNow, "default");

        /// <summary>A folder opened for the first time: recorded, and in no project.</summary>
        private static WorkspaceRecord Unset { get; } =
            new(NotSetUp, "Stomme", @"C:\src\Stomme", DateTimeOffset.UtcNow);
    }

    /// <summary>Backlog 307: the approvals inbox's own list, always empty here (it is not what these tests exercise).</summary>
    private sealed class EmptyWaitingForPersonList : IWaitingForPersonList
    {
        public Task<IReadOnlyList<WaitingForPersonEntry>> CurrentAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WaitingForPersonEntry>>([]);
    }

    private sealed class FakeSync(string? copyPath) : IWorkspaceAgentSync
    {
        /// <summary>Backlog 173: why the copy failed to start; null when it did not.</summary>
        public string? Failure { get; set; }

        public string? CopyPath { get; set; } = copyPath;

        public List<string> Retried { get; } = [];

        public void EnsureStarted(string workspaceId) { }

        public string? CopyPathFor(string workspaceId) => CopyPath;

        public object? StatusFor(string workspaceId) => null;

        public string? StartFailureFor(string workspaceId) => Failure;

        /// <summary>Models a retry whose start succeeds: the failure goes and the copy is there.</summary>
        public void Retry(string workspaceId)
        {
            Retried.Add(workspaceId);
            Failure = null;
            CopyPath = CopyPathOnRetry;
            StartChanged?.Invoke(workspaceId);
        }

        public string? CopyPathOnRetry { get; set; }

        public event Action<string>? StartChanged;
    }

    private BunitContext Context(string? workspaceId = Workspace, string? copyPath = CopyPath)
    {
        var ctx = new BunitContext();

        // The transcript keeps itself pinned to the newest content through sessionScroll.js, so every
        // render here calls into JS. Strict mode (bunit's default) would throw on the unplanned call.
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [BridgeWorkspaceContext.ActiveIdKey] = workspaceId,
                [BridgeWorkspaceContext.UrlKey] = "http://127.0.0.1:9",
                [BridgeWorkspaceContext.TokenKey] = "t",
            })
            .Build();

        ctx.Services.AddSingleton<IConfiguration>(configuration);
        ctx.Services.AddSingleton(new BridgeWorkspaceContext(configuration));
        ctx.Services.AddSingleton<IWorkspaceAgentSync>(new FakeSync(copyPath));
        ctx.Services.AddSingleton<IWorkspaceStore>(new FakeWorkspaces());
        ctx.Services.AddSingleton(_registry);
        // Backlog 307: the sessions page now embeds the approvals inbox (a WaitingForYouPanel), which needs
        // these even though these tests never put anything in it.
        ctx.Services.AddSingleton<IWaitingForPersonList>(new EmptyWaitingForPersonList());
        ctx.Services.AddSingleton(sp => new BridgeClient(new HttpClient(), sp.GetRequiredService<BridgeWorkspaceContext>()));
        ctx.Services.AddSingleton(new MarkdownPreviewRenderer());
        // Backlog 10: the pane's Node banner. The copy path here is not a folder, so nothing is
        // requested and the banner stays silent; NodeVersionBannerTests drives its states.
        ctx.Services.AddSingleton(new Fravashi.Infrastructure.AgentRuntimes.WorkspaceNodeAdvisor(
            new Fravashi.Infrastructure.AgentRuntimes.NodePathProvisioner(configuration), new FakeNodeCommandRunner()));
        ctx.Services.AddSingleton(new Fravashi.Infrastructure.AgentRuntimes.NodeProvisionCoordinator(new FakeNodeProvisioner()));
        // Backlog 43: the host CLI's history. Empty unless a test puts a conversation in it.
        ctx.Services.AddSingleton(new ImportedConversationCatalog());
        ctx.Services.AddSingleton<ICliConversationSource>(_imports);
        // Backlog 44: the strip above the transcript.
        ctx.Services.AddSingleton<Fravashi.Application.WorkItems.ITrackerResolver>(_trackers);
        // Backlog 121: a page follows its window's project; these tests have one project.
        ctx.Services.AddSingleton<Fravashi.Application.Projects.ICurrentProject>(Fravashi.Application.Projects.SingleProject.Instance);
        ctx.Services.AddSingleton<Fravashi.Application.Projects.IProjectStore>(new OneProjectStore());
        ctx.Services.AddSingleton<ISessionWorkItems>(_work);
        ctx.Services.AddSingleton<ISessionPlans>(_sessionPlans);
        // Backlog 121: a page asks for its project's wiki, and what holds an id asks for the by-id view.
        ctx.Services.AddSingleton<Fravashi.Application.Wiki.IWikiLibraries>(new OneWiki(_wikiLibrary));
        ctx.Services.AddSingleton<Fravashi.Application.Wiki.IWikiDocuments>(_wikiLibrary);
        ctx.Services.AddSingleton<ISessionGovernance>(new SessionGovernance(
            _work,
            _sessionPlans,
            _trackers,
            _wikiLibrary,
            new SqliteSessionChangedFiles(_graph, TimeProvider.System)));
        ctx.Services.AddSingleton(new Fravashi.Application.Sessions.QuickChange(
            _trackers, new OneWiki(_wikiLibrary), _work, _sessionPlans));
        ctx.Services.AddSingleton<ISessionRoles>(new SqliteSessionRoles(_graph));
        ctx.Services.AddSingleton<IWorkspaceTrust>(_trust);
        ctx.Services.AddSingleton<IAgentBrowser>(_browser);
        ctx.Services.AddLogging();
        return ctx;
    }

    private IRenderedComponent<SessionPane> Pane(BunitContext ctx) =>
        ctx.Render<SessionPane>(p => p
            .Add(c => c.WorkspaceId, Workspace)
            .Add(c => c.WorkingDirectory, CopyPath));

    /// <summary>
    /// Waits for something a background pump produces. The budget is generous on purpose: these run
    /// alongside the whole solution's tests, where a one-second wait is a coin toss, not a check.
    /// </summary>
    /// <summary>
    /// How long a wait on a background pump is given. Generous on purpose: these run alongside the whole
    /// solution's tests, where a one-second wait is a coin toss, not a check.
    /// </summary>
    private static TimeSpan Patience => TimeSpan.FromSeconds(10);

    private static async Task Eventually(Func<bool> condition, string because = "")
    {
        // A DEADLINE, not a fixed number of attempts. The budgets here used to be 2, 5, 10 and 15
        // seconds with no principle behind the numbers, and the 2 second ones failed on CI: run 103
        // and run 107 on 2026-09-19 went red on commits that changed only markdown, because the
        // runner was a shared host that also ran production, and a contended machine needs longer to
        // reach a turn boundary than this dev host does. A generous ceiling costs nothing when the
        // condition is met quickly, since the loop exits the moment it is true; it only matters on a
        // machine slow enough that the old limit was measuring the machine rather than the behaviour.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !condition())
        {
            await Task.Delay(10); // poll interval inside a wait helper, not an arm-window guess (backlog 107)
        }

        Assert.True(condition(), because);
    }

    private LiveAgentSession StartSession(string prompt = "write the readme") =>
        _registry.Start(new SessionStartRequest(Workspace, CopyPath, prompt));

    // ---- starting and continuing ---------------------------------------------------------

    [Fact]
    public async Task Sending_the_first_prompt_starts_a_session_in_the_workspace_copy()
    {
        using var ctx = Context();
        var cut = Pane(ctx);

        await cut.InvokeAsync(() => cut.Find("[data-testid=composer-input]").Input("write the readme"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=send-prompt]").Click());

        await Eventually(() => _runner.LastRequest is not null, "the runner should have been asked to start");
        Assert.Equal(CopyPath, _runner.LastRequest!.WorkingDirectory);
        Assert.Equal("write the readme", _runner.LastRequest.Prompt);
        Assert.Equal(Workspace, _runner.LastRequest.WorkspaceId);
    }

    [Fact]
    public async Task The_chosen_model_is_passed_to_the_agent_and_the_default_is_the_clis_own()
    {
        using var ctx = Context();
        var cut = Pane(ctx);

        // The picker is the house dropdown now (backlog 24), so choosing is opening it and clicking an
        // option rather than changing a select's value.
        await cut.InvokeAsync(() => cut.Find("[data-testid=model-picker-trigger]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=model-picker-opt-claude-haiku-4-5]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=composer-input]").Input("hello"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=send-prompt]").Click());

        await Eventually(() => _runner.LastRequest is not null);
        Assert.Equal("claude-haiku-4-5", _runner.LastRequest!.Model);
    }

    [Fact]
    public async Task The_chosen_effort_is_passed_to_the_agent_and_the_default_passes_none()
    {
        // Requested 2026-10-22 (model and effort per work item): an effort menu beside the model menu.
        using var ctx = Context();
        var cut = Pane(ctx);

        await cut.InvokeAsync(() => cut.Find("[data-testid=effort-picker-trigger]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=effort-picker-opt-xhigh]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=effort-picker-trigger]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=effort-picker-opt-medium]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=composer-input]").Input("hello"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=send-prompt]").Click());

        await Eventually(() => _runner.LastRequest is not null);
        Assert.Equal(AgentEffort.Medium, _runner.LastRequest!.Effort);
        Assert.Null(_runner.LastRequest.Model);
    }

    [Fact]
    public async Task Leaving_the_effort_alone_passes_none()
    {
        using var ctx = Context();
        var cut = Pane(ctx);

        await cut.InvokeAsync(() => cut.Find("[data-testid=composer-input]").Input("hello"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=send-prompt]").Click());

        await Eventually(() => _runner.LastRequest is not null);
        Assert.Null(_runner.LastRequest!.Effort);
    }

    [Fact]
    public async Task A_new_conversation_pre_selects_the_settings_default_model_and_effort()
    {
        // Work item #357: Settings > Agents > Default models and effort, for a new conversation.
        using var ctx = Context();
        var config = new FakeConfigurationStore();
        AgentDefaultsSetting.Write(config, AgentDefaults.None with
        {
            NewConversation = new AgentDefault("sonnet", AgentEffort.High),
        });
        ctx.Services.AddSingleton<IConfigurationStore>(config);
        var cut = Pane(ctx);

        Assert.Contains("sonnet", cut.Find("[data-testid=model-picker-trigger]").TextContent, StringComparison.Ordinal);
        Assert.Contains("high effort", cut.Find("[data-testid=effort-picker-trigger]").TextContent, StringComparison.Ordinal);

        await cut.InvokeAsync(() => cut.Find("[data-testid=composer-input]").Input("hello"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=send-prompt]").Click());

        await Eventually(() => _runner.LastRequest is not null);
        Assert.Equal("sonnet", _runner.LastRequest!.Model);
        Assert.Equal(AgentEffort.High, _runner.LastRequest.Effort);
    }

    [Fact]
    public async Task A_persons_pick_wins_over_the_settings_default()
    {
        using var ctx = Context();
        var config = new FakeConfigurationStore();
        AgentDefaultsSetting.Write(config, AgentDefaults.None with
        {
            NewConversation = new AgentDefault("sonnet", AgentEffort.High),
        });
        ctx.Services.AddSingleton<IConfigurationStore>(config);
        var cut = Pane(ctx);

        await cut.InvokeAsync(() => cut.Find("[data-testid=effort-picker-trigger]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=effort-picker-opt-low]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=composer-input]").Input("hello"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=send-prompt]").Click());

        await Eventually(() => _runner.LastRequest is not null);
        Assert.Equal(AgentEffort.Low, _runner.LastRequest!.Effort);
        Assert.Equal("sonnet", _runner.LastRequest.Model);
    }

    [Fact]
    public void Every_effort_level_is_offered_after_a_default_that_is_not_called_default()
    {
        // Beside the model picker's own "Default", the effort's reads "Effort", so the two are told apart in a
        // 280px pane.
        Assert.Equal(
            ["", "low", "medium", "high", "xhigh", "max"],
            SessionEffortOptions.All.Select(o => o.Value).ToArray());
        Assert.Equal("Effort", SessionEffortOptions.All[0].Label);
        Assert.Equal("high effort", SessionEffortOptions.All[3].Label);
    }

    [Fact]
    public void The_suggestable_model_aliases_are_ones_the_session_model_menu_offers()
    {
        // A work item suggests one of the menu's own family aliases, so a suggestion can never name a model the
        // session pane would not offer.
        var offered = SessionModelOptions.For(AgentTool.Claude).Select(o => o.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var alias in AgentModelAliases.All)
        {
            Assert.Contains(alias.CliId(), offered);
        }
    }

    [Fact]
    public void Between_turns_the_composer_names_the_effort_the_conversation_is_really_at()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<SessionComposer>(p => p
            .Add(c => c.Effort, null)
            .Add(c => c.RunningEffort, AgentEffort.High));
        Assert.Contains("high effort", cut.Find("[data-testid=effort-actual]").TextContent, StringComparison.Ordinal);

        var same = ctx.Render<SessionComposer>(p => p
            .Add(c => c.Effort, AgentEffort.High)
            .Add(c => c.RunningEffort, AgentEffort.High));
        Assert.Empty(same.FindAll("[data-testid=effort-actual]"));

        var locked = ctx.Render<SessionComposer>(p => p
            .Add(c => c.ModelLocked, true)
            .Add(c => c.RunningEffort, AgentEffort.Low));
        Assert.Empty(locked.FindAll("[data-testid=effort-picker]"));
        Assert.Contains("low effort", locked.Find("[data-testid=effort-locked]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Choosing_the_cli_default_back_again_passes_no_model_at_all()
    {
        using var ctx = Context();
        var cut = Pane(ctx);

        await cut.InvokeAsync(() => cut.Find("[data-testid=model-picker-trigger]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=model-picker-opt-opus]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=model-picker-trigger]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=model-picker-opt-none]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=composer-input]").Input("hello"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=send-prompt]").Click());

        await Eventually(() => _runner.LastRequest is not null);
        Assert.Null(_runner.LastRequest!.Model);
    }

    [Fact]
    public async Task Each_model_shows_the_catalog_note_as_a_subtitle_rather_than_as_a_tooltip()
    {
        // The reported defect's other half: every model's one-line note existed already and lived in a
        // title attribute on an <option>, where it was invisible unless the user hovered a list they had
        // to open first. A native option cannot render a second line at all.
        using var ctx = Context();
        var cut = Pane(ctx);

        await cut.InvokeAsync(() => cut.Find("[data-testid=model-picker-trigger]").Click());

        Assert.Contains(
            "Opus while planning",
            cut.Find("[data-testid=model-picker-subtitle-opusplan]").TextContent,
            StringComparison.Ordinal);
        Assert.Equal("H", cut.Find("[data-testid=model-picker-icon-claude-haiku-4-5]").TextContent.Trim());
    }

    [Fact]
    public void The_model_picker_does_not_force_the_shared_form_field_width_into_the_composer()
    {
        // Backlog 24's "one real defect rides along": .studio-input sets min-width: 240px for full-width
        // form fields, and .session-model-picker's own override only shrank the font and the padding, so
        // the composer carried a 240px control. bunit has no layout, so the check is on the class the
        // element carries rather than on its measured width: the composer's control must not be one of
        // the classes the shipped stylesheet gives a wide minimum to.
        var wide = StudioDropdownTests.ClassesWithAWideMinimum(200);

        using var ctx = Context();
        var cut = Pane(ctx);

        foreach (var testId in new[] { "model-picker", "model-picker-trigger" })
        {
            var classes = cut.Find($"[data-testid={testId}]").ClassList;
            var offenders = classes.Where(c => wide.Contains(c, StringComparer.Ordinal)).ToList();
            Assert.True(
                offenders.Count == 0,
                $"{testId} must not carry a wide minimum width in a compact composer, found: "
                + string.Join(" | ", offenders));
        }
    }

    [Fact]
    public async Task A_follow_up_continues_the_running_session_rather_than_starting_another()
    {
        using var ctx = Context();
        StartSession();
        var cut = Pane(ctx);

        await cut.InvokeAsync(() => cut.Find("[data-testid=composer-input]").Input("also add a licence"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=send-prompt]").Click());

        await Eventually(() => _runner.Sent.Count == 1);
        Assert.Equal("also add a licence", _runner.Sent[0]);
    }

    // ---- pasted images (backlog 26) ---------------------------------------------------------

    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 7, 7, 7, 7];

    private static Task PasteAsync(IRenderedComponent<SessionPane> cut, byte[] bytes) =>
        cut.InvokeAsync(() => cut.FindComponent<SessionComposer>().Instance.AddImageAsync(bytes));

    [Fact]
    public async Task A_pasted_image_is_shown_in_the_composer_and_can_be_removed_before_sending()
    {
        using var ctx = Context();
        var cut = Pane(ctx);

        await PasteAsync(cut, PngBytes);

        cut.WaitForAssertion(() =>
        {
            var thumb = Assert.Single(cut.FindAll("[data-testid=composer-image]"));
            Assert.StartsWith("data:image/png;base64,", thumb.QuerySelector("img")!.GetAttribute("src"), StringComparison.Ordinal);
        });

        await cut.InvokeAsync(() => cut.Find("[data-testid=remove-image]").Click());

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=composer-image]")));
        Assert.True(cut.Find("[data-testid=send-prompt]").HasAttribute("disabled"), "nothing is left to send");
    }

    [Fact]
    public async Task An_image_alone_can_start_a_conversation_and_the_composer_empties_once_it_is_sent()
    {
        using var ctx = Context();
        var cut = Pane(ctx);

        await PasteAsync(cut, PngBytes);
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=send-prompt]").HasAttribute("disabled")));
        await cut.InvokeAsync(() => cut.Find("[data-testid=send-prompt]").Click());

        await Eventually(() => _runner.LastRequest is not null, "the runner should have been asked to start");
        var sent = Assert.Single(_runner.LastRequest!.PromptImages!);
        Assert.Equal(Convert.ToBase64String(PngBytes), sent.Base64);
        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("[data-testid=composer-image]"));
            var bubble = cut.Find("[data-testid=bubble-user]");
            Assert.Single(bubble.QuerySelectorAll("[data-testid=bubble-image]"));
        });
    }

    [Fact]
    public async Task A_follow_up_carries_its_images_to_the_agent_and_shows_them_in_the_users_bubble()
    {
        using var ctx = Context();
        StartSession();
        var cut = Pane(ctx);

        await PasteAsync(cut, PngBytes);
        await cut.InvokeAsync(() => cut.Find("[data-testid=composer-input]").Input("what is wrong here?"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=send-prompt]").Click());

        await Eventually(() => _runner.SentImages.Count == 1);
        Assert.Equal("what is wrong here?", _runner.Sent.Single());
        cut.WaitForAssertion(() =>
        {
            var bubble = cut.FindAll("[data-testid=bubble-user]").Last();
            Assert.Contains("what is wrong here?", bubble.TextContent, StringComparison.Ordinal);
            Assert.Single(bubble.QuerySelectorAll("[data-testid=bubble-image]"));
        });
    }

    [Fact]
    public async Task A_paste_that_is_not_an_image_the_agent_can_read_is_refused_in_words()
    {
        using var ctx = Context();
        var cut = Pane(ctx);

        await PasteAsync(cut, "<svg xmlns='http://www.w3.org/2000/svg'/>"u8.ToArray());

        cut.WaitForAssertion(() =>
            Assert.Contains("PNG, JPEG, GIF and WebP", cut.Find("[data-testid=paste-error]").TextContent, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-testid=composer-image]"));
    }

    [Fact]
    public void A_prompt_the_studio_sent_on_a_resume_is_shown_as_the_studios_and_never_as_the_persons()
    {
        // Backlog 184, through the real serializer: the resume prompt read "You" in the first agent-scheduled upgrade.
        using var ctx = Context();
        var id = AgentSessionId.New();
        _store.Save(new SessionRecord(
            id, Workspace, AgentTool.Claude, "upgrade", "prov-1",
            AgentSessionState.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        AgentEvent[] rows =
        [
            new SessionStarted(DateTimeOffset.UtcNow, id),
            new MessageEmitted(DateTimeOffset.UtcNow, TranscriptRole.User, "cut the release and upgrade"),
            new MessageEmitted(DateTimeOffset.UtcNow, TranscriptRole.StudioPrompt, LiveSessionRegistry.UpgradedPrompt("0.9.48")),
        ];
        _store.Append(id, [.. rows.Select(e => AgentEventJson.Deserialize(AgentEventJson.KindOf(e)!, AgentEventJson.Serialize(e))!)]);

        var cut = ctx.Render<SessionsPage>(p => p.Add(c => c.Id, id.Value.ToString("D")));

        var person = Assert.Single(cut.FindAll("[data-testid=stored-transcript] [data-testid=bubble-user]"));
        Assert.Contains("cut the release and upgrade", person.TextContent, StringComparison.Ordinal);
        Assert.Equal("You", person.QuerySelector(".session-turn-role")!.TextContent);

        var studio = Assert.Single(
            cut.FindAll("[data-testid=stored-transcript] [data-testid=bubble-system]"),
            b => b.TextContent.Contains("studio_version", StringComparison.Ordinal));
        Assert.Equal("Studio", studio.QuerySelector(".session-turn-role")!.TextContent);
        Assert.DoesNotContain("session-turn-user", studio.ClassList);
    }

    [Fact]
    public async Task An_oversized_paste_is_refused_before_any_bytes_are_pulled_from_the_page()
    {
        using var ctx = Context();
        var cut = Pane(ctx);

        await cut.InvokeAsync(() =>
            cut.FindComponent<SessionComposer>().Instance.ImagePasted("img1", AgentImageAttachment.MaxBytes + 1L));

        cut.WaitForAssertion(() =>
            Assert.Contains("limit is 3.5 MB", cut.Find("[data-testid=paste-error]").TextContent, StringComparison.Ordinal));
        Assert.DoesNotContain(ctx.JSInterop.Invocations, i => i.Identifier == "fravashiImagePaste.take");
    }

    [Fact]
    public void A_stored_conversation_still_shows_the_images_that_were_pasted_into_it()
    {
        // Through the real serializer, which is what a reload reads back.
        using var ctx = Context();
        var id = AgentSessionId.New();
        _store.Save(new SessionRecord(
            id, Workspace, AgentTool.Claude, "screenshot", "prov-1",
            AgentSessionState.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        AgentEvent[] rows =
        [
            new SessionStarted(DateTimeOffset.UtcNow, id),
            new UserImagesAttached(DateTimeOffset.UtcNow, [AgentImageAttachment.TryCreate(PngBytes, out _)!]),
            new MessageEmitted(DateTimeOffset.UtcNow, TranscriptRole.User, "why is this red?"),
            new MessageEmitted(DateTimeOffset.UtcNow, TranscriptRole.Assistant, "A failing test."),
        ];
        _store.Append(id, [.. rows.Select(e => AgentEventJson.Deserialize(AgentEventJson.KindOf(e)!, AgentEventJson.Serialize(e))!)]);

        var cut = ctx.Render<SessionsPage>(p => p.Add(c => c.Id, id.Value.ToString("D")));

        var bubble = cut.Find("[data-testid=stored-transcript] [data-testid=bubble-user]");
        Assert.Contains("why is this red?", bubble.TextContent, StringComparison.Ordinal);
        var image = Assert.Single(bubble.QuerySelectorAll("[data-testid=bubble-image] img"));
        Assert.Equal("data:image/png;base64," + Convert.ToBase64String(PngBytes), image.GetAttribute("src"));
    }

    [Fact]
    public void Stopping_asks_the_runner_to_stop()
    {
        using var ctx = Context();
        StartSession();
        var cut = Pane(ctx);

        cut.Find("[data-testid=stop-session]").Click();

        Assert.True(_runner.Stopped);
    }

    // ---- the transcript -------------------------------------------------------------------

    [Fact]
    public async Task The_transcript_shows_the_conversation_the_tool_calls_and_how_it_ended()
    {
        using var ctx = Context();
        var session = StartSession();
        var cut = Pane(ctx);

        _runner.Emit(new ToolCallStarted(DateTimeOffset.UtcNow, "toolu_1", "Write", "/workspaces/w1/README.md"));
        _runner.Emit(new ToolCallCompleted(DateTimeOffset.UtcNow, "toolu_1", "Write", true, "File created"));
        _runner.Emit(new MessageEmitted(DateTimeOffset.UtcNow, TranscriptRole.Assistant, "Wrote the readme."));
        _runner.Emit(new SessionEnded(DateTimeOffset.UtcNow, AgentSessionState.Completed, null));

        await Eventually(() => cut.Markup.Contains("Wrote the readme"));
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("write the readme", cut.Markup);          // the user's own turn
            Assert.Contains("Write", cut.Markup);                     // the tool
            Assert.Contains("/workspaces/w1/README.md", cut.Markup);  // what it acted on
            Assert.NotEmpty(cut.FindAll("[data-testid=session-ended-note]"));
        }, TimeSpan.FromSeconds(10));

        // The RESULT is one click away rather than on screen (backlog 18's recorded decision): a session
        // of any length is unreadable when every call prints its output into the transcript.
        await cut.InvokeAsync(() => cut.Find("[data-testid=tool-call-toggle]").Click());
        cut.WaitForAssertion(
            () => Assert.Contains("File created", cut.Find("[data-testid=tool-call-output]").TextContent),
            TimeSpan.FromSeconds(10));
        Assert.Equal(AgentSessionState.Completed, session.State);
    }

    // ---- a distinct shape per element (backlog 18) ------------------------------------------

    [Fact]
    public async Task A_finished_tool_call_is_one_row_until_the_reader_opens_it()
    {
        // The reported defect was a transcript with no hierarchy: prose, tool calls and their output all
        // rendered as the same flat block, so a long session was a wall. A finished call is a row.
        using var ctx = Context();
        StartSession();
        var cut = Pane(ctx);

        _runner.Emit(new ToolCallStarted(DateTimeOffset.UtcNow, "toolu_1", "Read", "/workspaces/w1/notes.txt"));
        _runner.Emit(new ToolCallCompleted(DateTimeOffset.UtcNow, "toolu_1", "Read", true, "alpha\nbeta"));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=tool-call-toggle]")), Patience);
        Assert.Empty(cut.FindAll("[data-testid=tool-call-output]"));
        Assert.DoesNotContain("alpha", cut.Markup, StringComparison.Ordinal);

        await cut.InvokeAsync(() => cut.Find("[data-testid=tool-call-toggle]").Click());

        cut.WaitForAssertion(
            () => Assert.Contains("alpha", cut.Find("[data-testid=tool-call-output]").TextContent, StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_tool_call_that_failed_shows_what_went_wrong_without_being_asked()
    {
        // Collapsing by default is for the calls nobody needs to read. A failure is the reason the
        // reader is looking at the transcript at all, so it is the one that opens itself.
        using var ctx = Context();
        StartSession();
        var cut = Pane(ctx);

        _runner.Emit(new ToolCallStarted(DateTimeOffset.UtcNow, "toolu_1", "Bash", "npm test"));
        _runner.Emit(new ToolCallCompleted(DateTimeOffset.UtcNow, "toolu_1", "Bash", false, "ENOENT: no such file"));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=tool-call-output]")), Patience);
        Assert.Contains("ENOENT", cut.Find("[data-testid=tool-call-output]").TextContent, StringComparison.Ordinal);
        Assert.Contains("session-tool-fail", cut.Find("[data-testid=tool-call]").ClassList);
    }

    [Fact]
    public async Task A_tool_calls_stderr_is_shown_distinctly_so_a_failed_sub_command_is_not_hidden()
    {
        // Backlog 104 D3, end to end through the live transcript: the runner completes a call carrying
        // stderr, and the transcript shows it as its own block rather than dropping it (which read as an
        // empty success before the fix).
        using var ctx = Context();
        StartSession();
        var cut = Pane(ctx);

        _runner.Emit(new ToolCallStarted(DateTimeOffset.UtcNow, "toolu_1", "Bash", "true && missing"));
        _runner.Emit(new ToolCallCompleted(
            DateTimeOffset.UtcNow, "toolu_1", "Bash", true, string.Empty, "missing: command not found"));

        cut.WaitForAssertion(
            () => Assert.Contains(
                "missing: command not found",
                cut.Find("[data-testid=tool-call-stderr]").TextContent,
                StringComparison.Ordinal),
            Patience);
    }

    [Fact]
    public async Task A_running_tool_call_says_so_and_a_finished_one_does_not()
    {
        using var ctx = Context();
        StartSession();
        var cut = Pane(ctx);

        _runner.Emit(new ToolCallStarted(DateTimeOffset.UtcNow, "toolu_1", "Grep", "FocusAsync"));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=tool-call-state]")), Patience);
        Assert.Equal("running", cut.Find("[data-testid=tool-call-state]").TextContent.Trim());
        Assert.Contains("session-tool-running", cut.Find("[data-testid=tool-call]").ClassList);

        _runner.Emit(new ToolCallCompleted(DateTimeOffset.UtcNow, "toolu_1", "Grep", true, "3 matches"));

        cut.WaitForAssertion(
            () => Assert.Contains("session-tool-ok", cut.Find("[data-testid=tool-call]").ClassList),
            Patience);
        Assert.DoesNotContain("running", cut.Find("[data-testid=tool-call-state]").TextContent);
    }

    [Fact]
    public async Task The_user_turn_and_the_agent_prose_are_not_the_same_element()
    {
        // "Flat and indistinguishable" was the whole complaint: both roles wore one bubble class and
        // differed only by a background colour.
        using var ctx = Context();
        StartSession("fix the filter");
        var cut = Pane(ctx);
        _runner.Emit(new MessageEmitted(DateTimeOffset.UtcNow, TranscriptRole.Assistant, "Found it."));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=bubble-assistant]")), Patience);

        var user = cut.Find("[data-testid=bubble-user]");
        var agent = cut.Find("[data-testid=bubble-assistant]");
        Assert.Contains("session-turn-user", user.ClassList);
        Assert.Contains("session-turn-assistant", agent.ClassList);
        Assert.DoesNotContain("session-turn-user", agent.ClassList);
    }

    [Fact]
    public async Task A_session_that_failed_is_an_error_and_not_another_grey_note()
    {
        using var ctx = Context();
        StartSession();
        var cut = Pane(ctx);

        _runner.Emit(new SessionEnded(DateTimeOffset.UtcNow, AgentSessionState.Failed, "the CLI exited with 1"));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=session-ended-note]")), Patience);
        var note = cut.Find("[data-testid=session-ended-note]");
        Assert.Contains("session-error", note.ClassList);
        Assert.Contains("the CLI exited with 1", note.TextContent, StringComparison.Ordinal);
    }

    // ---- typed turns: the untyped marker (backlog 25 increment 5) -----------------------------

    /// <summary>Drives one finished turn through the pane, opened the way the real runner opens it.</summary>
    private async Task<IRenderedComponent<SessionPane>> FinishedTurn(
        BunitContext ctx, bool schemaSent, string prose, TurnCompleted turn)
    {
        var session = StartSession("what is 7 times 8");
        var cut = Pane(ctx);

        _runner.Emit(new SessionStarted(DateTimeOffset.UtcNow, session.Id, schemaSent));
        _runner.Emit(new MessageEmitted(DateTimeOffset.UtcNow, TranscriptRole.Assistant, prose));
        _runner.Emit(turn);

        await Eventually(() => session.Transcript.OfType<TurnCompleted>().Any());
        cut.WaitForAssertion(() => Assert.Contains(prose, cut.Markup, StringComparison.Ordinal), Patience);
        return cut;
    }

    [Fact]
    public async Task A_typed_turn_that_ended_in_its_document_is_one_answer_and_carries_no_marker()
    {
        using var ctx = Context();

        var cut = await FinishedTurn(ctx, schemaSent: true, "56", new TurnCompleted(
            DateTimeOffset.UtcNow, null, false, "completed",
            AgentTurnDocumentReader.Read("""{"version":"1","kind":"answer","message":"56"}""")));

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll("[data-testid=bubble-assistant]"));
            Assert.Empty(cut.FindAll("[data-testid=untyped-marker]"));
        }, Patience);
    }

    [Fact]
    public async Task A_typed_sessions_turn_that_answered_in_prose_is_shown_once_and_marked_untyped()
    {
        // ADR 0022 outcome ii: success on every field and no document. Decided 2026-09-14: the prose
        // is the answer, shown once, quietly marked, never presented as the typed answer it is not.
        using var ctx = Context();

        var cut = await FinishedTurn(ctx, schemaSent: true, "The schema contradicts itself, so here it is in prose.",
            new TurnCompleted(DateTimeOffset.UtcNow, null, false, "completed"));

        cut.WaitForAssertion(() =>
        {
            var bubble = Assert.Single(cut.FindAll("[data-testid=bubble-assistant]"));
            Assert.Single(bubble.QuerySelectorAll("[data-testid=untyped-marker]"));
            Assert.Empty(cut.FindAll("[data-testid=failed-turn-note]"));
        }, Patience);
        Assert.DoesNotContain("session-error", cut.Find("[data-testid=untyped-marker]").ClassList);
    }

    [Fact]
    public async Task A_turn_on_a_session_that_sent_no_schema_carries_no_marker()
    {
        // No document is simply how an untyped session's turns end, so there is nothing to mark.
        using var ctx = Context();

        var cut = await FinishedTurn(ctx, schemaSent: false, "Wrote the readme.",
            new TurnCompleted(DateTimeOffset.UtcNow, null, false, "completed"));

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll("[data-testid=bubble-assistant]"));
            Assert.Empty(cut.FindAll("[data-testid=untyped-marker]"));
        }, Patience);
    }

    [Fact]
    public void A_stored_untyped_turn_renders_the_same_marker()
    {
        // Through the real serializer: the claim is that a restart does not turn an untyped answer into what
        // looks like a typed one.
        using var ctx = Context();
        var id = AgentSessionId.New();
        _store.Save(new SessionRecord(
            id, Workspace, AgentTool.Claude, "multiply", "prov-1",
            AgentSessionState.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        AgentEvent[] rows =
        [
            new SessionStarted(DateTimeOffset.UtcNow, id, TypedTurns: true),
            new MessageEmitted(DateTimeOffset.UtcNow, TranscriptRole.User, "what is 7 times 8"),
            new MessageEmitted(DateTimeOffset.UtcNow, TranscriptRole.Assistant, "In prose: 56."),
            new TurnCompleted(DateTimeOffset.UtcNow, null, false, "completed"),
            new MessageEmitted(DateTimeOffset.UtcNow, TranscriptRole.User, "and 6 times 7"),
            new MessageEmitted(DateTimeOffset.UtcNow, TranscriptRole.Assistant, "42"),
            new TurnCompleted(
                DateTimeOffset.UtcNow, null, false, "completed",
                AgentTurnDocumentReader.Read("""{"version":"1","kind":"answer","message":"42"}""")),
        ];
        _store.Append(id, [.. rows.Select(e => AgentEventJson.Deserialize(AgentEventJson.KindOf(e)!, AgentEventJson.Serialize(e))!)]);

        var cut = ctx.Render<SessionsPage>(p => p.Add(c => c.Id, id.Value.ToString("D")));

        var stored = cut.Find("[data-testid=stored-transcript]");
        var marker = Assert.Single(stored.QuerySelectorAll("[data-testid=untyped-marker]"));
        Assert.Contains("In prose: 56.", marker.Closest("[data-testid=bubble-assistant]")!.TextContent, StringComparison.Ordinal);
    }

    // ---- typed turns: one answer even when the prose and the document differ (backlog 25) -------

    [Fact]
    public async Task A_typed_turn_whose_document_reworded_the_prose_shows_one_answer_and_folds_the_draft()
    {
        // Measured against the pinned CLI on 2026-09-14: "17 times 3 equals 51." and then the document's
        // "17 times 3 equals **51**.", both committed by the parser. The reader gets the document's answer open
        // and the earlier wording folded, not two bubbles saying one thing.
        using var ctx = Context();
        var session = StartSession("what is 17 times 3");
        var cut = Pane(ctx);

        _runner.Emit(new SessionStarted(DateTimeOffset.UtcNow, session.Id, TypedTurns: true));
        _runner.Emit(new MessageEmitted(DateTimeOffset.UtcNow, TranscriptRole.Assistant, "17 times 3 equals 51."));
        _runner.Emit(new MessageEmitted(DateTimeOffset.UtcNow, TranscriptRole.Assistant, "17 times 3 equals **51**."));
        _runner.Emit(new TurnCompleted(
            DateTimeOffset.UtcNow, null, false, "completed",
            AgentTurnDocumentReader.Read("""{"version":"1","kind":"answer","message":"17 times 3 equals **51**."}""")));

        await Eventually(() => session.Transcript.OfType<TurnCompleted>().Any());
        cut.WaitForAssertion(() =>
        {
            var draft = Assert.Single(cut.FindAll("[data-testid=superseded-draft]"));
            Assert.Equal("details", draft.TagName, ignoreCase: true);
            Assert.False(draft.HasAttribute("open"));
            Assert.Contains("17 times 3 equals 51.", draft.TextContent, StringComparison.Ordinal);

            var open = cut.FindAll("[data-testid=bubble-assistant]")
                .Where(b => b.Closest("[data-testid=superseded-draft]") is null)
                .ToList();
            Assert.Contains("51", Assert.Single(open).TextContent, StringComparison.Ordinal);
            Assert.Empty(cut.FindAll("[data-testid=untyped-marker]"));
        }, Patience);
    }

    [Fact]
    public void A_stored_typed_turn_folds_the_same_draft()
    {
        using var ctx = Context();
        var id = AgentSessionId.New();
        _store.Save(new SessionRecord(
            id, Workspace, AgentTool.Claude, "multiply", "prov-1",
            AgentSessionState.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        AgentEvent[] rows =
        [
            new SessionStarted(DateTimeOffset.UtcNow, id, TypedTurns: true),
            new MessageEmitted(DateTimeOffset.UtcNow, TranscriptRole.User, "what is 17 times 3"),
            new MessageEmitted(DateTimeOffset.UtcNow, TranscriptRole.Assistant, "17 times 3 is **51**."),
            new MessageEmitted(
                DateTimeOffset.UtcNow, TranscriptRole.Assistant,
                "You requested that I call the StructuredOutput tool. Here is your response in structured format."),
            new TurnCompleted(
                DateTimeOffset.UtcNow, null, false, "completed",
                AgentTurnDocumentReader.Read("""{"version":"1","kind":"answer","message":"17 times 3 is **51**."}""")),
        ];
        _store.Append(id, [.. rows.Select(e => AgentEventJson.Deserialize(AgentEventJson.KindOf(e)!, AgentEventJson.Serialize(e))!)]);

        var cut = ctx.Render<SessionsPage>(p => p.Add(c => c.Id, id.Value.ToString("D")));

        var stored = cut.Find("[data-testid=stored-transcript]");
        var draft = Assert.Single(stored.QuerySelectorAll("[data-testid=superseded-draft]"));
        Assert.Contains("StructuredOutput tool", draft.TextContent, StringComparison.Ordinal);
        var open = stored.QuerySelectorAll("[data-testid=bubble-assistant]")
            .Where(b => b.Closest("[data-testid=superseded-draft]") is null)
            .ToList();
        Assert.Contains("51", Assert.Single(open).TextContent, StringComparison.Ordinal);
    }

    // ---- typed turns: failure, exhaustion and resume (backlog 25 increment 6) -----------------

    [Fact]
    public async Task A_turn_that_exhausted_its_attempts_says_so_and_the_pane_offers_to_continue_it()
    {
        using var ctx = Context();
        var session = StartSession("what is the nonce");
        var cut = Pane(ctx);

        _runner.Emit(new SessionStarted(DateTimeOffset.UtcNow, session.Id, TypedTurns: true));
        _runner.Emit(new SessionRegistered(DateTimeOffset.UtcNow, "prov-exhausted", null));
        _runner.Emit(new TurnCompleted(
            DateTimeOffset.UtcNow, null, true, AgentTurnEnding.RetriesExhausted, null, 5,
            "Output does not match required schema: /nonce: must match pattern",
            "Failed to provide valid structured output after 5 attempts"));
        _runner.Emit(new SessionEnded(
            DateTimeOffset.UtcNow, AgentSessionState.Failed, ClaudeSessionRunner.ExhaustedEnding, Resumable: true));

        await Eventually(() => session.IsResumable);
        cut.WaitForAssertion(() =>
        {
            var note = cut.Find("[data-testid=failed-turn-note]").TextContent;
            Assert.Contains("failed validation after 5 attempts", note, StringComparison.Ordinal);
            Assert.Contains("/nonce: must match pattern", note, StringComparison.Ordinal);

            // The process ending is what can happen next, not a second error on top of the failed turn.
            var ended = cut.Find("[data-testid=session-ended-note]");
            Assert.DoesNotContain("session-error", ended.ClassList);
            Assert.Single(cut.FindAll("[data-testid=continue-conversation]"));
        }, Patience);

        await cut.InvokeAsync(() => cut.Find("[data-testid=continue-conversation]").Click());

        // Continued IN PLACE: the same session id, now started under --resume with nothing said.
        await Eventually(() => _runner.LastRequest?.ResumeSessionId is not null);
        Assert.Equal(session.Id, _runner.LastRequest!.SessionId);
        Assert.Equal("prov-exhausted", _runner.LastRequest!.ResumeSessionId);
        Assert.Equal(string.Empty, _runner.LastRequest.Prompt);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=continue-conversation]")), Patience);
    }

    // ---- a turn the network cut off (backlog 229) --------------------------------------------

    /// <summary>The pinned CLI's own failed turn on a dead resolver, as the parser hands it on (measured 2026-10-07).</summary>
    private void FailTurnOnTheNetwork()
    {
        const string said = "API Error: Can't reach the API server, check your internet or DNS (EAI_AGAIN)";
        _runner.Emit(new MessageEmitted(DateTimeOffset.UtcNow, TranscriptRole.Assistant, said));
        _runner.Emit(new ApiErrorReported(DateTimeOffset.UtcNow, said));
        _runner.Emit(new TurnCompleted(DateTimeOffset.UtcNow, null, true, "api_error"));
    }

    [Fact]
    public async Task A_turn_the_network_cut_off_shows_that_it_waits_since_when_and_for_what()
    {
        using var ctx = Context();
        var session = StartSession();
        var cut = Pane(ctx);
        _runner.Emit(new SessionRegistered(DateTimeOffset.UtcNow, "prov-net", null));

        FailTurnOnTheNetwork();

        await Eventually(() => session.NetworkWait?.Probe is not null);
        cut.WaitForAssertion(() =>
        {
            var words = cut.Find("[data-testid=session-network-wait-words]").TextContent;
            Assert.StartsWith("Waiting for the network since ", words, StringComparison.Ordinal);
            Assert.Contains("could not reach api.anthropic.com: name not resolved", words, StringComparison.Ordinal);
            Assert.Contains("continues this conversation by itself when it is back", words, StringComparison.Ordinal);
            Assert.Contains("unreachable since", cut.Find("[data-testid=session-network-wait-probe]").TextContent, StringComparison.Ordinal);
            Assert.Single(cut.FindAll("[data-testid=network-retry-now]"));
            Assert.Single(cut.FindAll("[data-testid=network-stop-waiting]"));
        }, Patience);
        Assert.Empty(_runner.Sent);
    }

    [Fact]
    public async Task The_waiting_banner_says_since_in_the_persons_zone()
    {
        // Backlog 246: the banner is the person's clock (Kolkata, +05:30), the plain words stay the process's.
        using var ctx = Context();
        ctx.Services.AddSingleton(PersonZones.Fixed("Asia/Kolkata"));
        var session = StartSession();
        var cut = Pane(ctx);
        _runner.Emit(new SessionRegistered(DateTimeOffset.UtcNow, "prov-net", null));

        FailTurnOnTheNetwork();

        await Eventually(() => session.NetworkWait?.Probe is not null);
        var since = session.NetworkWait!.Since;
        var kolkata = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        cut.WaitForAssertion(() =>
        {
            var words = cut.Find("[data-testid=session-network-wait-words]").TextContent;
            Assert.StartsWith(
                $"Waiting for the network since {Fravashi.Web.Services.PersonTime.Format(since, kolkata, DateTimeOffset.UtcNow)}: could not reach api.anthropic.com",
                words,
                StringComparison.Ordinal);
            Assert.Equal("person", cut.Find("[data-testid=session-network-wait-words] time").GetAttribute("data-zone"));
        }, Patience);
        Assert.StartsWith(
            $"Waiting for the network since {since.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)}:",
            session.NetworkWait.Words,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retry_now_continues_the_conversation_only_once_the_host_answers()
    {
        using var ctx = Context();
        var session = StartSession();
        var cut = Pane(ctx);
        _runner.Emit(new SessionRegistered(DateTimeOffset.UtcNow, "prov-net", null));
        FailTurnOnTheNetwork();
        cut.WaitForAssertion(() => cut.Find("[data-testid=network-retry-now]"), Patience);

        await cut.InvokeAsync(() => cut.Find("[data-testid=network-retry-now]").Click());
        await Eventually(() => _reachability.Probes >= 2);
        Assert.Empty(_runner.Sent);
        Assert.NotNull(session.NetworkWait);

        _reachability.Reachable = true;
        await cut.InvokeAsync(() => cut.Find("[data-testid=network-retry-now]").Click());

        await Eventually(() => _runner.Sent.Count == 1);
        Assert.StartsWith("The network dropped (could not reach api.anthropic.com: name not resolved) and is back now.", _runner.Sent[0], StringComparison.Ordinal);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=session-network-wait]")), Patience);
    }

    [Fact]
    public async Task Stop_on_the_waiting_banner_stops_the_conversation_and_sends_nothing()
    {
        using var ctx = Context();
        var session = StartSession();
        var cut = Pane(ctx);
        _runner.Emit(new SessionRegistered(DateTimeOffset.UtcNow, "prov-net", null));
        FailTurnOnTheNetwork();
        cut.WaitForAssertion(() => cut.Find("[data-testid=network-stop-waiting]"), Patience);

        await cut.InvokeAsync(() => cut.Find("[data-testid=network-stop-waiting]").Click());

        Assert.True(_runner.Stopped);
        Assert.Null(session.NetworkWait);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=session-network-wait]")), Patience);
        Assert.Empty(_runner.Sent);
    }

    [Fact]
    public async Task A_session_that_failed_for_any_other_reason_is_not_offered_a_continue()
    {
        using var ctx = Context();
        StartSession();
        var cut = Pane(ctx);

        _runner.Emit(new SessionRegistered(DateTimeOffset.UtcNow, "prov-1", null));
        _runner.Emit(new SessionEnded(DateTimeOffset.UtcNow, AgentSessionState.Failed, "the CLI exited with 1"));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=session-ended-note]")), Patience);
        Assert.Empty(cut.FindAll("[data-testid=continue-conversation]"));
    }

    [Fact]
    public async Task A_turn_that_ran_out_of_budget_is_a_failed_turn_naming_the_budget()
    {
        using var ctx = Context();
        var session = StartSession();
        var cut = Pane(ctx);

        _runner.Emit(new SessionStarted(DateTimeOffset.UtcNow, session.Id, TypedTurns: true));
        _runner.Emit(new TurnCompleted(
            DateTimeOffset.UtcNow, null, true, AgentTurnEnding.BudgetExhausted,
            ErrorText: "Reached maximum budget ($0.50)"));

        cut.WaitForAssertion(() =>
        {
            var note = cut.Find("[data-testid=failed-turn-note]");
            Assert.Contains("Reached maximum budget ($0.50)", note.TextContent, StringComparison.Ordinal);
            Assert.Contains("session-error", note.ClassList);
            Assert.Empty(cut.FindAll("[data-testid=untyped-marker]"));
        }, Patience);
        await Task.CompletedTask;
    }

    // ---- what the agent is doing (backlog 20) -----------------------------------------------

    [Fact]
    public async Task From_the_moment_a_turn_is_submitted_the_panel_says_it_is_working()
    {
        // The reported gap is between pressing send and the first byte coming back, and the CLI says
        // nothing at all in it. The studio knows the turn went out, so the studio is what says so.
        using var ctx = Context();
        var cut = Pane(ctx);

        await cut.InvokeAsync(() => cut.Find("[data-testid=composer-input]").Input("fix the filter"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=send-prompt]").Click());

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=session-activity]")), Patience);
        Assert.Contains("Sent to the agent", cut.Find("[data-testid=session-activity]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Thinking_is_shown_while_the_model_reasons_and_stops_when_it_answers()
    {
        using var ctx = Context();
        StartSession();
        var cut = Pane(ctx);

        _runner.Emit(new TurnActivity(DateTimeOffset.UtcNow, AgentTurnPhase.Thinking));

        cut.WaitForAssertion(
            () => Assert.Contains(
                "Thinking",
                cut.Find("[data-testid=session-activity]").TextContent,
                StringComparison.Ordinal),
            Patience);

        _runner.Emit(new MessageEmitted(DateTimeOffset.UtcNow, TranscriptRole.Assistant, "Found it."));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=session-activity]")), Patience);
    }

    // ---- the one-line usage strip (backlog 18 and 19) ----------------------------------------

    [Fact]
    public async Task Usage_is_one_strip_that_leads_with_context_and_is_visible_while_the_session_lives()
    {
        // It was a four-line stack (tokens, cost, tool calls, model) taking a third of a narrow panel,
        // and context usage was not in it at all because nothing measured it.
        using var ctx = Context();
        StartSession();
        var cut = Pane(ctx);

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=session-usage]")), Patience);

        _runner.Emit(new StatsUpdated(
            DateTimeOffset.UtcNow,
            new AgentStatsSnapshot(1200, 400, 20_000, 0.38m, 2, 0, 1, ContextTokens: 46_000, ContextWindow: 200_000)));

        cut.WaitForAssertion(
            () => Assert.Equal("23%", cut.Find("[data-testid=session-context]").TextContent.Trim()),
            Patience);
        Assert.Single(cut.FindAll("[data-testid=session-context-meter]"));
        Assert.Equal("$0.38", cut.Find("[data-testid=session-cost]").TextContent.Trim());
        Assert.Equal("2 tools", cut.Find("[data-testid=session-tools]").TextContent.Trim());
    }

    [Fact]
    public async Task The_context_meter_splits_the_fixed_prefix_from_the_conversation_and_the_tooltip_puts_numbers_to_it()
    {
        // 39% after two short questions is TRUE, and the owner called it invalid because the reason was
        // invisible: roughly half of the occupancy is a fixed prefix nobody typed (agent setup, tool
        // schemas, project instructions). The meter dims that segment and the tooltip states the split.
        using var ctx = Context();
        StartSession();
        var cut = Pane(ctx);

        _runner.Emit(new StatsUpdated(
            DateTimeOffset.UtcNow,
            new AgentStatsSnapshot(1200, 400, 20_000, 0.38m, 2, 0, 1,
                ContextTokens: 78_000, ContextWindow: 200_000, BaselineTokens: 40_000)));

        cut.WaitForAssertion(
            () => Assert.Equal("39%", cut.Find("[data-testid=session-context]").TextContent.Trim()),
            Patience);

        var fixedSegment = cut.Find("[data-testid=session-context-fixed]");
        Assert.Contains("width: 20%", fixedSegment.GetAttribute("style"));

        var title = cut.Find("[data-testid=session-context]").GetAttribute("title") ?? string.Empty;
        Assert.Contains("78,000 of 200,000", title);
        Assert.Contains("~40,000 is a fixed prefix", title);
        Assert.Contains("~38,000 is the conversation", title);
        Assert.Contains("caching lowers cost, not context", title);
    }

    [Fact]
    public async Task A_snapshot_without_a_baseline_keeps_the_single_segment_meter_and_still_names_the_prefix()
    {
        // Transcripts stored before the baseline existed read back as zero: no invented split, but the
        // tooltip still says WHAT rides in every turn, because that is the part the owner cannot see.
        using var ctx = Context();
        StartSession();
        var cut = Pane(ctx);

        _runner.Emit(new StatsUpdated(
            DateTimeOffset.UtcNow,
            new AgentStatsSnapshot(1200, 400, 20_000, 0.38m, 2, 0, 1,
                ContextTokens: 46_000, ContextWindow: 200_000)));

        cut.WaitForAssertion(
            () => Assert.Equal("23%", cut.Find("[data-testid=session-context]").TextContent.Trim()),
            Patience);

        Assert.Empty(cut.FindAll("[data-testid=session-context-fixed]"));
        var title = cut.Find("[data-testid=session-context]").GetAttribute("title") ?? string.Empty;
        Assert.Contains("Every turn carries a fixed prefix", title);
    }

    [Fact]
    public async Task Context_usage_shows_the_tokens_and_no_meter_until_the_window_is_known()
    {
        // The window arrives only with a finished turn. A percentage before that would be a made-up
        // denominator, which is worse on a strip whose whole job is to be believed at a glance.
        using var ctx = Context();
        StartSession();
        var cut = Pane(ctx);

        _runner.Emit(new StatsUpdated(
            DateTimeOffset.UtcNow,
            new AgentStatsSnapshot(10, 4, 24_000, 0m, 0, 0, 0, ContextTokens: 24_100, ContextWindow: 0)));

        cut.WaitForAssertion(
            () => Assert.Equal("24.1k", cut.Find("[data-testid=session-context]").TextContent.Trim()),
            Patience);
        Assert.Empty(cut.FindAll("[data-testid=session-context-meter]"));
    }

    [Fact]
    public void The_totals_strip_shows_today_month_and_all_time_cost_and_tokens()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<SessionTotalsStrip>(p => p.Add(c => c.Totals, Totals()));

        Assert.Single(cut.FindAll("[data-testid=session-totals]"));
        Assert.Equal("$0.05", cut.Find("[data-testid=session-totals-today-cost]").TextContent.Trim());
        Assert.Equal("120k tokens", cut.Find("[data-testid=session-totals-today-tokens]").TextContent.Trim());
        Assert.Equal("$0.42", cut.Find("[data-testid=session-totals-month-cost]").TextContent.Trim());
        Assert.Equal("1.23M tokens", cut.Find("[data-testid=session-totals-month-tokens]").TextContent.Trim());
        Assert.Equal("$12.50", cut.Find("[data-testid=session-totals-alltime-cost]").TextContent.Trim());
        Assert.Equal("9.9M tokens", cut.Find("[data-testid=session-totals-alltime-tokens]").TextContent.Trim());
    }

    // ---- spend against the configured ceilings (backlog 62) -----------------------------------

    [Fact]
    public void The_totals_strip_says_nothing_about_limits_when_none_are_configured()
    {
        // The studio ships with no ceilings, and a strip that showed an empty "of" for them would be
        // inventing a limit nobody set.
        using var ctx = new BunitContext();

        var cut = ctx.Render<SessionTotalsStrip>(p => p.Add(c => c.Totals, Totals()));

        Assert.Empty(cut.FindAll("[data-testid=session-totals-today-limit]"));
        Assert.Empty(cut.FindAll("[data-testid=session-totals-month-limit]"));
    }

    [Fact]
    public void The_totals_strip_shows_each_configured_limit_beside_the_spend_it_governs()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<SessionTotalsStrip>(p => p
            .Add(c => c.Totals, Totals())
            .Add(c => c.Limits, new AgentSpendLimits(null, 5m, 50m)));

        Assert.Equal("of $5.00", cut.Find("[data-testid=session-totals-today-limit]").TextContent.Trim());
        Assert.Equal("of $50.00", cut.Find("[data-testid=session-totals-month-limit]").TextContent.Trim());

        // Neither is reached, so neither row is marked: "at-limit" is the strip's loud state and it must
        // not fire while there is room.
        Assert.DoesNotContain("at-limit", cut.Find("[data-testid=session-totals-today]").ClassName);
        Assert.DoesNotContain("at-limit", cut.Find("[data-testid=session-totals-month]").ClassName);
    }

    [Fact]
    public void The_totals_strip_marks_the_row_whose_limit_the_guard_would_refuse_on()
    {
        // $0.42 this month against a $0.42 ceiling: reaching a limit is reaching it, and the row says so
        // in a word rather than leaving the reader to compare two numbers.
        using var ctx = new BunitContext();

        var cut = ctx.Render<SessionTotalsStrip>(p => p
            .Add(c => c.Totals, Totals())
            .Add(c => c.Limits, new AgentSpendLimits(null, 5m, 0.42m)));

        Assert.Contains("at-limit", cut.Find("[data-testid=session-totals-month]").ClassName);
        Assert.Equal("of $0.42 - reached", cut.Find("[data-testid=session-totals-month-limit]").TextContent.Trim());
        Assert.DoesNotContain("at-limit", cut.Find("[data-testid=session-totals-today]").ClassName);
    }

    // ---- backlog 126 (decided 2026-09-21): the picker says what the conversation really runs on ----

    [Fact]
    public void Between_turns_the_composer_names_the_model_the_conversation_is_really_on_even_when_the_picker_cannot()
    {
        // After a reload the picker read "Default" for a conversation running on a dated Haiku id the picker
        // does not list. The picker stays a picker; the truth is said beside it, read-only.
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<SessionComposer>(p => p
            .Add(c => c.Model, null)
            .Add(c => c.RunningModel, "claude-haiku-4-5-20251001"));

        var label = cut.Find("[data-testid=model-actual]");
        Assert.Contains("claude-haiku-4-5-20251001", label.TextContent, StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll("[data-testid=model-picker]"));
    }

    [Fact]
    public void The_label_is_not_shown_when_it_would_only_repeat_the_picker_or_when_nothing_is_known()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var same = ctx.Render<SessionComposer>(p => p
            .Add(c => c.Model, "claude-opus-5")
            .Add(c => c.RunningModel, "claude-opus-5"));
        Assert.Empty(same.FindAll("[data-testid=model-actual]"));

        var unknown = ctx.Render<SessionComposer>();
        Assert.Empty(unknown.FindAll("[data-testid=model-actual]"));
    }

    [Fact]
    public void The_composer_says_nothing_about_a_session_limit_when_none_is_set()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<SessionComposer>();

        Assert.Empty(cut.FindAll("[data-testid=composer-session-limit]"));
    }

    [Fact]
    public void The_composer_states_the_per_session_limit_where_the_money_is_about_to_be_spent()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<SessionComposer>(p => p.Add(c => c.SessionLimitUsd, 7.5m));

        Assert.Equal(
            "Limit $7.50 / session.",
            cut.Find("[data-testid=composer-session-limit]").TextContent.Trim());
    }

    /// <summary>Today $0.05, this month $0.42, all time $12.50: one shape for every strip assertion.</summary>
    private static SessionUsageTotals Totals() => new(
        new UsageTotal(0.05m, 120_000, 1),
        new UsageTotal(0.42m, 1_234_000, 3),
        new UsageTotal(12.50m, 9_900_000, 40));

    // ---- starting fresh, and Stop meaning what it says (backlog 21) --------------------------

    [Fact]
    public void New_conversation_is_a_header_action_in_the_same_place_on_both_surfaces()
    {
        using var ctx = Context();
        var wide = Pane(ctx);
        var narrow = ctx.Render<SessionPane>(p => p
            .Add(c => c.WorkspaceId, Workspace)
            .Add(c => c.WorkingDirectory, CopyPath)
            .Add(c => c.Compact, true));

        foreach (var cut in new[] { wide, narrow })
        {
            var action = cut.Find("[data-testid=new-conversation]");
            Assert.Equal("session-pane-head", action.ParentElement?.GetAttribute("data-testid"));

            // And never in the row Stop lives in, which is the confusion that was reported.
            Assert.Empty(cut.FindAll("[data-testid=session-composer] [data-testid=new-conversation]"));
        }
    }

    [Fact]
    public async Task Starting_a_new_conversation_while_a_turn_runs_asks_before_ending_it()
    {
        using var ctx = Context();
        StartSession("the first conversation");
        var cut = Pane(ctx);

        await cut.InvokeAsync(() => cut.Find("[data-testid=new-conversation]").Click());

        cut.WaitForAssertion(
            () => Assert.NotEmpty(cut.FindAll("[data-testid=new-conversation-confirm]")),
            TimeSpan.FromSeconds(10));
        Assert.False(_runner.Stopped, "nothing may be killed before the user answers");
        Assert.Contains("the first conversation", cut.Markup, StringComparison.Ordinal);

        await cut.InvokeAsync(() => cut.Find("[data-testid=new-conversation-confirm-yes]").Click());

        cut.WaitForAssertion(
            () =>
            {
                Assert.True(_runner.Stopped);
                Assert.NotEmpty(cut.FindAll("[data-testid=session-empty]"));
                Assert.DoesNotContain("the first conversation", cut.Markup, StringComparison.Ordinal);
            },
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Keeping_going_leaves_the_running_conversation_exactly_where_it_was()
    {
        using var ctx = Context();
        StartSession("the first conversation");
        var cut = Pane(ctx);
        await cut.InvokeAsync(() => cut.Find("[data-testid=new-conversation]").Click());

        await cut.InvokeAsync(() => cut.Find("[data-testid=new-conversation-confirm-no]").Click());

        cut.WaitForAssertion(
            () =>
            {
                Assert.Empty(cut.FindAll("[data-testid=new-conversation-confirm]"));
                Assert.Contains("the first conversation", cut.Markup, StringComparison.Ordinal);
            },
            TimeSpan.FromSeconds(10));
        Assert.False(_runner.Stopped);
    }

    // ---- work item #352: a graph-only conversation beside the running one -------------------------------------

    [Fact]
    public async Task With_a_conversation_running_new_conversation_offers_notes_and_items_only_beside_it()
    {
        using var ctx = Context();
        var running = StartSession("the first conversation");
        var cut = Pane(ctx);
        await cut.InvokeAsync(() => cut.Find("[data-testid=new-conversation]").Click());

        var offer = cut.WaitForElement("[data-testid=new-conversation-graph-only]", Patience);
        Assert.Equal(AgentGraphOnlyTools.Label, offer.TextContent.Trim());

        await cut.InvokeAsync(() => cut.Find("[data-testid=new-conversation-graph-only]").Click());

        cut.WaitForAssertion(
            () =>
            {
                Assert.NotEmpty(cut.FindAll("[data-testid=session-empty]"));
                Assert.Equal(AgentGraphOnlyTools.Label, cut.Find("[data-testid=graph-only-chip]").TextContent.Trim());
                Assert.NotEmpty(cut.FindAll("[data-testid=graph-only-forced]"));
            },
            Patience);
        Assert.False(_runner.Stopped, "the running conversation keeps running");

        await cut.InvokeAsync(() => cut.Find("[data-testid=composer-input]").Input("write these up"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=send-prompt]").Click());

        await Eventually(() => _runner.LastRequest is { Prompt: "write these up" });
        Assert.True(_runner.LastRequest!.GraphOnly);
        Assert.True(running.IsRunning);
        Assert.Same(running, _registry.RunningFor(Workspace));
        Assert.True(_registry.RunningGraphOnlyFor(Workspace)!.IsGraphOnly);
        cut.WaitForAssertion(
            () => Assert.Equal(AgentGraphOnlyTools.Label, cut.Find("[data-testid=graph-only-chip]").TextContent.Trim()),
            Patience);
        Assert.Empty(cut.FindAll("[data-testid=session-error]"));
    }

    [Fact]
    public async Task Notes_and_items_only_can_be_chosen_explicitly_for_a_fresh_conversation()
    {
        using var ctx = Context();
        var cut = Pane(ctx);
        Assert.Empty(cut.FindAll("[data-testid=graph-only-chip]"));

        await cut.InvokeAsync(() => cut.Find("[data-testid=graph-only-toggle]").Change(true));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=graph-only-chip]")), Patience);
        await cut.InvokeAsync(() => cut.Find("[data-testid=composer-input]").Input("note these down"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=send-prompt]").Click());

        await Eventually(() => _runner.LastRequest is not null);
        Assert.True(_runner.LastRequest!.GraphOnly);
        Assert.Null(_registry.RunningFor(Workspace));
    }

    [Fact]
    public async Task A_fresh_conversation_is_an_editing_one_unless_notes_and_items_only_is_chosen()
    {
        using var ctx = Context();
        var cut = Pane(ctx);
        Assert.Empty(cut.FindAll("[data-testid=graph-only-forced]"));

        await cut.InvokeAsync(() => cut.Find("[data-testid=composer-input]").Input("fix it"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=send-prompt]").Click());

        await Eventually(() => _runner.LastRequest is not null);
        Assert.False(_runner.LastRequest!.GraphOnly);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=graph-only-chip]")), Patience);
    }

    [Fact]
    public void The_conversation_list_labels_a_graph_only_conversation()
    {
        using var ctx = Context();
        StartSession("the editing one");
        _registry.Start(new SessionStartRequest(Workspace, CopyPath, "the notes one", GraphOnly: true));

        var page = ctx.Render<SessionsPage>();

        page.WaitForAssertion(
            () =>
            {
                var chips = page.FindAll("[data-testid=session-card-graph-only]");
                Assert.Single(chips);
                Assert.Equal(AgentGraphOnlyTools.Label, chips[0].TextContent.Trim());
            },
            Patience);
    }

    [Fact]
    public void Stop_is_offered_only_while_a_turn_is_running()
    {
        using var ctx = Context();

        var idle = Pane(ctx);
        Assert.Empty(idle.FindAll("[data-testid=stop-session]"));

        StartSession();
        var running = Pane(ctx);
        Assert.Single(running.FindAll("[data-testid=stop-session]"));
    }

    [Fact]
    public void Stop_goes_away_when_the_turn_ends_even_though_the_conversation_is_still_alive()
    {
        // The case the test above is named after and never reached: it compared "no session" with "a
        // session", and one CLI process serves a WHOLE conversation, so a session is alive between turns
        // too. Seen in the studio window on the installed 0.9.24: the turn had finished, the cost was in, the
        // composer was waiting, and Stop still sat beside Send with the tooltip "Interrupt the turn the
        // agent is running now", where pressing it would have ended the process instead.
        using var ctx = Context();
        var session = StartSession();
        var cut = Pane(ctx);

        _runner.Emit(new TurnActivity(DateTimeOffset.UtcNow, AgentTurnPhase.Submitted));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=stop-session]")));

        _runner.Emit(new MessageEmitted(DateTimeOffset.UtcNow, TranscriptRole.Assistant, "Done."));
        _runner.Emit(new TurnCompleted(DateTimeOffset.UtcNow, "Done.", false, null));

        cut.WaitForAssertion(() =>
        {
            Assert.True(session.IsRunning, "the conversation's process is still alive between turns");
            Assert.Empty(cut.FindAll("[data-testid=stop-session]"));
        });
    }

    [Fact]
    public async Task An_idle_graph_only_conversation_can_be_ended_from_its_pane_and_frees_its_slot()
    {
        // Work item #362: Stop shows only mid-turn, so an idle graph-only conversation held the folder's one
        // graph-only slot with no way to end it from its pane, and the next graph-only start was refused.
        _runner.CompleteOnStop = true;
        using var ctx = Context();
        var notes = _registry.Start(new SessionStartRequest(Workspace, CopyPath, "the notes one", GraphOnly: true));
        _runner.Emit(new MessageEmitted(DateTimeOffset.UtcNow, TranscriptRole.Assistant, "Noted."));
        _runner.Emit(new TurnCompleted(DateTimeOffset.UtcNow, "Noted.", false, null));
        await Eventually(() => notes.IsRunning && !notes.TurnInFlight, "the conversation is alive and idle");

        Assert.Throws<InvalidOperationException>(
            () => _registry.Start(new SessionStartRequest(Workspace, CopyPath, "another", GraphOnly: true)));

        var cut = ctx.Render<SessionPane>(p => p
            .Add(c => c.WorkspaceId, Workspace)
            .Add(c => c.WorkingDirectory, CopyPath)
            .Add(c => c.SessionId, notes.Id));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=stop-session]")), Patience);

        await cut.InvokeAsync(() => cut.Find("[data-testid=end-conversation]").Click());
        Assert.False(_runner.Stopped, "End asks first");
        await cut.InvokeAsync(() => cut.Find("[data-testid=end-conversation-confirm-yes]").Click());

        await Eventually(() => !notes.IsRunning, "ending stops the conversation");
        Assert.True(_runner.Stopped);
        Assert.Null(_registry.RunningGraphOnlyFor(Workspace));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=end-conversation]")), Patience);

        var next = _registry.Start(new SessionStartRequest(Workspace, CopyPath, "the next notes", GraphOnly: true));
        Assert.True(next.IsGraphOnly);
        Assert.Same(next, _registry.RunningGraphOnlyFor(Workspace));
    }

    [Fact]
    public async Task End_is_offered_on_an_editing_conversation_too_and_can_be_kept()
    {
        using var ctx = Context();
        var session = StartSession();
        var cut = Pane(ctx);

        await cut.InvokeAsync(() => cut.Find("[data-testid=end-conversation]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=end-conversation-confirm-no]").Click());

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=end-conversation-confirm]")), Patience);
        Assert.False(_runner.Stopped);
        Assert.True(session.IsRunning);
    }

    [Fact]
    public async Task Mid_turn_the_conversation_states_its_model_rather_than_offering_to_change_it()
    {
        // Starting submits the opening turn, so the session is mid-turn here: ending the child now
        // would throw the answer away, so what the turn runs on is stated instead of a control.
        using var ctx = Context();
        StartSession();
        var cut = Pane(ctx);

        _runner.Emit(new SessionRegistered(DateTimeOffset.UtcNow, "cli-1", "claude-opus-4-5"));

        cut.WaitForAssertion(
            () => Assert.Contains(
                "claude-opus-4-5",
                cut.Find("[data-testid=model-locked]").TextContent,
                StringComparison.Ordinal),
            Patience);
        Assert.Empty(cut.FindAll("[data-testid=model-picker]"));
        Assert.Contains("claude-opus-4-5", cut.Find("[data-testid=model-locked]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Between_turns_picking_a_model_moves_the_running_conversation_onto_it()
    {
        // The dropdown wakes up once the turn finishes, and a pick goes through the registry to the
        // runner, which restarts the CLI child with --resume and the new --model (same conversation).
        using var ctx = Context();
        StartSession();
        var cut = Pane(ctx);

        _runner.Emit(new SessionRegistered(DateTimeOffset.UtcNow, "cli-1", "claude-opus-4-5"));
        _runner.Emit(new TurnCompleted(DateTimeOffset.UtcNow, "done", false, "completed"));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=model-picker]")), Patience);
        await cut.InvokeAsync(() => cut.Find("[data-testid=model-picker-trigger]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=model-picker-opt-claude-haiku-4-5]").Click());

        cut.WaitForAssertion(
            () =>
            {
                Assert.Single(_runner.ModelChanges);
                Assert.Equal("claude-haiku-4-5", _runner.ModelChanges[0]);
            },
            Patience);
    }

    // ---- the empty state (backlog 22) --------------------------------------------------------

    [Fact]
    public void A_workspace_with_no_conversation_says_what_a_session_is_and_what_it_will_not_touch()
    {
        using var ctx = Context();

        var cut = Pane(ctx);

        var empty = cut.Find("[data-testid=session-empty]").TextContent;
        Assert.Contains("copy of this repository", empty, StringComparison.Ordinal);
        Assert.Contains("Edits only what you approve", empty, StringComparison.Ordinal);
        Assert.Contains("Git history stays untouched", empty, StringComparison.Ordinal);
        Assert.Single(cut.FindAll("[data-testid=session-empty-start]"));
    }

    [Fact]
    public void With_edits_trusted_the_empty_state_says_edits_in_the_copy_run_without_asking()
    {
        // Work item #369: "every change it makes asks you first" was shown beside an Auto-approve of
        // "Edits in the copy", which is not what the broker does at that level.
        using var ctx = Context();
        _trust.Set(Workspace, WorkspaceTrust.Edits);

        var cut = Pane(ctx);

        var empty = cut.Find("[data-testid=session-empty]").TextContent;
        Assert.DoesNotContain("every change it makes asks you first", empty, StringComparison.Ordinal);
        Assert.DoesNotContain("Edits only what you approve", empty, StringComparison.Ordinal);
        Assert.Contains("Edits inside the copy run without asking", empty, StringComparison.Ordinal);
        Assert.Contains("commands and anything outside the copy still ask", empty, StringComparison.Ordinal);
    }

    [Fact]
    public void The_empty_state_follows_the_trust_picker_while_it_is_on_screen()
    {
        using var ctx = Context();
        var cut = Pane(ctx);
        Assert.Contains("Edits only what you approve", cut.Find("[data-testid=session-empty-edits]").TextContent, StringComparison.Ordinal);

        cut.InvokeAsync(() => _trust.Set(Workspace, WorkspaceTrust.Edits));

        cut.WaitForAssertion(
            () => Assert.Equal("edits", cut.Find("[data-testid=session-empty]").GetAttribute("data-trust")),
            Patience);
        Assert.Contains("without asking", cut.Find("[data-testid=session-empty-edits]").TextContent, StringComparison.Ordinal);

        cut.InvokeAsync(() => _trust.Set(Workspace, WorkspaceTrust.Ask));

        cut.WaitForAssertion(
            () => Assert.Contains("every change it makes asks you first", cut.Find("[data-testid=session-empty-lede]").TextContent, StringComparison.Ordinal),
            Patience);
    }

    [Theory]
    [InlineData(WorkspaceTrust.Ask, "every change it makes asks you first", "Edits only what you approve")]
    [InlineData(WorkspaceTrust.Edits, "commands and anything outside the copy still ask", "Edits inside the copy without asking")]
    public void The_empty_states_copy_names_what_each_trust_level_lets_run(WorkspaceTrust trust, string lede, string edits)
    {
        var copy = SessionEmptyState.Copy(trust);

        Assert.Contains(lede, copy.Lede, StringComparison.Ordinal);
        Assert.Equal(edits, copy.EditsLine);
    }

    [Fact]
    public async Task Open_beside_the_code_hands_the_conversation_to_the_code_page_panel()
    {
        // Work item #366: from the row and from the open conversation's header.
        using var ctx = Context();
        ctx.Services.AddScoped<Fravashi.Web.Services.AgentPanelState>();
        var session = StartSession();
        var panel = ctx.Services.GetRequiredService<Fravashi.Web.Services.AgentPanelState>();
        var cut = ctx.Render<SessionsPage>(p => p.Add(c => c.Id, session.Id.Value.ToString("D")));

        Assert.Single(cut.FindAll("[data-testid=session-open-beside-code]"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=session-card-open-beside-code]").Click());

        Assert.True(panel.IsOpen);
        Assert.Equal(session.Id, panel.SessionId);
        Assert.EndsWith("/code", ctx.Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_the_code_page_panel_the_sessions_area_offers_no_open_beside_the_code()
    {
        using var ctx = Context();
        var session = StartSession();

        var cut = ctx.Render<SessionsPage>(p => p.Add(c => c.Id, session.Id.Value.ToString("D")));

        Assert.Empty(cut.FindAll("[data-testid=session-card-open-beside-code]"));
        Assert.Empty(cut.FindAll("[data-testid=session-open-beside-code]"));
    }

    [Fact]
    public void The_sessions_area_greets_an_empty_workspace_with_the_same_state_the_panel_does()
    {
        // The two surfaces that can be empty are the Sessions area and the Code page's panel, and the
        // panel's own is held by CodePageTests. Proving it on the SessionPane alone would not prove it
        // here: this page has four branches before the pane is reached (no workspace, a stored
        // conversation, a resume note, the pane), and an empty workspace has to land on the last one.
        using var ctx = Context();

        var cut = ctx.Render<SessionsPage>();

        var empty = cut.Find("[data-testid=session-empty]").TextContent;
        Assert.Contains("copy of this repository", empty, StringComparison.Ordinal);
        Assert.Contains("Reads and searches freely", empty, StringComparison.Ordinal);
        Assert.Contains("Edits only what you approve", empty, StringComparison.Ordinal);
        Assert.Contains("Git history stays untouched", empty, StringComparison.Ordinal);
        Assert.Single(cut.FindAll("[data-testid=session-empty-start]"));
    }

    [Fact]
    public async Task The_empty_states_action_puts_the_cursor_where_the_conversation_starts()
    {
        // A conversation begins by saying something, so the primary action opens nothing and starts
        // nothing: it hands over the composer. Without this the button is decoration that renders,
        // passes the markup test above, and does nothing when it is pressed.
        using var ctx = Context();
        var cut = Pane(ctx);

        await cut.InvokeAsync(() => cut.Find("[data-testid=session-empty-start]").Click());

        ctx.JSInterop.VerifyFocusAsyncInvoke();
        Assert.Null(_runner.LastRequest);
    }

    // ---- the governance strip (backlog 18; 44 to 46 are not built) ---------------------------

    [Fact]
    public void The_governance_strip_names_the_workspace_and_is_honest_that_nothing_else_is_bound()
    {
        using var ctx = Context();

        var cut = Pane(ctx);

        Assert.Contains("widgets", cut.Find("[data-testid=gov-workspace]").TextContent, StringComparison.Ordinal);
        Assert.Single(cut.FindAll("[data-testid=gov-copy]"));

        // No invented work item: the strip says the binding does not exist, because it does not.
        var unbound = cut.Find("[data-testid=gov-unbound]");
        Assert.Contains("No work item", unbound.TextContent, StringComparison.Ordinal);
        Assert.Contains("session-gov-chip-pending", unbound.ClassList);
        Assert.Equal("session-governance", unbound.ParentElement?.GetAttribute("data-testid"));
    }

    [Fact]
    public void The_work_strip_shows_for_the_conversation_on_screen_even_when_none_was_picked()
    {
        // Seen in the studio window on the installed 0.9.24. The rail opens /sessions with no id, the pane
        // falls back to the workspace's live conversation and shows it, and the strip that says what that
        // conversation is bound to (and is the only place a person can change it) stayed hidden until the
        // same conversation was clicked in the sidebar, because the page looked only at the route's id.
        using var ctx = Context();
        StartSession();

        var cut = ctx.Render<SessionsPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll("[data-testid=session-pane]"));
            Assert.Single(cut.FindAll("[data-testid=session-work]"));
        });
    }

    [Fact]
    public async Task The_governance_chip_follows_what_the_agent_binds_itself_to()
    {
        // Seen in the studio window on the installed 0.9.24. A real agent was refused an Edit by the gate,
        // created a work item, linked it, wrote a plan and linked that, and was then allowed to edit, all in
        // one turn. Through the whole of it, and after a page reload, this chip said "No work item or plan"
        // and its tooltip said binding "is not built yet ... there is no gate to satisfy", directly above a
        // transcript in which the gate had just refused a change. It was hard-coded from before 44 and 45.
        using var ctx = Context();
        var session = StartSession();
        var cut = Pane(ctx);

        var unbound = cut.Find("[data-testid=gov-unbound]");
        Assert.Contains("No work item", unbound.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("not built", unbound.GetAttribute("title") ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no gate", unbound.GetAttribute("title") ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var created = await _trackers.For(ProjectId.Default).CreateAsync(
            new NewWorkItem { Level = WorkItemLevel.Task, Title = "Add a farewell", Description = "Why it exists.", Status = WorkItemStatus.Ready },
            MutationContext.User);
        _work.Link(session.Id, created.Value.Id, ProjectId.Default, "agent");

        cut.WaitForAssertion(() =>
        {
            var half = cut.Find("[data-testid=gov-bound]");
            Assert.Contains("#" + created.Value.Number, half.TextContent, StringComparison.Ordinal);
            Assert.Contains("no plan", half.TextContent, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("session-gov-chip-pending", half.ClassList);
            Assert.Empty(cut.FindAll("[data-testid=gov-unbound]"));
        });

        var plan = await _wikiLibrary.CreateAsync(new Fravashi.Application.Wiki.NewWikiItem
        {
            Type = Fravashi.Domain.Wiki.WikiItemType.Plan,
            Title = "How the farewell lands",
        });
        _sessionPlans.Link(session.Id, plan.Value.Id, "agent");

        cut.WaitForAssertion(() =>
        {
            var bound = cut.Find("[data-testid=gov-bound]");
            Assert.Contains("#" + created.Value.Number, bound.TextContent, StringComparison.Ordinal);
            Assert.Contains("@" + plan.Value.Number, bound.TextContent, StringComparison.Ordinal);
            Assert.Contains("Add a farewell", bound.GetAttribute("title") ?? string.Empty, StringComparison.Ordinal);
        });
    }

    // ---- conversations a person can read (work item #351) --------------------------------------

    [Fact]
    public async Task The_pane_title_is_a_short_summary_and_then_the_linked_work_item()
    {
        using var ctx = Context();
        var session = StartSession("Hi. please fix the tray icon. It vanished after the update.");
        var cut = Pane(ctx);

        Assert.Equal("fix the tray icon", cut.Find(".session-pane-title").TextContent.Trim());

        var created = await _trackers.For(ProjectId.Default).CreateAsync(
            new NewWorkItem { Level = WorkItemLevel.Task, Title = "Tray icon vanishes", Description = "Why.", Status = WorkItemStatus.Ready },
            MutationContext.User);
        _work.Link(session.Id, created.Value.Id, ProjectId.Default, "agent");

        cut.WaitForAssertion(() =>
            Assert.Equal("#" + created.Value.Number + " Tray icon vanishes", cut.Find(".session-pane-title").TextContent.Trim()));
    }

    [Fact]
    public async Task A_clicked_reference_opens_its_preview_beside_the_conversation()
    {
        using var ctx = Context();
        StartSession();
        var cut = Pane(ctx);
        var created = await _trackers.For(ProjectId.Default).CreateAsync(
            new NewWorkItem
            {
                Level = WorkItemLevel.Task,
                Title = "Sidebar sections squeeze",
                Description = "Three panes open turn the sidebar into slivers.",
                Status = WorkItemStatus.Ready,
            },
            MutationContext.User);
        var number = created.Value.Number;
        Assert.Empty(cut.FindAll("[data-testid=ref-preview]"));

        // What sessionScroll.js calls when a link in the conversation is clicked.
        await cut.InvokeAsync(() => cut.FindComponent<SessionTranscript>().Instance.OpenReference("#" + number));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("#" + number, cut.Find("[data-testid=ref-preview-ref]").TextContent.Trim());
            Assert.Equal("Sidebar sections squeeze", cut.Find("[data-testid=ref-preview-title]").TextContent.Trim());
            Assert.Contains("Ready", cut.Find("[data-testid=ref-preview-facts]").TextContent, StringComparison.Ordinal);
            Assert.Contains("slivers", cut.Find("[data-testid=ref-preview-excerpt]").TextContent, StringComparison.Ordinal);
        });

        await cut.InvokeAsync(() => cut.Find("[data-testid=ref-preview-open]").Click());
        Assert.EndsWith("/workitems/" + number, ctx.Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }

    /// <summary>Work item #356: a work item's preview shows its next action and acceptance criteria, like the full page.</summary>
    [Fact]
    public async Task A_work_item_preview_shows_its_next_action_and_acceptance_criteria()
    {
        using var ctx = Context();
        StartSession();
        var cut = Pane(ctx);
        var created = await _trackers.For(ProjectId.Default).CreateAsync(
            new NewWorkItem
            {
                Level = WorkItemLevel.Task,
                Title = "Usage page",
                Description = "Add a usage page.",
                Status = WorkItemStatus.Ready,
                NextAction = "Write the page outline",
                AcceptanceCriteria = [new AcceptanceCriterion("c1", "The page lists every command")],
            },
            MutationContext.User);

        await cut.InvokeAsync(() => cut.FindComponent<SessionTranscript>().Instance.OpenReference("#" + created.Value.Number));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Write the page outline", cut.Find("[data-testid=ref-preview-next-action]").TextContent, StringComparison.Ordinal);
            Assert.Contains("The page lists every command", cut.Find("[data-testid=ref-preview-criteria]").TextContent, StringComparison.Ordinal);
        });
    }

    /// <summary>Work item #359: a work item's preview shows the images it carries, as its page does.</summary>
    [Fact]
    public async Task A_work_item_preview_shows_the_images_the_item_carries()
    {
        using var ctx = Context();
        var attachments = new InMemoryWorkItemAttachments();
        ctx.Services.AddSingleton<Fravashi.Application.WorkItems.IWorkItemAttachments>(attachments);
        StartSession();
        var cut = Pane(ctx);
        var created = await _trackers.For(ProjectId.Default).CreateAsync(
            new NewWorkItem { Level = WorkItemLevel.Task, Title = "Save does nothing", Description = "Reported on Home." },
            MutationContext.User);
        attachments.AddImage(created.Value.Id, "save-button.png", AssistantComposerAndAttachmentsTests.Png, "user");

        await cut.InvokeAsync(() => cut.FindComponent<SessionTranscript>().Instance.OpenReference("#" + created.Value.Number));

        cut.WaitForAssertion(() =>
            Assert.Single(cut.FindAll("[data-testid=ref-preview] [data-testid=work-item-attachment]")));
    }

    /// <summary>Work item #356: the folder's trust level is in the session header, always visible.</summary>
    [Fact]
    public void The_session_header_shows_the_folders_trust_level()
    {
        using var ctx = Context();
        StartSession();
        var cut = Pane(ctx);

        var head = cut.Find("[data-testid=session-pane-head]");
        Assert.NotNull(head.QuerySelector("[data-testid=session-trust]"));
        Assert.Contains("Auto-approve", head.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reference_to_nothing_says_so_and_the_preview_closes()
    {
        using var ctx = Context();
        StartSession();
        var cut = Pane(ctx);

        await cut.InvokeAsync(() => cut.FindComponent<SessionTranscript>().Instance.OpenReference("#9999"));

        cut.WaitForAssertion(() => Assert.Contains(
            "no work item #9999", cut.Find("[data-testid=ref-preview-missing]").TextContent, StringComparison.Ordinal));

        await cut.InvokeAsync(() => cut.Find("[data-testid=ref-preview-close]").Click());
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=ref-preview]")));
    }

    [Fact]
    public async Task Text_that_is_not_exactly_one_reference_opens_nothing()
    {
        using var ctx = Context();
        StartSession();
        var cut = Pane(ctx);

        await cut.InvokeAsync(() => cut.FindComponent<SessionTranscript>().Instance.OpenReference("javascript:#1"));

        Assert.Empty(cut.FindAll("[data-testid=ref-preview]"));
    }

    [Fact]
    public async Task The_reply_is_shown_while_it_is_still_being_written()
    {
        using var ctx = Context();
        StartSession();
        var cut = Pane(ctx);

        _runner.Emit(new PartialText(DateTimeOffset.UtcNow, "Looking at "));
        _runner.Emit(new PartialText(DateTimeOffset.UtcNow, "the project"));

        await Eventually(() => cut.Markup.Contains("Looking at the project"));
    }

    // ---- approvals ------------------------------------------------------------------------

    [Fact]
    public async Task A_change_waits_for_the_user_and_approving_it_releases_the_agent()
    {
        using var ctx = Context();
        var session = StartSession();
        _broker.RegisterSession(session.Id);
        var cut = Pane(ctx);

        var blocked = _broker.RequestAsync(
            session.Id, "Write", "toolu_1", new Dictionary<string, string>
            {
                ["file_path"] = "/workspaces/w1/README.md",
                ["content"] = "# Widgets",
            });

        await Eventually(() => cut.FindAll("[data-testid=approval-prompt]").Count == 1);
        Assert.Contains("/workspaces/w1/README.md", cut.Markup);
        Assert.Contains("# Widgets", cut.Markup);
        Assert.False(blocked.IsCompleted, "the agent must still be waiting");

        await cut.InvokeAsync(() => cut.Find("[data-testid=approve]").Click());

        var decision = await blocked;
        Assert.True(decision.Approved);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=approval-prompt]")), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Denying_a_change_tells_the_agent_no_and_says_so_in_the_transcript()
    {
        using var ctx = Context();
        var session = StartSession();
        _broker.RegisterSession(session.Id);
        var cut = Pane(ctx);
        var blocked = _broker.RequestAsync(
            session.Id, "Bash", "toolu_1", new Dictionary<string, string> { ["command"] = "rm -rf /" });
        await Eventually(() => cut.FindAll("[data-testid=approval-prompt]").Count == 1);

        await cut.InvokeAsync(() => cut.Find("[data-testid=deny]").Click());

        var decision = await blocked;
        Assert.False(decision.Approved);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=denied-note]")), TimeSpan.FromSeconds(10));

        // Backlog 114: this sentence is only true when the user really is the one who said no, which
        // here they are. The studio's own refusals are the tests below.
        Assert.Contains("You denied Bash", cut.Markup);
        Assert.Empty(cut.FindAll("[data-testid=refused-note]"));

        // Backlog 220: nothing typed, so the agent is told exactly what it was always told.
        Assert.Equal("the user declined this change in the studio", decision.Reason);
    }

    [Fact]
    public async Task Denying_with_a_reason_tells_the_agent_why_and_shows_it_encoded_in_the_transcript()
    {
        // Backlog 220: "no, because". The words reach the decision the hook is answered with, and the transcript
        // shows them on the declined call as TEXT: markup a person typed is never markup in the page.
        using var ctx = Context();
        var session = StartSession();
        _broker.RegisterSession(session.Id);
        var cut = Pane(ctx);
        var blocked = _broker.RequestAsync(
            session.Id, "Bash", "toolu_1", new Dictionary<string, string> { ["command"] = "rm -rf /" });
        await Eventually(() => cut.FindAll("[data-testid=approval-prompt]").Count == 1);

        await cut.InvokeAsync(() => cut.Find("[data-testid=deny-reason]").Input("delete only <b>bin</b>"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=deny]").Click());

        var decision = await blocked;
        Assert.False(decision.Approved);
        Assert.Equal(ToolApprovalDecidedBy.User, decision.DecidedBy);
        Assert.Equal(
            "the user declined this change in the studio and gave this reason: delete only <b>bin</b>",
            decision.Reason);

        cut.WaitForAssertion(
            () =>
            {
                var note = Assert.Single(cut.FindAll("[data-testid=denied-note]"));
                Assert.Contains("You denied Bash", note.TextContent);
                Assert.Contains("gave this reason: delete only <b>bin</b>", note.TextContent);
                Assert.Empty(note.QuerySelectorAll("b"));
                Assert.Contains("delete only &lt;b&gt;bin&lt;/b&gt;", note.InnerHtml);
            },
            Patience);
    }

    // ---- who refused it (backlog 114) -----------------------------------------------------
    //
    // A refusal the STUDIO imposed is not the user's decision, and rendering it as one told an owner
    // they had denied a call they were never asked about. They reasonably read that as the approval
    // gate having been bypassed. Each reason below is one the broker really writes.

    [Theory]
    [InlineData("The studio stopped waiting for an answer.", "The studio stopped waiting for an answer")]
    [InlineData("The studio saw no answer within 4 hour(s), so the call was denied.", "no answer within 4 hour(s)")]
    [InlineData("The session ended before this call was answered.", "The session ended before this call was answered")]
    [InlineData("The studio shut down.", "The studio shut down")]
    [InlineData("The studio has no running session to ask about this call.", "no running session")]
    public async Task A_refusal_the_studio_imposed_names_its_cause_and_never_blames_the_user(
        string reason, string expected)
    {
        using var ctx = Context();
        var session = StartSession();
        var cut = Pane(ctx);

        _runner.Emit(new ToolApprovalResolved(
            DateTimeOffset.UtcNow, "req-1", "Bash", false, reason, ToolApprovalDecidedBy.Studio));

        // The emitted event has to reach the session before the markup is worth reading: the pump is a
        // background loop, and asserting on the DOM first is a race the render can lose (the same trap
        // the ask-question test below documents).
        await Eventually(
            () => session.Transcript.OfType<ToolApprovalResolved>().Any(),
            "the refusal should reach the session");
        cut.WaitForAssertion(
            () =>
            {
                Assert.Single(cut.FindAll("[data-testid=refused-note]"));
                Assert.Contains("Bash was not run", cut.Markup);
                Assert.Contains(expected, cut.Markup);
                Assert.DoesNotContain("You denied", cut.Markup);
                Assert.Empty(cut.FindAll("[data-testid=denied-note]"));
            },
            Patience);
    }

    // ---- no refusal is invisible (backlog 117) -----------------------------------------------

    [Fact]
    public async Task A_call_refused_again_and_again_shows_every_refusal_and_how_often()
    {
        // Through the real broker and registry: the broker holds no registration for this session, so each
        // call is refused without anybody being asked, which is the path that used to leave no trace.
        using var ctx = Context();
        var session = StartSession();
        var cut = Pane(ctx);
        var bash = new Dictionary<string, string> { ["command"] = "curl evil.example" };

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.False((await _broker.RequestAsync(session.Id, "Bash", "toolu_1", bash)).Approved);
        }

        cut.WaitForAssertion(
            () =>
            {
                Assert.Equal(3, cut.FindAll("[data-testid=auto-refused-note]").Count);
                Assert.Contains("Bash was refused automatically", cut.Markup);
                Assert.Contains("no running session", cut.Markup);
                var counts = cut.FindAll("[data-testid=refusal-count]").Select(c => c.TextContent.Trim()).ToList();
                Assert.Equal(["Refused 2 times in this session.", "Refused 3 times in this session."], counts);
                Assert.Empty(cut.FindAll("[data-testid=approval-prompt]"));
                Assert.DoesNotContain("You denied", cut.Markup);
            },
            Patience);
    }

    [Fact]
    public async Task A_repeated_studio_refusal_of_a_parked_call_says_how_often()
    {
        using var ctx = Context();
        var session = StartSession();
        var cut = Pane(ctx);

        _runner.Emit(new ToolApprovalResolved(
            DateTimeOffset.UtcNow, "req-1", "Write", false, "The studio stopped waiting for an answer.",
            ToolApprovalDecidedBy.Studio, RepeatCount: 4));

        await Eventually(() => session.Transcript.OfType<ToolApprovalResolved>().Any(), "the refusal should reach the session");
        cut.WaitForAssertion(
            () =>
            {
                Assert.Single(cut.FindAll("[data-testid=refused-note]"));
                Assert.Equal("Refused 4 times in this session.", cut.Find("[data-testid=refusal-count]").TextContent.Trim());
            },
            Patience);
    }

    [Fact]
    public async Task A_first_refusal_shows_no_count()
    {
        using var ctx = Context();
        var session = StartSession();
        var cut = Pane(ctx);

        _runner.Emit(new ToolCallRefused(DateTimeOffset.UtcNow, string.Empty, string.Empty, "The studio could not read this tool call."));

        await Eventually(() => session.Transcript.OfType<ToolCallRefused>().Any(), "the refusal should reach the session");
        cut.WaitForAssertion(
            () =>
            {
                Assert.Contains("A tool call was refused automatically", cut.Markup);
                Assert.Empty(cut.FindAll("[data-testid=refusal-count]"));
            },
            Patience);
    }

    /// <summary>
    /// Work item #356: no raw mcp__fravashi__ name in a transcript note a person reads (refused, denied, trusted, not
    /// run). Only the raw view keeps it.
    /// </summary>
    [Fact]
    public async Task Transcript_notes_name_a_studio_tool_in_words_never_by_its_raw_name()
    {
        using var ctx = Context();
        var session = StartSession();
        var cut = Pane(ctx);
        const string tool = "mcp__fravashi__work_item_update";

        _runner.Emit(new ToolCallRefused(DateTimeOffset.UtcNow, tool, "#23", "The gate refused " + tool + "."));
        _runner.Emit(new ToolApprovalResolved(DateTimeOffset.UtcNow, "req-1", tool, false, "not now", ToolApprovalDecidedBy.User));
        _runner.Emit(new ToolApprovalResolved(DateTimeOffset.UtcNow, "req-2", tool, false, "The studio shut down.", ToolApprovalDecidedBy.Studio));

        await Eventually(() => session.Transcript.OfType<ToolApprovalResolved>().Count() == 2, "the notes should reach the session");
        cut.WaitForAssertion(
            () =>
            {
                Assert.Contains("Work item update was refused automatically", cut.Markup);
                Assert.Contains("You denied work item update", cut.Markup);
                Assert.Contains("Work item update was not run", cut.Markup);
                foreach (var note in cut.FindAll(".session-note"))
                {
                    Assert.DoesNotContain("mcp__fravashi__", note.TextContent);
                }
            },
            Patience);
    }

    [Fact]
    public async Task A_resolution_stored_before_the_attribution_existed_is_rendered_neutrally()
    {
        // The exact row an older build wrote, read back through the real transcript reader: no
        // attribution at all. It must fall back to the neutral wording, because telling a user they
        // denied something they never saw is the worse of the two possible mistakes.
        using var ctx = Context();
        var probe = StartSession();
        var cut = Pane(ctx);
        var legacy = AgentEventJson.Deserialize(
            "approval_resolved",
            """
            {"timestamp":"2026-08-13T09:00:00.0000000+00:00","requestId":"req-1","toolName":"Bash",
             "approved":false,"reason":"The studio stopped waiting for an answer."}
            """);
        Assert.NotNull(legacy);

        _runner.Emit(legacy!);

        await Eventually(
            () => probe.Transcript.OfType<ToolApprovalResolved>().Any(),
            "the stored refusal should reach the session");
        cut.WaitForAssertion(
            () =>
            {
                Assert.Single(cut.FindAll("[data-testid=refused-note]"));
                Assert.Contains("Bash was not run", cut.Markup);
                Assert.DoesNotContain("You denied", cut.Markup);
            },
            Patience);
    }

    [Fact]
    public async Task A_trusted_edit_shows_no_prompt_and_leaves_a_transcript_row()
    {
        // ADR 0028 through the real broker, registry and pane.
        // A real folder: the trust branch keys on the destination relative to the root, which a root that does
        // not exist cannot give on every platform.
        var copy = Path.Combine(Path.GetTempPath(), "fravashi-trust-ui-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(copy, "src"));
        using var ctx = Context();
        var session = StartSession();
        _broker.RegisterSession(session.Id, copy, workspaceId: "w1");
        _trust.Set("w1", WorkspaceTrust.Edits);
        var cut = Pane(ctx);

        var decision = await _broker.RequestAsync(
            session.Id, "Edit", "toolu_1",
            new Dictionary<string, string> { ["file_path"] = Path.Combine(copy, "src", "a.cs"), ["old_string"] = "x", ["new_string"] = "y" });

        Assert.True(decision.Approved);
        cut.WaitForAssertion(
            () =>
            {
                Assert.Empty(cut.FindAll("[data-testid=approval-prompt]"));
                Assert.Single(cut.FindAll("[data-testid=trusted-note]"));
                Assert.Contains("a.cs", cut.Markup);
            },
            Patience);
        Assert.Contains(session.Transcript, e => e is ToolCallTrusted);
    }

    /// <summary>ADR 0028's port in memory, shared by the broker and the strip in these tests.</summary>
    private sealed class InMemoryTrust : IWorkspaceTrust
    {
        private readonly Dictionary<string, WorkspaceTrust> _levels = new(StringComparer.Ordinal);

        public event Action<string>? Changed;

        public WorkspaceTrust For(string workspaceId) =>
            _levels.TryGetValue(workspaceId, out var level) ? level : WorkspaceTrust.Ask;

        public void Set(string workspaceId, WorkspaceTrust trust)
        {
            _levels[workspaceId] = trust;
            Changed?.Invoke(workspaceId);
        }
    }

    [Fact]
    public async Task A_read_inside_the_copy_never_interrupts_the_user()
    {
        using var ctx = Context();
        var session = StartSession();
        _broker.RegisterSession(session.Id, CopyPath);
        var cut = Pane(ctx);

        var decision = await _broker.RequestAsync(
            session.Id, "Read", "toolu_1", new Dictionary<string, string> { ["file_path"] = "/workspaces/w1/a.cs" });

        Assert.True(decision.Approved);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=approval-prompt]")), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_read_that_leaves_the_copy_is_put_in_front_of_the_user_with_the_reason()
    {
        // An approved-without-reading Read of /proc/1/environ would hand over the host-bridge token,
        // so it gets the same prompt a change gets, and the prompt says why it is there.
        using var ctx = Context();
        var session = StartSession();
        _broker.RegisterSession(session.Id, CopyPath);
        var cut = Pane(ctx);

        var blocked = _broker.RequestAsync(
            session.Id, "Read", "toolu_1", new Dictionary<string, string> { ["file_path"] = "/proc/1/environ" });

        await Eventually(() => cut.FindAll("[data-testid=approval-prompt]").Count == 1);
        Assert.Contains("/proc/1/environ", cut.Markup);
        Assert.Single(cut.FindAll("[data-testid=approval-warning]"));
        Assert.False(blocked.IsCompleted, "the agent must still be waiting");

        await cut.InvokeAsync(() => cut.Find("[data-testid=deny]").Click());

        Assert.False((await blocked).Approved);
    }

    // ---- the prompt says where the change really lands --------------------------------------
    //
    // The subject line on the prompt is capped at 200 characters, and a capped path is a different
    // path. A file_path long enough reads there as an ordinary folder deep inside the copy while the
    // CLI writes to the studio's own data directory. These drive the REAL broker and the REAL
    // component, because the layering between them is what hid it: the broker knew the destination and
    // the prompt rendered only the capped subject.

    /// <summary>Deep-but-ordinary for far longer than the cap, then back to the root and out.</summary>
    private static string PaddedEscape(string leaf) =>
        CopyPath + "/src"
        + string.Concat(Enumerable.Repeat("/aaaa", 60))
        + string.Concat(Enumerable.Repeat("/..", 63))
        + leaf;

    [Fact]
    public async Task A_change_disguised_as_a_deep_in_copy_path_shows_the_user_where_it_really_writes()
    {
        // The worst target: this session's own approve.sh answers the NEXT approval, so one disguised
        // yes would become every yes.
        using var ctx = Context();
        var session = StartSession();
        _broker.RegisterSession(session.Id, CopyPath);
        var cut = Pane(ctx);
        var padded = PaddedEscape("/data/agent-sessions/s1/approve.sh");

        var blocked = _broker.RequestAsync(session.Id, "Write", "toolu_1", new Dictionary<string, string>
        {
            ["file_path"] = padded,
            ["content"] = "#!/bin/sh\necho allow",
        });

        await Eventually(() => cut.FindAll("[data-testid=approval-prompt]").Count == 1);

        // The reproduction, phrased the way the user experiences it: before this the whole prompt was
        // the capped subject, the (absent) warning and the content, and none of them said approve.sh.
        Assert.Contains("approve.sh", cut.Find("[data-testid=approval-prompt]").TextContent);

        Assert.Contains("approve.sh", cut.Find("[data-testid=approval-target-resolved]").TextContent);
        Assert.Single(cut.FindAll("[data-testid=approval-outside]"));
        Assert.Contains("OUTSIDE", cut.Find("[data-testid=approval-outside]").TextContent);
        Assert.False(blocked.IsCompleted, "the agent must still be waiting");

        await cut.InvokeAsync(() => cut.Find("[data-testid=deny]").Click());

        Assert.False((await blocked).Approved);
    }

    [Fact]
    public async Task An_ordinary_change_inside_the_copy_shows_its_target_without_the_alarm()
    {
        // A prompt that shouts at every change says nothing about the one that matters.
        using var ctx = Context();
        var session = StartSession();
        _broker.RegisterSession(session.Id, CopyPath);
        var cut = Pane(ctx);

        var blocked = _broker.RequestAsync(session.Id, "Write", "toolu_1", new Dictionary<string, string>
        {
            ["file_path"] = CopyPath + "/README.md",
            ["content"] = "# Widgets",
        });

        await Eventually(() => cut.FindAll("[data-testid=approval-prompt]").Count == 1);
        Assert.Contains("README.md", cut.Find("[data-testid=approval-target-resolved]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=approval-outside]"));
        Assert.Empty(cut.FindAll("[data-testid=approval-warning]"));

        await cut.InvokeAsync(() => cut.Find("[data-testid=approve]").Click());

        Assert.True((await blocked).Approved);
    }

    // ---- the prompt says what the arguments MEAN, not just what they point at ----------------
    //
    // Same family as the padded path above, one layer further out: there the prompt showed a true
    // sentence about the wrong path, here it shows a true sentence and leaves out the argument that
    // decides what saying yes does. An Agent call renders its description and its prompt; "isolation"
    // and "run_in_background" are rendered nowhere, and one of them sends the workspace copy to a
    // machine that is not this one.

    [Fact]
    public async Task An_agent_task_sent_to_a_remote_cloud_environment_says_so_before_the_buttons()
    {
        using var ctx = Context();
        var session = StartSession();
        _broker.RegisterSession(session.Id, CopyPath);
        var cut = Pane(ctx);

        var blocked = _broker.RequestAsync(session.Id, "Agent", "toolu_1", new Dictionary<string, string>
        {
            ["description"] = "Tidy the helpers",
            ["prompt"] = "Rename the helper functions under src/.",
            ["isolation"] = "remote",
        });

        await Eventually(() => cut.FindAll("[data-testid=approval-prompt]").Count == 1);

        // The reproduction, phrased the way the user experiences it: before this the whole prompt was
        // "Agent", "Tidy the helpers", the prompt text and two buttons. Nothing said the work leaves
        // this machine.
        var prompt = cut.Find("[data-testid=approval-prompt]").TextContent;
        Assert.Contains("remote", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not on this machine", prompt, StringComparison.OrdinalIgnoreCase);

        var effects = cut.FindAll("[data-testid=approval-effect]");
        Assert.NotEmpty(effects);
        Assert.Contains(effects, e => e.TextContent.Contains("isolation", StringComparison.Ordinal));

        // Loud, not a footnote: it is the reason this approval is not the ordinary one.
        Assert.NotEmpty(cut.FindAll("[data-testid=approval-effect].session-approval-effect-alarm"));
        Assert.False(blocked.IsCompleted, "the agent must still be waiting");

        await cut.InvokeAsync(() => cut.Find("[data-testid=deny]").Click());

        Assert.False((await blocked).Approved);
    }

    [Fact]
    public async Task An_agent_task_that_left_run_in_background_out_is_said_to_run_in_the_foreground()
    {
        // Backlog 192: the CLI's schema says agents default to the background, but the studio starts the CLI with
        // background tasks off, and in the #50 trial the subagent's result came back inside the turn while this
        // card said BACKGROUND. The card says what happens in this studio.
        using var ctx = Context();
        var session = StartSession();
        _broker.RegisterSession(session.Id, CopyPath);
        var cut = Pane(ctx);

        var blocked = _broker.RequestAsync(session.Id, "Agent", "toolu_1", new Dictionary<string, string>
        {
            ["description"] = "Tidy the helpers",
            ["prompt"] = "Rename the helper functions under src/.",
        });

        await Eventually(() => cut.FindAll("[data-testid=approval-prompt]").Count == 1);

        var effect = Assert.Single(cut.FindAll("[data-testid=approval-effect]")).TextContent;
        Assert.Contains("runs in the FOREGROUND", effect, StringComparison.Ordinal);
        Assert.Contains(AgentCliSwitches.DisableBackgroundTasksVariable, effect, StringComparison.Ordinal);
        Assert.DoesNotContain("runs in the BACKGROUND", effect, StringComparison.Ordinal);

        // Worth stating, not worth shouting: the alarm has to keep meaning something, so an ordinary Agent call
        // must not raise one.
        Assert.Empty(cut.FindAll("[data-testid=approval-effect].session-approval-effect-alarm"));

        await cut.InvokeAsync(() => cut.Find("[data-testid=approve]").Click());

        Assert.True((await blocked).Approved);
    }

    [Theory]
    [InlineData("Agent", "prompt", "Rename the helper functions under src/.", "this agent runs in the FOREGROUND")]
    [InlineData("Bash", "command", "npm test", "Whether it refuses the call or runs it in the foreground is not settled")]
    public async Task An_explicit_run_in_background_is_described_as_the_cli_treats_it_with_background_tasks_off(
        string tool, string field, string value, string expected)
    {
        // Backlog 192: with the studio's switch on, the pinned CLI leaves run_in_background out of both schemas,
        // and launches an agent in the background only for a remote isolation. The card says that, not the schema.
        using var ctx = Context();
        var session = StartSession();
        _broker.RegisterSession(session.Id, CopyPath);
        var cut = Pane(ctx);

        var blocked = _broker.RequestAsync(session.Id, tool, "toolu_1", new Dictionary<string, string>
        {
            [field] = value,
            ["run_in_background"] = "true",
        });

        await Eventually(() => cut.FindAll("[data-testid=approval-prompt]").Count == 1);

        var effect = Assert.Single(cut.FindAll("[data-testid=approval-effect]")).TextContent;
        Assert.Contains("does not offer this field", effect, StringComparison.Ordinal);
        Assert.Contains(expected, effect, StringComparison.Ordinal);
        Assert.Contains(AgentCliSwitches.DisableBackgroundTasksVariable, effect, StringComparison.Ordinal);

        await cut.InvokeAsync(() => cut.Find("[data-testid=deny]").Click());

        Assert.False((await blocked).Approved);
    }

    [Fact]
    public async Task A_message_to_another_agent_shows_the_message_rather_than_the_clis_preview()
    {
        // Backlog 192, seen in the #50 trial: the card's body showed only the CLI's shortened "content" preview,
        // while "message" held the whole text sent to the subagent.
        using var ctx = Context();
        var session = StartSession();
        _broker.RegisterSession(session.Id, CopyPath);
        var cut = Pane(ctx);

        var message = "The studio's reader refused your tree with these errors:\n"
            + "1. docs/a.md: heading level skipped.\n2. docs/b.md: unknown reference @99.\n"
            + "Fix both and hand back the corrected tree.";
        var blocked = _broker.RequestAsync(session.Id, "SendMessage", "toolu_1", new Dictionary<string, string>
        {
            ["to"] = "docs-writer",
            ["content"] = "The studio's reader refused your tree with these ...",
            ["message"] = message,
        });

        await Eventually(() => cut.FindAll("[data-testid=approval-prompt]").Count == 1);

        var body = cut.Find(".session-approval-body pre").TextContent;
        Assert.Contains(message, body, StringComparison.Ordinal);
        Assert.Contains("docs-writer", body, StringComparison.Ordinal);
        Assert.DoesNotContain("these ...", body, StringComparison.Ordinal);

        // The subject line is the message too, shortened head and tail like every other long value.
        var detail = cut.Find(".session-approval-detail").TextContent;
        Assert.Equal(AgentToolSubject.Shorten(message, AgentToolSubject.MaxLength), detail);

        await cut.InvokeAsync(() => cut.Find("[data-testid=deny]").Click());

        Assert.False((await blocked).Approved);
    }

    [Fact]
    public async Task A_shell_call_with_the_sandbox_turned_off_says_so_rather_than_showing_the_command_alone()
    {
        using var ctx = Context();
        var session = StartSession();
        _broker.RegisterSession(session.Id, CopyPath);
        var cut = Pane(ctx);

        var blocked = _broker.RequestAsync(session.Id, "Bash", "toolu_1", new Dictionary<string, string>
        {
            ["command"] = "npm install",
            ["dangerouslyDisableSandbox"] = "true",
        });

        await Eventually(() => cut.FindAll("[data-testid=approval-prompt]").Count == 1);

        var effects = cut.FindAll("[data-testid=approval-effect]");
        Assert.Contains(effects, e => e.TextContent.Contains("sandbox", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(cut.FindAll("[data-testid=approval-effect].session-approval-effect-alarm"));

        await cut.InvokeAsync(() => cut.Find("[data-testid=deny]").Click());

        Assert.False((await blocked).Approved);
    }

    [Fact]
    public async Task An_ordinary_change_carries_no_effect_lines_at_all()
    {
        // The legitimate case, so this is not a blanket alarm: a plain Write inside the copy keeps the
        // prompt it always had.
        using var ctx = Context();
        var session = StartSession();
        _broker.RegisterSession(session.Id, CopyPath);
        var cut = Pane(ctx);

        var blocked = _broker.RequestAsync(session.Id, "Write", "toolu_1", new Dictionary<string, string>
        {
            ["file_path"] = CopyPath + "/README.md",
            ["content"] = "# Widgets",
        });

        await Eventually(() => cut.FindAll("[data-testid=approval-prompt]").Count == 1);
        Assert.Empty(cut.FindAll("[data-testid=approval-effect]"));

        await cut.InvokeAsync(() => cut.Find("[data-testid=approve]").Click());

        Assert.True((await blocked).Approved);
    }

    [Fact]
    public async Task A_notebook_cell_deletion_shows_the_edit_mode_that_makes_it_a_deletion()
    {
        // NotebookEdit's edit_mode turns the same arguments into an insert, a replace or a DELETE, and
        // a deletion carries no replacement text, so without this line the prompt is a path and nothing.
        using var ctx = Context();
        var session = StartSession();
        _broker.RegisterSession(session.Id, CopyPath);
        var cut = Pane(ctx);

        var blocked = _broker.RequestAsync(session.Id, "NotebookEdit", "toolu_1", new Dictionary<string, string>
        {
            ["notebook_path"] = CopyPath + "/analysis.ipynb",
            ["cell_id"] = "c3",
            ["new_source"] = string.Empty,
            ["edit_mode"] = "delete",
        });

        await Eventually(() => cut.FindAll("[data-testid=approval-prompt]").Count == 1);

        var effects = cut.FindAll("[data-testid=approval-effect]");
        Assert.Contains(effects, e => e.TextContent.Contains("edit_mode", StringComparison.Ordinal));
        Assert.Contains("delete", cut.Find("[data-testid=approval-prompt]").TextContent, StringComparison.OrdinalIgnoreCase);

        await cut.InvokeAsync(() => cut.Find("[data-testid=deny]").Click());

        Assert.False((await blocked).Approved);
    }

    // ---- honesty about where an agent may run ----------------------------------------------

    [Fact]
    public void Without_an_open_workspace_the_pane_says_so_instead_of_offering_a_session()
    {
        using var ctx = Context(workspaceId: null);

        var cut = ctx.Render<SessionPane>();

        Assert.NotEmpty(cut.FindAll("[data-testid=session-no-workspace]"));
        Assert.Empty(cut.FindAll("[data-testid=composer-input]"));
    }

    [Fact]
    public void A_folder_that_is_not_set_up_shows_the_state_instead_of_the_composer_and_starts_nothing()
    {
        // Item #373: no shared bucket. An agent records its work as work items and plans, which live in a project, so a
        // folder in none starts no conversation; the pane says so and offers the setup, in both of its places.
        using var ctx = Context(workspaceId: NotSetUp);

        var cut = ctx.Render<SessionPane>(p => p
            .Add(c => c.WorkspaceId, NotSetUp)
            .Add(c => c.WorkingDirectory, CopyPath));

        Assert.Empty(cut.FindAll("[data-testid=session-empty]"));
        Assert.Empty(cut.FindAll("[data-testid=composer-input]"));
        var states = cut.FindAll("[data-testid=folder-not-set-up]");
        Assert.Equal(2, states.Count);
        Assert.All(states, s => Assert.Equal("sessions", s.GetAttribute("data-surface")));
        Assert.Contains("Stomme is not set up yet.", cut.Find(".folder-setup-inline-text").TextContent);
        Assert.All(cut.FindAll("[data-testid=folder-set-up]"), b => Assert.Equal("/setup", b.GetAttribute("href")));
        Assert.Null(_registry.RunningFor(NotSetUp));
    }

    [Fact]
    public void Without_a_contained_copy_no_agent_is_offered()
    {
        // Containment is absolute (plan 2d decision 2): no copy means nowhere safe to run.
        using var ctx = Context(copyPath: null);

        var cut = ctx.Render<SessionPane>(p => p.Add(c => c.WorkspaceId, Workspace));

        Assert.NotEmpty(cut.FindAll("[data-testid=session-no-copy]"));
        Assert.Empty(cut.FindAll("[data-testid=session-copy-failed]"));
        Assert.Empty(cut.FindAll("[data-testid=composer-input]"));
    }

    [Fact]
    public void A_copy_that_failed_to_start_says_why_and_trying_again_brings_the_ordinary_pane()
    {
        // Backlog 173: the reason replaces the generic sentence, and Try again starts the copy again. Driven
        // through the page, because it is the page that hands the pane the copy once there is one.
        using var ctx = Context(copyPath: null);
        var sync = (FakeSync)ctx.Services.GetRequiredService<IWorkspaceAgentSync>();
        sync.Failure = "Response status code does not indicate success: 500 (Internal Server Error).";
        sync.CopyPathOnRetry = CopyPath;

        var cut = ctx.Render<SessionsPage>();

        var reason = cut.Find("[data-testid=session-copy-failed-reason]").TextContent;
        Assert.Contains("could not start", reason);
        Assert.Contains("500 (Internal Server Error)", reason);
        Assert.Empty(cut.FindAll("[data-testid=session-no-copy]"));
        Assert.Empty(cut.FindAll("[data-testid=composer-input]"));

        cut.InvokeAsync(() => cut.Find("[data-testid=session-copy-retry]").Click());

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=composer-input]")));
        Assert.Equal([Workspace], sync.Retried);
        Assert.Empty(cut.FindAll("[data-testid=session-copy-failed]"));
    }

    // ---- the page ---------------------------------------------------------------------------

    [Fact]
    public void The_page_lists_this_workspaces_sessions_and_offers_a_new_one()
    {
        using var ctx = Context();
        StartSession("write the readme");

        var cut = ctx.Render<SessionsPage>();

        Assert.NotEmpty(cut.FindAll("[data-testid=new-session]"));
        var cards = cut.FindAll("[data-testid=session-card]");
        Assert.Single(cards);
        Assert.Contains("write the readme", cards[0].TextContent);
    }

    // ---- conversations run outside Fravashi (backlog 43) -----------------------------------

    private readonly FakeImports _imports = new();

    private sealed class FakeImports : ICliConversationSource
    {
        public Dictionary<string, (CliConversationFile File, byte[] Bytes)> Files { get; } = [];

        public void Put(string name, string jsonl, DateTimeOffset modified)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(jsonl);
            Files[name] = (new CliConversationFile(name, bytes.Length, modified), bytes);
        }

        public Task<IReadOnlyList<CliConversationFile>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CliConversationFile>>([.. Files.Values.Select(f => f.File)]);

        public Task<CliConversationContent> ReadAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult(Files.TryGetValue(name, out var file)
                ? new CliConversationContent(CliConversationReadStatus.Read, file.Bytes)
                : CliConversationContent.Missing);
    }

    // Synthetic, in the CLI's line shape: one question and one answer.
    private const string ImportedJsonl =
        """
        {"type":"user","isSidechain":false,"message":{"role":"user","content":"how are widgets counted"},"timestamp":"2026-09-01T10:00:00.000Z"}
        {"type":"assistant","isSidechain":false,"message":{"id":"m1","role":"assistant","content":[{"type":"text","text":"One by one."}]},"timestamp":"2026-09-01T10:00:02.000Z"}
        """;

    [Fact]
    public void The_list_shows_imported_conversations_beside_the_studios_own_and_marks_only_them()
    {
        using var ctx = Context();
        StartSession("write the readme");
        _imports.Put("abc-123.jsonl", ImportedJsonl, DateTimeOffset.UtcNow.AddDays(-1));

        var cut = ctx.Render<SessionsPage>();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("[data-testid=session-card-row]");
            Assert.Equal(2, rows.Count);

            var imported = Assert.Single(rows, r => r.QuerySelector("[data-testid=session-card-imported]") is not null);
            Assert.Contains("how are widgets counted", imported.TextContent, StringComparison.Ordinal);
            Assert.Contains("Imported", imported.TextContent, StringComparison.Ordinal);
            // The CLI's file is never written back to, so there is nothing to rename it in.
            Assert.Null(imported.QuerySelector("[data-testid=session-card-rename]"));

            var own = Assert.Single(rows, r => r.QuerySelector("[data-testid=session-card-imported]") is null);
            Assert.Contains("write the readme", own.TextContent, StringComparison.Ordinal);
            Assert.NotNull(own.QuerySelector("[data-testid=session-card-rename]"));
        }, Patience);
    }

    [Fact]
    public void An_imported_conversation_is_readable_and_says_what_import_cannot_recover()
    {
        using var ctx = Context();
        _imports.Put("abc-123.jsonl", ImportedJsonl, DateTimeOffset.UtcNow);

        var cut = ctx.Render<SessionsPage>(p => p.Add(c => c.ImportedName, "abc-123.jsonl"));

        cut.WaitForAssertion(() =>
        {
            var view = cut.Find("[data-testid=imported-conversation]");
            Assert.Contains("Imported", view.QuerySelector("[data-testid=imported-badge]")!.TextContent, StringComparison.Ordinal);

            var disclaimer = cut.Find("[data-testid=imported-disclaimer]").TextContent;
            Assert.Contains("Tool calls, token counts", disclaimer, StringComparison.Ordinal);
            Assert.Contains("cost are not recoverable", disclaimer, StringComparison.Ordinal);
            Assert.Contains("not the complete conversation", disclaimer, StringComparison.Ordinal);

            var transcript = cut.Find("[data-testid=imported-transcript]");
            Assert.Contains("how are widgets counted", transcript.QuerySelector("[data-testid=bubble-user]")!.TextContent, StringComparison.Ordinal);
            Assert.Contains("One by one.", transcript.QuerySelector("[data-testid=bubble-assistant]")!.TextContent, StringComparison.Ordinal);
        }, Patience);

        // Nothing here offers to run anything: no composer, no resume.
        Assert.Empty(cut.FindAll("[data-testid=session-pane]"));
        Assert.Empty(cut.FindAll("[data-testid=resume-session]"));
    }

    [Fact]
    public void An_imported_conversation_too_large_to_read_says_so_under_the_same_disclaimer()
    {
        using var ctx = Context();
        var conversation = new ImportedConversation(
            Workspace, "huge.jsonl", "Untitled conversation", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            100L * 1024 * 1024, [], ImportedConversationProblem.TooLarge);

        var cut = ctx.Render<ImportedTranscript>(p => p.Add(c => c.Conversation, conversation));

        Assert.Contains("not recoverable", cut.Find("[data-testid=imported-disclaimer]").TextContent, StringComparison.Ordinal);
        Assert.Contains("too large to import (100.0 MB)", cut.Find("[data-testid=imported-too-large]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=imported-transcript]"));
    }

    // ---- renaming a conversation (backlog 109) ---------------------------------------------

    private static KeyboardEventArgs Key(string key) => new() { Key = key };

    [Fact]
    public async Task Renaming_from_the_list_card_updates_the_shown_title_on_both_surfaces()
    {
        using var ctx = Context();
        var session = StartSession("write the readme");
        var cut = ctx.Render<SessionsPage>();

        await cut.InvokeAsync(() => cut.Find("[data-testid=session-card-rename]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=session-card-rename-input]").Input("Readme rewrite"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=session-card-rename-input]").KeyDown(Key("Enter")));

        cut.WaitForAssertion(() =>
        {
            var card = cut.Find("[data-testid=session-card]");
            Assert.Contains("Readme rewrite", card.TextContent);
            Assert.DoesNotContain("write the readme", card.TextContent);
            // The pane is the other view of the same conversation, so it shows the new name too.
            Assert.Equal("Readme rewrite", cut.Find("[data-testid=session-pane] .session-pane-title").TextContent.Trim());
        }, Patience);
        Assert.Empty(cut.FindAll("[data-testid=session-card-rename-input]"));

        // Persisted, and the derived title kept as the fallback rather than overwritten.
        var stored = _store.Find(session.Id)!;
        Assert.Equal("Readme rewrite", stored.CustomTitle);
        Assert.Equal("write the readme", stored.Title);
    }

    [Fact]
    public async Task Renaming_a_stored_conversation_from_the_list_card_persists_it()
    {
        // After a restart the conversation is only a row: the card still renames it, through the store.
        using var ctx = Context();
        var id = StoredConversation("an older conversation");
        var cut = ctx.Render<SessionsPage>();

        await cut.InvokeAsync(() => cut.Find("[data-testid=session-card-rename]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=session-card-rename-input]").Input("The old one"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=session-card-rename-input]").KeyDown(Key("Enter")));

        cut.WaitForAssertion(
            () => Assert.Contains("The old one", cut.Find("[data-testid=session-card]").TextContent),
            Patience);
        Assert.Equal("The old one", _store.Find(id)!.CustomTitle);
        Assert.Equal("an older conversation", _store.Find(id)!.Title);
    }

    [Fact]
    public async Task Renaming_from_the_pane_header_updates_the_shown_title()
    {
        using var ctx = Context();
        var session = StartSession("write the readme");
        var cut = Pane(ctx);
        Assert.Equal("write the readme", cut.Find(".session-pane-title").TextContent.Trim());

        await cut.InvokeAsync(() => cut.Find("[data-testid=rename-conversation]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=session-rename-input]").Input("Readme rewrite"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=session-rename-input]").KeyDown(Key("Enter")));

        cut.WaitForAssertion(
            () => Assert.Equal("Readme rewrite", cut.Find(".session-pane-title").TextContent.Trim()),
            Patience);
        Assert.Equal("Readme rewrite", _store.Find(session.Id)!.CustomTitle);
    }

    [Fact]
    public async Task Escape_cancels_a_rename_and_a_blank_or_unchanged_name_leaves_it_alone()
    {
        using var ctx = Context();
        var session = StartSession("write the readme");
        var cut = Pane(ctx);

        // Escape: what was typed is thrown away.
        await cut.InvokeAsync(() => cut.Find("[data-testid=rename-conversation]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=session-rename-input]").Input("something else"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=session-rename-input]").KeyDown(Key("Escape")));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=session-rename-input]")), Patience);
        Assert.Equal("write the readme", cut.Find(".session-pane-title").TextContent.Trim());

        // Blank: not a name.
        await cut.InvokeAsync(() => cut.Find("[data-testid=rename-conversation]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=session-rename-input]").Input("   "));
        await cut.InvokeAsync(() => cut.Find("[data-testid=session-rename-input]").KeyDown(Key("Enter")));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=session-rename-input]")), Patience);
        Assert.Equal("write the readme", cut.Find(".session-pane-title").TextContent.Trim());

        // Unchanged: nothing to do.
        await cut.InvokeAsync(() => cut.Find("[data-testid=rename-conversation]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=session-rename-input]").Input("write the readme"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=session-rename-input]").KeyDown(Key("Enter")));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=session-rename-input]")), Patience);

        Assert.Equal("write the readme", cut.Find(".session-pane-title").TextContent.Trim());
        Assert.Null(_store.Find(session.Id)!.CustomTitle);
    }

    [Fact]
    public void The_pane_offers_no_rename_before_there_is_a_conversation()
    {
        using var ctx = Context();

        var cut = Pane(ctx);

        Assert.Equal("Agent", cut.Find(".session-pane-title").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid=rename-conversation]"));
    }

    [Fact]
    public async Task Showing_another_conversation_closes_an_open_rename_box_in_the_pane()
    {
        // A draft typed against one conversation must never be committed against the next one the pane
        // shows: the box belongs to the conversation it was opened on and goes away with it.
        using var ctx = Context();
        var first = StartSession("write the readme");
        var second = _registry.Start(new SessionStartRequest("another-workspace", CopyPath, "fix the build"));
        var cut = ctx.Render<SessionPane>(p => p
            .Add(c => c.WorkspaceId, Workspace)
            .Add(c => c.WorkingDirectory, CopyPath)
            .Add(c => c.SessionId, first.Id));

        await cut.InvokeAsync(() => cut.Find("[data-testid=rename-conversation]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=session-rename-input]").Input("something else"));

        cut.Render(p => p.Add(c => c.SessionId, second.Id));

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("[data-testid=session-rename-input]"));
            Assert.Equal("fix the build", cut.Find(".session-pane-title").TextContent.Trim());
        }, Patience);
        Assert.Null(_store.Find(first.Id)!.CustomTitle);
        Assert.Null(_store.Find(second.Id)!.CustomTitle);
    }

    [Fact]
    public async Task A_newer_conversation_inserted_above_leaves_an_open_rename_box_on_its_own_card()
    {
        // The list is keyed by session id, so a card's open box stays with the conversation it was opened
        // on when a newer one appears above it, rather than drifting onto the newcomer by position.
        using var ctx = Context();
        StoredConversation("an older conversation");
        var cut = ctx.Render<SessionsPage>();

        await cut.InvokeAsync(() => cut.Find("[data-testid=session-card-rename]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=session-card-rename-input]").Input("The old one"));

        StartSession("write the readme");

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("[data-testid=session-card-row]");
            Assert.Equal(2, rows.Count);
            Assert.Contains("write the readme", rows[0].TextContent);
            Assert.Empty(rows[0].QuerySelectorAll("[data-testid=session-card-rename-input]"));
            var box = rows[1].QuerySelector("[data-testid=session-card-rename-input]");
            Assert.NotNull(box);
            Assert.Equal("The old one", box.GetAttribute("value"));
        }, Patience);
    }

    [Fact]
    public void The_page_without_a_workspace_explains_why_there_is_nothing_to_run()
    {
        using var ctx = Context(workspaceId: null);

        var cut = ctx.Render<SessionsPage>();

        Assert.NotEmpty(cut.FindAll("[data-testid=sessions-no-workspace]"));
    }

    [Fact]
    public void The_page_bar_and_the_split_share_one_flex_column_so_the_page_itself_never_scrolls()
    {
        // Backlog 16: without the .studio-workitems wrapper the page bar's 48px is added to a
        // 100%-tall split, the whole body scrolls, and the composer is pushed under the taskbar.
        // Git and Settings both wrap PageBar + PageSplit this way; this keeps Sessions honest.
        using var ctx = Context();

        var cut = ctx.Render<SessionsPage>();

        var bar = cut.Find(".studio-page-bar");
        var split = cut.Find(".page-split");
        var wrapper = split.ParentElement;

        Assert.NotNull(wrapper);
        Assert.Contains("studio-workitems", wrapper!.GetAttribute("class") ?? string.Empty);
        Assert.Contains("sessions-page", wrapper.GetAttribute("class") ?? string.Empty);
        Assert.Same(wrapper, bar.ParentElement);

        // The body pane is a flex column of its own, so the resume note cannot push the pane past
        // the bottom of the split either (the same bug one level down).
        Assert.Contains("sessions-body", cut.Find(".page-body").GetAttribute("class") ?? string.Empty);
    }

    // ---- keeping the newest content in view (backlog 17) ------------------------------------

    private static IRenderedComponent<SessionTranscript> Transcript(BunitContext ctx, LiveAgentSession session) =>
        ctx.Render<SessionTranscript>(p => p.Add(c => c.Session, session));

    [Fact]
    public void The_transcript_pins_itself_to_the_newest_content_when_it_opens()
    {
        using var ctx = Context();
        var session = StartSession();

        var cut = Transcript(ctx, session);

        var attach = ctx.JSInterop.VerifyInvoke("fravashiSessionScroll.attach");
        Assert.Equal(2, attach.Arguments.Count);
        Assert.IsType<DotNetObjectReference<SessionTranscript>>(attach.Arguments[1]);
        Assert.NotEmpty(cut.FindAll("[data-testid=session-transcript]"));
    }

    [Fact]
    public async Task A_reader_who_scrolled_up_is_offered_the_latest_rather_than_dragged_to_it()
    {
        using var ctx = Context();
        var session = StartSession();
        var cut = Transcript(ctx, session);

        await cut.InvokeAsync(() => cut.Instance.AwayFromBottomChanged(true));

        cut.WaitForAssertion(
            () => Assert.NotEmpty(cut.FindAll("[data-testid=jump-to-latest]")),
            TimeSpan.FromSeconds(10));
        ctx.JSInterop.VerifyNotInvoke("fravashiSessionScroll.toBottom");
    }

    [Fact]
    public async Task Jump_to_latest_returns_the_reader_to_the_newest_content()
    {
        using var ctx = Context();
        var session = StartSession();
        var cut = Transcript(ctx, session);
        await cut.InvokeAsync(() => cut.Instance.AwayFromBottomChanged(true));
        cut.WaitForAssertion(
            () => Assert.NotEmpty(cut.FindAll("[data-testid=jump-to-latest]")),
            TimeSpan.FromSeconds(10));

        await cut.InvokeAsync(() => cut.Find("[data-testid=jump-to-latest]").Click());

        ctx.JSInterop.VerifyInvoke("fravashiSessionScroll.toBottom");
        cut.WaitForAssertion(
            () => Assert.Empty(cut.FindAll("[data-testid=jump-to-latest]")),
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task At_the_newest_content_there_is_nothing_to_jump_to()
    {
        using var ctx = Context();
        var session = StartSession();
        var cut = Transcript(ctx, session);

        Assert.Empty(cut.FindAll("[data-testid=jump-to-latest]"));

        await cut.InvokeAsync(() => cut.Instance.AwayFromBottomChanged(true));
        cut.WaitForAssertion(
            () => Assert.NotEmpty(cut.FindAll("[data-testid=jump-to-latest]")),
            TimeSpan.FromSeconds(10));

        await cut.InvokeAsync(() => cut.Instance.AwayFromBottomChanged(false));
        cut.WaitForAssertion(
            () => Assert.Empty(cut.FindAll("[data-testid=jump-to-latest]")),
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Opening_another_conversation_starts_at_its_newest_message()
    {
        // A different conversation must not inherit where the reader was in the previous one. The
        // second session lives in another workspace: the registry allows only one running session per
        // workspace.
        using var ctx = Context();
        var cut = Transcript(ctx, StartSession());
        var second = _registry.Start(new SessionStartRequest("w2", "/workspaces/w2", "second"));

        cut.Render(p => p.Add(c => c.Session, second));

        ctx.JSInterop.VerifyInvoke("fravashiSessionScroll.toBottom");
    }

    [Fact]
    public async Task An_approval_the_agent_is_blocked_on_is_never_something_the_reader_scrolls_to()
    {
        // Approvals are siblings of the transcript, and the group is bounded so a pile-up shrinks the
        // transcript rather than pushing the composer off the pane.
        using var ctx = Context();
        var session = StartSession();
        _broker.RegisterSession(session.Id);
        var cut = Pane(ctx);

        _ = _broker.RequestAsync(
            session.Id, "Write", "toolu_1", new Dictionary<string, string> { ["file_path"] = "/workspaces/w1/a.md" });

        await Eventually(() => cut.FindAll("[data-testid=session-pending]").Count == 1);
        var group = cut.FindAll("[data-testid=session-pending]")[0];
        Assert.Equal("session-pane", group.ParentElement?.GetAttribute("data-testid"));
        Assert.Empty(cut.FindAll("[data-testid=session-transcript] [data-testid=session-pending]"));
        Assert.Single(group.QuerySelectorAll("[data-testid=approval-prompt]"));
    }

    [Fact]
    public async Task Both_surfaces_watch_the_same_session()
    {
        // The Sessions area and the Code page panel are two views of one object (decision 3).
        using var ctx = Context();
        StartSession();
        var first = Pane(ctx);
        var second = ctx.Render<SessionPane>(p => p
            .Add(c => c.WorkspaceId, Workspace)
            .Add(c => c.WorkingDirectory, CopyPath)
            .Add(c => c.Compact, true));

        _runner.Emit(new MessageEmitted(DateTimeOffset.UtcNow, TranscriptRole.Assistant, "on it"));

        await Eventually(() => first.Markup.Contains("on it") && second.Markup.Contains("on it"));
    }

    // ---- a replaced conversation is replaced EVERYWHERE -------------------------------------
    //
    // The owner's repro: on /sessions, New conversation, confirm stop-and-start; open /code; the
    // right panel still rendered the OLD conversation, and submitting there started a fresh session
    // while the displayed history silently vanished. The fresh state lived in the one pane that was
    // clicked, while every other pane kept resolving the workspace's latest session out of the
    // registry, which still held the replaced one.

    /// <summary>The Code page's view of the conversation: a compact pane with no session id.</summary>
    private IRenderedComponent<SessionPane> CodePanel(BunitContext ctx) =>
        ctx.Render<SessionPane>(p => p
            .Add(c => c.WorkspaceId, Workspace)
            .Add(c => c.WorkingDirectory, CopyPath)
            .Add(c => c.Compact, true));

    /// <summary>Replaces the workspace's conversation the way the owner did, on the Sessions page.</summary>
    private static async Task ReplaceOnTheSessionsPage(BunitContext ctx)
    {
        var page = ctx.Render<SessionsPage>();
        await page.InvokeAsync(() => page.Find("[data-testid=new-conversation]").Click());
        page.WaitForAssertion(
            () => Assert.NotEmpty(page.FindAll("[data-testid=new-conversation-confirm]")), Patience);
        await page.InvokeAsync(() => page.Find("[data-testid=new-conversation-confirm-yes]").Click());
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-testid=session-empty]")), Patience);
    }

    [Fact]
    public async Task A_conversation_replaced_on_the_sessions_page_leaves_the_code_panel_too()
    {
        using var ctx = Context();
        StartSession("the first conversation");
        var panel = CodePanel(ctx);
        Assert.Contains("the first conversation", panel.Markup, StringComparison.Ordinal);

        // A second context so the two surfaces do not share a NavigationManager; the registry is the
        // same object, so this is the same workspace conversation seen from two windows.
        using var other = Context();
        await ReplaceOnTheSessionsPage(other);

        // One conversation per workspace, two views of it: the replaced transcript may not keep
        // rendering in the panel as if it were still the live conversation.
        panel.WaitForAssertion(
            () =>
            {
                Assert.NotEmpty(panel.FindAll("[data-testid=session-empty]"));
                Assert.DoesNotContain("the first conversation", panel.Markup, StringComparison.Ordinal);
            },
            Patience);
    }

    [Fact]
    public async Task A_panel_opened_after_the_replacement_shows_the_new_conversation_and_never_the_old_one()
    {
        // The repro's exact order: the panel is opened AFTER the replacement, so there is no live pane
        // to be updated; a brand new one has to resolve to the fresh conversation on its own.
        using var ctx = Context();
        var replaced = StartSession("the first conversation");
        await ReplaceOnTheSessionsPage(ctx);

        // The real CLI answers the stop by ending the session; the fake's Stop does not, so the end
        // is emitted here. Without it the replaced session still counts as running and the send
        // below would be refused rather than starting the new conversation.
        _runner.Emit(new SessionEnded(DateTimeOffset.UtcNow, AgentSessionState.Cancelled, null));
        await Eventually(() => !replaced.IsRunning, "the replaced session should have ended");

        using var later = Context();
        var panel = CodePanel(later);

        panel.WaitForAssertion(
            () =>
            {
                Assert.NotEmpty(panel.FindAll("[data-testid=session-empty]"));
                Assert.DoesNotContain("the first conversation", panel.Markup, StringComparison.Ordinal);
            },
            Patience);

        // And submitting there is the NEW conversation, with nothing vanishing: the old transcript
        // was never on screen to vanish.
        await panel.InvokeAsync(() => panel.Find("[data-testid=composer-input]").Input("the second conversation"));
        await panel.InvokeAsync(() => panel.Find("[data-testid=send-prompt]").Click());

        panel.WaitForAssertion(
            () =>
            {
                Assert.Contains(
                    panel.FindAll("[data-testid=bubble-user]"),
                    b => b.TextContent.Contains("the second conversation", StringComparison.Ordinal));
                Assert.DoesNotContain("the first conversation", panel.Markup, StringComparison.Ordinal);
            },
            Patience);
    }

    // ---- the questions payload as real controls (backlog 92) ----------------------------------

    /// <summary>
    /// A questions document, in the wire shape the CLI produces. Descriptions are present here because
    /// the mockup's whole point is that an option carries its trade-off; the reader tests cover the
    /// measured case where the model omits them.
    /// </summary>
    private const string CachingQuestion =
        "{\"version\":\"1\",\"kind\":\"questions\",\"message\":\"Where should cached responses live?\","
        + "\"questions\":[{\"id\":\"storage\",\"question\":\"Where should cached responses live?\","
        + "\"options\":[{\"label\":\"In memory\",\"description\":\"Fastest, lost on restart\"},"
        + "{\"label\":\"Redis\",\"description\":\"Shared and survives restarts, adds a dependency\"}]}]}";

    private static TurnCompleted Asked(string json) =>
        new(DateTimeOffset.UtcNow, null, false, "completed", AgentTurnDocumentReader.Read(json));

    /// <summary>Starts a session and drives it to the point where it has asked something.</summary>
    private async Task<LiveAgentSession> AskingSession(
        IRenderedComponent<SessionPane> cut, string json = CachingQuestion)
    {
        var session = StartSession("add caching to the api");
        _runner.Emit(Asked(json));
        cut.WaitForAssertion(
            () => Assert.NotEmpty(cut.FindAll("[data-testid=session-ask]")), Patience);
        return session;
    }

    [Fact]
    public async Task A_question_becomes_options_carrying_their_own_reasons()
    {
        // The reported defect was "it can not ask questions": the agent's choices arrived as prose and
        // the user had to retype one. This is the whole feature in one assertion.
        using var ctx = Context();
        var cut = Pane(ctx);
        await AskingSession(cut);

        var options = cut.FindAll("[data-testid=session-ask-option]");
        Assert.Equal(2, options.Count);
        Assert.Contains("In memory", options[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Fastest, lost on restart", options[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Redis", options[1].TextContent, StringComparison.Ordinal);
        Assert.Contains("adds a dependency", options[1].TextContent, StringComparison.Ordinal);
        Assert.Contains("Where should cached responses live?", cut.Find("[data-testid=session-ask]").TextContent);
    }

    [Fact]
    public async Task Picking_an_option_continues_the_conversation_as_the_users_own_turn()
    {
        using var ctx = Context();
        var cut = Pane(ctx);
        await AskingSession(cut);

        await cut.InvokeAsync(() => cut.FindAll("[data-testid=session-ask-option]")[1].Click());

        // It went to the agent as a turn, not through some second transport (ADR 0022, decision 4).
        await Eventually(() => _runner.Sent.Count == 1);
        Assert.Equal("Where should cached responses live? Redis", _runner.Sent[0]);

        // And it reads as the user's own turn in the transcript, not as a second agent bubble.
        cut.WaitForAssertion(
            () => Assert.Contains(
                cut.FindAll("[data-testid=bubble-user]"),
                b => b.TextContent.Contains("Redis", StringComparison.Ordinal)),
            Patience);
    }

    [Fact]
    public async Task One_question_with_one_answer_needs_no_send_button()
    {
        // The approved mockup draws bare option buttons and no Send. A second click to confirm a single
        // choice is a control the mockup does not have and the user does not need.
        using var ctx = Context();
        var cut = Pane(ctx);
        await AskingSession(cut);

        Assert.Empty(cut.FindAll("[data-testid=session-ask-send]"));
    }

    [Fact]
    public async Task A_multi_select_question_gathers_the_picks_before_it_sends_them()
    {
        using var ctx = Context();
        var cut = Pane(ctx);
        await AskingSession(cut,
            "{\"version\":\"1\",\"kind\":\"questions\",\"message\":\"Which may I touch?\",\"questions\":["
            + "{\"id\":\"scope\",\"question\":\"Which may I touch?\",\"multiSelect\":true,\"options\":["
            + "{\"label\":\"src\"},{\"label\":\"tests\"},{\"label\":\"docs\"}]}]}");

        // Nothing leaves on the first click: a multi-select that sent on click could never be multi.
        await cut.InvokeAsync(() => cut.FindAll("[data-testid=session-ask-option]")[0].Click());
        Assert.Empty(_runner.Sent);
        // Wait for the toggle's render to settle rather than reading aria-pressed synchronously (item
        // 106 class; sibling of the WaitForAssertion at A_pick_can_be_taken_back). Do not revert to a
        // bare Assert: a live ask/runner pump feeds this pane, so the read can race the toggle render.
        cut.WaitForAssertion(
            () => Assert.Equal("true", cut.FindAll("[data-testid=session-ask-option]")[0].GetAttribute("aria-pressed")),
            Patience);

        await cut.InvokeAsync(() => cut.FindAll("[data-testid=session-ask-option]")[1].Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=session-ask-send]").Click());

        await Eventually(() => _runner.Sent.Count == 1);
        Assert.Equal("Which may I touch? src, tests", _runner.Sent[0]);
    }

    [Fact]
    public async Task A_pick_can_be_taken_back_before_it_is_sent()
    {
        using var ctx = Context();
        var cut = Pane(ctx);
        await AskingSession(cut,
            "{\"version\":\"1\",\"kind\":\"questions\",\"message\":\"Which may I touch?\",\"questions\":["
            + "{\"id\":\"scope\",\"question\":\"Which may I touch?\",\"multiSelect\":true,\"options\":["
            + "{\"label\":\"src\"},{\"label\":\"tests\"}]}]}");

        await cut.InvokeAsync(() => cut.FindAll("[data-testid=session-ask-option]")[0].Click());
        await cut.InvokeAsync(() => cut.FindAll("[data-testid=session-ask-option]")[0].Click());

        // Wait for the second toggle's render to settle rather than reading the DOM synchronously
        // (flaked under parallel load, item 106 class): with nothing picked the option is unpressed
        // and there is no answer to send.
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("false", cut.FindAll("[data-testid=session-ask-option]")[0].GetAttribute("aria-pressed"));
            Assert.True(cut.Find("[data-testid=session-ask-send]").HasAttribute("disabled"),
                "with nothing picked there is no answer to send");
        }, Patience);
    }

    [Fact]
    public async Task Two_questions_are_answered_in_one_turn()
    {
        using var ctx = Context();
        var cut = Pane(ctx);
        await AskingSession(cut,
            "{\"version\":\"1\",\"kind\":\"questions\",\"message\":\"Two things.\",\"questions\":["
            + "{\"id\":\"a\",\"question\":\"Where?\",\"options\":[{\"label\":\"In memory\"},{\"label\":\"Redis\"}]},"
            + "{\"id\":\"b\",\"question\":\"When?\",\"options\":[{\"label\":\"Now\"},{\"label\":\"Later\"}]}]}");

        Assert.Equal(2, cut.FindAll("[data-testid=session-ask-block]").Count);

        // The first click must not fire the turn: the second question is still unanswered.
        await cut.InvokeAsync(() => cut.FindAll("[data-testid=session-ask-option]")[1].Click());
        Assert.Empty(_runner.Sent);
        // Wait for the render to settle rather than reading the disabled attribute synchronously (item
        // 106 class; a live ask pump feeds this pane). Do not revert to a bare Assert.
        cut.WaitForAssertion(
            () => Assert.True(cut.Find("[data-testid=session-ask-send]").HasAttribute("disabled")),
            Patience);

        await cut.InvokeAsync(() => cut.FindAll("[data-testid=session-ask-option]")[2].Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=session-ask-send]").Click());

        await Eventually(() => _runner.Sent.Count == 1);
        Assert.Equal("Where? Redis\nWhen? Now", _runner.Sent[0]);
    }

    [Fact]
    public async Task A_question_that_has_been_answered_stops_offering_controls()
    {
        using var ctx = Context();
        var cut = Pane(ctx);
        await AskingSession(cut);

        await cut.InvokeAsync(() => cut.FindAll("[data-testid=session-ask-option]")[0].Click());

        cut.WaitForAssertion(
            () => Assert.Single(cut.FindAll("[data-testid=session-ask-answered]")), Patience);

        // The options stay, because they are the record of what was offered; they just cannot be
        // clicked again. A second answer would be a turn the agent never asked for.
        Assert.All(
            cut.FindAll("[data-testid=session-ask-option]"),
            option => Assert.True(option.HasAttribute("disabled")));
    }

    [Fact]
    public async Task Answering_in_your_own_words_closes_the_question_too()
    {
        // The controls are an offer, never a gate: a user who wants to say something the options do not
        // cover types it, and the question stops waiting because the agent got its answer.
        using var ctx = Context();
        var cut = Pane(ctx);
        await AskingSession(cut);

        // The block says so itself while it is open, so the escape hatch is not folklore.
        Assert.Contains("Or answer in your own words", cut.Markup, StringComparison.Ordinal);

        await cut.InvokeAsync(() => cut.Find("[data-testid=composer-input]").Input("neither, use the file system"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=send-prompt]").Click());

        cut.WaitForAssertion(
            () => Assert.Single(cut.FindAll("[data-testid=session-ask-answered]")), Patience);
        Assert.Equal("neither, use the file system", _runner.Sent[0]);
    }

    [Fact]
    public async Task An_unanswered_question_is_still_there_after_the_circuit_comes_back()
    {
        // The open state is read off the transcript, never held in the component, so a reload and a
        // reconnect both land on the same question rather than on a block that has forgotten itself.
        using var ctx = Context();
        var first = Pane(ctx);
        await AskingSession(first);

        var second = Pane(ctx);

        Assert.Equal(2, second.FindAll("[data-testid=session-ask-option]").Count);
        Assert.Empty(second.FindAll("[data-testid=session-ask-answered]"));
        Assert.All(
            second.FindAll("[data-testid=session-ask-option]"),
            option => Assert.False(option.HasAttribute("disabled")));
    }

    [Fact]
    public async Task A_question_the_conversation_ended_on_says_there_is_nothing_to_answer_to()
    {
        // The honest case behind "survives a studio restart": the question is still there, and so is
        // what was offered, but the process that asked it is gone and no button here can reach it.
        using var ctx = Context();
        var cut = Pane(ctx);
        await AskingSession(cut);

        _runner.Emit(new SessionEnded(DateTimeOffset.UtcNow, AgentSessionState.Completed, null));

        cut.WaitForAssertion(
            () => Assert.Single(cut.FindAll("[data-testid=session-ask-unanswerable]")), Patience);
        Assert.Contains("Resume it to reply", cut.Find("[data-testid=session-ask-unanswerable]").TextContent);
        Assert.All(
            cut.FindAll("[data-testid=session-ask-option]"),
            option => Assert.True(option.HasAttribute("disabled")));
    }

    [Fact]
    public async Task A_questions_document_from_a_newer_studio_renders_its_message_and_no_controls()
    {
        // Item 92's acceptance: a document written under an older (or newer) schema version still
        // renders. It renders as its message, which item 25's reader already guarantees; what must not
        // happen is controls drawn from a payload this build never validated.
        using var ctx = Context();
        var cut = Pane(ctx);
        StartSession("add caching to the api");

        _runner.Emit(Asked(
            "{\"version\":\"9\",\"kind\":\"questions\",\"message\":\"Pick one.\",\"questions\":["
            + "{\"id\":\"a\",\"question\":\"Which?\",\"options\":[{\"label\":\"A\"}]}]}"));

        // Something the panel does render, so this waits on a real render rather than on nothing.
        _runner.Emit(new MessageEmitted(DateTimeOffset.UtcNow, TranscriptRole.Assistant, "Pick one."));
        await Eventually(() => cut.Markup.Contains("Pick one.", StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-testid=session-ask]"));
    }

    [Fact]
    public async Task What_the_real_cli_asked_renders_as_controls_in_the_panel()
    {
        // The document below is a verbatim capture (bar its elided prose) from claude 2.1.223 in the
        // fravashi container on 2026-08-07, driven with the assembled default schema. It is here because
        // the first real question the CLI produced was nothing like the tidy one-question example: three
        // questions at once, eight options on the first, and not one option description. This is the
        // panel meeting that.
        const string captured =
            "{\"version\":\"1\",\"kind\":\"questions\",\"message\":\"Before we add response caching...\","
            + "\"questions\":[{\"id\":\"tech_stack\",\"question\":\"What tech stack is your API built with?\","
            + "\"options\":[{\"label\":\"Node.js / Express\"},{\"label\":\"Python / Flask or FastAPI\"},"
            + "{\"label\":\"Python / Django\"},{\"label\":\"Go\"},{\"label\":\"Java / Spring\"},"
            + "{\"label\":\"C# / .NET\"},{\"label\":\"Ruby on Rails\"},{\"label\":\"Other\"}]},"
            + "{\"id\":\"scale\",\"question\":\"How many instances/replicas of your API run in production?\","
            + "\"options\":[{\"label\":\"Single server only\"},{\"label\":\"Multiple servers (2-10)\"},"
            + "{\"label\":\"Large scale (10+ replicas)\"},{\"label\":\"Not sure / still in development\"}]},"
            + "{\"id\":\"persistence\",\"question\":\"Should cached responses survive a server restart?\","
            + "\"options\":[{\"label\":\"Yes, persistence is important\"},"
            + "{\"label\":\"No, ephemeral cache is fine\"},{\"label\":\"Not sure yet\"}]}]}";

        using var ctx = Context();
        var cut = Pane(ctx);
        await AskingSession(cut, captured);

        Assert.Equal(3, cut.FindAll("[data-testid=session-ask-block]").Count);
        Assert.Equal(15, cut.FindAll("[data-testid=session-ask-option]").Count);

        // One pick per question, then one turn carrying all three, which is the text the same CLI was
        // fed back and answered.
        var options = cut.FindAll("[data-testid=session-ask-option]");
        await cut.InvokeAsync(() => options[5].Click());
        await cut.InvokeAsync(() => cut.FindAll("[data-testid=session-ask-option]")[8].Click());
        await cut.InvokeAsync(() => cut.FindAll("[data-testid=session-ask-option]")[13].Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=session-ask-send]").Click());

        await Eventually(() => _runner.Sent.Count == 1);
        Assert.Equal(
            "What tech stack is your API built with? C# / .NET\n"
            + "How many instances/replicas of your API run in production? Single server only\n"
            + "Should cached responses survive a server restart? No, ephemeral cache is fine",
            _runner.Sent[0]);
    }

    [Fact]
    public void A_question_in_a_stored_conversation_is_shown_with_what_was_offered()
    {
        // The studio restarted while a question was waiting. The Sessions area reads that conversation
        // back from SQLite, so the question has to survive the round trip through the store and say
        // plainly that answering it means resuming.
        using var ctx = Context();
        var id = AgentSessionId.New();
        _store.Save(new SessionRecord(
            id, Workspace, AgentTool.Claude, "add caching", "prov-1",
            AgentSessionState.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        // Through the real serializer, because the claim is that the question survives being written to
        // a row and read back: holding the live object would prove nothing about a restart.
        _store.Append(id, [
            new MessageEmitted(DateTimeOffset.UtcNow, TranscriptRole.User, "add caching to the api"),
            AgentEventJson.Deserialize("turn_completed", AgentEventJson.Serialize(Asked(CachingQuestion)))!,
        ]);

        var cut = ctx.Render<SessionsPage>(p => p.Add(c => c.Id, id.Value.ToString("D")));

        var stored = cut.Find("[data-testid=stored-transcript]");
        Assert.Equal(2, stored.QuerySelectorAll("[data-testid=session-ask-option]").Length);
        Assert.Single(stored.QuerySelectorAll("[data-testid=session-ask-unanswerable]"));
        Assert.Single(cut.FindAll("[data-testid=resume-session]"));
    }

    // ---- backlog 34: the home card's Resume arrives on the URL ------------------------------

    /// <summary>Puts the circuit on the route the home card's Resume navigates to.</summary>
    private static void Navigate(BunitContext ctx, AgentSessionId id) =>
        ctx.Services.GetRequiredService<NavigationManager>()
            .NavigateTo($"/sessions/{id.Value:D}?resume=true");

    [Fact]
    public void A_resume_asked_for_on_the_url_arms_the_composer_rather_than_only_showing_the_conversation()
    {
        // The home surface's Resume is a NAVIGATION (it may even be switching workspace), so the
        // request has to ride on the route and mean exactly what the page's own Resume button means.
        using var ctx = Context();
        var id = StoredConversationWithTurns("add caching");

        // Through the URL, which is how it really arrives: bunit refuses to set a query-supplied
        // parameter directly, so the route the card navigates to is what is under test here.
        Navigate(ctx, id);
        var cut = ctx.Render<SessionsPage>(p => p.Add(c => c.Id, id.Value.ToString("D")));

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("[data-testid=stored-transcript]"));
            Assert.Contains("add caching to the api", PaneOutcome.Of(cut).Turns, StringComparison.Ordinal);
            Assert.Single(cut.FindAll("[data-testid=composer-input]"));
        }, Patience);
    }

    // ---- resuming in place (owner report 2026-09-15) ----------------------------------------

    [Fact]
    public async Task Resume_shows_the_earlier_conversation_at_once_with_the_composer_armed()
    {
        // The owner picked Resume and got an EMPTY chat with a note; the earlier turns only came back
        // after they had typed something. What Resume shows must be what the next message lands under.
        using var ctx = Context();
        var id = StoredConversationWithTurns("add caching");
        var cut = ctx.Render<SessionsPage>(p => p.Add(c => c.Id, id.Value.ToString("D")));

        await cut.InvokeAsync(() => cut.Find("[data-testid=resume-session]").Click());

        cut.WaitForAssertion(() =>
        {
            var pane = PaneOutcome.Of(cut);
            Assert.False(pane.Empty, "a resumed conversation is not an empty chat");
            Assert.Contains("add caching to the api", pane.Turns, StringComparison.Ordinal);
            Assert.Contains("caching is in", pane.Turns, StringComparison.Ordinal);
            Assert.Empty(cut.FindAll("[data-testid=resuming-note]"));
            Assert.False(cut.Find("[data-testid=composer-input]").HasAttribute("disabled"));
        }, Patience);

        await Eventually(() => _runner.LastRequest?.SessionId == id, "the conversation resumes under its own id");
        Assert.Equal("prov-1", _runner.LastRequest!.ResumeSessionId);
        Assert.Equal(string.Empty, _runner.LastRequest.Prompt);
    }

    [Fact]
    public void A_conversation_whose_agent_history_was_removed_says_so_instead_of_offering_resume()
    {
        // Conversations that ended before resume-in-place had the CLI's file deleted at teardown. Offering
        // Resume for them could only end in the CLI's raw "No conversation found".
        using var ctx = Context();
        var files = new AgentConversationFiles(
            Path.Combine(Path.GetTempPath(), "fravashi-no-such-data-" + Guid.NewGuid().ToString("N")), () => null);
        using var registry = new LiveSessionRegistry(_runner, _store, _broker, conversations: files);
        ctx.Services.AddSingleton(registry);
        var id = StoredConversationWithTurns("add caching");

        var cut = ctx.Render<SessionsPage>(p => p.Add(c => c.Id, id.Value.ToString("D")));

        Assert.Equal(LiveSessionRegistry.HistoryRemovedNotice, cut.Find("[data-testid=resume-history-removed]").TextContent.Trim());
        Assert.True(cut.Find("[data-testid=resume-session]").HasAttribute("disabled"));
    }

    [Fact]
    public void A_conversation_whose_agent_history_is_there_offers_resume_without_the_notice()
    {
        using var ctx = Context();
        var data = Path.Combine(Path.GetTempPath(), "fravashi-data-" + Guid.NewGuid().ToString("N"));
        var id = StoredConversationWithTurns("add caching");
        var file = Path.Combine(data, "agent-sessions", id.ToString(), "claude", "projects", "-workspaces-w1", "prov-1.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{}\n");
        try
        {
            using var registry = new LiveSessionRegistry(
                _runner, _store, _broker, conversations: new AgentConversationFiles(data, () => null));
            ctx.Services.AddSingleton(registry);

            var cut = ctx.Render<SessionsPage>(p => p.Add(c => c.Id, id.Value.ToString("D")));

            Assert.Empty(cut.FindAll("[data-testid=resume-history-removed]"));
            Assert.False(cut.Find("[data-testid=resume-session]").HasAttribute("disabled"));
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Fact]
    public async Task A_resumed_conversation_rises_to_the_top_of_the_list()
    {
        using var ctx = Context();
        var older = AgentSessionId.New();
        var newer = AgentSessionId.New();
        var hoursAgo = DateTimeOffset.UtcNow.AddHours(-3);
        _store.Save(new SessionRecord(
            older, Workspace, AgentTool.Claude, "the older one", "prov-older", AgentSessionState.Completed, hoursAgo, hoursAgo));
        _store.Save(new SessionRecord(
            newer, Workspace, AgentTool.Claude, "the newer one", "prov-newer", AgentSessionState.Completed,
            hoursAgo.AddHours(2), hoursAgo.AddHours(2)));
        var cut = ctx.Render<SessionsPage>(p => p.Add(c => c.Id, older.Value.ToString("D")));
        Assert.Contains("the newer one", cut.FindAll("[data-testid=session-card]")[0].TextContent, StringComparison.Ordinal);

        await cut.InvokeAsync(() => cut.Find("[data-testid=resume-session]").Click());

        cut.WaitForAssertion(
            () => Assert.Contains("the older one", cut.FindAll("[data-testid=session-card]")[0].TextContent, StringComparison.Ordinal),
            Patience);
    }

    [Fact]
    public async Task A_resumed_conversation_stays_one_entry_in_the_list_after_the_next_message()
    {
        using var ctx = Context();
        var id = StoredConversationWithTurns("add caching");
        var cut = ctx.Render<SessionsPage>(p => p.Add(c => c.Id, id.Value.ToString("D")));

        await cut.InvokeAsync(() => cut.Find("[data-testid=resume-session]").Click());
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=composer-input]")), Patience);
        await cut.InvokeAsync(() => cut.Find("[data-testid=composer-input]").Input("now add expiry"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=send-prompt]").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("now add expiry", PaneOutcome.Of(cut).Turns, StringComparison.Ordinal);
            var card = Assert.Single(cut.FindAll("[data-testid=session-card]"));
            Assert.Contains("add caching", card.TextContent, StringComparison.Ordinal);
        }, Patience);
        Assert.Single(_store.ForWorkspace(Workspace));
        Assert.Equal(
            ["add caching to the api", "caching is in", "now add expiry"],
            _store.Transcript(id).OfType<MessageEmitted>().Select(m => m.Text));
    }

    [Fact]
    public void A_conversation_that_cannot_be_resumed_is_still_opened_rather_than_refused()
    {
        // No CLI session to continue: the page shows the conversation with its Resume disabled, which
        // is the same answer the reader would get by pressing the button.
        using var ctx = Context();
        var id = AgentSessionId.New();
        var when = DateTimeOffset.UtcNow.AddHours(-1);
        _store.Save(new SessionRecord(
            id, Workspace, AgentTool.Claude, "add caching", null, AgentSessionState.Completed, when, when));

        Navigate(ctx, id);
        var cut = ctx.Render<SessionsPage>(p => p.Add(c => c.Id, id.Value.ToString("D")));

        Assert.Empty(cut.FindAll("[data-testid=resuming-note]"));
        Assert.Single(cut.FindAll("[data-testid=session-history]"));
        Assert.True(cut.Find("[data-testid=resume-session]").HasAttribute("disabled"));
    }

    // ---- starting fresh from the sidebar ----------------------------------------------------

    /// <summary>A conversation that has already ended, as the sidebar and the store see it.</summary>
    private AgentSessionId StoredConversation(string title)
    {
        var id = AgentSessionId.New();
        var when = DateTimeOffset.UtcNow.AddHours(-1);
        _store.Save(new SessionRecord(
            id, Workspace, AgentTool.Claude, title, "prov-1", AgentSessionState.Completed, when, when));
        return id;
    }

    /// <summary>An ended conversation that said something: the user's turn, the answer, and its ending.</summary>
    private AgentSessionId StoredConversationWithTurns(string title)
    {
        var id = StoredConversation(title);
        var when = _store.Find(id)!.StartedUtc;
        _store.Append(id, [
            new MessageEmitted(when, TranscriptRole.User, title + " to the api"),
            new MessageEmitted(when, TranscriptRole.Assistant, "caching is in"),
            new SessionEnded(when, AgentSessionState.Completed, null),
        ]);
        return id;
    }

    /// <summary>
    /// What the reader is actually looking at in the pane. Element presence is NOT enough here and that
    /// is the whole point: the previous round asserted that the stored history was gone and a pane was
    /// present, and both were true while the reader sat inside the still-running conversation, complete
    /// with its title, its turns and its Stop button. So this reads the pane's CONTENT.
    /// </summary>
    private sealed record PaneOutcome(string Head, bool Asking, bool Empty, bool Stoppable, string Turns)
    {
        public static PaneOutcome Of(IRenderedComponent<SessionsPage> cut) => new(
            cut.Find("[data-testid=session-pane] .session-pane-title").TextContent.Trim(),
            cut.FindAll("[data-testid=new-conversation-confirm]").Count > 0,
            cut.FindAll("[data-testid=session-empty]").Count > 0,
            cut.FindAll("[data-testid=stop-session]").Count > 0,
            string.Join(
                " / ",
                cut.FindAll("[data-testid=session-pane] .session-turn-body")
                    .Select(turn => turn.TextContent.Trim())));
    }

    /// <summary>The sidebar's New conversation, from a state where a stored conversation is on screen.</summary>
    private async Task<IRenderedComponent<SessionsPage>> NewFromTheSidebar(BunitContext ctx, string stored)
    {
        var cut = ctx.Render<SessionsPage>();
        var older = cut.FindAll("[data-testid=session-card]")
            .Single(card => card.TextContent.Contains(stored, StringComparison.Ordinal));

        // Selecting it takes the pane off the screen and disposes it, which is exactly the state in which
        // the sidebar is the only New conversation control the reader has.
        await cut.InvokeAsync(() => older.Click());
        // Wait for the selection to take effect rather than reading the DOM synchronously (item 106
        // class): the click disposes the pane and mutates registry state on the render pump. Do not
        // revert to a bare Assert.
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=session-history]")), Patience);

        await cut.InvokeAsync(() => cut.Find("[data-testid=new-session]").Click());
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=session-history]")), Patience);
        return cut;
    }

    [Fact]
    public async Task New_conversation_from_the_sidebar_asks_before_it_ends_a_running_turn()
    {
        // Two rounds of this defect. First the click was swallowed outright (the page called a DISPOSED
        // pane, because Blazor never nulls a component @ref when the component leaves the render tree).
        // Then the reader MOVED and landed straight in the running conversation: the page only dropped its
        // selection, and the brand new pane resolved to the workspace's latest session. What is asserted
        // is therefore what the reader is looking at, not that a pane exists.
        using var ctx = Context();
        StartSession("write the readme");
        StoredConversation("an older conversation");

        var cut = await NewFromTheSidebar(ctx, "an older conversation");

        cut.WaitForAssertion(() => Assert.True(PaneOutcome.Of(cut).Asking), Patience);
        Assert.Contains(
            "still running",
            cut.Find("[data-testid=new-conversation-confirm]").TextContent,
            StringComparison.Ordinal);

        // And answering really does end that turn and leave a conversation with nothing in it, which is
        // what the control's own label promises.
        await cut.InvokeAsync(() => cut.Find("[data-testid=new-conversation-confirm-yes]").Click());

        cut.WaitForAssertion(
            () => Assert.Equal(
                new PaneOutcome("Agent", Asking: false, Empty: true, Stoppable: false, Turns: string.Empty),
                PaneOutcome.Of(cut)),
            Patience);
        Assert.True(_runner.Stopped);
    }

    [Fact]
    public async Task The_sidebars_new_conversation_and_the_panels_own_land_in_the_same_place()
    {
        // The page's action documents itself as meaning the same thing as the panel's. It did not: from
        // the same running-session state one asked and the other silently dropped the reader into the
        // running turn. Measuring both and comparing is what the previous test could not do, because it
        // never looked at where either one landed.
        using var ctx = Context();
        StartSession("write the readme");
        StoredConversation("an older conversation");

        // The panel's own control, which is only reachable while a pane is on screen.
        var viaPanel = ctx.Render<SessionsPage>();
        await viaPanel.InvokeAsync(() => viaPanel.Find("[data-testid=new-conversation]").Click());
        viaPanel.WaitForAssertion(() => Assert.True(PaneOutcome.Of(viaPanel).Asking), Patience);

        // A second context so the two pages do not share a NavigationManager; the registry and the store
        // are the same objects, so this is the same workspace in the same state.
        using var other = Context();
        var viaSidebar = await NewFromTheSidebar(other, "an older conversation");

        viaSidebar.WaitForAssertion(
            () => Assert.Equal(PaneOutcome.Of(viaPanel), PaneOutcome.Of(viaSidebar)), Patience);
    }

    [Fact]
    public async Task New_conversation_from_the_sidebar_empties_the_pane_when_nothing_is_running()
    {
        // The other half of the same defect, and the one no confirm strip covers: the registry keeps an
        // ENDED session, so resolving the new pane to the workspace's latest one served the reader the
        // finished conversation's turns under a heading with its title. Nothing is running, so there is
        // nothing to ask about and the outcome is simply an empty conversation.
        using var ctx = Context();
        var session = StartSession("write the readme");
        _runner.Emit(new SessionEnded(DateTimeOffset.UtcNow, AgentSessionState.Completed, null));
        await Eventually(() => !session.IsRunning, "the session should have ended");
        StoredConversation("an older conversation");

        var cut = await NewFromTheSidebar(ctx, "an older conversation");

        cut.WaitForAssertion(
            () => Assert.Equal(
                new PaneOutcome("Agent", Asking: false, Empty: true, Stoppable: false, Turns: string.Empty),
                PaneOutcome.Of(cut)),
            Patience);
    }

    [Fact]
    public async Task Starting_fresh_takes_the_address_back_to_the_sessions_root()
    {
        // Without this the URL still names the conversation the reader just left, so a reload (or the
        // window restoring its last address) lands back on it.
        using var ctx = Context();
        var session = StartSession("write the readme");
        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"/sessions/{session.Id.Value:D}");

        var cut = ctx.Render<SessionsPage>(p => p.Add(c => c.Id, session.Id.Value.ToString("D")));

        // Through the pane's own action, which is the path that stopped navigating.
        await cut.InvokeAsync(() => cut.Find("[data-testid=new-conversation]").Click());
        cut.WaitForAssertion(
            () => Assert.NotEmpty(cut.FindAll("[data-testid=new-conversation-confirm]")), Patience);
        await cut.InvokeAsync(() => cut.Find("[data-testid=new-conversation-confirm-yes]").Click());

        cut.WaitForAssertion(
            () => Assert.EndsWith("/sessions", nav.Uri, StringComparison.Ordinal), Patience);
    }

    [Fact]
    public async Task The_question_buttons_disable_themselves_while_an_answer_is_on_its_way()
    {
        // The busy flag was set and then awaited on with no render, so nothing ever showed it: the whole
        // send happens inside one handler and the renderer only catches up once that handler returns.
        using var ctx = Context();
        var session = StartSession("add caching to the api");
        _runner.Emit(Asked(CachingQuestion));

        // WAIT FOR THE TURN TO REACH THE SESSION BEFORE RENDERING, because this test renders the
        // transcript ON ITS OWN and the transcript is presentational: SessionPane owns the
        // `session.Changed` subscription, so a standalone transcript that renders before the emitted
        // turn has been applied shows nothing and is never re-rendered by anything. That made this a
        // race between a background emit and the render, and the render wins on a slower box: it
        // passed 10 times out of 10 on the Windows dev host and FAILED 10 out of 10 on a 2-CPU Linux
        // container, where it was written off as a flake. It is not a flake and there is nothing
        // wrong with the component; the test was reading a snapshot it had not waited for.
        await Eventually(
            () => session.Transcript.OfType<TurnCompleted>().Any(),
            "the asked turn should reach the session before the transcript renders it");

        var sending = new TaskCompletionSource();
        var cut = ctx.Render<SessionTranscript>(p => p
            .Add(c => c.Session, session)
            .Add(c => c.Answer, (string _) => sending.Task));

        cut.WaitForAssertion(
            () => Assert.Equal(2, cut.FindAll("[data-testid=session-ask-option]").Count), Patience);
        Assert.All(
            cut.FindAll("[data-testid=session-ask-option]"),
            option => Assert.False(option.HasAttribute("disabled")));

        var answering = cut.InvokeAsync(
            () => cut.FindAll("[data-testid=session-ask-option]")[1].ClickAsync(new MouseEventArgs()));
        try
        {
            cut.WaitForAssertion(
                () => Assert.All(
                    cut.FindAll("[data-testid=session-ask-option]"),
                    option => Assert.True(option.HasAttribute("disabled"))),
                Patience);
        }
        finally
        {
            sending.SetResult();
        }

        await answering;

        // And the block goes back to what it was, so a send that fails does not leave a dead question.
        cut.WaitForAssertion(
            () => Assert.All(
                cut.FindAll("[data-testid=session-ask-option]"),
                option => Assert.False(option.HasAttribute("disabled"))),
            Patience);
    }

    // ---- right-click in a conversation (backlog 110) --------------------------------------

    /// <summary>
    /// The studio window removes the browser's context menu (MainWindow.cs, ADR 0014), so a right-click in
    /// a transcript did nothing at all: the selection was real, Ctrl+C worked, and the menu people reach
    /// for was simply absent. The studio has to bring its own, and it is the Code page's menu component
    /// rather than a second one.
    /// </summary>
    private static void RightClick(IRenderedComponent<SessionPane> cut, string testId) =>
        cut.Find($"[data-testid={testId}]").ContextMenu(new MouseEventArgs { ClientX = 40, ClientY = 60 });

    private static IReadOnlyList<string> MenuLabels(IRenderedComponent<SessionPane> cut) =>
        [.. cut.FindAll(".code-context-item").Select(item => item.TextContent.Trim())];

    [Fact]
    public void A_right_click_on_a_message_opens_the_studios_own_menu_with_both_copies()
    {
        using var ctx = Context();
        StartSession("fix the filter");
        var cut = Pane(ctx);

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=bubble-user]")), Patience);
        RightClick(cut, "bubble-user");

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=code-context-menu]")), Patience);
        Assert.Equal<IReadOnlyList<string>>(["Copy", "Copy message"], MenuLabels(cut));
    }

    [Fact]
    public void The_menus_copy_item_is_offered_only_while_something_is_selected()
    {
        using var ctx = Context();
        StartSession("fix the filter");
        var cut = Pane(ctx);

        // Nothing set up for the selection call, so the script answers nothing: no selection.
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=bubble-user]")), Patience);
        RightClick(cut, "bubble-user");

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=code-context-menu]")), Patience);
        var items = cut.FindAll(".code-context-item");
        Assert.True(items[0].HasAttribute("disabled"), "Copy has nothing to copy without a selection");
        Assert.False(items[1].HasAttribute("disabled"), "Copy message always has the message");
    }

    [Fact]
    public void A_selection_enables_copy_and_it_copies_exactly_what_was_selected()
    {
        using var ctx = Context();
        ctx.JSInterop.Setup<string?>(TranscriptClipboard.SelectionFunction).SetResult("the middle of a sentence");
        ctx.JSInterop.Setup<CopyOutcome?>(TranscriptClipboard.CopyFunction, _ => true)
            .SetResult(new CopyOutcome(true, null));
        StartSession("fix the filter");
        var cut = Pane(ctx);

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=bubble-user]")), Patience);
        RightClick(cut, "bubble-user");

        cut.WaitForAssertion(
            () => Assert.False(cut.FindAll(".code-context-item")[0].HasAttribute("disabled")),
            Patience);
        cut.Find(".code-context-item").Click();

        cut.WaitForAssertion(
            () => Assert.Single(ctx.JSInterop.Invocations[TranscriptClipboard.CopyFunction]),
            Patience);
        Assert.Equal(
            "the middle of a sentence",
            ctx.JSInterop.Invocations[TranscriptClipboard.CopyFunction].Single().Arguments[0]);
    }

    [Fact]
    public void Copy_message_copies_the_raw_text_of_the_message_under_the_pointer()
    {
        const string markdown = "Here:\n\n```sh\nmake test\n```";

        using var ctx = Context();
        ctx.JSInterop.Setup<CopyOutcome?>(TranscriptClipboard.CopyFunction, _ => true)
            .SetResult(new CopyOutcome(true, null));
        StartSession("fix the filter");
        var cut = Pane(ctx);
        _runner.Emit(new MessageEmitted(DateTimeOffset.UtcNow, TranscriptRole.Assistant, markdown));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=bubble-assistant]")), Patience);
        RightClick(cut, "bubble-assistant");

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=code-context-menu]")), Patience);
        cut.FindAll(".code-context-item")[1].Click();

        cut.WaitForAssertion(
            () => Assert.Single(ctx.JSInterop.Invocations[TranscriptClipboard.CopyFunction]),
            Patience);

        // The message the pointer was over, in the form the agent wrote it: the fences are part of the
        // message even though the block below them has its own control.
        Assert.Equal(
            markdown, ctx.JSInterop.Invocations[TranscriptClipboard.CopyFunction].Single().Arguments[0]);
    }

    [Fact]
    public void A_right_click_away_from_any_message_still_opens_the_menu_with_copy_message_disabled()
    {
        // The empty space below the last turn, a tool card, a note. The menu does not change shape there,
        // it just cannot offer a message it does not have.
        using var ctx = Context();
        StartSession("fix the filter");
        var cut = Pane(ctx);

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=bubble-user]")), Patience);
        RightClick(cut, "session-transcript");

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=code-context-menu]")), Patience);
        Assert.Equal<IReadOnlyList<string>>(["Copy", "Copy message"], MenuLabels(cut));
        Assert.True(cut.FindAll(".code-context-item")[1].HasAttribute("disabled"));
    }

    [Fact]
    public void A_copy_that_did_not_happen_says_so_where_the_menu_used_to_be()
    {
        using var ctx = Context();
        ctx.JSInterop.Setup<string?>(TranscriptClipboard.SelectionFunction).SetResult("some words");
        ctx.JSInterop.Setup<CopyOutcome?>(TranscriptClipboard.CopyFunction, _ => true)
            .SetResult(new CopyOutcome(false, "the browser refused the copy"));
        StartSession("fix the filter");
        var cut = Pane(ctx);

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=bubble-user]")), Patience);
        RightClick(cut, "bubble-user");

        cut.WaitForAssertion(
            () => Assert.False(cut.FindAll(".code-context-item")[0].HasAttribute("disabled")),
            Patience);
        cut.Find(".code-context-item").Click();

        cut.WaitForAssertion(
            () => Assert.Contains(
                "the browser refused the copy",
                cut.Find("[data-testid=copy-status]").TextContent),
            Patience);
    }

    // ---- the same menu on a STORED conversation ---------------------------------------------

    /// <summary>A conversation with one turn of each role, read back the way a restart reads it.</summary>
    private AgentSessionId StoredExchange(string assistantText)
    {
        var id = StoredConversation("an older conversation");
        _store.Append(id, [
            new MessageEmitted(DateTimeOffset.UtcNow, TranscriptRole.User, "how do I run the tests?"),
            new MessageEmitted(DateTimeOffset.UtcNow, TranscriptRole.Assistant, assistantText),
        ]);
        return id;
    }

    [Fact]
    public void A_right_click_on_a_stored_conversations_message_opens_the_same_menu()
    {
        // The reported defect, on the surface where it survived the first fix: the live transcript owned
        // the menu, the Sessions page's stored view rendered bubbles without it, and a bubble stops the
        // right-click unconditionally. So on a conversation read back after a restart a right-click was
        // swallowed and nothing at all happened.
        using var ctx = Context();
        ctx.JSInterop.Setup<string?>(TranscriptClipboard.SelectionFunction).SetResult("run the tests");
        ctx.JSInterop.Setup<CopyOutcome?>(TranscriptClipboard.CopyFunction, _ => true)
            .SetResult(new CopyOutcome(true, null));
        var id = StoredExchange("Run `dotnet test`.");

        var cut = ctx.Render<SessionsPage>(p => p.Add(c => c.Id, id.Value.ToString("D")));

        cut.Find("[data-testid=stored-transcript] [data-testid=bubble-assistant]")
            .ContextMenu(new MouseEventArgs { ClientX = 40, ClientY = 60 });

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=code-context-menu]")), Patience);
        Assert.Equal<IReadOnlyList<string>>(
            ["Copy", "Copy message"],
            [.. cut.FindAll(".code-context-item").Select(item => item.TextContent.Trim())]);

        cut.FindAll(".code-context-item")[1].Click();

        cut.WaitForAssertion(
            () => Assert.Single(ctx.JSInterop.Invocations[TranscriptClipboard.CopyFunction]),
            Patience);
        Assert.Equal(
            "Run `dotnet test`.",
            ctx.JSInterop.Invocations[TranscriptClipboard.CopyFunction].Single().Arguments[0]);
    }

    [Fact]
    public void A_right_click_in_the_empty_space_of_a_stored_conversation_opens_it_too()
    {
        using var ctx = Context();
        var id = StoredExchange("Run the suite.");

        var cut = ctx.Render<SessionsPage>(p => p.Add(c => c.Id, id.Value.ToString("D")));

        cut.Find("[data-testid=stored-transcript]")
            .ContextMenu(new MouseEventArgs { ClientX = 40, ClientY = 60 });

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=code-context-menu]")), Patience);
        Assert.True(cut.FindAll(".code-context-item")[1].HasAttribute("disabled"),
            "there is no message under the pointer out here");
    }

    [Fact]
    public void A_stored_conversations_code_block_still_carries_its_own_control()
    {
        // The per-block half of the Acceptance on the same surface: the bubble does the split wherever it
        // is rendered, so this holds unless the stored view stops using ChatBubble.
        using var ctx = Context();
        var id = StoredExchange("Here:\n\n```sh\nmake test\n```");

        var cut = ctx.Render<SessionsPage>(p => p.Add(c => c.Id, id.Value.ToString("D")));

        var stored = cut.Find("[data-testid=stored-transcript]");
        Assert.Single(stored.QuerySelectorAll("[data-testid=code-block]"));
        Assert.Equal(2, stored.QuerySelectorAll("[data-testid=copy-message]").Length);
    }
}
