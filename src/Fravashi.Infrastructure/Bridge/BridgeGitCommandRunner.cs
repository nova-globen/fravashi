using Fravashi.Application.Git;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fravashi.Infrastructure.Bridge;

/// <summary>
/// The bridge-backed <see cref="IGitCommandRunner"/>: every git command the parsed services issue
/// runs on the HOST inside the active workspace folder (plan 2b-i). The environment parameter is
/// deliberately IGNORED: on a host folder, the host user's own git config and credential manager
/// decide identity and authentication ("the user's folder, the user's git", decision
/// 2026-08-01); the studio's Git:Author* settings and stored source credential apply only to the
/// local-clone mode. A curated env allowlist can open later if that trade ever reverses.
/// </summary>
public sealed class BridgeGitCommandRunner(BridgeClient client, ILogger<BridgeGitCommandRunner>? logger = null)
    : IGitCommandRunner
{
    private readonly ILogger _logger = logger ?? NullLogger<BridgeGitCommandRunner>.Instance;
    /// <summary>What the Git page and the git tools say when the host has no git (backlog 266).</summary>
    public const string GitMissingReason = "git is not installed on this machine, so the studio cannot read or change this folder's history. Install git on this computer to use it here.";

    private bool? _available;
    private bool _envDropNoted;

    /// <inheritdoc />
    public string? UnavailableReason { get; private set; }

    public bool IsAvailable()
    {
        if (_available is { } cached)
        {
            return cached;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = Task.Run(
                    () => client.GitAsync(["rev-parse", "--is-inside-work-tree"], timeout.Token))
                .GetAwaiter().GetResult();
            _available = result.ExitCode == 0 && result.Stdout.Trim() == "true";
            UnavailableReason = result.IsGitMissing ? GitMissingReason : null;
        }
        catch (Exception)
        {
            _available = false;
        }

        return _available.Value;
    }

    public async Task<GitCommandResult> RunAsync(
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken)
    {
        if (environment is { Count: > 0 } && !_envDropNoted)
        {
            _envDropNoted = true;
            _logger.LogDebug(
                "Bridge git ignores per-command env ({Keys}); the host git config owns identity and credentials.",
                string.Join(",", environment.Keys));
        }

        var result = await client.GitAsync(args, cancellationToken).ConfigureAwait(false);
        return new GitCommandResult(result.ExitCode, result.Stdout, result.Stderr);
    }
}
