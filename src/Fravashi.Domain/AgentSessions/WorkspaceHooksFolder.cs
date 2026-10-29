namespace Fravashi.Domain.AgentSessions;

/// <summary>What the studio knows about where git runs a workspace's hooks from (backlog 288).</summary>
public enum WorkspaceHooksFolderState
{
    /// <summary>
    /// No configured hooks folder lands inside the copy: <c>core.hooksPath</c> is unset (git then runs
    /// <c>.git/hooks</c>, which is hidden and never synced), or it points outside the repository.
    /// </summary>
    None = 0,

    /// <summary><c>core.hooksPath</c> names a folder inside the copy; <see cref="WorkspaceHooksFolder.Folder"/> says which.</summary>
    At = 1,

    /// <summary>The host's git could not be asked, so the studio cannot say which folder git runs hooks from.</summary>
    Unknown = 2,
}

/// <summary>
/// The folder git runs a workspace's hooks from, in the contained copy's coordinates (backlog 288). ADR 0028 keeps hidden
/// paths asking because hooks run on the host at the next commit, but git runs them from wherever <c>core.hooksPath</c>
/// points, and that can be an ordinary folder such as <c>scripts/hooks</c>. That folder is configuration plane too.
/// </summary>
public sealed record WorkspaceHooksFolder
{
    private WorkspaceHooksFolder(WorkspaceHooksFolderState state, string? folder)
    {
        State = state;
        Folder = folder;
    }

    public WorkspaceHooksFolderState State { get; }

    /// <summary>
    /// The copy-relative folder, forward slashes, when <see cref="State"/> is <see cref="WorkspaceHooksFolderState.At"/>.
    /// Empty means the whole copy: the hooks folder is the workspace folder or a folder above it.
    /// </summary>
    public string? Folder { get; }

    /// <summary>No configured hooks folder inside the copy: the rule is exactly ADR 0028's.</summary>
    public static WorkspaceHooksFolder None { get; } = new(WorkspaceHooksFolderState.None, null);

    /// <summary>The lookup failed. A path that looks like a hooks folder then asks rather than runs.</summary>
    public static WorkspaceHooksFolder Unknown { get; } = new(WorkspaceHooksFolderState.Unknown, null);

    public static WorkspaceHooksFolder At(string copyRelativeFolder) =>
        new(WorkspaceHooksFolderState.At, (copyRelativeFolder ?? string.Empty).Replace('\\', '/').Trim('/'));

    /// <summary>
    /// Maps the host git's answer into the copy. <paramref name="hooksPath"/> is <c>core.hooksPath</c> (null when unset),
    /// <paramref name="topLevel"/> the repository root (<c>git rev-parse --show-toplevel</c>) and <paramref name="prefix"/>
    /// the workspace folder relative to it (<c>--show-prefix</c>, empty when the workspace IS the root). A relative
    /// value is taken against the repository root, which is where git runs hooks in a work tree. The arithmetic is
    /// lexical and compares without regard to case (the host is Windows), and every doubt lands on the side that asks:
    /// a folder that only differs in case is still the hooks folder, and a hooks folder above the workspace covers it all.
    /// A value that lands outside the repository, or one git left unexpanded (<c>~</c>), changes nothing in the copy.
    /// </summary>
    public static WorkspaceHooksFolder Resolve(string? hooksPath, string topLevel, string? prefix)
    {
        if (string.IsNullOrWhiteSpace(hooksPath))
        {
            return None;
        }

        var value = hooksPath.Trim().Replace('\\', '/');
        if (value.StartsWith('~') || Collapse(topLevel.Trim().Replace('\\', '/')) is not { } top)
        {
            return None;
        }

        var full = IsAbsolute(value) ? value : top + "/" + value;
        if (Collapse(full) is not { } collapsed || Below(collapsed, top) is not { } inRepository)
        {
            return None;
        }

        var workspace = (prefix ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        if (Below(inRepository, workspace) is { } inCopy)
        {
            return At(inCopy);
        }

        // The hooks folder holds the workspace folder: everything in the copy is under it.
        return Below(workspace, inRepository) is not null ? At(string.Empty) : None;
    }

    /// <summary>Whether a copy-relative path is this folder or inside it.</summary>
    public bool Contains(string? copyRelativePath) =>
        State == WorkspaceHooksFolderState.At
        && !string.IsNullOrWhiteSpace(copyRelativePath)
        && Below(copyRelativePath.Replace('\\', '/').Trim('/'), Folder ?? string.Empty) is not null;

    /// <summary>Shared with <see cref="WorkspaceConfigIncludeTarget"/> (backlog 289), which places include targets the same way.</summary>
    internal static bool IsAbsolute(string path) =>
        path.StartsWith('/') || (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]));

    /// <summary>
    /// Removes <c>.</c> and resolves <c>..</c> lexically; null when <c>..</c> climbs past the start. Shared with
    /// <see cref="WorkspaceConfigIncludeTarget"/> (backlog 289).
    /// </summary>
    internal static string? Collapse(string path)
    {
        if (path.Length == 0)
        {
            return null;
        }

        var rooted = path.StartsWith('/');
        var parts = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (parts.Count == 0 || (parts.Count == 1 && parts[0].EndsWith(':')))
                {
                    return null;
                }

                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(segment);
        }

        return (rooted ? "/" : string.Empty) + string.Join('/', parts);
    }

    /// <summary>
    /// The part of <paramref name="path"/> below <paramref name="folder"/> ("" when equal), or null when it is not
    /// below. Shared with <see cref="WorkspaceConfigIncludeTarget"/> (backlog 289).
    /// </summary>
    internal static string? Below(string path, string folder)
    {
        if (folder.Length == 0 || folder == "/")
        {
            return folder.Length == 0 ? path : path.TrimStart('/');
        }

        if (string.Equals(path, folder, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return path.Length > folder.Length
            && path[folder.Length] == '/'
            && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                ? path[(folder.Length + 1)..]
                : null;
    }
}
