using Microsoft.FluentUI.AspNetCore.Components;

namespace Fravashi.Web.Services;

/// <summary>
/// Holds the active Fluent design-theme mode for the circuit and cycles System → Light → Dark.
/// Bound by <see cref="FluentDesignTheme"/> in the layout and persisted client-side under
/// <c>fravashi-theme</c>; mirrors the company site's theming so the studio and the product match.
/// </summary>
public sealed class ThemeState
{
    /// <summary>Raised when <see cref="Mode"/> changes so the layout and status bar can re-render.</summary>
    public event Action? Changed;

    private DesignThemeModes _mode = DesignThemeModes.System;

    /// <summary>
    /// The current appearance: follow the OS (default) or force light/dark. The setter raises
    /// <see cref="Changed"/> so every subscriber re-renders - including when <c>FluentDesignTheme</c>
    /// restores the persisted mode from localStorage on circuit start (otherwise the status-bar
    /// toggle keeps showing the initial System label).
    /// </summary>
    public DesignThemeModes Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
            {
                return;
            }

            _mode = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Cycles System → Light → Dark and returns the new mode.</summary>
    public DesignThemeModes Cycle()
    {
        Mode = Mode switch
        {
            DesignThemeModes.System => DesignThemeModes.Light,
            DesignThemeModes.Light => DesignThemeModes.Dark,
            _ => DesignThemeModes.System,
        };
        return Mode;
    }
}
