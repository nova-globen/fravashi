using System.Reflection;
using Fravashi.Domain;

namespace Fravashi.Launcher;

/// <summary>The <c>fravashi</c> verbs. <see cref="Default"/> is the no-verb launch.</summary>
public enum DesktopVerb
{
    Default,
    Onboarding,
    Stop,
    Exit,
    Status,
    Website,
    Logs,
    Broker,
    Help,
    Version,
    Update,
    Repin,
    Restore,
    Unknown,
}

/// <summary>A parsed <c>fravashi</c> command line: the verb plus global options and verb arguments.</summary>
/// <param name="Verb">The resolved verb.</param>
/// <param name="ConfigPath">Override for the launcher.json path (<c>--config</c>) - the safe-testing seam.</param>
/// <param name="ContainerName">Override for the container name (<c>--name</c>) - the safe-testing seam.</param>
/// <param name="Tail">Line count for <c>fravashi logs</c> (<c>--tail</c>), default 200.</param>
/// <param name="UnknownToken">The offending token when <paramref name="Verb"/> is <see cref="DesktopVerb.Unknown"/>.</param>
/// <param name="VerbArgument">
/// The second bare token: the verb's own argument. <c>fravashi broker &lt;url&gt;</c> reads it as an endpoint and
/// <c>fravashi restore &lt;zip&gt;</c> as an archive path. Null when the verb was given none.
/// </param>
/// <param name="OpenPath">
/// The folder from <c>fravashi --open PATH</c> (plan 2a): the GUI path registers it as a bridge
/// workspace. Parsed for any verb; console verbs simply ignore it.
/// </param>
public sealed record ParsedCommand(
    DesktopVerb Verb,
    string? ConfigPath = null,
    string? ContainerName = null,
    int Tail = 200,
    string? UnknownToken = null,
    string? VerbArgument = null,
    string? OpenPath = null)
{
    /// <summary>The verb argument read under the name <c>fravashi broker</c> gives it.</summary>
    public string? BrokerUrl => VerbArgument;

    /// <summary>The verb argument read under the name <c>fravashi restore</c> gives it.</summary>
    public string? ArchivePath => VerbArgument;
}

