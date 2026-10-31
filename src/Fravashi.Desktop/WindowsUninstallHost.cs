using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Fravashi.Domain;
using Fravashi.Launcher;
using Microsoft.Win32;

namespace Fravashi.Desktop;

/// <summary>
/// The Windows parts of <c>fravashi uninstall</c> (backlog 375): what install-windows.ps1 and fravashi.exe left outside
/// the container engine. The order and the reporting are <see cref="Uninstaller"/>'s; each method here does one thing
/// and says what it did.
/// </summary>
internal sealed class WindowsUninstallHost : IUninstallHost
{
    /// <summary>The Apps &amp; Features entry install-windows.ps1 writes.</summary>
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Fravashi";

    private static readonly string[] ContextMenuKeys =
        [@"Software\Classes\Directory\shell\Fravashi", @"Software\Classes\Directory\Background\shell\Fravashi"];

    /// <summary>What a data copy leaves out: the agent CLIs' home and installs, the logs and WebView2's cache, all
    /// rebuilt at the next start (the studio's own backup leaves the same out).</summary>
    private static readonly string[] NotCopied =
        [Path.Combine("data", "home"), Path.Combine("data", ".npm-global"), Path.Combine("data", "logs"), "webview2"];

    public string CloseResident()
    {
        var signalled = new WindowsTrayControl().SignalExit();
        var self = Environment.ProcessId;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && Residents(self).Count > 0)
        {
            Thread.Sleep(250);
        }

        var left = Residents(self);
        return left.Count == 0
            ? signalled ? "closed" : "was not running"
            : $"still running (process {string.Join(", ", left)}); close it from the tray and run this again";
    }

    public string RemoveContextMenu()
    {
        var removed = 0;
        foreach (var key in ContextMenuKeys)
        {
            if (Registry.CurrentUser.OpenSubKey(key) is { } open)
            {
                open.Dispose();
                Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
                removed++;
            }
        }

        return removed == 0 ? "not there" : $"removed ({removed} keys)";
    }

    public string RemoveShortcuts()
    {
        var removed = 0;
        foreach (var folder in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                     Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                 })
        {
            var link = Path.Combine(folder, ProductName.Short + ".lnk");
            if (File.Exists(link))
            {
                File.Delete(link);
                removed++;
            }
        }

        return removed == 0 ? "not there" : $"removed ({removed})";
    }

    public string RemoveFromUserPath(string programFolder)
    {
        var current = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);
        var (newPath, changed) = UserPathEditor.Remove(current, programFolder);
        if (!changed)
        {
            return "not there";
        }

        Environment.SetEnvironmentVariable("Path", newPath, EnvironmentVariableTarget.User);
        return "removed (open a new terminal to see it)";
    }

    public string RemoveFirewallRules(string program)
    {
        var script = Path.Combine(Path.GetDirectoryName(program)!, "firewall-rule.ps1");
        if (!File.Exists(script))
        {
            return "no firewall-rule.ps1 beside the program (an install before 1.0.1 made no rule); any rule Windows " +
                   "made for it can be removed in Windows Defender Firewall, Inbound Rules";
        }

        var powerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Program \"{program}\" -Remove";
        var elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        var start = new ProcessStartInfo(powerShell, arguments)
        {
            UseShellExecute = !elevated,
            Verb = elevated ? string.Empty : "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = elevated,
        };

        try
        {
            using var process = Process.Start(start)!;
            if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
            {
                return "not removed: the administrator step did not finish within five minutes";
            }

            return process.ExitCode == 0 ? "removed" : $"not removed (exit code {process.ExitCode})";
        }
        catch (Win32Exception)
        {
            return "not removed: the administrator prompt was declined or could not be shown; remove the rules named " +
                   $"'{ProductName.Executable}' in Windows Defender Firewall, Inbound Rules";
        }
    }

    public string CopyData(string dataFolder, string destination)
    {
        var files = 0;
        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(dataFolder, "*", new EnumerationOptions
                 {
                     RecurseSubdirectories = true,
                     AttributesToSkip = FileAttributes.ReparsePoint,
                     IgnoreInaccessible = false,
                 }))
        {
            var relative = Path.GetRelativePath(dataFolder, file);
            if (NotCopied.Any(skip => relative.StartsWith(skip + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
            files++;
            bytes += new FileInfo(file).Length;
        }

        // One decimal: whole megabytes said "0 MB" for a real 0.7 MB copy on the test VM.
        var size = (bytes / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        return $"copied {files} files ({size} MB) to {destination}; to restore, put its contents back in " +
               $"{dataFolder} before installing {ProductName.Short} again";
    }

    public string RemoveDataFolder(string dataFolder)
    {
        if (!Directory.Exists(dataFolder))
        {
            return "not there";
        }

        Directory.Delete(dataFolder, recursive: true);
        return "removed";
    }

    public string RemoveUninstallEntry()
    {
        if (Registry.CurrentUser.OpenSubKey(UninstallKey) is not { } key)
        {
            return "not there";
        }

        key.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        return "removed";
    }

    public string ScheduleProgramFolderRemoval(string programFolder)
    {
        // This process runs from that folder, so a short detached step removes it once this process has exited.
        var start = new ProcessStartInfo("cmd.exe",
            $"/c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{programFolder}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        using var _ = Process.Start(start);
        return "removed a few seconds after this command ends";
    }

    private static List<int> Residents(int self) =>
        Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ProductName.Executable))
            .Where(p => p.Id != self)
            .Select(p => p.Id)
            .ToList();
}
