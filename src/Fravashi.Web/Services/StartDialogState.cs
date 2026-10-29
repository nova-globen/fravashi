using Fravashi.Application.Demo;
using Fravashi.Infrastructure.Bridge;

namespace Fravashi.Web.Services;

/// <summary>
/// Work item #350: whether the start dialog (Shell/StartDialog.razor) is open OVER a window that has a folder.
/// Circuit-scoped, like <see cref="AgentPanelState"/>, because the control that opens it (the title bar's workspace
/// button) and the host that draws it (Shell/StartDialogHost.razor, rendered by the layout) are in component trees
/// with no parent in common.
/// <para>
/// A window with NO folder does not ask this at all: it shows the dialog in place of the studio, always, and it
/// cannot be closed (<see cref="NoFolderOpen"/>).
/// </para>
/// </summary>
public sealed class StartDialogState
{
    /// <summary>True while the dialog is drawn over the page.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Raised whenever <see cref="IsOpen"/> changes.</summary>
    public event Action? Changed;

    public void Open()
    {
        if (IsOpen)
        {
            return;
        }

        IsOpen = true;
        Changed?.Invoke();
    }

    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        IsOpen = false;
        Changed?.Invoke();
    }

    /// <summary>
    /// Work item #350: "no folder open means no studio". True when this window could open a folder (the host bridge
    /// is configured, which is every window fravashi.exe draws) and has none. Without a bridge (a host <c>dotnet run</c>
    /// against the local clone) there is no folder to pick and the clone IS the folder, so the studio is shown as
    /// before; a demo install has no registered workspace by design (backlog 71), so it is never locked out either.
    /// </summary>
    public static bool NoFolderOpen(BridgeWorkspaceContext workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return workspace.IsBridgeConfigured
            && string.IsNullOrWhiteSpace(workspace.ActiveWorkspaceId)
            && !DemoMode.IsEnabled;
    }
}
