using System.Diagnostics;
using Fravashi.Application.Git;
using Fravashi.Application.Repository;
using Fravashi.Domain.Git;
using Fravashi.Infrastructure.Repository;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Fravashi.Infrastructure.Git;

/// <summary>
/// <see cref="IGitWorkingTreeService"/> over the git CLI in <c>Repository:Root</c> (plan phase 4).
/// Status parses <c>--porcelain=v2 -z --branch</c> (NUL-safe for renames and exotic names); stage,
/// unstage, and commit take the repository write gate (ADR 0016). Commit identity flows from the
/// settings table as <c>GIT_AUTHOR_*</c>/<c>GIT_COMMITTER_*</c> per-command environment (locked
/// decision 9); when unset the repo/global git config decides, and a missing identity surfaces
/// git's own error plus a pointer at the setting.
/// </summary>
public sealed class GitWorkingTreeService : IGitWorkingTreeService
{
    private readonly string _root;
    private readonly IGitCommandRunner _runner;
    private readonly IRepositoryWriteGate _gate;
    private readonly IConfiguration _configuration;

    public GitWorkingTreeService(
        IOptions<RepositoryReaderOptions> options,
        IGitCommandRunner runner,
        IRepositoryWriteGate gate,
        IConfiguration configuration)
    {
        var configured = options.Value.Root;
        _root = string.IsNullOrWhiteSpace(configured) ? string.Empty : Path.GetFullPath(configured);
        _runner = runner;
        _gate = gate;
        _configuration = configuration;
    }

