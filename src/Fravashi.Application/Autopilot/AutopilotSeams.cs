using Fravashi.Application.Sessions;
using Fravashi.Domain.Autopilot;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;

namespace Fravashi.Application.Autopilot;

/// <summary>
/// The four narrow seams the engine (#297) calls into and a later slice fills for real (ADR 0031 decisions 10 and
/// 11). Each is driven by a fake in this slice's tests; nothing here runs a real agent.
/// </summary>
/// <param name="Text">The prompt text.</param>
/// <param name="CostUsd">What writing it cost.</param>
public sealed record AutopilotPrompt(string Text, decimal CostUsd);

/// <summary>What the operator advises after a build attempt that made no commit and asked for no person.</summary>
/// <param name="RetryBuild">True to try the building agent again; false parks the item for a person.</param>
/// <param name="Reason">Why, when parking.</param>
/// <param name="CostUsd">
/// What asking for this advice cost (#300): folded into the item's and the run's spending by
/// <see cref="AutopilotEngine"/> before it decides what to do with the advice, so the money limits see it.
/// Defaults to zero so every existing caller that builds one positionally still compiles.
/// </param>
public sealed record AutopilotAdvice(bool RetryBuild, string? Reason = null, decimal CostUsd = 0m);

/// <summary>One work item the operator drafted from findings (#300): a draft only, never written to the tracker.</summary>
/// <param name="Title">The draft's title.</param>
/// <param name="Description">The draft's description.</param>
/// <param name="AcceptanceCriteria">The draft's done-test, in order; empty when the operator gave none.</param>
public sealed record AutopilotFollowUpDraft(string Title, string Description, IReadOnlyList<string> AcceptanceCriteria);

/// <summary>
/// What drafting follow-ups cost, beside the drafts themselves (#300). The operator never calls
/// <c>work_item_create</c> (it holds no write tools at all): whether a draft becomes a real item is the
/// engine's call, in whichever later slice reads findings and asks for this.
/// </summary>
/// <param name="Drafts">The drafts, in the order the operator wrote them; empty when it found nothing to draft.</param>
/// <param name="CostUsd">What drafting them cost.</param>
public sealed record AutopilotFollowUpDrafts(IReadOnlyList<AutopilotFollowUpDraft> Drafts, decimal CostUsd);

/// <summary>
/// What the operator needs beside the item to answer (#300): which project's tracker and wiki to read, and
/// which runtime and model the dispatch configured it on (ADR 0031 decision 2, "each role's runtime and
/// model"). Neither travelled with the original two-method seam, which a real implementation cannot work
/// without: it has no way to pick a tracker, a workspace to run in, or a model otherwise.
/// </summary>
/// <param name="Project">The run's project.</param>
/// <param name="Role">The dispatch's operator runtime and model.</param>
public sealed record AutopilotOperatorContext(ProjectId Project, AutopilotRole Role)
{
    /// <summary>The context for one run, read off the run itself.</summary>
    public static AutopilotOperatorContext From(AutopilotRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new AutopilotOperatorContext(run.Project, run.Settings.Operator);
    }
}

/// <summary>
/// The seam #300 fills: narrow advice the engine asks for, never a decision (ADR 0031 decision 10, "the operator
/// only ADVISES: the engine decides what is allowed"). Three judgement steps, exactly the three the ADR names:
/// writing the item's prompt, advising on a failure, and drafting follow-ups from findings. Every method either
/// answers or throws; a malformed answer, no answer, a timeout or an error from a real implementation is always a
/// thrown exception, which <see cref="AutopilotEngine"/> already turns into a named stop (never a sentinel value
/// that could be mistaken for a decision).
/// </summary>
public interface IAutopilotOperator
{
    /// <summary>Writes the item's prompt (the Prompting stage), from the item, its plan and the repository's instructions.</summary>
    Task<AutopilotPrompt> WritePromptAsync(AutopilotOperatorContext context, WorkItemId item, CancellationToken ct = default);

