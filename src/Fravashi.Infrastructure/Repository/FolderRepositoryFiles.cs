using System.Net;
using System.Text;
using Fravashi.Application.Git;
using Fravashi.Application.Migration;
using Fravashi.Application.Persistence;
using Fravashi.Application.Search;
using Fravashi.Infrastructure.Bridge;
using Fravashi.Infrastructure.Search;
using Microsoft.Extensions.Options;

namespace Fravashi.Infrastructure.Repository;

/// <summary>
/// The git listing both folder readers share (item #373): the markdown files git tracks, so <c>.gitignore</c> is
/// honoured by git itself. Null when there is no git work tree or git refuses, which the scan answers with a walk.
/// </summary>
internal static class TrackedMarkdown
{
    public static async Task<IReadOnlyList<string>?> ListAsync(IGitCommandRunner? git, CancellationToken ct)
    {
        if (git is null)
        {
            return null;
        }

        try
        {
            if (!await Task.Run(git.IsAvailable, ct).ConfigureAwait(false))
            {
                return null;
            }

            var result = await git.RunAsync(
                ["-c", "core.quotepath=off", "ls-files", "-z", "--cached", "--", ":(icase)*.md", ":(icase)*.markdown"],
                null, ct).ConfigureAwait(false);
            return result.ExitCode != 0
                ? null
                : result.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException
                                       or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// <see cref="IRepositoryFiles"/> over the host bridge: the window's own folder, read only, through the same listing,
/// read and git endpoints the Code page uses (item #373). A head read stops after the bytes it needs.
/// </summary>
public sealed class BridgeRepositoryFiles(BridgeClient client, IGitCommandRunner? git) : IRepositoryFiles
{
    /// <inheritdoc />
    public Task<IReadOnlyList<string>?> TrackedMarkdownAsync(CancellationToken ct) => TrackedMarkdown.ListAsync(git, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<RepositoryEntry>> ListAsync(string folder, CancellationToken ct)
    {
        try
        {
            return [.. (await client.ListAsync(folder, ct).ConfigureAwait(false))
                .Select(e => new RepositoryEntry(e.Path.Replace('\\', '/'), e.IsDirectory, e.Size))];
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<string?> ReadHeadAsync(string path, int maxBytes, CancellationToken ct) =>
        await client.ReadHeadAsync(path, maxBytes, ct).ConfigureAwait(false) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    /// <inheritdoc />
    public async Task<string?> ReadAllAsync(string path, CancellationToken ct) =>
        await client.ReadAsync(path, ct).ConfigureAwait(false) is { } content ? Encoding.UTF8.GetString(content.Bytes) : null;
}

/// <summary>
/// <see cref="IRepositoryFiles"/> over a folder on this machine: a test's temporary folder, or a dev studio's
/// <c>Repository:Root</c>. Every path is held inside <paramref name="root"/>.
/// </summary>
public sealed class LocalRepositoryFiles(string root, IGitCommandRunner? git) : IRepositoryFiles
{
    private readonly string _root = Path.GetFullPath(root);

    /// <inheritdoc />
    public Task<IReadOnlyList<string>?> TrackedMarkdownAsync(CancellationToken ct) => TrackedMarkdown.ListAsync(git, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<RepositoryEntry>> ListAsync(string folder, CancellationToken ct)
    {
        var directory = Inside(folder);
        if (directory is null || !Directory.Exists(directory))
        {
            return Task.FromResult<IReadOnlyList<RepositoryEntry>>([]);
        }

        var entries = new List<RepositoryEntry>();
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var relative = Path.GetRelativePath(_root, path).Replace('\\', '/');
            try
            {
                entries.Add(Directory.Exists(path)
                    ? new RepositoryEntry(relative, true, 0)
                    : new RepositoryEntry(relative, false, new FileInfo(path).Length));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // One entry that cannot be described costs that entry, never the listing (as the bridge does).
            }
        }

        return Task.FromResult<IReadOnlyList<RepositoryEntry>>(entries);
    }

    /// <inheritdoc />
    public async Task<string?> ReadHeadAsync(string path, int maxBytes, CancellationToken ct)
    {
        if (Inside(path) is not { } file || !File.Exists(file))
        {
            return null;
        }

        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[Math.Max(0, maxBytes)];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false)) > 0)
        {
            total += read;
        }

        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    /// <inheritdoc />
    public async Task<string?> ReadAllAsync(string path, CancellationToken ct) =>
        Inside(path) is { } file && File.Exists(file) ? await File.ReadAllTextAsync(file, ct).ConfigureAwait(false) : null;

    private string? Inside(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(_root, (relative ?? string.Empty).Replace('/', Path.DirectorySeparatorChar)));
        return full == _root || full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
}

/// <summary>
/// The scan for the folder this window shows (item #373): over the host bridge when the window has a folder, over
/// <c>Repository:Root</c> in a dev run without one, and none otherwise. A walk (a folder that is not a repository) skips
/// what the folder's own <c>.gitignore</c> and Settings, Environment, Search ignore rules skip (ADR 0015).
/// </summary>
public sealed class WindowRepositoryScans(
    BridgeWorkspaceContext workspace,
    IServiceProvider services,
    IConfigurationStore settings,
    IOptions<RepositoryReaderOptions> local) : IRepositoryScans
{
    /// <inheritdoc />
    public async Task<RepositoryScan?> ForWindowAsync(CancellationToken ct = default)
    {
        var git = services.GetService(typeof(IGitCommandRunner)) as IGitCommandRunner;
        IRepositoryFiles files;
        if (workspace.IsBridgeActive && services.GetService(typeof(BridgeClient)) is BridgeClient client)
        {
            files = new BridgeRepositoryFiles(client, git);
        }
        else if (!workspace.IsBridgeConfigured && local.Value.Root is { Length: > 0 } root && Directory.Exists(root))
        {
            files = new LocalRepositoryFiles(root, git);
        }
        else
        {
            return null;
        }

        string? gitignore = null;
        try
        {
            gitignore = await files.ReadHeadAsync(".gitignore", 256 * 1024, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            // No .gitignore to read is the common case for a folder that is not a repository.
        }

        var rules = settings.Get(SearchDefaults.IgnoreRulesKey) is { Length: > 0 } stored ? stored : SearchDefaults.IgnoreRules;
        var ignore = GitignoreMatcher.FromRuleText(gitignore, rules);
        return new RepositoryScan(files, ignore.IsIgnored);
    }
}
