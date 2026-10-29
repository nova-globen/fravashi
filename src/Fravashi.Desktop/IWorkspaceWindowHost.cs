namespace Fravashi.Desktop;

/// <summary>
/// What a studio window asks of the resident host about the workspace it shows (backlog 29). The rules
/// themselves (<see cref="Fravashi.Launcher.WorkspaceWindowMap{TWindow}"/>,
/// <see cref="Fravashi.Launcher.WorkspaceNavigation"/>) live in the launcher library where they are unit
/// tested; this is only the seam the WinForms window reaches them through. Every member runs on the UI
/// thread.
/// </summary>
internal interface IWorkspaceWindowHost
{
    /// <summary>Whether a window OTHER than <paramref name="window"/> shows <paramref name="workspaceId"/>.</summary>
    bool IsShownByAnotherWindow(MainWindow window, string workspaceId);

    /// <summary>The window is following a navigation to another workspace: re-key it and re-title it.</summary>
    void Adopt(MainWindow window, string workspaceId);

    /// <summary>Brings forward the window that shows <paramref name="workspaceId"/>.</summary>
    void FocusWorkspace(string workspaceId);

    /// <summary>
    /// The workspace menu's "Close workspace": the window stops showing its workspace and lands on the
    /// home surface, or closes when another unbound window already exists. Host access is NOT revoked.
    /// </summary>
    void CloseWorkspace(MainWindow window);

    /// <summary>
    /// The workspace menu's recents: focus the window already showing that folder, or re-bind this one.
    /// </summary>
    void SwitchWorkspace(MainWindow window, string workspaceId);
}
