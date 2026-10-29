namespace Fravashi.Application.Git;

/// <summary>The outcome of one git command.</summary>
public sealed record GitCommandResult(int ExitCode, string StdOut, string StdErr);

/// <summary>
/// Runs one git command rooted at the active workspace. The seam the parsed git services
/// (GitCliService, GitWorkingTreeService, GitRemoteService) share, so swapping it re-roots the
/// ENTIRE parsed git surface (local clone vs host folder over the bridge, plan 2b-i) in one place.
/// </summary>
public interface IGitCommandRunner
{
    /// <summary>
    /// True when git can run against the workspace (root configured and inside a work tree).
    /// Synchronous by contract: it is read during Blazor render, where blocking on an async call
    /// would deadlock the circuit. Implementations cache their probe.
    /// </summary>
    bool IsAvailable();

    /// <summary>
    /// Why git cannot run, in words a person reads, when there is a reason worth more than "no repository" (backlog
    /// 266: no git installed on the host). Null otherwise. Meaningful after <see cref="IsAvailable"/> has answered.
    /// </summary>
    string? UnavailableReason => null;

    Task<GitCommandResult> RunAsync(
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken);
}
