using Bunit;
using Fravashi.Application.Autopilot;
using Fravashi.Application.Notifications;
using Fravashi.Application.Persistence;
using Fravashi.Application.Projects;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Autopilot;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Autopilot;
using Fravashi.Infrastructure.Bridge;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.WorkItems;
using Fravashi.Web.Components.Dashboard;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Work item #356: a parked autopilot entry in "Waiting for you" opens an explanation in place, in plain words, with the
/// actions that can really be taken: dispatch again (an ended run), open the item, open the Git page (a push is
/// missing), open the retired autopilot page, and dismiss the notice. The owner had been sent to the retired autopilot page's dispatch form with
/// no word on what to do there.
/// </summary>
public sealed class WaitingForYouParkedTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private const string PushReason = "Commits are on 'main' locally. Push it to run CI and the scenario.";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fravashi-waiting-parked-tests", Guid.NewGuid().ToString("N"));
    private readonly SqliteConnectionFactory _factory;
    private readonly LocalTrackerResolver _trackers;
    private readonly SqliteAutopilotRunStore _runs;
    private readonly SqliteProjectDelivery _delivery;

    public WaitingForYouParkedTests()
    {
        Directory.CreateDirectory(_directory);
        _factory = new SqliteConnectionFactory(new SqliteStoreOptions(Path.Combine(_directory, "test.db"), null, Pooling: false));
        SqliteSchema.EnsureCreated(_factory);
        _trackers = new LocalTrackerResolver(_factory, TimeProvider.System);
        _runs = new SqliteAutopilotRunStore(_factory);
        _delivery = new SqliteProjectDelivery(_factory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeList(List<WaitingForPersonEntry> entries, FakeDismissals dismissals) : IWaitingForPersonList
    {
        public Task<IReadOnlyList<WaitingForPersonEntry>> CurrentAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WaitingForPersonEntry>>(
                [.. entries.Where(e => e.Run is not { } run || e.Item is not { } item || !dismissals.IsDismissed(run, item))]);
    }

    private sealed class FakeDismissals : IParkedNoticeDismissals
    {
        public HashSet<(Guid, string)> Dismissed { get; } = [];

        public bool IsDismissed(Guid run, WorkItemId item) => Dismissed.Contains((run, item.Value));

        public void Dismiss(Guid run, WorkItemId item) => Dismissed.Add((run, item.Value));
    }

    private sealed class FakeWorkspaceStore : IWorkspaceStore
    {
        public IReadOnlyList<WorkspaceRecord> Recent(int max = 10) => [];

        public WorkspaceRecord? Get(string id) => null;

        public void Touch(string id, string name, string rootPath)
        {
        }

        public void Remove(string id)
        {
        }
    }

    private sealed class FixedReadiness : IAutopilotDeliveryReadiness
    {
        public Task<AutopilotDeliveryReadiness> CheckAsync(CancellationToken ct = default) =>
            Task.FromResult(new AutopilotDeliveryReadiness(true, "github.com"));
    }

    private sealed class CleanWorkspace : IAutopilotWorkspaceStatus
    {
        public Task<AutopilotWorkspaceState> CheckAsync(ProjectId project, CancellationToken ct = default) =>
            Task.FromResult(new AutopilotWorkspaceState("main", 0, WorkspaceId: "w1"));
    }

    private sealed class FixedCap : IAutopilotConcurrencySettings
    {
        public int Cap() => 1;
    }

    private (BunitContext Ctx, FakeDismissals Dismissals) Context(params WaitingForPersonEntry[] entries)
    {
        var ctx = new BunitContext();
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [BridgeWorkspaceContext.UrlKey] = "http://127.0.0.1:9",
            [BridgeWorkspaceContext.TokenKey] = "t",
            [BridgeWorkspaceContext.ActiveIdKey] = "w1",
        }).Build();
        var bridgeContext = new BridgeWorkspaceContext(configuration);
        var dismissals = new FakeDismissals();
        ctx.Services.AddSingleton(configuration);
        ctx.Services.AddSingleton(bridgeContext);
        ctx.Services.AddSingleton(new BridgeClient(new HttpClient(), bridgeContext));
        ctx.Services.AddSingleton<IWorkspaceStore>(new FakeWorkspaceStore());
        ctx.Services.AddSingleton<IWaitingForPersonList>(new FakeList([.. entries], dismissals));
        ctx.Services.AddSingleton<IParkedNoticeDismissals>(dismissals);
        ctx.Services.AddSingleton<IAutopilotRunStore>(_runs);
        ctx.Services.AddSingleton(new AutopilotDispatcher(
            _trackers, _delivery, new FixedReadiness(), new CleanWorkspace(), _runs, TimeProvider.System, new FixedCap()));
        ctx.Services.AddLogging();
        return (ctx, dismissals);
    }

    private static WaitingForPersonEntry Parked(Guid run, WorkItemId item, int number, string reason, bool ended) =>
        new(WaitingForPersonKind.Parked,
            $"#{number} Add a usage page is parked: {reason}" + (ended ? " This run has ended; dispatch the project again to resume it." : string.Empty),
            DateTimeOffset.UtcNow, "w1", Item: item, Project: ProjectId.Default, ItemNumber: number,
            Run: run, Reason: reason, RunEnded: ended);

    [Fact]
    public async Task Clicking_a_parked_entry_explains_it_in_place_with_its_actions_instead_of_navigating()
    {
        var (ctx, _) = Context(Parked(Guid.NewGuid(), WorkItemId.New(), 23, PushReason, ended: true));
        using var _ctx = ctx;
        var cut = ctx.Render<WaitingForYouPanel>();
        var before = ctx.Services.GetRequiredService<NavigationManager>().Uri;

        await cut.InvokeAsync(() => cut.Find("[data-testid=waiting-for-you-open]").Click());

        cut.WaitForAssertion(() =>
        {
            var explain = cut.Find("[data-testid=waiting-for-you-explain]").TextContent;
            Assert.Contains("set #23 aside because it needs a person", explain);
            Assert.Contains("Push them", explain);
            Assert.Contains("has ended, so nothing is working on it now", explain);
            Assert.NotNull(cut.Find("[data-testid=waiting-for-you-dispatch]"));
            Assert.NotNull(cut.Find("[data-testid=waiting-for-you-open-item]"));
            Assert.NotNull(cut.Find("[data-testid=waiting-for-you-open-git]"));
            Assert.NotNull(cut.Find("[data-testid=waiting-for-you-dismiss]"));
        }, Patience);
        Assert.Equal(before, ctx.Services.GetRequiredService<NavigationManager>().Uri);

        await cut.InvokeAsync(() => cut.Find("[data-testid=waiting-for-you-open-item]").Click());
        Assert.EndsWith("/workitems/23", ctx.Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Fact]
    public async Task A_run_still_going_offers_no_dispatch_and_the_autopilot_card_opens()
    {
        var (ctx, _) = Context(Parked(Guid.NewGuid(), WorkItemId.New(), 7, "The building session is waiting on a person.", ended: false));
        using var _ctx = ctx;
        var cut = ctx.Render<WaitingForYouPanel>();

        await cut.InvokeAsync(() => cut.Find("[data-testid=waiting-for-you-open]").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("[data-testid=waiting-for-you-dispatch]"));
            Assert.Empty(cut.FindAll("[data-testid=waiting-for-you-open-git]"));
            Assert.Contains("still going", cut.Find("[data-testid=waiting-for-you-explain]").TextContent);
        }, Patience);

        await cut.InvokeAsync(() => cut.Find("[data-testid=waiting-for-you-open-autopilot]").Click());
        Assert.EndsWith("/#autopilot", ctx.Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Fact]
    public async Task Dismiss_records_the_dismissal_and_the_entry_leaves_the_list()
    {
        var run = Guid.NewGuid();
        var item = WorkItemId.New();
        var (ctx, dismissals) = Context(Parked(run, item, 23, PushReason, ended: true));
        using var _ctx = ctx;
        var cut = ctx.Render<WaitingForYouPanel>();

        await cut.InvokeAsync(() => cut.Find("[data-testid=waiting-for-you-open]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=waiting-for-you-dismiss]").Click());

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=waiting-for-you-row]")), Patience);
        Assert.Contains((run, item.Value), dismissals.Dismissed);
    }

    [Fact]
    public async Task Dispatch_again_starts_a_new_run_with_the_last_runs_settings_and_says_so()
    {
        var tracker = _trackers.For(ProjectId.Default);
        var created = (await tracker.CreateAsync(
            new NewWorkItem { Level = WorkItemLevel.Task, Title = "Add a usage page", Description = "x", Status = WorkItemStatus.Ready },
            MutationContext.User)).Value;
        var marked = (await tracker.UpdateAsync(
            created.Id, new WorkItemPatch { MarkedForAutopilot = Optional<bool>.Of(true) }, created.Version, MutationContext.User)).Value;
        var last = AutopilotRun.Start(Guid.NewGuid(), ProjectId.Default, AutopilotDispatchSettings.Default, [marked.Id], DateTimeOffset.UtcNow.AddHours(-1));
        last = last.With(last.Queue[0] with
        {
            Stage = AutopilotItemStage.Parked,
            StartedUtc = DateTimeOffset.UtcNow.AddHours(-1),
            ParkedReason = PushReason,
        }) with { State = AutopilotRunState.Stopped };
        await _runs.SaveAsync(last);

        var (ctx, _) = Context(Parked(last.Id, marked.Id, marked.Number, PushReason, ended: true));
        using var _ctx = ctx;
        var cut = ctx.Render<WaitingForYouPanel>();

        await cut.InvokeAsync(() => cut.Find("[data-testid=waiting-for-you-open]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=waiting-for-you-dispatch]").Click());

        cut.WaitForAssertion(
            () => Assert.StartsWith("Dispatched: a new run started with 1 item", cut.Find("[data-testid=waiting-for-you-said]").TextContent.Trim()),
            Patience);
        var open = await _runs.OpenRunsAsync();
        Assert.Contains(open, r => r.Project == ProjectId.Default && r.Id != last.Id);
    }

    [Fact]
    public async Task A_refused_dispatch_says_why_in_words()
    {
        // No such run in the store: its settings cannot be reused, and the panel says so rather than guessing.
        var (ctx, _) = Context(Parked(Guid.NewGuid(), WorkItemId.New(), 23, PushReason, ended: true));
        using var _ctx = ctx;
        var cut = ctx.Render<WaitingForYouPanel>();

        await cut.InvokeAsync(() => cut.Find("[data-testid=waiting-for-you-open]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=waiting-for-you-dispatch]").Click());

        cut.WaitForAssertion(
            () => Assert.Contains("could not be found", cut.Find("[data-testid=waiting-for-you-said]").TextContent),
            Patience);
    }
}
