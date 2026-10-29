using System.Diagnostics;
using System.Text;

namespace Fravashi.Bridge;

/// <summary>The outcome of one git invocation.</summary>
public sealed record BridgeGitResult(int ExitCode, string Stdout, string Stderr)
{
    /// <summary>
    /// The exit code the bridge answers when git could not be started at all (backlog 266): the shell's "command not
    /// found". Together with <see cref="GitMissingMessage"/> starting the error text, it is how the studio tells a host
    /// with no git from git refusing (git's own refusals exit 1, 128 or 129) and from a bridge that does not answer.
    /// </summary>
    public const int GitMissingExitCode = 127;

    /// <summary>How the error text of a git-missing answer starts. The studio's copy of it is held equal by a test.</summary>
    public const string GitMissingMessage = "git is not installed on this machine";
}

/// <summary>
/// Runs git rooted at a registered workspace. The binary is the literal <c>git</c> (host PATH,
/// preflight-required per the 2026-08-01 decision): the bridge never executes an arbitrary
/// program, and the working directory always comes from <see cref="WorkspaceRegistry.ResolvePath"/>.
/// <c>GIT_TERMINAL_PROMPT=0</c> guarantees no invocation ever blocks on a credential prompt; output
/// is capped so a runaway command cannot balloon the host process.
/// <para>
/// <paramref name="executable"/> exists for tests that need git to be missing (backlog 266). Only host code chooses
/// it (<see cref="BridgeServerOptions.GitExecutable"/>); nothing the container sends can name a program.
/// </para>
/// </summary>
public sealed class BridgeGitService(WorkspaceRegistry registry, string executable = "git")
{
    private const int OutputCapBytes = 8 * 1024 * 1024;
    private const string TruncationMarker = "\n[truncated]";

    public static bool IsGitAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10_000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Only transport shapes a remote clone legitimately uses (plan 2c): http(s), ssh, or the
    /// scp-like <c>git@host:path</c> form. Everything option-shaped, local (<c>file://</c>,
    /// drive paths), or helper-based (<c>ext::</c>) is refused at the door.
    /// </summary>
    public static bool IsCloneableUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var value = url.Trim();
        if (value.StartsWith('-'))
        {
            return false;
        }

        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(value, UriKind.Absolute, out _);
        }

        // scp-like user@host:path; a Windows drive path has no '@' before its ':' and ext::
        // style transport helpers carry a double colon.
        var at = value.IndexOf('@');
        var colon = value.IndexOf(':');
        return at > 0 && colon > at && !value.Contains("::", StringComparison.Ordinal);
    }

    /// <summary>The target folder name a clone URL implies: last segment, sanitized.</summary>
    public static string DeriveFolderName(string url)
    {
        var trimmed = url.Trim().TrimEnd('/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        var lastSeparator = Math.Max(
            Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\')), trimmed.LastIndexOf(':'));
        var raw = lastSeparator >= 0 ? trimmed[(lastSeparator + 1)..] : trimmed;
        var sanitized = new string([.. raw.Select(c =>
            char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')]);
        return sanitized.Trim('-', '.');
    }

    /// <summary>
    /// <c>git clone</c> into <paramref name="parentPath"/>/{derived-name} (the parent ALWAYS
    /// comes from the host's own folder picker, never from the container). Returns the created
    /// folder's full path. The URL gate is the endpoint's job; this method also serves tests
    /// cloning from local fixture paths.
    /// </summary>
    public async Task<string> CloneAsync(string url, string parentPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("A clone URL is required.", nameof(url));
        }

        if (!Directory.Exists(parentPath))
        {
            throw new DirectoryNotFoundException($"The target parent folder does not exist: {parentPath}");
        }

        var name = DeriveFolderName(url);
        if (name.Length == 0)
        {
            throw new ArgumentException("The URL has no usable repository name.", nameof(url));
        }

        var target = Path.Combine(parentPath, name);
        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new IOException($"'{name}' already exists in the chosen folder.");
        }

        var result = await RunProcessAsync(parentPath, ["clone", "--", url.Trim(), name], cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var reason = result.Stderr.Trim();
            throw new InvalidOperationException(reason.Length > 0 ? reason : $"git clone exited with {result.ExitCode}.");
        }

        return target;
    }

    public async Task<BridgeGitResult> RunAsync(
        string id, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count == 0)
        {
            throw new ArgumentException("git needs at least one argument.", nameof(args));
        }

        return await RunProcessAsync(registry.ResolvePath(id, null), args, cancellationToken).ConfigureAwait(false);
    }

    private async Task<BridgeGitResult> RunProcessAsync(
        string workingDirectory, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        // Never block on an interactive prompt; a missing credential is an honest git failure.
        startInfo.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("git failed to start.");
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // No git on the host's PATH (backlog 266). This used to escape as a 500, which the studio read as a bridge
            // it could not reach, so no agent copy could start on a machine without git. It is an answer, not an outage.
            return new BridgeGitResult(
                BridgeGitResult.GitMissingExitCode,
                string.Empty,
                $"{BridgeGitResult.GitMissingMessage}, or it could not be started: {ex.Message}");
        }

        var stdoutTask = ReadCappedAsync(process.StandardOutput, cancellationToken);
        var stderrTask = ReadCappedAsync(process.StandardError, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        return new BridgeGitResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static async Task<string> ReadCappedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[8192];
        var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (builder.Length < OutputCapBytes)
            {
                builder.Append(buffer, 0, Math.Min(read, OutputCapBytes - builder.Length));
            }
            else
            {
                truncated = true;
            }
        }

        if (truncated)
        {
            builder.Append(TruncationMarker);
        }

        return builder.ToString();
    }
}
