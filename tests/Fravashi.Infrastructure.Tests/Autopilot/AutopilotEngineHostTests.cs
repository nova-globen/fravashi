using AwesomeAssertions;
using Fravashi.Application.Autopilot;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Autopilot;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Autopilot;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Autopilot;

/// <summary>
/// The host #309 calls for: it resumes every open run once at start, and ticks every open run one at a time,
/// through <see cref="AutopilotTickGate"/>, so two ticks of one run never overlap and a crashed tick never
/// stops the host.
/// </summary>
public sealed class AutopilotEngineHostTests
{
    [Fact]
    public async Task Two_ticks_of_the_same_run_never_overlap()
    {
        var store = new SlowGetStore();
        var engine = NewEngine(store);
        var host = new AutopilotEngineHost(engine, store, new AutopilotTickGate(), interval: TimeSpan.FromMinutes(10));
        var runId = Guid.NewGuid();

        var first = host.TickOneRunAsync(runId, CancellationToken.None);
        await WaitUntilAsync(() => store.GetCalls >= 1);

        await host.TickOneRunAsync(runId, CancellationToken.None); // the gate is still held by the first tick

        store.GetCalls.Should().Be(1, "the second call must skip this cycle rather than tick alongside the first");

        store.Release.SetResult(null);
        await first;
    }

    [Fact]
    public async Task A_tick_that_throws_is_swallowed_and_releases_the_gate_for_the_next_cycle()
    {
        var store = new ThrowingGetStore();
        var engine = NewEngine(store);
        var host = new AutopilotEngineHost(engine, store, new AutopilotTickGate(), interval: TimeSpan.FromMinutes(10));
        var runId = Guid.NewGuid();

        await host.TickOneRunAsync(runId, CancellationToken.None);
        await host.TickOneRunAsync(runId, CancellationToken.None);

        store.GetCalls.Should().Be(2, "the gate must be released after the first failing tick, or the second never runs");
    }

    [Fact]
    public async Task Resume_at_start_stops_a_run_mid_building_and_rebases_a_queued_runs_clock()
    {
        var store = new FakeRunStore();
        var item1 = new WorkItemId("item-1");
        var started = DateTimeOffset.UtcNow.AddHours(-1);
        var midBuild = AutopilotRun.Start(Guid.NewGuid(), ProjectId.Default, AutopilotDispatchSettings.Default, [item1], started);
        midBuild = midBuild.With(midBuild.Queue[0] with { Stage = AutopilotItemStage.Building, StartedUtc = started });
        await store.SaveAsync(midBuild);

        var item2 = new WorkItemId("item-2");
        var queued = AutopilotRun.Start(Guid.NewGuid(), ProjectId.Default, AutopilotDispatchSettings.Default, [item2], started);
        await store.SaveAsync(queued);

        var engine = NewFunctionalEngine(store);
        using var host = new AutopilotEngineHost(engine, store, new AutopilotTickGate(), interval: TimeSpan.FromMinutes(10));

        await host.StartAsync(CancellationToken.None);
        await WaitUntilAsync(async () => (await store.GetAsync(midBuild.Id))!.State == AutopilotRunState.Stopped);
        await host.StopAsync(CancellationToken.None);

        var stoppedRun = await store.GetAsync(midBuild.Id);
        stoppedRun!.Stops.Should().ContainSingle(s => s.Kind == AutopilotStopKind.CannotResume);

        var rebasedRun = await store.GetAsync(queued.Id);
        rebasedRun!.State.Should().Be(AutopilotRunState.Running);
        rebasedRun.LastProgressUtc.Should().BeAfter(started, "the outage must not count against the resumed run's no-progress window");
    }

    [Fact]
    public async Task A_dispatched_run_advances_by_itself_through_prompt_build_and_review()
    {
        var store = new FakeRunStore();
        var run = AutopilotRun.Start(
            Guid.NewGuid(), ProjectId.Default, AutopilotDispatchSettings.Default, [new WorkItemId("item-1")], DateTimeOffset.UtcNow);
        await store.SaveAsync(run);

        var engine = NewFunctionalEngine(store);
        using var host = new AutopilotEngineHost(engine, store, new AutopilotTickGate(), interval: TimeSpan.FromMilliseconds(25));

        await host.StartAsync(CancellationToken.None);
        await WaitUntilAsync(async () =>
            (await store.GetAsync(run.Id))!.Queue.Single().Stage is AutopilotItemStage.Delivering or AutopilotItemStage.Parked);
        await host.StopAsync(CancellationToken.None);

        var advanced = await store.GetAsync(run.Id);
        advanced!.Queue.Single().Stage.Should().BeOneOf(AutopilotItemStage.Delivering, AutopilotItemStage.Parked);
        advanced.Queue.Single().BranchHeadSha.Should().Be(
            "sha-1", "it must have passed through Prompting, Building and a passed Reviewing to get this far");
    }

