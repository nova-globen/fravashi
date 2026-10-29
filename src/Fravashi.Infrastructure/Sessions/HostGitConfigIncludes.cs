using Fravashi.Application.Git;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>
/// <see cref="IWorkspaceConfigIncludes"/> over the host's own git, through the runner pinned to the workspace
/// (<see cref="IWorkspaceGit"/>), the same bridge path <see cref="HostGitHooksFolder"/> takes (backlog 289).
/// Finds the work-tree files git's configuration includes two ways, as the item asks: (1) the repository's own
/// LOCAL and per-worktree config files, found with <c>git rev-parse --git-path config</c> and <c>--git-path
/// config.worktree</c> rather than assumed to be under <c>&lt;toplevel&gt;/.git</c> (that assumption is wrong in
/// a linked worktree or a submodule, where <c>.git</c> there is a file naming a gitdir elsewhere, and a
/// <c>--git-path</c> answer that is relative is relative to the directory the git PROCESS ran in, which is the
/// WORKSPACE folder, not the repository root, whenever those differ), each read with <c>git config --file ...
/// --get-regexp</c> for its literal <c>include.path</c>/<c>includeif.*.path</c> keys, condition-blind, followed
/// recursively into every file named, present or not, in or out of the repository; and (2) <c>git config
/// --show-origin --list</c>, the net under whatever condition evaluation (1) did not reimplement, unioned in. A
/// file counts the moment either method names it inside the workspace's copy.
/// </summary>
public sealed class HostGitConfigIncludes(IWorkspaceGit git, TimeSpan? budget = null, ILogger<HostGitConfigIncludes>? logger = null)
    : IWorkspaceConfigIncludes
{
    /// <summary>How long a trusted edit waits for the answer before treating it as unknown.</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(5);

    /// <summary>Caps the recursion so a loop the visited-set somehow missed cannot run past the budget anyway.</summary>
    private const int MaxFilesFollowed = 500;

    private const string IncludeKeyPattern = @"^include\.path$|^includeif\..*\.path$";

    private readonly IWorkspaceGit _git = git ?? throw new ArgumentNullException(nameof(git));
    private readonly TimeSpan _budget = budget ?? DefaultBudget;
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    public async Task<WorkspaceConfigIncludes> ForAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (_git.For(workspaceId)?.Runner is not { } runner)
            {
                return WorkspaceConfigIncludes.Unknown;
            }

            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(_budget);

            // --git-path config is the repository's REAL local config file, which is not always
            // <toplevel>/.git/config: in a linked worktree or a submodule checkout, .git there is a file naming a
            // gitdir elsewhere, and this answers with the shared file in the main checkout. --git-path
            // config.worktree is the PER-WORKTREE file a repository with extensions.worktreeConfig also reads;
            // it answers a path whether or not that extension is on or the file exists yet, which is handled the
            // same way as any other named-but-absent file.
            var place = await runner.RunAsync(
                ["rev-parse", "--show-toplevel", "--show-prefix", "--git-path", "config", "--git-path", "config.worktree"],
                null,
                bounded.Token).ConfigureAwait(false);
            var lines = place.StdOut.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
            if (place.ExitCode != 0 || lines.Length < 4
                || string.IsNullOrWhiteSpace(lines[0]) || string.IsNullOrWhiteSpace(lines[2]) || string.IsNullOrWhiteSpace(lines[3]))
            {
                _logger.LogWarning(
                    "The host's git could not place the repository or its config files (exit {ExitCode}: {Error}); "
                    + "every edit in this workspace asks until the next successful lookup.", place.ExitCode, place.StdErr.Trim());
                return WorkspaceConfigIncludes.Unknown;
            }

            var topLevel = lines[0].Trim().Replace('\\', '/');
            var prefix = lines[1];

            // --git-path answers relative to the directory the git PROCESS ran in (the workspace folder), which is
            // the repository root only when the workspace IS the root; everywhere else it is topLevel/prefix, never
            // bare topLevel. A workspace at repo/sub answering ../.git/config against bare topLevel would name
            // repo/.git/config's own PARENT, which does not exist.
            var workingDirectory = prefix.Length == 0 ? topLevel : topLevel + "/" + prefix.TrimEnd('/');
            string[] seeds = [
                WorkspaceConfigIncludeTarget.ResolveHostPath(lines[2], workingDirectory),
                WorkspaceConfigIncludeTarget.ResolveHostPath(lines[3], workingDirectory),
            ];

            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!await FollowIncludesAsync(runner, seeds, topLevel, prefix, files, bounded.Token).ConfigureAwait(false))
            {
                return WorkspaceConfigIncludes.Unknown;
            }

            if (!await UnionEffectiveOriginsAsync(runner, topLevel, prefix, files, bounded.Token).ConfigureAwait(false))
            {
                return WorkspaceConfigIncludes.Unknown;
            }

            return WorkspaceConfigIncludes.Of(files);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "The host's git could not be asked which files its configuration includes; every edit in this "
                + "workspace asks until the next successful lookup.");
            return WorkspaceConfigIncludes.Unknown;
        }
    }

    /// <summary>
    /// Method (1): the repository's own config chain, read raw and condition-blind, starting at the two SEED files
    /// the caller already placed (<c>--git-path config</c> and <c>--git-path config.worktree</c>, never assumed to
    /// be <c>&lt;toplevel&gt;/.git/...</c>). False when ANY file in the chain, seed or nested, is one git itself
    /// refuses to PARSE (<see cref="ReadIncludeKeysAsync"/>'s any-exit-code-but-0-or-1 case), which is cheap to
    /// treat as Unknown rather than a stopped branch: git itself cannot use a file it cannot parse either, so the
    /// repository's own git is already in trouble there, and a half-read chain is not a complete answer to widen
    /// only partly from. A file that is merely unreadable (missing, empty, no match, or permission denied) is a
    /// leaf, not a failure: it widens nothing beyond itself, which is recorded as configuration plane the moment
    /// an earlier file named it.
    /// </summary>
    private async Task<bool> FollowIncludesAsync(
        IGitCommandRunner runner, IReadOnlyList<string> seeds, string topLevel, string prefix, HashSet<string> files, CancellationToken cancellationToken)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var frontier = new Queue<string>(seeds);

        while (frontier.Count > 0 && visited.Count < MaxFilesFollowed)
        {
            var file = frontier.Dequeue();
            var key = Normalize(file);
            if (!visited.Add(key))
            {
                continue;
            }

            var keys = await ReadIncludeKeysAsync(runner, file, cancellationToken).ConfigureAwait(false);
            if (keys is null)
            {
                _logger.LogWarning(
                    "The host's git could not read a file its configuration includes ({File}); every edit in this "
                    + "workspace asks until the next successful lookup.", file);
                return false;
            }

            var directory = DirectoryOf(file);
            foreach (var value in keys)
            {
                if (WorkspaceConfigIncludeTarget.Resolve(value, directory, topLevel, prefix) is not { } resolved)
                {
                    continue;
                }

                if (resolved.CopyRelativePath is { } copyRelative)
                {
                    files.Add(copyRelative);
                }

                frontier.Enqueue(resolved.AbsolutePath);
            }
        }

        if (frontier.Count > 0)
        {
            // The cap stopped the walk with files still unread: a truncated chain is not a complete answer, so it
            // is not Known or None either, whatever was found on the way there.
            _logger.LogWarning(
                "The host's git config include chain is deeper than {Cap} files; every edit in this workspace asks "
                + "until the next successful lookup.", MaxFilesFollowed);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Method (2): <c>git config --show-origin --list</c>, the effective configuration under whatever condition
    /// evaluation method (1) did not reimplement. Every <c>file:</c> origin that lands inside the workspace's copy
    /// is unioned in. False only when this call itself fails outright: the item asks for both nets together, so a
    /// failure here is treated the same as a failure of the first.
    /// </summary>
    private async Task<bool> UnionEffectiveOriginsAsync(
        IGitCommandRunner runner, string topLevel, string prefix, HashSet<string> files, CancellationToken cancellationToken)
    {
        // Not --null here: each record is "file:<origin>\t<key>=<value>", one per line, and only the origin before
        // the tab is read. A value with a literal embedded newline can split one record across two lines, but the
        // continuation line never starts with "file:" and is simply skipped, so the worst case is a missed origin
        // method (1) already found some other way, never a wrong one.
        var result = await runner.RunAsync(["config", "--show-origin", "--list"], null, cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            _logger.LogWarning(
                "The host's git could not list its effective configuration (exit {ExitCode}: {Error}); every edit "
                + "in this workspace asks until the next successful lookup.", result.ExitCode, result.StdErr.Trim());
            return false;
        }

        foreach (var line in result.StdOut.Split('\n'))
        {
            if (!line.StartsWith("file:", StringComparison.Ordinal))
            {
                continue;
            }

            var tab = line.IndexOf('\t');
            if (tab < 0)
            {
                continue;
            }

            var origin = line[5..tab].Trim();
            if (WorkspaceConfigIncludeTarget.ResolveOrigin(origin, topLevel, prefix) is { } copyRelative)
            {
                files.Add(copyRelative);
            }
        }

        return true;
    }

    /// <summary>
    /// One file's own literal <c>include.path</c>/<c>includeif.*.path</c> values, never following that file's OWN
    /// includes (<c>--file</c> defaults to that). Exit 1 is a leaf, an empty list, never an error: git answers it
    /// for a missing file, an empty file, a file with no matching key, AND a permission-denied file (a warning on
    /// stderr, exit 1 regardless) alike, which is what backlog 289 asks for - a file named but not yet created is
    /// exactly the case to still cover, not an error to raise, and a file git itself warns about and moves past
    /// rather than failing on is one the caller does the same with. Any OTHER exit code (git refusing to PARSE the
    /// file, not merely failing to read it) is null, which the caller treats as Unknown for the whole lookup.
    /// </summary>
    private static async Task<IReadOnlyList<string>?> ReadIncludeKeysAsync(
        IGitCommandRunner runner, string absoluteFile, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            ["config", "--file", absoluteFile, "--null", "--type=path", "--get-regexp", IncludeKeyPattern],
            null,
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode == 1)
        {
            return [];
        }

        if (result.ExitCode != 0)
        {
            return null;
        }

        var values = new List<string>();
        foreach (var record in SplitNullTerminated(result.StdOut))
        {
            var newline = record.IndexOf('\n');
            if (newline >= 0)
            {
                values.Add(record[(newline + 1)..]);
            }
        }

        return values;
    }

    private static IEnumerable<string> SplitNullTerminated(string stdOut) =>
        stdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    private static string DirectoryOf(string absoluteFile)
    {
        var normalized = Normalize(absoluteFile);
        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? normalized : normalized[..slash];
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}
