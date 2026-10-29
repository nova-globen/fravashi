using Fravashi.Application.Git;
using Fravashi.Application.Releases;

namespace Fravashi.Infrastructure.Ci;

/// <summary>
/// <see cref="ICommitChecks"/> over the same forge reader the agents' ci_runs uses (work item #368): the workspace's
/// origin names the repository, the token is the one Settings holds for that host, and the answer is the newest run's
/// own status word for the commit. Anything that stops it answering (no origin, a host the studio does not know, a
/// network failure, an error the forge answered with) is "nothing to say", because the dialog shows the state as a hint
/// and never gates on it.
/// </summary>
public sealed class ForgeCommitChecks(IGitService git, ForgeRuns forge) : ICommitChecks
{
    /// <inheritdoc />
    public async Task<string?> StateAsync(string sha, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sha) || !sha.All(char.IsAsciiHexDigit))
        {
            return null;
        }

        try
        {
            var origin = await git.GetRemoteUrlAsync(ct).ConfigureAwait(false);
            if (forge.Repository(origin) is not { } repository)
            {
                return null;
            }

            var runs = await forge.ListAsync(repository, 5, sha, ct).ConfigureAwait(false);
            return runs.Count == 0 ? null : runs[0].Status;
        }
        catch (Exception ex) when (ex is ForgeUnreachableException or HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            return null;
        }
    }
}
