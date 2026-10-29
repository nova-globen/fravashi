using System.Diagnostics;
using Fravashi.Application.Git;

namespace Fravashi.Infrastructure.Git;

/// <summary>
/// The real <see cref="IGitProcessRunner"/>: shells out to <c>git</c>, injecting the supplied environment
/// (including the <c>GIT_CONFIG_*</c> credential vars) and streaming each stderr line to
/// <c>onStderrLine</c> so <c>--progress</c> can drive a live progress bar. Mirrors the process handling used by
/// the onboarding wiki round-trip probe - no shell, arguments via <see cref="ProcessStartInfo.ArgumentList"/>,
/// prompts hard-disabled by the caller's environment.
/// </summary>
public sealed class GitProcessRunner : IGitProcessRunner
{
    public async Task<GitProcessResult> RunAsync(
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        Action<string>? onStderrLine,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in environment)
        {
            startInfo.Environment[key] = value;
        }

        try
        {
            using var process = new Process { StartInfo = startInfo };
            var stderrBuffer = new System.Text.StringBuilder();
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                stderrBuffer.AppendLine(e.Data);
                onStderrLine?.Invoke(e.Data);
            };

            process.Start();
            process.BeginErrorReadLine();
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new GitProcessResult(process.ExitCode, stdout, stderrBuffer.ToString());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return new GitProcessResult(-1, string.Empty,
                "git is not available. Install git or use the packaged container image, which bundles it.");
        }
    }
}
