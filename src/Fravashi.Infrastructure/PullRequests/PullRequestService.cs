using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Fravashi.Application.Git;
using Fravashi.Application.Onboarding;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Persistence;
using Fravashi.Application.PullRequests;
using Fravashi.Domain.PullRequests;
using Fravashi.Infrastructure.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Fravashi.Infrastructure.PullRequests;

/// <summary>
/// <see cref="IPullRequestService"/> over the provider REST APIs (plan phase 6, revived from the
/// archived service and extended with Forgejo). Provider and credential come from
/// <see cref="Git.GitHostAccess"/> (plan 2c, decision 8): the workspace's origin remote is parsed
/// for its host, an explicit git_hosts entry wins, built-in cloud detection and the transitional
/// legacy source credential fall behind it. GitHub sends the token as a bearer, Azure DevOps as
/// basic PAT (or bearer for an SSO token), Forgejo as its <c>token</c> scheme.
/// </summary>
public sealed class PullRequestService : IPullRequestService
{
    private const string AdoApi = "api-version=7.1";

    private readonly HttpClient _http;
    private readonly IGitService _git;
    private readonly Git.GitHostAccess _access;
    private (RemoteInfo Info, Git.GitHostResolution Resolution)? _remote;

    public PullRequestService(HttpClient http, IGitService git, Git.GitHostAccess access)
    {
        _http = http;
        _git = git;
        // Resolution reads the stores LIVE (no config-bridge hop), so a Settings-page save takes
        // effect on the next page load.
        _access = access;
    }

