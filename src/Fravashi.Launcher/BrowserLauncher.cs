using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Fravashi.Launcher;

/// <summary>Opens a URL in the platform default browser.</summary>
public static class BrowserLauncher
{
    public static void Open(string url, Action<string>? log = null)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
            else
            {
                Process.Start("xdg-open", url);
            }
        }
        catch (Exception ex)
        {
            // Never fail the launch just because a browser could not be opened. Backlog item 2:
            // report through the injected sink so a WinExe host still surfaces this in the log file.
            (log ?? Console.WriteLine)($"Open {url} in your browser (auto-open failed: {ex.Message}).");
        }
    }
}
