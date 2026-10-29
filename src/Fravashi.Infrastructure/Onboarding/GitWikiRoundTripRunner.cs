using System.Diagnostics;
using System.Globalization;
using System.Text;
using Fravashi.Domain;

namespace Fravashi.Infrastructure.Onboarding;

/// <summary>
/// Proves write and delete access to a GitHub wiki the only way GitHub offers - GitHub wikis have <b>no REST
/// content API</b>; the wiki <i>is</i> a git repository. This runs a real, user-consented round-trip:
/// shallow-clone the wiki repo to a throwaway temp directory, add a clearly-labelled probe page, commit and
/// push (<b>Write</b> proven), then remove the page, commit and push (<b>Delete</b> proven). It mirrors the
/// Confluence/ADO-wiki consented probes, extended to git.
/// <para>
/// <b>Credential hygiene.</b> The token is passed to git via <c>http.extraHeader</c> supplied through the
/// <c>GIT_CONFIG_COUNT/KEY/VALUE</c> environment variables - <i>never</i> embedded in the remote URL (which
/// git would persist into <c>.git/config</c>) and never on the command line. Runtime config from those env
/// vars is not written to disk, so no token ever lands in the clone. The runner re-reads the clone's
/// <c>.git/config</c> after cloning and reports <see cref="GitWikiRoundTripResult.TokenFoundOnDisk"/> as a
/// belt-and-braces check.
/// </para>
/// <para>
/// <b>Identity.</b> Author and committer are set explicitly via the <c>GIT_AUTHOR_*</c>/<c>GIT_COMMITTER_*</c>
/// env vars ("Fravashi probe &lt;probe@fravashi.local&gt;"), so the probe commits do not depend on any global
/// git identity - the container/host may have none.
/// </para>
/// <para>
/// <b>Honesty.</b> git history is append-only: the delete pushes a removal commit, but both probe commits stay
/// in the wiki's history. The caller reports that plainly. The temp directory is always removed in a finally.
/// </para>
/// </summary>
public class GitWikiRoundTripRunner
{
    private const string AuthorName = $"{ProductName.Short} probe";
    private const string AuthorEmail = "probe@fravashi.local";

    private const string ProbeBody =
        $"# {ProductName.Short} access probe\n\n" +
        $"This page was created automatically by {ProductName.Short} to verify write access to this wiki during " +
        $"onboarding. It is safe to delete - {ProductName.Short} removes it automatically as part of the same test.\n";

    /// <summary>The result of the round-trip. Each stage reports what actually happened, honestly.</summary>
    /// <param name="Cloned">The shallow clone succeeded (the wiki repo exists and was readable).</param>
    /// <param name="CloneDetail">Failure detail when <paramref name="Cloned"/> is false.</param>
    /// <param name="WriteProven">The probe page was committed and pushed - a real write.</param>
    /// <param name="WriteDetail">Failure detail when <paramref name="WriteProven"/> is false.</param>
    /// <param name="DeleteProven">The probe page's removal was committed and pushed - a real delete.</param>
    /// <param name="DeleteDetail">Failure detail when <paramref name="DeleteProven"/> is false.</param>
    /// <param name="ProbeFileName">The probe page's file name (e.g. <c>Fravashi-access-probe-20260716T120000Z.md</c>).</param>
    /// <param name="TokenFoundOnDisk">True only if a token was found in the clone's <c>.git/config</c> - always expected false.</param>
    public sealed record GitWikiRoundTripResult(
        bool Cloned,
        string? CloneDetail,
        bool WriteProven,
        string? WriteDetail,
        bool DeleteProven,
        string? DeleteDetail,
        string ProbeFileName,
        bool TokenFoundOnDisk);

