namespace Fravashi.Domain.AgentSessions;

/// <summary>
/// How much a person lets an agent do in a folder without being asked first (ADR 0028, backlog 152). It is a
/// property of the WORKSPACE, because it is a statement about the person's supervision of that repository
/// rather than about any one conversation, and it is chosen deliberately: nothing accumulates into it and
/// there is still no "remember this tool".
/// </summary>
public enum WorkspaceTrust
{
    /// <summary>Every change asks, every time. The default, and what every folder starts at.</summary>
    Ask = 0,

    /// <summary>
    /// An edit whose target is provably inside the session's contained copy, under every reading of the
    /// path, runs without asking. Everything else still asks: a command, a change outside the copy, a
    /// change the studio cannot place, a hidden path (see <see cref="WorkspaceTrustLevels.IsHiddenPath"/>),
    /// a path in the folder git runs hooks from (backlog 288, <see cref="WorkspaceHooksFolder"/>), a file
    /// git's own configuration includes (backlog 289, <see cref="WorkspaceConfigIncludes"/>), and every
    /// tool that is not an editor.
    /// </summary>
    Edits = 1,
}

/// <summary>The names a trust level is stored and shown under, and the rules that bound the Edits level.</summary>
public static class WorkspaceTrustLevels
{
    /// <summary>The stored name of a level; the inverse of <see cref="Parse"/>.</summary>
    public static string Name(WorkspaceTrust trust) => trust switch
    {
        WorkspaceTrust.Edits => "edits",
        _ => "ask",
    };

    /// <summary>
    /// A stored or posted name as a level. Anything unknown is <see cref="WorkspaceTrust.Ask"/>: a value the
    /// studio does not recognise must read as the level that asks, never as one that runs.
    /// </summary>
    public static WorkspaceTrust Parse(string? name) =>
        string.Equals(name?.Trim(), "edits", StringComparison.OrdinalIgnoreCase) ? WorkspaceTrust.Edits : WorkspaceTrust.Ask;

    /// <summary>What a person reads beside each level.</summary>
    public static string Describe(WorkspaceTrust trust) => trust switch
    {
        WorkspaceTrust.Edits => "Trust edits inside the copy",
        _ => "Ask before every change",
    };

    /// <summary>
    /// The tools that EDIT a file, by exact name: the only calls the Edits level ever covers. A command is
    /// not an edit, whatever it writes, and a studio tool is governed by its own list.
    /// </summary>
    public static bool IsEdit(string? toolName) =>
        toolName is "Write" or "Edit" or "MultiEdit" or "NotebookEdit";

    /// <summary>
    /// Whether a copy-relative path is HIDDEN: any segment starting with a dot. Those paths are the
    /// configuration plane rather than the code: <c>.githooks</c> run on the host at the next commit,
    /// <c>.forgejo</c> and <c>.github</c> workflows run in CI with its secrets at the next push, <c>.claude</c>
    /// is read by the agent CLI itself, and <c>.npmrc</c> or <c>.env</c> change what other tools do. A person
    /// who trusts edits to the code has not said they trust edits to those, so they keep asking.
    /// </summary>
    public static bool IsHiddenPath(string? copyRelativePath)
    {
        if (string.IsNullOrWhiteSpace(copyRelativePath))
        {
            return false;
        }

        foreach (var segment in copyRelativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Length > 1 && segment[0] == '.' && segment != "..")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Why a trusted edit to a path under the configured hooks folder still asks (backlog 288).</summary>
    public const string HooksFolderReason =
        "This path is in the folder git runs hooks from (core.hooksPath): git runs this folder's scripts on the host "
        + "at the next commit, so an edit here asks even though this folder trusts edits.";

    /// <summary>Why a trusted edit to a hooks-looking folder asks when the host's git could not be asked (backlog 288).</summary>
    public const string UnknownHooksFolderReason =
        "The studio could not ask the host's git which folder it runs hooks from (core.hooksPath), and this path looks "
        + "like a hooks folder: git may run its scripts on the host at the next commit, so an edit here asks.";

    /// <summary>Why a trusted edit to a file git's configuration includes still asks (backlog 289).</summary>
    public const string ConfigIncludeReason =
        "This path is a file git's configuration includes (include.path or includeif.<condition>.path): an edit "
        + "here can set what git reads on the host, including core.hooksPath, core.fsmonitor or core.sshCommand, "
        + "so an edit here asks even though this folder trusts edits.";

    /// <summary>
    /// Why every trusted edit asks when the host's git could not say which files its configuration includes
    /// (backlog 289). A hooks folder has a naming convention to fall back on (<see cref="LooksLikeHooksFolder"/>); an
    /// include can name any file at all, so there is no narrower fallback here, and the lookup failing widens all
    /// the way rather than not at all.
    /// </summary>
    public const string UnknownConfigIncludesReason =
        "The studio could not ask the host's git which files its configuration includes, and an include can name "
        + "any file in the work tree: every edit in this folder asks until the next successful lookup.";

    /// <summary>
    /// Why a copy-relative path is CONFIGURATION PLANE, which a trusted edit never covers, or null when it is not: a
    /// hidden path (<see cref="IsHiddenPath"/>), a file git's own configuration includes (backlog 289), a path under
    /// the folder <c>core.hooksPath</c> names, and, when that folder could not be learned, a path with a folder that
    /// looks like a hooks folder. Hidden paths answer an empty string rather than a sentence, because they asked
    /// without one before backlog 288 and still do.
    /// </summary>
    public static string? ConfigurationPlaneReason(string? copyRelativePath, WorkspaceHooksFolder hooks, WorkspaceConfigIncludes includes)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(includes);

        if (IsHiddenPath(copyRelativePath))
        {
            return string.Empty;
        }

        if (includes.State == WorkspaceConfigIncludesState.Unknown)
        {
            return UnknownConfigIncludesReason;
        }

        if (includes.Contains(copyRelativePath))
        {
            return ConfigIncludeReason;
        }

        if (hooks.Contains(copyRelativePath))
        {
            return HooksFolderReason;
        }

        return hooks.State == WorkspaceHooksFolderState.Unknown && LooksLikeHooksFolder(copyRelativePath)
            ? UnknownHooksFolderReason
            : null;
    }

    /// <summary>
    /// Whether any FOLDER on a copy-relative path has a name that reads like a hooks folder (<c>hooks</c>,
    /// <c>githooks</c>, <c>git-hooks</c>): what asks when the configured one is not known. Wide on purpose, because a
    /// miss runs unattended on the host and an extra match only asks.
    /// </summary>
    public static bool LooksLikeHooksFolder(string? copyRelativePath)
    {
        if (string.IsNullOrWhiteSpace(copyRelativePath))
        {
            return false;
        }

        var segments = copyRelativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return segments.Take(segments.Length - 1).Any(s => s.Contains("hook", StringComparison.OrdinalIgnoreCase));
    }
}
