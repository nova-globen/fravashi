using Fravashi.Domain;

namespace Fravashi.Launcher;

/// <summary>
/// The Windows parts of an uninstall (backlog 375): what lives outside the container engine and the data folder. The
/// real one is in fravashi.exe (Windows APIs); the tests use a fake. Each call reports, never throws past itself.
/// </summary>
public interface IUninstallHost
{
    /// <summary>Asks a running resident fravashi.exe to close (its exit event) and waits briefly.</summary>
    string CloseResident();

    /// <summary>The two HKCU "Open with" context-menu keys.</summary>
    string RemoveContextMenu();

    /// <summary>The Start menu and Desktop shortcuts.</summary>
    string RemoveShortcuts();

    /// <summary>The program folder's entry on the user PATH.</summary>
    string RemoveFromUserPath(string programFolder);

    /// <summary>The firewall rules for the program, elevated (the one step that needs it). Null result: not removed,
    /// with the reason in the returned text.</summary>
    string RemoveFirewallRules(string program);

    /// <summary>A raw copy of the data folder (without its regenerable caches) at <paramref name="destination"/>.</summary>
    string CopyData(string dataFolder, string destination);

    /// <summary>The data folder itself.</summary>
    string RemoveDataFolder(string dataFolder);

    /// <summary>The Apps &amp; Features entry.</summary>
    string RemoveUninstallEntry();

    /// <summary>The program folder, after this process exits (a running executable cannot delete itself).</summary>
    string ScheduleProgramFolderRemoval(string programFolder);
}

/// <summary>Where an install lives: the program folder, the data folder and the engine names.</summary>
/// <param name="ProgramFolder">%LOCALAPPDATA%\Programs\Fravashi.</param>
/// <param name="DataFolder">%LOCALAPPDATA%\Fravashi (launcher.json, data\).</param>
/// <param name="ContainerName">The container launcher.json names (or the default).</param>
/// <param name="Image">The image launcher.json names (or the default).</param>
/// <param name="BackupFolder">Where <c>--remove-data</c> puts its copy (the user's Documents).</param>
public sealed record UninstallTarget(string ProgramFolder, string DataFolder, string ContainerName, string Image, string BackupFolder);

/// <summary>
/// <c>fravashi uninstall</c> (backlog 375): removes what an install left, current and former names alike, in an order
/// that never strands anything (the resident first, the program folder last). The DATA is kept unless
/// <c>--remove-data</c>, and then copied out first (the owner's "nothing must get lost", 2026-10-30). Every step
/// reports a line; a failed step is said and the next one still runs.
/// </summary>
public sealed class Uninstaller
{
    private readonly IContainerRuntime _runtime;
    private readonly IUninstallHost _host;
    private readonly Action<string> _say;
    private readonly Func<DateTimeOffset> _now;

    public Uninstaller(IContainerRuntime runtime, IUninstallHost host, Action<string> say, Func<DateTimeOffset> now)
    {
        _runtime = runtime;
        _host = host;
        _say = say;
        _now = now;
    }

    /// <summary>The containers to remove: the current one, the former one and the former VPN sidecar.</summary>
    public static IReadOnlyList<string> Containers(UninstallTarget target) =>
        Distinct([target.ContainerName, FormerInstall.ContainerName, FormerInstall.VpnContainerName]);

    /// <summary>The image references to untag: the current one and its localhost/ form, and the former one.</summary>
    public static IReadOnlyList<string> Images(UninstallTarget target) =>
        Distinct([target.Image, "localhost/" + target.Image, FormerInstall.Image, "localhost/" + FormerInstall.Image]);

    /// <summary>The networks to remove, current and former.</summary>
    public static IReadOnlyList<string> Networks() => [ContainerHardening.StudioNetwork, FormerInstall.StudioNetwork];

    /// <summary>The volumes, removed only with the data: they hold the agents' copies.</summary>
    public static IReadOnlyList<string> Volumes() => [LauncherDefaults.WorkspacesVolume, FormerInstall.WorkspacesVolume];

    /// <summary>Runs the uninstall. Returns true when nothing failed.</summary>
    public bool Run(UninstallTarget target, bool removeData)
    {
        var failed = false;
        void Step(string what, Func<string> act)
        {
            try
            {
                _say($"{what}: {act()}");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failed = true;
                _say($"{what}: FAILED ({ex.Message})");
            }
        }

        Step($"{ProductName.Executable}", _host.CloseResident);

        if (_runtime.IsAvailable())
        {
            foreach (var name in Containers(target))
            {
                Step($"container {name}", () => RemoveContainer(name));
            }

            foreach (var image in Images(target))
            {
                Step($"image {image}", () => RemoveImage(image));
            }

            foreach (var network in Networks())
            {
                Step($"network {network}", () => _runtime.RemoveNetwork(network) ? "removed" : "not there");
            }

            if (removeData)
            {
                foreach (var volume in Volumes())
                {
                    Step($"volume {volume}", () => _runtime.RemoveVolume(volume) ? "removed" : "not there");
                }
            }
        }
        else
        {
            _say($"container engine ({_runtime.Name}): not available, so its container, image and network were NOT removed; " +
                 $"remove them with {_runtime.Name} once it runs.");
            failed = true;
        }

        Step("context menu", _host.RemoveContextMenu);
        Step("shortcuts", _host.RemoveShortcuts);
        Step("user PATH", () => _host.RemoveFromUserPath(target.ProgramFolder));
        Step("firewall rules", () => _host.RemoveFirewallRules(Path.Combine(target.ProgramFolder, ProductName.Executable)));

        if (removeData)
        {
            var copy = Path.Combine(target.BackupFolder, $"{ProductName.Command}-data-{_now():yyyyMMdd-HHmmss}");
            var copied = false;
            Step("data copy", () =>
            {
                var said = _host.CopyData(target.DataFolder, copy);
                copied = true;
                return said;
            });
            if (copied)
            {
                Step("data folder", () => _host.RemoveDataFolder(target.DataFolder));
            }
            else
            {
                failed = true;
                _say($"data folder: KEPT, because the copy did not complete ({target.DataFolder}).");
            }
        }
        else
        {
            _say($"data folder: kept ({target.DataFolder}); reinstalling {ProductName.Short} picks it up again. " +
                 $"`{ProductName.Command} uninstall --remove-data` removes it after copying it out.");
        }

        Step("Apps & Features entry", _host.RemoveUninstallEntry);
        Step("program folder", () => _host.ScheduleProgramFolderRemoval(target.ProgramFolder));
        return !failed;
    }

    private string RemoveContainer(string name)
    {
        if (!_runtime.ContainerExists(name))
        {
            return "not there";
        }

        if (_runtime.ContainerRunning(name))
        {
            _runtime.StopContainer(name);
        }

        _runtime.RemoveContainer(name);
        return "removed";
    }

    private string RemoveImage(string image)
    {
        if (!_runtime.ImageExists(image))
        {
            return "not there";
        }

        _runtime.RemoveImage(image);
        return "removed";
    }

    private static IReadOnlyList<string> Distinct(IEnumerable<string> names) =>
        names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).ToList();
}
