using Fravashi.Infrastructure.Search;

namespace Fravashi.Infrastructure.AgentSync;

/// <summary>
/// What the agent sync engine copies (plan 2d, proposed decisions 1+3): NEVER <c>.git</c> in
/// either direction (the host repository's index/refs are the studio's single source of git
/// truth; syncing them back would be the silent clobbering ADR 0016 forbids), and never
/// gitignored content (agent-side installs like node_modules stay on the volume copy only).
/// <para>
/// "Gitignored" means what the HOST's git ignores, by every source it honours (backlog 161, <see cref="HostGitIgnore"/>).
/// The rules of the repository's root <c>.gitignore</c> still apply as well: they are what decides synchronously for a
/// path the host has not been asked about yet, so an agent's <c>node_modules</c> is never walked while the answer is
/// on its way. Between them a path is synced only when neither says it is ignored.
/// </para>
/// </summary>
public sealed class WorkspaceSyncIgnore
{
    private readonly GitignoreMatcher _matcher;

    public WorkspaceSyncIgnore(GitignoreMatcher matcher, HostGitIgnore? host = null)
    {
        _matcher = matcher;
        Host = host ?? HostGitIgnore.None;
    }

    /// <summary>What the host's git ignores; asked by the session about a path that is new since the start.</summary>
    public HostGitIgnore Host { get; }

    public static WorkspaceSyncIgnore FromGitignoreText(string? gitignoreText, HostGitIgnore? host = null) =>
        new(GitignoreMatcher.FromRuleText(gitignoreText), host);

    public bool ShouldSync(string relativePath, bool isDirectory) => WhyWithheld(relativePath, isDirectory) is null;

    /// <summary>
    /// Why the path is not synced, in words for the log with the rule that decided it, or null when it is synced
    /// (backlog 253). A path the host's git TRACKS, or a folder holding one, is synced whatever the rules say, as in git:
    /// the root rules read here know nothing of tracking, and withheld a force-added file and the whole folder above it.
    /// </summary>
    public string? WhyWithheld(string relativePath, bool isDirectory)
    {
        var normalized = relativePath.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0)
        {
            return "it names the workspace root itself";
        }

        // Any path segment named .git (the repo itself, or a submodule/worktree pointer).
        foreach (var segment in normalized.Split('/'))
        {
            if (string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase))
            {
                return "it is inside .git, which never syncs";
            }
        }

        if (Host.Tracks(normalized, isDirectory))
        {
            return null;
        }

        if (_matcher.IgnoringRule(normalized, isDirectory) is { } rule)
        {
            return $"the repository's .gitignore rule '{rule}'";
        }

        return Host.Lists(normalized) ? Host.Why(normalized) : null;
    }
}
