using System.Diagnostics;
using Fravashi.Application.Git;
using Fravashi.Domain.Git;
using Fravashi.Domain.Repository;
using Fravashi.Application.Repository;
using Fravashi.Infrastructure.Repository;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fravashi.Infrastructure.Git;

/// <summary>
/// <see cref="IGitService"/> implemented by shelling out to the <c>git</c> CLI in <c>Repository:Root</c>.
/// Browsing reads at an arbitrary ref (<c>ls-tree</c> / <c>show ref:path</c> / <c>log</c> / <c>show</c>) and
/// never mutates the working tree; only checkout / branch-create do. Arguments are passed via
/// <see cref="ProcessStartInfo.ArgumentList"/> (no shell), and refs/paths are separated with <c>--</c>.
/// </summary>
public sealed class GitCliService : IGitService
{
    // ASCII unit separator, a safe field delimiter for --pretty formats.
    private const char Sep = '';

    private readonly IGitCommandRunner _runner;

    public GitCliService(IGitCommandRunner runner)
    {
        _runner = runner;
    }

    // The runner owns the availability probe (and its caching); local keeps the render-safe
    // synchronous semantics, the bridge variant answers from the host workspace.
    public bool IsAvailable => _runner.IsAvailable();

    public string? UnavailableReason => IsAvailable ? null : _runner.UnavailableReason;

