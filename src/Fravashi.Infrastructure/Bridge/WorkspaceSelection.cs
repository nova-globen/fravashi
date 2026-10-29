namespace Fravashi.Infrastructure.Bridge;

/// <summary>
/// The circuit's workspace (plan 2b-ii): captured ONCE from the initial request's <c>?ws=</c>
/// query (the studio window guarantees every top-level navigation carries it), then held for the
/// circuit's whole life across SPA navigations. Null means the circuit runs without a window
/// workspace and the settings-row fallback decides the mode.
/// </summary>
public sealed class WorkspaceSelection
{
    public string? WorkspaceId { get; private set; }

    public bool Captured { get; private set; }

    public void Capture(string? workspaceId)
    {
        if (Captured)
        {
            return;
        }

        Captured = true;
        WorkspaceId = string.IsNullOrWhiteSpace(workspaceId) ? null : workspaceId;
    }
}
