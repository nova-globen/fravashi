using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Fravashi.Application.Network;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Domain.PullRequests;
using Fravashi.Infrastructure.Git;
using Fravashi.Infrastructure.Providers;
using Fravashi.Infrastructure.PullRequests;

namespace Fravashi.Infrastructure.Ci;

/// <summary>One CI run (or, on a forge that lists jobs, one job of a run) as every provider reports it.</summary>
/// <param name="Id">What the provider needs back to name the run: Forgejo's run number, GitHub's run id, Azure DevOps' build id.</param>
/// <param name="Number">The human number, where the provider has one distinct from the id.</param>
/// <param name="Status">The provider's own word, the conclusion when there is one (success, failure, running, queued).</param>
public sealed record ForgeRun(
    string Id,
    int? Number,
    string Workflow,
    string? Job,
    string Status,
    string HeadSha,
    string? Branch,
    string Title,
    string? Url,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? UpdatedUtc);

/// <summary>A run's log text, cut to a cap from the tail by default because that is where a failure is.</summary>
public sealed record ForgeRunLog(string Text, bool Truncated, string Source);

/// <summary>
/// A CI read that never got an answer because of the NETWORK (backlog 233): the name, the connection or the client's own
/// timeout, in slice A's kinds. Never thrown for an answer the forge gave, whatever its status.
/// </summary>
public sealed class ForgeUnreachableException(string host, NetworkFailure failure, Exception inner)
    : Exception($"Could not reach {host} ({failure.Words}).", inner)
{
    /// <summary>The host the failed request was addressed to.</summary>
    public string Host { get; } = host;

    /// <summary>What happened, in slice A's kinds.</summary>
    public NetworkFailure Failure { get; } = failure;
}

/// <summary>The repository behind a workspace's origin remote, with the provider and credential that serve it.</summary>
public sealed record ForgeRepository(RemoteInfo Remote, GitHostResolution Resolution)
{
    public PullRequestProvider Provider => Resolution.Provider;

    public StoredCredential? Credential => Resolution.Credential;

    /// <summary>What was addressed, in the coordinates a person recognises from the origin remote.</summary>
    public string Subject
    {
        get
        {
            var path = Provider == PullRequestProvider.AzureDevOps && Remote.Project.Length > 0
                ? $"{Remote.Owner}/{Remote.Project}/{Remote.Repo}"
                : $"{Remote.Owner}/{Remote.Repo}";
            return Remote.Host.Length > 0 ? $"{path} on {Remote.Host}" : path;
        }
    }

    public string ProviderName => Provider switch
    {
        PullRequestProvider.GitHub => "GitHub",
        PullRequestProvider.AzureDevOps => "Azure DevOps",
        PullRequestProvider.Forgejo => "Forgejo",
        _ => "The git host",
    };
}

/// <summary>
/// CI runs and their logs for a workspace's repository, read from its git host with the token the PR panel already
/// uses (backlog 153, plan 2c decision 8: the credential is per git host and resolves from the origin remote). The
/// agent never holds the token: it asks the studio, which asks the host. Three providers, each spoken to in its own
/// dialect, every URL built here from the parsed remote and never from agent text.
/// <para>
/// What each can and cannot do, measured rather than assumed: Forgejo's API lists TASKS (one row per job) and has
/// no log endpoint at all, so a log is read from the web page that serves it, which answers only while the repository
/// is public or the token is accepted there; GitHub lists runs and serves a job's log behind a redirect; Azure DevOps
/// lists builds and serves numbered logs per build.
/// </para>
/// </summary>
public sealed class ForgeRuns(HttpClient http, Func<GitHostAccess> access)
{
    private const string AdoApi = "api-version=7.1";

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly Func<GitHostAccess> _access = access ?? throw new ArgumentNullException(nameof(access));

    /// <summary>The repository an origin URL names, or null when no provider serves it.</summary>
    public ForgeRepository? Repository(string? originUrl)
    {
        var remote = RemoteInfo.Parse(originUrl);
        if (remote.Host.Length == 0)
        {
            return null;
        }

        var resolution = _access().Resolve(remote);
        return resolution.Provider == PullRequestProvider.None
            ? null
            : new ForgeRepository(remote with { Provider = resolution.Provider }, resolution);
    }

