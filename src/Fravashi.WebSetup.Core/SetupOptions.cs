using System.Text.RegularExpressions;

namespace Fravashi.WebSetup;

/// <summary>
/// What FravashiSetup.exe was asked to do. With no arguments it is the wizard; <c>/quiet</c> runs the same
/// install with no window, for a host driving an end-to-end install on the test VM with nobody at its desktop:
/// <code>
/// FravashiSetup.exe /quiet [/version:1.0.0] [/log:C:\path\setup.log]
/// </code>
/// Each option is <c>/name:value</c> or <c>/name=value</c>, and a leading <c>-</c> or <c>--</c> works as well as
/// <c>/</c>. The wizard takes the same options as its defaults.
/// <para>
/// There is one source: the update channel on this build's service host, whose signed manifest names what
/// to download. The forge the setup could also install from retired at F1, so <c>/forge</c>, <c>/tag:</c>,
/// <c>/tokenfile:</c> and <c>/variant:</c> are refused with that reason rather than ignored.
/// </para>
/// </summary>
public sealed record SetupOptions
{
    public const string LatestTag = "latest";

    /// <summary>
    /// The update channel on this build's service host, handed to the engine as <c>-DownloadBase</c> so a
    /// test build installs from its own host; the engine's own default is production's.
    /// </summary>
    public static string DownloadsBase => Fravashi.Domain.ServiceHost.UpdateChannelUrl;

    public bool Quiet { get; init; }

    public bool ShowHelp { get; init; }

    /// <summary>The version on the update channel, without a <c>v</c>, or null for its <c>latest/</c> folder.</summary>
    public string? Version { get; init; }

    /// <summary>An absolute path for the log, or null for the default under the temp folder.</summary>
    public string? LogPath { get; init; }

    /// <summary>What is being installed and from where, in words for the log and the console.</summary>
    public string Describe() => $"{Version ?? LatestTag} from the update channel ({DownloadsBase}/{Version ?? LatestTag}/)";

    public const string Usage =
        "FravashiSetup.exe [/quiet] [/version:<x.y.z, default latest>] [/log:<path>]\n" +
        "The release comes from the update channel, its signed manifest verified before anything it names is downloaded.\n" +
        "Exit codes: 0 installed, 1 install failed, 2 bad arguments or this usage (nothing started), " +
        "4 Windows PowerShell did not run the engine, 5 cancelled, 6 no release is published at that version.\n" +
        "Run it with Start-Process -Wait -PassThru (or start /wait) to wait for the exit code.";

    // The engine's own check of a channel folder, '^(latest|[0-9][0-9A-Za-z._-]*)$', bounded in length. A version becomes
    // a URL segment and a PowerShell argument, so a closed alphabet with no leading hyphen.
    private static readonly Regex VersionShape = new("^[0-9][0-9A-Za-z._-]{0,99}$", RegexOptions.CultureInvariant);

    /// <summary>A version as given (a leading <c>v</c> is dropped), or null when it is not one.</summary>
    public static string? NormalizeVersion(string? text)
    {
        var value = text?.Trim() ?? string.Empty;
        if (value.Length > 1 && value[0] is 'v' or 'V')
        {
            value = value[1..];
        }

        return VersionShape.IsMatch(value) ? value : null;
    }

    /// <summary>
    /// The options with the release the wizard's settings page chose: the version as typed, where empty or <c>latest</c>
    /// is the newest. Null with <paramref name="problem"/> when it cannot be used.
    /// </summary>
    public SetupOptions? Choose(string? release, out string? problem)
    {
        var text = release?.Trim() ?? string.Empty;
        problem = null;
        if (text.Length == 0 || string.Equals(text, LatestTag, StringComparison.OrdinalIgnoreCase))
        {
            return this with { Version = null };
        }

        if (NormalizeVersion(text) is not { } version)
        {
            problem = $"'{text}' is not a version (for example 1.0.0), or leave it empty for the newest.";
            return null;
        }

        return this with { Version = version };
    }

    /// <summary>
    /// Whether the command line asks for the silent mode, read on its own so that a run whose OTHER arguments
    /// are wrong still answers with exit code 2 on the console instead of a message box nobody can dismiss.
    /// Every spelling <see cref="Parse"/> accepts counts.
    /// </summary>
    public static bool AsksForQuiet(IReadOnlyList<string> args) =>
        args.Any(raw =>
        {
            var arg = raw.Trim();
            var body = arg.StartsWith("--", StringComparison.Ordinal) ? arg[2..]
                : arg.Length > 0 && arg[0] is '/' or '-' ? arg[1..]
                : null;
            return body?.ToLowerInvariant() is "quiet" or "q" or "silent";
        });

    /// <summary>
    /// Parses the command line. Returns the options, or null with <paramref name="error"/> saying what was
    /// wrong, so a silent run can exit with <see cref="SetupExitCode.BadArguments"/> and a reason.
    /// </summary>
    public static SetupOptions? Parse(IReadOnlyList<string> args, out string? error)
    {
        var options = new SetupOptions();
        foreach (var raw in args)
        {
            var arg = raw.Trim();
            if (arg.Length == 0)
            {
                continue;
            }

            var body = arg.StartsWith("--", StringComparison.Ordinal) ? arg[2..]
                : arg[0] is '/' or '-' ? arg[1..]
                : null;
            if (string.IsNullOrEmpty(body))
            {
                error = $"'{raw}' is not an option. {Usage}";
                return null;
            }

            var separator = body.IndexOfAny([':', '=']);
            var name = (separator < 0 ? body : body[..separator]).ToLowerInvariant();
            var value = separator < 0 ? null : body[(separator + 1)..].Trim();

            switch (name)
            {
                case "quiet" or "q" or "silent" when value is null:
                    options = options with { Quiet = true };
                    break;
                case "?" or "h" or "help" when value is null:
                    options = options with { ShowHelp = true };
                    break;
                case "version" when !string.IsNullOrEmpty(value):
                    if (string.Equals(value, LatestTag, StringComparison.OrdinalIgnoreCase))
                    {
                        options = options with { Version = null };
                    }
                    else if (NormalizeVersion(value) is { } version)
                    {
                        options = options with { Version = version };
                    }
                    else
                    {
                        error = $"'{value}' is not a version (for example 1.0.0, or latest).";
                        return null;
                    }

                    break;
                case "log" when !string.IsNullOrEmpty(value):
                    var log = Absolute(value, out error);
                    if (log is null)
                    {
                        return null;
                    }

                    options = options with { LogPath = log };
                    break;
                case "forge" or "tag" or "tokenfile" or "variant":
                    // The forge retired at F1: a command line from a setup before F1 is told why, not left to install something else.
                    error = $"'{raw}' installed from the forge, which retired; the release now comes from the update channel " +
                            $"(/version: picks one). {Usage}";
                    return null;
                default:
                    error = $"'{raw}' is not understood. {Usage}";
                    return null;
            }
        }

        error = null;
        return options;
    }

    private static string? Absolute(string value, out string? error)
    {
        try
        {
            error = null;
            return Path.GetFullPath(value.Trim('"'));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"'{value}' is not a usable path ({ex.Message}).";
            return null;
        }
    }
}
