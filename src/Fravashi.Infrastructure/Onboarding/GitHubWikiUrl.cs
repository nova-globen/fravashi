namespace Fravashi.Infrastructure.Onboarding;

/// <summary>
/// The three URL forms a GitHub wiki target is described by, derived from whatever the user typed. A GitHub
/// wiki is itself a git repository at <c>&lt;repo&gt;.wiki.git</c>, so the probe must clone/ls-remote
/// <see cref="WikiGitUrl"/>; <see cref="WikiWebUrl"/> is the browser URL used to name a leftover probe page.
/// </summary>
/// <param name="RepoWebUrl">The repository's canonical web URL, e.g. <c>https://github.com/owner/repo</c>.</param>
/// <param name="WikiGitUrl">The wiki's git URL, e.g. <c>https://github.com/owner/repo.wiki.git</c>.</param>
/// <param name="WikiWebUrl">The wiki's browser URL, e.g. <c>https://github.com/owner/repo/wiki</c>.</param>
public sealed record GitHubWikiTarget(string RepoWebUrl, string WikiGitUrl, string WikiWebUrl);

/// <summary>
/// Normalizes the many shapes a user can paste for a GitHub wiki into the one form the probe needs. The old
/// probe blindly appended <c>.wiki.git</c>, so <c>…/agent/wiki</c> became <c>agent/wiki.wiki.git</c> and
/// <c>…/agent.wiki.git</c> became <c>agent.wiki.wiki.git</c> - both 404. This accepts, and reduces to the same
/// <c>&lt;repo&gt;.wiki.git</c>:
/// <list type="bullet">
///   <item>a plain repo URL - <c>https://github.com/owner/repo</c></item>
///   <item>a repo git URL - <c>https://github.com/owner/repo.git</c></item>
///   <item>a wiki web URL - <c>https://github.com/owner/repo/wiki</c> (or <c>…/wiki/SomePage</c>)</item>
///   <item>a wiki git URL - <c>https://github.com/owner/repo.wiki.git</c></item>
/// </list>
/// Trailing slashes and <c>.git</c>/<c>.wiki.git</c> case are tolerated. Host-agnostic, so GitHub Enterprise
/// hosts work too.
/// </summary>
public static class GitHubWikiUrl
{
    /// <summary>
    /// Reduces any accepted input to a <see cref="GitHubWikiTarget"/>. Throws <see cref="FormatException"/>
    /// when no <c>owner/repo</c> can be read from the input, so the caller can report an input-validation
    /// failure rather than probing a mangled URL.
    /// </summary>
    public static GitHubWikiTarget Normalize(string input)
    {
        var s = (input ?? string.Empty).Trim().TrimEnd('/');
        if (s.Length == 0)
        {
            throw new FormatException("No repository URL was provided.");
        }

        // Preferred path: parse as an absolute URL and read owner/repo from the path segments. This handles a
        // /wiki (or /wiki/<page>) suffix as a real path segment rather than as text, so a repo literally named
        // "wiki" is never mistaken for the wiki suffix.
        if (Uri.TryCreate(s, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2)
            {
                var owner = segments[0];
                var repo = StripRepoSuffixes(segments[1]);
                if (owner.Length > 0 && repo.Length > 0)
                {
                    return Build($"{uri.Scheme}://{uri.Authority}/{owner}/{repo}");
                }
            }

            throw new FormatException($"Could not read an owner/repository from '{s}'.");
        }

        // Fallback for inputs that are not absolute URLs (e.g. a bare host/owner/repo). Strip the wiki/git
        // suffixes textually, then require at least an owner/repo shape.
        var baseStr = s;
        if (baseStr.EndsWith(".wiki.git", StringComparison.OrdinalIgnoreCase))
        {
            baseStr = baseStr[..^".wiki.git".Length];
        }
        else if (baseStr.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            baseStr = baseStr[..^".git".Length];
        }
        else if (baseStr.EndsWith("/wiki", StringComparison.OrdinalIgnoreCase))
        {
            baseStr = baseStr[..^"/wiki".Length];
        }

        baseStr = baseStr.TrimEnd('/');
        if (baseStr.Length == 0 || baseStr.Trim('/').Split('/').Length < 2)
        {
            throw new FormatException($"Could not read an owner/repository from '{s}'.");
        }

        return Build(baseStr);
    }

    private static string StripRepoSuffixes(string repoSegment)
    {
        if (repoSegment.EndsWith(".wiki.git", StringComparison.OrdinalIgnoreCase))
        {
            return repoSegment[..^".wiki.git".Length];
        }

        if (repoSegment.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            return repoSegment[..^".git".Length];
        }

        return repoSegment;
    }

    private static GitHubWikiTarget Build(string repoWebUrl)
    {
        var trimmed = repoWebUrl.TrimEnd('/');
        return new GitHubWikiTarget(trimmed, $"{trimmed}.wiki.git", $"{trimmed}/wiki");
    }
}
