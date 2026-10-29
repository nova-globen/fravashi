using System.ComponentModel;
using System.Diagnostics;

namespace Fravashi.Infrastructure.Onboarding;

/// <summary>
/// Runs <c>git ls-remote</c> against a remote as a read-only proof of clone access. Credentials are injected
/// into the URL and never logged; interactive credential prompts are hard-disabled
/// (<c>GIT_TERMINAL_PROMPT=0</c>, <c>GCM_INTERACTIVE=never</c>) so a bad token fails fast instead of hanging
/// the onboarding request waiting for console input.
/// </summary>
public class GitLsRemoteRunner
{
    public sealed record LsRemoteResult(bool Success, int ExitCode, string Error);

    /// <summary>
    /// Attempts <c>git ls-remote --heads &lt;authenticatedUrl&gt;</c>. Returns success when git exits 0. The
    /// caller builds <paramref name="authenticatedUrl"/> with any token embedded; this method only runs it.
    /// </summary>
    public virtual Task<LsRemoteResult> LsRemoteAsync(string authenticatedUrl, CancellationToken cancellationToken = default) =>
        LsRemoteAsync(authenticatedUrl, bearerToken: null, cancellationToken);

    /// <summary>
    /// Runs <c>git ls-remote</c>, optionally injecting an <c>Authorization: Bearer</c> header via
    /// <c>-c http.extraHeader</c>. A bearer token is how an Entra (OAuth) access token authenticates to Azure
    /// DevOps git - unlike a PAT, it cannot be embedded in the URL as a password, so the caller passes the plain
    /// URL plus the token here.
    /// </summary>
    public virtual async Task<LsRemoteResult> LsRemoteAsync(string authenticatedUrl, string? bearerToken, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrEmpty(bearerToken))
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add($"http.extraHeader=Authorization: Bearer {bearerToken}");
        }

        psi.ArgumentList.Add("ls-remote");
        psi.ArgumentList.Add("--heads");
        psi.ArgumentList.Add(authenticatedUrl);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";
        psi.Environment["GIT_ASKPASS"] = "echo";

        try
        {
            using var process = new Process { StartInfo = psi };
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            return new LsRemoteResult(process.ExitCode == 0, process.ExitCode, Redact(error));
        }
        catch (Exception ex)
        {
            return new LsRemoteResult(false, -1, DescribeStartFailure(ex));
        }
    }

    /// <summary>
    /// Turns a process-start failure into a message the onboarding wizard can show. A missing <c>git</c>
    /// binary surfaces as a <see cref="Win32Exception"/> ("No such file or directory" / ENOENT); rather than
    /// leaking that raw OS text, report plainly that git is unavailable - the common cause is a runtime
    /// container image that never installed git. Any other failure keeps its original message.
    /// </summary>
    public static string DescribeStartFailure(Exception ex) =>
        ex is Win32Exception
            ? "git is not available in this environment. Install git in the runtime image (or on the host) so read access can be verified."
            : ex.Message;

    /// <summary>
    /// Embeds a token into an https URL for a one-shot probe (x-access-token username: GitHub's
    /// convention, accepted by ADO/Forgejo alike). Lived on the retired source probe before the
    /// wizard shed that step (plan 2c); the wiki probe still authenticates its round trips here.
    /// </summary>
    public static string BuildAuthenticatedUrl(string url, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return url;
        }

        const string https = "https://";
        if (!url.StartsWith(https, StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var rest = url[https.Length..];
        // Strip any existing credentials before the host.
        var slash = rest.IndexOf('/');
        var hostPart = slash < 0 ? rest : rest[..slash];
        var at = hostPart.IndexOf('@');
        if (at >= 0)
        {
            rest = rest[(at + 1)..];
        }

        // GitHub convention uses x-access-token as the username; ADO accepts any username with the PAT.
        return $"{https}x-access-token:{Uri.EscapeDataString(token)}@{rest}";
    }

    /// <summary>Strips a token that git may echo back inside the URL in an error line.</summary>
    private static string Redact(string text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        // git error lines can include the URL with embedded creds ("https://user:token@host/..").
        var at = trimmed.IndexOf('@');
        var scheme = trimmed.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0 && at > scheme)
        {
            trimmed = string.Concat(trimmed.AsSpan(0, scheme + 3), "***@", trimmed.AsSpan(at + 1));
        }

        return trimmed.Length > 500 ? trimmed[..500] : trimmed;
    }
}
