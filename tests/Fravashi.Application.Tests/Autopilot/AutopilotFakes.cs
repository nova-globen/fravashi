using Fravashi.Application.Autopilot;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Autopilot;
using Fravashi.Domain.WorkItems;

namespace Fravashi.Application.Tests.Autopilot;

/// <summary>A clock the test moves by hand, the same shape as the one the mirroring tests use.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// The operator seam (#300), faked: a prompt, an advice and a set of follow-up drafts, each a function the test
/// can override. The real implementation's extra context (which project, which runtime and model) is accepted
/// and ignored here, so every existing test that sets <see cref="Prompt"/> or <see cref="Advice"/> by
/// <see cref="WorkItemId"/> alone keeps compiling unchanged.
/// </summary>
internal sealed class FakeOperator : IAutopilotOperator
{
    public Func<WorkItemId, AutopilotPrompt> Prompt { get; set; } = _ => new AutopilotPrompt("(prompt)", 0m);

    public Func<WorkItemId, string, AutopilotAdvice> Advice { get; set; } =
        (_, failure) => new AutopilotAdvice(RetryBuild: false, failure);

    public Func<WorkItemId, string, AutopilotFollowUpDrafts> FollowUps { get; set; } =
        (_, _) => new AutopilotFollowUpDrafts([], 0m);

    public int AdviceCalls { get; private set; }

    public Task<AutopilotPrompt> WritePromptAsync(AutopilotOperatorContext context, WorkItemId item, CancellationToken ct = default) =>
        Task.FromResult(Prompt(item));

    public Task<AutopilotAdvice> AdviseOnFailureAsync(
        AutopilotOperatorContext context, WorkItemId item, string failure, CancellationToken ct = default)
    {
        AdviceCalls++;
        return Task.FromResult(Advice(item, failure));
    }

    public Task<AutopilotFollowUpDrafts> DraftFollowUpsAsync(
        AutopilotOperatorContext context, WorkItemId item, string findings, CancellationToken ct = default) =>
        Task.FromResult(FollowUps(item, findings));
}

/// <summary>
/// The building agent seam, faked. Defaults to a clean commit with no cost. The real implementation's extra
/// context (which project, which runtime and model) is accepted and ignored here, so every existing test that
/// sets <see cref="Result"/> by <see cref="WorkItemId"/> alone keeps compiling unchanged.
/// </summary>
internal sealed class FakeBuilder : IAutopilotBuilder
{
    public Func<WorkItemId, string, AutopilotBuildResult> Result { get; set; } =
        (_, _) => new AutopilotBuildResult(CommitMade: true, BranchHeadSha: "sha-1", CostUsd: 0m, NeedsPerson: false, Problem: null);

    /// <summary>
    /// Awaited before <see cref="Result"/> is read, so a test can hold a call open (a <see cref="TaskCompletionSource"/>
    /// it completes itself) to drive a race against a concurrent <c>StopNowAsync</c> or <c>StopAfterCurrentItemAsync</c>
    /// call on the same run. Null (the default) returns at once.
    /// </summary>
    public Func<Task>? Gate { get; set; }

    public List<WorkItemId> Stopped { get; } = [];

    /// <summary>
    /// When set, <see cref="StopAsync"/> throws for any item this answers true for, standing in for a stuck
    /// process StopAsync could not actually end (#310, commit review's third pass).
    /// </summary>
    public Func<WorkItemId, bool>? ThrowOnStop { get; set; }

    public async Task<AutopilotBuildResult> BuildAsync(
        AutopilotBuilderContext context, WorkItemId item, string prompt, CancellationToken ct = default)
    {
        if (Gate is { } gate)
        {
            await gate().ConfigureAwait(false);
        }

        return Result(item, prompt);
    }

    public Task StopAsync(WorkItemId item, CancellationToken ct = default)
    {
        if (ThrowOnStop?.Invoke(item) == true)
        {
            throw new InvalidOperationException($"could not stop {item.Value}");
        }

        Stopped.Add(item);
        return Task.CompletedTask;
    }

    public List<WorkItemId> Ended { get; } = [];

