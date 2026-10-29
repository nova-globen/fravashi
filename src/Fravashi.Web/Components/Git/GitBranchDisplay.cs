using Fravashi.Domain.Git;

namespace Fravashi.Web.Components.Git;

/// <summary>
/// How a branch's publish/sync state reads, in one place.
///
/// The Git page's branch list and the title bar's branch dropdown are two views of the same fact, and
/// backlog 31 asks for them to agree. Agreement by copy is agreement until the next edit, so the words
/// and the state class both come from here and the stylesheet colours the class once for both surfaces.
/// </summary>
public static class GitBranchDisplay
{
    /// <summary>Local branch, not on any remote: nothing to be ahead of or behind.</summary>
    public const string Unpublished = "git-b-unpublished";

    /// <summary>Remote-only: it exists on a remote and has no local branch of that name yet.</summary>
    public const string RemoteOnly = "git-b-remote";

    /// <summary>The state class for a branch row, which is what the stylesheet colours.</summary>
    public static string StateClass(GitBranch branch) =>
        !branch.IsPublished ? Unpublished
        : branch.Ahead > 0 && branch.Behind > 0 ? "git-b-diverged"
        : branch.Ahead > 0 ? "git-b-ahead"
        : branch.Behind > 0 ? "git-b-behind"
        : "git-b-synced";

    /// <summary>The short state line: "synced", the arrow counts, or "not published".</summary>
    public static string StateText(GitBranch branch) =>
        !branch.IsPublished ? "not published"
        : branch.Ahead > 0 && branch.Behind > 0 ? $"↑{branch.Ahead} ↓{branch.Behind}"
        : branch.Ahead > 0 ? $"↑{branch.Ahead}"
        : branch.Behind > 0 ? $"↓{branch.Behind}"
        : "synced";

    /// <summary>The long form, for a tooltip: the same state spelled out with its upstream.</summary>
    public static string StateTitle(GitBranch branch) =>
        branch.IsCurrent ? "Current branch"
        : !branch.IsPublished ? $"{branch.Name}: not published to a remote. Click to switch."
        : $"{branch.Name}: {branch.Ahead} ahead, {branch.Behind} behind {branch.Upstream}. Click to switch.";

    /// <summary>
    /// What a remote-only branch says about itself. It names the remote rather than saying "remote",
    /// because on a repository with two remotes which one it is decides what checking it out tracks.
    /// </summary>
    public static string RemoteOnlyText(GitRemoteBranch branch) => $"on {branch.Remote}, not local yet";
}
