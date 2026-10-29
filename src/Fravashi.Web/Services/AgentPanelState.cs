using Fravashi.Domain.AgentSessions;

namespace Fravashi.Web.Services;

/// <summary>
/// Whether the agent panel is showing, held for the circuit rather than inside the page that draws
/// it. The panel lives in the Code page's body; its toggle lives in the window title bar, which the
/// layout renders. Those are two component trees with no parent in common, so a private field on the
/// page could not serve both (backlog 23).
///
/// <para>
/// Availability is separate from openness on purpose: a toggle for a panel that is not on screen is
/// a lie, so the surface that can draw the panel <see cref="Attach"/>es while it is mounted and the
/// toggle renders only then. Openness SURVIVES a detach, so leaving the Code page and coming back
/// finds the panel as it was left, the way a window-chrome toggle is expected to behave.
/// </para>
///
/// <para>
/// Work item #366: the panel can be handed a CHOSEN conversation (<see cref="ShowBesideCode"/>, from the
/// Sessions area) instead of always showing the workspace's latest, and it carries whether the editor follows
/// the agent. "Open in Sessions" (<see cref="ReturnToSessions"/>) closes the panel and drops the choice, so a
/// conversation moves between the two surfaces rather than being shown live in both.
/// </para>
/// </summary>
public sealed class AgentPanelState
{
    /// <summary>Raised whenever <see cref="IsOpen"/>, <see cref="IsAvailable"/>, <see cref="SessionId"/> or <see cref="FollowAgent"/> changes.</summary>
    public event Action? Changed;

    /// <summary>
    /// How many mounted surfaces can draw the panel. A count rather than a flag because a component
    /// can legitimately be mounted twice around a navigation (the new instance initialises before the
    /// old one disposes), and a bool would let the departing instance switch the arriving one off.
    /// </summary>
    private int _attached;

    private bool _isOpen;

    /// <summary>True while the panel should be drawn by whichever surface hosts it.</summary>
    public bool IsOpen => _isOpen;

    /// <summary>True while a surface that draws the panel is mounted, so a toggle has something to act on.</summary>
    public bool IsAvailable => _attached > 0;

    /// <summary>The conversation chosen for the panel, or null for the workspace's latest (work item #366).</summary>
    public AgentSessionId? SessionId { get; private set; }

    /// <summary>Whether the editor follows the agent's file tools (work item #366). On by default.</summary>
    public bool FollowAgent { get; private set; } = true;

    public void Open() => Set(true);

    public void Close() => Set(false);

    public void Toggle() => Set(!_isOpen);

    /// <summary>Work item #366: show <paramref name="id"/> in the panel beside the code, opening it.</summary>
    public void ShowBesideCode(AgentSessionId id)
    {
        var changed = !_isOpen || !Equals(SessionId, id);
        SessionId = id;
        _isOpen = true;
        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// The panel's pane moved to another conversation on its own (a new one started, or it was cleared for a fresh
    /// one), so the choice follows it. Null means the workspace's latest again.
    /// </summary>
    public void Choose(AgentSessionId? id)
    {
        if (Equals(SessionId, id))
        {
            return;
        }

        SessionId = id;
        Changed?.Invoke();
    }

    /// <summary>Work item #366: "Open in Sessions". The panel closes and forgets its choice.</summary>
    public void ReturnToSessions()
    {
        var changed = _isOpen || SessionId is not null;
        _isOpen = false;
        SessionId = null;
        if (changed)
        {
            Changed?.Invoke();
        }
    }

    public void SetFollow(bool follow)
    {
        if (FollowAgent == follow)
        {
            return;
        }

        FollowAgent = follow;
        Changed?.Invoke();
    }

    /// <summary>Called by the surface that draws the panel as it mounts.</summary>
    public void Attach()
    {
        _attached++;
        if (_attached == 1)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Called by the surface that draws the panel as it unmounts. Never takes the count below zero.</summary>
    public void Detach()
    {
        if (_attached == 0)
        {
            return;
        }

        _attached--;
        if (_attached == 0)
        {
            Changed?.Invoke();
        }
    }

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
