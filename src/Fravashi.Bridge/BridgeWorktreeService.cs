using System.Globalization;
using Fravashi.Domain;

namespace Fravashi.Bridge;

/// <summary>
/// A worktree the bridge made for one work item, already registered as its own workspace.
/// <paramref name="BaseBranch"/> is the base that was asked for; <paramref name="BranchReused"/> says
/// whether the worktree actually starts there. When true (the worktree was already listed, or its branch
/// already existed from an earlier create), the worktree carries whatever is already on that branch, which
/// may not be <paramref name="BaseBranch"/>'s own commit.
/// </summary>
public sealed record BridgeWorktree(
    BridgeWorkspace Workspace, string Branch, string BaseBranch, bool BranchReused = false);

/// <summary>What asking to prune a worktree did.</summary>
public enum BridgeWorktreePruneOutcome
{
    /// <summary>It was clean, so it is gone: removed from disk and unregistered.</summary>
    Removed,

    /// <summary>Nothing was there to prune. Not an error: the item may never have reached this point.</summary>
    NotFound,

    /// <summary>It has uncommitted changes, so it was left exactly as it is.</summary>
    Dirty,
}

/// <summary>The outcome of a prune attempt, with words to say when it did not remove anything.</summary>
public sealed record BridgeWorktreePruneResult(BridgeWorktreePruneOutcome Outcome, string? Detail = null)
{
    public static readonly BridgeWorktreePruneResult NotFound = new(BridgeWorktreePruneOutcome.NotFound);

    public static BridgeWorktreePruneResult Removed() => new(BridgeWorktreePruneOutcome.Removed);

    public static BridgeWorktreePruneResult DirtyWith(string detail) =>
        new(BridgeWorktreePruneOutcome.Dirty, detail);
}

/// <summary>
/// A folder already stands where the worktree would go, and git does not know it as one of this
/// repository's worktrees: the product never creates a worktree on top of something it did not make.
/// </summary>
public sealed class WorktreeTargetConflictException(string path)
    : IOException($"A folder already exists at '{path}' and it is not a worktree this product made.")
{
    public string Path { get; } = path;
}

/// <summary>
/// Creates and removes a git WORKTREE per concurrent session (ADR 0031 decision 15), a closed operation
/// the way <see cref="BridgeGitService.CloneAsync"/> is: the only inputs a caller supplies are a registered
/// workspace id, a work item NUMBER and a base branch name the host already resolved, never a path or free
/// text an agent wrote. The target folder and the branch name are both derived here, not accepted from
/// outside.
/// <para>
/// A worktree lands in a sibling folder named after the repository
/// (<c>&lt;repo&gt;.fravashi-worktrees/&lt;item&gt;</c>), on a new branch made from the named base branch (or,
/// when the item's own branch already exists, reusing it as it stands; see <see cref="BridgeWorktree"/>),
/// and is registered with the same <see cref="WorkspaceRegistry"/> any folder opened by hand uses, so every
/// existing file/git/search/watch endpoint serves it without knowing it is different from a first-class
/// clone. The person's own checkout (the registered repository root itself) is never touched: every git
/// command this class runs against the repository id is a read (<c>worktree list</c>) or a worktree
/// operation, never a checkout of the root.
/// </para>
/// </summary>
public sealed class BridgeWorktreeService(WorkspaceRegistry registry, string executable = "git")
{
    /// <summary>The sibling folder's suffix: <c>&lt;repo-folder-name&gt;</c> plus this.</summary>
    public const string ContainerSuffix = ".fravashi-worktrees";

    /// <summary>The prefix every worktree's own branch carries, distinct from an attempt branch's own naming.</summary>
    public const string BranchPrefix = "fravashi/worktree/item-";

    private readonly BridgeGitService _git = new(registry, executable);

