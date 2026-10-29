using System.IO.Compression;
using Fravashi.Application.Git;
using Fravashi.Application.Repository;
using Fravashi.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace Fravashi.Infrastructure.Repository;

/// <summary>
/// <see cref="IRepositoryTrash"/>: zips the doomed paths into the data directory's <c>trash/</c>
/// folder (named <c>yyyy-MM-dd_HHmm_[branch].zip</c>, suffixed when that name is taken), then
/// deletes them. The archive happens BEFORE any delete, so a failed zip aborts the whole operation
/// and nothing is lost.
/// </summary>
public sealed class RepositoryTrash : IRepositoryTrash
{
    private readonly string _root;
    private readonly IRepositoryWriteGate _gate;
    private readonly IGitService _git;
    private readonly DataDirectory _dataDirectory;

    public RepositoryTrash(
        IOptions<RepositoryReaderOptions> options,
        IRepositoryWriteGate gate,
        IGitService git,
        DataDirectory dataDirectory)
    {
        var configured = options.Value.Root;
        _root = string.IsNullOrWhiteSpace(configured)
            ? string.Empty
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
        _gate = gate;
        _git = git;
        _dataDirectory = dataDirectory;
    }

    public async Task<TrashResult> DeleteToTrashAsync(
        IReadOnlyList<string> relativePaths, CancellationToken cancellationToken = default)
    {
        if (_root.Length == 0)
        {
            return TrashResult.Fail("No repository is configured.");
        }

        var resolved = new List<(string Full, string Relative)>();
        foreach (var relativePath in relativePaths)
        {
            var full = Resolve(relativePath);
            if (full is null)
            {
                return TrashResult.Fail($"'{relativePath}' is outside the repository.");
            }

            if (string.Equals(full, _root, StringComparison.OrdinalIgnoreCase))
            {
                return TrashResult.Fail("The repository root itself cannot be deleted.");
            }

            if (File.Exists(full) || Directory.Exists(full))
            {
                resolved.Add((full, relativePath.Replace('\\', '/').Trim('/')));
            }
        }

        if (resolved.Count == 0)
        {
            return TrashResult.Fail("Nothing to delete; the items no longer exist.");
        }

        string branch;
        try
        {
            branch = await _git.GetCurrentBranchAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            branch = "no-branch";
        }

        using var _ = await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var zipPath = NextZipPath(SafeName(branch));
            Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var (full, relative) in resolved)
                {
                    if (File.Exists(full))
                    {
                        zip.CreateEntryFromFile(full, relative);
                    }
                    else
                    {
                        foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                        {
                            var entryName = relative + "/" + Path.GetRelativePath(full, file).Replace('\\', '/');
                            zip.CreateEntryFromFile(file, entryName);
                        }
                    }
                }
            }

            // The archive is safely on disk; only now do the deletes.
            foreach (var (full, _) in resolved)
            {
                if (File.Exists(full))
                {
                    File.SetAttributes(full, FileAttributes.Normal);
                    File.Delete(full);
                }
                else if (Directory.Exists(full))
                {
                    foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }

                    Directory.Delete(full, recursive: true);
                }
            }

            return new TrashResult(true, ZipFileName: Path.GetFileName(zipPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return TrashResult.Fail($"Delete aborted, nothing was removed: {ex.Message}");
        }
    }

    public async Task<TrashResult> RestoreAsync(string zipFileName, CancellationToken cancellationToken = default)
    {
        if (_root.Length == 0)
        {
            return TrashResult.Fail("No repository is configured.");
        }

        // The zip name comes from our own TrashResult, but it still gets the hostile-input treatment.
        var name = Path.GetFileName(zipFileName ?? string.Empty);
        var zipPath = Path.Combine(_dataDirectory.Path, "trash", name);
        if (name.Length == 0 || !File.Exists(zipPath))
        {
            return TrashResult.Fail($"The trash archive '{zipFileName}' no longer exists.");
        }

        using var _ = await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);

            // Pass 1: resolve and validate every entry before writing anything (all-or-nothing).
            var targets = new List<(ZipArchiveEntry Entry, string Full)>();
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/'))
                {
                    continue; // directory marker; directories materialize with their files
                }

                var full = Resolve(entry.FullName);
                if (full is null)
                {
                    return TrashResult.Fail($"'{entry.FullName}' in the archive points outside the repository; restore aborted.");
                }

                if (File.Exists(full))
                {
                    return TrashResult.Fail($"'{entry.FullName}' already exists again; nothing was restored.");
                }

                targets.Add((entry, full));
            }

            if (targets.Count == 0)
            {
                return TrashResult.Fail("The trash archive is empty; nothing to restore.");
            }

            foreach (var (entry, full) in targets)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                entry.ExtractToFile(full, overwrite: false);
            }

            return new TrashResult(true, ZipFileName: name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return TrashResult.Fail($"Restore failed: {ex.Message}");
        }
    }

    private string NextZipPath(string branch)
    {
        var trashDir = Path.Combine(_dataDirectory.Path, "trash");
        var baseName = $"{DateTimeOffset.Now:yyyy-MM-dd_HHmm}_[{branch}]";
        var candidate = Path.Combine(trashDir, baseName + ".zip");
        for (var i = 2; File.Exists(candidate); i++)
        {
            candidate = Path.Combine(trashDir, $"{baseName}-{i}.zip");
        }

        return candidate;
    }

    private static string SafeName(string branch)
    {
        var safe = new string(branch.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray());
        return safe.Length == 0 ? "no-branch" : safe;
    }

    private string? Resolve(string relativePath)
    {
        var normalized = (relativePath ?? string.Empty).Replace('\\', '/').Trim('/');
        var combined = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(_root, normalized)));
        if (string.Equals(combined, _root, StringComparison.OrdinalIgnoreCase))
        {
            return combined;
        }

        var rootWithSeparator = _root + Path.DirectorySeparatorChar;
        return combined.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) ? combined : null;
    }
}
