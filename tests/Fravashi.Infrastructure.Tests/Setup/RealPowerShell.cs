using System.ComponentModel;
using System.Diagnostics;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Setup;

/// <summary>
/// Resolves a PowerShell interpreter to drive <c>setup/webinstall/webinstall.ps1</c>'s dot-sourceable
/// decision functions against (backlog 272), or explains why there is none.
/// <para>
/// Tries <c>pwsh</c> (PowerShell 7, cross-platform) first and falls back to <c>powershell</c> (Windows
/// PowerShell 5.1), both resolved from PATH. Unlike the agent CLI (<c>RealClaudeCli</c>), there is no
/// pinned version to miss here: the engine itself is held to running under both, so the test only needs
/// whichever one is present to drive it.
/// </para>
/// </summary>
internal static class RealPowerShell
{
    private static readonly Lazy<string?> Resolved = new(Resolve);

    /// <summary>The interpreter executable name to run, or null when none was found.</summary>
    public static string? ExeName => Resolved.Value;

    /// <summary>Null when a test can run; otherwise why it cannot.</summary>
    public static string? SkipReason =>
        ExeName is not null
            ? null
            : "No PowerShell interpreter on PATH (tried pwsh, then powershell); this container does not ship one.";

    private static string? Resolve()
    {
        foreach (var candidate in new[] { "pwsh", "powershell" })
        {
            if (CanRun(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="exeName"/> (pwsh or powershell) runs here.</summary>
    public static bool IsAvailable(string exeName) => CanRun(exeName);

    private static bool CanRun(string exeName)
    {
        try
        {
            var info = new ProcessStartInfo(exeName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-NonInteractive");
            // Matches how FravashiWebSetup.exe itself launches the engine (it cannot assume an
            // unrestricted execution policy on the machine being installed on), and a test here is held
            // to the same standard: a default Restricted policy would otherwise refuse the dot-source
            // and every assertion below would fail on "0 calls" with no hint why.
            info.ArgumentList.Add("-ExecutionPolicy");
            info.ArgumentList.Add("Bypass");
            info.ArgumentList.Add("-Command");
            info.ArgumentList.Add("$PSVersionTable.PSVersion.Major");

            using var process = Process.Start(info);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10_000);
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs a command with the resolved interpreter and returns its stdout and stderr. Stdout is null
    /// only if the process could not be started or timed out; stderr is always whatever it wrote
    /// (whether or not the process also produced stdout), so a caller that cannot find the output line
    /// it expected can report WHY rather than just "the count was wrong".
    /// <para>
    /// Passed as <c>-EncodedCommand</c> (the command's UTF-16LE bytes, base64), never <c>-Command</c>
    /// with the text itself, which the commands here would need: they interpolate strings inside
    /// <c>Write-Output "..."</c>, and <c>-EncodedCommand</c> carries that text as bytes rather than as
    /// an argument the interpreter's own command-line parsing has to re-quote, so no quoting rule of
    /// either interpreter applies to it at all.
    /// </para>
    /// <para>
    /// The process exit code is deliberately not read as a success signal for these commands: whether
    /// a run did what was expected is read from the printed <c>RESULT=</c>/<c>CALLS=</c>/<c>LOG:</c>
    /// lines (or the exit-to-class lines), not from the process's own exit code.
    /// </para>
    /// <para>
    /// <paramref name="redirectStandardInput"/> gives the child a PIPE for stdin (closed immediately,
    /// since nothing here ever needs to write to it) instead of inheriting whatever this test process's
    /// own stdin happens to be. It is what lets a test drive the real
    /// <c>[Console]::IsInputRedirected</c> check in <c>webinstall.ps1</c> deterministically, rather than
    /// depending on how the test runner itself was launched.
    /// </para>
    /// </summary>
    public static (string? Stdout, string Stderr) Run(string command, TimeSpan timeout, bool redirectStandardInput = false, string? exeName = null)
    {
        if ((exeName ?? ExeName) is not { } exe)
        {
            return (null, "no PowerShell interpreter was resolved");
        }

        var encodedCommand = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command));
        var info = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectStandardInput,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-ExecutionPolicy");
        info.ArgumentList.Add("Bypass");
        info.ArgumentList.Add("-EncodedCommand");
        info.ArgumentList.Add(encodedCommand);

        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                return (null, "the process did not start");
            }

            if (redirectStandardInput)
            {
                // A pipe stdin that nothing ever reads would otherwise block a child that genuinely
                // tried to read it; closing it immediately both makes [Console]::IsInputRedirected
                // true in the child AND turns any accidental read into an immediate EOF rather than a
                // hang, which is a second safety net on top of the fix this proves.
                process.StandardInput.Close();
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SystemException)
                {
                }

                return (null, $"timed out after {timeout}");
            }

            return (stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return (null, ex.Message);
        }
    }
}

/// <summary>A fact that runs only when a PowerShell interpreter is available to drive.</summary>
internal sealed class RealPowerShellFactAttribute : FactAttribute
{
    public RealPowerShellFactAttribute() => Skip = RealPowerShell.SkipReason;
}
