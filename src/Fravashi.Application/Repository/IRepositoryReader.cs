using Fravashi.Domain.Repository;

namespace Fravashi.Application.Repository;

/// <summary>
/// Read-only access to the configured repository's working tree, bounded to <c>Repository:Root</c> (set by
/// the clone service when onboarding's git step completes). Implementations must reject paths that escape
/// the root (no traversal outside <see cref="RootPath"/>). There is deliberately no fallback root: when no
/// repository has been configured/cloned, <see cref="IsConfigured"/> is false and reads return empty/null,
/// never the app's own directory.
/// </summary>
public interface IRepositoryReader
{
    /// <summary>False until onboarding's git step has recorded a cloned working copy.</summary>
    bool IsConfigured { get; }

    string RootPath { get; }

    Task<RepositoryNode> GetTreeAsync(string relativePath = "", CancellationToken cancellationToken = default);

    Task<RepositoryFile?> ReadFileAsync(string relativePath, CancellationToken cancellationToken = default);

    /// <summary>Raw bytes for a file (image previews); null when absent, unconfigured, or over the size cap.</summary>
    Task<byte[]?> ReadBytesAsync(string relativePath, CancellationToken cancellationToken = default);
}
