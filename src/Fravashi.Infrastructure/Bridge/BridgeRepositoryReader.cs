using System.Text;
using Fravashi.Application.Repository;
using Fravashi.Domain.Repository;
using Fravashi.Infrastructure.Repository;

namespace Fravashi.Infrastructure.Bridge;

/// <summary>
/// <see cref="IRepositoryReader"/> over the host bridge (plan 2b-i): the Code page's tree and file
/// reads served from the ACTIVE host workspace folder. Mirrors LocalRepositoryReader's observable
/// behavior: one directory level per call, denied dirs hidden, dirs-then-files ordering, the same
/// preview/image caps and truncation sentinel, languages via <see cref="SourceLanguage"/>.
/// </summary>
public sealed class BridgeRepositoryReader(BridgeClient client) : IRepositoryReader
{
    private const int MaxPreviewBytes = 512 * 1024;
    private const int MaxImageBytes = 4 * 1024 * 1024;

    private static readonly HashSet<string> DeniedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".vs", ".idea",
    };

    private BridgeWorkspaceDto? _workspace;

    public bool IsConfigured => true; // constructed only when the bridge mode is active (DI factory)

    public string RootPath => Workspace()?.RootPath ?? string.Empty;

    public async Task<RepositoryNode> GetTreeAsync(string relativePath = "", CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(relativePath);
        var entries = await client.ListAsync(normalized, cancellationToken).ConfigureAwait(false);
        var children = new List<RepositoryNode>();
        foreach (var entry in entries)
        {
            if (entry.IsDirectory)
            {
                if (!DeniedDirectories.Contains(entry.Name))
                {
                    children.Add(RepositoryNode.Directory(entry.Name, entry.Path, []));
                }
            }
            else
            {
                children.Add(RepositoryNode.File(entry.Name, entry.Path));
            }
        }

        var ordered = children
            .OrderByDescending(c => c.IsDirectory)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var name = normalized.Length == 0
            ? Workspace()?.Name ?? string.Empty
            : normalized[(normalized.LastIndexOf('/') + 1)..];
        return RepositoryNode.Directory(name, normalized, ordered);
    }

    public async Task<RepositoryFile?> ReadFileAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(relativePath);
        var stat = await client.StatAsync(normalized, cancellationToken).ConfigureAwait(false);
        if (stat is null || stat.IsDirectory)
        {
            return null;
        }

        if (stat.Size > MaxPreviewBytes)
        {
            return new RepositoryFile(
                normalized, "(file too large to preview)", string.Empty, stat.ModifiedUtc, IsTruncated: true);
        }

        var content = await client.ReadAsync(normalized, cancellationToken).ConfigureAwait(false);
        if (content is null)
        {
            return null;
        }

        return new RepositoryFile(
            normalized,
            Encoding.UTF8.GetString(content.Bytes),
            SourceLanguage.For(normalized),
            content.ModifiedUtc ?? stat.ModifiedUtc);
    }

    public async Task<byte[]?> ReadBytesAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(relativePath);
        var stat = await client.StatAsync(normalized, cancellationToken).ConfigureAwait(false);
        if (stat is null || stat.IsDirectory || stat.Size > MaxImageBytes)
        {
            return null;
        }

        var content = await client.ReadAsync(normalized, cancellationToken).ConfigureAwait(false);
        return content?.Bytes;
    }

    private BridgeWorkspaceDto? Workspace()
    {
        if (_workspace is not null)
        {
            return _workspace;
        }

        try
        {
            // Sync-over-async is render-safe here: the client never captures a context
            // (ConfigureAwait(false) throughout), so the continuation lands on the thread pool.
            var workspaces = Task.Run(() => client.ListWorkspacesAsync(CancellationToken.None))
                .GetAwaiter().GetResult();
            _workspace = workspaces.FirstOrDefault(w => w.Id == client.WorkspaceId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Bridge briefly away: RootPath degrades to empty until the next call.
        }

        return _workspace;
    }

    private static string Normalize(string? path) => (path ?? string.Empty).Replace('\\', '/').Trim('/');
}
