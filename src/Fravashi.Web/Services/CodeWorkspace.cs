namespace Fravashi.Web.Services;

/// <summary>
/// Per-circuit state of the Code page: the open editors (tabs), which one is active, and which are
/// pinned (pinned tabs render on their own row above the regular tabs, VS style). Content and editor
/// state live client-side in Monaco models; this only tracks identity/order so the Open Editors panel,
/// the tab strip, and the breadcrumbs render from one source of truth. Phase 2 adds dirty flags.
/// </summary>
public sealed class CodeWorkspace
{
    /// <summary>
    /// One open editor. <paramref name="IsPreview"/> is VS Code's preview tab (work item #366): opened by following
    /// the agent, drawn in italics, and replaced by the next preview instead of piling up. Editing it, pinning it or
    /// double-clicking its tab makes it an ordinary tab (<see cref="Promote"/>).
    /// </summary>
    public sealed record OpenFile(string Path, string Name, bool IsPinned = false, bool IsPreview = false);

    private readonly List<OpenFile> _open = [];
    private readonly List<string> _recent = [];

    public IReadOnlyList<OpenFile> Open => _open;

    /// <summary>Open tabs in most-recently-used order (the Ctrl+Tab switcher's order, VS style).</summary>
    public IReadOnlyList<OpenFile> RecentFirst
    {
        get
        {
            var byPath = _open.ToDictionary(f => f.Path);
            var ordered = _recent.Where(byPath.ContainsKey).Select(p => byPath[p]).ToList();
            ordered.AddRange(_open.Where(f => !_recent.Contains(f.Path)));
            return ordered;
        }
    }

    public IReadOnlyList<OpenFile> Pinned => _open.Where(f => f.IsPinned).ToList();

    public IReadOnlyList<OpenFile> Unpinned => _open.Where(f => !f.IsPinned).ToList();

    public string? ActivePath { get; private set; }

    public OpenFile? Active => _open.FirstOrDefault(f => f.Path == ActivePath);

    public event Action? Changed;

    /// <summary>
    /// Merge a previously persisted session (open tabs + active + pins) into this workspace: saved tabs
    /// keep their order, tabs already open this circuit are appended if the saved list lacks them, and a
    /// live active tab wins over the saved one. No event is raised; the caller re-renders and re-persists.
    /// </summary>
    public void Hydrate(IReadOnlyList<OpenFile> open, string? activePath)
    {
        if (open.Count == 0)
        {
            return;
        }

        var live = _open.ToList();
        _open.Clear();
        _open.AddRange(open.Where(f => !string.IsNullOrWhiteSpace(f.Path)).DistinctBy(f => f.Path));
        _open.AddRange(live.Where(f => _open.All(existing => existing.Path != f.Path)));

        if (ActivePath is null || _open.All(f => f.Path != ActivePath))
        {
            ActivePath = _open.Any(f => f.Path == activePath) ? activePath : _open[0].Path;
        }

        if (ActivePath is { } restoredActive)
        {
            Touch(restoredActive);
        }
    }

    /// <summary>Open (or re-activate) a file; returns true when it was not already open.</summary>
    public bool OpenAt(string path, string name)
    {
        var added = false;
        if (_open.All(f => f.Path != path))
        {
            _open.Add(new OpenFile(path, name));
            added = true;
        }

        ActivePath = path;
        Touch(path);
        Changed?.Invoke();
        return added;
    }

    /// <summary>The open preview tab, if there is one (work item #366).</summary>
    public string? PreviewPath => _open.FirstOrDefault(f => f.IsPreview)?.Path;

    /// <summary>
    /// Open (or re-activate) a file as the preview tab (work item #366). A file already open keeps its tab as it
    /// is; otherwise the existing preview tab is REPLACED in place by this one. Returns the path of the tab it
    /// replaced, so the caller can drop that buffer, or null. The caller promotes a preview with unsaved changes
    /// before calling this, so a replaced tab never held unsaved work.
    /// </summary>
    public string? OpenAsPreview(string path, string name)
    {
        string? replaced = null;
        if (_open.All(f => f.Path != path))
        {
            var entry = new OpenFile(path, name, IsPreview: true);
            var index = _open.FindIndex(f => f.IsPreview);
            if (index >= 0)
            {
                replaced = _open[index].Path;
                _open[index] = entry;
                _recent.Remove(replaced);
            }
            else
            {
                _open.Add(entry);
            }
        }

        ActivePath = path;
        Touch(path);
        Changed?.Invoke();
        return replaced;
    }