    /// <summary>
    /// Runs the clone → add → commit → push → remove → commit → push round-trip against
    /// <paramref name="wikiGitUrl"/>. When <paramref name="token"/> is null/empty the remote is treated as
    /// needing no auth (used by the local bare-repo tests over <c>file://</c>).
    /// </summary>
    public virtual async Task<GitWikiRoundTripResult> RunAsync(string wikiGitUrl, string? token, CancellationToken cancellationToken = default)
    {
        var probeFileName = $"Fravashi-access-probe-{DateTime.UtcNow.ToString("yyyyMMddTHHmmss", CultureInfo.InvariantCulture)}Z.md";
        var env = BuildEnvironment(token);
        var tempDir = Directory.CreateTempSubdirectory("fravashi-wiki-probe-");
        var cloneDir = Path.Combine(tempDir.FullName, "wiki");

        try
        {
            // 1. Shallow-clone the wiki repo. The token (if any) travels via http.extraHeader in env config.
            var clone = await RunGitAsync(tempDir.FullName, env, cancellationToken,
                "clone", "--depth", "1", wikiGitUrl, cloneDir);
            if (clone.ExitCode != 0)
            {
                return new GitWikiRoundTripResult(false, clone.Error, false, null, false, null, probeFileName, false);
            }

            // 2. Prove the token never landed on disk: re-read the clone's config.
            var tokenOnDisk = ConfigContainsToken(Path.Combine(cloneDir, ".git", "config"), token);

            // 3. Write: add the probe page, commit, push.
            await File.WriteAllTextAsync(Path.Combine(cloneDir, probeFileName), ProbeBody, cancellationToken);
            var add = await RunGitAsync(cloneDir, env, cancellationToken, "add", probeFileName);
            if (add.ExitCode != 0)
            {
                return new GitWikiRoundTripResult(true, null, false, add.Error, false, "Not attempted - staging the probe page failed.", probeFileName, tokenOnDisk);
            }

            var commit = await RunGitAsync(cloneDir, env, cancellationToken,
                "commit", "-m", $"{ProductName.Short} onboarding probe: verify write access (auto-removed)");
            if (commit.ExitCode != 0)
            {
                return new GitWikiRoundTripResult(true, null, false, commit.Error, false, "Not attempted - the probe commit failed.", probeFileName, tokenOnDisk);
            }

            var writePush = await RunGitAsync(cloneDir, env, cancellationToken, "push", "origin", "HEAD");
            if (writePush.ExitCode != 0)
            {
                return new GitWikiRoundTripResult(true, null, false, writePush.Error, false, "Not attempted - the write push failed, so nothing was left to delete.", probeFileName, tokenOnDisk);
            }

            // 4. Delete: remove the probe page, commit, push.
            var rm = await RunGitAsync(cloneDir, env, cancellationToken, "rm", probeFileName);
            if (rm.ExitCode != 0)
            {
                return new GitWikiRoundTripResult(true, null, true, null, false, rm.Error, probeFileName, tokenOnDisk);
            }

            var deleteCommit = await RunGitAsync(cloneDir, env, cancellationToken,
                "commit", "-m", $"{ProductName.Short} onboarding probe: remove probe page");
            if (deleteCommit.ExitCode != 0)
            {
                return new GitWikiRoundTripResult(true, null, true, null, false, deleteCommit.Error, probeFileName, tokenOnDisk);
            }

            var deletePush = await RunGitAsync(cloneDir, env, cancellationToken, "push", "origin", "HEAD");
            return deletePush.ExitCode == 0
                ? new GitWikiRoundTripResult(true, null, true, null, true, null, probeFileName, tokenOnDisk)
                : new GitWikiRoundTripResult(true, null, true, null, false, deletePush.Error, probeFileName, tokenOnDisk);
        }
        finally
        {
            TryDeleteDirectory(tempDir.FullName);
        }
    }

    private static Dictionary<string, string> BuildEnvironment(string? token)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GCM_INTERACTIVE"] = "never",
            ["GIT_ASKPASS"] = "echo",
            // Explicit identity so the probe commits never depend on a global git config (the container has none).
            ["GIT_AUTHOR_NAME"] = AuthorName,
            ["GIT_AUTHOR_EMAIL"] = AuthorEmail,
            ["GIT_COMMITTER_NAME"] = AuthorName,
            ["GIT_COMMITTER_EMAIL"] = AuthorEmail,
        };

        if (!string.IsNullOrWhiteSpace(token))
        {
            // Pass the credential as an HTTP Authorization header via runtime config env vars - NOT in the URL
            // (git would write that into .git/config) and NOT on the command line. Runtime config is never
            // persisted to disk.
            var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"x-access-token:{token}"));
            env["GIT_CONFIG_COUNT"] = "1";
            env["GIT_CONFIG_KEY_0"] = "http.extraHeader";
            env["GIT_CONFIG_VALUE_0"] = $"AUTHORIZATION: basic {basic}";
        }

        return env;
    }

    private static bool ConfigContainsToken(string configPath, string? token)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                return false;
            }

            var text = File.ReadAllText(configPath);
            if (!string.IsNullOrWhiteSpace(token) && text.Contains(token, StringComparison.Ordinal))
            {
                return true;
            }

            // The header key would only appear if runtime config had leaked into the persisted config.
            return text.Contains("extraheader", StringComparison.OrdinalIgnoreCase)
                || text.Contains("AUTHORIZATION", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private readonly record struct GitRun(int ExitCode, string Output, string Error);

    private static async Task<GitRun> RunGitAsync(string workingDirectory, IReadOnlyDictionary<string, string> env, CancellationToken cancellationToken, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in env)
        {
            psi.Environment[key] = value;
        }

        try
        {
            using var process = new Process { StartInfo = psi };
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            return new GitRun(process.ExitCode, output, Redact(error));
        }
        catch (Exception ex)
        {
            return new GitRun(-1, string.Empty, GitLsRemoteRunner.DescribeStartFailure(ex));
        }
    }

    /// <summary>Strips any embedded credentials git might echo inside a URL in an error line.</summary>
    private static string Redact(string text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        var scheme = trimmed.IndexOf("://", StringComparison.Ordinal);
        var at = trimmed.IndexOf('@');
        if (scheme >= 0 && at > scheme)
        {
            trimmed = string.Concat(trimmed.AsSpan(0, scheme + 3), "***@", trimmed.AsSpan(at + 1));
        }

        return trimmed.Length > 500 ? trimmed[..500] : trimmed;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            // git marks pack files read-only on Windows; clear the attribute before deleting.
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch (Exception)
                {
                    // best effort
                }
            }

            Directory.Delete(path, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temp directory is harmless; never fail the probe over cleanup.
        }
    }
}
