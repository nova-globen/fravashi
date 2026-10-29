namespace Fravashi.Domain.PullRequests;

/// <summary>The Git host that backs the repository's <c>origin</c> remote.</summary>
public enum PullRequestProvider
{
    None,
    GitHub,
    AzureDevOps,

    /// <summary>A self-hosted Forgejo instance (locked decision 10); its host is never URL-guessable,
    /// it is whatever the onboarding source step says it is.</summary>
    Forgejo,
}

public enum PullRequestState
{
    Open,
    Closed,
    All,
}

public enum MergeMethod
{
    Merge,
    Squash,
    Rebase,
}

/// <summary>A pull request summary as listed from the provider.</summary>
public sealed record PullRequest(
    int Number,
    string Title,
    string Author,
    string SourceBranch,
    string TargetBranch,
    string State,
    Uri? Url,
    DateTimeOffset Created);

public sealed record PullRequestComment(string Author, string Body, DateTimeOffset When);

/// <summary>Full detail of one pull request: summary, description, and discussion comments.</summary>
public sealed record PullRequestDetail(PullRequest Summary, string Body, IReadOnlyList<PullRequestComment> Comments);

/// <summary>A request to open a pull request from <paramref name="SourceBranch"/> into <paramref name="TargetBranch"/>.</summary>
public sealed record CreatePullRequestRequest(string Title, string Body, string SourceBranch, string TargetBranch);

/// <summary>The verdict of a review submission (the PR lifecycle's approve / reject actions).</summary>
public enum ReviewVerdict
{
    Approve,
    RequestChanges,
    Comment,
}

/// <summary>
/// One review on a pull request. <see cref="State"/> is the provider's own wording (e.g. APPROVED,
/// REQUEST_CHANGES, approved with suggestions) so the UI reports what the host actually recorded.
/// </summary>
public sealed record PullRequestReview(string Author, string State, string Body, DateTimeOffset? When);
