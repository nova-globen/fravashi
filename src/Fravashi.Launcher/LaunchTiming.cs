using System.Globalization;
using Fravashi.Domain;

namespace Fravashi.Launcher;

/// <summary>
/// How fast a launch shows something (backlog 379). The owner saw nothing for 2 to 3 s after a Start-menu click: the
/// splash only came after the data-folder move and the former-install migration, whose cost depends on the machine (a
/// cold first start, podman's slower engine). The window path now shows the splash before those, and says in the
/// launcher log how long after the process started it was on screen, so the figure can be read on any machine.
/// </summary>
public static class LaunchTiming
{
    /// <summary>The target the item set: the splash within about this long of the click.</summary>
    public static readonly TimeSpan Target = TimeSpan.FromMilliseconds(300);

    /// <summary>The launcher-log line: "The splash was shown 412 ms after fravashi.exe started.", with a note past the
    /// target, so a slow start is visible in the log rather than only felt.</summary>
    public static string SplashShown(TimeSpan after)
    {
        var ms = Math.Max(0, (long)Math.Round(after.TotalMilliseconds));
        var line = string.Create(CultureInfo.InvariantCulture, $"The splash was shown {ms} ms after {ProductName.Executable} started.");
        return after > Target
            ? line + string.Create(CultureInfo.InvariantCulture, $" That is past the {(long)Target.TotalMilliseconds} ms target (backlog 379).")
            : line;
    }
}
