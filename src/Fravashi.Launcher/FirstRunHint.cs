using Fravashi.Domain;

namespace Fravashi.Launcher;

/// <summary>
/// Prints the "run `fravashi help` for commands" hint once, then drops a marker so later launches stay quiet
/// (F10). The marker sits beside launcher.json, so a <c>--config</c> override gets its own hint state and
/// tests never touch the real one. An install from before the internal rename left its marker under the former name
/// (<see cref="FormerInstall.FirstRunHintMarker"/>): that counts as shown too (the bridge release).
/// </summary>
public static class FirstRunHint
{
    /// <summary>The marker's name beside launcher.json.</summary>
    public const string MarkerName = ".fravashi-hint-shown";

    public static void ShowOnce(string configPath, Action<string>? output = null)
    {
        var write = output ?? Console.WriteLine;
        var directory = Path.GetDirectoryName(configPath);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        var marker = Path.Combine(directory, MarkerName);
        try
        {
            if (File.Exists(marker) || File.Exists(Path.Combine(directory, FormerInstall.FirstRunHintMarker)))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            File.WriteAllText(marker, "shown");
        }
        catch (IOException)
        {
            // A hint is never worth failing a launch over.
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        write($"Tip: run `{ProductName.Command} help` to see all commands (onboarding, stop, status, website, and more).");
    }
}
