using Fravashi.Domain.PullRequests;

namespace Fravashi.Infrastructure.PullRequests;

/// <summary>
/// The provider and identifiers parsed from a git <c>origin</c> URL. <see cref="Host"/> is the
/// lowercase host name for EVERY successful parse (plan 2c): it keys the per-host credential
/// lookup. <see cref="Provider"/> is only what the URL alone proves (github.com, dev.azure.com,
/// *.visualstudio.com); an unrecognized host parses as
/// <c>Provider == None</c> WITH coordinates, and the git_hosts registry decides its provider.
/// </summary>
public sealed record RemoteInfo(PullRequestProvider Provider, string Owner, string Project, string Repo, string Host = "")
{
    public static readonly RemoteInfo None = new(PullRequestProvider.None, string.Empty, string.Empty, string.Empty);

    /// <summary>Parse an origin URL (HTTPS or SSH) into host + provider + owner/org, project, repo.</summary>
    public static RemoteInfo Parse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return None;
        }

        var trimmed = url.Trim();
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        // Normalize scp-style SSH (git@host:path) to host/path for uniform splitting.
        var normalized = trimmed
            .Replace("git@", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("ssh://", string.Empty, StringComparison.OrdinalIgnoreCase);

        // Strip a leading "user@" still present in some https forms (e.g. org@dev.azure.com).
        var atHost = normalized.IndexOf('@');
        var firstSlash = normalized.IndexOf('/');
        if (atHost >= 0 && (firstSlash < 0 || atHost < firstSlash))
        {
            normalized = normalized[(atHost + 1)..];
        }

        normalized = normalized.Replace(':', '/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return None;
        }

        var host = parts[0].ToLowerInvariant();
        var rest = parts[1..];

        if (host.Contains("github.com", StringComparison.Ordinal) && rest.Length >= 2)
        {
            return new RemoteInfo(PullRequestProvider.GitHub, rest[0], string.Empty, rest[1], host);
        }

        if (host.Contains("dev.azure.com", StringComparison.Ordinal))
        {
            // dev.azure.com/{org}/{project}/_git/{repo}  (SSH: v3/{org}/{project}/{repo})
            var segs = rest[0] == "v3" ? rest[1..] : rest;
            segs = segs.Where(s => s != "_git").ToArray();
            if (segs.Length >= 3)
            {
                return new RemoteInfo(PullRequestProvider.AzureDevOps, segs[0], segs[1], segs[2], host);
            }
        }

        if (host.EndsWith("visualstudio.com", StringComparison.Ordinal))
        {
            // {org}.visualstudio.com/{project}/_git/{repo}
            var org = host.Replace(".visualstudio.com", string.Empty, StringComparison.Ordinal);
            var segs = rest.Where(s => s != "_git").ToArray();

            // The legacy Services SSH remote is the same shape as ssh.dev.azure.com's v3 form and must be
            // parsed the same way. Microsoft documents it on the BARE host,
            // {org}@vs-ssh.visualstudio.com:v3/{org}/{project}/{repo}, and it has also been seen as
            // vs-ssh.{org}.visualstudio.com; neither host names the org reliably, but the path repeats it
            // after the v3 segment in both, so the org is taken from the PATH. Reading either as the web
            // form yielded Owner=vs-ssh[.org], Project=v3, Repo={org}, which still passed the Services
            // guard and then aimed the API at a dev.azure.com URL that 404s, reported as "renamed, moved
            // or deleted" (backlog 76).
            if (segs.Length >= 4 && segs[0].Equals("v3", StringComparison.Ordinal))
            {
                return new RemoteInfo(PullRequestProvider.AzureDevOps, segs[1], segs[2], segs[3], host);
            }

            if (segs.Length >= 2)
            {
                return new RemoteInfo(PullRequestProvider.AzureDevOps, org, segs[0], segs[1], host);
            }
        }

        if (rest.Length >= 2)
        {
            // Unknown host shaped like {host}/{owner}/{repo}: keep the coordinates, let the
            // git_hosts registry (or the transitional legacy flag) decide the provider.
            return new RemoteInfo(PullRequestProvider.None, rest[0], string.Empty, rest[1], host);
        }

        return None;
    }

    /// <summary>
    /// Transitional overload (plan 2c Task 2->4 bridge): the legacy call sites vouched for
    /// Forgejo via the onboarding source-provider flag. Removed once GitHostAccess owns this.
    /// </summary>
    public static RemoteInfo Parse(string? url, bool treatUnknownAsForgejo)
    {
        var info = Parse(url);
        return treatUnknownAsForgejo && info.Provider == PullRequestProvider.None && info.Host.Length > 0
            ? info with { Provider = PullRequestProvider.Forgejo }
            : info;
    }
}
