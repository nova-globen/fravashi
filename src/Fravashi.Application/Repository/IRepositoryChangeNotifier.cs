namespace Fravashi.Application.Repository;

public enum RepositoryChangeKind
{
    /// <summary>A file or directory was created or its content changed.</summary>
    ChangedOrCreated,

    /// <summary>A file or directory disappeared.</summary>
    Deleted,
}

/// <summary>
/// One observed working-tree change. <see cref="RelativePath"/> is repository-relative and
/// forward-slash normalized; an <b>empty</b> path means "too many changes to enumerate, assume
/// anything changed" (watcher buffer overflow), subscribers should do a full refresh.
/// </summary>
public sealed record RepositoryChange(RepositoryChangeKind Kind, string RelativePath);

/// <summary>
/// Watches the configured working copy (<c>Repository:Root</c>) for changes made outside the studio,
/// by the user's own tools or an AI agent writing the same tree (ADR 0016). Events are debounced per
/// path; handlers are invoked on background threads and must marshal to their own context.
/// </summary>
public interface IRepositoryChangeNotifier
{
    /// <summary>Subscribe to change events; dispose the return value to unsubscribe.</summary>
    IDisposable Subscribe(Action<RepositoryChange> handler);
}
