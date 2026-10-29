namespace Fravashi.Bridge;

/// <summary>What a look at a remembered workspace folder could actually establish.</summary>
public enum WorkspacePathState
{
    /// <summary>The folder is there and can be granted.</summary>
    Available,

    /// <summary>
    /// The folder is not there and NOTHING says it ever stopped existing: the volume it lives on is
    /// not attached (an unmapped drive letter, an offline UNC root, an unplugged disk), a link on
    /// the way to it dangles, or an ancestor refuses to be read. This is routinely transient, so a
    /// caller must never turn it into "deleted" (and must never prune a grant on it).
    /// </summary>
    Unreachable,

    /// <summary>
    /// The folder is not there and its nearest surviving ancestor IS, on an attached volume that
    /// enumerates: the folder is simply not in it. As close to "gone" as a filesystem lets anyone
    /// get.
    /// </summary>
    Missing,
}

/// <summary>
/// Answers the only question the bridge's grant list may act on: is this folder here, definitely
/// not here, or merely out of reach right now?
/// <para>
/// <see cref="Directory.Exists(string)"/> alone cannot tell those apart, and treating its false as
/// "gone" is how a workspace on <c>Z:\</c> or an external disk loses its grant (and then its
/// recents row) just because fravashi.exe started before the drive was mapped. The rule here is
/// deliberately lopsided: only an affirmative look INSIDE a readable parent yields
/// <see cref="WorkspacePathState.Missing"/>, and everything ambiguous yields
/// <see cref="WorkspacePathState.Unreachable"/>.
/// </para>
/// <para>
/// Known limit: a Unix path under a directory that is empty because its filesystem is not mounted
/// is indistinguishable from a deleted folder, since the empty mount point enumerates fine. Windows
/// (where fravashi.exe hosts the bridge) is covered: unmapped and offline roots fail the root check,
/// and detached volume mount points and dangling junctions fail the reparse-point / enumeration
/// checks.
/// </para>
/// </summary>
public static class WorkspacePathProbe
{
    public static WorkspacePathState Probe(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return WorkspacePathState.Missing;
        }

        if (Directory.Exists(path))
        {
            return WorkspacePathState.Available;
        }

        var root = RootOf(path);
        if (root is null || !Directory.Exists(root))
        {
            // There is no volume to look in: an unmapped drive letter, a disk that is not plugged
            // in, a UNC root whose server is not answering. None of that says the folder is gone.
            return WorkspacePathState.Unreachable;
        }

        return NearestAncestorVerdict(path);
    }

    private static string? RootOf(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            return string.IsNullOrEmpty(root) ? null : root;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Walks up to the first ancestor that exists and decides on THAT: a plain readable directory
    /// means the folder really is not there; anything else (a link, a detached mount, a directory
    /// that will not enumerate) means we cannot see well enough to say so.
    /// </summary>
    private static WorkspacePathState NearestAncestorVerdict(string path)
    {
        var current = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(current))
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // This ancestor is not there either: keep walking towards the volume root.
                current = Path.GetDirectoryName(current);
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or NotSupportedException or ArgumentException)
            {
                // It is there but will not answer (a detached volume mount point, a denied ACL).
                return WorkspacePathState.Unreachable;
            }

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                // A junction, symlink or volume mount point: what is behind it may simply be
                // detached right now, so its contents (or emptiness) prove nothing either way.
                return WorkspacePathState.Unreachable;
            }

            if (!attributes.HasFlag(FileAttributes.Directory))
            {
                // A FILE stands where a parent folder would have to be: this is not coming back.
                return WorkspacePathState.Missing;
            }

            return CanEnumerate(current) ? WorkspacePathState.Missing : WorkspacePathState.Unreachable;
        }

        return WorkspacePathState.Unreachable;
    }

    private static bool CanEnumerate(string directory)
    {
        try
        {
            // Enumeration is lazy, so the first MoveNext is what actually asks the filesystem.
            using var entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
            entries.MoveNext();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>
/// The folder cannot be reached RIGHT NOW and nothing says it is gone (see
/// <see cref="WorkspacePathState.Unreachable"/>). Deliberately NOT a
/// <see cref="DirectoryNotFoundException"/>: every caller that would prune a grant, delete a
/// recents row, or tell the user their folder no longer exists has to handle this case separately.
/// </summary>
public sealed class WorkspaceUnreachableException(string folderPath, string message) : IOException(message)
{
    /// <summary>The canonical folder path that could not be reached.</summary>
    public string FolderPath { get; } = folderPath;
}