/// <summary>
/// Hand-rolled parser and dispatcher for the <c>fravashi</c> command line - small enough that a dependency
/// (System.CommandLine) would not earn its keep. Parsing is pure and fully unit-tested; dispatch maps a
/// parsed verb onto the matching <see cref="LauncherApp"/> method.
/// </summary>
public static class DesktopCommandLine
{
    /// <summary>Parses argv into a <see cref="ParsedCommand"/>. Never throws - a bad token becomes <see cref="DesktopVerb.Unknown"/>.</summary>
    public static ParsedCommand Parse(string[] args)
    {
        string? configPath = null;
        string? containerName = null;
        var tail = 200;
        string? verbToken = null;
        string? verbArg = null;
        string? openPath = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--config" when i + 1 < args.Length:
                    configPath = args[++i];
                    break;
                case "--name" when i + 1 < args.Length:
                    containerName = args[++i];
                    break;
                case "--tail" when i + 1 < args.Length && int.TryParse(args[i + 1], out var t):
                    tail = t;
                    i++;
                    break;
                case "--open" when i + 1 < args.Length:
                    openPath = args[++i];
                    break;
                default:
                    // First bare (non-option) token is the verb; the second is its argument (e.g. the broker URL);
                    // any later bare tokens are ignored.
                    if (verbToken is null)
                    {
                        verbToken = arg;
                    }
                    else
                    {
                        verbArg ??= arg;
                    }

                    break;
            }
        }

        var verb = ResolveVerb(verbToken);
        return verb == DesktopVerb.Unknown
            ? new ParsedCommand(DesktopVerb.Unknown, configPath, containerName, tail, verbToken, OpenPath: openPath)
            : new ParsedCommand(verb, configPath, containerName, tail, VerbArgument: verbArg, OpenPath: openPath);
    }

    private static DesktopVerb ResolveVerb(string? token) => token?.ToLowerInvariant() switch
    {
        null => DesktopVerb.Default,
        "run" => DesktopVerb.Default,
        "onboarding" or "settings" => DesktopVerb.Onboarding,
        "stop" => DesktopVerb.Stop,
        "exit" => DesktopVerb.Exit,
        "status" => DesktopVerb.Status,
        "update" => DesktopVerb.Update,
        "repin" => DesktopVerb.Repin,
        "restore" => DesktopVerb.Restore,
        "website" => DesktopVerb.Website,
        "logs" => DesktopVerb.Logs,
        "broker" => DesktopVerb.Broker,
        "help" or "--help" or "-h" or "-?" or "/?" => DesktopVerb.Help,
        "version" or "--version" or "-v" => DesktopVerb.Version,
        _ => DesktopVerb.Unknown,
    };

    /// <summary>
    /// True for verbs that open the studio window (the merged fravashi.exe's GUI path: splash, window,
    /// tray residency); false for console verbs that attach to the parent console, print, and exit.
    /// </summary>
    public static bool IsWindowVerb(DesktopVerb verb) => verb is DesktopVerb.Default or DesktopVerb.Onboarding;

    /// <summary>
    /// Executes a parsed command against <paramref name="app"/>, writing help/version/usage text through
    /// <paramref name="output"/>. Returns a process exit code.
    /// </summary>
    public static int Execute(ParsedCommand command, LauncherApp app, Action<string> output)
    {
        switch (command.Verb)
        {
            case DesktopVerb.Default:
                return app.Run();
            case DesktopVerb.Onboarding:
                return app.Onboarding();
            case DesktopVerb.Stop:
                return app.Stop();
            case DesktopVerb.Exit:
                return app.Exit();
            case DesktopVerb.Status:
                return app.Status();
            case DesktopVerb.Update:
                return app.Update();
            case DesktopVerb.Repin:
                return app.Repin();
            case DesktopVerb.Website:
                return app.Website();
            case DesktopVerb.Logs:
                return app.Logs(command.Tail);
            case DesktopVerb.Broker:
                return app.SetBroker(command.BrokerUrl);
            case DesktopVerb.Restore:
                return app.Restore(command.ArchivePath);
            case DesktopVerb.Help:
                output(HelpText);
                return ExitCodes.Success;
            case DesktopVerb.Version:
                output($"{ProductName.Command} {Version}");
                return ExitCodes.Success;
            default:
                output($"{ProductName.Command}: unknown command '{command.UnknownToken}'.");
                output($"Run `{ProductName.Command} help` for the list of commands.");
                return ExitCodes.UsageError;
        }
    }

    /// <summary>The launcher assembly's informational/file version, for <c>fravashi version</c>.</summary>
    public static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "0.0.0";

    /// <summary>The full <c>fravashi help</c> reference (F10). Also printed by the installer on completion.</summary>
    public static string HelpText =>
        $"""
        {ProductName.Full} - the `{ProductName.Command}` command

        Usage: {ProductName.Command} [command] [options]

        Commands:
          (none)        Start {ProductName.Short}: ensure the container is running and open the browser.
                        On Windows this also starts the notification-area tray.
          onboarding    Open the onboarding / settings wizard in the browser (alias: settings).
          stop          Stop the container. The reserved port is kept; start again with `{ProductName.Command}`.
          exit          Close the tray only. The container keeps running (see `stop` to stop it).
          status        Show container state, port, URL, data directory, version, and update status.
          update        Check for a new {ProductName.Short} release and, on confirmation, install it (migrates the DB).
          repin         Re-pin the integrity digest to the CURRENT local image after YOU changed it
                        (a reinstall, a retag, or a local build). Shows the old and new digests and
                        asks before writing. Recovery when `{ProductName.Command}` refuses with an integrity mismatch.
          restore ZIP   Restore a backup taken in Settings > Data > Backup and restore. Verifies the archive
                        first, stops {ProductName.Short}, MOVES the current database, key ring and agent sessions
                        into <data dir>/backups/pre-restore-<stamp> (never deletes them), unpacks, and
                        starts {ProductName.Short} again. Asks before it changes anything.
          website       Open the product website in the browser.
          logs          Print the container's recent logs (use --tail N; default 200).
          broker URL    Point this studio at a credential broker (corp-broker tier, ADR 0010);
                        `{ProductName.Command} broker off` clears it. Applied on the next container (re)creation.
          version       Print the {ProductName.Command} version.
          help          Show this help.

        Options:
          --open PATH    Open a host folder as a bridge workspace on launch (window verbs only).
          --tail N       Number of log lines for `{ProductName.Command} logs` (default 200).
          --config PATH  Use an alternate launcher.json (for testing a separate instance).
          --name NAME    Use an alternate container name (for testing a separate instance).

        The tray (notification-area icon) is Windows-only today; on macOS and Linux the
        commands above are the full surface. `stop` vs `exit`: stop stops the product;
        exit only closes the tray icon and leaves the product running.
        """;
}
