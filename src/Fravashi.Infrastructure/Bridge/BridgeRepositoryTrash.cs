using System.IO.Compression;
using Fravashi.Application.Git;
using Fravashi.Application.Repository;
using Fravashi.Infrastructure.Persistence;

namespace Fravashi.Infrastructure.Bridge;

/// <summary>
/// <see cref="IRepositoryTrash"/> over the host bridge (plan 2b-i): deletions from a host
/// workspace are zipped into the SAME studio-side trash as local deletions
/// (<c>&lt;data dir&gt;/trash/yyyy-MM-dd_HHmm_[branch].zip</c>) BEFORE anything is removed on the
/// host, and restore writes every entry back through the bridge, all-or-nothing. The zip living in
/// the studio's data volume (not the host folder) is deliberate: the trash survives even when the
/// host folder itself is the thing being torn down.
/// </summary>
public sealed class BridgeRepositoryTrash(
    BridgeClient client, IRepositoryWriteGate gate, IGitService git, DataDirectory dataDirectory)
    : IRepositoryTrash
{
    public async Task<TrashResult> DeleteToTrashAsync(
        IReadOnlyList<string> relativePaths, CancellationToken cancellationToken = default)
    {
        var normalized = relativePaths.Select(Normalize).ToList();
        if (normalized.Count == 0 || normalized.Any(p => p.Length == 0))
        {
            return TrashResult.Fail("The workspace root itself cannot be deleted.");
        }

        using var _ = await gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

        // Collect every file first: the zip must be complete before the first delete.
        var files = new List<(string Path, byte[] Bytes)>();
        foreach (var path in normalized)
        {
            var stat = await client.StatAsync(path, cancellationToken).ConfigureAwait(false);
            if (stat is null)
            {
                return TrashResult.Fail($"'{path}' no longer exists.");
            }

            if (stat.IsDirectory)
            {
                await CollectTreeAsync(path, files, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var content = await client.ReadAsync(path, cancellationToken).ConfigureAwait(false);
                if (content is null)
                {
                    return TrashResult.Fail($"'{path}' no longer exists.");
                }

                files.Add((path, content.Bytes));
            }
        }

        var zipPath = await NextZipPathAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var (path, bytes) in files)
                {
                    var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
                    await using var stream = entry.Open();
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TrashResult.Fail($"Could not write the trash zip: {ex.Message}");
        }

        foreach (var path in normalized)
        {
            await client.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
        }

        return new TrashResult(true, ZipFileName: Path.GetFileName(zipPath));
    }

    public async Task<TrashResult> RestoreAsync(string zipFileName, CancellationToken cancellationToken = default)
    {
        var zipPath = Path.Combine(dataDirectory.Path, "trash", Path.GetFileName(zipFileName));
        if (!File.Exists(zipPath))
        {
            return TrashResult.Fail("The trash zip no longer exists.");
        }

        using var _ = await gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        using var archive = ZipFile.OpenRead(zipPath);

        // All-or-nothing: refuse when any entry escapes or already exists on the host again.
        foreach (var entry in archive.Entries)
        {
            var target = Normalize(entry.FullName);
            if (target.Length == 0 || target.StartsWith("..", StringComparison.Ordinal)
                || Path.IsPathRooted(entry.FullName))
            {
                return TrashResult.Fail("The zip contains an entry outside the workspace; nothing was restored.");
            }

            if (await client.StatAsync(target, cancellationToken).ConfigureAwait(false) is not null)
            {
                return TrashResult.Fail($"'{target}' already exists; nothing was restored.");
            }
        }

        foreach (var entry in archive.Entries)
        {
            await using var stream = entry.Open();
            await client.WriteAsync(Normalize(entry.FullName), stream, expectedModifiedUtc: null, cancellationToken)
                .ConfigureAwait(false);
        }

        return new TrashResult(true, ZipFileName: Path.GetFileName(zipPath));
    }

    private async Task CollectTreeAsync(
        string directory, List<(string Path, byte[] Bytes)> files, CancellationToken ct)
    {
        foreach (var entry in await client.ListAsync(directory, ct).ConfigureAwait(false))
        {
            if (entry.IsDirectory)
            {
                await CollectTreeAsync(entry.Path, files, ct).ConfigureAwait(false);
            }
            else
            {
                var content = await client.ReadAsync(entry.Path, ct).ConfigureAwait(false);
                if (content is not null)
                {
                    files.Add((entry.Path, content.Bytes));
                }
            }
        }
    }

    private async Task<string> NextZipPathAsync(CancellationToken ct)
    {
        string branch;
        try
        {
            branch = (await git.GetCurrentBranchAsync(ct).ConfigureAwait(false)).Trim();
        }
        catch (Exception)
        {
            branch = "no-branch";
        }

        var safeBranch = string.Join("-", branch.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmm");
        var trashDirectory = Path.Combine(dataDirectory.Path, "trash");
        for (var i = 1; ; i++)
        {
            var suffix = i == 1 ? string.Empty : $"-{i}";
            var candidate = Path.Combine(trashDirectory, $"{stamp}_[{safeBranch}]{suffix}.zip");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static string Normalize(string? path) => (path ?? string.Empty).Replace('\\', '/').Trim('/');
}
