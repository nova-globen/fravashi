namespace Fravashi.Domain.Repository;

/// <summary>
/// A node in a read-only repository file tree. Paths are repository-relative and forward-slash normalized;
/// a directory carries its children, a file has none.
/// </summary>
public sealed record RepositoryNode(
    string Name,
    string RelativePath,
    bool IsDirectory,
    IReadOnlyList<RepositoryNode> Children)
{
    public static RepositoryNode File(string name, string relativePath) =>
        new(name, relativePath, false, []);

    public static RepositoryNode Directory(string name, string relativePath, IReadOnlyList<RepositoryNode> children) =>
        new(name, relativePath, true, children);
}

/// <summary>
/// A file's content plus a language hint for syntax highlighting. <see cref="LastWriteUtc"/> is the
/// on-disk timestamp at read time (save-conflict detection); <see cref="IsTruncated"/> marks content
/// that is a placeholder rather than the real file (too large), which must never be written back.
/// </summary>
public sealed record RepositoryFile(
    string RelativePath,
    string Content,
    string Language,
    DateTimeOffset? LastWriteUtc = null,
    bool IsTruncated = false)
{
    public int ByteLength => System.Text.Encoding.UTF8.GetByteCount(Content);
}
