using Fravashi.Domain;
using Fravashi.Launcher;
using Fravashi.Launcher.Releases;

namespace Fravashi.Desktop;

/// <summary>
/// fravashi.exe entry point. Console verbs run the launcher verb dispatch attached to the parent
/// console; window verbs (default `fravashi`, `fravashi onboarding`) run the GUI path: single instance,
/// splash, studio window, tray residency.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Named so a thread dump (and the exit watchdog's report, backlog 5) shows the product's own
        // thread by name; the product creates no other dedicated threads, the runtime names its own.
        Thread.CurrentThread.Name ??= "fravashi main";
        var parsed = DesktopCommandLine.Parse(args);
        if (!DesktopCommandLine.IsWindowVerb(parsed.Verb))
        {
            return RunConsoleVerb(parsed);
        }

        // Single instance per install: the resident fravashi.exe (window or tray) holds the mutex; a
        // second GUI launch signals the activate event and exits, so double-launching focuses the
        // existing window instead of racing the container. Reuses the historical tray mutex name so
        // `fravashi exit` keeps working against either generation.
        using var singleInstance = new Mutex(
            initiallyOwned: true, TrayControlNames.SingleInstanceMutex, out var acquired);
        using var activateEvent = new EventWaitHandle(
            initialState: false, EventResetMode.AutoReset, TrayControlNames.ActivateEvent);
        if (!acquired)
        {
            // Hand the folder (when given) to the resident instance, which opens/focuses its window.
            if (parsed.OpenPath is { Length: > 0 } openPath)
            {
                try
                {
                    var configDirectory = Path.GetDirectoryName(parsed.ConfigPath ?? LauncherConfigStore.DefaultPath)!;
                    File.WriteAllText(Path.Combine(configDirectory, TrayControlNames.OpenRequestFile), openPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best effort: the activation still focuses the resident instance.
                }
            }

            activateEvent.Set();
            return ExitCodes.Success;
        }

        // The bridge release: an install from before the internal rename has its resident under the FORMER mutex name. This
        // process holds that one too, so an old copy and this one never run together: an old resident still running is
        // asked to close (its exit event), and this start gives up if it does not.
        using var formerInstance = HoldFormerInstanceMutex();
        if (formerInstance is null)
        {
            return ExitCodes.UsageError;
        }

        ApplicationConfiguration.Initialize();

        // The splash FIRST (backlog 379): everything below (the data-folder move, the former-install migration, which asks
        // the container engine on every start, and the context's own setup) took 2 to 3 s on a cold start with nothing on
        // screen. Refresh paints it now, because no message loop runs until Application.Run.
        var splash = new SplashForm();
        splash.Show();
        splash.Refresh();
        var splashAfter = DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime;

        // The rename to Fravashi moves the former data folder to %LOCALAPPDATA%\Fravashi, here: this process holds the
        // single-instance mutex an older fravashi.exe used too, so no older resident is running, and nothing has opened a
        // file in either folder yet. Every path below follows the folder it settles on. Then the internal rename's
        // migration (container, image, database names), before anything starts the studio.
        MoveDataFolder(parsed);

        using var exitEvent = new EventWaitHandle(
            initialState: false, EventResetMode.ManualReset, TrayControlNames.ExitEvent);
        // Qualified: the launcher links NetworkFailure as source (backlog 230), so a Fravashi.Application namespace is
        // in scope here and a bare Application would name it.
        System.Windows.Forms.Application.Run(new DesktopApplicationContext(parsed, exitEvent, activateEvent, splash, splashAfter));
        return Environment.ExitCode;
    }

    /// <summary>
    /// Takes the single-instance mutex of an install from before the internal rename, so a resident of the old build and
    /// this one never run together (the bridge release). An old resident still running is asked to close through its
    /// exit event and given ten seconds; null, said on screen, when it is still there after that. Removed in the cleanup
    /// release.
    /// </summary>
    private static Mutex? HoldFormerInstanceMutex()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var mutex = new Mutex(initiallyOwned: true, TrayControlNames.FormerSingleInstanceMutex, out var acquired);
            if (acquired)
            {
                return mutex;
            }

            mutex.Dispose();
            if (attempt == 0)
            {
                WindowsTrayControl.Signal(TrayControlNames.FormerExitEvent);
            }

            Thread.Sleep(500);
        }

        MessageBox.Show(
            $"An earlier {Fravashi.Domain.ProductName.Short} is still running and did not close when asked. Close it from its "
            + $"icon near the clock, then start {Fravashi.Domain.ProductName.Short} again.",
            Fravashi.Domain.ProductName.Short,
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
        return null;
    }

    /// <summary>
    /// Runs <see cref="DataFolderMove"/> once at the resident's start and fixes <see cref="ProductHome.Current"/> to the
    /// folder it settled on. A separate instance started with <c>--config</c> moves nothing.
    /// </summary>
    private static void MoveDataFolder(ParsedCommand parsed)
    {
        if (parsed.ConfigPath is { Length: > 0 })
        {
            return;
        }

        var said = new List<string>();
        var result = new DataFolderMove(ProductHome.LocalAppData, StopForMove, () => DateTimeOffset.UtcNow, said.Add).Run();
        ProductHome.Use(result.Root);

        // The internal rename (docs/plans/rename-internal-names.md, phase 2), on the folder the move settled on and before
        // anything reads launcher.json's container or image name or opens the database.
        new FormerInstallMigration(
            new LauncherConfigStore(ProductHome.ConfigPath(result.Root)),
            ContainerRuntimeDetector.Detect,
            said.Add,
            () => DateTimeOffset.UtcNow).Run();

        if (said.Count > 0)
        {
            // Said in the launcher log of the folder the move settled on, which is where every later line goes.
            var log = new LauncherLog(ProductHome.DataDir(result.Root), _ => { });
            foreach (var line in said)
            {
                log.Line(line);
            }
        }
    }

    /// <summary>
    /// Stops the studio container before its data folder moves (it holds the database open through the bind mount);
    /// null once it is not running, else why that could not be made sure of.
    /// </summary>
    private static string? StopForMove(LauncherConfig? config)
    {
        if (config is null || string.IsNullOrWhiteSpace(config.ContainerName))
        {
            return null;
        }

        try
        {
            var runtime = ContainerRuntimeDetector.Detect(config);
            if (!runtime.IsAvailable())
            {
                return $"the '{runtime.Name}' container runtime is not answering, so whether the container still has "
                    + "the database open cannot be known";
            }

            if (runtime.ContainerExists(config.ContainerName) && runtime.ContainerRunning(config.ContainerName))
            {
                runtime.StopContainer(config.ContainerName);
                if (runtime.ContainerRunning(config.ContainerName))
                {
                    return $"'{config.ContainerName}' is still running after it was asked to stop";
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static int RunConsoleVerb(ParsedCommand parsed)
    {
        var seen = NativeConsole.AttachToParent();
        if (parsed.Verb == DesktopVerb.Uninstall)
        {
            return RunUninstall(parsed, seen);
        }

        var configPath = parsed.ConfigPath ?? LauncherConfigStore.DefaultPath;
        var configStore = new LauncherConfigStore(configPath);
        var storedConfig = configStore.Load();

        // Everything printed also lands in <data dir>/logs/launcher-*.log (directive
        // 2026-07-26), the same folder the studio container and the window write into.
        var launcherLog = new LauncherLog(
            string.IsNullOrWhiteSpace(storedConfig?.DataDir) ? LauncherApp.DefaultDataDir() : storedConfig!.DataDir!,
            Console.WriteLine);

        // The bridge release: a console verb that creates or recreates the studio container must not do it under the names
        // from before the internal rename, even when it runs before the first window start has migrated them.
        if (parsed.ConfigPath is null && parsed.Verb is DesktopVerb.Update or DesktopVerb.Restore or DesktopVerb.Repin)
        {
            new FormerInstallMigration(configStore, ContainerRuntimeDetector.Detect, launcherLog.Line, () => DateTimeOffset.UtcNow).Run();
            storedConfig = configStore.Load();
        }

        // Runtime-agnostic (ADR 0009): honour an explicit launcher.json `runtime`, else auto-detect.
        var runtime = ContainerRuntimeDetector.Detect(storedConfig);
        var app = new LauncherApp(
            runtime,
            configStore,
            launcherDirectory: AppContext.BaseDirectory,
            log: launcherLog.Line,
            openBrowser: url => BrowserLauncher.Open(url, launcherLog.Line),
            containerNameOverride: parsed.ContainerName,
            // Backlog 157: `fravashi update` hands the update to the running fravashi.exe when one answers, so the studio keeps
            // its bridge; with none running it updates by itself.
            residentUpgrade: new HttpResidentUpgrade(configStore.ConfigDirectory, () => configStore.Load()?.UpgradePort));

        return DesktopCommandLine.Execute(parsed, app, launcherLog.Line);
    }

    /// <summary>
    /// <c>fravashi uninstall</c> (backlog 375). Runs only from the installed program folder, never from a build output,
    /// because it removes the folder it runs from. Asks first unless <c>--yes</c> (Windows Settings asks before it starts
    /// the Apps &amp; Features entry, which passes it); with no console it asks and reports in a window. With
    /// <c>--remove-data</c> nothing is written to the launcher log, which lives in the data folder being removed.
    /// </summary>
    private static int RunUninstall(ParsedCommand parsed, bool seen)
    {
        var programFolder = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var installed = Path.Combine(ProductHome.LocalAppData, "Programs", ProductName.Short);
        if (!string.Equals(Path.GetFullPath(programFolder), Path.GetFullPath(installed), StringComparison.OrdinalIgnoreCase))
        {
            Say(seen, $"`{ProductName.Command} uninstall` runs only from the installed {ProductName.Executable} ({installed}); " +
                      $"this one runs from {programFolder}. Nothing was changed.");
            return ExitCodes.UsageError;
        }

        var dataFolder = ProductHome.Current;
        var config = new LauncherConfigStore(ProductHome.ConfigPath(dataFolder)).Load();
        var target = new UninstallTarget(
            programFolder,
            dataFolder,
            string.IsNullOrWhiteSpace(config?.ContainerName) ? LauncherDefaults.ContainerName : config!.ContainerName!,
            string.IsNullOrWhiteSpace(config?.Image) ? LauncherDefaults.Image : config!.Image!,
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));

        var question = parsed.RemoveData
            ? $"Remove {ProductName.Full} AND its data ({dataFolder})? The data is copied to {target.BackupFolder} first."
            : $"Remove {ProductName.Full}? Your data ({dataFolder}) is kept, and a reinstall picks it up again.";
        if (!parsed.Yes && !Confirm(seen, question))
        {
            Say(seen, "Nothing was changed.");
            return ExitCodes.Success;
        }

        var said = new List<string>();
        var log = parsed.RemoveData
            ? null
            : new LauncherLog(string.IsNullOrWhiteSpace(config?.DataDir) ? ProductHome.DataDir(dataFolder) : config!.DataDir!, _ => { });
        void Line(string text)
        {
            said.Add(text);
            if (seen)
            {
                Console.WriteLine(text);
            }

            log?.Line("uninstall: " + text);
        }

        var ok = new Uninstaller(ContainerRuntimeDetector.Detect(config), new WindowsUninstallHost(), Line, () => DateTimeOffset.Now)
            .Run(target, parsed.RemoveData);
        var summary = (ok ? $"{ProductName.Full} is removed." : $"{ProductName.Full} is removed, except where a line says FAILED.")
                      + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, said);
        if (seen)
        {
            Console.WriteLine(ok ? $"{ProductName.Full} is removed." : "Some steps FAILED; see the lines above.");
        }
        else
        {
            MessageBox.Show(summary, ProductName.Full, MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        return ok ? ExitCodes.Success : ExitCodes.UninstallIncomplete;
    }

    private static bool Confirm(bool seen, string question)
    {
        if (!seen)
        {
            return MessageBox.Show(question, ProductName.Full, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        Console.Write(question + " [y/N] ");
        var answer = Console.ReadLine();
        return answer is not null && answer.Trim().StartsWith('y');
    }

    private static void Say(bool seen, string text)
    {
        if (seen)
        {
            Console.WriteLine(text);
        }
        else
        {
            MessageBox.Show(text, ProductName.Full, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
