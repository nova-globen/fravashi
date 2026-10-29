using Fravashi.Application.Repository;
using Microsoft.Extensions.Options;

namespace Fravashi.Infrastructure.Repository;

/// <summary>
/// Filesystem implementation of <see cref="IRepositoryWriter"/>, bounded to <c>Repository:Root</c> with
/// the same no-fallback, no-escape rules as <see cref="LocalRepositoryReader"/>. Collisions fail with a
/// message instead of overwriting (saves overwrite deliberately, with a timestamp conflict check);
/// nothing here ever deletes. Every mutation serializes through the repository write gate (ADR 0016).
/// </summary>
public sealed class LocalRepositoryWriter : IRepositoryWriter
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private readonly string _root;
    private readonly IRepositoryWriteGate _gate;

    public LocalRepositoryWriter(IOptions<RepositoryReaderOptions> options, IRepositoryWriteGate gate)
    {
        var configured = options.Value.Root;
        _root = string.IsNullOrWhiteSpace(configured)
            ? string.Empty
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
        _gate = gate;
    }

    public Task<RepositoryWriteResult> CreateFileAsync(string directoryPath, string name, CancellationToken cancellationToken = default) =>
        Gated(cancellationToken, () => InDirectory(directoryPath, name, target =>
        {
            if (File.Exists(target) || Directory.Exists(target))
            {
                return RepositoryWriteResult.Fail($"'{name}' already exists here.");
            }

            File.WriteAllText(target, string.Empty);
            return RepositoryWriteResult.Ok;
        }));

    public Task<RepositoryWriteResult> CreateDirectoryAsync(string directoryPath, string name, CancellationToken cancellationToken = default) =>
        Gated(cancellationToken, () => InDirectory(directoryPath, name, target =>
        {
            if (File.Exists(target) || Directory.Exists(target))
            {
                return RepositoryWriteResult.Fail($"'{name}' already exists here.");
            }

            Directory.CreateDirectory(target);
            return RepositoryWriteResult.Ok;
        }));

    public Task<RepositoryWriteResult> CopyAsync(string sourcePath, string targetDirectoryPath, CancellationToken cancellationToken = default) =>
        Gated(cancellationToken, () => Transfer(sourcePath, targetDirectoryPath, move: false));

    public Task<RepositoryWriteResult> MoveAsync(string sourcePath, string targetDirectoryPath, CancellationToken cancellationToken = default) =>
        Gated(cancellationToken, () => Transfer(sourcePath, targetDirectoryPath, move: true));

    public Task<RepositoryWriteResult> RenameAsync(string path, string newName, CancellationToken cancellationToken = default) =>
        Gated(cancellationToken, () =>
        {
            if (string.IsNullOrWhiteSpace(newName) || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || newName is "." or ".." || newName.Contains('/', StringComparison.Ordinal) || newName.Contains('\\', StringComparison.Ordinal))
            {
                return RepositoryWriteResult.Fail("That name is not valid.");
            }

            var source = Resolve(path);
            if (source is null)
            {
                return NotWritable();
            }

            var isDirectory = Directory.Exists(source);
            if (!isDirectory && !File.Exists(source))
            {
                return RepositoryWriteResult.Fail("The item no longer exists.");
            }

            var target = Path.Combine(Path.GetDirectoryName(source)!, newName);
            if (string.Equals(target, source, StringComparison.Ordinal))
            {
                return RepositoryWriteResult.Ok;
            }

            // Same name, different case: allowed (a real rename on Windows despite the case-blind FS).
            var caseOnly = string.Equals(target, source, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && (File.Exists(target) || Directory.Exists(target)))
            {
                return RepositoryWriteResult.Fail($"'{newName}' already exists here.");
            }

            try
            {
                if (isDirectory)
                {
                    Directory.Move(source, target);
                }
                else
                {
                    File.Move(source, target);
                }

                return RepositoryWriteResult.Ok;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return RepositoryWriteResult.Fail(ex.Message);
            }
        });

    public async Task<RepositorySaveResult> SaveFileAsync(
        string relativePath,
        Stream content,
        DateTimeOffset? expectedLastWriteUtc,
        CancellationToken cancellationToken = default)
    {
        var target = Resolve(relativePath);
        if (target is null)
        {
            return RepositorySaveResult.Fail("No repository is configured, or the path is outside it.");
        }

        using var _ = await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var exists = File.Exists(target);
        if (!exists && expectedLastWriteUtc is not null)
        {
            // A checked save of a vanished file surfaces the deletion; only the deliberate force path
            // (null expected: the user chose keep-my-copy after an external delete) recreates it.
            return RepositorySaveResult.Fail("The file no longer exists on disk.");
        }

        if (exists && expectedLastWriteUtc is { } expected
            && new DateTimeOffset(File.GetLastWriteTimeUtc(target), TimeSpan.Zero) != expected)
        {
            return RepositorySaveResult.Conflicted;
        }

        try
        {
            // Write beside the target then swap in, so a crash mid-write can never leave a torn file.
            var temp = target + ".fravashi-save-tmp";
            var keepBom = exists && HasUtf8Bom(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                if (keepBom)
                {
                    await output.WriteAsync(Utf8Bom, cancellationToken).ConfigureAwait(false);
                }

                await content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, target, overwrite: true);
            return new RepositorySaveResult(
                true, LastWriteUtc: new DateTimeOffset(File.GetLastWriteTimeUtc(target), TimeSpan.Zero));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RepositorySaveResult.Fail(ex.Message);
        }
    }

    private static bool HasUtf8Bom(string path)
    {
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> head = stackalloc byte[3];
            return probe.Read(head) == 3 && head.SequenceEqual(Utf8Bom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<RepositoryWriteResult> Gated(CancellationToken cancellationToken, Func<RepositoryWriteResult> action)
    {
        using var _ = await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        return action();
    }

    private RepositoryWriteResult Transfer(string sourcePath, string targetDirectoryPath, bool move)
    {
        var source = Resolve(sourcePath);
        var targetDirectory = Resolve(targetDirectoryPath);
        if (source is null || targetDirectory is null)
        {
            return NotWritable();
        }

        var isDirectory = Directory.Exists(source);
        if (!isDirectory && !File.Exists(source))
        {
            return RepositoryWriteResult.Fail("The source no longer exists.");
        }

        if (!Directory.Exists(targetDirectory))
        {
            return RepositoryWriteResult.Fail("The target folder no longer exists.");
        }

        var target = Path.Combine(targetDirectory, Path.GetFileName(source));
        if (string.Equals(target, source, StringComparison.OrdinalIgnoreCase))
        {
            // Same-folder copy gets a "copy" suffix, VS style; a same-folder move is a no-op.
            if (move)
            {
                return RepositoryWriteResult.Ok;
            }

            target = NextCopyName(source, targetDirectory);
        }

        if (File.Exists(target) || Directory.Exists(target))
        {
            return RepositoryWriteResult.Fail($"'{Path.GetFileName(target)}' already exists in the target folder.");
        }

        if (isDirectory && move && target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return RepositoryWriteResult.Fail("A folder cannot be moved into itself.");
        }

        if (isDirectory && !move && target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return RepositoryWriteResult.Fail("A folder cannot be copied into itself.");
        }

        try
        {
            if (isDirectory)
            {
                if (move)
                {
                    Directory.Move(source, target);
                }
                else
                {
                    CopyDirectory(source, target);
                }
            }
            else if (move)
            {
                File.Move(source, target);
            }
            else
            {
                File.Copy(source, target);
            }

            return RepositoryWriteResult.Ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RepositoryWriteResult.Fail(ex.Message);
        }
    }

    private RepositoryWriteResult InDirectory(string directoryPath, string name, Func<string, RepositoryWriteResult> action)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name is "." or ".." || name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal))
        {
            return RepositoryWriteResult.Fail("That name is not valid.");
        }

        var directory = Resolve(directoryPath);
        if (directory is null)
        {
            return NotWritable();
        }

        if (!Directory.Exists(directory))
        {
            return RepositoryWriteResult.Fail("The folder no longer exists.");
        }

        try
        {
            return action(Path.Combine(directory, name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RepositoryWriteResult.Fail(ex.Message);
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    private static string NextCopyName(string source, string directory)
    {
        var name = Path.GetFileNameWithoutExtension(source);
        var extension = Path.GetExtension(source);
        for (var i = 1; ; i++)
        {
            var suffix = i == 1 ? " copy" : $" copy {i}";
            var candidate = Path.Combine(directory, name + suffix + extension);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>Root-bounded resolution: null when unconfigured or the path escapes the root.</summary>
    private string? Resolve(string relativePath)
    {
        if (_root.Length == 0)
        {
            return null;
        }

        var normalized = (relativePath ?? string.Empty).Replace('\\', '/').Trim('/');
        var combined = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(_root, normalized)));
        if (string.Equals(combined, _root, StringComparison.OrdinalIgnoreCase))
        {
            return combined;
        }

        var rootWithSeparator = _root + Path.DirectorySeparatorChar;
        return combined.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) ? combined : null;
    }

    private static RepositoryWriteResult NotWritable() =>
        RepositoryWriteResult.Fail("No repository is configured, or the path is outside it.");
}
