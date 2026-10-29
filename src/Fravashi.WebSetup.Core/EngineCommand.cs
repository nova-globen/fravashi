namespace Fravashi.WebSetup;

/// <summary>
/// The program and argument LIST that runs the engine. A list, handed to <c>ProcessStartInfo.ArgumentList</c>,
/// rather than a command line built by concatenation as the Inno wizard did: each value stays one argument
/// whatever it contains.
/// </summary>
public sealed record EngineCommand(string FileName, IReadOnlyList<string> Arguments)
{
    /// <summary>
    /// Windows PowerShell 5.1 by its full path under the system folder, never <c>powershell</c> from PATH:
    /// what is first on PATH is exactly what broke 1.0.2 (GNU tar ahead of System32, backlog 258), and 5.1 is
    /// on every supported Windows while PowerShell 7 is not.
    /// </summary>
    public static string WindowsPowerShell(string systemDirectory) =>
        Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>
    /// <c>powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File &lt;engine&gt; -DownloadBase
    /// &lt;channel&gt; [-Version &lt;x.y.z&gt;]</c>: this build's update channel (<see cref="SetupOptions.DownloadsBase"/>),
    /// and a version when one was asked for. Bypass because the machine's policy may be Restricted, as the Inno wizard
    /// already passed it. No <c>-Pause</c>: the wizard shows the output itself, and the engine's stdin is a closed pipe
    /// (see <see cref="EngineRunner"/>), so it never waits for anybody.
    /// </summary>
    public static EngineCommand For(string powerShell, string scriptPath, SetupOptions options)
    {
        var arguments = new List<string>
        {
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            Path.GetFullPath(scriptPath),
            "-DownloadBase",
            SetupOptions.DownloadsBase,
        };

        if (options.Version is { } version)
        {
            if (SetupOptions.NormalizeVersion(version) != version)
            {
                throw new ArgumentException($"'{version}' is not a version.", nameof(options));
            }

            arguments.Add("-Version");
            arguments.Add(version);
        }

        return new EngineCommand(powerShell, arguments);
    }

    /// <summary>The command as one line for the log. Holds paths and the version only.</summary>
    public override string ToString() =>
        FileName + " " + string.Join(' ', Arguments.Select(a => a.Contains(' ', StringComparison.Ordinal) ? "\"" + a + "\"" : a));
}
