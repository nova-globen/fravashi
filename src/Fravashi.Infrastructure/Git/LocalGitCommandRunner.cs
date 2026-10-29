using System.Diagnostics;
using Fravashi.Application.Git;
using Fravashi.Infrastructure.Repository;
using Microsoft.Extensions.Options;

namespace Fravashi.Infrastructure.Git;

/// <summary>
/// The local <see cref="IGitCommandRunner"/>: spawns <c>git</c> in <c>Repository:Root</c> exactly
/// as the git services always did (ArgumentList, no shell, per-command environment). No fallback
/// root, matching LocalRepositoryReader: unconfigured means unavailable, never the app's own
/// working directory.
/// </summary>
public sealed class LocalGitCommandRunner : IGitCommandRunner
{
    private readonly string _root;
    private bool? _available;

    public LocalGitCommandRunner(IOptions<RepositoryReaderOptions> options)
    {
        var configured = options.Value.Root;
        _root = string.IsNullOrWhiteSpace(configured) ? string.Empty : Path.GetFullPath(configured);
    }

    public bool IsAvailable()
    {
        if (_root.Length == 0)
        {
            return false;
        }

        if (_available is { } cached)
        {
            return cached;
        }

        // Probe synchronously (no async continuation): this is read during render on Blazor
        // Server's single-threaded circuit, where blocking on an async call whose continuation
        // recaptures that context would deadlock the page. A plain Process avoids that entirely.
        try
        {
            using var process = new Process { StartInfo = StartInfo(["rev-parse", "--is-inside-work-tree"]) };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            process.WaitForExit();
            _available = process.ExitCode == 0 && output.Trim() == "true";
        }
        catch
        {
            _available = false;
        }

        return _available.Value;
    }

    public async Task<GitCommandResult> RunAsync(
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken)
    {
        var psi = StartInfo(args);
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                psi.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        // ConfigureAwait(false): never resume on a captured (e.g. Blazor circuit) context: defends any
        // sync-over-async caller from deadlocking and avoids needless context hops.
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new GitCommandResult(
            process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private ProcessStartInfo StartInfo(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        return psi;
    }
}
