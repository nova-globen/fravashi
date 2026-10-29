using Fravashi.Domain.Git;
using Fravashi.Domain.Repository;

namespace Fravashi.Application.Git;

/// <summary>
/// Git access over the single local repository (<c>Repository:Root</c>) via the git command line. Browsing
/// (tree, file, log, diff) is read-only and non-destructive: it reads at an arbitrary ref without touching
/// the working tree. <see cref="CheckoutAsync"/> and <see cref="CreateBranchAsync"/> are the explicit
/// mutating operations. The remote is never contacted here (that is the PR service's job).
/// </summary>
public interface IGitService
{
    /// <summary>True when <c>Repository:Root</c> is inside a git work tree.</summary>
    bool IsAvailable { get; }

    /// <summary>Why git is not available, in words, when there is a specific reason (<see cref="IGitCommandRunner.UnavailableReason"/>); else null.</summary>
    string? UnavailableReason => null;

    Task<string> GetCurrentBranchAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitBranch>> GetBranchesAsync(CancellationToken cancellationToken = default);

    /// <summary>Switch the working tree to <paramref name="branch"/> (destructive, an explicit user action).</summary>
    Task CheckoutAsync(string branch, CancellationToken cancellationToken = default);

    /// <summary>Create <paramref name="name"/> from <paramref name="baseBranch"/>, optionally checking it out.</summary>
    Task CreateBranchAsync(string name, string baseBranch, bool checkout, CancellationToken cancellationToken = default);

    /// <summary>List one directory level of the tree at <paramref name="reference"/> (non-destructive).</summary>
    Task<IReadOnlyList<RepositoryNode>> ListTreeAsync(string reference, string path, CancellationToken cancellationToken = default);

    /// <summary>Read a file's content at <paramref name="reference"/> (non-destructive).</summary>
    Task<RepositoryFile?> ReadFileAsync(string reference, string path, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitCommit>> GetCommitsAsync(string reference, int max = 50, string? path = null, CancellationToken cancellationToken = default);

    Task<GitCommitDetail?> GetCommitAsync(string sha, CancellationToken cancellationToken = default);

    /// <summary>Unified diff for a commit (optionally scoped to a single <paramref name="path"/>).</summary>
    Task<string> GetDiffAsync(string sha, string? path = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// The files that differ between <paramref name="headRef"/> and its merge base with
    /// <paramref name="baseRef"/> (triple-dot semantics, what a PR review shows). Throws with git's
    /// own message when a ref is unknown locally (fetch first).
    /// </summary>
    Task<IReadOnlyList<GitFileChange>> GetDiffFilesAsync(string baseRef, string headRef, CancellationToken cancellationToken = default);

    /// <summary>The merge base of two refs, or null when none exists locally.</summary>
    Task<string?> GetMergeBaseAsync(string first, string second, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitCommit>> GetFileHistoryAsync(string path, string reference, int max = 50, CancellationToken cancellationToken = default);

    /// <summary>The <c>origin</c> remote URL (for PR-provider detection), or null when there is none.</summary>
    Task<string?> GetRemoteUrlAsync(CancellationToken cancellationToken = default);

    /// <summary>Branches on the remotes (e.g. <c>origin/feature/x</c>), excluding HEAD pointers.</summary>
    Task<IReadOnlyList<GitRemoteBranch>> GetRemoteBranchesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Check out <paramref name="branch"/> as a local branch tracking it, creating the local branch when
    /// it does not exist yet. Takes the parsed branch rather than a name because the remote and the branch
    /// cannot be recovered from a single string: a branch name may contain slashes of its own.
    /// </summary>
    Task CheckoutTrackingAsync(GitRemoteBranch branch, CancellationToken cancellationToken = default);

    /// <summary>
    /// The repository's tags, newest first (work item #367, read-only). The default answers none, so a double that
    /// has no tags to offer need not say so.
    /// </summary>
    Task<IReadOnlyList<GitTag>> GetTagsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GitTag>>([]);

    /// <summary>
    /// Creates the annotated tag <paramref name="name"/> on <paramref name="commit"/> with <paramref name="message"/> as
    /// its annotation (work item #368, a release's tag). It never moves or replaces a tag: a name that already exists is
    /// refused. The name must be a release version (<c>ReleaseVersion</c>) and the commit a hexadecimal object name;
    /// anything else is refused before git runs. Nothing is pushed here. The default refuses, so a double that cannot
    /// create tags need not say so.
    /// </summary>
    Task CreateTagAsync(string name, string commit, string message, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This git service cannot create tags.");

    /// <summary>The commit graph rows (parents + decorating refs) for the lane renderer, newest first.</summary>
    Task<IReadOnlyList<GitGraphCommit>> GetGraphAsync(int max = 150, CancellationToken cancellationToken = default);

    /// <summary>Who last changed a 1-based line of a working-tree file, or null (untracked/uncommitted line).</summary>
    Task<GitLineBlame?> GetLineBlameAsync(string path, int line, CancellationToken cancellationToken = default);
}
