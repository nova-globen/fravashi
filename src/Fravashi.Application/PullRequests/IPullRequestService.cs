using Fravashi.Domain.PullRequests;

namespace Fravashi.Application.PullRequests;

/// <summary>
/// Pull-request management against the repository's <c>origin</c> remote (plan phase 6). GitHub and
/// Azure DevOps are auto-detected from the remote URL; Forgejo is whatever host the onboarding source
/// step declared (locked decision 10). Auth rides the stored onboarding source credential. This and
/// <see cref="Git.IGitRemoteService"/> are the only studio surfaces that contact the remote.
/// Reports <see cref="IsConfiguredAsync"/> = false when the provider is unknown or no token is stored.
/// </summary>
public interface IPullRequestService
{
    Task<PullRequestProvider> GetProviderAsync(CancellationToken cancellationToken = default);

    Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PullRequest>> ListAsync(PullRequestState state, CancellationToken cancellationToken = default);

    Task<PullRequestDetail?> GetAsync(int number, CancellationToken cancellationToken = default);

    Task<PullRequest> CreateAsync(CreatePullRequestRequest request, CancellationToken cancellationToken = default);

    Task MergeAsync(int number, MergeMethod method, CancellationToken cancellationToken = default);

    /// <summary>The reviews recorded on a pull request (approvals, change requests, review comments).</summary>
    Task<IReadOnlyList<PullRequestReview>> GetReviewsAsync(int number, CancellationToken cancellationToken = default);

    /// <summary>
    /// Submit a review with the given verdict and optional comment. Azure DevOps review votes are
    /// deferred until the ADO PAT work (an honest error, never a silent no-op).
    /// </summary>
    Task SubmitReviewAsync(int number, ReviewVerdict verdict, string body, CancellationToken cancellationToken = default);

    /// <summary>Close (GitHub/Forgejo) or abandon (Azure DevOps) a pull request without merging.</summary>
    Task CloseAsync(int number, CancellationToken cancellationToken = default);
}