    /// <summary>The deterministic branch name for one item's worktree.</summary>
    public static string BranchFor(int item) => BranchPrefix + item.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Creates (or, asked again for the same item, finds) the worktree. Never touches a folder that exists
    /// but that git does not already know as this repository's worktree at the computed path.
    /// </summary>
    public async Task<BridgeWorktree> CreateAsync(
        string repositoryId, int item, string baseBranch, CancellationToken ct)
    {
        if (item <= 0)
        {
            throw new ArgumentException("A work item number is required.", nameof(item));
        }

        // The bridge runs whatever it is handed (CLAUDE.md's own rule for every git tool): never trust that
        // a caller already resolved a safe value. A leading hyphen would read as a flag to git itself, so
        // it is refused before git ever sees it; everything else git itself may still reject is asked,
        // never guessed at here.
        if (!IsAcceptableBranchArgument(baseBranch))
        {
            throw new ArgumentException(
                "A base branch is required, with no leading hyphen and no whitespace or control characters.",
                nameof(baseBranch));
        }

        var repository = registry.TryGet(repositoryId)
            ?? throw new KeyNotFoundException($"Unknown workspace '{repositoryId}'.");

        if (!await IsValidRefNameAsync(repositoryId, baseBranch, ct).ConfigureAwait(false))
        {
            throw new ArgumentException($"'{baseBranch}' is not a branch name git accepts.", nameof(baseBranch));
        }

        var target = TargetPath(repository, item);
        var branch = BranchFor(item);

        var worktrees = await ListWorktreesAsync(repositoryId, ct).ConfigureAwait(false);

        // The bridge release: a worktree made before the internal rename sits under the former sibling folder. It is still
        // found and used as it stands (never moved under a running git); a new one goes under the new folder.
        var formerTarget = TargetPath(repository, item, FormerNames.WorktreeContainerSuffix);
        if (worktrees.FirstOrDefault(w => PathsEqual(w.Path, formerTarget)) is { } former && Directory.Exists(formerTarget))
        {
            return new BridgeWorktree(
                registry.Register(formerTarget), former.Branch ?? FormerBranchFor(item), baseBranch, BranchReused: true);
        }

        var known = worktrees.FirstOrDefault(w => PathsEqual(w.Path, target));
        if (known is not null)
        {
            if (Directory.Exists(target))
            {
                // Already made: a retry, or asking again for a run already under way. Register() is
                // idempotent per canonical path, so this never makes a second worktree or a second branch.
                return new BridgeWorktree(
                    registry.Register(target), known.Branch ?? branch, baseBranch, BranchReused: true);
            }

            // git still lists it as a worktree, but the folder itself is gone (removed by hand): clear
            // git's own record and the stale grant, then fall through to remake it below, which reuses
            // whatever branch it finds there, the same as PruneAsync's own "removed by hand" case.
            await _git.RunAsync(repositoryId, ["worktree", "prune"], ct).ConfigureAwait(false);
            registry.Unregister(WorkspaceRegistry.IdFor(target));
        }
        else if (Directory.Exists(target) || File.Exists(target))
        {
            throw new WorktreeTargetConflictException(target);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        // A previous attempt for this item may have been pruned already: `git worktree remove` deletes
        // the worktree but keeps its branch, and `worktree add -b` refuses a branch that already exists.
        // Reusing the existing branch, rather than failing or silently deleting and remaking it, is what
        // keeps a retry a retry: it carries whatever is already on it (this product's own prune only ever
        // keeps a branch that was clean when it was pruned, but the branch could equally pre-exist for any
        // other reason), which BranchReused on the result says plainly rather than implying it started at
        // baseBranch's own commit.
        var reuseBranch = await BranchExistsAsync(repositoryId, branch, ct).ConfigureAwait(false);
        if (!reuseBranch && await BranchExistsAsync(repositoryId, FormerBranchFor(item), ct).ConfigureAwait(false))
        {
            // The item's branch from before the internal rename carries its earlier work: reused, not remade.
            branch = FormerBranchFor(item);
            reuseBranch = true;
        }

        var addArgs = reuseBranch
            ? (IReadOnlyList<string>)["worktree", "add", target, branch]
            : ["worktree", "add", "-b", branch, target, baseBranch];
        var result = await _git.RunAsync(repositoryId, addArgs, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"git worktree add failed: {result.Stderr.Trim()}");
        }

        return new BridgeWorktree(registry.Register(target), branch, baseBranch, BranchReused: reuseBranch);
    }

    /// <summary>
    /// Removes a clean worktree and drops its grant. Leaves a dirty one exactly as it is and says why;
    /// never <c>--force</c>s the removal, so git's own refusal stands as a second check behind the explicit
    /// status read here. Nothing to prune answers <see cref="BridgeWorktreePruneOutcome.NotFound"/>, not an
    /// error.
    /// </summary>
    public async Task<BridgeWorktreePruneResult> PruneAsync(string repositoryId, int item, CancellationToken ct)
    {
        var repository = registry.TryGet(repositoryId)
            ?? throw new KeyNotFoundException($"Unknown workspace '{repositoryId}'.");
        var target = TargetPath(repository, item);

        var worktrees = await ListWorktreesAsync(repositoryId, ct).ConfigureAwait(false);
        if (!worktrees.Any(w => PathsEqual(w.Path, target)))
        {
            // The bridge release: one made before the internal rename, under the former sibling folder.
            target = TargetPath(repository, item, FormerNames.WorktreeContainerSuffix);
            if (!worktrees.Any(w => PathsEqual(w.Path, target)))
            {
                return BridgeWorktreePruneResult.NotFound;
            }
        }

        if (!Directory.Exists(target))
        {
            // Git still lists it (as "prunable" in its own porcelain output) even though the folder is
            // gone, for example removed by hand. Registering it to find its id would throw for a folder
            // that is not there, so the id is computed instead, and git's own record is cleared too.
            await _git.RunAsync(repositoryId, ["worktree", "prune"], ct).ConfigureAwait(false);
            registry.Unregister(WorkspaceRegistry.IdFor(target));
            return BridgeWorktreePruneResult.Removed();
        }

        var workspace = registry.Register(target);
        var status = await _git.RunAsync(workspace.Id, ["status", "--porcelain"], ct).ConfigureAwait(false);
        if (status.ExitCode != 0)
        {
            throw new InvalidOperationException($"git status failed: {status.Stderr.Trim()}");
        }

        if (status.Stdout.Trim().Length > 0)
        {
            return BridgeWorktreePruneResult.DirtyWith(
                "The worktree has uncommitted changes, so it was left as it is.");
        }

        var remove = await _git.RunAsync(repositoryId, ["worktree", "remove", target], ct).ConfigureAwait(false);
        if (remove.ExitCode != 0)
        {
            throw new InvalidOperationException($"git worktree remove failed: {remove.Stderr.Trim()}");
        }

        registry.Unregister(workspace.Id);
        return BridgeWorktreePruneResult.Removed();
    }

