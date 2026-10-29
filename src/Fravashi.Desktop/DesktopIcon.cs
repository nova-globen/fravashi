using System.Drawing;
using System.Reflection;

namespace Fravashi.Desktop;

/// <summary>
/// The brand icon (<c>src/Fravashi.Launcher/fravashi.ico</c>, embedded by the csproj so it survives a
/// single-file publish). One loader for every window that shows it, because there were three copies of
/// the same three lines and the splash was the one that had no copy at all: it is borderless but sits in
/// the TASKBAR, and a WinForms form with no <see cref="Form.Icon"/> shows the framework's default there,
/// not the executable's, so the first thing a launch put on screen was not branded (backlog 112).
/// </summary>
internal static class DesktopIcon
{
    private const string ResourceName = "Fravashi.Desktop.icon.ico";

    /// <summary>
    /// Loads a fresh <see cref="Icon"/> (each caller owns and disposes its own). Falls back to the system
    /// application icon rather than throwing: a missing brand resource must never stop a window opening.
    /// </summary>
    public static Icon Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        return stream is null ? SystemIcons.Application : new Icon(stream);
    }
}
