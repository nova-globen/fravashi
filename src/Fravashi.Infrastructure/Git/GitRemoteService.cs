using Fravashi.Application.Git;
using Fravashi.Application.Network;
using Fravashi.Application.Repository;
using Fravashi.Domain.PullRequests;
using Fravashi.Infrastructure.PullRequests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fravashi.Infrastructure.Git;

/// <summary>
/// <see cref="IGitRemoteService"/> over the git CLI (plan phase 5). Credentials ride the same
/// <see cref="GitAuthHeader"/> mechanism the clone service proved out: an <c>http.extraHeader</c>
/// injected via <c>GIT_CONFIG_*</c> environment, so no token ever reaches a command line, a URL,
/// or the clone's config. The credential itself resolves PER GIT HOST from the origin remote
/// (plan 2c, decision 8). Pull is <c>--ff-only</c> and holds the repository write gate (ADR 0016).
/// Bridge mode note: the bridge runner ignores env by design (the HOST credential manager owns
/// transport auth there), so this environment only ever acts in local-clone/dev mode.
/// </summary>
public sealed class GitRemoteService : IGitRemoteService
{
    /// <summary>
    /// The pause before retrying a push once after an authentication refusal (backlog 325): this forge's first
    /// authenticated git call after idle is known to fail once and succeed on an immediate retry.
    /// </summary>
    public static readonly TimeSpan AuthenticationRetryPause = TimeSpan.FromSeconds(2);

    private readonly IGitCommandRunner _runner;
    private readonly IRepositoryWriteGate _gate;
    private readonly GitHostAccess _access;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _logger;

    /// <param name="delay">How the authentication retry waits; tests pass one that resolves immediately.</param>
    public GitRemoteService(
        IGitCommandRunner runner,
        IRepositoryWriteGate gate,
        GitHostAccess access,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        ILogger<GitRemoteService>? logger = null)
    {
        _runner = runner;
        _gate = gate;
        _access = access;
        _delay = delay ?? Task.Delay;
        _logger = logger ?? NullLogger<GitRemoteService>.Instance;
    }

    public async Task<IReadOnlyList<GitRemote>> GetRemotesAsync(CancellationToken cancellationToken = default)
    {
        var (code, output, _) = await RunAsync(["remote", "-v"], null, cancellationToken).ConfigureAwait(false);
        if (code != 0)
        {
            return [];
        }

        var remotes = new Dictionary<string, (string? Fetch, string? Push)>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                continue;
            }

            var entry = remotes.TryGetValue(parts[0], out var existing) ? existing : (null, null);
            if (parts[2] == "(fetch)")
            {
                entry.Fetch = parts[1];
            }
            else if (parts[2] == "(push)")
            {
                entry.Push = parts[1];
            }

