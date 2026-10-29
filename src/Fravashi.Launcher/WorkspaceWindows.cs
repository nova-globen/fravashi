using Fravashi.Domain;

namespace Fravashi.Launcher;

/// <summary>
/// Which workspace each resident studio window shows (plan 2b-ii, backlog 29), kept apart from WinForms so
/// the rules are testable: ONE window per workspace folder, and AT MOST ONE unbound window (the home
/// surface with the recents and the open-folder affordances).
/// <para>
/// Before backlog 29 the host keyed its window map once, at creation, by the id it opened the window
/// with. A window that later adopted another workspace (the home page's recents navigate with a new
/// <c>?ws=</c>) stayed filed under the OLD id, so reopening the new folder opened a second window for
/// it and reopening the old one focused a window that no longer showed it. Every binding change goes
/// through <see cref="Rebind"/> or <see cref="Unbind"/> here, which move the window's entry instead of
/// adding one.
/// </para>
/// <para>
/// Closing a workspace here only changes what a WINDOW shows. It does NOT revoke host access: the
/// folder stays in the bridge's grant list and its registry file, its contained copy keeps syncing, and
/// its agent session stays alive. A full close (registry unregister, a bridge DELETE, per-workspace sync
/// and session teardown) is out of scope for backlog 29 and none of that machinery exists yet.
/// </para>
/// Not thread-safe: every caller is the host's UI thread.
/// </summary>
/// <typeparam name="TWindow">The host's window type; compared by reference.</typeparam>
public sealed class WorkspaceWindowMap<TWindow>
    where TWindow : class
{
    private readonly Dictionary<string, TWindow> _bound = new(StringComparer.Ordinal);
    private TWindow? _unbound;

    /// <summary>The one window showing no workspace, if any.</summary>
    public TWindow? Unbound => _unbound;

    /// <summary>Every tracked window: the unbound one first, then the bound ones.</summary>
    public IReadOnlyList<TWindow> All
    {
        get
        {
            var all = new List<TWindow>(_bound.Count + 1);
            if (_unbound is not null)
            {
                all.Add(_unbound);
            }

            all.AddRange(_bound.Values);
            return all;
        }
    }

    /// <summary>The window showing <paramref name="workspaceId"/>, if one is open.</summary>
    public TWindow? OwnerOf(string workspaceId) =>
        string.IsNullOrEmpty(workspaceId) ? null : _bound.GetValueOrDefault(workspaceId);

    /// <summary>The workspace <paramref name="window"/> shows; null when unbound or untracked.</summary>
    public string? WorkspaceOf(TWindow window)
    {
        foreach (var (id, candidate) in _bound)
        {
            if (ReferenceEquals(candidate, window))
            {
                return id;
            }
        }

        return null;
    }

    public bool Contains(TWindow window) => ReferenceEquals(_unbound, window) || WorkspaceOf(window) is not null;

    /// <summary>
    /// Tracks a window that shows no workspace. When a DIFFERENT unbound window already exists nothing
    /// changes and that window is returned, so the caller focuses it instead of opening a second one.
    /// </summary>
    public TWindow? TryAddUnbound(TWindow window)
    {
        if (_unbound is not null && !ReferenceEquals(_unbound, window))
        {
            return _unbound;
        }

        Detach(window);
        _unbound = window;
        return null;
    }

    /// <summary>
    /// Moves <paramref name="window"/> to <paramref name="workspaceId"/> (a new window, an adoption or a
    /// switch). Refused when another window already shows that workspace: the caller focuses it.
    /// </summary>
    public WindowRebind<TWindow> Rebind(TWindow window, string workspaceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspaceId);
        var owner = OwnerOf(workspaceId);
        if (ReferenceEquals(owner, window))
        {
            return new WindowRebind<TWindow>(WindowRebindOutcome.Unchanged, null);
        }

        if (owner is not null)
        {
            return new WindowRebind<TWindow>(WindowRebindOutcome.ShownElsewhere, owner);
        }

        Detach(window);
        _bound[workspaceId] = window;
        return new WindowRebind<TWindow>(WindowRebindOutcome.Rebound, null);
    }

    /// <summary>
    /// Closes the workspace <paramref name="window"/> shows. It becomes THE unbound window, unless one
    /// already exists: then this window leaves the map and the caller closes it and focuses the other.
    /// </summary>
    public WindowUnbind<TWindow> Unbind(TWindow window)
    {
        if (ReferenceEquals(_unbound, window))
        {
            return new WindowUnbind<TWindow>(WindowUnbindOutcome.AlreadyUnbound, null);
        }

        if (_unbound is not null)
        {
            Detach(window);
            return new WindowUnbind<TWindow>(WindowUnbindOutcome.CloseInFavourOf, _unbound);
        }

        Detach(window);
        _unbound = window;
        return new WindowUnbind<TWindow>(WindowUnbindOutcome.BecameUnbound, null);
    }

    /// <summary>Forgets a window that closed, wherever it was filed.</summary>
    public void Remove(TWindow window) => Detach(window);

    private void Detach(TWindow window)
    {
        if (ReferenceEquals(_unbound, window))
        {
            _unbound = null;
        }

        if (WorkspaceOf(window) is { } id)
        {
            _bound.Remove(id);
        }
    }
}

public enum WindowRebindOutcome
{
    /// <summary>The window now shows the workspace; its previous entry (bound or unbound) is gone.</summary>
    Rebound,

    /// <summary>The window already showed it; nothing moved.</summary>
    Unchanged,

    /// <summary>Another window shows it; nothing moved and that window should be focused.</summary>
    ShownElsewhere,
}

