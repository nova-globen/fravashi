using Fravashi.Application.Git;
using Fravashi.Domain.Git;
using Fravashi.Domain.Repository;

namespace Fravashi.Application.Tests.Releases;

/// <summary>
/// The repository a release test talks to (work item #368): its tags, its branches and commits, and a record of every
/// tag created and pushed. Anything a release never asks of git throws, so a test that reaches it fails loudly.
/// </summary>
internal sealed class ReleaseGitFake : IGitService, IGitRemoteService
{
    public List<GitTag> Tags { get; } = [];

    public List<GitBranch> Branches { get; } = [new GitBranch("main", true)];

    public List<GitCommit> Commits { get; } = [];

    public List<(string Name, string Commit, string Message)> Created { get; } = [];

    public List<string> Pushed { get; } = [];

    public GitSyncOutcome PushOutcome { get; set; } = new(true);

    public bool IsAvailable => true;

    public Task<IReadOnlyList<GitTag>> GetTagsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GitTag>>([.. Tags]);

    public Task CreateTagAsync(string name, string commit, string message, CancellationToken cancellationToken = default)
    {
        Created.Add((name, commit, message));
        Tags.Add(new GitTag(name, commit, DateTimeOffset.UnixEpoch, message));
        return Task.CompletedTask;
    }

    public Task<GitSyncOutcome> PushTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        Pushed.Add(tag);
        return Task.FromResult(PushOutcome);
    }

    public Task<IReadOnlyList<GitBranch>> GetBranchesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GitBranch>>([.. Branches]);

    public Task<IReadOnlyList<GitCommit>> GetCommitsAsync(string reference, int max = 50, string? path = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GitCommit>>([.. Commits.Take(max)]);

    private static NotSupportedException Unused() => new("a release never asks git for this");

    public Task<string> GetCurrentBranchAsync(CancellationToken cancellationToken = default) => throw Unused();

    public Task CheckoutAsync(string branch, CancellationToken cancellationToken = default) => throw Unused();

    public Task CreateBranchAsync(string name, string baseBranch, bool checkout, CancellationToken cancellationToken = default) => throw Unused();

    public Task<IReadOnlyList<RepositoryNode>> ListTreeAsync(string reference, string path, CancellationToken cancellationToken = default) => throw Unused();

    public Task<RepositoryFile?> ReadFileAsync(string reference, string path, CancellationToken cancellationToken = default) => throw Unused();

    public Task<GitCommitDetail?> GetCommitAsync(string sha, CancellationToken cancellationToken = default) => throw Unused();

    public Task<string> GetDiffAsync(string sha, string? path = null, CancellationToken cancellationToken = default) => throw Unused();

    public Task<IReadOnlyList<GitFileChange>> GetDiffFilesAsync(string baseRef, string headRef, CancellationToken cancellationToken = default) => throw Unused();

    public Task<string?> GetMergeBaseAsync(string first, string second, CancellationToken cancellationToken = default) => throw Unused();

    public Task<IReadOnlyList<GitCommit>> GetFileHistoryAsync(string path, string reference, int max = 50, CancellationToken cancellationToken = default) => throw Unused();

    public Task<string?> GetRemoteUrlAsync(CancellationToken cancellationToken = default) => throw Unused();

    public Task<IReadOnlyList<GitRemoteBranch>> GetRemoteBranchesAsync(CancellationToken cancellationToken = default) => throw Unused();

    public Task CheckoutTrackingAsync(GitRemoteBranch branch, CancellationToken cancellationToken = default) => throw Unused();

    public Task<IReadOnlyList<GitGraphCommit>> GetGraphAsync(int max = 150, CancellationToken cancellationToken = default) => throw Unused();

    public Task<GitLineBlame?> GetLineBlameAsync(string path, int line, CancellationToken cancellationToken = default) => throw Unused();

    public Task<IReadOnlyList<GitRemote>> GetRemotesAsync(CancellationToken cancellationToken = default) => throw Unused();

    public Task<GitSyncOutcome> FetchAsync(CancellationToken cancellationToken = default) => throw Unused();

    public Task<GitSyncOutcome> PullAsync(CancellationToken cancellationToken = default) => throw Unused();

    public Task<GitSyncOutcome> PushAsync(CancellationToken cancellationToken = default) => throw Unused();

    public Task<GitSyncOutcome> ComparePushAsync(GitPendingPush pending, CancellationToken cancellationToken = default) => throw Unused();
}
