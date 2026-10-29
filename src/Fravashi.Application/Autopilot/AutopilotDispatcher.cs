using Fravashi.Domain.Autopilot;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain;

namespace Fravashi.Application.Autopilot;

/// <summary>What a dispatch attempt produced: the saved run, or a refusal naming what is missing.</summary>
/// <param name="Run">The saved run, when dispatched.</param>
/// <param name="Refusal">The reason, when not.</param>
public sealed record AutopilotDispatchOutcome(AutopilotRun? Run, string? Refusal)
{
    /// <summary>True when a run was saved.</summary>
    public bool Dispatched => Run is not null;
}

/// <summary>
/// Builds and saves an <see cref="AutopilotRun"/> from a project's currently marked Ready items (ADR 0031
/// decisions 1 to 3, #298). Reads every item itself (paged, the same way <c>WorkItemsPage.LoadAsync</c>
/// does) so a caller only has to name the project and the dispatch's settings.
/// </summary>
public sealed class AutopilotDispatcher(
    Fravashi.Application.WorkItems.ITrackerResolver trackers,
    Fravashi.Application.Projects.IProjectDelivery delivery,
    IAutopilotDeliveryReadiness deliveryReadiness,
    IAutopilotWorkspaceStatus workspaceStatus,
    IAutopilotRunStore runs,
    TimeProvider clock,
    IAutopilotConcurrencySettings? concurrencyCapSettings = null)
{
    private readonly IAutopilotConcurrencySettings _concurrencyCapSettings =
        concurrencyCapSettings ?? IAutopilotConcurrencySettings.Default;


    /// <summary>
    /// Selects, validates and, when valid, saves a fresh run. Readiness is checked only for branch + pull
    /// request delivery: commit-to-main never needs a token, so it is never asked. The workspace's own dirty
    /// count is always checked (#310): an attempt branch made from a dirty tree would carry a person's own
    /// edits into the run, whatever the delivery mode.
    /// </summary>
    public async Task<AutopilotDispatchOutcome> DispatchAsync(
        ProjectId project, AutopilotDispatchSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // ADR 0031 decision 1 is "one at a time": a marked item still sitting in an open run's queue has
        // not necessarily left Ready yet (Queued and Prompting leave the item's own status untouched), so
        // AutopilotSelection alone cannot stop a second dispatch from picking it up again. Refusing while
        // this project already has a run open is what actually stops that, rather than a comment claiming
        // a queued item is already excluded when it is not. Nothing in this slice ticks a run or stops one
        // (that is #301's hosted service and #59's autopilot page), so the refusal names only what is true here:
        // the project cannot dispatch again until that arrives.
        var open = await runs.OpenRunsAsync(ct).ConfigureAwait(false);
        if (open.Any(r => r.Project == project))
        {
            return new AutopilotDispatchOutcome(
                null,
                "A run is already open for this project. This build has no way to tick, stop or finish a "
                + "run yet, so no further dispatch is possible for this project until that arrives.");
        }

        var workspace = await workspaceStatus.CheckAsync(project, ct).ConfigureAwait(false);
        if (!workspace.Reachable)
        {
            return new AutopilotDispatchOutcome(
                null, $"The workspace's git cannot be reached; open the folder with {ProductName.Short} and try again.");
        }

        // git rev-parse --abbrev-ref HEAD answers the literal "HEAD" on a detached checkout (GitCliService's
        // own GetCurrentBranchAsync): recording that as BaseBranch would make the eventual return a no-op
        // checkout onto "HEAD" that reports success while the folder stays on the attempt branch (commit
        // review #310), so this refuses rather than record a branch nobody is really on.
        if (string.Equals(workspace.Branch, "HEAD", StringComparison.Ordinal))
        {
            return new AutopilotDispatchOutcome(
                null, "The workspace is not on a branch; check one out before dispatching.");
        }

        // A reachable workspace with an empty Branch (commit review #310) has nothing a base branch could
        // name: proceeding would dispatch with BaseBranch null, and the run would then never attempt a return
        // at all, silently, rather than refusing here where the reason is still known.
        if (workspace.Branch.Length == 0)
        {
            return new AutopilotDispatchOutcome(null, "The workspace's branch could not be read; try again.");
        }

        // The same silent-skip the branch check above exists to prevent (commit review #310, fifth pass): a
        // reachable workspace with no resolved id would dispatch with BaseWorkspaceId null, and the run would
        // then never attempt a return at all, with no note anywhere saying so.
        if (workspace.WorkspaceId is not { Length: > 0 })
        {
            return new AutopilotDispatchOutcome(null, "The workspace's own id could not be resolved; try again.");
        }

        var items = await AllItemsAsync(trackers.For(project), ct).ConfigureAwait(false);
        var marked = AutopilotSelection.MarkedReadyInBoardOrder(items);

        // ADR 0031 decision 12 (#305): a parked item can be resumed in a later run. The project's most
        // recent run is never Running here (the open-run guard above already refused otherwise), so its
        // queue is settled history, and every item it left Parked that is still marked and still In Progress
        // is still autopilot's own to finish, not a fresh Ready pick.
        var history = await runs.ForProjectAsync(project, ct).ConfigureAwait(false);
        var resumed = AutopilotSelection.ParkedInBoardOrder(items, history.FirstOrDefault()?.Queue);
        var selected = marked
            .Concat(resumed)
            .OrderBy(i => i.Rank)
            .ThenBy(i => i.Number)
            .ToList();

        var mode = delivery.For(project);
        var readiness = mode == AutopilotDeliveryMode.BranchAndPullRequest
            ? await deliveryReadiness.CheckAsync(ct).ConfigureAwait(false)
            : new AutopilotDeliveryReadiness(HasToken: true, Host: null);

        var settingsCap = _concurrencyCapSettings.Cap();
        var refusal = AutopilotDispatchValidation.Validate(
            selected, mode, readiness.HasToken, readiness.Host, workspace.DirtyCount,
            settings.ConcurrencyCapOverride, settingsCap);
        if (refusal is not null)
        {
            return new AutopilotDispatchOutcome(null, refusal);
        }

        // Resolved ONCE, here, and never re-read from live Settings for the life of this run (#308): whether
        // every item shares one workspace or gets its own worktree is a mode decision a later Settings change
        // must never flip out from under a run already under way.
        //
        // #311: a parallel item's worktree is always made from the base branch the person's folder had
        // checked out at dispatch (decision 15: that folder is never touched), and AgentAutopilotDelivery
        // .DeliverToMainAsync refuses to move that branch's ref while it is checked out there (#308's own
        // guard, correct on its own). So a parallel commit-to-main run can never deliver; every item parks.
        // Host decision, 2026-10-13 (ADR 0031 decision 16's own dated addendum): commit to main runs one
        // item at a time, whatever the Settings cap or this dispatch's own override says.
        var effectiveCap = mode == AutopilotDeliveryMode.CommitToMain
            ? 1
            : AutopilotConcurrency.Effective(settingsCap, settings.ConcurrencyCapOverride);
        var dispatched = settings with
        {
            EffectiveConcurrencyCap = effectiveCap,
        };
        var run = AutopilotRun.Start(
            Guid.NewGuid(), project, dispatched, [.. selected.Select(i => i.Id)], clock.GetUtcNow(),
            baseBranch: workspace.Branch, baseWorkspaceId: workspace.WorkspaceId);
        var saved = await runs.SaveAsync(run, ct).ConfigureAwait(false);
        return new AutopilotDispatchOutcome(saved, null);
    }

    private static async Task<IReadOnlyList<WorkItem>> AllItemsAsync(
        Fravashi.Application.WorkItems.ITracker tracker, CancellationToken ct)
    {
        var items = new List<WorkItem>();
        string? cursor = null;
        do
        {
            var page = await tracker.QueryAsync(
                new Fravashi.Application.WorkItems.WorkItemQuery
                {
                    Limit = Fravashi.Application.WorkItems.WorkItemQuery.MaxLimit,
                    Cursor = cursor,
                },
                ct).ConfigureAwait(false);
            items.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return items;
    }
}