    /// <summary>Advises whether another build attempt is warranted after one that made no commit.</summary>
    Task<AutopilotAdvice> AdviseOnFailureAsync(
        AutopilotOperatorContext context, WorkItemId item, string failure, CancellationToken ct = default);

    /// <summary>
    /// Drafts follow-up items from findings (a CI log, a verifier report, or a review): never written to the
    /// tracker, and never called by this slice's engine (nothing yet hands it findings to read).
    /// </summary>
    Task<AutopilotFollowUpDrafts> DraftFollowUpsAsync(
        AutopilotOperatorContext context, WorkItemId item, string findings, CancellationToken ct = default);
}

/// <summary>What one building attempt did (the Building stage).</summary>
/// <param name="CommitMade">True when the attempt branch moved.</param>
/// <param name="BranchHeadSha">The attempt branch's new head, when a commit was made.</param>
/// <param name="CostUsd">What the attempt cost.</param>
/// <param name="NeedsPerson">True when the building agent itself asked a question, or a call it made is waiting on a person.</param>
/// <param name="Problem">Why there is no commit, or what the agent asked, in words a person can act on.</param>
/// <param name="ReviewFailed">
/// True when the commit reviewer FAILED the attempt (backlog 243, 309): the broker refuses a failing commit
/// before the tool itself ever runs, so this attempt never reaches the Reviewing stage the way a passed one
/// does, and the engine would otherwise never learn a review failed at all. Default false, so every existing
/// caller that builds one positionally still compiles.
/// </param>
/// <param name="FailedReviewCount">
/// How many separate reviews failed this turn (#321): a turn can retry <c>attempt_commit</c> more than once,
/// each attempt reviewed on its own, and every one of them must count against ADR 0031 decision 6's per-item
/// ceiling, not only the last, whether the turn ends with nothing committed (<see cref="ReviewFailed"/> true)
/// or with an EARLIER attempt failing and a LATER one landing the commit (<see cref="CommitMade"/> true): a
/// failure that preceded an eventual success still owes its own count, or it would be drained and discarded
/// by the Reviewing stage's own single-review read instead. Default 0 (nothing failed alongside whatever this
/// result reports), so every existing caller that builds one positionally still compiles; a caller that sets
/// <see cref="ReviewFailed"/> true without naming a count is still read as exactly one failure (the engine
/// clamps it to at least one on that path), since "failed, but nothing failed" is not a real answer.
/// </param>
/// <param name="WaitingForCapacity">
/// True when the concurrency gate (ADR 0031 decision 16, #308) refused a FRESH session start: nothing ran, so
/// <see cref="CostUsd"/> is always zero on this path, and the engine reads this before anything else and
/// leaves the item exactly as it was, spending no limit at all rather than a failure (host note point 4):
/// the engine rebases the item's own elapsed-time clock on every tick this is true, so not even
/// <c>AutopilotLimits.TimePerItem</c> fires while it waits. Ends only when a slot frees or a person stops the
/// run. Default false, so every existing caller that builds one positionally still compiles.
/// </param>
/// <param name="Refused">
/// True when the item was NOT started, for one of two reasons: its workspace could not be put back on the
/// run's base branch (#315: uncommitted changes left between items, edits still arriving, an unreadable
/// tree), or its worktree's or workspace's contained copy never finished its first pull within the builder's
/// own bound, or that copy's start had already failed (#328, <c>AgentAutopilotBuilder.WaitForCopyReadyAsync</c>).
/// Either way nothing ran and nothing is spent, and <see cref="Problem"/> says why in words, naming the copy
/// on the #328 path. The engine parks the item at once with that reason, without asking the operator for
/// advice, because there is no attempt to advise on, and one item's leftovers are never carried into the
/// next. Default false.
/// </param>
public sealed record AutopilotBuildResult(
    bool CommitMade, string? BranchHeadSha, decimal CostUsd, bool NeedsPerson, string? Problem,
    bool ReviewFailed = false, bool WaitingForCapacity = false, bool Refused = false, int FailedReviewCount = 0);

