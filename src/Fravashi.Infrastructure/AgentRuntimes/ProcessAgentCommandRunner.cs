using System.Diagnostics;
using System.Text;
using Fravashi.Application.AgentRuntimes;

namespace Fravashi.Infrastructure.AgentRuntimes;

/// <summary>
/// Runs an <see cref="AgentCommand"/> as a real child process, capturing stdout/stderr with a hard timeout.
/// A start failure (the CLI is not installed → <see cref="System.ComponentModel.Win32Exception"/>) is returned
/// as <see cref="AgentCommandResult.NotStarted"/> rather than thrown, so callers treat "not installed" as a
/// value, not an exception.
/// <para>
/// The child's environment is the allowlist in <see cref="AgentProcessEnvironment"/>, not the studio's
/// own (backlog 72); the command's own <c>Environment</c> is applied on top and still wins.
/// </para>
/// </summary>
public sealed class ProcessAgentCommandRunner : IAgentCommandRunner
{
    public async Task<AgentCommandResult> RunAsync(AgentCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = command.StdIn is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Backlog 72: FIRST, before anything is added on purpose. This runner starts agent CLIs and npm,
        // both of which are inside the agent trust boundary, so neither may inherit the studio's own
        // environment and the live host-bridge credential in it.
        AgentProcessEnvironment.Restrict(startInfo);

        // Plan 2d: an agent runs INSIDE its synced workspace copy; absent = inherit the app's cwd.
        if (!string.IsNullOrWhiteSpace(command.WorkingDirectory))
        {
            startInfo.WorkingDirectory = command.WorkingDirectory;
        }

        foreach (var arg in command.Arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (command.Environment is not null)
        {
            foreach (var (key, value) in command.Environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            return AgentCommandResult.NotStarted(ex.Message);
        }

        if (process is null)
        {
            return AgentCommandResult.NotStarted("The process did not start.");
        }

        using (process)
        {
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var stdoutTask = ReadAllAsync(process.StandardOutput, stdout);
            var stderrTask = ReadAllAsync(process.StandardError, stderr);

            if (command.StdIn is not null)
            {
                try
                {
                    await process.StandardInput.WriteAsync(command.StdIn);
                    process.StandardInput.Close();
                }
                catch
                {
                    // A closed stdin surfaces via the exit code.
                }
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(command.Timeout);

            var timedOut = false;
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = !cancellationToken.IsCancellationRequested;
                TryKill(process);
                cancellationToken.ThrowIfCancellationRequested();
            }

            // Let the readers drain whatever is buffered even after a kill.
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch
            {
                // Best-effort drain.
            }

            var exitCode = TryExitCode(process);
            return new AgentCommandResult(
                Started: true,
                ExitCode: timedOut ? null : exitCode,
                StandardOutput: stdout.ToString(),
                StandardError: stderr.ToString(),
                TimedOut: timedOut);
        }
    }

    private static async Task ReadAllAsync(StreamReader reader, StringBuilder sink)
    {
        try
        {
            var buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer)) > 0)
            {
                sink.Append(buffer, 0, read);
            }
        }
        catch
        {
            // A killed process closes the pipe; whatever was captured is enough.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Already gone.
        }
    }

    private static int? TryExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch
        {
            return null;
        }
    }
}