    public async Task<string> GetCurrentBranchAsync(CancellationToken cancellationToken = default)
    {
        var (_, output, _) = await RunAsync(["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken);
        return output.Trim();
    }

    public async Task<IReadOnlyList<GitBranch>> GetBranchesAsync(CancellationToken cancellationToken = default)
    {
        var current = await GetCurrentBranchAsync(cancellationToken);
        var (_, output, _) = await RunAsync(
            ["for-each-ref", $"--format=%(refname:short){Sep}%(upstream:short){Sep}%(upstream:track)", "refs/heads/"],
            cancellationToken);
        return Lines(output).Select(line =>
        {
            var fields = line.Split(Sep);
            var name = fields[0];
            var upstream = fields.Length > 1 && fields[1].Length > 0 ? fields[1] : null;
            var (ahead, behind) = ParseTrack(fields.Length > 2 ? fields[2] : string.Empty);
            return new GitBranch(name, string.Equals(name, current, StringComparison.Ordinal), upstream, ahead, behind);
        }).ToList();
    }

    /// <summary>Parses git's upstream:track form, e.g. "[ahead 2, behind 1]" / "[gone]" / "".</summary>
    private static (int Ahead, int Behind) ParseTrack(string track)
    {
        var ahead = 0;
        var behind = 0;
        var aheadMatch = System.Text.RegularExpressions.Regex.Match(track, @"ahead (\d+)");
        if (aheadMatch.Success)
        {
            _ = int.TryParse(aheadMatch.Groups[1].Value, out ahead);
        }

        var behindMatch = System.Text.RegularExpressions.Regex.Match(track, @"behind (\d+)");
        if (behindMatch.Success)
        {
            _ = int.TryParse(behindMatch.Groups[1].Value, out behind);
        }

        return (ahead, behind);
    }

    public Task CheckoutAsync(string branch, CancellationToken cancellationToken = default) =>
        RunExpectSuccessAsync(["checkout", branch], cancellationToken);

    public Task CreateBranchAsync(string name, string baseBranch, bool checkout, CancellationToken cancellationToken = default) =>
        checkout
            ? RunExpectSuccessAsync(["checkout", "-b", name, baseBranch], cancellationToken)
            : RunExpectSuccessAsync(["branch", name, baseBranch], cancellationToken);

    public async Task<IReadOnlyList<RepositoryNode>> ListTreeAsync(string reference, string path, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(path);
        // A trailing slash makes ls-tree list the directory's direct children.
        var spec = string.IsNullOrEmpty(normalized) ? string.Empty : normalized + "/";
        string[] args = spec.Length == 0
            ? ["ls-tree", reference]
            : ["ls-tree", reference, spec];

        var (code, output, _) = await RunAsync(args, cancellationToken);
        if (code != 0)
        {
            return [];
        }

        var dirs = new List<RepositoryNode>();
        var files = new List<RepositoryNode>();
        foreach (var line in Lines(output))
        {
            // Format: "<mode> <type> <sha>\t<path>"
            var tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (tab < 0)
            {
                continue;
            }

            var meta = line[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (meta.Length < 2)
            {
                continue;
            }

            var fullPath = line[(tab + 1)..].Trim();
            var name = fullPath.Contains('/', StringComparison.Ordinal) ? fullPath[(fullPath.LastIndexOf('/') + 1)..] : fullPath;
            if (meta[1] == "tree")
            {
                dirs.Add(RepositoryNode.Directory(name, fullPath, []));
            }
            else if (meta[1] == "blob")
            {
                files.Add(RepositoryNode.File(name, fullPath));
            }
        }

        return
        [
            .. dirs.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase),
            .. files.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    public async Task<RepositoryFile?> ReadFileAsync(string reference, string path, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(path);
        if (normalized.Length == 0)
        {
            return null;
        }

        var (code, output, _) = await RunAsync(["show", $"{reference}:{normalized}"], cancellationToken);
        if (code != 0)
        {
            return null;
        }

        return new RepositoryFile(normalized, output, SourceLanguage.For(normalized));
    }

    public async Task<IReadOnlyList<GitCommit>> GetCommitsAsync(string reference, int max = 50, string? path = null, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "log", reference, $"-n{Math.Clamp(max, 1, 500)}", $"--pretty=format:%H{Sep}%h{Sep}%s{Sep}%an{Sep}%aI" };
        if (!string.IsNullOrWhiteSpace(path))
        {
            args.Add("--");
            args.Add(Normalize(path));
        }

        var (code, output, _) = await RunAsync(args, cancellationToken);
        return code != 0 ? [] : Lines(output).Select(ParseCommit).Where(c => c is not null).Select(c => c!).ToList();
    }

    public async Task<GitCommitDetail?> GetCommitAsync(string sha, CancellationToken cancellationToken = default)
    {
        var (headCode, head, _) = await RunAsync(
            ["show", sha, "--no-patch", $"--pretty=format:%H{Sep}%h{Sep}%s{Sep}%an{Sep}%aI{Sep}%b"], cancellationToken);
        if (headCode != 0)
        {
            return null;
        }

        var fields = head.Split(Sep);
        if (fields.Length < 5)
        {
            return null;
        }

        var commit = new GitCommit(fields[0], fields[1], fields[2], fields[3], ParseDate(fields[4]));
        var body = fields.Length > 5 ? fields[5].Trim() : string.Empty;

        var (_, names, _) = await RunAsync(["show", sha, "--name-status", "--pretty=format:"], cancellationToken);
        var files = Lines(names).Select(ParseChange).Where(c => c is not null).Select(c => c!).ToList();

        return new GitCommitDetail(commit, body, files);
    }

    public async Task<string> GetDiffAsync(string sha, string? path = null, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "show", sha, "--format=" };
        if (!string.IsNullOrWhiteSpace(path))
        {
            args.Add("--");
            args.Add(Normalize(path));
        }

        var (_, output, _) = await RunAsync(args, cancellationToken);
        return output;
    }

    public async Task<IReadOnlyList<GitFileChange>> GetDiffFilesAsync(string baseRef, string headRef, CancellationToken cancellationToken = default)
    {
        // Triple-dot: differences of headRef against the merge base, which is what a PR shows.
        var (code, output, error) = await RunAsync(
            ["diff", "--name-status", "--find-renames", $"{baseRef}...{headRef}"], cancellationToken);
        if (code != 0)
        {
            throw new InvalidOperationException($"git diff {baseRef}...{headRef} failed: {error.Trim()}");
        }

        return Lines(output).Select(ParseChange).Where(c => c is not null).Select(c => c!).ToList();
    }

    public async Task<string?> GetMergeBaseAsync(string first, string second, CancellationToken cancellationToken = default)
    {
        var (code, output, _) = await RunAsync(["merge-base", first, second], cancellationToken);
        var sha = output.Trim();
        return code == 0 && sha.Length > 0 ? sha : null;
    }

    public async Task<IReadOnlyList<GitCommit>> GetFileHistoryAsync(string path, string reference, int max = 50, CancellationToken cancellationToken = default)
    {
        var (code, output, _) = await RunAsync(
            ["log", reference, $"-n{Math.Clamp(max, 1, 500)}", "--follow", $"--pretty=format:%H{Sep}%h{Sep}%s{Sep}%an{Sep}%aI", "--", Normalize(path)],
            cancellationToken);
        return code != 0 ? [] : Lines(output).Select(ParseCommit).Where(c => c is not null).Select(c => c!).ToList();
    }

    public async Task<string?> GetRemoteUrlAsync(CancellationToken cancellationToken = default)
    {
        var (code, output, _) = await RunAsync(["remote", "get-url", "origin"], cancellationToken);
        var url = output.Trim();
        return code == 0 && url.Length > 0 ? url : null;
    }

    public async Task<IReadOnlyList<GitRemoteBranch>> GetRemoteBranchesAsync(CancellationToken cancellationToken = default)
    {
        var (code, output, _) = await RunAsync(
            ["for-each-ref", "--format=%(refname:short)", "refs/remotes/"], cancellationToken);
        if (code != 0)
        {
            return [];
        }

        return Lines(output)
            .Where(full => !full.EndsWith("/HEAD", StringComparison.Ordinal) && full.Contains('/', StringComparison.Ordinal))
            .Select(full =>
            {
                var slash = full.IndexOf('/', StringComparison.Ordinal);
                return new GitRemoteBranch(full, full[..slash], full[(slash + 1)..]);
            })
            .ToList();
    }

    public async Task CheckoutTrackingAsync(GitRemoteBranch branch, CancellationToken cancellationToken = default)
    {
        // No name arithmetic here, and that is the point: a branch name may itself contain slashes, so
        // splitting one off a remote-qualified string cannot tell "origin/feature/x" (remote origin,
        // branch feature/x) from anything else, and the caller that already knew the two apart used to
        // have its answer thrown away and re-guessed. GitRemoteBranch carries them separately, from
        // git's own for-each-ref output.
        //
        // Whether the local branch exists is likewise asked, not assumed: `git checkout -b` on an
        // existing branch fails outright, and a plain `git checkout` on an absent one only works by the
        // DWIM that a second remote carrying the same name makes ambiguous.
        var (probe, _, _) = await RunAsync(
            ["rev-parse", "--verify", "--quiet", $"refs/heads/{branch.Name}"], cancellationToken);
        if (probe == 0)
        {
            await RunExpectSuccessAsync(["checkout", branch.Name], cancellationToken);
            return;
        }

        await RunExpectSuccessAsync(
            ["checkout", "-b", branch.Name, "--track", branch.FullName], cancellationToken);
    }

    public async Task<IReadOnlyList<GitTag>> GetTagsAsync(CancellationToken cancellationToken = default)
    {
        // A fixed argument vector: nothing a person typed reaches it. %(*objectname) is the commit an annotated tag
        // points at and is empty for a lightweight one, whose own objectname is already the commit.
        var (code, output, _) = await RunAsync(
            ["for-each-ref", "--sort=-creatordate",
             $"--format=%(refname:short){Sep}%(objectname){Sep}%(*objectname){Sep}%(creatordate:iso-strict){Sep}%(contents:subject)",
             "refs/tags/"],
            cancellationToken);
        if (code != 0)
        {
            return [];
        }

        return Lines(output).Select(line =>
        {
            var f = line.Split(Sep);
            if (f.Length < 4 || f[0].Length == 0)
            {
                return null;
            }

            // A lightweight tag's contents are its commit's, so only an annotated tag has a subject of its own.
            var annotated = f[2].Length > 0;
            return new GitTag(f[0], annotated ? f[2] : f[1], ParseDate(f[3]), annotated && f.Length > 4 ? f[4] : string.Empty);
        }).Where(t => t is not null).Select(t => t!).ToList();
    }

    public async Task CreateTagAsync(string name, string commit, string message, CancellationToken cancellationToken = default)
    {
        // Work item #368. Every word is checked before git runs, and the argument vector is FIXED: the name is a release
        // version (which is also inside git's own ref-name rules, GitToolArguments), the commit is hexadecimal, and the
        // message rides as the value of -m, where git never reads it as an option. An existing tag is refused here and
        // again by git itself (no -f, ever), so a tag is never moved or replaced.
        if (!ReleaseTagWords.IsTagName(name))
        {
            throw new ArgumentException(Fravashi.Domain.Releases.ReleaseVersion.Rule, nameof(name));
        }

        if (!ReleaseTagWords.IsCommit(commit))
        {
            throw new ArgumentException("A commit is named by its hexadecimal object name, 7 to 64 characters.", nameof(commit));
        }

        if (!ReleaseTagWords.IsMessage(message))
        {
            throw new ArgumentException("A tag message is one to 500 characters with no control characters but line breaks.", nameof(message));
        }

        var (exists, _, _) = await RunAsync(["rev-parse", "--verify", "--quiet", "refs/tags/" + name], cancellationToken);
        if (exists == 0)
        {
            throw new InvalidOperationException($"A tag named {name} already exists; a release never moves or replaces a tag.");
        }

        await RunExpectSuccessAsync(["tag", "-a", "-m", message, name, commit], cancellationToken);
    }

    public async Task<IReadOnlyList<GitGraphCommit>> GetGraphAsync(int max = 150, CancellationToken cancellationToken = default)
    {
        var (code, output, _) = await RunAsync(
            ["log", "--branches", "--remotes", "HEAD", $"-n{Math.Clamp(max, 1, 500)}", "--topo-order",
             $"--pretty=format:%H{Sep}%h{Sep}%s{Sep}%an{Sep}%aI{Sep}%P{Sep}%D"],
            cancellationToken);
        if (code != 0)
        {
            return [];
        }

        return Lines(output).Select(line =>
        {
            var f = line.Split(Sep);
            if (f.Length < 5)
            {
                return null;
            }

            var parents = f.Length > 5 && f[5].Length > 0
                ? f[5].Split(' ', StringSplitOptions.RemoveEmptyEntries)
                : [];
            var refs = f.Length > 6 && f[6].Length > 0
                ? f[6].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(r => r.StartsWith("HEAD -> ", StringComparison.Ordinal) ? r["HEAD -> ".Length..] : r)
                    .Where(r => r != "HEAD")
                    .ToArray()
                : [];
            return new GitGraphCommit(f[0], f[1], f[2], f[3], ParseDate(f[4]), parents, refs);
        }).Where(c => c is not null).Select(c => c!).ToList();
    }

    public async Task<GitLineBlame?> GetLineBlameAsync(string path, int line, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(path);
        if (normalized.Length == 0 || line < 1)
        {
            return null;
        }

        var (code, output, _) = await RunAsync(
            ["blame", "--porcelain", "-L", $"{line},{line}", "--", normalized], cancellationToken);
        if (code != 0)
        {
            return null;
        }

        string? sha = null;
        string author = string.Empty;
        var when = DateTimeOffset.UnixEpoch;
        var subject = string.Empty;
        foreach (var row in output.Split('\n'))
        {
            // Porcelain header: "<40-char sha> <orig-line> <final-line> [<group-size>]".
            if (sha is null && row.Length >= 40)
            {
                sha = row[..40];
            }
            else if (row.StartsWith("author ", StringComparison.Ordinal))
            {
                author = row["author ".Length..].Trim();
            }
            else if (row.StartsWith("author-time ", StringComparison.Ordinal)
                && long.TryParse(row["author-time ".Length..].Trim(), out var seconds))
            {
                when = DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
            else if (row.StartsWith("summary ", StringComparison.Ordinal))
            {
                subject = row["summary ".Length..].Trim();
            }
        }

        if (sha is null || sha.All(c => c == '0'))
        {
            return null; // uncommitted line
        }

        var pr = System.Text.RegularExpressions.Regex.Match(subject, @"[#!](\d+)");
        return new GitLineBlame(
            sha,
            sha[..Math.Min(7, sha.Length)],
            author,
            when,
            subject,
            pr.Success && int.TryParse(pr.Groups[1].Value, out var number) ? number : null);
    }

    private static GitCommit? ParseCommit(string line)
    {
        var f = line.Split(Sep);
        return f.Length < 5 ? null : new GitCommit(f[0], f[1], f[2], f[3], ParseDate(f[4]));
    }

    private static GitFileChange? ParseChange(string line)
    {
        var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        var code = parts[0].Trim();
        var kind = code[0] switch
        {
            'A' => GitChangeKind.Added,
            'M' => GitChangeKind.Modified,
            'D' => GitChangeKind.Deleted,
            'R' => GitChangeKind.Renamed,
            'C' => GitChangeKind.Copied,
            _ => GitChangeKind.Unknown,
        };

        return kind == GitChangeKind.Renamed && parts.Length >= 3
            ? new GitFileChange(parts[2], kind, parts[1])
            : new GitFileChange(parts[^1], kind);
    }

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt
            : DateTimeOffset.UnixEpoch;

    private static IEnumerable<string> Lines(string output) =>
        output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0);

    private static string Normalize(string? path) => (path ?? string.Empty).Replace('\\', '/').Trim('/');

    private async Task RunExpectSuccessAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var (code, _, error) = await RunAsync(args, cancellationToken);
        if (code != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error.Trim()}");
        }
    }

