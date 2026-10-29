using Fravashi.Application.Git;
using Fravashi.Application.Wiki;
using Fravashi.Infrastructure.Git;
using Fravashi.Infrastructure.Onboarding;
using Fravashi.Infrastructure.Providers;

namespace Fravashi.Infrastructure.Wiki;

/// <summary>
/// The GitHub-wiki read source. A GitHub wiki has no REST content API - it <i>is</i> a git repository at
/// <c>&lt;repo&gt;.wiki.git</c> - so this shallow-clones it (reusing the credential-hygiene
/// <see cref="IGitProcessRunner"/> path: the token travels via <c>http.extraHeader</c>, never on disk) into a
/// throwaway temp directory and reads each <c>.md</c> page off disk. The file name (without extension) is the
/// slug; dashes become spaces for the title. One-way: nothing is pushed back.
/// </summary>
public sealed class GitHubWikiSource(
    string repoUrl,
    string? authorizationHeader,
    IGitProcessRunner git) : IWikiSource
{
    public string Provider => "GitHub wiki";

    public async Task<IReadOnlyList<WikiSourcePage>> FetchAllAsync(
        IProgress<WikiFetchProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var wikiGitUrl = GitHubWikiUrl.Normalize(repoUrl).WikiGitUrl;
        var env = GitAuthHeader.Environment(authorizationHeader);
        var tempDir = Directory.CreateTempSubdirectory("fravashi-wiki-sync-");
        var cloneDir = Path.Combine(tempDir.FullName, "wiki");

        try
        {
            var clone = await git.RunAsync(tempDir.FullName, env, null, cancellationToken,
                "clone", "--depth", "1", wikiGitUrl, cloneDir).ConfigureAwait(false);
            if (clone.ExitCode != 0)
            {
                throw new InvalidOperationException(CloneFailure(wikiGitUrl, clone.StdErr));
            }

            var files = Directory.EnumerateFiles(cloneDir, "*.md", SearchOption.AllDirectories).ToList();
            var pages = new List<WikiSourcePage>(files.Count);
            var fetched = 0;
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var slug = Path.GetFileNameWithoutExtension(file);
                var title = slug.Replace('-', ' ');
                progress?.Report(new WikiFetchProgress(fetched, files.Count, title));
                var body = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                pages.Add(new WikiSourcePage("wiki", title, slug, body, "markdown", null));
                fetched++;
                progress?.Report(new WikiFetchProgress(fetched, files.Count, title));
            }

            return pages;
        }
        finally
        {
            TryDelete(tempDir.FullName);
        }
    }

    /// <summary>
    /// Names the provider and the condition a failed clone actually means (backlog 76). The wiki-sync card
    /// shows this message verbatim, and "Could not clone the GitHub wiki repository. fatal: ..." read like a
    /// Fravashi failure for the two cases that are not: a token that expired or was revoked, and a wiki whose
    /// git repository does not exist until its first page is created.
    /// </summary>
    private static string CloneFailure(string wikiGitUrl, string stderr) => GitRemoteFailure.Classify(stderr) switch
    {
        GitRemoteFailureKind.BadCredential =>
            "GitHub rejected the credential for the wiki repository: the stored token is expired, revoked, or no "
            + $"longer grants access to this repository. Re-enter it and re-run the sync. (Tried {wikiGitUrl}.) "
            + LastLine(stderr),
        GitRemoteFailureKind.NotFound =>
            "GitHub has no wiki repository here: a GitHub wiki's git repository exists only once its first page "
            + "is created, and a renamed or deleted repository looks the same. Open the repository's Wiki tab, "
            + $"create one page, then re-run the sync. (Tried {wikiGitUrl}.) " + LastLine(stderr),
        _ => $"Could not clone the GitHub wiki repository {wikiGitUrl}. {LastLine(stderr)}",
    };

    private static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? string.Empty;

    private static void TryDelete(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); }
                catch (Exception) { /* best effort */ }
            }

            Directory.Delete(path, recursive: true);
        }
        catch (Exception)
        {
            // best effort - a leftover temp dir is harmless
        }
    }
}
