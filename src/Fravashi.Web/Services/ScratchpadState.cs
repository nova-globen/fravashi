namespace Fravashi.Web.Services;

/// <summary>
/// Whether the scratchpad is showing (backlog 67), held for the circuit rather than by either component that
/// uses it: the toggle lives in the window title bar and the panel in the layout, two trees with no parent in
/// common. The same shape as <see cref="AgentPanelState"/>, for the same reason.
/// <para>
/// Unlike the agent panel it needs no availability count: the layout draws the scratchpad, and the layout is
/// on every page, so the toggle is never a control for something that is not there.
/// </para>
/// </summary>
public sealed class ScratchpadState
{
    private bool _isOpen;

    /// <summary>Raised whenever <see cref="IsOpen"/> changes.</summary>
    public event Action? Changed;

    /// <summary>True while the scratchpad is drawn beside the page.</summary>
    public bool IsOpen => _isOpen;

    public void Toggle() => Set(!_isOpen);

    public void Close() => Set(false);

    private void Set(bool open)
    {
        if (_isOpen == open)
        {
            return;
        }

        _isOpen = open;
        Changed?.Invoke();
    }
}