/// <summary>
/// What the builder needs beside the item to work it (#309): which project's tracker and workspace to run
/// in, and which runtime and model the dispatch configured the building agent on (ADR 0031 decision 2).
/// Neither travelled with the original one-item seam, which a real implementation cannot work without: it
/// has no way to pick a tracker, a workspace to run in, or a model otherwise. The same addition #300 made to
/// <see cref="IAutopilotOperator"/>, for the same reason. <see cref="RunId"/> (#305) is what lets a real
/// implementation tell "this item's bound session belongs to THIS run" apart from "it is an earlier run's
/// own leftover session for the same item" (a parked item resumed in a later run, whose old session a prior
/// run's own ending failed to stop): a fresh attempt must never send into, or read a re-check from, a
/// session another run started.
/// </summary>
/// <param name="Project">The run's project.</param>
/// <param name="Role">The dispatch's builder runtime and model.</param>
/// <param name="RunId">The run asking for this build attempt.</param>
/// <param name="ConcurrencyCapOverride">
/// The dispatch's own concurrency cap override (ADR 0031 decision 16, #307), read by
/// <see cref="IAutopilotConcurrencyGate"/> before a fresh session starts. Null means "use the Settings cap".
/// </param>
/// <param name="BaseBranch">
/// The repository's own real base branch (#308: <see cref="AutopilotRun.BaseBranch"/>), needed only for a
/// PARALLEL run (effective cap more than 1), to make each item's own worktree off it; null for a run
/// dispatched before #310 ever recorded one, or an unreachable workspace, in which case a parallel run's
/// worktree creation fails honestly rather than guessing a branch.
/// </param>
/// <param name="EffectiveConcurrencyCap">
/// <see cref="AutopilotDispatchSettings.EffectiveConcurrencyCap"/>, resolved once at dispatch: whether THIS
/// item gets its own worktree or the run's shared workspace (#308 decision 2). Never re-read from live
/// Settings here either, for the identical reason the engine does not.
/// </param>
/// <param name="RepositoryWorkspaceId">
/// <see cref="AutopilotRun.BaseWorkspaceId"/>, resolved once at dispatch (#310): the repository a PARALLEL
/// item's own worktree is made FROM. Deliberately never the project-to-workspace lookup
/// (<c>IWorkspaceStore</c>'s "most recently opened for this project") <see cref="IAutopilotBuilder"/>'s own
/// shared-workspace resolution still uses for a cap-1 run: once a worktree is registered through that SAME
/// lookup (<c>Touch</c>, which stamps the newest <c>LastOpenedUtc</c>), the lookup would answer the worktree
/// itself for every call after the first, making a worktree OF a worktree. Null for a run dispatched before
/// #310 ever recorded one, or an unreachable workspace, in which case a parallel run's worktree creation
/// fails honestly rather than guessing a repository.
/// </param>
public sealed record AutopilotBuilderContext(
    ProjectId Project, AutopilotRole Role, Guid RunId, int? ConcurrencyCapOverride = null, string? BaseBranch = null,
    int EffectiveConcurrencyCap = 1, string? RepositoryWorkspaceId = null)
{
    /// <summary>The context for one run, read off the run itself.</summary>
    public static AutopilotBuilderContext From(AutopilotRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new AutopilotBuilderContext(
            run.Project, run.Settings.Builder, run.Id, run.Settings.ConcurrencyCapOverride, run.BaseBranch,
            run.Settings.EffectiveConcurrencyCap, run.BaseWorkspaceId);
    }
}

/// <summary>
/// The seam the building agent fills: an ordinary studio session, marked as an autopilot session (ADR 0031
/// decision 11). Here, a fake.
/// </summary>
public interface IAutopilotBuilder
{
    /// <summary>Runs one building attempt on the item's prompt.</summary>
    Task<AutopilotBuildResult> BuildAsync(
        AutopilotBuilderContext context, WorkItemId item, string prompt, CancellationToken ct = default);

    /// <summary>
    /// Ends the item's live agent process now (ADR 0031 decision 13, "stop now"), when it has one. A no-op,
    /// never a throw, for an item with no live process (it never started building, or it already ended).
    /// </summary>
    Task StopAsync(WorkItemId item, CancellationToken ct = default);