            remotes[parts[0]] = entry;
        }

        return remotes
            .Where(r => r.Value.Fetch is not null || r.Value.Push is not null)
            .Select(r => new GitRemote(r.Key, r.Value.Fetch ?? r.Value.Push!, r.Value.Push))
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .ToList();
    }

    public Task<GitSyncOutcome> FetchAsync(CancellationToken cancellationToken = default) =>
        GuardAsync(async () =>
        {
            var env = await AuthEnvironmentAsync(cancellationToken).ConfigureAwait(false);
            var run = await RunRemoteAsync(["fetch", "origin"], env, cancellationToken).ConfigureAwait(false);
            if (run.Transport is { } transport)
            {
                return StoppedRead(transport, "fetch");
            }

            return run.Result!.ExitCode == 0 ? new GitSyncOutcome(true) : FromGit(run.Result.StdErr, Summarize(run.Result.StdErr));
        }, cancellationToken);

    public Task<GitSyncOutcome> PullAsync(CancellationToken cancellationToken = default) =>
        GuardAsync(async () =>
        {
            // The gate: a pull rewrites the working tree, and an editor save mid-pull must wait.
            using var _ = await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
            var env = await AuthEnvironmentAsync(cancellationToken).ConfigureAwait(false);
            var run = await RunRemoteAsync(["pull", "--ff-only", "origin"], env, cancellationToken).ConfigureAwait(false);
            if (run.Transport is { } transport)
            {
                return StoppedRead(transport, "pull");
            }

            var (code, output, error) = (run.Result!.ExitCode, run.Result.StdOut, run.Result.StdErr);
            if (code != 0)
            {
                var detail = Summarize(error);
                if (detail.Contains("Not possible to fast-forward", StringComparison.OrdinalIgnoreCase)
                    || detail.Contains("divergent", StringComparison.OrdinalIgnoreCase))
                {
                    detail += " The local branch diverged from origin; resolve it with your own git tooling, nothing is merged silently.";
                }

                return FromGit(error, detail);
            }

            return new GitSyncOutcome(true, Detail: Summarize(output));
        }, cancellationToken);

    public Task<GitSyncOutcome> PushAsync(CancellationToken cancellationToken = default) =>
        GuardAsync(async () =>
        {
            var (branchCode, branch, _) = await RunAsync(["rev-parse", "--abbrev-ref", "HEAD"], null, cancellationToken)
                .ConfigureAwait(false);
            if (branchCode != 0)
            {
                return new GitSyncOutcome(false, "Could not determine the current branch.");
            }

            branch = branch.Trim();
            var (upstreamCode, _, _) = await RunAsync(
                ["rev-parse", "--abbrev-ref", "@{u}"], null, cancellationToken).ConfigureAwait(false);
            string[] args = upstreamCode == 0
                ? ["push", "origin", branch]
                : ["push", "--set-upstream", "origin", branch];

            // What origin should name afterwards, recorded BEFORE the push, so a lost answer can be compared.
            var (shaCode, sha, _) = await RunAsync(["rev-parse", "HEAD"], null, cancellationToken).ConfigureAwait(false);
            var pending = shaCode == 0
                ? new GitPendingPush("refs/heads/" + branch, sha.Trim(), upstreamCode == 0 ? null : branch)
                : null;
            return await PushRefAsync(args, pending, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<GitSyncOutcome> PushTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        return GuardAsync(async () =>
        {
            var full = "refs/tags/" + tag;
            var (shaCode, sha, _) = await RunAsync(["rev-parse", "--verify", full], null, cancellationToken).ConfigureAwait(false);
            var pending = shaCode == 0 ? new GitPendingPush(full, sha.Trim()) : null;
            // The tool layer checks tag against git's own ref-name rules and that it already exists before this
            // ever runs (backlog 285), so the word cannot carry a ':' and turn this into a refspec with its own
            // destination; the fixed "refs/tags/" prefix alone was not enough to guarantee that.
            return await PushRefAsync(["push", "origin", full], pending, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<GitSyncOutcome> ComparePushAsync(GitPendingPush pending, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pending);
        return GuardAsync(
            () => CompareAsync(pending, "the answer to the earlier push was lost", cancellationToken), cancellationToken);
    }

    /// <summary>How long a remote git operation may run before the studio stops it (the bridge client's own limit).</summary>
    public static readonly TimeSpan RemoteTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Runs a remote command, and retries it exactly once, after <see cref="AuthenticationRetryPause"/>, when git's own
    /// answer is an authentication refusal (backlog 325): this forge's first authenticated call after idle is known to
    /// fail once and succeed on an immediate retry. A second refusal, a different refusal, a network failure and a
    /// transport failure (where <see cref="RemoteRun.Result"/> is null) all fall through unchanged to the caller's own
    /// handling. Used by both the push itself and the <c>ls-remote</c> that settles a push whose answer was lost, so
    /// every remote call delivery makes gets the same retry from the one place every pusher shares.
    /// </summary>
    private async Task<RemoteRun> RunRemoteWithAuthRetryAsync(
        IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
    {
        var run = await RunRemoteAsync(args, environment, cancellationToken).ConfigureAwait(false);
        if (run.Result is { ExitCode: not 0 } first
            && GitRemoteStderr.Classify(first.StdErr).Refusal == GitRefusal.Authentication)
        {
            _logger.LogInformation(
                "Retrying {Operation} against {Remote} after an authentication refusal.", Describe(args), OriginRemoteName);
            await _delay(AuthenticationRetryPause, cancellationToken).ConfigureAwait(false);
            run = await RunRemoteAsync(args, environment, cancellationToken).ConfigureAwait(false);

            if (run.Result is { ExitCode: not 0 } second
                && GitRemoteStderr.Classify(second.StdErr).Refusal == GitRefusal.Authentication)
            {
                _logger.LogInformation(
                    "{Operation} against {Remote} was refused on authentication again after the retry.",
                    Describe(args), OriginRemoteName);
            }
        }

        return run;
    }

    private const string OriginRemoteName = "origin";

    /// <summary>
    /// Names a remote command for a log line, never from its stderr: the command is <c>push</c> or
    /// <c>ls-remote</c>, and the ref it names is always the last argument, whether it is a plain push, a
    /// <c>--set-upstream</c> push, a tag push, or the <c>ls-remote</c> that settles a lost push answer.
    /// </summary>
    private static string Describe(IReadOnlyList<string> args) =>
        args[0] == "ls-remote" ? $"the ls-remote compare of {args[^1]}" : $"the push of {args[^1]}";

    private async Task<GitSyncOutcome> PushRefAsync(
        IReadOnlyList<string> args, GitPendingPush? pending, CancellationToken cancellationToken)
    {
        var env = await AuthEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        var run = await RunRemoteWithAuthRetryAsync(args, env, cancellationToken).ConfigureAwait(false);

        if (run.Transport is { } transport)
        {
            if (transport.Failure.NeverLeft)
            {
                return CouldNotReachBridge(transport.Failure);
            }

            // The studio stopped hearing from the bridge, which kills git on the host: the push's fate is unknown.
            var why = transport.TimedOut
                ? $"origin did not answer within {RemoteTimeout.TotalMinutes:0} minutes, so the push was stopped"
                : $"the host bridge stopped answering ({transport.Failure.Words})";
            return await AskOrUnknownAsync(pending, why, cancellationToken).ConfigureAwait(false);
        }

        var result = run.Result!;
        if (result.ExitCode == 0)
        {
            return new GitSyncOutcome(true);
        }

        var verdict = GitRemoteStderr.Classify(result.StdErr);
        if (verdict.IsNetwork && !verdict.NeverLeft)
        {
            return await AskOrUnknownAsync(pending, verdict.Reason, cancellationToken).ConfigureAwait(false);
        }

        return FromGit(result.StdErr, Summarize(result.StdErr));
    }

    private Task<GitSyncOutcome> AskOrUnknownAsync(GitPendingPush? pending, string why, CancellationToken cancellationToken) =>
        pending is null
            ? Task.FromResult(Unknown(null, why, "the pushed commit could not be read locally"))
            : CompareAsync(pending, why, cancellationToken);

    /// <summary>
    /// Settles a push whose answer was lost by asking origin for the ref (<c>ls-remote</c>) and comparing it with the
    /// object pushed. Reached: the local remote-tracking ref and upstream are set as a completed push would have.
    /// </summary>
    private async Task<GitSyncOutcome> CompareAsync(GitPendingPush pending, string why, CancellationToken cancellationToken)
    {
        var env = await AuthEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        var asked = await RunRemoteWithAuthRetryAsync(["ls-remote", "origin", pending.RemoteRef], env, cancellationToken).ConfigureAwait(false);
        if (asked.Transport is { } transport)
        {
            return Unknown(pending, why, transport.Failure.Words);
        }

        if (asked.Result!.ExitCode != 0)
        {
            var verdict = GitRemoteStderr.Classify(asked.Result.StdErr);
            return Unknown(pending, why, verdict.IsNetwork ? verdict.Reason : Summarize(asked.Result.StdErr));
        }

        var remoteSha = asked.Result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t', 2))
            .Where(parts => parts.Length == 2 && string.Equals(parts[1].Trim(), pending.RemoteRef, StringComparison.Ordinal))
            .Select(parts => parts[0].Trim())
            .FirstOrDefault();
        var shortSha = pending.Sha.Length > 7 ? pending.Sha[..7] : pending.Sha;
        if (string.Equals(remoteSha, pending.Sha, StringComparison.OrdinalIgnoreCase))
        {
            const string BranchPrefix = "refs/heads/";
            if (pending.RemoteRef.StartsWith(BranchPrefix, StringComparison.Ordinal))
            {
                var name = pending.RemoteRef[BranchPrefix.Length..];
                await RunAsync(["update-ref", "refs/remotes/origin/" + name, pending.Sha], null, cancellationToken).ConfigureAwait(false);
                if (pending.UpstreamBranch is { } upstream)
                {
                    await RunAsync(["branch", "--set-upstream-to=origin/" + name, upstream], null, cancellationToken).ConfigureAwait(false);
                }
            }

            return new GitSyncOutcome(true, Detail: $"The answer was lost on the way back ({why}), but origin has {shortSha}, so the push arrived.");
        }

        var where = remoteSha is null
            ? $"origin does not have {Short(pending.RemoteRef)}"
            : $"origin is still at {(remoteSha.Length > 7 ? remoteSha[..7] : remoteSha)}";
        return new GitSyncOutcome(false, $"The push did not reach origin ({why}); {where}. Nothing changed there.")
        {
            Failure = GitSyncFailure.Network,
            RetrySafe = true,
        };
    }

    private static GitSyncOutcome Unknown(GitPendingPush? pending, string why, string askWhy) =>
        new(false,
            $"The push was sent but no answer came back ({why}), and origin cannot be asked right now ({askWhy}). "
            + "It may have arrived: check origin before pushing again.")
        {
            Failure = GitSyncFailure.OutcomeUnknown,
            Pending = pending,
        };

    private static string Short(string fullRef) =>
        fullRef.StartsWith("refs/heads/", StringComparison.Ordinal) ? fullRef["refs/heads/".Length..]
        : fullRef.StartsWith("refs/tags/", StringComparison.Ordinal) ? "the tag " + fullRef["refs/tags/".Length..]
        : fullRef;

    /// <summary>A failed git exit, worded by what git's text says (backlog 231). <paramref name="said"/> is git's own line.</summary>
    private static GitSyncOutcome FromGit(string stderr, string said)
    {
        var verdict = GitRemoteStderr.Classify(stderr);
        if (verdict.IsNetwork)
        {
            // A fetch or pull is a read until its fetch completed, and a push gets here only when it never left.
            return new GitSyncOutcome(false, $"Could not reach origin ({verdict.Reason}). Nothing changed.", said)
            {
                Failure = GitSyncFailure.Network,
                RetrySafe = true,
            };
        }

        if (verdict.Refusal == GitRefusal.None)
        {
            return new GitSyncOutcome(false, said);
        }

        var sentence = verdict.Refusal switch
        {
            GitRefusal.Rejected => "Origin refused it: origin has commits this branch does not. Pull first, then push again.",
            GitRefusal.HookDeclined => "Origin's own rules refused it (a hook or a protected branch).",
            GitRefusal.Authentication => "Origin did not accept the credentials. Sign in with the host's git credential manager, then try again.",
            GitRefusal.Permission => "Origin refused it: these credentials are not allowed to do this.",
            GitRefusal.NotFound => "Origin has no such repository, or hides it from these credentials.",
            GitRefusal.Certificate => "Origin's certificate was not trusted. This is not the network.",
            _ => "Origin answered with a server error before anything was sent. Nothing changed.",
        };
        return new GitSyncOutcome(false, $"{sentence} Git said: {said}", said)
        {
            Failure = GitSyncFailure.Refused,
            Refusal = verdict.Refusal,
            RetrySafe = verdict.Refusal == GitRefusal.ServerError,
        };
    }

    /// <summary>A fetch or pull the studio stopped hearing about: a read, so trying again is safe.</summary>
    private static GitSyncOutcome StoppedRead(TransportFailure transport, string act)
    {
        if (transport.Failure.NeverLeft)
        {
            return CouldNotReachBridge(transport.Failure);
        }

        var said = transport.TimedOut
            ? $"Origin did not answer within {RemoteTimeout.TotalMinutes:0} minutes, so the {act} was stopped. Nothing was merged."
            : $"The host bridge stopped answering during the {act} ({transport.Failure.Words}). Nothing was merged.";
        return new GitSyncOutcome(false, said) { Failure = GitSyncFailure.Network, RetrySafe = true };
    }

    private static GitSyncOutcome CouldNotReachBridge(NetworkFailure failure) =>
        new(false, $"Could not reach the host bridge ({failure.Words}). Nothing changed.")
        {
            Failure = GitSyncFailure.Network,
            RetrySafe = true,
        };

    /// <summary>
    /// Words a transport failure from any LOCAL step (reading the branch, the commit) instead of letting it escape: no
    /// remote was asked yet, so nothing changed. A cancellation the caller asked for is never worded.
    /// </summary>
    private static async Task<GitSyncOutcome> GuardAsync(Func<Task<GitSyncOutcome>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransport(ex, cancellationToken))
        {
            if (ex is HttpRequestException { StatusCode: not null })
            {
                return new GitSyncOutcome(false, $"The host bridge refused it: {ex.Message}.") { Failure = GitSyncFailure.Other };
            }

            var failure = NetworkFailure.Classify(ex, cancellationToken);
            return new GitSyncOutcome(false,
                $"Could not reach the host bridge ({(failure.IsNetwork ? failure.Words : "the request was stopped")}). Nothing changed.")
            {
                Failure = GitSyncFailure.Network,
                RetrySafe = true,
            };
        }
    }

    private static bool IsTransport(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException or IOException
        || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    /// <summary>A transport failure between the studio and the bridge, classified by slice A.</summary>
    private sealed record TransportFailure(NetworkFailure Failure, bool TimedOut);

    private sealed record RemoteRun(GitCommandResult? Result, TransportFailure? Transport);

    /// <summary>
    /// Runs a command that talks to origin. A failure of the studio-to-bridge hop is returned rather than thrown, so
    /// the caller can tell "never reached the bridge" (nothing ran) from "stopped hearing" (git may have finished).
    /// A bridge that ANSWERED with an error status still throws, and <see cref="GuardAsync"/> words it.
    /// </summary>
    private async Task<RemoteRun> RunRemoteAsync(
        IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
    {
        try
        {
            return new(await _runner.RunAsync(args, environment, cancellationToken).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (IsTransport(ex, cancellationToken) && ex is not HttpRequestException { StatusCode: not null })
        {
            var failure = NetworkFailure.Classify(ex, cancellationToken);
            var timedOut = ex is OperationCanceledException && failure.Kind == NetworkFailureKind.LeftWithNoAnswer;
            // A stop with no network shape (some other cancellation) may still have killed git mid-way.
            return new(null, new(failure.IsNetwork ? failure : new(NetworkFailureKind.LeftWithNoAnswer, ex.GetType().Name), timedOut));
        }
    }

    /// <summary>
    /// The per-host credential for the ORIGIN remote as git env (empty when none resolves:
    /// public/file remotes, or a host with no stored entry).
    /// </summary>
    private async Task<Dictionary<string, string>> AuthEnvironmentAsync(CancellationToken cancellationToken)
    {
        var (code, url, _) = await RunAsync(["remote", "get-url", "origin"], null, cancellationToken)
            .ConfigureAwait(false);
        var resolution = _access.Resolve(RemoteInfo.Parse(code == 0 ? url.Trim() : null));
        var azureForm = resolution.Provider == PullRequestProvider.AzureDevOps;
        return GitAuthHeader.Environment(GitAuthHeader.Build(resolution.Credential, azureForm));
    }

    private static string Summarize(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 0)
            .ToList();
        // git prefixes the actual problem with fatal:/error:; progress lines come first otherwise.
        var line = lines.FirstOrDefault(l =>
                l.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase)
                || l.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
            ?? lines.LastOrDefault() ?? string.Empty;
        return line.Length > 300 ? line[..300] : line;
    }

    private async Task<(int Code, string Output, string Error)> RunAsync(
        IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(args, environment, cancellationToken).ConfigureAwait(false);
        return (result.ExitCode, result.StdOut, result.StdErr);
    }
}
