using System.Net.Http.Headers;
using System.Text.Json;
using Fravashi.Application.Wiki;
using Fravashi.Infrastructure.Providers;
using Microsoft.Extensions.Options;
using Atlassian = Fravashi.Infrastructure.Tracker.Atlassian;

namespace Fravashi.Infrastructure.Wiki;

/// <summary>
/// Confluence reader for <see cref="IConfluenceReader"/>, lifted from the DevOps app's ConfluenceService
/// (Confluence Cloud v2 API). Resolves a space key to its id, lists current pages, and returns a page's
/// storage-format body as HTML.
/// </summary>
/// <remarks>
/// The Confluence Cloud wire models (<see cref="Atlassian.ConfluencePage"/> and friends) live with the rest
/// of the Atlassian integration and are reached through the <c>Atlassian</c> namespace alias, so the Wiki
/// DTOs this reader returns keep their bare names - including the wire/DTO pair that both read
/// <c>ConfluencePage</c>.
/// </remarks>
public sealed class AtlassianConfluenceReader : IConfluenceReader
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// The kinds of space a sync reads when nobody named one. The request used to carry <c>type=global</c>, which
    /// was there to skip personal spaces and skipped far more: the v2 contract's types are global, collaboration,
    /// knowledge_base, personal, system, onboarding and xflow_sample_space, a current site creates its team
    /// spaces as the second and third, and the parameter takes ONE value. So the installed product verified a
    /// connection by writing a page to a space it then could not list, and reported "Synced 0 page(s)"
    /// (backlog 12, 2026-09-21). The list is asked for unfiltered and narrowed here, to the same intent.
    /// </summary>
    private static readonly HashSet<string> TeamSpaceTypes =
        new(StringComparer.OrdinalIgnoreCase) { "global", "collaboration", "knowledge_base" };

    private readonly HttpClient _http;
    private readonly IOptionsMonitor<Atlassian.AtlassianConfig> _options;

    public AtlassianConfluenceReader(HttpClient http, IOptionsMonitor<Atlassian.AtlassianConfig> options)
    {
        _http = http;
        _options = options;
    }

    /// <summary>
    /// The configuration AS IT IS NOW, read at each use and never captured. <c>IOptions&lt;T&gt;.Value</c> is bound
    /// once for the life of the process and does not hear a reload, and the first ask comes from the Home page's
    /// wiki card, before anything is connected. So a wiki connected from Settings in a running studio stayed
    /// "not fully wired yet" until the container restarted, which is how the installed product met backlog 12
    /// after its fix had shipped. A Blazor circuit also keeps one reader for as long as the window is open, which
    /// is why the credential rides on each request (<see cref="GetAsync"/>) rather than on the client's default
    /// headers: a token that was rotated, or that arrived after this reader was built, has to be the one sent.
    /// </summary>
    private Atlassian.AtlassianConfig Config => _options.CurrentValue;

    /// <summary>
    /// Gated on the CONFLUENCE half of the Atlassian config, never on the Jira half: a wiki-only install has no
    /// Jira project key and never will, and the reader does not need one (backlog 12).
    /// </summary>
    public bool IsConfigured => Config.IsConfluenceValid;

    public async Task<IReadOnlyList<ConfluenceSpaceSummary>> ListSpacesAsync(CancellationToken cancellationToken = default)
    {
        var config = Config;
        if (!config.IsConfluenceValid)
        {
            return [];
        }

        // Not handled today: only the first 250 global spaces are read (Confluence paginates with _links.next,
        // which this build does not follow). See docs/providers/support-contract.md.
        var url = $"{config.ConfluenceBaseUrl}/spaces?limit=250&status=current";
        var result = await GetAsync<Atlassian.ConfluenceSpaceResult>(config, url, "the space list", cancellationToken);
        return result?.Results
            .Where(s => TeamSpaceTypes.Contains(s.Type))
            .Select(s => new ConfluenceSpaceSummary(s.Id, s.Key, string.IsNullOrWhiteSpace(s.Name) ? s.Key : s.Name))
            .ToList() ?? [];
    }

    public async Task<IReadOnlyList<ConfluencePageSummary>> ListPagesAsync(string spaceKey, CancellationToken cancellationToken = default)
    {
        var config = Config;
        if (!config.IsConfluenceValid || string.IsNullOrWhiteSpace(spaceKey))
        {
            return [];
        }

        var spaceId = await ResolveSpaceIdAsync(config, spaceKey, cancellationToken);
        if (spaceId is null)
        {
            return [];
        }

        // Not handled today: only the first 50 pages of a space are read (same unfollowed _links.next).
        var url = $"{config.ConfluenceBaseUrl}/pages?space-id={spaceId}&status=current&limit=50&sort=title";
        var result = await GetAsync<Atlassian.ConfluencePageResult>(config, url, $"the pages of space '{spaceKey}'", cancellationToken);
        return result?.Results.Select(p => new ConfluencePageSummary(p.Id, p.Title, spaceKey)).ToList() ?? [];
    }

    public async Task<ConfluencePage?> GetPageAsync(string pageId, CancellationToken cancellationToken = default)
    {
        var config = Config;
        if (!config.IsConfluenceValid || string.IsNullOrWhiteSpace(pageId))
        {
            return null;
        }

        var url = $"{config.ConfluenceBaseUrl}/pages/{pageId}?body-format=storage";
        var page = await GetAsync<Atlassian.ConfluencePage>(config, url, $"page {pageId}", cancellationToken);
        if (page is null)
        {
            return null;
        }

        return new ConfluencePage(page.Id, page.Title, page.SpaceId, page.Body?.Storage?.Value ?? string.Empty);
    }

    private async Task<string?> ResolveSpaceIdAsync(
        Atlassian.AtlassianConfig config, string spaceKey, CancellationToken cancellationToken)
    {
        // Asked BY KEY, which is the exact question. Listing every space and looking for the key in the answer
        // was measured not to work: in the owner's install on 2026-09-21 the unfiltered listing answered 200
        // and did not contain the owner's personal space, so the sync resolved no space, asked for no pages and
        // said "Synced 0 page(s)" (backlog 12, second cause). The same site answers `?keys=~<user>` with the
        // space, which is how the publish gateway found it and created a page there the same day.
        var byKey = $"{config.ConfluenceBaseUrl}/spaces?keys={Uri.EscapeDataString(spaceKey.Trim())}&limit=1";
        var named = await GetAsync<Atlassian.ConfluenceSpaceResult>(config, byKey, $"the space '{spaceKey}'", cancellationToken);
        if (named?.Results.FirstOrDefault(s => !string.IsNullOrEmpty(s.Id)) is { } found)
        {
            return found.Id;
        }

        // A caller that holds a space ID rather than a key still resolves, through the listing.
        var url = $"{config.ConfluenceBaseUrl}/spaces?limit=250&status=current";
        var result = await GetAsync<Atlassian.ConfluenceSpaceResult>(config, url, $"the space '{spaceKey}'", cancellationToken);
        var match = result?.Results.FirstOrDefault(s =>
            string.Equals(s.Key, spaceKey, StringComparison.OrdinalIgnoreCase) || s.Id == spaceKey);
        return match?.Id;
    }

    /// <summary>
    /// A GET that fails HONESTLY (backlog 76). <c>GetFromJsonAsync</c> turned every provider-side failure into
    /// ".NET: Response status code does not indicate success: 401 (Unauthorized)", which the wiki-sync card
    /// then showed as "Sync failed: ..." and read like a Fravashi bug. The three conditions that actually
    /// happen here (an expired or revoked API token, a space that was renamed or deleted, and Confluence
    /// changing its v2 shape) are now named as Confluence's, with Confluence's own error text attached.
    /// </summary>
    private async Task<T?> GetAsync<T>(
        Atlassian.AtlassianConfig config, string url, string subject, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = config.AuthenticationHeader;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _http.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                ProviderFailure.Describe("Confluence", subject, response.StatusCode, payload),
                inner: null,
                response.StatusCode);
        }

        try
        {
            return JsonSerializer.Deserialize<T>(payload, Json);
        }
        catch (JsonException)
        {
            throw new HttpRequestException(ProviderFailure.UnreadableBody("Confluence", subject, payload));
        }
    }
}
