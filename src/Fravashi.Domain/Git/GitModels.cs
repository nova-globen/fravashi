namespace Fravashi.Domain.Git;

/// <summary>
/// A local branch; <see cref="IsCurrent"/> marks the checked-out branch (HEAD). Sync status against
/// its upstream: <see cref="Upstream"/> null means never published; ahead/behind of 0/0 with an
/// upstream means synced.
/// </summary>
public sealed record GitBranch(
    string Name,
    bool IsCurrent,
    string? Upstream = null,
    int Ahead = 0,
    int Behind = 0)
{
    public bool IsPublished => Upstream is not null;
}

/// <summary>A branch on a remote (e.g. <c>origin/feature/x</c>) with its short local-style name.</summary>
public sealed record GitRemoteBranch(string FullName, string Remote, string Name);

/// <summary>
/// A tag (work item #367): its name, the commit it points at (peeled through an annotated tag), when it was made, and
/// an annotated tag's subject (empty for a lightweight tag).
/// </summary>
public sealed record GitTag(string Name, string Sha, DateTimeOffset When, string Subject);

/// <summary>One entry of <c>git stash list</c>.</summary>
public sealed record GitStash(int Index, string Message, string? Branch);

/// <summary>A commit in the graph: parents wire the lanes, refs label the tips.</summary>
public sealed record GitGraphCommit(
    string Sha,
    string ShortSha,
    string Subject,
    string Author,
    DateTimeOffset When,
    IReadOnlyList<string> Parents,
    IReadOnlyList<string> Refs);

/// <summary>Who last touched a line (git blame), plus a PR number when the message references one.</summary>
public sealed record GitLineBlame(
    string Sha,
    string ShortSha,
    string Author,
    DateTimeOffset When,
    string Subject,
    int? PullRequestNumber);

/// <summary>A commit summary from <c>git log</c>.</summary>
public sealed record GitCommit(string Sha, string ShortSha, string Subject, string Author, DateTimeOffset When);

/// <summary>How a file changed in a commit.</summary>
public enum GitChangeKind
{
    Added,
    Modified,
    Deleted,
    Renamed,
    Copied,
    Unknown,
}

/// <summary>One changed file in a commit (<c>git show --name-status</c>).</summary>
public sealed record GitFileChange(string Path, GitChangeKind Kind, string? OldPath = null);

/// <summary>Full detail of a commit: its summary, body, and changed files.</summary>
public sealed record GitCommitDetail(GitCommit Commit, string Body, IReadOnlyList<GitFileChange> Files);

/// <summary>State of one working-tree entry (from <c>git status --porcelain=v2</c>).</summary>
public enum GitEntryState
{
    Modified,
    Added,
    Deleted,
    Renamed,
    Untracked,
    Conflicted,
    Unknown,
}

/// <summary>One file in the working-tree status; <see cref="Staged"/> tells which side it sits on.</summary>
public sealed record GitWorkingEntry(string Path, GitEntryState State, bool Staged, string? OldPath = null);

/// <summary>The working tree at a glance: branch, upstream tracking, and both change lists.</summary>
public sealed record GitWorkingStatus(
    string Branch,
    string? Upstream,
    int Ahead,
    int Behind,
    IReadOnlyList<GitWorkingEntry> StagedEntries,
    IReadOnlyList<GitWorkingEntry> UnstagedEntries)
{
    public static GitWorkingStatus Empty { get; } = new(string.Empty, null, 0, 0, [], []);
}

/// <summary>Outcome of a commit attempt; <see cref="Error"/> is user-facing on failure.</summary>
public sealed record GitCommitOutcome(bool Succeeded, string? Error = null, string? Sha = null);
