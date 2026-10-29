namespace Fravashi.Application.Repository;

/// <summary>Outcome of a working-tree mutation; <see cref="Error"/> is a user-facing sentence on failure.</summary>
public sealed record RepositoryWriteResult(bool Succeeded, string? Error = null)
{
    public static RepositoryWriteResult Ok { get; } = new(true);

    public static RepositoryWriteResult Fail(string error) => new(false, error);
}

/// <summary>
/// Outcome of a save. <see cref="Conflict"/> is true when the on-disk file changed after the caller's
/// last-known timestamp (nothing was written); <see cref="LastWriteUtc"/> is the file's timestamp
/// after a successful write, for the caller's next conflict check.
/// </summary>
public sealed record RepositorySaveResult(
    bool Succeeded,
    string? Error = null,
    DateTimeOffset? LastWriteUtc = null,
    bool Conflict = false)
{
    public static RepositorySaveResult Conflicted { get; } = new(false, "The file changed on disk after it was opened.", Conflict: true);

    public static RepositorySaveResult Fail(string error) => new(false, error);
}

/// <summary>
/// Mutations on the configured repository's working tree (the Code page's explorer operations: new
/// file/folder, copy, move, rename; plus the editor's save). Bounded to <c>Repository:Root</c> exactly
/// like <see cref="IRepositoryReader"/>: paths that escape the root fail, they never touch the
/// filesystem outside it. Deletion lives on <see cref="IRepositoryTrash"/> (archive-first, owner
/// ruling 2026-07-26). All mutations serialize through <see cref="IRepositoryWriteGate"/> (ADR 0016).
/// </summary>
public interface IRepositoryWriter
{
    /// <summary>Create an empty file named <paramref name="name"/> inside <paramref name="directoryPath"/>.</summary>
    Task<RepositoryWriteResult> CreateFileAsync(string directoryPath, string name, CancellationToken cancellationToken = default);

    Task<RepositoryWriteResult> CreateDirectoryAsync(string directoryPath, string name, CancellationToken cancellationToken = default);

    /// <summary>Copy a file or directory into <paramref name="targetDirectoryPath"/> (recursive for directories).</summary>
    Task<RepositoryWriteResult> CopyAsync(string sourcePath, string targetDirectoryPath, CancellationToken cancellationToken = default);

    /// <summary>Move a file or directory into <paramref name="targetDirectoryPath"/> (the paste half of cut).</summary>
    Task<RepositoryWriteResult> MoveAsync(string sourcePath, string targetDirectoryPath, CancellationToken cancellationToken = default);

    /// <summary>Rename a file or directory in place; collisions fail rather than overwrite.</summary>
    Task<RepositoryWriteResult> RenameAsync(string path, string newName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Overwrite an <b>existing</b> file with <paramref name="content"/> (UTF-8; a leading UTF-8 BOM on
    /// the current file is preserved). When <paramref name="expectedLastWriteUtc"/> is given and the
    /// on-disk timestamp moved past it, nothing is written and the result reports a conflict; pass null
    /// to overwrite unconditionally (the user chose "overwrite" on the conflict banner, ADR 0016).
    /// </summary>
    Task<RepositorySaveResult> SaveFileAsync(
        string relativePath,
        Stream content,
        DateTimeOffset? expectedLastWriteUtc,
        CancellationToken cancellationToken = default);
}