    /// <summary>The former branch of an item's worktree, from before the internal rename (the bridge release).</summary>
    private static string FormerBranchFor(int item) =>
        FormerNames.WorktreeBranchPrefix + item.ToString(CultureInfo.InvariantCulture);

    private static string TargetPath(BridgeWorkspace repository, int item, string suffix = ContainerSuffix)
    {
        var parent = Path.GetDirectoryName(repository.RootPath)
            ?? throw new InvalidOperationException(
                $"'{repository.RootPath}' has no parent folder to hold a worktree.");
        return Path.Combine(
            parent, repository.Name + suffix, item.ToString(CultureInfo.InvariantCulture));
    }

    private async Task<bool> BranchExistsAsync(string repositoryId, string branch, CancellationToken ct)
    {
        var result = await _git.RunAsync(
            repositoryId, ["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}"], ct)
            .ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    /// <summary>Whether this text may be handed to git as a positional revision word at all (no leading
    /// hyphen, which git would read as an option, and nothing whitespace or control can hide inside).</summary>
    private static bool IsAcceptableBranchArgument(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value[0] == '-')
        {
            return false;
        }

        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether git itself accepts this as a branch name, asked rather than guessed at. A git that
    /// could not even run (backlog 266) is a failure, never read as "not a valid name".</summary>
    private async Task<bool> IsValidRefNameAsync(string repositoryId, string name, CancellationToken ct)
    {
        var result = await _git.RunAsync(repositoryId, ["check-ref-format", "--branch", name], ct)
            .ConfigureAwait(false);
        if (result.ExitCode == BridgeGitResult.GitMissingExitCode)
        {
            throw new InvalidOperationException(result.Stderr);
        }

        return result.ExitCode == 0;
    }

    /// <summary>
    /// The repository's worktrees, asked of git itself. A non-zero exit (git missing, the folder is not a
    /// git repository, anything else) is a failure, never read as "no worktrees": that misreading would
    /// make <see cref="PruneAsync"/> answer NotFound, and <see cref="CreateAsync"/> carry on, for a
    /// registered repository git simply could not be asked about.
    /// </summary>
    private async Task<IReadOnlyList<WorktreeEntry>> ListWorktreesAsync(string repositoryId, CancellationToken ct)
    {
        var result = await _git.RunAsync(repositoryId, ["worktree", "list", "--porcelain"], ct)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var reason = result.Stderr.Trim();
            throw new InvalidOperationException(
                reason.Length > 0 ? reason : $"git worktree list exited with {result.ExitCode}.");
        }

        return ParsePorcelain(result.Stdout);
    }

    private static bool PathsEqual(string a, string b) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
        StringComparison.OrdinalIgnoreCase);

    private sealed record WorktreeEntry(string Path, string? Branch);

    /// <summary>
    /// Parses <c>git worktree list --porcelain</c>: blocks separated by a blank line, a <c>worktree &lt;path&gt;</c>
    /// line and (absent only for a detached HEAD) a <c>branch refs/heads/&lt;name&gt;</c> line.
    /// </summary>
    private static IReadOnlyList<WorktreeEntry> ParsePorcelain(string stdout)
    {
        var entries = new List<WorktreeEntry>();
        string? path = null;
        string? branch = null;
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                if (path is not null)
                {
                    entries.Add(new WorktreeEntry(path, branch));
                }

                path = null;
                branch = null;
                continue;
            }

            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                path = line["worktree ".Length..];
            }
            else if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                var value = line["branch ".Length..];
                branch = value.StartsWith("refs/heads/", StringComparison.Ordinal)
                    ? value["refs/heads/".Length..]
                    : value;
            }
        }

        if (path is not null)
        {
            entries.Add(new WorktreeEntry(path, branch));
        }

        return entries;
    }
}
