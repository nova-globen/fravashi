using Fravashi.Domain.Git;

namespace Fravashi.Application.Git;

/// <summary>
/// The Git page's working-tree surface (plan phase 4): status, staging, and committing over the
/// configured clone. Mutations serialize through the repository write gate (ADR 0016), the same
/// gate editor saves take, so a commit can never interleave with a save. Commit identity comes from
/// the onboarding git step's settings (<c>Git:AuthorName</c>/<c>Git:AuthorEmail</c>), passed as
/// per-command environment, never written into the clone's config (locked decision 9).
/// </summary>
public interface IGitWorkingTreeService
{
    Task<GitWorkingStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    Task StageAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    Task UnstageAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    Task<GitCommitOutcome> CommitAsync(string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks each path executable in the host index (<c>git update-index --chmod=+x</c>), under the same
    /// write gate every other mutation here takes (#327). Throws when any one fails (an index.lock from a
    /// concurrent writer is the realistic case), so a caller never reports a commit as succeeded while a
    /// file it meant to mark executable silently stayed <c>100644</c>.
    /// </summary>
    Task MarkExecutableAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>
    /// The two sides for a Monaco diff of one entry: staged compares HEAD to the index, unstaged
    /// compares the index to the working tree (VS Code's semantics). Missing sides come back empty
    /// (an added file has no left side, a deleted file no right side).
    /// </summary>
    Task<(string Original, string Modified)> GetWorkingDiffAsync(
        string path, bool staged, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stash changes (untracked included). Null <paramref name="paths"/> stashes everything. This is
    /// also the studio's UNDO for working-tree changes: a path-scoped stash with a
    /// "change backup: ..." message reverts the change while keeping it recoverable from the list.
    /// </summary>
    Task<GitCommitOutcome> StashAsync(string message, IReadOnlyList<string>? paths = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitStash>> GetStashesAsync(CancellationToken cancellationToken = default);

    /// <summary>Apply a stash and drop it from the list.</summary>
    Task<GitCommitOutcome> StashPopAsync(int index, CancellationToken cancellationToken = default);

    /// <summary>Apply a stash but keep it in the list.</summary>
    Task<GitCommitOutcome> StashApplyAsync(int index, CancellationToken cancellationToken = default);
}

/// <summary>Settings keys for the commit identity (asked in the onboarding git step, editable in Settings).</summary>
public static class GitIdentityKeys
{
    public const string AuthorName = "Git:AuthorName";
    public const string AuthorEmail = "Git:AuthorEmail";
}