    /// <summary>
    /// The item has left every open stage (Done, Stopped or Parked alike, #308): the moment to stop its live
    /// session (if any is still running: the parallel path has nothing else that stops it, unlike the shared
    /// workspace, which is only ever returned when the whole RUN ends) and prune whatever worktree a PARALLEL
    /// run's own item was given (never for a cap-1, sequential run, which never makes one), dropping its
    /// registration too. A no-op for an item that was never given one. <paramref name="context"/> is what
    /// lets this resolve the item's repository even after a restart, when the process-local record of which
    /// item had which worktree is gone: the bridge's own prune primitive is keyed by (repository workspace id,
    /// item number), not by anything only this process remembers. Never throws for an ordinary refusal (a
    /// dirty worktree is left in place): an implementation RETURNS those words instead of raising them, since
    /// the item has already closed and there is nothing left here for the engine to stop a second time; the
    /// caller folds a non-null answer onto the run's own record (plan @126 point 8, "never silently
    /// swallowed"), the same way a thrown exception already is. Null means nothing needs saying: there was no
    /// worktree, or it pruned cleanly.
    /// </summary>
    Task<string?> EndItemAsync(AutopilotBuilderContext context, WorkItemId item, CancellationToken ct = default);
}

/// <summary>
/// The seam the commit review outcome fills (the Reviewing stage): the studio's own <see cref="CommitReview"/>
/// (backlog 243), asked here about the item's building attempt rather than reusing a separate verdict type.
/// </summary>
public interface IAutopilotCommitReviews
{
    /// <summary>Reviews what the building agent would commit for this item.</summary>
    Task<CommitReview> ReviewAsync(WorkItemId item, CancellationToken ct = default);
}

/// <summary>What one delivery attempt did (the Delivering stage).</summary>
/// <param name="Delivered">True when the attempt branch was pushed and the pull request opened.</param>
/// <param name="PullRequestUrl">The pull request, once opened.</param>
/// <param name="Problem">Why it did not deliver.</param>
public sealed record AutopilotDeliveryResult(bool Delivered, string? PullRequestUrl, string? Problem);

/// <summary>What verification found (the Verifying stage).</summary>
/// <param name="Passed">True when CI is green and the scenario (and design check, where approved) passed.</param>
/// <param name="NeedsPerson">True when verification itself could not be run and a person must look.</param>
/// <param name="Problem">What failed, or why a person is needed.</param>
/// <param name="Pending">
/// True when CI or the scenario has not SETTLED yet, as distinct from having failed (#301): the item stays in
/// Verifying and the next tick checks again, bounded by the item's own time and no-progress limits rather
/// than a new timeout. Default false, so every existing named-argument construction keeps compiling.
/// </param>
public sealed record AutopilotVerifyResult(bool Passed, bool NeedsPerson, string? Problem, bool Pending = false);

/// <summary>
/// What the delivery seam needs beside the item to work it (#301): which project's tracker and delivery mode
/// to use, and what the dispatch said to do about an item with no scenario. The same addition #300 and #309
/// made to the operator and builder seams, for the identical reason: a real implementation cannot resolve a
/// tracker, or a delivery mode, without it.
/// </summary>
/// <param name="Project">The run's project.</param>
/// <param name="MissingScenario">The dispatch's policy for an item with no scenario (ADR decision 2).</param>
public sealed record AutopilotDeliveryContext(ProjectId Project, MissingScenarioPolicy MissingScenario)
{
    /// <summary>The context for one run, read off the run itself.</summary>
    public static AutopilotDeliveryContext From(AutopilotRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new AutopilotDeliveryContext(run.Project, run.Settings.MissingScenario);
    }
}

/// <summary>
/// The seam #301 fills: the attempt branch, the push, the pull request, CI and the scenario run. Here, a fake.
/// </summary>
public interface IAutopilotDelivery
{
    /// <summary>Pushes the attempt branch at <paramref name="branchHeadSha"/> and opens the pull request.</summary>
    Task<AutopilotDeliveryResult> DeliverAsync(AutopilotDeliveryContext context, WorkItemId item, string branchHeadSha, CancellationToken ct = default);