    /// <summary>A preview tab becomes an ordinary one (edited, pinned, or double-clicked).</summary>
    public void Promote(string path)
    {
        var index = _open.FindIndex(f => f.Path == path);
        if (index < 0 || !_open[index].IsPreview)
        {
            return;
        }

        _open[index] = _open[index] with { IsPreview = false };
        Changed?.Invoke();
    }

    public void Activate(string path)
    {
        if (_open.Any(f => f.Path == path) && ActivePath != path)
        {
            ActivePath = path;
            Touch(path);
            Changed?.Invoke();
        }
    }

    /// <summary>Close a tab; when it was active, the nearest remaining neighbor becomes active.</summary>
    public void Close(string path)
    {
        var index = _open.FindIndex(f => f.Path == path);
        if (index < 0)
        {
            return;
        }

        _open.RemoveAt(index);
        _recent.Remove(path);
        if (ActivePath == path)
        {
            ActivePath = _open.Count == 0 ? null : _open[Math.Min(index, _open.Count - 1)].Path;
        }

        Changed?.Invoke();
    }

    public void SetPinned(string path, bool pinned)
    {
        var index = _open.FindIndex(f => f.Path == path);
        if (index < 0 || _open[index].IsPinned == pinned)
        {
            return;
        }

        _open[index] = _open[index] with { IsPinned = pinned, IsPreview = false };
        Changed?.Invoke();
    }

    /// <summary>The active tab after a bulk close: the kept tab itself when it survives, else a neighbor.</summary>
    public void CloseOthers(string keepPath) =>
        CloseWhere(f => f.Path != keepPath && !f.IsPinned, preferredActive: keepPath);

    /// <summary>Close unpinned tabs positioned after <paramref name="fromPath"/> in the unpinned row.</summary>
    public void CloseToTheRight(string fromPath)
    {
        var unpinned = Unpinned;
        var from = unpinned.ToList().FindIndex(f => f.Path == fromPath);
        if (from < 0)
        {
            return;
        }

        var doomed = unpinned.Skip(from + 1).Select(f => f.Path).ToHashSet();
        CloseWhere(f => doomed.Contains(f.Path), preferredActive: fromPath);
    }

    public void CloseAllButPinned() =>
        CloseWhere(f => !f.IsPinned, preferredActive: null);

    /// <summary>Close everything, pinned tabs included (the Open Editors "Close All" action).</summary>
    public void CloseAll() =>
        CloseWhere(_ => true, preferredActive: null);

    /// <summary>Re-key an open tab after a rename (the file itself or a folder above it moved).</summary>
    public void Remap(string oldPath, string newPath, string newName)
    {
        var index = _open.FindIndex(f => f.Path == oldPath);
        if (index < 0)
        {
            return;
        }

        _open[index] = _open[index] with { Path = newPath, Name = newName };
        var recentIndex = _recent.IndexOf(oldPath);
        if (recentIndex >= 0)
        {
            _recent[recentIndex] = newPath;
        }

        if (ActivePath == oldPath)
        {
            ActivePath = newPath;
        }

        Changed?.Invoke();
    }

    /// <summary>Cycle the active tab forward/backward through the full tab list (Ctrl+Tab).</summary>
    public string? Cycle(int direction)
    {
        if (_open.Count < 2 || ActivePath is null)
        {
            return _open.Count == 1 ? _open[0].Path : null;
        }

        var index = _open.FindIndex(f => f.Path == ActivePath);
        var next = ((index + direction) % _open.Count + _open.Count) % _open.Count;
        return _open[next].Path;
    }

    private void Touch(string path)
    {
        _recent.Remove(path);
        _recent.Insert(0, path);
    }

    private void CloseWhere(Func<OpenFile, bool> doomed, string? preferredActive)
    {
        var removedAny = _open.RemoveAll(f => doomed(f)) > 0;
        _recent.RemoveAll(p => _open.All(f => f.Path != p));
        if (!removedAny)
        {
            return;
        }

        if (ActivePath is null || _open.All(f => f.Path != ActivePath))
        {
            ActivePath = preferredActive is not null && _open.Any(f => f.Path == preferredActive)
                ? preferredActive
                : _open.Count > 0 ? _open[^1].Path : null;
        }

        Changed?.Invoke();
    }
}
