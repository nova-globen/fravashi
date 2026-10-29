using Fravashi.Application.Git;
using Fravashi.Domain.Git;
using Fravashi.Infrastructure.Sessions.Tools;

namespace Fravashi.Infrastructure.Git;

/// <summary>
/// Carries a file's owner-executable bit from an agent's contained copy into a host commit (#327). The
/// host's git has <c>core.filemode false</c> on Windows, so it never looks at a file's permission bits at
/// all: a script an agent ran <c>chmod +x</c> on in the copy (Linux, where the bit is real) would
/// otherwise reach the host index as <c>100644</c> whatever the copy says. For each staged path the copy
/// holds as owner-executable, <see cref="IGitWorkingTreeService.MarkExecutableAsync"/> tells the host
/// index so explicitly, under the same write gate every other mutation there takes and with the same
/// failure reported rather than swallowed.
/// <para>
/// <b>Only ever adds the bit.</b> A path whose copy file is not executable is left exactly as staging
/// found it, so a file the repository already tracks as <c>100755</c> is never demoted just because the
/// sync engine's own writes into the copy (<c>WorkspaceSyncSession</c>, plain <c>File.WriteAllBytesAsync</c>,
/// no mode set) happen to leave it without the bit there.
/// </para>
/// </summary>
public static class ExecutableBitStaging
{
    /// <summary>
    /// THE decision (#330): which of these paths the copy holds as owner-executable, so a commit staging
    /// them must mark them so on the host. The one place that answers this, so <see cref="MarkAsync"/> (the
    /// commit) and the commit reviewer's prompt (which paths it says will land as <c>100755</c>) are reading
    /// the same fact and can never disagree.
    /// </summary>
    public static IReadOnlyList<string> FilesToMark(string copyPath, IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var marked = new List<string>();
        foreach (var path in paths)
        {
            if (IsExecutableInCopy(copyPath, path))
            {
                marked.Add(path);
            }
        }

        return marked;
    }

    /// <summary>
    /// For each entry already staged on the host (not deleted), marks it executable when <see cref="FilesToMark"/>
    /// says the copy holds it as owner-executable. Nothing is unmarked. A failure from <paramref name="tree"/>
    /// propagates rather than being swallowed, so the caller's own failure handling (a failed commit, never a
    /// quiet success) applies; the same is true of a path that IS executable in the copy but fails
    /// <see cref="GitToolArguments.NormalizePath"/> (the same gate every agent-supplied path already
    /// passes through before it becomes a git argument), which throws rather than being dropped, because
    /// dropping it would leave an executable file committed as <c>100644</c> with the commit reported a
    /// success. A path that is NOT executable in the copy is skipped whatever its shape, since there is
    /// nothing to mark either way.
    /// </summary>
    public static Task MarkAsync(
        IGitWorkingTreeService tree, string copyPath, IReadOnlyList<GitWorkingEntry> staged, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(staged);

        var eligible = staged.Where(e => e.State != GitEntryState.Deleted).Select(e => e.Path).ToList();
        var candidates = new List<string>();
        foreach (var path in FilesToMark(copyPath, eligible))
        {
            if (GitToolArguments.NormalizePath(path) is not { } safe || safe != path)
            {
                throw new InvalidOperationException(
                    $"'{path}' is executable in the copy but could not be marked so on the host: {GitToolArguments.PathRule}");
            }

            candidates.Add(safe);
        }

        return candidates.Count == 0 ? Task.CompletedTask : tree.MarkExecutableAsync(candidates, cancellationToken);
    }

    private static bool IsExecutableInCopy(string copyPath, string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // The bit this method cares about is real only on the Linux container the copy lives in.
            return false;
        }

        var local = Path.Combine(copyPath, path.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            return File.Exists(local) && File.GetUnixFileMode(local).HasFlag(UnixFileMode.UserExecute);
        }
        catch (IOException)
        {
            return false;
        }
    }
}
