using System.Diagnostics;

namespace Fravashi.AppRunner;

/// <summary>What was assembled for a build: the folder, and what was deliberately left out.</summary>
/// <param name="Directory">A private temporary folder holding exactly the files the image may see.</param>
/// <param name="Files">How many files were copied.</param>
/// <param name="Bytes">How many bytes.</param>
/// <param name="SkippedLinks">Links found and NOT followed, relative to the workspace, so leaving them out is visible.</param>
public sealed record AssembledContext(string Directory, int Files, long Bytes, IReadOnlyList<string> SkippedLinks);

/// <summary>
/// Assembles a build context from a workspace folder (ADR 0027, "What a run may mount: nothing"). The source reaches
/// the image ONLY through this folder, so it is where the exclusions live: the same ones the sync engine honours,
/// <c>.git</c> and everything gitignored, taken from git itself (<c>git ls-files --cached --others
/// --exclude-standard</c>) rather than re-implemented, with a plain walk that skips <c>.git</c> when the folder is
/// not a repository.
/// <para>
/// No link is ever followed. The app runner runs on the HOST, and copying through a link would put the host's own
/// files into an image the application (and, through the verifier, an agent) can read. A link is skipped and named.
/// A context larger than <see cref="MaxBytes"/> is refused rather than copied, because an unbounded copy on the
/// person's disk is a denial of service that reads like a feature.
/// </para>
/// </summary>
public static class BuildContext
{
    public const long MaxBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// Copies the files under <paramref name="contextRelative"/> into a new folder under <paramref name="scratchRoot"/>,
    /// keeping their paths relative to that context. Throws <see cref="InvalidOperationException"/> with a reason when
    /// the context cannot be assembled.
    /// </summary>
    public static AssembledContext Assemble(string workspaceRoot, string contextRelative, string scratchRoot, string gitExecutable = "git")
    {
        var contextFull = WorkspacePath.Resolve(workspaceRoot, contextRelative)
            ?? throw new InvalidOperationException("the build context leaves the workspace or passes through a link");
        if (!Directory.Exists(contextFull))
        {
            throw new InvalidOperationException("the build context is not a folder in the workspace");
        }

        var files = ListFiles(workspaceRoot, contextFull, gitExecutable);
        var target = Path.Combine(scratchRoot, "ctx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        var skipped = new List<string>();
        long bytes = 0;
        var copied = 0;
        try
        {
            foreach (var relative in files)
            {
                var source = WorkspacePath.Resolve(contextFull, relative);
                if (source is null)
                {
                    // A link somewhere on the path, or a name that climbs: never followed.
                    skipped.Add(Path.GetRelativePath(workspaceRoot, Path.Combine(contextFull, relative)).Replace('\\', '/'));
                    continue;
                }

                var info = new FileInfo(source);
                if (!info.Exists)
                {
                    continue; // Tracked but deleted in the working tree.
                }

                bytes += info.Length;
                if (bytes > MaxBytes)
                {
                    throw new InvalidOperationException($"the build context is larger than {MaxBytes / (1024 * 1024)} MiB");
                }

                var destination = Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
                copied++;
            }
        }
        catch
        {
            TryDelete(target);
            throw;
        }

        return new AssembledContext(target, copied, bytes, skipped);
    }

    /// <summary>Removes an assembled context. Never throws.</summary>
    public static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stuck temp folder is the OS's to clean; the build already has its answer.
        }
    }

    /// <summary>The files under the context, relative to it, with git's exclusions when the folder is a repository.</summary>
    internal static IReadOnlyList<string> ListFiles(string workspaceRoot, string contextFull, string gitExecutable)
    {
        if (TryGitList(workspaceRoot, contextFull, gitExecutable) is { } listed)
        {
            return listed;
        }

        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(contextFull);
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
            {
                var name = Path.GetFileName(entry);
                if (name == ".git")
                {
                    continue;
                }

                if (Directory.Exists(entry) && !WorkspacePath.IsLink(entry))
                {
                    pending.Push(entry);
                }
                else
                {
                    files.Add(Path.GetRelativePath(contextFull, entry).Replace('\\', '/'));
                }
            }
        }

        return files;
    }

    private static List<string>? TryGitList(string workspaceRoot, string contextFull, string gitExecutable)
    {
        try
        {
            var start = new ProcessStartInfo(gitExecutable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = contextFull,
            };
            foreach (var arg in new[] { "ls-files", "-z", "--cached", "--others", "--exclude-standard", "--", "." })
            {
                start.ArgumentList.Add(arg);
            }

            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(60_000) || process.ExitCode != 0)
            {
                return null;
            }

            // git lists paths relative to the working directory, which is the context itself.
            return [.. output.Split('\0', StringSplitOptions.RemoveEmptyEntries)];
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
