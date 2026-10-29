namespace Fravashi.Application.Git;

/// <summary>A configured remote (from <c>git remote -v</c>).</summary>
public sealed record GitRemote(string Name, string FetchUrl, string? PushUrl = null);

/// <summary>What kind of failure a remote operation met (backlog 231).</summary>
public enum GitSyncFailure
{
    /// <summary>It succeeded.</summary>
    None,

    /// <summary>The network failed and nothing changed on origin; trying again is safe.</summary>
    Network,

    /// <summary>
    /// A push was sent and no answer came back, and origin could not be asked whether it arrived. Never called
    /// failed, never retried: <see cref="IGitRemoteService.ComparePushAsync"/> settles it.
    /// </summary>
    OutcomeUnknown,

    /// <summary>Origin answered and refused (<see cref="GitSyncOutcome.Refusal"/> says how).</summary>
    Refused,

    /// <summary>Anything else git or the bridge reported.</summary>
    Other,
}

/// <summary>A push whose answer was lost: which ref, and the object it should now name on origin.</summary>
/// <param name="RemoteRef">The full ref on origin, <c>refs/heads/x</c> or <c>refs/tags/x</c>.</param>
/// <param name="Sha">The object pushed (a commit for a branch, the tag object for an annotated tag).</param>
/// <param name="UpstreamBranch">The local branch whose upstream the push would have set, or null.</param>
public sealed record GitPendingPush(string RemoteRef, string Sha, string? UpstreamBranch = null);

/// <summary>Outcome of a network sync operation; <see cref="Error"/> is user-facing on failure.</summary>
public sealed record GitSyncOutcome(bool Succeeded, string? Error = null, string? Detail = null)
{
    private readonly GitSyncFailure? _failure;

    /// <summary>What kind of failure it was; <see cref="GitSyncFailure.Other"/> when a failure says no more.</summary>
    public GitSyncFailure Failure
    {
        get => _failure ?? (Succeeded ? GitSyncFailure.None : GitSyncFailure.Other);
        init => _failure = value;
    }

    /// <summary>Origin's refusal, for <see cref="GitSyncFailure.Refused"/>.</summary>
    public GitRefusal Refusal { get; init; }

    /// <summary>True when doing the same thing again cannot do anything twice (the page's Retry, the tool's retry_safe).</summary>
    public bool RetrySafe { get; init; }

    /// <summary>The push to compare, for <see cref="GitSyncFailure.OutcomeUnknown"/>.</summary>
    public GitPendingPush? Pending { get; init; }
}

/// <summary>
/// Network-side git operations for the Git page (plan phase 5): fetch, pull, push, and remote
/// listing. The onboarding source credential travels exactly like the clone service's does, as an
/// <c>http.extraHeader</c> through <c>GIT_CONFIG_*</c> env vars, never on the command line, in the
/// URL, or on disk. Pull is fast-forward only (a diverged working copy is never merged silently,
/// the same stance the clone service takes); it also holds the repository write gate because it
/// rewrites the working tree. Fetch and push touch no tracked files and skip the gate.
/// <para>
/// A failure is worded (backlog 231): a network failure says so and is safe to retry, a refusal says so and is
/// not, and a push whose answer was lost is compared with origin rather than called failed.
/// </para>
/// </summary>
public interface IGitRemoteService
{
    Task<IReadOnlyList<GitRemote>> GetRemotesAsync(CancellationToken cancellationToken = default);

    Task<GitSyncOutcome> FetchAsync(CancellationToken cancellationToken = default);

    /// <summary>Fast-forward only; a diverged branch comes back as a failure explaining the state.</summary>
    Task<GitSyncOutcome> PullAsync(CancellationToken cancellationToken = default);

    /// <summary>Push the current branch to origin, setting the upstream when it has none yet.</summary>
    Task<GitSyncOutcome> PushAsync(CancellationToken cancellationToken = default);

    /// <summary>Push one tag to origin by its full ref name.</summary>
    Task<GitSyncOutcome> PushTagAsync(string tag, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks origin whether a push whose answer was lost arrived, by comparing its ref with the pushed object: success
    /// when it did, a retry-safe network failure when it did not, and still unknown when origin cannot be asked.
    /// </summary>
    Task<GitSyncOutcome> ComparePushAsync(GitPendingPush pending, CancellationToken cancellationToken = default);
}
