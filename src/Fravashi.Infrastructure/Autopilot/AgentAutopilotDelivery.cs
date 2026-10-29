using System.Globalization;
using System.Net.Http;
using System.Text;
using Fravashi.Application.AgentSync;
using Fravashi.Application.Autopilot;
using Fravashi.Application.Git;
using Fravashi.Application.Projects;
using Fravashi.Application.Sessions;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Autopilot;
using Fravashi.Domain.PullRequests;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;
using Fravashi.Infrastructure.Ci;
using Fravashi.Infrastructure.Git;
using Fravashi.Infrastructure.PullRequests;
using Fravashi.Infrastructure.Sessions;

namespace Fravashi.Infrastructure.Autopilot;

/// <summary>
/// The real <see cref="IAutopilotDelivery"/> (#301, ADR 0031 decisions 3, 4 and 5): under branch + pull
/// request, pushes the item's attempt branch through the studio's own git services (never the building
/// agent: see the plan @119 for why), opens or finds its pull request, reads CI, runs the item's scenario
/// against the branch, records evidence and moves the item to In Review. Under commit to main, moves the
/// base branch's ref to the attempt's head when that is a fast-forward (never checking anything out, so a
/// person's own checkout is never switched) and leaves the actual push, and so verification, to a person.
/// Resolves everything from the <see cref="WorkItemId"/> alone: which session built it
/// (<see cref="AutopilotBuilderSessions"/>), that session's workspace and git (<see cref="SessionGit"/>), and
/// the branch it made (<see cref="ISessionBranches"/>), exactly as <c>AttemptGitTools</c>/<c>SessionGitWork</c>
/// already do for a person's own Git page.
/// </summary>
public sealed class AgentAutopilotDelivery : IAutopilotDelivery
{
    /// <summary>
    /// A run's <c>Status</c> when it has not settled yet, in each provider's own documented words (never
    /// <see cref="ForgeRuns"/>'s own mapping, which passes each provider's word straight through unmapped):
    /// GitHub's workflow-run <c>status</c> values once there is no <c>conclusion</c> yet (queued, in_progress,
    /// requested, waiting), Forgejo's task status, Azure DevOps' build <c>status</c> once there is no
    /// <c>result</c> yet (notStarted/postponed read lower-cased here, since <see cref="ForgeRun"/> passes the
    /// raw string through and this set is matched case-insensitively).
    /// </summary>
    private static readonly HashSet<string> StillGoing = new(StringComparer.OrdinalIgnoreCase)
    {
        "queued", "in_progress", "inprogress", "running", "waiting", "blocked", "requested",
        "notstarted", "not_started", "pending", "postponed", "cancelling",
    };

    private readonly AutopilotBuilderSessions _sessions;
    private readonly SessionGit _sessionGit;
    private readonly ISessionBranches _branches;
    private readonly IProjectDelivery _deliveryMode;
    private readonly IHttpClientFactory _httpClients;
    private readonly Func<GitHostAccess> _hostAccess;
    private readonly ForgeRuns _forge;
    private readonly ITrackerResolver _trackers;
    private readonly IWorkspaceAgentSync _sync;
    private readonly ScenarioRunner _scenarios;

