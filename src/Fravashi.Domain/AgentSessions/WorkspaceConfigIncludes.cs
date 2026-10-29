namespace Fravashi.Domain.AgentSessions;

/// <summary>What the studio knows about the work-tree files the repository's own git config includes (backlog 289).</summary>
public enum WorkspaceConfigIncludesState
{
    /// <summary>No <c>include.path</c> or <c>includeif.&lt;condition&gt;.path</c> anywhere in the chain names a file inside the copy.</summary>
    None = 0,

    /// <summary>One or more files inside the copy are named; <see cref="WorkspaceConfigIncludes.Files"/> says which.</summary>
    Known = 1,

    /// <summary>The host's git could not be asked, or the chain could not be read all the way, so the studio cannot say.</summary>
    Unknown = 2,
}

/// <summary>
/// The work-tree files named by <c>include.path</c> or <c>includeif.&lt;condition&gt;.path</c> anywhere in the
/// repository's own config chain, in the contained copy's coordinates (backlog 289, follow-up of #288's
/// <see cref="WorkspaceHooksFolder"/>). Unlike a hooks folder this is a set of individual FILES rather than a
/// folder prefix, because an include names one file. Through one, a trusted edit could set
/// <c>core.fsmonitor</c> (runs on the host at the next unattended <c>git_status</c>), <c>core.sshCommand</c>
/// (the next fetch or push), <c>core.hooksPath</c> (the folder #288 already guards), or a diff, merge or
/// filter driver. A file counts whether or not it exists yet, because creating it is the same act as editing
/// it, and whether or not an <c>includeif</c> condition on it currently holds, because an <c>onbranch</c>
/// condition inactive now can become active after a checkout and a <c>gitdir</c> condition after the
/// repository is moved or re-cloned.
/// </summary>
public sealed record WorkspaceConfigIncludes
{
    private static readonly IReadOnlySet<string> EmptyFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private readonly IReadOnlySet<string> _files;

    private WorkspaceConfigIncludes(WorkspaceConfigIncludesState state, IReadOnlySet<string> files)
    {
        State = state;
        _files = files;
    }

    public WorkspaceConfigIncludesState State { get; }

    /// <summary>The copy-relative files, forward slashes, when <see cref="State"/> is <see cref="WorkspaceConfigIncludesState.Known"/>.</summary>
    public IReadOnlySet<string> Files => _files;

    /// <summary>No included file lands inside the copy: the rule is exactly ADR 0028's as #288 left it.</summary>
    public static WorkspaceConfigIncludes None { get; } = new(WorkspaceConfigIncludesState.None, EmptyFiles);

    /// <summary>
    /// The lookup failed. Unlike a hooks folder, an include can name any file at all, so there is no narrower
    /// naming convention to fall back on: every trusted edit in the workspace asks until the next successful
    /// lookup (backlog 289, decided in plan @112 because no heuristic here is safe to trust).
    /// </summary>
    public static WorkspaceConfigIncludes Unknown { get; } = new(WorkspaceConfigIncludesState.Unknown, EmptyFiles);

    /// <summary>The known set of copy-relative files, normalized; <see cref="None"/> when it ends up empty.</summary>
    public static WorkspaceConfigIncludes Of(IEnumerable<string> copyRelativeFiles)
    {
        ArgumentNullException.ThrowIfNull(copyRelativeFiles);

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in copyRelativeFiles)
        {
            var normalized = (file ?? string.Empty).Replace('\\', '/').Trim('/');
            if (normalized.Length > 0)
            {
                files.Add(normalized);
            }
        }

        return files.Count == 0 ? None : new WorkspaceConfigIncludes(WorkspaceConfigIncludesState.Known, files);
    }

    /// <summary>Whether a copy-relative path IS one of these files: an exact match, never a prefix, because an include names a file.</summary>
    public bool Contains(string? copyRelativePath) =>
        State == WorkspaceConfigIncludesState.Known
        && !string.IsNullOrWhiteSpace(copyRelativePath)
        && _files.Contains(copyRelativePath.Replace('\\', '/').Trim('/'));
}

/// <summary>One include target placed in the copy's coordinates (backlog 289).</summary>
/// <param name="AbsolutePath">
/// The include value resolved to an absolute, forward-slash host path, present or not, in or out of the
/// repository: the next file to read for further nested includes.
/// </param>
/// <param name="CopyRelativePath">
/// Where it lands inside the workspace's copy, or null when it is outside the repository, or inside the
/// repository but outside this workspace's own subtree.
/// </param>
public sealed record ResolvedConfigInclude(string AbsolutePath, string? CopyRelativePath);