    private async Task<(int Code, string Output, string Error)> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(args, environment: null, cancellationToken).ConfigureAwait(false);
        return (result.ExitCode, result.StdOut, result.StdErr);
    }
}

/// <summary>
/// The words a release's tag creation accepts (work item #368), checked before any of them reaches git. Strict on
/// purpose: a release tag is a release version, a commit is hexadecimal, and a message is plain text.
/// </summary>
public static class ReleaseTagWords
{
    /// <summary>True when <paramref name="name"/> is a release version AND inside git's own ref-name rules.</summary>
    public static bool IsTagName(string? name) =>
        Fravashi.Domain.Releases.ReleaseVersion.IsValid(name)
        && Fravashi.Infrastructure.Sessions.Tools.GitToolArguments.IsTagName(name);

    /// <summary>True when <paramref name="commit"/> is a hexadecimal object name of 7 to 64 characters.</summary>
    public static bool IsCommit(string? commit) =>
        commit is { Length: >= 7 and <= 64 } && commit.All(char.IsAsciiHexDigit);

    /// <summary>True when <paramref name="message"/> is 1 to 500 characters with no control character but a line break.</summary>
    public static bool IsMessage(string? message) =>
        !string.IsNullOrWhiteSpace(message)
        && message.Length <= 500
        && message.All(c => !char.IsControl(c) || c is (char)10 or (char)13);
}

