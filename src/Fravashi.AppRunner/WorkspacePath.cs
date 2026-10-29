namespace Fravashi.AppRunner;

/// <summary>
/// Resolves a path a CALLER names (a spec file, a Dockerfile, a build context) inside a workspace folder, refusing
/// anything that could leave it (backlog 87). Two rules, both measured elsewhere in this product the hard way
/// (CLAUDE.md, the containment rounds): a path must stay inside the folder LEXICALLY, and no component of it may be
/// a link, because a link is exactly how a path that reads as inside lands outside. The app runner runs on the
/// HOST, so reading a file through a link here would read the host's own files into a refusal or an image.
/// </summary>
public static class WorkspacePath
{
    /// <summary>The full path of <paramref name="relative"/> inside <paramref name="root"/>, or null when it escapes or passes through a link.</summary>
    public static string? Resolve(string root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(root) || relative is null || relative.Contains('\0', StringComparison.Ordinal))
        {
            return null;
        }

        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var trimmed = relative.Replace('\\', '/').Trim();
        if (trimmed.StartsWith('/') || Path.IsPathRooted(trimmed) || trimmed.Split('/').Any(part => part == ".."))
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(rootFull, trimmed));
        if (!IsInside(rootFull, full))
        {
            return null;
        }

        // Every component from the root down, the target included, must be a plain entry.
        var current = rootFull;
        foreach (var part in Path.GetRelativePath(rootFull, full).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            current = Path.Combine(current, part);
            if (IsLink(current))
            {
                return null;
            }
        }

        return full;
    }

    /// <summary>True when <paramref name="path"/> is a symbolic link or another reparse point (a junction).</summary>
    public static bool IsLink(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return info.Exists && (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cannot tell: treat as a link, which refuses.
            return true;
        }
    }

    private static bool IsInside(string root, string full)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(full, root, comparison)
            || full.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }
}
