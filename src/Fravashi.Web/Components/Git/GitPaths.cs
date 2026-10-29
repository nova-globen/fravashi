namespace Fravashi.Web.Components.Git;

/// <summary>The slash arithmetic the Git page's trees share (changes by folder, branches by prefix).</summary>
internal static class GitPaths
{
    /// <summary>The last segment of a slash-separated path or branch name.</summary>
    public static string NameOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    /// <summary>Everything before the last slash, or empty at the root.</summary>
    public static string DirectoryOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash <= 0 ? string.Empty : path[..slash];
    }

    /// <summary>
    /// Whether a working-tree entry names a whole folder: git status reports an untracked folder as one entry whose
    /// path ends in a slash (<c>?? docs/evidences/run/</c>) rather than listing the files inside it (#369).
    /// </summary>
    public static bool IsFolderEntry(string path) => path.EndsWith('/');

    /// <summary>
    /// The name a working-tree entry's row shows in the tree: the file name, or for a folder entry the folder's own
    /// name with its trailing slash. Never empty, which is what NameOf answered for a folder entry (#369).
    /// </summary>
    public static string EntryNameOf(string path) =>
        IsFolderEntry(path) ? NameOf(path.TrimEnd('/')) + "/" : NameOf(path);

    /// <summary>The folder a working-tree entry's row sits in: a folder entry sits in its PARENT, not inside itself (#369).</summary>
    public static string EntryDirectoryOf(string path) => DirectoryOf(path.TrimEnd('/'));
}