    /// <summary>The newest runs, optionally only those for a commit (a full or short sha).</summary>
    public async Task<IReadOnlyList<ForgeRun>> ListAsync(
        ForgeRepository repository, int max, string? sha, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        max = Math.Clamp(max, 1, 50);
        var runs = repository.Provider switch
        {
            PullRequestProvider.Forgejo => await ListForgejoAsync(repository, cancellationToken).ConfigureAwait(false),
            PullRequestProvider.GitHub => await ListGitHubAsync(repository, max, sha, cancellationToken).ConfigureAwait(false),
            PullRequestProvider.AzureDevOps => await ListAdoAsync(repository, max, sha, cancellationToken).ConfigureAwait(false),
            _ => [],
        };

        if (!string.IsNullOrWhiteSpace(sha))
        {
            runs = runs.Where(r => r.HeadSha.StartsWith(sha.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return runs.Take(max).ToList();
    }

    /// <summary>
    /// A run's log, or null when the provider has none to give for it. <paramref name="job"/> is a job's name or its
    /// index (Forgejo: the job's position in the run, GitHub: name or position, Azure DevOps: a log id); absent means
    /// the provider's most useful default (Forgejo job 0, GitHub the first failed job or else the last, Azure DevOps
    /// the last log).
    /// </summary>
    public async Task<ForgeRunLog?> LogAsync(
        ForgeRepository repository, string run, string? job, int cap, bool tail, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(run);
        var raw = repository.Provider switch
        {
            PullRequestProvider.Forgejo => await ForgejoLogAsync(repository, run, job, cancellationToken).ConfigureAwait(false),
            PullRequestProvider.GitHub => await GitHubLogAsync(repository, run, job, cancellationToken).ConfigureAwait(false),
            PullRequestProvider.AzureDevOps => await AdoLogAsync(repository, run, job, cancellationToken).ConfigureAwait(false),
            _ => null,
        };

        if (raw is null)
        {
            return null;
        }

        var (text, source) = raw.Value;
        if (text.Length <= cap)
        {
            return new ForgeRunLog(text, false, source);
        }

        return new ForgeRunLog(tail ? text[^cap..] : text[..cap], true, source);
    }

    // ---- Forgejo -------------------------------------------------------------------------------

    private static string ForgejoApi(ForgeRepository r) =>
        $"https://{r.Remote.Host}/api/v1/repos/{r.Remote.Owner}/{r.Remote.Repo}";

    private async Task<List<ForgeRun>> ListForgejoAsync(ForgeRepository repository, CancellationToken ct)
    {
        // The endpoint lists every task the repository ever ran and ignores a limit (measured 2026-09-20), so the
        // trimming is done here; it is one call either way.
        using var doc = await GetJsonAsync(repository, ForgejoApi(repository) + "/actions/tasks", ct).ConfigureAwait(false);
        var runs = new List<ForgeRun>();
        if (!doc.RootElement.TryGetProperty("workflow_runs", out var tasks) || tasks.ValueKind != JsonValueKind.Array)
        {
            return runs;
        }

        foreach (var task in tasks.EnumerateArray())
        {
            var number = Int(task, "run_number");
            runs.Add(new ForgeRun(
                number?.ToString(CultureInfo.InvariantCulture) ?? Text(task, "id") ?? string.Empty,
                number,
                Text(task, "workflow_id") ?? string.Empty,
                Text(task, "name"),
                Text(task, "status") ?? string.Empty,
                Text(task, "head_sha") ?? string.Empty,
                Text(task, "head_branch"),
                Text(task, "display_title") ?? string.Empty,
                Text(task, "url"),
                When(task, "run_started_at") ?? When(task, "created_at"),
                When(task, "updated_at")));
        }

        // Newest first, by the task id the forge hands out in order.
        return runs.OrderByDescending(r => r.Number ?? 0).ThenByDescending(r => r.StartedUtc).ToList();
    }

    private async Task<(string Text, string Source)?> ForgejoLogAsync(
        ForgeRepository repository, string run, string? job, CancellationToken ct)
    {
        if (!int.TryParse(run, NumberStyles.None, CultureInfo.InvariantCulture, out var runNumber))
        {
            return null;
        }

        var index = 0;
        if (!string.IsNullOrWhiteSpace(job) && !int.TryParse(job, NumberStyles.None, CultureInfo.InvariantCulture, out index))
        {
            // A job NAME: its position among the run's tasks, oldest first, which is how the forge numbers them.
            var jobs = (await ListForgejoAsync(repository, ct).ConfigureAwait(false))
                .Where(r => r.Number == runNumber)
                .OrderBy(r => r.StartedUtc)
                .Select(r => r.Job)
                .ToList();
            index = jobs.FindIndex(name => string.Equals(name, job.Trim(), StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return null;
            }
        }

        // Not the API: it has no log endpoint (every candidate answers 404, measured 2026-08-08). The web page that
        // serves the log does, for a public repository or a token the forge accepts there.
        var url = $"https://{repository.Remote.Host}/{repository.Remote.Owner}/{repository.Remote.Repo}"
            + $"/actions/runs/{runNumber}/jobs/{index}/attempt/1/logs";
        var text = await GetTextAsync(repository, url, ct, notFoundIsNull: true).ConfigureAwait(false);
        return text is null ? null : (text, url);
    }

    // ---- GitHub --------------------------------------------------------------------------------

    private static string GitHubApi(ForgeRepository r) =>
        $"https://api.github.com/repos/{r.Remote.Owner}/{r.Remote.Repo}";

    private async Task<List<ForgeRun>> ListGitHubAsync(ForgeRepository repository, int max, string? sha, CancellationToken ct)
    {
        var url = $"{GitHubApi(repository)}/actions/runs?per_page={max}";
        if (sha is { Length: 40 })
        {
            url += "&head_sha=" + Uri.EscapeDataString(sha);
        }

        using var doc = await GetJsonAsync(repository, url, ct).ConfigureAwait(false);
        var runs = new List<ForgeRun>();
        if (!doc.RootElement.TryGetProperty("workflow_runs", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return runs;
        }

        foreach (var run in items.EnumerateArray())
        {
            runs.Add(new ForgeRun(
                Text(run, "id") ?? string.Empty,
                Int(run, "run_number"),
                Text(run, "name") ?? string.Empty,
                null,
                Text(run, "conclusion") ?? Text(run, "status") ?? string.Empty,
                Text(run, "head_sha") ?? string.Empty,
                Text(run, "head_branch"),
                Text(run, "display_title") ?? string.Empty,
                Text(run, "html_url"),
                When(run, "run_started_at") ?? When(run, "created_at"),
                When(run, "updated_at")));
        }

        return runs;
    }

    private async Task<(string Text, string Source)?> GitHubLogAsync(
        ForgeRepository repository, string run, string? job, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(repository, $"{GitHubApi(repository)}/actions/runs/{Uri.EscapeDataString(run)}/jobs", ct)
            .ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("jobs", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var jobs = items.EnumerateArray()
            .Select(j => (Id: Text(j, "id"), Name: Text(j, "name"), Conclusion: Text(j, "conclusion")))
            .Where(j => j.Id is not null)
            .ToList();
        if (jobs.Count == 0)
        {
            return null;
        }

        (string? Id, string? Name, string? Conclusion) chosen;
        if (string.IsNullOrWhiteSpace(job))
        {
            chosen = jobs.FirstOrDefault(j => j.Conclusion == "failure");
            if (chosen.Id is null)
            {
                chosen = jobs[^1];
            }
        }
        else if (int.TryParse(job, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            if (index < 0 || index >= jobs.Count)
            {
                return null;
            }

            chosen = jobs[index];
        }
        else
        {
            chosen = jobs.FirstOrDefault(j => string.Equals(j.Name, job.Trim(), StringComparison.OrdinalIgnoreCase));
            if (chosen.Id is null)
            {
                return null;
            }
        }

        // The log answers with a redirect to a signed blob; the client follows it and, being another host, drops the
        // token on the way, which is what should happen.
        var url = $"{GitHubApi(repository)}/actions/jobs/{chosen.Id}/logs";
        var text = await GetTextAsync(repository, url, ct, notFoundIsNull: true).ConfigureAwait(false);
        return text is null ? null : (text, $"job {chosen.Name ?? chosen.Id} of run {run}");
    }

    // ---- Azure DevOps --------------------------------------------------------------------------

    private static string AdoBase(ForgeRepository r) =>
        r.Remote.Host.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase)
            ? $"https://dev.azure.com/{r.Remote.Owner}/{Uri.EscapeDataString(r.Remote.Project)}"
            : $"https://{r.Remote.Host}/{Uri.EscapeDataString(r.Remote.Project)}";

    private async Task<List<ForgeRun>> ListAdoAsync(ForgeRepository repository, int max, string? sha, CancellationToken ct)
    {
        // Builds cannot be asked for by commit, so a sha filter fetches more and trims client-side.
        var top = string.IsNullOrWhiteSpace(sha) ? max : Math.Max(max, 50);
        using var doc = await GetJsonAsync(repository, $"{AdoBase(repository)}/_apis/build/builds?{AdoApi}&$top={top}", ct)
            .ConfigureAwait(false);
        var runs = new List<ForgeRun>();
        if (!doc.RootElement.TryGetProperty("value", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return runs;
        }

        foreach (var build in items.EnumerateArray())
        {
            var definition = build.TryGetProperty("definition", out var d) ? Text(d, "name") : null;
            string? web = null;
            if (build.TryGetProperty("_links", out var links) && links.TryGetProperty("web", out var w))
            {
                web = Text(w, "href");
            }

            runs.Add(new ForgeRun(
                Text(build, "id") ?? string.Empty,
                Int(build, "id"),
                definition ?? string.Empty,
                null,
                Text(build, "result") ?? Text(build, "status") ?? string.Empty,
                Text(build, "sourceVersion") ?? string.Empty,
                Text(build, "sourceBranch"),
                Text(build, "buildNumber") ?? string.Empty,
                web,
                When(build, "startTime") ?? When(build, "queueTime"),
                When(build, "finishTime")));
        }

        return runs;
    }

    private async Task<(string Text, string Source)?> AdoLogAsync(
        ForgeRepository repository, string run, string? job, CancellationToken ct)
    {
        var logs = $"{AdoBase(repository)}/_apis/build/builds/{Uri.EscapeDataString(run)}/logs";
        int logId;
        if (string.IsNullOrWhiteSpace(job))
        {
            using var doc = await GetJsonAsync(repository, $"{logs}?{AdoApi}", ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("value", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var ids = items.EnumerateArray().Select(l => Int(l, "id")).Where(i => i is not null).Select(i => i!.Value).ToList();
            if (ids.Count == 0)
            {
                return null;
            }

            logId = ids.Max();
        }
        else if (!int.TryParse(job, NumberStyles.None, CultureInfo.InvariantCulture, out logId))
        {
            return null;
        }

        var url = $"{logs}/{logId}?{AdoApi}";
        var text = await GetTextAsync(repository, url, ct, notFoundIsNull: true).ConfigureAwait(false);
        return text is null ? null : (text, $"log {logId} of build {run}");
    }

    // ---- plumbing ------------------------------------------------------------------------------

    private async Task<JsonDocument> GetJsonAsync(ForgeRepository repository, string url, CancellationToken ct)
    {
        var (status, payload) = await FetchAsync(repository, url, "application/json", ct).ConfigureAwait(false);
        if ((int)status is < 200 or > 299)
        {
            throw new HttpRequestException(
                ProviderFailure.Describe(repository.ProviderName, repository.Subject, status, payload),
                inner: null,
                status);
        }

        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(payload) ? "{}" : payload);
        }
        catch (JsonException)
        {
            throw new HttpRequestException(ProviderFailure.UnreadableBody(repository.ProviderName, repository.Subject, payload));
        }
    }

    private async Task<string?> GetTextAsync(ForgeRepository repository, string url, CancellationToken ct, bool notFoundIsNull)
    {
        var (status, payload) = await FetchAsync(repository, url, "text/plain", ct).ConfigureAwait(false);
        if (notFoundIsNull && status == HttpStatusCode.NotFound)
        {
            return null;
        }

        if ((int)status is < 200 or > 299)
        {
            throw new HttpRequestException(
                ProviderFailure.Describe(repository.ProviderName, repository.Subject, status, payload),
                inner: null,
                status);
        }

        return payload;
    }

    /// <summary>
    /// One GET and its body. A failure slice A calls the network (backlog 233) becomes <see cref="ForgeUnreachableException"/>,
    /// naming the host of the request that failed (after a redirect, the host it was redirected to), so a caller can tell
    /// "the forge answered 401" from "nothing came back" without guessing from an exception that carries no status. The
    /// caller's own cancellation stays a cancellation.
    /// </summary>
    private async Task<(HttpStatusCode Status, string Payload)> FetchAsync(
        ForgeRepository repository, string url, string accept, CancellationToken ct)
    {
        using var request = Request(repository, url, accept);
        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (response.StatusCode, payload);
        }
        catch (Exception ex) when (NetworkFailure.Classify(ex, ct) is { IsNetwork: true } failure)
        {
            throw new ForgeUnreachableException(request.RequestUri?.Host ?? repository.Remote.Host, failure, ex);
        }
    }

    /// <summary>The request with the provider's own authentication, the same dialects the PR panel speaks.</summary>
    private static HttpRequestMessage Request(ForgeRepository repository, string url, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        var credential = repository.Credential;
        switch (repository.Provider)
        {
            case PullRequestProvider.GitHub:
                if (credential is not null)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
                }

                request.Headers.UserAgent.ParseAdd("Fravashi");
                request.Headers.Accept.ParseAdd(accept == "application/json" ? "application/vnd.github+json" : accept);
                break;
            case PullRequestProvider.AzureDevOps:
                if (credential is not null)
                {
                    request.Headers.Authorization = string.Equals(credential.Method, AuthMethods.Pat, StringComparison.OrdinalIgnoreCase)
                        ? new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{credential.AccessToken}")))
                        : new AuthenticationHeaderValue("Bearer", credential.AccessToken);
                }

                request.Headers.Accept.ParseAdd(accept);
                break;
            default:
                if (credential is not null)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("token", credential.AccessToken);
                }

                request.Headers.Accept.ParseAdd(accept);
                break;
        }

        return request;
    }

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i
            : null;

    private static DateTimeOffset? When(JsonElement e, string name) =>
        Text(e, name) is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)
        && when.Year > 1
            ? when
            : null;
}
