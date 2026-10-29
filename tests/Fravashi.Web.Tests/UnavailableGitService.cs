using Fravashi.Application.Git;
using Fravashi.Domain.Git;
using Fravashi.Domain.Repository;

namespace Fravashi.Web.Tests;

/// <summary>
/// An <see cref="IGitService"/> for fixtures whose component tree merely touches git-aware chrome
/// (e.g. the window title bar's branch chip) without a configured repository: IsAvailable is false
/// and every operation throws, so a test that unexpectedly reaches git fails loudly.
/// </summary>
internal sealed class UnavailableGitService : IGitService
{
    public bool IsAvailable => false;

    private static NotSupportedException Unavailable() => new("git is unavailable in this test fixture");

    public Task<string> GetCurrentBranchAsync(CancellationToken cancellationToken = default) => throw Unavailable();

    public Task<IReadOnlyList<GitBranch>> GetBranchesAsync(CancellationToken cancellationToken = default) => throw Unavailable();

    public Task CheckoutAsync(string branch, CancellationToken cancellationToken = default) => throw Unavailable();

    public Task CreateBranchAsync(string name, string baseBranch, bool checkout, CancellationToken cancellationToken = default) => throw Unavailable();

    public Task<IReadOnlyList<RepositoryNode>> ListTreeAsync(string reference, string path, CancellationToken cancellationToken = default) => throw Unavailable();

    public Task<RepositoryFile?> ReadFileAsync(string reference, string path, CancellationToken cancellationToken = default) => throw Unavailable();

    public Task<IReadOnlyList<GitCommit>> GetCommitsAsync(string reference, int max = 50, string? path = null, CancellationToken cancellationToken = default) => throw Unavailable();

    public Task<GitCommitDetail?> GetCommitAsync(string sha, CancellationToken cancellationToken = default) => throw Unavailable();

    public Task<string> GetDiffAsync(string sha, string? path = null, CancellationToken cancellationToken = default) => throw Unavailable();

    public Task<IReadOnlyList<GitCommit>> GetFileHistoryAsync(string path, string reference, int max = 50, CancellationToken cancellationToken = default) => throw Unavailable();

    public Task<string?> GetRemoteUrlAsync(CancellationToken cancellationToken = default) => throw Unavailable();

    public Task<IReadOnlyList<GitRemoteBranch>> GetRemoteBranchesAsync(CancellationToken cancellationToken = default) => throw Unavailable();

    public Task CheckoutTrackingAsync(GitRemoteBranch branch, CancellationToken cancellationToken = default) => throw Unavailable();

    public Task<IReadOnlyList<GitGraphCommit>> GetGraphAsync(int max = 150, CancellationToken cancellationToken = default) => throw Unavailable();

    public Task<GitLineBlame?> GetLineBlameAsync(string path, int line, CancellationToken cancellationToken = default) => throw Unavailable();

    public Task<IReadOnlyList<GitFileChange>> GetDiffFilesAsync(string baseRef, string headRef, CancellationToken cancellationToken = default) => throw Unavailable();

    public Task<string?> GetMergeBaseAsync(string first, string second, CancellationToken cancellationToken = default) => throw Unavailable();
}