/// <summary>DI for the git CLI services.</summary>
public static class GitServiceCollectionExtensions
{
    public static IServiceCollection AddGitService(this IServiceCollection services)
    {
        // The runner is the ONE mode switch for every parsed git operation (plan 2b-i): local
        // clone spawn vs the host bridge, chosen per circuit by the active workspace mode.
        services.AddScoped<LocalGitCommandRunner>();
        services.AddScoped<IGitCommandRunner>(sp =>
            sp.GetRequiredService<Infrastructure.Bridge.BridgeWorkspaceContext>().IsBridgeActive
                ? sp.GetRequiredService<Infrastructure.Bridge.BridgeGitCommandRunner>()
                : sp.GetRequiredService<LocalGitCommandRunner>());
        services.AddScoped<IGitService, GitCliService>();
        services.AddScoped<IGitWorkingTreeService, GitWorkingTreeService>();
        services.AddScoped<IGitRemoteService, GitRemoteService>();
        // Plan 2c (decision 8): origin remote -> (provider, API credential), per git host.
        services.AddScoped<GitHostAccess>();
        // Backlog 298 (ADR 0031 decision 3): whether branch + pull request delivery has a token for this
        // workspace's origin, scoped per circuit exactly like GitHostAccess itself.
        services.AddScoped<Application.Autopilot.IAutopilotDeliveryReadiness, GitHostDeliveryReadiness>();

        // Backlog 150: the same seams for a NAMED workspace, for callers that are not a circuit (an agent's tool
        // call). Built over a bridge runner pinned to the id; null on a host with no bridge, which is the honest
        // answer there rather than a local clone nobody has. GitHostAccess is built from its singleton stores
        // directly because the scoped registration above cannot serve a singleton.
        services.TryAddSingleton<Func<GitHostAccess>>(sp => () => new GitHostAccess(
            sp.GetRequiredService<Application.Persistence.IGitHostStore>(),
            sp.GetRequiredService<Application.Persistence.ISecretStore>(),
            sp.GetRequiredService<Application.Persistence.IProviderConnectionStore>(),
            sp.GetRequiredService<Application.Persistence.IConfigurationStore>()));
        services.TryAddSingleton<IWorkspaceGit>(sp => new WorkspaceGit(
            WorkspaceGit.BridgeRunners(sp.GetRequiredService<IConfiguration>(), sp.GetService<ILoggerFactory>()),
            sp.GetService<IOptions<RepositoryReaderOptions>>() ?? Options.Create(new RepositoryReaderOptions()),
            sp.GetRequiredService<IRepositoryWriteGate>(),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<Func<GitHostAccess>>()(),
            sp.GetService<ILoggerFactory>()));
        return services;
    }
}
