namespace Fravashi.Application.Repository;

/// <summary>
/// Deletion with a safety net (ruling 2026-07-26): deleted files/folders are first zipped into
/// <c>&lt;data dir&gt;/trash/yyyy-MM-dd_HHmm_[branch].zip</c>, then removed from the working tree.
/// Root-bounded like every other repository mutation, and serialized through the write gate.
/// </summary>
public sealed record TrashResult(bool Succeeded, string? Error = null, string? ZipFileName = null)
{
    public static TrashResult Fail(string error) => new(false, error);
}

public interface IRepositoryTrash
{
    /// <summary>Archive then delete; reports the zip's file name on success.</summary>
    Task<TrashResult> DeleteToTrashAsync(IReadOnlyList<string> relativePaths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restore a trash zip's contents back into the working tree (the explorer's undo-delete). The
    /// restore is all-or-nothing: when any target file already exists again, nothing is written and
    /// the result says which one blocked it. Root-bounded like the delete.
    /// </summary>
    Task<TrashResult> RestoreAsync(string zipFileName, CancellationToken cancellationToken = default);
}
