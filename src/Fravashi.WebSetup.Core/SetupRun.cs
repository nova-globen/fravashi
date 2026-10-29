using System.Text;
using Fravashi.Domain;

namespace Fravashi.WebSetup;

/// <summary>Where one install runs: the machine facts the shell supplies, so the run itself is testable anywhere.</summary>
/// <param name="PowerShell">The program that runs the engine (<see cref="EngineCommand.WindowsPowerShell"/> on Windows).</param>
/// <param name="TempRoot">Where the private engine folder is made.</param>
/// <param name="OutputEncoding">How the child's console encodes what it prints.</param>
public sealed record SetupHost(string PowerShell, string TempRoot, Encoding OutputEncoding);

/// <summary>
/// One install from start to verdict, shared by the wizard and <c>/quiet</c> so the two cannot differ: write
/// the engine out, run it, read its output, log everything, decide, clean up.
/// </summary>
public static class SetupRun
{
    /// <param name="onLine">Each line for a window or a console. Called on a pool thread.</param>
    public static async Task<SetupVerdict> RunAsync(
        SetupOptions options,
        SetupHost host,
        SetupLog log,
        EngineProgress progress,
        Action<string> onLine,
        CancellationToken cancellationToken)
    {
        EngineWorkspace workspace;
        try
        {
            workspace = EngineWorkspace.Create(host.TempRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // A full or locked temp folder must end in a verdict and an exit code, never a crash: a silent run
            // has nobody at the desktop to dismiss a crash dialog.
            var refused = new SetupVerdict(SetupExitCode.EngineDidNotRun, "Setup could not prepare its temporary folder.", ex.Message);
            log.Write($"Verdict: {refused.Headline} {refused.Detail} (exit code {(int)refused.Code})");
            return refused;
        }

        void Line(string line)
        {
            log.Write(line);
            onLine(line);
        }

        EngineExit exit;
        try
        {
            var command = EngineCommand.For(host.PowerShell, workspace.ScriptPath, options);

            log.Write($"{ProductName.Full} setup {typeof(SetupRun).Assembly.GetName().Version}: {options.Describe()}.");
            log.Write("Running: " + command);

            exit = await EngineRunner.RunAsync(
                command,
                host.OutputEncoding,
                line => Line(progress.OnOutputLine(line)),
                fragment =>
                {
                    if (progress.OnErrorFragment(fragment) is { } shown)
                    {
                        Line(shown);
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            // Before or around the engine: what the cleanup could not remove reaches the verdict and the log.
            var failed = new SetupVerdict(SetupExitCode.EngineDidNotRun, "Setup could not run the installer.", ex.Message);
            return Finish(failed);
        }
        catch
        {
            // Not one of the failures above: still clean up and say what remained before it propagates.
            if (workspace.Remove() is { } leftover)
            {
                Line(leftover);
            }

            throw;
        }

        return Finish(SetupVerdict.From(exit, progress));

        SetupVerdict Finish(SetupVerdict verdict)
        {
            // What the cleanup could not remove is said, in the log and in the verdict, never swallowed.
            if (workspace.Remove() is { } leftover)
            {
                Line(leftover);
                verdict = verdict with { Detail = string.IsNullOrEmpty(verdict.Detail) ? leftover : verdict.Detail + " " + leftover };
            }

            log.Write($"Verdict: {verdict.Headline} {verdict.Detail} (exit code {(int)verdict.Code})".TrimEnd());
            return verdict;
        }
    }
}
