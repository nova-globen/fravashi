using Fravashi.Application.Repository;

namespace Fravashi.Infrastructure.Bridge;

/// <summary>
/// <see cref="IRepositoryWriter"/> over the host bridge (plan 2b-i): editor writes land in the
/// ACTIVE host workspace folder. Mirrors LocalRepositoryWriter's semantics: name validation,
/// collisions fail instead of overwriting, same-folder copies get a " copy" suffix, saves carry
/// the freshness precondition (ADR 0016). Every mutation still serializes through the in-process
/// write gate. Known gap vs local: a UTF-8 BOM on the host file is not re-added on save (the
/// bridge write streams the editor's content as-is).
/// </summary>
public sealed class BridgeRepositoryWriter(BridgeClient client, IRepositoryWriteGate gate) : IRepositoryWriter
{
    public Task<RepositoryWriteResult> CreateFileAsync(string directoryPath, string name, CancellationToken cancellationToken = default) =>
        Gated(cancellationToken, async () => await InDirectoryAsync(directoryPath, name, async target =>
        {
            if (await client.StatAsync(target, cancellationToken).ConfigureAwait(false) is not null)
            {
                return RepositoryWriteResult.Fail($"'{name}' already exists here.");
            }

            using var empty = new MemoryStream();
            await client.WriteAsync(target, empty, expectedModifiedUtc: null, cancellationToken).ConfigureAwait(false);
            return RepositoryWriteResult.Ok;
        }, cancellationToken));

    public Task<RepositoryWriteResult> CreateDirectoryAsync(string directoryPath, string name, CancellationToken cancellationToken = default) =>
        Gated(cancellationToken, async () => await InDirectoryAsync(directoryPath, name, async target =>
        {
            if (await client.StatAsync(target, cancellationToken).ConfigureAwait(false) is not null)
            {
                return RepositoryWriteResult.Fail($"'{name}' already exists here.");
            }

            await client.CreateDirectoryAsync(target, cancellationToken).ConfigureAwait(false);
            return RepositoryWriteResult.Ok;
        }, cancellationToken));

    public Task<RepositoryWriteResult> CopyAsync(string sourcePath, string targetDirectoryPath, CancellationToken cancellationToken = default) =>
        Gated(cancellationToken, () => TransferAsync(sourcePath, targetDirectoryPath, move: false, cancellationToken));

    public Task<RepositoryWriteResult> MoveAsync(string sourcePath, string targetDirectoryPath, CancellationToken cancellationToken = default) =>
        Gated(cancellationToken, () => TransferAsync(sourcePath, targetDirectoryPath, move: true, cancellationToken));

    public Task<RepositoryWriteResult> RenameAsync(string path, string newName, CancellationToken cancellationToken = default) =>
        Gated(cancellationToken, async () =>
        {
            if (!IsValidName(newName))
            {
                return RepositoryWriteResult.Fail("That name is not valid.");
            }

            var source = Normalize(path);
            var stat = await client.StatAsync(source, cancellationToken).ConfigureAwait(false);
            if (stat is null)
            {
                return RepositoryWriteResult.Fail("The item no longer exists.");
            }

            var parent = ParentOf(source);
            var target = parent.Length == 0 ? newName : $"{parent}/{newName}";
            if (string.Equals(target, source, StringComparison.Ordinal))
            {
                return RepositoryWriteResult.Ok;
            }

            // Same name, different case: allowed (a real rename on Windows despite the case-blind FS).
            var caseOnly = string.Equals(target, source, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && await client.StatAsync(target, cancellationToken).ConfigureAwait(false) is not null)
            {
                return RepositoryWriteResult.Fail($"'{newName}' already exists here.");
            }

            try
            {
                await client.RenameAsync(source, target, cancellationToken).ConfigureAwait(false);
                return RepositoryWriteResult.Ok;
            }
            catch (HttpRequestException ex)
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
        using var _ = await gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stamp = await client.WriteAsync(
                Normalize(relativePath), content, expectedLastWriteUtc, cancellationToken).ConfigureAwait(false);
            return new RepositorySaveResult(true, LastWriteUtc: stamp);
        }
        catch (BridgeConflictException conflict)
        {
            return conflict.Missing
                ? RepositorySaveResult.Fail("The file no longer exists on disk.")
                : RepositorySaveResult.Conflicted;
        }
        catch (HttpRequestException ex)
        {
            return RepositorySaveResult.Fail(ex.Message);
        }
    }

