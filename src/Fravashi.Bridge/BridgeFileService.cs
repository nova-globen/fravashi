namespace Fravashi.Bridge;

/// <summary>A file-system entry inside a workspace; <see cref="Path"/> is workspace-relative with <c>/</c> separators.</summary>
public sealed record BridgeEntry(string Name, string Path, bool IsDirectory, long Size, DateTimeOffset ModifiedUtc);

/// <summary>
/// A guarded write was refused: the target changed since the caller read it (stale editor buffer),
/// or it vanished entirely (<see cref="Missing"/>). Maps to 412/410 at the HTTP surface so the
/// studio's save-conflict flow (ADR 0016: never silently clobber) works over the bridge.
/// </summary>
public sealed class BridgeWriteConflictException(bool missing) : Exception
{
    public bool Missing { get; } = missing;
}

/// <summary>
/// Workspace-scoped file operations. Every entry point resolves through
/// <see cref="WorkspaceRegistry.ResolvePath"/> FIRST, so containment is enforced in exactly one
/// place. Writes go to a sibling temp file and move into place so a crashed write never leaves a
/// half-written target.
/// </summary>
public sealed class BridgeFileService(WorkspaceRegistry registry)
{
    private const string TempSuffix = ".fravashi-tmp";

    public IReadOnlyList<BridgeEntry> ListDirectory(string id, string? relativePath)
    {
        var root = registry.TryGet(id)?.RootPath ?? throw new KeyNotFoundException($"Unknown workspace '{id}'.");
        var directory = registry.ResolvePath(id, relativePath);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"No such directory: {relativePath}");
        }

        var entries = new List<BridgeEntry>();
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            // One entry that cannot be described costs that entry, never the listing. Found 2026-09-29: a file named
            // NUL in a repository root (Git Bash's ssh writes one when handed Windows' null device as a file name)
            // made every listing of that root throw, and the agent's copy of the folder silently never started. A
            // reserved device name cannot be opened as a file anyway, and a file deleted between the listing and
            // its stat is simply gone; either way it is left out as though absent.
            try
            {
                entries.Add(ToEntry(root, path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return
        [
            .. entries
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    public BridgeEntry Stat(string id, string relativePath)
    {
        var root = registry.TryGet(id)?.RootPath ?? throw new KeyNotFoundException($"Unknown workspace '{id}'.");
        var path = registry.ResolvePath(id, relativePath);
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new FileNotFoundException($"No such entry: {relativePath}");
        }

        return ToEntry(root, path);
    }

    public Stream OpenRead(string id, string relativePath)
    {
        var path = registry.ResolvePath(id, relativePath);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }

    public Task WriteAsync(string id, string relativePath, Stream content, CancellationToken cancellationToken)
        => WriteAsync(id, relativePath, content, expectedModifiedUtc: null, cancellationToken);

    /// <summary>
    /// Writes with an optional freshness precondition: when <paramref name="expectedModifiedUtc"/>
    /// is given, a missing target or one whose timestamp moved beyond a 2s tolerance refuses with
    /// <see cref="BridgeWriteConflictException"/> (matching LocalRepositoryWriter's semantics).
    /// Returns the new last-write stamp.
    /// </summary>
    public async Task<DateTimeOffset> WriteAsync(
        string id, string relativePath, Stream content, DateTimeOffset? expectedModifiedUtc,
        CancellationToken cancellationToken)
    {
        var path = registry.ResolvePath(id, relativePath);
        if (expectedModifiedUtc is { } expected)
        {
            if (!File.Exists(path))
            {
                throw new BridgeWriteConflictException(missing: true);
            }

            var current = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            if ((current - expected).Duration() > TimeSpan.FromSeconds(2))
            {
                throw new BridgeWriteConflictException(missing: false);
            }
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

        var temp = path + TempSuffix;
        try
        {
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch (IOException) { }
            }
        }

        return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
    }

    public void CreateDirectory(string id, string relativePath)
        => Directory.CreateDirectory(registry.ResolvePath(id, relativePath));

    public void Delete(string id, string relativePath)
    {
        var path = registry.ResolvePath(id, relativePath);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
        else
        {
            throw new FileNotFoundException($"No such entry: {relativePath}");
        }
    }

    public void Rename(string id, string fromRelative, string toRelative)
    {
        var from = registry.ResolvePath(id, fromRelative);
        var to = registry.ResolvePath(id, toRelative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to)!);
        if (Directory.Exists(from))
        {
            Directory.Move(from, to);
        }
        else
        {
            File.Move(from, to);
        }
    }

    private static BridgeEntry ToEntry(string root, string fullPath)
    {
        var relative = System.IO.Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        if (Directory.Exists(fullPath))
        {
            var info = new DirectoryInfo(fullPath);
            return new BridgeEntry(info.Name, relative, IsDirectory: true, Size: 0, info.LastWriteTimeUtc);
        }

        var file = new FileInfo(fullPath);
        return new BridgeEntry(file.Name, relative, IsDirectory: false, file.Length, file.LastWriteTimeUtc);
    }
}
