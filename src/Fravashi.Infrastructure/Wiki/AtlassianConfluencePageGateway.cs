using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Fravashi.Application.Mirroring;
using Fravashi.Application.Wiki;
using Fravashi.Infrastructure.Providers;
using Microsoft.Extensions.Options;
using Atlassian = Fravashi.Infrastructure.Tracker.Atlassian;
using Fravashi.Domain;

namespace Fravashi.Infrastructure.Wiki;

/// <summary>
/// Confluence Cloud (v2) as somewhere an authored document can be sent (backlog 83). It is the write half
/// beside <see cref="AtlassianConfluenceReader"/>, and like the reader it reads the configuration at each
/// use and puts the credential on each request, so a connection saved in a running studio is the one used.
/// <para>
/// The part that is not plumbing is how a call that did not succeed is REPORTED. "It never left" and "it
/// left and nothing came back" are different states of knowledge, the publisher treats them differently,
/// and the choice between them is made here and nowhere else. Anything this cannot classify is reported as
/// indeterminate, because the cost of wrongly saying "never left" is a second page in somebody's wiki and
/// the cost of wrongly saying "indeterminate" is one question to the site.
/// </para>
/// </summary>
public sealed class AtlassianConfluencePageGateway : IWikiPageGateway
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly IOptionsMonitor<Atlassian.AtlassianConfig> _options;

    public AtlassianConfluencePageGateway(HttpClient http, IOptionsMonitor<Atlassian.AtlassianConfig> options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public string Provider => "Confluence";

    /// <inheritdoc />
    public bool IsConfigured => _options.CurrentValue.IsConfluenceValid;

    /// <inheritdoc />
    public async Task<ProviderResult<SitePage>> CreateAsync(
        string space, string title, string body, string? parentPageId = null, CancellationToken ct = default)
    {
        var config = _options.CurrentValue;
        var spaceId = await ResolveSpaceIdAsync(config, space, ct).ConfigureAwait(false);
        if (!spaceId.IsOk)
        {
            return spaceId.As<SitePage>();
        }

        var payload = new Dictionary<string, object>
        {
            ["spaceId"] = spaceId.Value!,
            ["status"] = "current",
            ["title"] = title,
            ["body"] = new { representation = "storage", value = body },
        };
        if (!string.IsNullOrWhiteSpace(parentPageId))
        {
            // Confluence nests a page under its parent by id; without it the page lands at the space's top.
            payload["parentId"] = parentPageId;
        }

        return await SendAsync<Atlassian.ConfluencePage, SitePage>(
            config, HttpMethod.Post, $"{config.ConfluenceBaseUrl}/pages", payload, $"creating the page '{title}'",
            page => ToSitePage(config, page, versionIfUnsaid: 1), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<ProviderResult<SitePage>> UpdateAsync(string pageId, string title, string body, int newVersion, CancellationToken ct = default)
    {
        var config = _options.CurrentValue;
        var payload = new
        {
            id = pageId,
            status = "current",
            title,
            body = new { representation = "storage", value = body },
            version = new { number = newVersion, message = $"Published from {ProductName.Short}" },
        };

        return SendAsync<Atlassian.ConfluencePage, SitePage>(
            config, HttpMethod.Put, $"{config.ConfluenceBaseUrl}/pages/{Uri.EscapeDataString(pageId)}", payload,
            $"updating page {pageId}", page => ToSitePage(config, page, versionIfUnsaid: newVersion), ct);
    }

    /// <inheritdoc />
    public async Task<ProviderResult<SitePage>> GetAsync(string pageId, CancellationToken ct = default)
    {
        var config = _options.CurrentValue;
        var result = await SendAsync<Atlassian.ConfluencePage, (SitePage Page, string Status)>(
            config, HttpMethod.Get, $"{config.ConfluenceBaseUrl}/pages/{Uri.EscapeDataString(pageId)}?body-format=storage",
            null, $"page {pageId}", page => (ToSitePage(config, page), page.Status), ct).ConfigureAwait(false);

        if (!result.IsOk)
        {
            return result.As<SitePage>();
        }

        // A page deleted in Confluence's own UI goes to the site's TRASH, and asking for it by id answers
        // 200 with an empty body and status "trashed", never 404. Measured against the real site on
        // 2026-09-22: reading only the status code reported a deleted page as the current one, because the
        // version number still matched what the last publish had written.
        var status = result.Value.Status;
        return IsGone(status)
            ? ProviderResult<SitePage>.Gone(
                $"Confluence holds page {pageId} with status '{status}', so the site no longer has it as a page.")
            : ProviderResult<SitePage>.Ok(result.Value.Page);
    }

    /// <summary>
    /// Deliberately narrow: only the two statuses that mean the site no longer holds the page. An ARCHIVED
    /// page is still there, and calling it gone offers "Publish as a new page", which is how a duplicate
    /// arrives in somebody's wiki. A status the site does not send is read as the current page for the same
    /// reason, which is the adapter's standing preference: one wrong question costs less than one wrong page.
    /// </summary>
    private static bool IsGone(string status) =>
        string.Equals(status, "trashed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "deleted", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<ProviderResult<SitePage?>> FindByTitleAsync(string space, string title, CancellationToken ct = default)
    {
        var config = _options.CurrentValue;
        var spaceId = await ResolveSpaceIdAsync(config, space, ct).ConfigureAwait(false);
        if (!spaceId.IsOk)
        {
            return spaceId.As<SitePage?>();
        }

        var url = $"{config.ConfluenceBaseUrl}/pages?space-id={Uri.EscapeDataString(spaceId.Value!)}"
            + $"&title={Uri.EscapeDataString(title)}&status=current&body-format=storage&limit=5";
        var found = await SendAsync<Atlassian.ConfluencePageResult, SitePage?>(
            config, HttpMethod.Get, url, null, $"the page titled '{title}'",
            result => result.Results
                .Where(p => string.Equals(p.Title, title, StringComparison.Ordinal))
                .Select(p => ToSitePage(config, p))
                .FirstOrDefault(),
            ct).ConfigureAwait(false);

        // "The site has none" is Ok(null), and a missing LIST is not that answer: a 404 here is the space or
        // the endpoint, never "no such page", so it must not be read as permission to send the creation again.
        return found.Outcome == ProviderOutcome.Gone
            ? ProviderResult<SitePage?>.Refused(found.Reason ?? "The site could not list the space's pages.")
            : found;
    }

    private async Task<ProviderResult<string>> ResolveSpaceIdAsync(
        Atlassian.AtlassianConfig config, string space, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(space))
        {
            return ProviderResult<string>.Refused("No space is named for published pages.");
        }

        var url = $"{config.ConfluenceBaseUrl}/spaces?keys={Uri.EscapeDataString(space.Trim())}&limit=1";
        var result = await SendAsync<Atlassian.ConfluenceSpaceResult, string?>(
            config, HttpMethod.Get, url, null, $"the space '{space}'",
            spaces => spaces.Results.FirstOrDefault(s => !string.IsNullOrEmpty(s.Id))?.Id, ct).ConfigureAwait(false);

        if (!result.IsOk)
        {
            return result.As<string>() with { Outcome = result.Outcome == ProviderOutcome.Gone ? ProviderOutcome.Refused : result.Outcome };
        }

        return result.Value is { } id
            ? ProviderResult<string>.Ok(id)
            : ProviderResult<string>.Refused($"Confluence has no space with the key '{space}' that this credential can see.");
    }

    private async Task<ProviderResult<TOut>> SendAsync<TWire, TOut>(
        Atlassian.AtlassianConfig config,
        HttpMethod method,
        string url,
        object? payload,
        string subject,
        Func<TWire, TOut> map,
        CancellationToken ct)
    {
        if (!config.IsConfluenceValid)
        {
            return ProviderResult<TOut>.Unreachable("No Confluence connection is configured, so nothing was sent.");
        }

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = config.AuthenticationHeader;
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (payload is not null)
            {
                request.Content = JsonContent.Create(payload);
            }

            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ProviderCallFailure.Catches(ex, ct))
        {
            // Never left (not sent) or left with no answer (the site is asked): the one shared rule decides.
            return ProviderCallFailure.Result<TOut>("Confluence", ex, ct);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    var wire = JsonSerializer.Deserialize<TWire>(text, Json);
                    return wire is null
                        ? ProviderResult<TOut>.Indeterminate(ProviderFailure.UnreadableBody("Confluence", subject, text))
                        : ProviderResult<TOut>.Ok(map(wire));
                }
                catch (JsonException)
                {
                    // It said yes and we cannot read what it did. That is not knowing, not a refusal.
                    return ProviderResult<TOut>.Indeterminate(ProviderFailure.UnreadableBody("Confluence", subject, text));
                }
            }

            var reason = ProviderFailure.Describe("Confluence", subject, response.StatusCode, text);
            return response.StatusCode switch
            {
                HttpStatusCode.NotFound => ProviderResult<TOut>.Gone(reason),
                HttpStatusCode.Conflict => ProviderResult<TOut>.Conflict(reason),

                // It answered, and it did nothing: asked to slow down. Safe to send again.
                HttpStatusCode.TooManyRequests => ProviderResult<TOut>.Unreachable(reason),

                // A server fault is an answer that does not say whether the write was applied.
                >= HttpStatusCode.InternalServerError => ProviderResult<TOut>.Indeterminate(reason),

                _ => ProviderResult<TOut>.Refused(reason),
            };
        }
    }

    // A write's answer that leaves the version out is read as the version the write asked to become. Recording
    // zero instead would make the next publish claim version one and be refused as stale for ever.
    private static SitePage ToSitePage(Atlassian.AtlassianConfig config, Atlassian.ConfluencePage page, int versionIfUnsaid = 0) => new(
        page.Id,
        page.Title,
        page.Version?.Number ?? versionIfUnsaid,
        page.Body?.Storage?.Value ?? string.Empty,
        string.IsNullOrEmpty(page.Links?.WebUi) ? null : $"https://{config.JiraDomain}/wiki{page.Links.WebUi}");
}