public sealed record WindowRebind<TWindow>(WindowRebindOutcome Outcome, TWindow? Owner)
    where TWindow : class;

public enum WindowUnbindOutcome
{
    /// <summary>The window is now the unbound (home) window.</summary>
    BecameUnbound,

    /// <summary>The window was already unbound; nothing moved.</summary>
    AlreadyUnbound,

    /// <summary>Another unbound window exists: this one left the map and should close, focusing that one.</summary>
    CloseInFavourOf,
}

public sealed record WindowUnbind<TWindow>(WindowUnbindOutcome Outcome, TWindow? Survivor)
    where TWindow : class;

/// <summary>What a studio-origin top-level navigation does to the window's workspace binding.</summary>
public enum WorkspaceNavigationAction
{
    /// <summary>Let it through: the window is unbound and the navigation names no workspace.</summary>
    ProceedUnbound,

    /// <summary>Let it through: it carries the workspace the window already shows.</summary>
    ProceedBound,

    /// <summary>Let it through and move the window to the carried workspace (re-key the map, re-title).</summary>
    Adopt,

    /// <summary>Cancel it and navigate again with the window's own <c>?ws=</c> appended.</summary>
    ReImpose,

    /// <summary>Cancel it: another window already shows the carried workspace, so focus that window.</summary>
    FocusOwner,
}

public sealed record WorkspaceNavigationDecision(WorkspaceNavigationAction Action, string? WorkspaceId);

/// <summary>The navigation rule for a studio window's <c>?ws=</c> query (plan 2b-ii, backlog 29).</summary>
public static class WorkspaceNavigation
{
    /// <summary>
    /// Decides a studio-origin navigation. A bound window re-imposes its workspace on a navigation that
    /// carries none, which is what keeps a reload or a forceLoad on the circuit's workspace; the ONLY
    /// way back to unbound is the host clearing the binding first (close workspace) and then navigating,
    /// after which the same empty-ws navigation proceeds unbound. A navigation carrying a workspace
    /// another window shows is refused, keeping one window per folder.
    /// </summary>
    /// <param name="boundWorkspaceId">What the window shows now; null or empty when unbound.</param>
    /// <param name="carriedWorkspaceId">The navigation's <c>ws</c> query value; null or empty when absent.</param>
    /// <param name="shownByAnotherWindow">Whether a DIFFERENT window already shows a workspace id.</param>
    public static WorkspaceNavigationDecision Decide(
        string? boundWorkspaceId, string? carriedWorkspaceId, Func<string, bool> shownByAnotherWindow)
    {
        ArgumentNullException.ThrowIfNull(shownByAnotherWindow);
        var bound = string.IsNullOrWhiteSpace(boundWorkspaceId) ? null : boundWorkspaceId;
        var carried = string.IsNullOrWhiteSpace(carriedWorkspaceId) ? null : carriedWorkspaceId;

        if (carried is not null)
        {
            if (string.Equals(carried, bound, StringComparison.Ordinal))
            {
                return new(WorkspaceNavigationAction.ProceedBound, carried);
            }

            return shownByAnotherWindow(carried)
                ? new(WorkspaceNavigationAction.FocusOwner, carried)
                : new(WorkspaceNavigationAction.Adopt, carried);
        }

        return bound is null
            ? new(WorkspaceNavigationAction.ProceedUnbound, null)
            : new(WorkspaceNavigationAction.ReImpose, bound);
    }

    /// <summary>
    /// True for a studio-origin navigation the window must let through exactly as it is, whatever its workspace: an
    /// identity provider's redirect back to a sign-in callback (<c>/auth/callback/...</c>). Its <c>state</c> is single-use,
    /// so it must reach the studio exactly once. Seen on 2026-10-25 (#372): a workspace window re-imposed its <c>?ws=</c>
    /// on that redirect, the cancelled original still reached the studio and consumed the state, and the re-imposed copy
    /// was refused as "possible CSRF". The callback answers with a redirect to a studio page, which is re-imposed as any
    /// other navigation, so the window keeps its workspace.
    /// </summary>
    public static bool PassesUntouched(Uri target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.AbsolutePath.StartsWith("/auth/callback/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The URL a re-imposition navigates to: the original with <c>ws</c> added to its query.</summary>
    public static string WithWorkspace(Uri target, string workspaceId)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrEmpty(workspaceId);
        var builder = new UriBuilder(target);
        var query = builder.Query.TrimStart('?');
        var pair = $"ws={Uri.EscapeDataString(workspaceId)}";
        builder.Query = query.Length == 0 ? pair : $"{query}&{pair}";
        return builder.Uri.AbsoluteUri;
    }

    /// <summary>The studio page a window opens when it switches to (or is created for) a workspace.</summary>
    public static string WorkspaceUrl(string studioBase, string workspaceId) =>
        $"{studioBase.TrimEnd('/')}/code?ws={Uri.EscapeDataString(workspaceId)}";

    /// <summary>Where a window lands once its workspace is closed: the home surface, with no ws.</summary>
    public static string HomeUrl(string studioBase) => $"{studioBase.TrimEnd('/')}/";
}

/// <summary>
/// The native caption (taskbar, Alt+Tab) of a studio window, chrome-worded (backlog 32). It once named the command
/// the owner typed; since the rename the command and the product share one word, Fravashi.
/// </summary>
public static class WindowCaption
{
    public const string ChromeName = ProductName.Short;

    /// <summary>"&lt;workspace&gt; - Fravashi" while a workspace is shown, the bare "Fravashi" otherwise.</summary>
    public static string For(string? workspaceName) =>
        string.IsNullOrWhiteSpace(workspaceName) ? ChromeName : $"{workspaceName.Trim()} - {ChromeName}";
}