    /// <summary>Answered by EndItemAsync for the named item: null (the default) or a refusal's own words.</summary>
    public Func<WorkItemId, string?> EndItemResult { get; set; } = _ => null;

    public Task<string?> EndItemAsync(AutopilotBuilderContext context, WorkItemId item, CancellationToken ct = default)
    {
        Ended.Add(item);
        return Task.FromResult(EndItemResult(item));
    }
}

/// <summary>The commit review outcome seam, faked with the studio's own <see cref="CommitReview"/>.</summary>
internal sealed class FakeCommitReviews : IAutopilotCommitReviews
{
    private static readonly CommitReviewerRole Role = new(true, AgentTool.Claude, "claude-sonnet-5");

    public Func<WorkItemId, CommitReview> Result { get; set; } = _ => new CommitReview(CommitReviewOutcome.Passed, Role);

    public Task<CommitReview> ReviewAsync(WorkItemId item, CancellationToken ct = default) =>
        Task.FromResult(Result(item));
}

/// <summary>
/// The delivery seam (#301), faked. The real implementation's extra context (which project, which delivery
/// mode) is accepted and ignored here, so every existing test that sets <see cref="Deliver"/> or
/// <see cref="Verify"/> by <see cref="WorkItemId"/> alone keeps compiling unchanged.
/// </summary>
internal sealed class FakeDelivery : IAutopilotDelivery
{
    public Func<WorkItemId, string, AutopilotDeliveryResult> Deliver { get; set; } =
        (_, _) => new AutopilotDeliveryResult(Delivered: true, PullRequestUrl: "https://example.invalid/pr/1", Problem: null);

    public Func<WorkItemId, string, AutopilotVerifyResult> Verify { get; set; } =
        (_, _) => new AutopilotVerifyResult(Passed: true, NeedsPerson: false, Problem: null);

    /// <summary>Awaited before <see cref="Verify"/> is read, the same shape as <see cref="FakeBuilder.Gate"/>.</summary>
    public Func<Task>? VerifyGate { get; set; }

    public Task<AutopilotDeliveryResult> DeliverAsync(
        AutopilotDeliveryContext context, WorkItemId item, string branchHeadSha, CancellationToken ct = default) =>
        Task.FromResult(Deliver(item, branchHeadSha));

    public async Task<AutopilotVerifyResult> VerifyAsync(
        AutopilotDeliveryContext context, WorkItemId item, string branchHeadSha, CancellationToken ct = default)
    {
        if (VerifyGate is { } gate)
        {
            await gate().ConfigureAwait(false);
        }

        return Verify(item, branchHeadSha);
    }
}

/// <summary>
/// The workspace-return seam (#310), faked: records every call and answers a fixed outcome, clean by default
/// (returns successfully, no note).
/// </summary>
internal sealed class FakeWorkspaceReturn : IAutopilotWorkspaceReturn
{
    public Func<string, string, AutopilotWorkspaceReturnResult> Result { get; set; } =
        (_, _) => new AutopilotWorkspaceReturnResult(Returned: true, Note: null);

    public List<(string WorkspaceId, string BaseBranch)> Calls { get; } = [];

    public Task<AutopilotWorkspaceReturnResult> ReturnAsync(string workspaceId, string baseBranch, CancellationToken ct = default)
    {
        Calls.Add((workspaceId, baseBranch));
        return Task.FromResult(Result(workspaceId, baseBranch));
    }
}

/// <summary>An in-memory run store, standing in for the real SQLite one (tested separately, Infrastructure side).</summary>
internal sealed class FakeRunStore : IAutopilotRunStore
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

    public Task<IReadOnlyList<AutopilotRun>> ForProjectAsync(Fravashi.Domain.Projects.ProjectId project, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AutopilotRun>>(
            [.. _runs.Values.Where(r => r.Project == project).OrderByDescending(r => r.StartedUtc)]);

    public Task<IReadOnlyList<AutopilotRun>> LatestPerProjectAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AutopilotRun>>(
            [.. _runs.Values.GroupBy(r => r.Project).Select(g => g.OrderByDescending(r => r.StartedUtc).First())]);
}