    private static AutopilotEngine NewEngine(IAutopilotRunStore store) =>
        new(StubOperator.Instance, StubBuilder.Instance, StubReviews.Instance, StubDelivery.Instance, store);

    /// <summary>An engine over fakes that actually answer, standing in for the real operator, builder and
    /// commit reviews (#309); delivery is the honest "not built in this test" stub, since this test is about
    /// the host's tick plumbing and not delivery's own correctness (see <see cref="GapDelivery"/>).</summary>
    private static AutopilotEngine NewFunctionalEngine(IAutopilotRunStore store) =>
        new(new FakeOperator(), new FakeBuilder(), new FakeCommitReviews(), new GapDelivery(), store);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !condition())
        {
            await Task.Delay(10);
        }

        condition().Should().BeTrue();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !await condition())
        {
            await Task.Delay(10);
        }

        (await condition()).Should().BeTrue();
    }

    // ---- run stores, each proving one thing ----------------------------------------------------------------

    /// <summary>Never returns from <c>GetAsync</c> until the test releases it, simulating a slow tick.</summary>
    private sealed class SlowGetStore : IAutopilotRunStore
    {
        public readonly TaskCompletionSource<AutopilotRun?> Release = new();
        private int _getCalls;

        public int GetCalls => _getCalls;

        public Task<AutopilotRun> SaveAsync(AutopilotRun run, CancellationToken ct = default) => Task.FromResult(run);

        public Task<AutopilotRun?> GetAsync(Guid id, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _getCalls);
            return Release.Task;
        }

        public Task<IReadOnlyList<AutopilotRun>> OpenRunsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AutopilotRun>>([]);

        public Task<IReadOnlyList<AutopilotRun>> ForProjectAsync(ProjectId project, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AutopilotRun>>([]);

        public Task<IReadOnlyList<AutopilotRun>> LatestPerProjectAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AutopilotRun>>([]);
    }

    /// <summary>Every <c>GetAsync</c> throws, simulating a crashed tick.</summary>
    private sealed class ThrowingGetStore : IAutopilotRunStore
    {
        private int _getCalls;

        public int GetCalls => _getCalls;

        public Task<AutopilotRun> SaveAsync(AutopilotRun run, CancellationToken ct = default) => Task.FromResult(run);

        public Task<AutopilotRun?> GetAsync(Guid id, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _getCalls);
            throw new InvalidOperationException("Simulated crash reading the run.");
        }

        public Task<IReadOnlyList<AutopilotRun>> OpenRunsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AutopilotRun>>([]);

        public Task<IReadOnlyList<AutopilotRun>> ForProjectAsync(ProjectId project, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AutopilotRun>>([]);

        public Task<IReadOnlyList<AutopilotRun>> LatestPerProjectAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AutopilotRun>>([]);
    }

    /// <summary>A real in-memory run store, for the tests that need the host to actually drive a run.</summary>
    private sealed class FakeRunStore : IAutopilotRunStore
    {
        private readonly Dictionary<Guid, AutopilotRun> _runs = [];

        public Task<AutopilotRun> SaveAsync(AutopilotRun run, CancellationToken ct = default)
        {
            _runs[run.Id] = run;
            return Task.FromResult(run);
        }

        public Task<AutopilotRun?> GetAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(_runs.TryGetValue(id, out var run) ? run : null);

        public Task<IReadOnlyList<AutopilotRun>> OpenRunsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AutopilotRun>>([.. _runs.Values.Where(r => r.State == AutopilotRunState.Running)]);

        public Task<IReadOnlyList<AutopilotRun>> ForProjectAsync(ProjectId project, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AutopilotRun>>(
                [.. _runs.Values.Where(r => r.Project == project).OrderByDescending(r => r.StartedUtc)]);

        public Task<IReadOnlyList<AutopilotRun>> LatestPerProjectAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AutopilotRun>>(
                [.. _runs.Values.GroupBy(r => r.Project).Select(g => g.OrderByDescending(r => r.StartedUtc).First())]);
    }

    // ---- seam stubs: never invoked in these tests, since no store here ever hands TickAsync a real run -----

    private sealed class StubOperator : IAutopilotOperator
    {
        public static readonly StubOperator Instance = new();

        public Task<AutopilotPrompt> WritePromptAsync(AutopilotOperatorContext context, WorkItemId item, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AutopilotAdvice> AdviseOnFailureAsync(AutopilotOperatorContext context, WorkItemId item, string failure, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AutopilotFollowUpDrafts> DraftFollowUpsAsync(AutopilotOperatorContext context, WorkItemId item, string findings, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubBuilder : IAutopilotBuilder
    {
        public static readonly StubBuilder Instance = new();

        public Task<AutopilotBuildResult> BuildAsync(AutopilotBuilderContext context, WorkItemId item, string prompt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task StopAsync(WorkItemId item, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<string?> EndItemAsync(AutopilotBuilderContext context, WorkItemId item, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class StubReviews : IAutopilotCommitReviews
    {
        public static readonly StubReviews Instance = new();

        public Task<Application.Sessions.CommitReview> ReviewAsync(WorkItemId item, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubDelivery : IAutopilotDelivery
    {
        public static readonly StubDelivery Instance = new();

        public Task<AutopilotDeliveryResult> DeliverAsync(
            AutopilotDeliveryContext context, WorkItemId item, string branchHeadSha, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AutopilotVerifyResult> VerifyAsync(
            AutopilotDeliveryContext context, WorkItemId item, string branchHeadSha, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// An honest "not built in this test" delivery, standing in for the real <c>AgentAutopilotDelivery</c>
    /// (#301) the way the retired <c>PlaceholderAutopilotDelivery</c> once did: it never answers with a silent
    /// success, so an item that reaches Delivering or Verifying here parks for a person with a named reason.
    /// This test is about the HOST's own tick plumbing, not delivery's correctness, so the stub only needs to
    /// be honest, never real.
    /// </summary>
    private sealed class GapDelivery : IAutopilotDelivery
    {
        private const string NotBuiltHere = "Delivery is not exercised by this test.";

        public Task<AutopilotDeliveryResult> DeliverAsync(
            AutopilotDeliveryContext context, WorkItemId item, string branchHeadSha, CancellationToken ct = default) =>
            Task.FromResult(new AutopilotDeliveryResult(Delivered: false, PullRequestUrl: null, Problem: NotBuiltHere));

        public Task<AutopilotVerifyResult> VerifyAsync(
            AutopilotDeliveryContext context, WorkItemId item, string branchHeadSha, CancellationToken ct = default) =>
            Task.FromResult(new AutopilotVerifyResult(Passed: false, NeedsPerson: true, Problem: NotBuiltHere));
    }

    // ---- functional fakes: answer like the real seams would, for the end-to-end progression test -----------

    private sealed class FakeOperator : IAutopilotOperator
    {
        public Task<AutopilotPrompt> WritePromptAsync(AutopilotOperatorContext context, WorkItemId item, CancellationToken ct = default) =>
            Task.FromResult(new AutopilotPrompt("build the item", 0m));

        public Task<AutopilotAdvice> AdviseOnFailureAsync(AutopilotOperatorContext context, WorkItemId item, string failure, CancellationToken ct = default) =>
            Task.FromResult(new AutopilotAdvice(RetryBuild: false, failure));

        public Task<AutopilotFollowUpDrafts> DraftFollowUpsAsync(AutopilotOperatorContext context, WorkItemId item, string findings, CancellationToken ct = default) =>
            Task.FromResult(new AutopilotFollowUpDrafts([], 0m));
    }

    private sealed class FakeBuilder : IAutopilotBuilder
    {
        public Task<AutopilotBuildResult> BuildAsync(AutopilotBuilderContext context, WorkItemId item, string prompt, CancellationToken ct = default) =>
            Task.FromResult(new AutopilotBuildResult(CommitMade: true, BranchHeadSha: "sha-1", CostUsd: 0m, NeedsPerson: false, Problem: null));

        public Task StopAsync(WorkItemId item, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> EndItemAsync(AutopilotBuilderContext context, WorkItemId item, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private sealed class FakeCommitReviews : IAutopilotCommitReviews
    {
        private static readonly CommitReviewerRole Role = new(true, AgentTool.Claude, "claude-sonnet-5");

        public Task<CommitReview> ReviewAsync(WorkItemId item, CancellationToken ct = default) =>
            Task.FromResult(new CommitReview(CommitReviewOutcome.Passed, Role));
    }
}