    /// <summary>Reads CI and runs the item's scenario against the delivered branch at <paramref name="branchHeadSha"/>.</summary>
    Task<AutopilotVerifyResult> VerifyAsync(AutopilotDeliveryContext context, WorkItemId item, string branchHeadSha, CancellationToken ct = default);
}

/// <summary>
/// One parallel item's own worktree (ADR 0031 decisions 15 and 16, #308), made from #306's bridge primitive:
/// a registered workspace of its own, on its own branch, off the repository's real base branch.
/// </summary>
/// <param name="WorkspaceId">The worktree's own registered workspace id (the building session runs here).</param>
/// <param name="Name">The worktree's folder name, for <see cref="Fravashi.Application.Persistence.IWorkspaceStore.Touch"/>.</param>
/// <param name="RootPath">The worktree's own host folder, for the same registration.</param>
/// <param name="Branch">The worktree's own branch, deterministic from the item number: the attempt branch too (#308's own decision 3 - attempt_branch makes no second one).</param>
/// <param name="BaseBranch">The repository's REAL base branch (e.g. "main") the worktree's branch was made from.</param>
public sealed record AutopilotWorktree(string WorkspaceId, string Name, string RootPath, string Branch, string BaseBranch);

/// <summary>
/// Creates and prunes a git worktree per parallel item (ADR 0031 decisions 15 and 16, #308), through the host
/// bridge's own already-tested primitive (#306: <c>POST</c>/<c>DELETE /bridge/ws/{id}/worktrees</c>). Reached
/// only for a run whose effective concurrency cap is more than 1; a cap-1 (sequential) run keeps today's
/// shared-workspace behaviour and never calls this at all (#308's own decision 2).
/// </summary>
public interface IAutopilotWorktrees
{
    /// <summary>Creates (or, asked again, finds) the item's own worktree off the repository's real base branch.</summary>
    Task<AutopilotWorktree> CreateAsync(
        string repositoryWorkspaceId, int itemNumber, string baseBranch, CancellationToken ct = default);

    /// <summary>
    /// Prunes a CLEAN worktree and drops its registration; words, never a throw, when it is left in place
    /// (dirty) or there was nothing to prune. Null means it was removed, or there was nothing there at all.
    /// </summary>
    Task<string?> PruneAsync(string repositoryWorkspaceId, int itemNumber, CancellationToken ct = default);
}

/// <summary>The durable store for a run (schema step 39). A run survives a studio restart through this.</summary>
public interface IAutopilotRunStore
{
    /// <summary>Saves the run as given, replacing whatever was stored for its id.</summary>
    Task<AutopilotRun> SaveAsync(AutopilotRun run, CancellationToken ct = default);

    /// <summary>The run with this id, or null when there is none.</summary>
    Task<AutopilotRun?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Every run still <see cref="AutopilotRunState.Running"/>, for resuming at process start.</summary>
    Task<IReadOnlyList<AutopilotRun>> OpenRunsAsync(CancellationToken ct = default);

    /// <summary>
    /// Every run ever dispatched for this project, newest started first, open or finished alike (#303: the
    /// autopilot page's history). A row this read cannot make sense of is left out rather than failing the whole list,
    /// since this is a display read, never the resume path <see cref="OpenRunsAsync"/> is.
    /// </summary>
    Task<IReadOnlyList<AutopilotRun>> ForProjectAsync(ProjectId project, CancellationToken ct = default);

    /// <summary>
    /// The single most recently STARTED run for each project that has ever had one, open or finished alike
    /// (ADR 0031 decision 12, #305): what <see cref="AutopilotDispatcher"/> resumes a parked item from, and
    /// what the home page's waiting list reads so a parked item stays listed after its own run has ended, not
    /// only while a run is still <see cref="AutopilotRunState.Running"/>. A row this read cannot make sense of
    /// is left out, the same display-read posture <see cref="ForProjectAsync"/> already takes.
    /// </summary>
    Task<IReadOnlyList<AutopilotRun>> LatestPerProjectAsync(CancellationToken ct = default);
}
