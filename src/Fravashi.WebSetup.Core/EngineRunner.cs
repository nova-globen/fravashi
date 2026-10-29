using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Fravashi.WebSetup;

/// <summary>How the engine's process ended, before anybody has interpreted it.</summary>
/// <param name="ExitCode">The process exit code, or null when it never started or was cancelled.</param>
/// <param name="StartError">Why the process could not be started, or null.</param>
/// <param name="Cancelled">Whether the run was cancelled (the process tree was killed).</param>
public sealed record EngineExit(int? ExitCode, string? StartError, bool Cancelled);

/// <summary>
/// Runs an <see cref="EngineCommand"/> with every stream redirected and reports its output as it arrives.
/// <para>
/// stdin is a pipe CLOSED at once. That is load-bearing: the engine asks <c>[Console]::IsInputRedirected</c>
/// before every prompt (backlog 276), so with a closed pipe it never waits at <c>Read-Host</c> for a person who
/// cannot see a console, and says what it needs instead; an accidental read would see end of file, not hang.
/// </para>
/// <para>
/// stdout is read as lines, stderr as fragments split on carriage returns and line feeds, because curl redraws
/// its progress bar with a carriage return and never ends that line until the file is done.
/// </para>
/// </summary>
public static class EngineRunner
{
    public static async Task<EngineExit> RunAsync(
        EngineCommand command,
        Encoding outputEncoding,
        Action<string> onOutputLine,
        Action<string> onErrorFragment,
        CancellationToken cancellationToken)
    {
        var info = StartInfo(command, outputEncoding);

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("the process did not start");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return new EngineExit(null, $"{command.FileName} could not be started: {ex.Message}", false);
        }

        using (process)
        {
            process.StandardInput.Close();

            var stdout = PumpLinesAsync(process.StandardOutput, onOutputLine);
            var stderr = PumpFragmentsAsync(process.StandardError, onErrorFragment);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                return new EngineExit(null, null, true);
            }

            // WaitForExitAsync returns when the process ends; the pipes may still hold the last lines, and the
            // last line is the engine's reason when it failed, so both are drained before the verdict.
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return new EngineExit(process.ExitCode, null, false);
        }
    }

    /// <summary>
    /// The engine's start info: every stream redirected, the arguments as a list, and the parent's environment
    /// WITHOUT <c>PSModulePath</c>. Started from a PowerShell 7 session the EXE inherits PowerShell 7's module path,
    /// under which Windows PowerShell 5.1 cannot load its own Microsoft.PowerShell.Utility, so the engine failed
    /// at <c>Get-FileHash</c> (measured by the host on websetup-v2.0.0, backlog 296). PowerShell 7 repairs the
    /// variable only for a powershell.exe it starts itself; with it absent, 5.1 computes its own default. Nothing
    /// else is removed, on purpose: only the variable measured to break 5.1 is withheld, no other PowerShell 7
    /// variable is known to be read by 5.1, and a process-scope <c>PSExecutionPolicyPreference</c> is overridden by
    /// the engine's own <c>-ExecutionPolicy Bypass</c>. The rest (PATH, TEMP, curl's proxy variables) is the
    /// person's and the engine needs it.
    /// </summary>
    internal static ProcessStartInfo StartInfo(EngineCommand command, Encoding outputEncoding)
    {
        var info = new ProcessStartInfo(command.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = outputEncoding,
            StandardErrorEncoding = outputEncoding,
        };
        foreach (var argument in command.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        // The environment block is case-insensitive on Windows and not on Linux, so every spelling goes.
        foreach (var name in info.Environment.Keys.Where(k => string.Equals(k, "PSModulePath", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            info.Environment.Remove(name);
        }

        return info;
    }

    private static async Task PumpLinesAsync(StreamReader reader, Action<string> onLine)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            onLine(line);
        }
    }

    private static async Task PumpFragmentsAsync(StreamReader reader, Action<string> onFragment)
    {
        var buffer = new char[4096];
        var pending = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (c is '\r' or '\n')
                {
                    if (pending.Length > 0)
                    {
                        onFragment(pending.ToString());
                        pending.Clear();
                    }
                }
                else
                {
                    pending.Append(c);
                }
            }
        }

        if (pending.Length > 0)
        {
            onFragment(pending.ToString());
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            // The whole tree: the engine's curl or the release's installer would otherwise carry on alone.
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }
}