    public async Task<PullRequestProvider> GetProviderAsync(CancellationToken cancellationToken = default) =>
        (await RemoteAsync(cancellationToken).ConfigureAwait(false)).Provider;

    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default)
    {
        // A known provider is enough to browse (public repos list anonymously); mutations
        // additionally require the stored token (see RequireConfiguredAsync).
        var info = await RemoteAsync(cancellationToken).ConfigureAwait(false);
        return info.Provider != PullRequestProvider.None;
    }

    public async Task<IReadOnlyList<PullRequest>> ListAsync(PullRequestState state, CancellationToken cancellationToken = default)
    {
        var info = await RemoteAsync(cancellationToken).ConfigureAwait(false);
        if (!await IsConfiguredAsync(cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        return info.Provider switch
        {
            PullRequestProvider.GitHub => await ListGitHubAsync(info, state, cancellationToken).ConfigureAwait(false),
            PullRequestProvider.AzureDevOps => await ListAdoAsync(info, state, cancellationToken).ConfigureAwait(false),
            _ => await ListForgejoAsync(info, state, cancellationToken).ConfigureAwait(false),
        };
    }

    public async Task<PullRequestDetail?> GetAsync(int number, CancellationToken cancellationToken = default)
    {
        var info = await RemoteAsync(cancellationToken).ConfigureAwait(false);
        if (!await IsConfiguredAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return info.Provider switch
        {
            PullRequestProvider.GitHub => await GetGitHubAsync(info, number, cancellationToken).ConfigureAwait(false),
            PullRequestProvider.AzureDevOps => await GetAdoAsync(info, number, cancellationToken).ConfigureAwait(false),
            _ => await GetForgejoAsync(info, number, cancellationToken).ConfigureAwait(false),
        };
    }

    public async Task<PullRequest> CreateAsync(CreatePullRequestRequest request, CancellationToken cancellationToken = default)
    {
        var info = await RequireConfiguredAsync(cancellationToken).ConfigureAwait(false);
        return info.Provider switch
        {
            PullRequestProvider.GitHub => await CreateGitHubAsync(info, request, cancellationToken).ConfigureAwait(false),
            PullRequestProvider.AzureDevOps => await CreateAdoAsync(info, request, cancellationToken).ConfigureAwait(false),
            _ => await CreateForgejoAsync(info, request, cancellationToken).ConfigureAwait(false),
        };
    }

    public async Task MergeAsync(int number, MergeMethod method, CancellationToken cancellationToken = default)
    {
        var info = await RequireConfiguredAsync(cancellationToken).ConfigureAwait(false);
        switch (info.Provider)
        {
            case PullRequestProvider.GitHub:
                await MergeGitHubAsync(info, number, method, cancellationToken).ConfigureAwait(false);
                break;
            case PullRequestProvider.AzureDevOps:
                await MergeAdoAsync(info, number, method, cancellationToken).ConfigureAwait(false);
                break;
            default:
                await MergeForgejoAsync(info, number, method, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    public async Task<IReadOnlyList<PullRequestReview>> GetReviewsAsync(int number, CancellationToken cancellationToken = default)
    {
        var info = await RemoteAsync(cancellationToken).ConfigureAwait(false);
        if (info.Provider == PullRequestProvider.None)
        {
            return [];
        }

        return info.Provider switch
        {
            PullRequestProvider.GitHub => await GetGitHubReviewsAsync(info, number, cancellationToken).ConfigureAwait(false),
            PullRequestProvider.AzureDevOps => await GetAdoReviewsAsync(info, number, cancellationToken).ConfigureAwait(false),
            _ => await GetForgejoReviewsAsync(info, number, cancellationToken).ConfigureAwait(false),
        };
    }

    public async Task SubmitReviewAsync(int number, ReviewVerdict verdict, string body, CancellationToken cancellationToken = default)
    {
        var info = await RequireConfiguredAsync(cancellationToken).ConfigureAwait(false);
        switch (info.Provider)
        {
            case PullRequestProvider.GitHub:
                // GitHub refuses REQUEST_CHANGES with an empty body; the UI enforces it too.
                using (await SendJsonAsync(
                    HttpMethod.Post,
                    $"{GitHubBase(info)}/pulls/{number}/reviews",
                    info,
                    new { @event = verdict switch
                    {
                        ReviewVerdict.Approve => "APPROVE",
                        ReviewVerdict.RequestChanges => "REQUEST_CHANGES",
                        _ => "COMMENT",
                    }, body },
                    cancellationToken).ConfigureAwait(false))
                {
                }

                break;
            case PullRequestProvider.AzureDevOps:
                // Voting needs the caller's reviewer identity; that arrives with the ADO PAT work.
                throw new InvalidOperationException(
                    "Azure DevOps review votes are not wired up yet; they arrive with the ADO PAT work. "
                    + "Merge/abandon already work.");
            default:
                using (await SendJsonAsync(
                    HttpMethod.Post,
                    $"{ForgejoBase(info)}/pulls/{number}/reviews",
                    info,
                    new { @event = verdict switch
                    {
                        ReviewVerdict.Approve => "APPROVED",
                        ReviewVerdict.RequestChanges => "REQUEST_CHANGES",
                        _ => "COMMENT",
                    }, body },
                    cancellationToken).ConfigureAwait(false))
                {
                }

                break;
        }
    }

    public async Task CloseAsync(int number, CancellationToken cancellationToken = default)
    {
        var info = await RequireConfiguredAsync(cancellationToken).ConfigureAwait(false);
        switch (info.Provider)
        {
            case PullRequestProvider.GitHub:
                using (await SendJsonAsync(
                    new HttpMethod("PATCH"),
                    $"{GitHubBase(info)}/pulls/{number}",
                    info, new { state = "closed" }, cancellationToken).ConfigureAwait(false))
                {
                }

                break;
            case PullRequestProvider.AzureDevOps:
                using (await SendJsonAsync(
                    new HttpMethod("PATCH"),
                    $"{AdoBase(info)}/pullrequests/{number}?{AdoApi}",
                    info, new { status = "abandoned" }, cancellationToken).ConfigureAwait(false))
                {
                }

                break;
            default:
                using (await SendJsonAsync(
                    new HttpMethod("PATCH"),
                    $"{ForgejoBase(info)}/pulls/{number}",
                    info, new { state = "closed" }, cancellationToken).ConfigureAwait(false))
                {
                }

                break;
        }
    }

    private async Task<IReadOnlyList<PullRequestReview>> GetGitHubReviewsAsync(RemoteInfo info, int number, CancellationToken ct)
    {
        var url = $"{GitHubBase(info)}/pulls/{number}/reviews?per_page=50";
        using var doc = await SendJsonAsync(HttpMethod.Get, url, info, null, ct).ConfigureAwait(false);
        return RequireArray(doc.RootElement, string.Empty, "GitHub", "the review list")
            .EnumerateArray().Select(MapHostReview).ToList();
    }

    private async Task<IReadOnlyList<PullRequestReview>> GetForgejoReviewsAsync(RemoteInfo info, int number, CancellationToken ct)
    {
        var url = $"{ForgejoBase(info)}/pulls/{number}/reviews";
        using var doc = await SendJsonAsync(HttpMethod.Get, url, info, null, ct).ConfigureAwait(false);
        return RequireArray(doc.RootElement, string.Empty, "Forgejo", "the review list")
            .EnumerateArray().Select(MapHostReview).ToList();
    }

    /// <summary>GitHub and Forgejo speak the same review shape (user.login / state / body / submitted_at).</summary>
    private static PullRequestReview MapHostReview(JsonElement e) => new(
        e.TryGetProperty("user", out var u) && u.TryGetProperty("login", out var l) ? l.GetString() ?? "unknown" : "unknown",
        e.TryGetProperty("state", out var s) ? s.GetString() ?? string.Empty : string.Empty,
        e.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? string.Empty : string.Empty,
        e.TryGetProperty("submitted_at", out var w) && w.ValueKind == JsonValueKind.String ? w.GetDateTimeOffset() : null);

    /// <summary>Azure DevOps records reviews as reviewer votes on the PR itself.</summary>
    private async Task<IReadOnlyList<PullRequestReview>> GetAdoReviewsAsync(RemoteInfo info, int number, CancellationToken ct)
    {
        var url = $"{AdoBase(info)}/pullrequests/{number}?{AdoApi}";
        using var doc = await SendJsonAsync(HttpMethod.Get, url, info, null, ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("reviewers", out var reviewers) || reviewers.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return reviewers.EnumerateArray()
            .Select(r => new PullRequestReview(
                r.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "unknown" : "unknown",
                r.TryGetProperty("vote", out var v) ? v.GetInt32() switch
                {
                    10 => "approved",
                    5 => "approved with suggestions",
                    -5 => "waiting for author",
                    -10 => "rejected",
                    _ => "no vote",
                } : "no vote",
                string.Empty,
                null))
            .Where(r => r.State != "no vote")
            .ToList();
    }

    // ── GitHub ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The repository's API base. This build addresses GitHub.com ONLY: every path below is
    /// <c>api.github.com</c>, which GitHub Enterprise Server does not serve. A git_hosts entry can name
    /// "GitHub" for any host, so without this guard a GHES remote silently asked api.github.com about a
    /// repository that is not there and got a 404 that looked like a Fravashi bug (backlog 76).
    /// </summary>
    private static string GitHubBase(RemoteInfo info)
    {
        if (info.Host.Length > 0
            && !info.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && !info.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"'{info.Host}' is registered as GitHub, but this build talks to GitHub.com (api.github.com) only. "
                + "GitHub Enterprise Server has a different API base and is not supported yet, so the request was "
                + "not sent rather than aimed at the wrong server.");
        }

        return $"https://api.github.com/repos/{info.Owner}/{info.Repo}";
    }

    private async Task<IReadOnlyList<PullRequest>> ListGitHubAsync(RemoteInfo info, PullRequestState state, CancellationToken ct)
    {
        var s = state switch { PullRequestState.Open => "open", PullRequestState.Closed => "closed", _ => "all" };
        var url = $"{GitHubBase(info)}/pulls?state={s}&per_page=50";
        using var doc = await SendJsonAsync(HttpMethod.Get, url, info, null, ct).ConfigureAwait(false);
        return RequireArray(doc.RootElement, string.Empty, "GitHub", "the pull-request list")
            .EnumerateArray().Select(MapGitHubPr).ToList();
    }

    private async Task<PullRequestDetail?> GetGitHubAsync(RemoteInfo info, int number, CancellationToken ct)
    {
        var url = $"{GitHubBase(info)}/pulls/{number}";
        using var doc = await SendJsonAsync(HttpMethod.Get, url, info, null, ct).ConfigureAwait(false);
        var pr = MapGitHubPr(doc.RootElement);
        var body = doc.RootElement.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String
            ? b.GetString() ?? string.Empty
            : string.Empty;

        var commentsUrl = $"{GitHubBase(info)}/issues/{number}/comments?per_page=50";
        using var cdoc = await SendJsonAsync(HttpMethod.Get, commentsUrl, info, null, ct).ConfigureAwait(false);
        var comments = RequireArray(cdoc.RootElement, string.Empty, "GitHub", "the comment list").EnumerateArray()
            .Select(c => new PullRequestComment(
                // Tolerant, like every other author field here: a comment whose author GitHub stopped
                // sending is still worth showing, so this is not a Require.
                c.TryGetProperty("user", out var cu) && cu.TryGetProperty("login", out var cl)
                    ? cl.GetString() ?? "unknown"
                    : "unknown",
                c.TryGetProperty("body", out var cb) ? cb.GetString() ?? string.Empty : string.Empty,
                c.TryGetProperty("created_at", out var ca) ? ca.GetDateTimeOffset() : DateTimeOffset.UnixEpoch))
            .ToList();

        return new PullRequestDetail(pr, body, comments);
    }

    private async Task<PullRequest> CreateGitHubAsync(RemoteInfo info, CreatePullRequestRequest request, CancellationToken ct)
    {
        var url = $"{GitHubBase(info)}/pulls";
        var payload = new { title = request.Title, head = request.SourceBranch, @base = request.TargetBranch, body = request.Body };
        using var doc = await SendJsonAsync(HttpMethod.Post, url, info, payload, ct).ConfigureAwait(false);
        return MapGitHubPr(doc.RootElement);
    }

    private async Task MergeGitHubAsync(RemoteInfo info, int number, MergeMethod method, CancellationToken ct)
    {
        var url = $"{GitHubBase(info)}/pulls/{number}/merge";
        var m = method switch { MergeMethod.Squash => "squash", MergeMethod.Rebase => "rebase", _ => "merge" };
        using var _ = await SendJsonAsync(HttpMethod.Put, url, info, new { merge_method = m }, ct).ConfigureAwait(false);
    }

    private static PullRequest MapGitHubPr(JsonElement e) => new(
        RequireInt(e, "number", "GitHub", "a pull request"),
        e.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty,
        e.TryGetProperty("user", out var u) && u.TryGetProperty("login", out var l) ? l.GetString() ?? string.Empty : string.Empty,
        e.TryGetProperty("head", out var h) && h.TryGetProperty("ref", out var hr) ? hr.GetString() ?? string.Empty : string.Empty,
        e.TryGetProperty("base", out var ba) && ba.TryGetProperty("ref", out var br) ? br.GetString() ?? string.Empty : string.Empty,
        e.TryGetProperty("state", out var s) ? s.GetString() ?? string.Empty : string.Empty,
        e.TryGetProperty("html_url", out var hu) && Uri.TryCreate(hu.GetString(), UriKind.Absolute, out var uri) ? uri : null,
        e.TryGetProperty("created_at", out var ca) ? ca.GetDateTimeOffset() : DateTimeOffset.UnixEpoch);

    // ── Azure DevOps ────────────────────────────────────────────────────────────

    /// <summary>
    /// The repository's API base. Azure DevOps SERVICES only: the path is <c>dev.azure.com</c> (which also
    /// serves the legacy <c>{org}.visualstudio.com</c> organizations). Azure DevOps Server (on-premises) has a
    /// collection-rooted API this build does not address, and a git_hosts entry can name "AzureDevOps" for any
    /// host, so it is refused by name instead of being aimed at the wrong server (backlog 76).
    /// </summary>
    private static string AdoBase(RemoteInfo info)
    {
        if (info.Host.Length > 0 && !IsAzureDevOpsServicesHost(info.Host))
        {
            throw new InvalidOperationException(
                $"'{info.Host}' is registered as Azure DevOps, but this build talks to Azure DevOps Services "
                + "(dev.azure.com) only. Azure DevOps Server (on-premises) exposes a different API root and is not "
                + "supported yet, so the request was not sent rather than aimed at the wrong server.");
        }

        return $"https://dev.azure.com/{info.Owner}/{info.Project}/_apis/git/repositories/{info.Repo}";
    }

    /// <summary>
    /// Every host Azure DevOps SERVICES is reached on, which is not only the one the API lives on. The
    /// origin remote a workspace carries decides <see cref="RemoteInfo.Host"/>, and the canonical Services
    /// SSH remote is <c>git@ssh.dev.azure.com:v3/{org}/{project}/{repo}</c> (parsed on purpose by
    /// <see cref="RemoteInfo.Parse"/>), with <c>vs-ssh.{org}.visualstudio.com</c> beside the legacy web
    /// hosts. Matching <c>dev.azure.com</c> exactly therefore refused Services itself over SSH, which is the
    /// configuration this guard exists to protect, so the subdomain forms are accepted here. A private
    /// Azure DevOps Server host still matches neither suffix and is still refused by name.
    /// </summary>
    private static bool IsAzureDevOpsServicesHost(string host) =>
        host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".dev.azure.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<PullRequest>> ListAdoAsync(RemoteInfo info, PullRequestState state, CancellationToken ct)
    {
        var s = state switch { PullRequestState.Open => "active", PullRequestState.Closed => "completed", _ => "all" };
        var url = $"{AdoBase(info)}/pullrequests?searchCriteria.status={s}&$top=50&{AdoApi}";
        using var doc = await SendJsonAsync(HttpMethod.Get, url, info, null, ct).ConfigureAwait(false);
        return RequireArray(doc.RootElement, "value", "Azure DevOps", "the pull-request list")
            .EnumerateArray().Select(MapAdoPr).ToList();
    }

    private async Task<PullRequestDetail?> GetAdoAsync(RemoteInfo info, int number, CancellationToken ct)
    {
        var url = $"{AdoBase(info)}/pullrequests/{number}?{AdoApi}";
        using var doc = await SendJsonAsync(HttpMethod.Get, url, info, null, ct).ConfigureAwait(false);
        var pr = MapAdoPr(doc.RootElement);
        var description = doc.RootElement.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty;

        var threadsUrl = $"{AdoBase(info)}/pullRequests/{number}/threads?{AdoApi}";
        using var tdoc = await SendJsonAsync(HttpMethod.Get, threadsUrl, info, null, ct).ConfigureAwait(false);
        var comments = new List<PullRequestComment>();
        foreach (var thread in RequireArray(tdoc.RootElement, "value", "Azure DevOps", "the comment threads").EnumerateArray())
        {
            if (!thread.TryGetProperty("comments", out var cs) || cs.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var c in cs.EnumerateArray())
            {
                var content = c.TryGetProperty("content", out var cc) ? cc.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                comments.Add(new PullRequestComment(
                    c.TryGetProperty("author", out var au) && au.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "unknown" : "unknown",
                    content,
                    c.TryGetProperty("publishedDate", out var pd) ? pd.GetDateTimeOffset() : DateTimeOffset.UnixEpoch));
            }
        }

        return new PullRequestDetail(pr, description, comments);
    }

    private async Task<PullRequest> CreateAdoAsync(RemoteInfo info, CreatePullRequestRequest request, CancellationToken ct)
    {
        var url = $"{AdoBase(info)}/pullrequests?{AdoApi}";
        var payload = new
        {
            sourceRefName = $"refs/heads/{request.SourceBranch}",
            targetRefName = $"refs/heads/{request.TargetBranch}",
            title = request.Title,
            description = request.Body,
        };
        using var doc = await SendJsonAsync(HttpMethod.Post, url, info, payload, ct).ConfigureAwait(false);
        return MapAdoPr(doc.RootElement);
    }

    private async Task MergeAdoAsync(RemoteInfo info, int number, MergeMethod method, CancellationToken ct)
    {
        // Completion needs the PR's last merge source commit.
        var prUrl = $"{AdoBase(info)}/pullrequests/{number}?{AdoApi}";
        using var prDoc = await SendJsonAsync(HttpMethod.Get, prUrl, info, null, ct).ConfigureAwait(false);
        var commitId = prDoc.RootElement.TryGetProperty("lastMergeSourceCommit", out var lm) && lm.TryGetProperty("commitId", out var cid)
            ? cid.GetString()
            : null;

        var strategy = method switch { MergeMethod.Squash => "squash", MergeMethod.Rebase => "rebase", _ => "noFastForward" };
        var payload = new
        {
            status = "completed",
            lastMergeSourceCommit = new { commitId },
            completionOptions = new { mergeStrategy = strategy },
        };
        using var _ = await SendJsonAsync(new HttpMethod("PATCH"), prUrl, info, payload, ct).ConfigureAwait(false);
    }

    private static PullRequest MapAdoPr(JsonElement e) => new(
        RequireInt(e, "pullRequestId", "Azure DevOps", "a pull request"),
        e.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty,
        e.TryGetProperty("createdBy", out var cb) && cb.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? string.Empty : string.Empty,
        StripRef(e, "sourceRefName"),
        StripRef(e, "targetRefName"),
        e.TryGetProperty("status", out var s) ? s.GetString() ?? string.Empty : string.Empty,
        BuildAdoUrl(e),
        e.TryGetProperty("creationDate", out var cd) ? cd.GetDateTimeOffset() : DateTimeOffset.UnixEpoch);

    private static string StripRef(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var r)
            ? (r.GetString() ?? string.Empty).Replace("refs/heads/", string.Empty, StringComparison.Ordinal)
            : string.Empty;

    private static Uri? BuildAdoUrl(JsonElement e)
    {
        if (e.TryGetProperty("repository", out var repo)
            && repo.TryGetProperty("webUrl", out var w)
            && Uri.TryCreate($"{w.GetString()}/pullrequest/{e.GetProperty("pullRequestId").GetInt32()}", UriKind.Absolute, out var uri))
        {
            return uri;
        }

        return null;
    }

    // ── Forgejo (new in phase 6; its API is Forgejo's own, treated as a first-class provider) ──

    private static string ForgejoBase(RemoteInfo info) =>
        $"https://{info.Host}/api/v1/repos/{info.Owner}/{info.Repo}";

    private async Task<IReadOnlyList<PullRequest>> ListForgejoAsync(RemoteInfo info, PullRequestState state, CancellationToken ct)
    {
        var s = state switch { PullRequestState.Open => "open", PullRequestState.Closed => "closed", _ => "all" };
        var url = $"{ForgejoBase(info)}/pulls?state={s}&limit=50";
        using var doc = await SendJsonAsync(HttpMethod.Get, url, info, null, ct).ConfigureAwait(false);
        return RequireArray(doc.RootElement, string.Empty, "Forgejo", "the pull-request list")
            .EnumerateArray().Select(MapForgejoPr).ToList();
    }

    private async Task<PullRequestDetail?> GetForgejoAsync(RemoteInfo info, int number, CancellationToken ct)
    {
        var url = $"{ForgejoBase(info)}/pulls/{number}";
        using var doc = await SendJsonAsync(HttpMethod.Get, url, info, null, ct).ConfigureAwait(false);
        var pr = MapForgejoPr(doc.RootElement);
        var body = doc.RootElement.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String
            ? b.GetString() ?? string.Empty
            : string.Empty;

        // PR discussion comments live on the issue of the same index.
        var commentsUrl = $"{ForgejoBase(info)}/issues/{number}/comments";
        using var cdoc = await SendJsonAsync(HttpMethod.Get, commentsUrl, info, null, ct).ConfigureAwait(false);
        var comments = RequireArray(cdoc.RootElement, string.Empty, "Forgejo", "the comment list").EnumerateArray()
            .Select(c => new PullRequestComment(
                c.TryGetProperty("user", out var u) && u.TryGetProperty("login", out var l) ? l.GetString() ?? "unknown" : "unknown",
                c.TryGetProperty("body", out var cb) ? cb.GetString() ?? string.Empty : string.Empty,
                c.TryGetProperty("created_at", out var ca) ? ca.GetDateTimeOffset() : DateTimeOffset.UnixEpoch))
            .ToList();

        return new PullRequestDetail(pr, body, comments);
    }

    private async Task<PullRequest> CreateForgejoAsync(RemoteInfo info, CreatePullRequestRequest request, CancellationToken ct)
    {
        var url = $"{ForgejoBase(info)}/pulls";
        var payload = new { title = request.Title, body = request.Body, head = request.SourceBranch, @base = request.TargetBranch };
        using var doc = await SendJsonAsync(HttpMethod.Post, url, info, payload, ct).ConfigureAwait(false);
        return MapForgejoPr(doc.RootElement);
    }

    private async Task MergeForgejoAsync(RemoteInfo info, int number, MergeMethod method, CancellationToken ct)
    {
        var url = $"{ForgejoBase(info)}/pulls/{number}/merge";
        var m = method switch { MergeMethod.Squash => "squash", MergeMethod.Rebase => "rebase", _ => "merge" };
        using var _ = await SendJsonAsync(HttpMethod.Post, url, info, new { Do = m }, ct).ConfigureAwait(false);
    }

    private static PullRequest MapForgejoPr(JsonElement e) => new(
        e.TryGetProperty("number", out var n) ? n.GetInt32() : 0,
        e.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty,
        e.TryGetProperty("user", out var u) && u.TryGetProperty("login", out var l) ? l.GetString() ?? string.Empty : string.Empty,
        e.TryGetProperty("head", out var h) && h.TryGetProperty("ref", out var hr) ? hr.GetString() ?? string.Empty : string.Empty,
        e.TryGetProperty("base", out var ba) && ba.TryGetProperty("ref", out var br) ? br.GetString() ?? string.Empty : string.Empty,
        e.TryGetProperty("state", out var s) ? s.GetString() ?? string.Empty : string.Empty,
        e.TryGetProperty("html_url", out var hu) && Uri.TryCreate(hu.GetString(), UriKind.Absolute, out var uri) ? uri : null,
        e.TryGetProperty("created_at", out var ca) ? ca.GetDateTimeOffset() : DateTimeOffset.UnixEpoch);

    // ── Plumbing ────────────────────────────────────────────────────────────────

    private async Task<RemoteInfo> RemoteAsync(CancellationToken ct)
    {
        if (_remote is { } cached)
        {
            return cached.Info;
        }

        var url = await _git.GetRemoteUrlAsync(ct).ConfigureAwait(false);
        var parsed = RemoteInfo.Parse(url);
        var resolution = _access.Resolve(parsed);
        // The switches below dispatch on Provider, so the cached info carries the RESOLVED one
        // (a git_hosts entry can name a provider the URL alone cannot prove).
        _remote = (parsed with { Provider = resolution.Provider }, resolution);
        return _remote.Value.Info;
    }

    private async Task<RemoteInfo> RequireConfiguredAsync(CancellationToken ct)
    {
        var info = await RemoteAsync(ct).ConfigureAwait(false);
        if (info.Provider == PullRequestProvider.None)
        {
            throw new InvalidOperationException("No configured pull-request provider for the origin remote.");
        }

        if (Credential() is null)
        {
            throw new InvalidOperationException(
                "Creating or merging a pull request needs a stored token; add one under Settings > Connections > Git hosts.");
        }

        return info;
    }

    /// <summary>The resolved per-host credential (populated by <see cref="RemoteAsync"/>).</summary>
    private StoredCredential? Credential() => _remote?.Resolution.Credential;

    private async Task<JsonDocument> SendJsonAsync(HttpMethod method, string url, RemoteInfo info, object? body, CancellationToken ct)
    {
        // Anonymous requests are legitimate for reads on public repos; auth attaches when stored.
        var credential = Credential();

        using var request = new HttpRequestMessage(method, url);
        switch (info.Provider)
        {
            case PullRequestProvider.GitHub:
                if (credential is not null)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
                }

                request.Headers.UserAgent.ParseAdd("Fravashi");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                break;
            case PullRequestProvider.AzureDevOps:
                // A PAT rides basic with an empty user; an SSO (Entra) token is a bearer.
                if (credential is not null)
                {
                    request.Headers.Authorization = string.Equals(credential.Method, AuthMethods.Pat, StringComparison.OrdinalIgnoreCase)
                        ? new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{credential.AccessToken}")))
                        : new AuthenticationHeaderValue("Bearer", credential.AccessToken);
                }

                request.Headers.Accept.ParseAdd("application/json");
                break;
            default:
                if (credential is not null)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("token", credential.AccessToken);
                }

                request.Headers.Accept.ParseAdd("application/json");
                break;
        }

        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Name the provider and the condition (backlog 76): a bare "403: {json}" reads like our bug,
            // and the three that matter here (expired token, renamed/deleted repo, retired API) are
            // exactly the ones a user can act on.
            throw new HttpRequestException(
                ProviderFailure.Describe(ProviderName(info.Provider), Subject(info), response.StatusCode, payload),
                inner: null,
                response.StatusCode);
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            return JsonDocument.Parse("{}");
        }

        try
        {
            return JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            throw new HttpRequestException(
                ProviderFailure.UnreadableBody(ProviderName(info.Provider), Subject(info), payload));
        }
    }

    /// <summary>The provider's own name, for a message a user reads.</summary>
    private static string ProviderName(PullRequestProvider provider) => provider switch
    {
        PullRequestProvider.GitHub => "GitHub",
        PullRequestProvider.AzureDevOps => "Azure DevOps",
        PullRequestProvider.Forgejo => "Forgejo",
        _ => "The git host",
    };

    /// <summary>What was addressed, in the coordinates the user recognises from the origin remote.</summary>
    private static string Subject(RemoteInfo info)
    {
        var path = info.Provider == PullRequestProvider.AzureDevOps && info.Project.Length > 0
            ? $"{info.Owner}/{info.Project}/{info.Repo}"
            : $"{info.Owner}/{info.Repo}";
        return info.Host.Length > 0 ? $"{path} on {info.Host}" : path;
    }

    /// <summary>
    /// Reads a field this build depends on, and says whose contract moved when it is absent, rather than
    /// letting a <c>KeyNotFoundException</c> escape as an unhandled studio error (backlog 76).
    /// </summary>
    private static int RequireInt(JsonElement element, string field, string provider, string subject) =>
        element.TryGetProperty(field, out var value) && value.TryGetInt32(out var number)
            ? number
            : throw new InvalidOperationException(ProviderFailure.MissingField(provider, subject, field));

    /// <summary>Same, for a field (or a whole response) this build iterates as an array.</summary>
    private static JsonElement RequireArray(JsonElement element, string field, string provider, string subject)
    {
        var target = field.Length == 0 ? element : element.TryGetProperty(field, out var v) ? v : default;
        return target.ValueKind == JsonValueKind.Array
            ? target
            : throw new InvalidOperationException(
                ProviderFailure.MissingField(provider, subject, field.Length == 0 ? "a top-level array" : field));
    }
}

/// <summary>DI for pull-request management (GitHub / Azure DevOps / Forgejo).</summary>
public static class PullRequestServiceCollectionExtensions
{
    public static IServiceCollection AddPullRequests(this IServiceCollection services)
    {
        services.AddHttpClient<IPullRequestService, PullRequestService>();
        return services;
    }
}
