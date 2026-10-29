using System.Text.RegularExpressions;
using Fravashi.Application.AgentRuntimes;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Infrastructure.AgentRuntimes;

/// <summary>
/// Observes each coding-agent runtime by shelling out to it: <c>&lt;cli&gt; --version</c> for presence and
/// version, and - for auth - a stored headless key (a real proof, injected on every run) or the CLI's own
/// status command. Never assumes; where nothing can prove sign-in the state is
/// <see cref="AgentRuntimeAuthState.Unknown"/>. Install goes through the descriptor's PINNED npm specifier
/// (<c>npm install -g &lt;pkg&gt;@&lt;pinned&gt;</c>, backlog 61 / ADR 0023), never an unpinned release.
/// </summary>
public sealed partial class ProcessAgentRuntimeService : IAgentRuntimeService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan UpdateTimeout = TimeSpan.FromMinutes(4);

    private readonly IAgentCommandRunner _runner;
    private readonly IAgentRuntimeCredentialStore _credentials;

    public ProcessAgentRuntimeService(IAgentCommandRunner runner, IAgentRuntimeCredentialStore credentials)
    {
        _runner = runner;
        _credentials = credentials;
    }

    public async Task<AgentRuntimeStatus> GetStatusAsync(AgentTool tool, CancellationToken cancellationToken = default)
    {
        var descriptor = AgentRuntimeCatalog.For(tool);

        var versionResult = await _runner.RunAsync(
            new AgentCommand(descriptor.Command, descriptor.VersionArguments, ProbeTimeout),
            cancellationToken);

        if (!versionResult.Started)
        {
            return new AgentRuntimeStatus(tool, Installed: false, Version: null,
                AgentRuntimeAuthState.NotInstalled, "The CLI is not installed in this environment.");
        }

        var version = ParseVersion(versionResult.StandardOutput + "\n" + versionResult.StandardError);
        var (authState, authDetail) = await ResolveAuthAsync(tool, descriptor, cancellationToken);
        return new AgentRuntimeStatus(tool, Installed: true, version, authState, authDetail);
    }

    public async Task<AgentRuntimeUpdateResult> InstallOrUpdateAsync(AgentTool tool, CancellationToken cancellationToken = default)
    {
        var descriptor = AgentRuntimeCatalog.For(tool);
        var before = await ReadVersionAsync(descriptor, cancellationToken);

        // Pinned by construction (backlog 61): UpdateArguments carries the exact version, so neither this
        // method nor any caller of it can install whatever the registry happens to be serving today.
        var result = await _runner.RunAsync(
            new AgentCommand("npm", descriptor.UpdateArguments, UpdateTimeout),
            cancellationToken);

        // Self-heal the stale-directory family (ENOTEMPTY and friends): the global npm prefix lives on
        // the persistent /data volume, so an interrupted update or a container swap leaves package
        // directories npm cannot rename over, and every later install/update of that package fails
        // until they are cleared. Clearing the package's own dirs and retrying once fixes it for good,
        // for every deployment, with no manual cleanup (directive 2026-07-26).
        if (!result.Success && result.Started && !result.TimedOut
            && IsStaleDirectoryFailure(result.StandardError + "\n" + result.StandardOutput)
            && await TryRemoveStalePackageDirsAsync(descriptor, cancellationToken))
        {
            result = await _runner.RunAsync(
                new AgentCommand("npm", descriptor.UpdateArguments, UpdateTimeout),
                cancellationToken);
        }

        var after = await ReadVersionAsync(descriptor, cancellationToken);
        var now = DateTimeOffset.UtcNow;

        if (result.Success)
        {
            return new AgentRuntimeUpdateResult(tool, AgentRuntimeUpdateOutcome.Succeeded, before, after, null, now);
        }

        var reason = result.TimedOut
            ? "The update timed out."
            : !result.Started
                ? "npm is not available in this environment."
                : FirstLine(result.StandardError) ?? $"npm exited with code {result.ExitCode}.";
        return new AgentRuntimeUpdateResult(tool, AgentRuntimeUpdateOutcome.Failed, before, after, reason, now);
    }

    private async Task<(AgentRuntimeAuthState State, string Detail)> ResolveAuthAsync(
        AgentTool tool, AgentRuntimeDescriptor descriptor, CancellationToken cancellationToken)
    {
        // Backlog 108, third clause. Where the broker carries the runtime a stored key authenticates nothing,
        // so it is not what this line may name: the proof is the token the child would really be handed, and
        // a brokered runtime holding none is signed out, whatever is left in the store.
        if (!_credentials.UsesStoredKey(tool))
        {
            if (_credentials.EnvironmentFor(tool).Count > 0)
            {
                return (AgentRuntimeAuthState.Authenticated,
                    "Authenticated through the credential broker with a short-lived token.");
            }

            if (descriptor.SupportsBrokering)
            {
                return (AgentRuntimeAuthState.SignedOut,
                    "The credential broker carries this runtime and no broker token is held yet. "
                    + "Connect to the broker under Settings.");
            }
        }
        else if (_credentials.HasApiKey(tool))
        {
            // A stored key is injected on every non-interactive run, so it genuinely authenticates the CLI.
            return (AgentRuntimeAuthState.Authenticated, $"Using a stored {descriptor.ApiKeyLabel}.");
        }
        else if (_credentials.HasSubscriptionToken(tool))
        {
            // Backlog 183: likewise the subscription token the sign-in printed, injected into every child.
            return (AgentRuntimeAuthState.Authenticated,
                "Signed in with your Claude subscription: a long-lived token, stored encrypted and handed to each conversation.");
        }

        // Otherwise fall back to the CLI's own status command, where one exists.
        if (descriptor.AuthStatusArguments is null)
        {
            return (AgentRuntimeAuthState.Unknown,
                $"{descriptor.DisplayName} has no scriptable sign-in check - sign in interactively or add a {descriptor.ApiKeyLabel}.");
        }

        var status = await _runner.RunAsync(
            new AgentCommand(descriptor.Command, descriptor.AuthStatusArguments, ProbeTimeout),
            cancellationToken);

        if (!status.Started || status.TimedOut)
        {
            return (AgentRuntimeAuthState.Unknown, "The sign-in check could not be run.");
        }

        var output = status.StandardOutput + "\n" + status.StandardError;
        // Plain text: this sentence is drawn as prose under a signed-in runtime (backlog 172) and as a tooltip, and
        // neither renders markdown, so backticks showed up as themselves.
        var command = $"{descriptor.Command} {string.Join(' ', descriptor.AuthStatusArguments)}";

        // Check the explicit signed-out marker first: a negative message can contain the positive substring
        // (an answer of "Not logged in" contains "logged in"), so the positive test alone would misclassify it.
        var signedOut = descriptor.AuthStatusSignedOutMarker;
        if (signedOut is not null && output.Contains(signedOut, StringComparison.OrdinalIgnoreCase))
        {
            return (AgentRuntimeAuthState.SignedOut, $"{command} reports signed out.");
        }

        var signedIn = descriptor.AuthStatusSignedInMarker;
        if (signedIn is not null && output.Contains(signedIn, StringComparison.OrdinalIgnoreCase))
        {
            return (AgentRuntimeAuthState.Authenticated, $"{command} reports signed in.");
        }

        return (AgentRuntimeAuthState.SignedOut, $"{command} reports signed out.");
    }

    /// <summary>npm's stale-directory error family; anything else is not ours to second-guess.</summary>
    private static bool IsStaleDirectoryFailure(string output) =>
        output.Contains("ENOTEMPTY", StringComparison.OrdinalIgnoreCase)
        || output.Contains("EEXIST", StringComparison.OrdinalIgnoreCase)
        || output.Contains("ENOTDIR", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Removes the package's directory under the global npm root plus npm's ".&lt;name&gt;-*" rename
    /// leftovers beside it. True when the cleanup ran (a retry is then worthwhile); false when the
    /// root could not even be determined.
    /// </summary>
    private async Task<bool> TryRemoveStalePackageDirsAsync(AgentRuntimeDescriptor descriptor, CancellationToken cancellationToken)
    {
        var rootResult = await _runner.RunAsync(
            new AgentCommand("npm", ["root", "-g"], ProbeTimeout), cancellationToken);
        var root = rootResult.Started ? FirstLine(rootResult.StandardOutput) : null;
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var packagePath = Path.Combine(root, descriptor.NpmPackage.Replace('/', Path.DirectorySeparatorChar));
        var parent = Path.GetDirectoryName(packagePath);
        var leaf = Path.GetFileName(packagePath);
        try
        {
            if (Directory.Exists(packagePath))
            {
                Directory.Delete(packagePath, recursive: true);
            }

            if (parent is not null && Directory.Exists(parent))
            {
                foreach (var leftover in Directory.EnumerateDirectories(parent, $".{leaf}-*"))
                {
                    Directory.Delete(leftover, recursive: true);
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Something still holds the directory; the retry would fail the same way, so report the
            // original npm error instead of looping.
            return false;
        }
    }

    private async Task<string?> ReadVersionAsync(AgentRuntimeDescriptor descriptor, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            new AgentCommand(descriptor.Command, descriptor.VersionArguments, ProbeTimeout),
            cancellationToken);
        return result.Started ? ParseVersion(result.StandardOutput + "\n" + result.StandardError) : null;
    }

    /// <summary>Extracts a semver-ish version token from CLI version output, or the first non-empty line.</summary>
    public static string? ParseVersion(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var match = VersionRegex().Match(output);
        if (match.Success)
        {
            return match.Value;
        }

        return FirstLine(output);
    }

    private static string? FirstLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return null;
    }

    [GeneratedRegex(@"\d+\.\d+\.\d+(?:[.-][0-9A-Za-z.]+)?")]
    private static partial Regex VersionRegex();
}
