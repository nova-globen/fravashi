using Fravashi.Application.Git;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>
/// <see cref="IWorkspaceHooksFolder"/> over the host's own git, through the runner pinned to the workspace
/// (<see cref="IWorkspaceGit"/>), the bridge path the git tools and HostGitIgnore (backlog 161) already take. Two
/// questions: <c>git config --type=path --get core.hooksPath</c> (exit 1 is "not set", which is ADR 0028's rule
/// unchanged), then, only when it is set, <c>git rev-parse --show-toplevel --show-prefix</c> to place it in the copy.
/// Anything else (no bridge, a git failure, an exception, the budget running out) is Unknown, which asks.
/// </summary>
public sealed class HostGitHooksFolder(IWorkspaceGit git, TimeSpan? budget = null, ILogger<HostGitHooksFolder>? logger = null)
    : IWorkspaceHooksFolder
{
    /// <summary>How long a trusted edit waits for the answer before treating it as unknown.</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(5);

    private readonly IWorkspaceGit _git = git ?? throw new ArgumentNullException(nameof(git));
    private readonly TimeSpan _budget = budget ?? DefaultBudget;
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    public async Task<WorkspaceHooksFolder> ForAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (_git.For(workspaceId)?.Runner is not { } runner)
            {
                return WorkspaceHooksFolder.Unknown;
            }

            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(_budget);

            var config = await runner.RunAsync(
                ["config", "--type=path", "--get", "core.hooksPath"], null, bounded.Token).ConfigureAwait(false);
            if (config.ExitCode == 1 && string.IsNullOrWhiteSpace(config.StdOut))
            {
                return WorkspaceHooksFolder.None;
            }

            if (config.ExitCode != 0)
            {
                _logger.LogWarning(
                    "The host's git could not say where it runs hooks from (exit {ExitCode}: {Error}); trusted edits to "
                    + "folders that look like hooks folders ask.", config.ExitCode, config.StdErr.Trim());
                return WorkspaceHooksFolder.Unknown;
            }

            var hooksPath = config.StdOut.Trim();
            if (hooksPath.Length == 0)
            {
                return WorkspaceHooksFolder.None;
            }

            var place = await runner.RunAsync(
                ["rev-parse", "--show-toplevel", "--show-prefix"], null, bounded.Token).ConfigureAwait(false);
            var lines = place.StdOut.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
            if (place.ExitCode != 0 || lines.Length < 1 || string.IsNullOrWhiteSpace(lines[0]))
            {
                _logger.LogWarning(
                    "The host's git names core.hooksPath but could not place the repository (exit {ExitCode}: {Error}); "
                    + "trusted edits to folders that look like hooks folders ask.", place.ExitCode, place.StdErr.Trim());
                return WorkspaceHooksFolder.Unknown;
            }

            return WorkspaceHooksFolder.Resolve(hooksPath, lines[0], lines.Length > 1 ? lines[1] : string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "The host's git could not be asked where it runs hooks from; trusted edits to folders that look like "
                + "hooks folders ask.");
            return WorkspaceHooksFolder.Unknown;
        }
    }
}
