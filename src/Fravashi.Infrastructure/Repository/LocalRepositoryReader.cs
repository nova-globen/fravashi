using Fravashi.Application.Repository;
using Fravashi.Domain.Repository;
using Microsoft.Extensions.Options;

namespace Fravashi.Infrastructure.Repository;

/// <summary>Options for the local repository reader: the root the explorer is bounded to.</summary>
public sealed class RepositoryReaderOptions
{
    public string Root { get; set; } = string.Empty;
}

/// <summary>
/// Read-only local-filesystem implementation of <see cref="IRepositoryReader"/>. Lists one directory level
/// at a time (lazy tree expansion) and reads files for preview. Every path is resolved and bounded to the
/// configured root: a request that escapes the root yields an empty node / null, never filesystem access
/// outside the root.
/// </summary>
public sealed class LocalRepositoryReader : IRepositoryReader
{
    private const long MaxPreviewBytes = 512 * 1024;
    private const long MaxImageBytes = 4 * 1024 * 1024;

    private static readonly HashSet<string> DeniedDirectories =
        new(StringComparer.OrdinalIgnoreCase) { ".git", "bin", "obj", "node_modules", ".vs", ".idea" };

    private readonly string _root;

    public LocalRepositoryReader(IOptions<RepositoryReaderOptions> options)
    {
        // No fallback root, deliberately: an unconfigured install must never expose the app's own
        // working directory as if it were the user's repository (ruling, 2026-07-24).
        var configured = options.Value.Root;
        _root = string.IsNullOrWhiteSpace(configured)
            ? string.Empty
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
    }

    public bool IsConfigured => _root.Length > 0;

    public string RootPath => _root;

    public Task<RepositoryNode> GetTreeAsync(string relativePath = "", CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(relativePath);
        if (!IsConfigured)
        {
            return Task.FromResult(RepositoryNode.Directory(string.Empty, normalized, []));
        }

        var full = ResolveWithinRoot(normalized);
        var name = string.IsNullOrEmpty(normalized) ? Path.GetFileName(_root) : Path.GetFileName(full ?? _root);

        if (full is null || !Directory.Exists(full))
        {
            return Task.FromResult(RepositoryNode.Directory(name, normalized, []));
        }

        return Task.FromResult(RepositoryNode.Directory(name, normalized, EnumerateChildren(full, normalized)));
    }

    public Task<RepositoryFile?> ReadFileAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(relativePath);
        var full = IsConfigured ? ResolveWithinRoot(normalized) : null;
        if (full is null || !File.Exists(full))
        {
            return Task.FromResult<RepositoryFile?>(null);
        }

        var info = new FileInfo(full);
        var lastWrite = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        if (info.Length > MaxPreviewBytes)
        {
            // IsTruncated matters: this placeholder must never be treated as editable content.
            return Task.FromResult<RepositoryFile?>(new RepositoryFile(
                normalized, "(file too large to preview)", string.Empty, lastWrite, IsTruncated: true));
        }

        var content = File.ReadAllText(full);
        return Task.FromResult<RepositoryFile?>(new RepositoryFile(normalized, content, LanguageFor(full), lastWrite));
    }

    public Task<byte[]?> ReadBytesAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(relativePath);
        var full = IsConfigured ? ResolveWithinRoot(normalized) : null;
        if (full is null || !File.Exists(full) || new FileInfo(full).Length > MaxImageBytes)
        {
            return Task.FromResult<byte[]?>(null);
        }

        return Task.FromResult<byte[]?>(File.ReadAllBytes(full));
    }

    private IReadOnlyList<RepositoryNode> EnumerateChildren(string fullDirectory, string normalizedPrefix)
    {
        var nodes = new List<RepositoryNode>();

        foreach (var dir in Directory.GetDirectories(fullDirectory).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var folderName = Path.GetFileName(dir);
            if (DeniedDirectories.Contains(folderName))
            {
                continue;
            }

            nodes.Add(RepositoryNode.Directory(folderName, Combine(normalizedPrefix, folderName), []));
        }

        foreach (var file in Directory.GetFiles(fullDirectory).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(file);
            nodes.Add(RepositoryNode.File(fileName, Combine(normalizedPrefix, fileName)));
        }

        return nodes;
    }

    private string? ResolveWithinRoot(string normalized)
    {
        var combined = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(_root, normalized)));
        if (string.Equals(combined, _root, StringComparison.OrdinalIgnoreCase))
        {
            return combined;
        }

        var rootWithSeparator = _root + Path.DirectorySeparatorChar;
        return combined.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) ? combined : null;
    }

    private static string Normalize(string? relativePath) =>
        (relativePath ?? string.Empty).Replace('\\', '/').Trim('/');

    private static string Combine(string prefix, string name) =>
        string.IsNullOrEmpty(prefix) ? name : $"{prefix}/{name}";

    private static string LanguageFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "csharp",
        ".razor" or ".cshtml" or ".html" or ".htm" => "html",
        ".csproj" or ".props" or ".targets" or ".xml" or ".slnx" => "xml",
        ".js" or ".mjs" => "javascript",
        ".ts" => "typescript",
        ".json" => "json",
        ".css" => "css",
        ".sql" => "sql",
        ".ps1" or ".psm1" => "powershell",
        ".sh" or ".bash" => "shell",
        ".py" => "python",
        ".java" => "java",
        ".md" or ".markdown" => "markdown",
        ".yml" or ".yaml" => "yaml",
        _ => string.Empty,
    };
}
