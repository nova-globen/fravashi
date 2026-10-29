using System.Globalization;
using System.Text;
using Fravashi.Application.Git;
using Fravashi.Application.Sessions;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Git;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Autopilot;
using Fravashi.Infrastructure.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fravashi.Infrastructure.Git;

/// <summary>What starting an attempt did.</summary>
/// <param name="Branch">The branch it runs on, or null when one could not be made.</param>
/// <param name="Error">Why not, when it could not.</param>
public sealed record AttemptStart(string? Branch, string? Error = null);

/// <summary>What committing an attempt did.</summary>
/// <param name="Sha">The commit, or null when nothing was committed.</param>
/// <param name="Committed">How many files went into it.</param>
/// <param name="Nothing">True when the session changed nothing, which is not an error.</param>
/// <param name="Error">Why it failed, when it did.</param>
/// <param name="NotSettled">
/// True when nothing was committed because the session's edits had not all reached the host folder within
/// the wait (backlog 150): the caller can simply ask again, and the tree it commits then is the one it meant.
/// </param>
/// <param name="Refused">
/// True when nothing was committed because the message broke <see cref="CommitMessageRule"/> (#318); the
/// <paramref name="Error"/> says what to write instead, so the caller can simply ask again with a better message.
/// </param>
public sealed record AttemptCommit(
    string? Sha, int Committed, bool Nothing = false, string? Error = null, bool NotSettled = false,
    bool Refused = false);

/// <summary>
/// What <see cref="SessionGitWork.DefaultPathsAsync"/> answers (backlog 332). <see cref="Paths"/> is the
/// honest default ONLY when <see cref="NotSettled"/> is false and <see cref="Error"/> is null: a
/// worktree-owning session whose settle genuinely timed out, or whose status could not be read, carries the
/// plain approved-changes fallback in <see cref="Paths"/> instead (possibly non-empty, possibly empty), which
/// is NOT the honest default and must never be committed or reviewed as one, because the worktree's own dirty
/// files were never actually read; a caller must check these two before reading <see cref="Paths"/> at all,
/// or a slow bridge can read exactly like a session that changed nothing (the #332 symptom, under a
/// different cause).
/// </summary>
/// <param name="Paths">The default path set, or the plain approved-changes fallback when a worktree's own read failed.</param>
/// <param name="NotSettled">True when a worktree-owning session's own settle timed out before anything could be read.</param>
/// <param name="Error">Why a worktree-owning session's own status could not be read, when it could not.</param>
public sealed record SessionDefaultPaths(IReadOnlyList<string> Paths, bool NotSettled = false, string? Error = null);

