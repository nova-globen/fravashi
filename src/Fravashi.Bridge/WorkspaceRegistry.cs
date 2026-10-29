using System.Security.Cryptography;
using System.Text;

namespace Fravashi.Bridge;

/// <summary>A registered host workspace folder the bridge is allowed to serve.</summary>
public sealed record BridgeWorkspace(string Id, string Name, string RootPath);

/// <summary>
/// The bridge's allowlist: only folders registered here are reachable through the API, and every
/// request path is canonicalized and prefix-checked against its workspace root, so a token holder
/// can never step outside a folder the user explicitly opened. Thread-safe; ids are stable across
/// restarts (a hash of the canonical path) so persisted state can key on them later (plan 2b).
/// <para>
/// With a <see cref="WorkspaceRegistryStore"/> the grant list itself also survives a restart
/// (backlog 14): every <see cref="Register"/> writes the folder paths through, and
/// <see cref="Rehydrate"/> re-registers them on start. A folder is dropped from the store ONLY
/// when the host can positively see it is not there any more; one that is merely out of reach this
/// start (an unmapped drive, an unplugged disk, an offline share) stays remembered. Without a store
/// the registry is purely in-memory, exactly as before.
/// </para>
/// </summary>
/// <param name="probe">
/// Seam for how a folder's state is established; defaults to <see cref="WorkspacePathProbe.Probe"/>.
/// Tests inject it to reproduce an unreachable volume without one.
/// </param>
public sealed class WorkspaceRegistry(
    WorkspaceRegistryStore? store = null, Func<string, WorkspacePathState>? probe = null)
{
    private readonly Func<string, WorkspacePathState> _probe = probe ?? WorkspacePathProbe.Probe;
    private readonly object _sync = new();
    private readonly object _persistSync = new();
    private readonly Dictionary<string, BridgeWorkspace> _workspaces = new(StringComparer.Ordinal);

    // Remembered, but not granted this start because the folder could not be reached. Kept so the
    // write-back below does not silently forget a perfectly good folder on an offline volume.
    private readonly List<string> _deferred = [];

    public BridgeWorkspace Register(string folderPath)
    {
        var workspace = RegisterCore(folderPath);
        Persist();
        return workspace;
    }

    /// <summary>
    /// Re-registers every remembered folder (fravashi.exe start), returning how many came back.
    /// <para>
    /// A folder the host can positively see is gone (its parent is there, readable, and does not
    /// contain it) is dropped, and the pruned list is written back so the next start does not retry
    /// it. A folder that merely cannot be reached right now is KEPT: an unmapped network drive or
    /// an unplugged disk is routine and says nothing about whether the work is still there.
    /// </para>
    /// Never throws: a broken store means an empty registry, not a failed launch.
    /// </summary>
    public int Rehydrate()
    {
        if (store is null)
        {
            return 0;
        }

        var restored = 0;
        foreach (var path in store.Load())
        {
            try
            {
                RegisterCore(path);
                restored++;
            }
            catch (WorkspaceUnreachableException ex)
            {
                // Transient by nature: keep the grant so a later start (or the studio
                // re-registering when the user clicks the recent) finds it again.
                Defer(ex.FolderPath);
            }
            catch (DirectoryNotFoundException)
            {
                // Positively not there any more: prune it (by omission from the write-back).
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Could not even look: same reasoning as unreachable, so keep it.
                Defer(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                // Not a usable path at all, which is never a transient condition: prune.
            }
        }

        Persist();
        return restored;
    }

    private BridgeWorkspace RegisterCore(string folderPath)
    {
        var canonical = Canonicalize(folderPath);
        var state = _probe(canonical);
        if (state == WorkspacePathState.Missing)
        {
            throw new DirectoryNotFoundException($"Workspace folder does not exist: {canonical}");
        }

        if (state == WorkspacePathState.Unreachable)
        {
            throw new WorkspaceUnreachableException(
                canonical, $"Workspace folder cannot be reached right now: {canonical}");
        }

        var id = IdForCanonical(canonical);
        var workspace = new BridgeWorkspace(id, Path.GetFileName(canonical), canonical);
        lock (_sync)
        {
            _workspaces[id] = workspace;
            _deferred.RemoveAll(p => string.Equals(p, canonical, StringComparison.OrdinalIgnoreCase));
        }

        return workspace;
    }

    private void Defer(string folderPath)
    {
        lock (_sync)
        {
            if (!_deferred.Contains(folderPath, StringComparer.OrdinalIgnoreCase))
            {
                _deferred.Add(folderPath);
            }
        }
    }

    // Lock order is _persistSync then _sync (via RememberedPaths()); nothing may ever take them the
    // other way round, which is why Persist() stays outside RegisterCore's lock.
    private void Persist()
    {
        if (store is null)
        {
            return;
        }

        lock (_persistSync)
        {
            store.Save(RememberedPaths());
        }
    }

    /// <summary>
    /// Everything the store must still remember: the live grants PLUS the folders that could not be
    /// reached. Dropping the latter here would turn "the drive was not mapped when fravashi.exe
    /// started" into a permanently forgotten workspace on the very next write.
    /// </summary>
    private IReadOnlyList<string> RememberedPaths()
    {
        lock (_sync)
        {
            var paths = _workspaces.Values
                .OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
                .Select(w => w.RootPath)
                .ToList();
            paths.AddRange(_deferred.Where(d => !paths.Contains(d, StringComparer.OrdinalIgnoreCase)));
            return paths;
        }
    }

    public IReadOnlyList<BridgeWorkspace> List()
    {
        lock (_sync)
        {
            return [.. _workspaces.Values.OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase)];
        }
    }

    public BridgeWorkspace? TryGet(string id)
    {
        lock (_sync)
        {
            return _workspaces.GetValueOrDefault(id);
        }
    }

    /// <summary>
    /// Drops one workspace from the grant list and persists the drop. Today's only callers are
    /// <c>BridgeWorktreeService.PruneAsync</c> and its <c>CreateAsync</c>'s own removed-by-hand path
    /// (backlog 306): a worktree the product removed from disk, or found gone when asked to remake it,
    /// must not keep answering as a registered folder. Unregistering an id that is not known is a no-op,
    /// matching <see cref="TryGet"/>'s own forgiving shape. Widening this into a general "forget a folder
    /// a person opened by hand" feature is a decision for whoever needs that next, not implied by this one.
    /// </summary>
    public void Unregister(string id)
    {
        lock (_sync)
        {
            _workspaces.Remove(id);
        }

        Persist();
    }

    /// <summary>
    /// The single chokepoint every file/git/search/watch operation resolves through: canonical
    /// full path inside the workspace, or a refusal. Never trust a relative path further in.
    /// </summary>
    public string ResolvePath(string id, string? relativePath)
    {
        var workspace = TryGet(id) ?? throw new KeyNotFoundException($"Unknown workspace '{id}'.");
        if (string.IsNullOrEmpty(relativePath) || relativePath == "." || relativePath == "/")
        {
            return workspace.RootPath;
        }

        if (Path.IsPathRooted(relativePath))
        {
            throw new UnauthorizedAccessException("Absolute paths are not accepted; paths are workspace-relative.");
        }

        var combined = Path.GetFullPath(Path.Combine(workspace.RootPath, relativePath));
        var rootWithSeparator = workspace.RootPath.EndsWith(Path.DirectorySeparatorChar)
            ? workspace.RootPath
            : workspace.RootPath + Path.DirectorySeparatorChar;
        if (!combined.Equals(workspace.RootPath, StringComparison.OrdinalIgnoreCase)
            && !combined.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"Path escapes the workspace: {relativePath}");
        }

        return combined;
    }

    /// <summary>
    /// The id a folder would get from <see cref="Register"/>, without requiring the folder to exist or be
    /// reachable. For a caller that already knows a path was registered before and only needs to
    /// unregister it (backlog 306's worktree prune, and its create's own removed-by-hand path, both for a
    /// worktree folder that is gone): <see cref="Register"/> would throw for a folder that is not there any
    /// more, and this never does.
    /// </summary>
    public static string IdFor(string path) => IdForCanonical(Canonicalize(path));

    private static string Canonicalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string IdForCanonical(string canonicalPath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPath.ToLowerInvariant()));
        return Convert.ToHexStringLower(hash)[..16];
    }
}