    public async Task<GitWorkingStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!_runner.IsAvailable())
        {
            return GitWorkingStatus.Empty;
        }

        var (code, output, _) = await RunAsync(["status", "--porcelain=v2", "--branch", "-z"], null, cancellationToken)
            .ConfigureAwait(false);
        if (code != 0)
        {
            return GitWorkingStatus.Empty;
        }

        var branch = string.Empty;
        string? upstream = null;
        var ahead = 0;
        var behind = 0;
        var staged = new List<GitWorkingEntry>();
        var unstaged = new List<GitWorkingEntry>();

        var tokens = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                branch = token["# branch.head ".Length..];
            }
            else if (token.StartsWith("# branch.upstream ", StringComparison.Ordinal))
            {
                upstream = token["# branch.upstream ".Length..];
            }
            else if (token.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                var ab = token["# branch.ab ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (ab.Length == 2)
                {
                    _ = int.TryParse(ab[0].TrimStart('+'), out ahead);
                    _ = int.TryParse(ab[1].TrimStart('-'), out behind);
                }
            }
            else if (token.StartsWith("1 ", StringComparison.Ordinal))
            {
                var fields = token.Split(' ', 9);
                if (fields.Length == 9)
                {
                    AddEntry(staged, unstaged, fields[1], fields[8], null);
                }
            }
            else if (token.StartsWith("2 ", StringComparison.Ordinal))
            {
                // Rename/copy: the NEXT NUL token is the original path.
                var fields = token.Split(' ', 10);
                var oldPath = i + 1 < tokens.Length ? tokens[++i] : null;
                if (fields.Length == 10)
                {
                    AddEntry(staged, unstaged, fields[1], fields[9], oldPath);
                }
            }
            else if (token.StartsWith("u ", StringComparison.Ordinal))
            {
                var fields = token.Split(' ', 11);
                if (fields.Length == 11)
                {
                    unstaged.Add(new GitWorkingEntry(fields[10], GitEntryState.Conflicted, Staged: false));
                }
            }
            else if (token.StartsWith("? ", StringComparison.Ordinal))
            {
                unstaged.Add(new GitWorkingEntry(token[2..], GitEntryState.Untracked, Staged: false));
            }
        }

        return new GitWorkingStatus(branch, upstream, ahead, behind, staged, unstaged);
    }

    private static void AddEntry(
        List<GitWorkingEntry> staged, List<GitWorkingEntry> unstaged, string xy, string path, string? oldPath)
    {
        if (xy.Length != 2)
        {
            return;
        }

        if (xy[0] != '.')
        {
            staged.Add(new GitWorkingEntry(path, StateOf(xy[0]), Staged: true, oldPath));
        }

        if (xy[1] != '.')
        {
            unstaged.Add(new GitWorkingEntry(path, StateOf(xy[1]), Staged: false, oldPath));
        }
    }

    private static GitEntryState StateOf(char code) => code switch
    {
        'M' or 'T' => GitEntryState.Modified,
        'A' => GitEntryState.Added,
        'D' => GitEntryState.Deleted,
        'R' or 'C' => GitEntryState.Renamed,
        'U' => GitEntryState.Conflicted,
        _ => GitEntryState.Unknown,
    };

    public async Task StageAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0)
        {
            return;
        }

        using var _ = await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        // -A on explicit paths stages modifications, additions, AND deletions alike.
        await RunExpectSuccessAsync(["add", "-A", "--", .. paths], null, cancellationToken).ConfigureAwait(false);
    }

    public async Task UnstageAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0)
        {
            return;
        }

        using var _ = await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await RunExpectSuccessAsync(["restore", "--staged", "--", .. paths], null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitCommitOutcome> CommitAsync(string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return new GitCommitOutcome(false, "A commit message is required.");
        }

        using var _ = await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var (code, _, error) = await RunAsync(["commit", "-m", message], IdentityEnvironment(), cancellationToken)
            .ConfigureAwait(false);
        if (code != 0)
        {
            var detail = Summarize(error);
            if (detail.Contains("tell me who you are", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("empty ident", StringComparison.OrdinalIgnoreCase))
            {
                detail += " Set the commit identity under Settings > Connections > Git hosts.";
            }

            return new GitCommitOutcome(false, detail);
        }

        var (shaCode, sha, _) = await RunAsync(["rev-parse", "HEAD"], null, cancellationToken).ConfigureAwait(false);
        return new GitCommitOutcome(true, Sha: shaCode == 0 ? sha.Trim() : null);
    }

    public async Task MarkExecutableAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0)
        {
            return;
        }

        using var _ = await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        foreach (var path in paths)
        {
            await RunExpectSuccessAsync(["update-index", "--chmod=+x", "--", path], null, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task<(string Original, string Modified)> GetWorkingDiffAsync(
        string path, bool staged, CancellationToken cancellationToken = default)
    {
        var normalized = path.Replace('\\', '/').Trim('/');
        if (staged)
        {
            var original = await ShowOrEmptyAsync($"HEAD:{normalized}", cancellationToken).ConfigureAwait(false);
            var modified = await ShowOrEmptyAsync($":0:{normalized}", cancellationToken).ConfigureAwait(false);
            return (original, modified);
        }

        var indexSide = await ShowOrEmptyAsync($":0:{normalized}", cancellationToken).ConfigureAwait(false);
        string worktree;
        try
        {
            var full = Path.GetFullPath(Path.Combine(_root, normalized));
            var rootWithSeparator = Path.TrimEndingDirectorySeparator(_root) + Path.DirectorySeparatorChar;
            worktree = full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) && File.Exists(full)
                ? await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false)
                : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            worktree = string.Empty;
        }

        return (indexSide, worktree);
    }

    private async Task<string> ShowOrEmptyAsync(string spec, CancellationToken cancellationToken)
    {
        var (code, output, _) = await RunAsync(["show", spec], null, cancellationToken).ConfigureAwait(false);
        return code == 0 ? output : string.Empty;
    }

    // ---- Stash (also the studio's undo-changes mechanism: a path-scoped backup stash) ----

    public async Task<GitCommitOutcome> StashAsync(
        string message, IReadOnlyList<string>? paths = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return new GitCommitOutcome(false, "A stash message is required.");
        }

        using var _ = await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var args = new List<string> { "stash", "push", "--include-untracked", "-m", message };
        if (paths is { Count: > 0 })
        {
            args.Add("--");
            args.AddRange(paths);
        }

        var (code, output, error) = await RunAsync(args, IdentityEnvironment(), cancellationToken).ConfigureAwait(false);
        if (code != 0)
        {
            return new GitCommitOutcome(false, Summarize(error.Length > 0 ? error : output));
        }

        if (output.Contains("No local changes", StringComparison.OrdinalIgnoreCase))
        {
            return new GitCommitOutcome(false, "There are no changes to stash.");
        }

        return new GitCommitOutcome(true);
    }

    public async Task<IReadOnlyList<GitStash>> GetStashesAsync(CancellationToken cancellationToken = default)
    {
        var (code, output, _) = await RunAsync(
            ["stash", "list", "--pretty=format:%gd%gs"], null, cancellationToken).ConfigureAwait(false);
        if (code != 0)
        {
            return [];
        }

        var stashes = new List<GitStash>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split('');
            if (fields.Length < 2)
            {
                continue;
            }

            var indexMatch = System.Text.RegularExpressions.Regex.Match(fields[0], @"stash@\{(\d+)\}");
            if (!indexMatch.Success || !int.TryParse(indexMatch.Groups[1].Value, out var index))
            {
                continue;
            }

            // Subject forms: "On <branch>: <message>" or "WIP on <branch>: <sha> <subject>".
            var subject = fields[1];
            string? branch = null;
            var onMatch = System.Text.RegularExpressions.Regex.Match(subject, @"^(?:WIP )?[Oo]n ([^:]+): (.*)$");
            if (onMatch.Success)
            {
                branch = onMatch.Groups[1].Value;
                subject = onMatch.Groups[2].Value;
            }

            stashes.Add(new GitStash(index, subject, branch));
        }

        return stashes;
    }

    public Task<GitCommitOutcome> StashPopAsync(int index, CancellationToken cancellationToken = default) =>
        StashRestoreAsync("pop", index, cancellationToken);

    public Task<GitCommitOutcome> StashApplyAsync(int index, CancellationToken cancellationToken = default) =>
        StashRestoreAsync("apply", index, cancellationToken);

    private async Task<GitCommitOutcome> StashRestoreAsync(string verb, int index, CancellationToken cancellationToken)
    {
        using var _ = await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var (code, _, error) = await RunAsync(
            ["stash", verb, $"stash@{{{index}}}"], null, cancellationToken).ConfigureAwait(false);
        return code == 0
            ? new GitCommitOutcome(true)
            : new GitCommitOutcome(false, Summarize(error));
    }

    /// <summary>The settings-backed commit identity as env, or null to let git config decide.</summary>
    private Dictionary<string, string>? IdentityEnvironment()
    {
        var name = _configuration[GitIdentityKeys.AuthorName];
        var email = _configuration[GitIdentityKeys.AuthorEmail];
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        return new Dictionary<string, string>
        {
            ["GIT_AUTHOR_NAME"] = name,
            ["GIT_AUTHOR_EMAIL"] = email,
            ["GIT_COMMITTER_NAME"] = name,
            ["GIT_COMMITTER_EMAIL"] = email,
        };
    }

    private static string Summarize(string stderr)
    {
        var line = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.Length > 0) ?? "git failed with no detail";
        return line.Length > 300 ? line[..300] : line;
    }

    private async Task RunExpectSuccessAsync(
        IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
    {
        var (code, _, error) = await RunAsync(args, environment, cancellationToken).ConfigureAwait(false);
        if (code != 0)
        {
            throw new InvalidOperationException($"git {args[0]} failed: {Summarize(error)}");
        }
    }

    private async Task<(int Code, string Output, string Error)> RunAsync(
        IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(args, environment, cancellationToken).ConfigureAwait(false);
        return (result.ExitCode, result.StdOut, result.StdErr);
    }
}