/// <summary>
/// Connects a session's work to Git (backlog 86, ADR 0018 decision 9). Agents edit a contained copy that
/// has no <c>.git</c>, and Git truth stays on the host with the sync engine writing changes back; nothing
/// joined the two ends, so an execution attempt had no branch, no known file set, and no commit. A person
/// did it by hand, which is fine supervised and impossible unattended.
/// <para>
/// The set of files it commits is the one the studio already knows: every path a change was APPROVED for
/// in this session (backlog 85's accounting). That is the honest answer to "what did this session write",
/// and it is better than committing everything dirty, which would sweep up whatever else was in the
/// working tree and attribute it to the agent.
/// </para>
/// <para>
/// <b>A session that changes nothing produces no commit</b>, and that is a success, not a failure. An
/// empty commit would put a lie in the history of the user's repository.
/// </para>
/// <para>
/// <b>Git is reached for the SESSION'S workspace, never for a circuit's (backlog 150).</b> An earlier version
/// resolved the scoped git seams from a fresh scope per operation, which is right for a page and wrong here:
/// a tool call arrives on the tool server's own request, which carries no window, so the scoped seams fell
/// through to a fallback nothing populates and every attempt in the installed product answered "there is no
/// git work tree here". <see cref="SessionGit"/> answers from the session's own record instead.
/// </para>
/// <para>
/// <b>A commit waits for the copy to settle first.</b> The agent writes into its contained copy and the sync
/// engine writes back to the host a debounce later, so a commit made the instant an edit finished would
/// record a tree that does not yet hold it. The wait is bounded, and running out of it is reported as its own
/// outcome rather than as a commit of whatever had landed.
/// </para>
/// </summary>
public sealed class SessionGitWork(
    SessionGit git,
    ISessionChangedFiles changed,
    ISessionBranches branches,
    ISessionWorkItems work,
    ITrackerResolver trackers,
    TimeProvider clock,
    ILogger<SessionGitWork>? logger = null,
    ISessionAttemptBases? attemptBases = null,
    AutopilotBuilderSessions? autopilotSessions = null)
{
    /// <summary>The prefix every attempt branch carries, so the set of them is findable and prunable.</summary>
    public const string BranchPrefix = "fravashi/";

    /// <summary>
    /// How long a commit waits for the session's edits to reach the host. Generous against the 300 ms debounce
    /// because a burst of edits re-queues on every bridge hiccup, and short enough that an agent is told rather
    /// than left hanging.
    /// </summary>
    public static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(15);

    /// <summary>What a commit that could not wait long enough says.</summary>
    public const string NotSettledMessage =
        "Nothing was committed: edits from this session had not all reached the host folder yet. Ask again in a moment.";

    private const string NoWorkTree =
        "This session's workspace has no git repository the studio can reach, so there is nothing to branch or commit.";

    private readonly ILogger _logger = logger ?? NullLogger<SessionGitWork>.Instance;

    /// <summary>
    /// Starts an attempt on its own branch, off whatever is checked out now, or off the branch
    /// <see cref="ISessionAttemptBases"/> names for this session when it names one (#315: an autopilot item
    /// starts from its run's base branch, never from the previous item's attempt branch the folder may still
    /// be on), and records THAT branch as the base delivery later reads. Starting one twice returns the branch
    /// already recorded rather than making a second: an attempt is one branch.
    /// </summary>
    public async Task<AttemptStart> StartAsync(AgentSessionId session, CancellationToken ct = default)
    {
        if (branches.For(session) is { } already)
        {
            return new AttemptStart(already.Branch);
        }

        if (git.For(session) is not { Git.IsAvailable: true } services)
        {
            return new AttemptStart(null, NoWorkTree);
        }

        try
        {
            var baseBranch = attemptBases?.For(session) is { Length: > 0 } given
                ? given
                : await services.Git.GetCurrentBranchAsync(ct).ConfigureAwait(false);
            var name = await NameAsync(session, ct).ConfigureAwait(false);

            await services.Git.CreateBranchAsync(name, baseBranch, checkout: true, ct).ConfigureAwait(false);
            branches.Record(session, new SessionBranch(name, baseBranch, clock.GetUtcNow()));

            return new AttemptStart(name);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            _logger.LogWarning(ex, "The studio could not branch an attempt for session {SessionId}.", session);
            return new AttemptStart(null, "The attempt could not be branched: " + ex.Message);
        }
    }

    /// <summary>
    /// Commits exactly what this session was approved to change, or the <paramref name="paths"/> the caller
    /// names when it names any (backlog 150's <c>git_commit</c>, where the agent says what belongs together),
    /// with a message that says which work item it was for and that an agent session produced it. It first
    /// waits for the session's edits to reach the host, and says so instead of committing when they do not.
    /// </summary>
    public async Task<AttemptCommit> CommitAsync(
        AgentSessionId session, string summary, IReadOnlyList<string>? paths = null, CancellationToken ct = default)
    {
        // #318: asked before anything is staged, so a refused message leaves the tree exactly as it was.
        if (CommitMessageRule.Problem(summary) is { } problem)
        {
            return new AttemptCommit(null, 0, Error: problem, Refused: true);
        }

        IReadOnlyList<string> chosen;
        if (paths is { Count: > 0 })
        {
            chosen = paths;
        }
        else
        {
            // Checked BEFORE chosen.Count == 0 below (commit review's finding): a worktree-owning session
            // whose own settle timed out, or whose status could not be read, answers an empty default here
            // purely because there was nothing left to fall back to reading, never because there is honestly
            // nothing to commit. Reading that as Nothing would reproduce #332's own symptom under a slow
            // bridge instead of fixing it.
            var defaults = await DefaultPathsAsync(session, ct).ConfigureAwait(false);
            if (defaults.NotSettled)
            {
                return new AttemptCommit(null, 0, Error: NotSettledMessage, NotSettled: true);
            }

            if (defaults.Error is { Length: > 0 } defaultsError)
            {
                return new AttemptCommit(null, 0, Error: defaultsError);
            }

            chosen = defaults.Paths;
        }

        if (chosen.Count == 0)
        {
            // Not an error. A session that read, discussed and changed nothing has nothing to record.
            return new AttemptCommit(null, 0, Nothing: true);
        }

        if (git.For(session) is not { Git.IsAvailable: true } services)
        {
            return new AttemptCommit(null, 0, Error: NoWorkTree);
        }

        if (await git.SettleAsync(session, SettleTimeout, ct).ConfigureAwait(false) == false)
        {
            return new AttemptCommit(null, 0, Error: NotSettledMessage, NotSettled: true);
        }

        var tree = services.Tree;
        try
        {
            await tree.StageAsync(chosen, ct).ConfigureAwait(false);

            // Asked rather than inferred from a failure message. A file can be approved for a change and
            // then left exactly as it was, and git then says nothing-to-commit on STDOUT with an empty
            // stderr, so reading the error text for it would answer "the commit failed" to something that
            // is not a failure at all. This was caught by a test that approved a file and touched nothing.
            var staged = await tree.GetStatusAsync(ct).ConfigureAwait(false);
            if (staged.StagedEntries.Count == 0)
            {
                return new AttemptCommit(null, 0, Nothing: true);
            }

            // #327: a Windows host's git never notices a permission bit at all, so a script an agent made
            // executable in its contained copy needs telling, explicitly, on the host side of this commit.
            if (git.CopyPathFor(session) is { Length: > 0 } copyPath)
            {
                await ExecutableBitStaging.MarkAsync(tree, copyPath, staged.StagedEntries, ct)
                    .ConfigureAwait(false);
            }

            var message = await MessageAsync(session, summary, ct).ConfigureAwait(false);
            var outcome = await tree.CommitAsync(message, ct).ConfigureAwait(false);

            return outcome.Succeeded
                ? new AttemptCommit(outcome.Sha, staged.StagedEntries.Count)
                : new AttemptCommit(null, 0, Error: outcome.Error);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            _logger.LogWarning(ex, "The studio could not commit session {SessionId}.", session);
            return new AttemptCommit(null, 0, Error: "The commit failed: " + ex.Message);
        }
    }

    /// <summary>The files this session was approved to change, as repository-relative paths.</summary>
    public IReadOnlyList<string> Paths(AgentSessionId session) => changed.Paths(session);

    /// <summary>
    /// What <see cref="CommitAsync"/> commits, and what the commit reviewer reviews (backlog 332), when the
    /// caller names no paths of its own: ordinarily <see cref="Paths"/>, the files this exact conversation was
    /// approved to change. For an autopilot builder running in the git worktree #308 made for its OWN item
    /// (<see cref="AutopilotBuilderSessions.OwnsWorktree"/>), it is every path uncommitted in that worktree
    /// right now (its own git status: staged, unstaged and untracked alike) JOINED WITH <see cref="Paths"/>,
    /// never in its place, because the worktree belongs to the item, never to whichever conversation happens
    /// to be building it, so a resumed item's fresh conversation still owns what an earlier, stopped one left
    /// there, AND this exact conversation's own latest edit must never drop out of the default just because
    /// its own write-back had not landed on the host at the moment the status below was read (the join is what
    /// still answers for it even then). Settled FIRST, before the status read, for the same reason: a snapshot
    /// taken before the session's own writes have reached the host folder could miss them outright.
    /// <para>
    /// A settle that genuinely times out, an unreachable repository, or a status read that fails, is
    /// reported as such (<see cref="SessionDefaultPaths.NotSettled"/>/<see cref="SessionDefaultPaths.Error"/>)
    /// rather than silently answered as <see cref="Paths"/> alone: for a worktree-owning session whose own
    /// approved changes are empty (exactly #332's own shape, a resumed item with only a leftover file),
    /// falling back silently would answer an empty default, which both callers read as "nothing to commit",
    /// reproducing #332's symptom under a slow or unreachable bridge instead of fixing it. A caller MUST
    /// check these two fields before reading <see cref="SessionDefaultPaths.Paths"/> as the honest default. A
    /// FAILED status read is told apart from a genuinely CLEAN one by <see cref="GitWorkingStatus.Branch"/>
    /// being empty (<see cref="IGitWorkingTreeService.GetStatusAsync"/>'s own documented tell: a real read
    /// always carries its branch, even for a clean tree with nothing to report, because it answers
    /// <see cref="GitWorkingStatus.Empty"/> rather than throwing when git itself fails or is unavailable,
    /// the same signal <see cref="Fravashi.Infrastructure.Autopilot.GitAutopilotWorkspaceReturn"/> already
    /// reads for the identical reason).
    /// </para>
    /// <para>
    /// Falls back to <see cref="Paths"/> alone, with neither flag set, only when the worktree genuinely has
    /// nothing uncommitted in it once settled: there is then nothing worktree-specific to add, and the
    /// ordinary accounting is the honest answer. This is the ONE place both <c>attempt_commit</c>/
    /// <c>git_commit</c> and the commit reviewer ask, so neither can ever read a different file set for the
    /// same session.
    /// </para>
    /// </summary>
    public async Task<SessionDefaultPaths> DefaultPathsAsync(AgentSessionId session, CancellationToken ct = default)
    {
        if (autopilotSessions?.OwnsWorktree(session) != true)
        {
            return new SessionDefaultPaths(Paths(session));
        }

        if (git.For(session) is not { Git.IsAvailable: true } services)
        {
            return new SessionDefaultPaths(Paths(session), Error: NoWorkTree);
        }

        try
        {
            var settled = await git.SettleAsync(session, SettleTimeout, ct).ConfigureAwait(false);
            if (settled == false)
            {
                return new SessionDefaultPaths(Paths(session), NotSettled: true);
            }

            var status = await services.Tree.GetStatusAsync(ct).ConfigureAwait(false);
            if (status.Branch.Length == 0)
            {
                return new SessionDefaultPaths(Paths(session), Error: "The worktree's own status could not be read.");
            }

            return new SessionDefaultPaths(
                status.StagedEntries.Select(e => e.Path)
                    .Concat(status.UnstagedEntries.Select(e => e.Path))
                    .Concat(Paths(session))
                    .Distinct(StringComparer.Ordinal)
                    .ToList());
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or HttpRequestException)
        {
            _logger.LogWarning(ex, "Could not read the worktree's own status for session {SessionId}.", session);
            return new SessionDefaultPaths(Paths(session), Error: "The worktree's own status could not be read: " + ex.Message);
        }
    }

    /// <summary>
    /// A branch name that says what it is for: the work items the session is doing, or the session itself
    /// when it names none. Kept to characters git is content with on every platform.
    /// </summary>
    private async Task<string> NameAsync(AgentSessionId session, CancellationToken ct)
    {
        var numbers = new List<int>();
        foreach (var entry in work.For(session))
        {
            // Read from the project the link was made in (backlog 121), which the link row carries.
            if (await trackers.For(entry.Project).GetAsync(entry.Item, ct).ConfigureAwait(false) is { } item)
            {
                numbers.Add(item.Number);
            }
        }

        var suffix = session.Value.ToString("N")[..8];
        return numbers.Count == 0
            ? BranchPrefix + "session-" + suffix
            : BranchPrefix + "item-"
                + string.Join("-", numbers.OrderBy(n => n).Select(n => n.ToString(CultureInfo.InvariantCulture)))
                + "-" + suffix;
    }

    /// <summary>
    /// The commit message. Honest attribution means saying what the change was for and that a Fravashi
    /// agent session produced it; the AUTHOR stays the host git identity, because that is who is
    /// accountable for what lands in their repository. No vendor is named: the role is what matters and
    /// the vendor is what changes.
    /// </summary>
    private async Task<string> MessageAsync(AgentSessionId session, string summary, CancellationToken ct)
    {
        // A trailer the message already carries is taken out first (#318), so the commit has exactly one and it
        // names this session; a message without one is used as it always was.
        var text = new StringBuilder(CommitMessageRule.WithoutStudioTrailer(summary.Trim()));
        var titles = new List<string>();
        foreach (var entry in work.For(session))
        {
            if (await trackers.For(entry.Project).GetAsync(entry.Item, ct).ConfigureAwait(false) is { } item)
            {
                titles.Add("#" + item.Number.ToString(CultureInfo.InvariantCulture) + " " + item.Title);
            }
        }

        if (titles.Count > 0)
        {
            text.Append("\n\n").Append(CommitMessageRule.WorkItemsHeading).Append('\n');
            foreach (var title in titles)
            {
                text.Append("- ").Append(title).Append('\n');
            }
        }

        // A BLANK line, not just a newline: git's subject runs to the first blank line, so a single one
        // would fold this into the subject and every attempt's one-line summary would carry it. Caught by
        // a test that read the commit's subject back.
        text.Append("\n\n").Append(CommitMessageRule.ProducedByPrefix).Append(" (")
            .Append(session.Value.ToString("N")[..8])
            .Append(").");

        return text.ToString();
    }
}