    public AgentAutopilotDelivery(
        AutopilotBuilderSessions sessions,
        SessionGit sessionGit,
        ISessionBranches branches,
        IProjectDelivery deliveryMode,
        IHttpClientFactory httpClients,
        Func<GitHostAccess> hostAccess,
        ForgeRuns forge,
        ITrackerResolver trackers,
        IWorkspaceAgentSync sync,
        ScenarioRunner scenarios)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _sessionGit = sessionGit ?? throw new ArgumentNullException(nameof(sessionGit));
        _branches = branches ?? throw new ArgumentNullException(nameof(branches));
        _deliveryMode = deliveryMode ?? throw new ArgumentNullException(nameof(deliveryMode));
        _httpClients = httpClients ?? throw new ArgumentNullException(nameof(httpClients));
        _hostAccess = hostAccess ?? throw new ArgumentNullException(nameof(hostAccess));
        _forge = forge ?? throw new ArgumentNullException(nameof(forge));
        _trackers = trackers ?? throw new ArgumentNullException(nameof(trackers));
        _sync = sync ?? throw new ArgumentNullException(nameof(sync));
        _scenarios = scenarios ?? throw new ArgumentNullException(nameof(scenarios));
    }

    /// <inheritdoc />
    public async Task<AutopilotDeliveryResult> DeliverAsync(
        AutopilotDeliveryContext context, WorkItemId item, string branchHeadSha, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var (resolved, problem) = Resolve(item);
        if (resolved is not { } target)
        {
            return new AutopilotDeliveryResult(Delivered: false, PullRequestUrl: null, Problem: problem);
        }

        return _deliveryMode.For(context.Project) == AutopilotDeliveryMode.CommitToMain
            ? await DeliverToMainAsync(target, branchHeadSha, ct).ConfigureAwait(false)
            : await DeliverToPullRequestAsync(context, item, target, branchHeadSha, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AutopilotVerifyResult> VerifyAsync(
        AutopilotDeliveryContext context, WorkItemId item, string branchHeadSha, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var (resolved, problem) = Resolve(item);
        if (resolved is not { } target)
        {
            return new AutopilotVerifyResult(Passed: false, NeedsPerson: true, Problem: problem);
        }

        if (_deliveryMode.For(context.Project) == AutopilotDeliveryMode.CommitToMain)
        {
            // Nothing has been pushed to origin yet (ADR 0031 decision 9: the push itself waits for a
            // person), so there is no CI to read and no branch anyone on the host side can see to verify.
            return new AutopilotVerifyResult(
                Passed: false,
                NeedsPerson: true,
                Problem: $"Commits are on '{target.Branch.BaseBranch}' locally. Push it to run CI and the scenario.");
        }

        return await VerifyPullRequestAsync(context, item, target, branchHeadSha, ct).ConfigureAwait(false);
    }

    // ---- resolution: item -> building session -> workspace git -> attempt branch ------------------------

    private readonly record struct Resolved(WorkspaceGitServices Git, SessionBranch Branch, string WorkspaceId);

    private (Resolved? Resolved, string? Problem) Resolve(WorkItemId item)
    {
        if (_sessions.SessionFor(item) is not { } session)
        {
            return (null, "There is no building session recorded for this item in this process, so there is nothing to deliver.");
        }

        if (_sessionGit.For(session) is not { Git.IsAvailable: true } git)
        {
            return (null, "This item's workspace has no git repository the studio can reach.");
        }

        if (_branches.For(session) is not { } branch)
        {
            return (null, "This item's attempt was never branched.");
        }

        return (new Resolved(git, branch, git.WorkspaceId), null);
    }

    // ---- branch + pull request --------------------------------------------------------------------------

    private async Task<AutopilotDeliveryResult> DeliverToPullRequestAsync(
        AutopilotDeliveryContext context, WorkItemId item, Resolved target, string branchHeadSha, CancellationToken ct)
    {
        var current = await target.Git.Git.GetCurrentBranchAsync(ct).ConfigureAwait(false);
        if (!string.Equals(current, target.Branch.Branch, StringComparison.Ordinal))
        {
            return new AutopilotDeliveryResult(false, null,
                $"The workspace is checked out on '{current}', not the attempt branch '{target.Branch.Branch}', so nothing was pushed.");
        }

        var pushed = await target.Git.Remote.PushAsync(ct).ConfigureAwait(false);
        if (!pushed.Succeeded)
        {
            if (pushed.Failure == GitSyncFailure.OutcomeUnknown && pushed.Pending is { } pending)
            {
                // Never a blind resend: ask origin whether the push that got no answer actually arrived.
                pushed = await target.Git.Remote.ComparePushAsync(pending, ct).ConfigureAwait(false);
            }

            if (!pushed.Succeeded)
            {
                return new AutopilotDeliveryResult(false, null, pushed.Error ?? "The push did not succeed.");
            }
        }

        var pullRequests = new PullRequestService(_httpClients.CreateClient(), target.Git.Git, _hostAccess());
        if (!await pullRequests.IsConfiguredAsync(ct).ConfigureAwait(false))
        {
            return new AutopilotDeliveryResult(false, null,
                "This repository's origin is not a git host the studio knows, so no pull request can be opened.");
        }

        // IsConfiguredAsync only answers "a provider is known" (a known provider reads anonymously on a
        // public repo); the stored TOKEN mutations actually need is checked by CreateAsync itself, below.
        var tracker = _trackers.For(context.Project);
        // ListAsync asks for the newest 50 open pull requests, with no source-branch filter the service
        // offers: "found, not opened twice" holds within that bound. A repository with more than 50 open
        // pull requests could miss the attempt branch's own here; the catch below is what keeps a duplicate
        // CreateAsync then draws from becoming an unhandled stop rather than a parked Problem.
        var open = await pullRequests.ListAsync(PullRequestState.Open, ct).ConfigureAwait(false);
        if (open.FirstOrDefault(pr => string.Equals(pr.SourceBranch, target.Branch.Branch, StringComparison.Ordinal)) is { } existing)
        {
            // Found, not opened twice. Still recorded as evidence (a replay when already recorded, below),
            // so a person can find the open pull request from the item alone when a later limit stops it.
            var url = existing.Url?.ToString();
            await RecordPullRequestEvidenceAsync(tracker, item, existing.Number, url, ct).ConfigureAwait(false);
            return new AutopilotDeliveryResult(true, url, null);
        }

        var workItem = await tracker.GetAsync(item, ct).ConfigureAwait(false);
        if (workItem is null)
        {
            return new AutopilotDeliveryResult(false, null,
                $"Project '{context.Project.Value}' has no item '{item.Value}' to open a pull request for.");
        }

        var evidence = await tracker.EvidenceAsync(item, ct).ConfigureAwait(false);
        try
        {
            var created = await pullRequests.CreateAsync(
                new CreatePullRequestRequest(Title(workItem), Body(workItem, evidence), target.Branch.Branch, target.Branch.BaseBranch),
                ct).ConfigureAwait(false);

            var url = created.Url?.ToString();
            await RecordPullRequestEvidenceAsync(tracker, item, created.Number, url, ct).ConfigureAwait(false);
            return new AutopilotDeliveryResult(true, url, null);
        }
        catch (InvalidOperationException ex)
        {
            // The one case RequireConfiguredAsync throws for rather than answering through IsConfiguredAsync:
            // the provider is known, but no token is stored for it (it can be removed between dispatch,
            // where #298 already checked for one, and delivery).
            return new AutopilotDeliveryResult(false, null, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            // The host's own refusal, most plausibly its "a pull request already exists for this branch"
            // answer when the 50-entry list above missed it: reported as a Problem (parks for a person) the
            // same as any other delivery failure, never an unhandled exception that would stop the item
            // under the stage's own name rather than this one.
            return new AutopilotDeliveryResult(false, null, ex.Message);
        }
    }

    /// <summary>
    /// Records a pull request's url as evidence, once per pull request (the operation id is scoped to the
    /// item AND the pull request's own number, never the item alone): a repeated delivery that finds the
    /// SAME pull request replays this write rather than duplicating the row, but a LATER run that opens a
    /// DIFFERENT pull request for the same item (the first since closed or merged) records its own row
    /// rather than replaying the first one's, which is what lets a stop record's "its pull request open"
    /// actually name the one that is still open.
    /// </summary>
    private static Task RecordPullRequestEvidenceAsync(ITracker tracker, WorkItemId item, int number, string? url, CancellationToken ct) =>
        tracker.RecordEvidenceAsync(
            item,
            new NewEvidence
            {
                Kind = EvidenceKind.Other,
                Result = EvidenceResult.Pass,
                Summary = url is { Length: > 0 } ? $"Pull request opened: {url}" : "Pull request opened.",
                ArtefactRef = url,
                Gate = "delivery",
            },
            new MutationContext(WorkItemActor.System, $"autopilot-pull-request-{item.Value}-{number.ToString(CultureInfo.InvariantCulture)}"),
            ct);

    private async Task<AutopilotVerifyResult> VerifyPullRequestAsync(
        AutopilotDeliveryContext context, WorkItemId item, Resolved target, string branchHeadSha, CancellationToken ct)
    {
        var origin = await target.Git.Git.GetRemoteUrlAsync(ct).ConfigureAwait(false);
        var repository = _forge.Repository(origin);
        if (repository is null)
        {
            return new AutopilotVerifyResult(Passed: false, NeedsPerson: true,
                Problem: "This repository's origin is not a git host the studio knows, so CI cannot be read.");
        }

        IReadOnlyList<ForgeRun> runs;
        try
        {
            runs = await _forge.ListAsync(repository, 50, branchHeadSha, ct).ConfigureAwait(false);
        }
        catch (ForgeUnreachableException)
        {
            // The network, not the forge (backlog 233): safe to look again next tick, never a failure.
            return new AutopilotVerifyResult(Passed: false, NeedsPerson: false, Problem: null, Pending: true);
        }
        catch (HttpRequestException ex)
        {
            return new AutopilotVerifyResult(Passed: false, NeedsPerson: true, Problem: ex.Message);
        }

        // Zero runs is ALSO pending, bounded by the item's own time and no-progress limits rather than a new
        // "CI never configured for this change" detector (see the plan @119): the two are indistinguishable
        // from here, and guessing between them is worse than letting the existing bounds eventually stop the
        // item honestly.
        if (runs.Count == 0 || runs.Any(r => StillGoing.Contains(r.Status)))
        {
            return new AutopilotVerifyResult(Passed: false, NeedsPerson: false, Problem: null, Pending: true);
        }

        var failed = runs.Where(r => !IsPassing(r.Status)).ToList();
        if (failed.Count > 0)
        {
            return new AutopilotVerifyResult(Passed: false, NeedsPerson: false,
                Problem: "CI is red: " + string.Join(", ", failed.Select(r => $"{r.Workflow} ({r.Status})")) + ".");
        }

        var tracker = _trackers.For(context.Project);
        // Scoped to the item AND the sha: a re-check of the SAME commit (a scenario running elsewhere, a
        // Pending re-tick) replays rather than writing an unbounded number of identical rows; a later commit
        // on the same item (the fix loop) is a genuinely new fact and gets its own row.
        await tracker.RecordEvidenceAsync(
            item,
            new NewEvidence
            {
                Kind = EvidenceKind.Build,
                Result = EvidenceResult.Pass,
                Summary = $"CI green on {repository.ProviderName}: " + string.Join(", ", runs.Select(r => r.Workflow)) + ".",
                Gate = "verify",
            },
            new MutationContext(WorkItemActor.System, $"autopilot-ci-green-{item.Value}-{branchHeadSha}"),
            ct).ConfigureAwait(false);

        var workItem = await tracker.GetAsync(item, ct).ConfigureAwait(false);
        if (workItem is null)
        {
            return new AutopilotVerifyResult(Passed: false, NeedsPerson: true,
                Problem: $"Project '{context.Project.Value}' has no item '{item.Value}'.");
        }

        if (workItem.Scenarios.Count == 0)
        {
            if (context.MissingScenario != MissingScenarioPolicy.Optional)
            {
                return new AutopilotVerifyResult(Passed: false, NeedsPerson: true,
                    Problem: "This item has no test scenario to verify, and the dispatch's missing-scenario "
                        + $"policy is '{context.MissingScenario}', not 'Optional'.");
            }
        }
        else
        {
            _sync.EnsureStarted(target.WorkspaceId);
            if (_sync.CopyPathFor(target.WorkspaceId) is not { Length: > 0 } copyPath)
            {
                // Not a failure of the change: ScenarioRunner reports every step "not run" for a null
                // working directory, which would otherwise read as a failed scenario and send the operator
                // and the builder into a fix attempt over a studio condition rather than anything in the item.
                return new AutopilotVerifyResult(Passed: false, NeedsPerson: true,
                    Problem: $"Workspace '{target.WorkspaceId}' has no contained copy yet, so the scenario cannot run.");
            }

            foreach (var scenario in workItem.Scenarios)
            {
                var attempt = await _scenarios.AttemptAsync(
                    context.Project, item, scenario.Id, copyPath, new MutationContext(WorkItemActor.System), ct).ConfigureAwait(false);
                if (attempt.AlreadyRunning)
                {
                    return new AutopilotVerifyResult(Passed: false, NeedsPerson: false, Problem: null, Pending: true);
                }

                if (attempt.Run is not { } scenarioRun || scenarioRun.Result != EvidenceResult.Pass)
                {
                    return new AutopilotVerifyResult(Passed: false, NeedsPerson: false,
                        Problem: attempt.Run?.Summarise(scenario.Title) ?? $"'{scenario.Title}' did not run.");
                }
            }
        }

        // Scoped to the item AND the sha: an operation id that named only the item would replay FOREVER once
        // used once, including on a LATER run of the same item (parked, re-marked, built again to a new
        // sha), which would report Passed without the item ever actually moving.
        var fresh = await tracker.GetAsync(item, ct).ConfigureAwait(false) ?? workItem;
        var transitioned = await tracker.TransitionAsync(
            item, WorkItemStatus.InReview, null, null, null, fresh.Version,
            new MutationContext(WorkItemActor.System, $"autopilot-verify-done-{item.Value}-{branchHeadSha}"), ct).ConfigureAwait(false);

        return transitioned.IsOk
            ? new AutopilotVerifyResult(Passed: true, NeedsPerson: false, Problem: null)
            : new AutopilotVerifyResult(Passed: false, NeedsPerson: true,
                Problem: $"Verification passed, but moving the item to In Review did not succeed ({transitioned.Outcome}).");
    }

    /// <summary>
    /// Green is an EXPLICIT pass word, never the absence of a recognised failure word: GitHub's conclusion
    /// <c>success</c>, plus the two documented non-failure conclusions a workflow can legitimately end in
    /// (<c>skipped</c>, <c>neutral</c>), Forgejo's own task status <c>success</c>, and Azure DevOps' own
    /// result <c>succeeded</c>. Anything settled that is NOT one of these (a GitHub <c>action_required</c>
    /// or <c>stale</c>, an Azure DevOps <c>partiallySucceeded</c> or <c>none</c>, a Forgejo <c>unknown</c>,
    /// an empty status, a provider's ordinary failure word) is red: ADR 0031 decision 4 is "CI is green",
    /// not "CI did not explicitly fail", and a settled word this build does not recognise is exactly the
    /// case that must not default to a pass.
    /// </summary>
    private static bool IsPassing(string status) =>
        string.Equals(status, "success", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "skipped", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "neutral", StringComparison.OrdinalIgnoreCase);

    private static string Title(WorkItem item) =>
        $"#{item.Number.ToString(CultureInfo.InvariantCulture)} {item.Title}";

    private static string Body(WorkItem item, IReadOnlyList<EvidenceRecord> evidence)
    {
        var text = new StringBuilder();
        text.Append("Autopilot delivery for #").Append(item.Number.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(item.Title).Append('\n');
        if (!string.IsNullOrWhiteSpace(item.Description))
        {
            text.Append('\n').Append(item.Description).Append('\n');
        }

        text.Append("\nEvidence so far:\n");
        if (evidence.Count == 0)
        {
            text.Append("- None recorded yet.\n");
        }
        else
        {
            foreach (var record in evidence)
            {
                text.Append("- ").Append(record.Summary).Append('\n');
            }
        }

        return text.ToString();
    }

    // ---- commit to main: a ref move only, never a checkout ---------------------------------------------

    private static async Task<AutopilotDeliveryResult> DeliverToMainAsync(
        Resolved target, string branchHeadSha, CancellationToken ct)
    {
        var baseBranch = target.Branch.BaseBranch;
        var current = await target.Git.Git.GetCurrentBranchAsync(ct).ConfigureAwait(false);
        if (string.Equals(current, baseBranch, StringComparison.Ordinal))
        {
            return new AutopilotDeliveryResult(false, null,
                $"'{baseBranch}' is checked out in this workspace, so its ref cannot be moved without touching the working tree.");
        }

        // #308 host note point 6: under a PARALLEL run this resolving workspace is one item's own worktree,
        // never the repository's root, so the check above never catches the base branch sitting checked out
        // in a DIFFERENT worktree of the SAME repository (the person's own folder, idle between runs; or, in
        // principle, another item's). `git worktree list` answers for the whole repository from any one of
        // its worktrees, so this asks it rather than resolving the repository's own root separately.
        string? elsewhere;
        try
        {
            elsewhere = await CheckedOutElsewhereAsync(target.Git, baseBranch, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            // Asked rather than guessed: a repository this cannot even list worktrees for must never be
            // read as "nothing else has it checked out", which would let the ref move proceed on a reading
            // that never actually answered the question. Parks for a person, the same shape every other
            // refusal here takes, never a silent proceed.
            return new AutopilotDeliveryResult(false, null, $"Could not list this repository's worktrees: {ex.Message}");
        }

        if (elsewhere is { Length: > 0 })
        {
            return new AutopilotDeliveryResult(false, null,
                $"'{baseBranch}' is checked out in {elsewhere}, so its ref cannot be moved without touching that working tree.");
        }

        var head = await target.Git.Runner.RunAsync(["rev-parse", "--verify", baseBranch], null, ct).ConfigureAwait(false);
        if (head.ExitCode != 0)
        {
            return new AutopilotDeliveryResult(false, null, $"Could not read '{baseBranch}': {head.StdErr.Trim()}");
        }

        var baseHead = head.StdOut.Trim();
        var mergeBase = await target.Git.Git.GetMergeBaseAsync(baseBranch, branchHeadSha, ct).ConfigureAwait(false);
        if (!string.Equals(mergeBase, baseHead, StringComparison.Ordinal))
        {
            return new AutopilotDeliveryResult(false, null,
                $"'{baseBranch}' moved since this attempt started and no longer fast-forwards to it; a person needs to resolve this.");
        }

        // The three-argument form is itself a compare-and-swap: git refuses if the ref moved since the read
        // above, which is the outbox's own "retry looks up, never blindly resends" shape, for free.
        var moved = await target.Git.Runner.RunAsync(
            ["update-ref", "refs/heads/" + baseBranch, branchHeadSha, baseHead], null, ct).ConfigureAwait(false);
        if (moved.ExitCode != 0)
        {
            return new AutopilotDeliveryResult(false, null, $"Could not move '{baseBranch}': {moved.StdErr.Trim()}");
        }

        return new AutopilotDeliveryResult(true, null, null);
    }

    /// <summary>
    /// Whichever OTHER worktree of this repository (never this one, resolving right now: its own current
    /// branch was already checked above) has <paramref name="baseBranch"/> checked out, named by its own
    /// folder, or null when none does. Parses the SAME porcelain shape <c>BridgeWorktreeService</c> already
    /// parses, duplicated rather than shared (a different project, a different trust boundary: the bridge is
    /// Windows-only and host-driven, this runs inside the Linux container). Throws, never answers null, when
    /// the listing itself fails: a repository this cannot even list worktrees for must never be read as "no
    /// worktrees", the same posture <c>BridgeWorktreeService.ListWorktreesAsync</c> already takes.
    /// </summary>
    private static async Task<string?> CheckedOutElsewhereAsync(
        WorkspaceGitServices git, string baseBranch, CancellationToken ct)
    {
        var listed = await git.Runner.RunAsync(["worktree", "list", "--porcelain"], null, ct).ConfigureAwait(false);
        if (listed.ExitCode != 0)
        {
            var reason = listed.StdErr.Trim();
            throw new InvalidOperationException(
                reason.Length > 0 ? reason : $"git worktree list exited with {listed.ExitCode}.");
        }

        string? path = null;
        foreach (var raw in listed.StdOut.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                path = null;
                continue;
            }

            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                path = line["worktree ".Length..];
            }
            else if (line.StartsWith("branch ", StringComparison.Ordinal) && path is { Length: > 0 })
            {
                var value = line["branch ".Length..];
                var branch = value.StartsWith("refs/heads/", StringComparison.Ordinal)
                    ? value["refs/heads/".Length..]
                    : value;
                if (string.Equals(branch, baseBranch, StringComparison.Ordinal))
                {
                    return path;
                }
            }
        }

        return null;
    }
}
