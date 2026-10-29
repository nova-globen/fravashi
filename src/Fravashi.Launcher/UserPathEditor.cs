namespace Fravashi.Launcher;

/// <summary>
/// Idempotent editing of the user's <c>PATH</c> (F9). The pure algorithm - "add this directory unless it is
/// already present" - is separated from the registry so it can be unit-tested without touching real
/// environment state. The Windows installer (install-windows.ps1) applies the same rule to the actual
/// user Environment registry key; this class is the tested reference and the seam the tests drive.
/// </summary>
public static class UserPathEditor
{
    /// <summary>
    /// Returns the PATH that results from ensuring <paramref name="directory"/> is present exactly once.
    /// Comparison is case-insensitive and ignores a trailing directory separator, matching Windows PATH
    /// semantics, so re-running an install never appends a duplicate. Empty segments (from stray
    /// semicolons) are preserved except that no new empty segment is introduced.
    /// </summary>
    /// <returns>
    /// <c>Changed</c> is false when the directory was already present (the returned <c>NewPath</c> equals the
    /// input); true when it was appended.
    /// </returns>
    public static (string NewPath, bool Changed) Ensure(string? currentPath, string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var normalizedTarget = Normalize(directory);
        var existing = (currentPath ?? string.Empty)
            .Split(';', StringSplitOptions.None);

        if (existing.Any(segment => Normalize(segment) == normalizedTarget))
        {
            return (currentPath ?? string.Empty, Changed: false);
        }

        // Append, avoiding a leading/double semicolon when the current PATH is empty or already ends in one.
        var trimmed = (currentPath ?? string.Empty).TrimEnd(';');
        var newPath = string.IsNullOrEmpty(trimmed) ? directory : $"{trimmed};{directory}";
        return (newPath, Changed: true);
    }

    /// <summary>
    /// Applies <see cref="Ensure"/> through injected read/write delegates (the seam). In production the
    /// delegates read and write the Windows user Environment key; in tests they are in-memory. Writes only
    /// when the PATH actually changes. Returns true if a write occurred.
    /// </summary>
    public static bool Apply(Func<string?> read, Action<string> write, string directory)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(write);

        var (newPath, changed) = Ensure(read(), directory);
        if (changed)
        {
            write(newPath);
        }

        return changed;
    }

    private static string Normalize(string segment) =>
        segment.Trim().TrimEnd('/', '\\').ToUpperInvariant();
}