/// <summary>
/// Places one raw <c>include.path</c>/<c>includeif.*.path</c> VALUE in the copy's coordinates: the same
/// lexical arithmetic <see cref="WorkspaceHooksFolder"/> uses for <c>core.hooksPath</c> (backlog 289), because
/// git resolves both the same way. A relative value is taken against the directory of the config file that
/// names it, which for <c>.git/config</c> is <c>.git/</c>: an <c>include.path = config/git.inc</c> written in
/// <c>.git/config</c> lands at <c>.git/config/git.inc</c> (hidden, under the git directory, never a work-tree
/// file an agent could edit), and only <c>../config/git.inc</c> reaches the work tree.
/// </summary>
public static class WorkspaceConfigIncludeTarget
{
    /// <param name="rawValue">
    /// The value as git prints it with <c>--type=path</c>, which has already expanded a leading <c>~/</c>: a
    /// value still starting with <c>~</c> here is one git itself could not expand (no home directory on the
    /// host that answered), and is unplaceable rather than guessed.
    /// </param>
    /// <param name="namingFileDirectory">The absolute, forward-slash directory of the config file that names this include.</param>
    /// <param name="topLevel">The repository root (<c>git rev-parse --show-toplevel</c>).</param>
    /// <param name="prefix">The workspace folder relative to the root (<c>--show-prefix</c>), empty when the workspace IS the root.</param>
    /// <returns>Null when the value cannot be placed at all (an unrooted relative base, or an unexpanded <c>~</c>).</returns>
    public static ResolvedConfigInclude? Resolve(string rawValue, string namingFileDirectory, string topLevel, string? prefix)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        var value = rawValue.Trim().Replace('\\', '/');
        if (value.StartsWith('~'))
        {
            return null;
        }

        if (WorkspaceHooksFolder.Collapse(namingFileDirectory.Trim().Replace('\\', '/')) is not { } baseDirectory)
        {
            return null;
        }

        var full = WorkspaceHooksFolder.IsAbsolute(value) ? value : baseDirectory + "/" + value;
        if (WorkspaceHooksFolder.Collapse(full) is not { } absolute)
        {
            return null;
        }

        if (WorkspaceHooksFolder.Collapse(topLevel.Trim().Replace('\\', '/')) is not { } top
            || WorkspaceHooksFolder.Below(absolute, top) is not { } inRepository)
        {
            return new ResolvedConfigInclude(absolute, null);
        }

        var workspace = (prefix ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        return new ResolvedConfigInclude(absolute, WorkspaceHooksFolder.Below(inRepository, workspace));
    }

    /// <summary>
    /// Places one ORIGIN <c>git config --show-origin</c> prints (method 2, backlog 289): already relative to the
    /// REPOSITORY ROOT with <c>.git/</c> baked in (a relative origin for an included file reads literally as
    /// <c>.git/../config/git.inc</c>), never relative to some other file's directory the way a raw include VALUE
    /// is. Null when it lands outside the repository or outside this workspace.
    /// </summary>
    public static string? ResolveOrigin(string origin, string topLevel, string? prefix)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return null;
        }

        var value = origin.Trim().Replace('\\', '/');
        if (WorkspaceHooksFolder.Collapse(topLevel.Trim().Replace('\\', '/')) is not { } top)
        {
            return null;
        }

        var full = WorkspaceHooksFolder.IsAbsolute(value) ? value : top + "/" + value;
        if (WorkspaceHooksFolder.Collapse(full) is not { } absolute
            || WorkspaceHooksFolder.Below(absolute, top) is not { } inRepository)
        {
            return null;
        }

        var workspace = (prefix ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        return WorkspaceHooksFolder.Below(inRepository, workspace);
    }

    /// <summary>
    /// Places a path <c>git rev-parse --git-path</c> answers, which is relative to the directory the git PROCESS
    /// ran in rather than to the repository root whenever those differ (a workspace rooted at a repository
    /// sub-folder; backlog 289), against <paramref name="workingDirectory"/> (the absolute, forward-slash folder
    /// the process actually ran in). Absolute already is returned unchanged. Never null: an unresolvable join
    /// (a climb past the start) falls
    /// back to the uncollapsed join, which at worst names a file that then answers "not found", already handled
    /// as an ordinary leaf rather than an error.
    /// </summary>
    public static string ResolveHostPath(string value, string workingDirectory)
    {
        var normalized = value.Trim().Replace('\\', '/');
        if (WorkspaceHooksFolder.IsAbsolute(normalized))
        {
            return normalized;
        }

        var full = workingDirectory.Trim().Replace('\\', '/') + "/" + normalized;
        return WorkspaceHooksFolder.Collapse(full) ?? full;
    }
}