    private async Task<RepositoryWriteResult> TransferAsync(
        string sourcePath, string targetDirectoryPath, bool move, CancellationToken ct)
    {
        var source = Normalize(sourcePath);
        var targetDirectory = Normalize(targetDirectoryPath);
        var sourceStat = await client.StatAsync(source, ct).ConfigureAwait(false);
        if (sourceStat is null)
        {
            return RepositoryWriteResult.Fail("The source no longer exists.");
        }

        if (targetDirectory.Length > 0)
        {
            var directoryStat = await client.StatAsync(targetDirectory, ct).ConfigureAwait(false);
            if (directoryStat is null || !directoryStat.IsDirectory)
            {
                return RepositoryWriteResult.Fail("The target folder no longer exists.");
            }
        }

        var name = source[(source.LastIndexOf('/') + 1)..];
        var target = targetDirectory.Length == 0 ? name : $"{targetDirectory}/{name}";
        if (string.Equals(target, source, StringComparison.OrdinalIgnoreCase))
        {
            if (move)
            {
                return RepositoryWriteResult.Ok;
            }

            target = await NextCopyNameAsync(target, ct).ConfigureAwait(false);
        }
        else if (await client.StatAsync(target, ct).ConfigureAwait(false) is not null)
        {
            return RepositoryWriteResult.Fail($"'{name}' already exists in the target folder.");
        }

        if (sourceStat.IsDirectory && (target + "/").StartsWith(source + "/", StringComparison.OrdinalIgnoreCase))
        {
            return RepositoryWriteResult.Fail(move
                ? "A folder cannot be moved into itself."
                : "A folder cannot be copied into itself.");
        }

        try
        {
            if (move)
            {
                await client.RenameAsync(source, target, ct).ConfigureAwait(false);
            }
            else if (sourceStat.IsDirectory)
            {
                await CopyTreeAsync(source, target, ct).ConfigureAwait(false);
            }
            else
            {
                await CopyFileAsync(source, target, ct).ConfigureAwait(false);
            }

            return RepositoryWriteResult.Ok;
        }
        catch (HttpRequestException ex)
        {
            return RepositoryWriteResult.Fail(ex.Message);
        }
    }

    private async Task CopyTreeAsync(string source, string target, CancellationToken ct)
    {
        await client.CreateDirectoryAsync(target, ct).ConfigureAwait(false);
        foreach (var entry in await client.ListAsync(source, ct).ConfigureAwait(false))
        {
            var childTarget = $"{target}/{entry.Name}";
            if (entry.IsDirectory)
            {
                await CopyTreeAsync(entry.Path, childTarget, ct).ConfigureAwait(false);
            }
            else
            {
                await CopyFileAsync(entry.Path, childTarget, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task CopyFileAsync(string source, string target, CancellationToken ct)
    {
        var content = await client.ReadAsync(source, ct).ConfigureAwait(false)
            ?? throw new HttpRequestException($"'{source}' vanished during the copy.");
        using var stream = new MemoryStream(content.Bytes);
        await client.WriteAsync(target, stream, expectedModifiedUtc: null, ct).ConfigureAwait(false);
    }

    private async Task<string> NextCopyNameAsync(string target, CancellationToken ct)
    {
        var slash = target.LastIndexOf('/');
        var directory = slash < 0 ? string.Empty : target[..slash];
        var fileName = slash < 0 ? target : target[(slash + 1)..];
        var dot = fileName.LastIndexOf('.');
        var stem = dot <= 0 ? fileName : fileName[..dot];
        var extension = dot <= 0 ? string.Empty : fileName[dot..];
        for (var i = 1; ; i++)
        {
            var suffix = i == 1 ? " copy" : $" copy {i}";
            var candidateName = stem + suffix + extension;
            var candidate = directory.Length == 0 ? candidateName : $"{directory}/{candidateName}";
            if (await client.StatAsync(candidate, ct).ConfigureAwait(false) is null)
            {
                return candidate;
            }
        }
    }

    private async Task<RepositoryWriteResult> InDirectoryAsync(
        string directoryPath, string name, Func<string, Task<RepositoryWriteResult>> action, CancellationToken ct)
    {
        if (!IsValidName(name))
        {
            return RepositoryWriteResult.Fail("That name is not valid.");
        }

        var directory = Normalize(directoryPath);
        if (directory.Length > 0)
        {
            var stat = await client.StatAsync(directory, ct).ConfigureAwait(false);
            if (stat is null || !stat.IsDirectory)
            {
                return RepositoryWriteResult.Fail("The folder no longer exists.");
            }
        }

        try
        {
            return await action(directory.Length == 0 ? name : $"{directory}/{name}").ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return RepositoryWriteResult.Fail(ex.Message);
        }
    }

    private async Task<RepositoryWriteResult> Gated(
        CancellationToken cancellationToken, Func<Task<RepositoryWriteResult>> action)
    {
        using var _ = await gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        return await action().ConfigureAwait(false);
    }

    // Must match LocalRepositoryWriter's validation exactly (the UX strings depend on it).
    private static bool IsValidName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && name is not ("." or "..")
        && !name.Contains('/', StringComparison.Ordinal)
        && !name.Contains('\\', StringComparison.Ordinal);

    private static string ParentOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    private static string Normalize(string? path) => (path ?? string.Empty).Replace('\\', '/').Trim('/');
}
