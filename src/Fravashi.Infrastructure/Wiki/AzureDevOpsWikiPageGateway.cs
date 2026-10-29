using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fravashi.Application.Mirroring;
using Fravashi.Application.Onboarding;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Persistence;
using Fravashi.Application.Wiki;
using Fravashi.Domain.Wiki;
using Fravashi.Infrastructure.Providers;

namespace Fravashi.Infrastructure.Wiki;

/// <summary>
/// An Azure DevOps project wiki as a place documents are published to (backlog 144), behind the same port as Confluence.
/// <para>
/// <b>What differs, and why it is decided here.</b> A page is MARKDOWN already, so a document crosses unconverted and
/// nothing is lost. A page's place is its PATH, so a page under a parent is created at the parent's path plus its own
/// name. A page's version is an ETAG, not a number: the port speaks numbers, so a page's version here is a stable number
/// made from its ETag, and a write first reads the page, refuses with a conflict when that number is not the one the
/// publisher last recorded, and then sends the change with <c>If-Match</c> on the ETag it read, so the site refuses a
/// change that raced it. The "space" is the wiki's name; with none named, the project's own wiki is used.
/// </para>
/// </summary>
public sealed class AzureDevOpsWikiPageGateway(
    HttpClient http,
    IProviderConnectionStore connections,
    IConfigurationStore settings,
    ISecretStore secrets,
    IProviderTokenRefresher? refresher = null) : IWikiPageGateway
{
    private const string Api = "api-version=7.1";
    private const string ConnectionId = "wiki";

    /// <summary>The space a page goes to when none is named: the project's own wiki.</summary>
    public const string ProjectWiki = "project wiki";

    /// <inheritdoc />
    public string Provider => "Azure DevOps Wiki";

    /// <inheritdoc />
    public bool VersionsAreReadable => false;

    /// <inheritdoc />
    public bool IsConfigured => Settings() is not null;

    /// <inheritdoc />
    public string? DefaultSpace => ProjectWiki;

    /// <inheritdoc />
    public ConversionResult ToSite(string markdown) => new(markdown ?? string.Empty, []);

    /// <inheritdoc />
    public ConversionResult FromSite(string body) => new(body ?? string.Empty, []);

    /// <inheritdoc />
    public async Task<ProviderResult<SitePage>> CreateAsync(
        string space, string title, string body, string? parentPageId = null, CancellationToken ct = default)
    {
        if (await ReadyAsync(ct).ConfigureAwait(false) is not { } site)
        {
            return NotConfigured<SitePage>();
        }

        // A parent is in the wiki its id names, and a child goes into the same wiki.
        var wiki = parentPageId is { Length: > 0 } && Split(parentPageId) is { } under
            ? ProviderResult<string>.Ok(under.Wiki)
            : await WikiAsync(site, space, ct).ConfigureAwait(false);
        if (!wiki.IsOk)
        {
            return wiki.As<SitePage>();
        }

        var parentPath = "/";
        if (parentPageId is { Length: > 0 })
        {
            var parent = await PageAsync(site, parentPageId, content: false, ct).ConfigureAwait(false);
            if (!parent.IsOk)
            {
                return parent.As<SitePage>();
            }

            parentPath = parent.Value!.Path.TrimEnd('/') + "/";
        }

        var path = parentPath + Segment(title);
        return await PutAsync(site, wiki.Value!, path, body, etag: null, $"creating the page '{path}'", ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ProviderResult<SitePage>> UpdateAsync(string pageId, string title, string body, int newVersion, CancellationToken ct = default)
    {
        if (await ReadyAsync(ct).ConfigureAwait(false) is not { } site)
        {
            return NotConfigured<SitePage>();
        }

        var current = await PageAsync(site, pageId, content: false, ct).ConfigureAwait(false);
        if (!current.IsOk)
        {
            return current.As<SitePage>();
        }

        if (VersionOf(current.Value!.ETag) != newVersion - 1)
        {
            return ProviderResult<SitePage>.Conflict($"The page '{current.Value.Path}' was changed on the wiki since it was last published here.");
        }

        // A page's name is its path, and renaming one is a move this adapter does not make: the page keeps its place and
        // its content is what changes.
        return await PutAsync(site, Split(pageId)!.Value.Wiki, current.Value.Path, body, current.Value.ETag, $"changing the page '{current.Value.Path}'", ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ProviderResult<SitePage>> GetAsync(string pageId, CancellationToken ct = default)
    {
        if (await ReadyAsync(ct).ConfigureAwait(false) is not { } site)
        {
            return NotConfigured<SitePage>();
        }

        var page = await PageAsync(site, pageId, content: true, ct).ConfigureAwait(false);
        return page.IsOk ? ProviderResult<SitePage>.Ok(page.Value!.Page) : page.As<SitePage>();
    }

    /// <inheritdoc />
    public async Task<ProviderResult<SitePage?>> FindByTitleAsync(string space, string title, CancellationToken ct = default)
    {
        if (await ReadyAsync(ct).ConfigureAwait(false) is not { } site)
        {
            return NotConfigured<SitePage?>();
        }

        var wiki = await WikiAsync(site, space, ct).ConfigureAwait(false);
        if (!wiki.IsOk)
        {
            return wiki.As<SitePage?>();
        }

        // The whole tree, without content, and the page whose own name is the title. A page's name is unique only under
        // its parent, so more than one match is not an answer this can give.
        var tree = await SendAsync(
            site, HttpMethod.Get,
            $"{site.Project}/_apis/wiki/wikis/{Uri.EscapeDataString(wiki.Value!)}/pages?path=/&recursionLevel=full&{Api}",
            null, null, "the wiki's pages", (body, _) => Flatten(body), ct).ConfigureAwait(false);
        if (!tree.IsOk)
        {
            return tree.As<SitePage?>();
        }

        var name = Segment(title);
        var matches = tree.Value!.Where(p => string.Equals(p.Path[(p.Path.LastIndexOf('/') + 1)..], name, StringComparison.Ordinal)).ToList();
        if (matches.Count == 0)
        {
            return ProviderResult<SitePage?>.Ok(null);
        }

        if (matches.Count > 1)
        {
            return ProviderResult<SitePage?>.Indeterminate($"The wiki has {matches.Count} pages named '{name}', so which one is this document's cannot be told.");
        }

        var page = await PageAsync(site, wiki.Value! + ":" + matches[0].Id, content: true, ct).ConfigureAwait(false);
        return page.IsOk ? ProviderResult<SitePage?>.Ok(page.Value!.Page) : page.As<SitePage?>();
    }

    // ---------------------------------------------------------------------------------------------------------
    // The site
    // ---------------------------------------------------------------------------------------------------------

    private sealed record Site(string Project, AuthenticationHeaderValue Authorization);

    private sealed record Found(string Id, string Path, string ETag, SitePage Page);

    private Site? Settings()
    {
        if (!string.Equals(settings.Get("Onboarding:Wiki:Provider"), OnboardingProviders.AzureDevOpsWiki, StringComparison.Ordinal))
        {
            return null;
        }

        var connection = connections.Get(ConnectionId);
        if (connection?.BaseUrl is not { Length: > 0 } project || connection.CredentialRef is not { Length: > 0 } reference
            || CredentialEnvelope.Parse(secrets.Get(reference)) is not { } credential)
        {
            return null;
        }

        var header = credential.Method == AuthMethods.Pat
            ? new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + credential.AccessToken)))
            : new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        return new Site(project.TrimEnd('/'), header);
    }

    private async Task<Site?> ReadyAsync(CancellationToken ct)
    {
        if (refresher is not null)
        {
            await refresher.RefreshIfNeededAsync(ConnectionId, ct).ConfigureAwait(false);
        }

        return Settings();
    }

    private static ProviderResult<T> NotConfigured<T>() =>
        ProviderResult<T>.Unreachable("No Azure DevOps wiki connection is configured, so nothing was sent.");

    /// <summary>The wiki a space names: by name or id, else the project's own wiki, else the first there is.</summary>
    private async Task<ProviderResult<string>> WikiAsync(Site site, string? space, CancellationToken ct)
    {
        var wikis = await SendAsync(
            site, HttpMethod.Get, $"{site.Project}/_apis/wiki/wikis?{Api}", null, null, "the project's wikis",
            (body, _) => body.TryGetProperty("value", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(w => (Id: Text(w, "id"), Name: Text(w, "name"), Type: Text(w, "type"))).Where(w => w.Id is not null).ToList()
                : throw new JsonException("the answer carries no value array"),
            ct).ConfigureAwait(false);
        if (!wikis.IsOk)
        {
            return wikis.As<string>();
        }

        var named = wikis.Value!.FirstOrDefault(w =>
            string.Equals(w.Name, space, StringComparison.OrdinalIgnoreCase) || string.Equals(w.Id, space, StringComparison.OrdinalIgnoreCase));
        var chosen = named.Id is not null ? named
            : wikis.Value!.FirstOrDefault(w => string.Equals(w.Type, "projectWiki", StringComparison.OrdinalIgnoreCase)) is { Id: not null } projectWiki ? projectWiki
            : wikis.Value!.FirstOrDefault();
        return chosen.Id is { } id
            ? ProviderResult<string>.Ok(id)
            : ProviderResult<string>.Refused("The Azure DevOps project has no wiki yet. Create its wiki there, then publish again.");
    }

    /// <summary>A page by the id this gateway gave it: the wiki's id and the page's, so a page is always read where it is.</summary>
    private async Task<ProviderResult<Found>> PageAsync(Site site, string pageId, bool content, CancellationToken ct)
    {
        if (Split(pageId) is not { } where)
        {
            return ProviderResult<Found>.Refused($"'{pageId}' is not a page id this wiki gave.");
        }

        return await SendAsync(
            site, HttpMethod.Get,
            $"{site.Project}/_apis/wiki/wikis/{Uri.EscapeDataString(where.Wiki)}/pages/{Uri.EscapeDataString(where.Page)}?includeContent={(content ? "true" : "false")}&{Api}",
            null, null, $"the page {where.Page}", (json, tag) => ToFound(json, tag, where.Wiki), ct).ConfigureAwait(false);
    }

    private static (string Wiki, string Page)? Split(string pageId)
    {
        var colon = pageId.LastIndexOf(':');
        return colon > 0 && colon < pageId.Length - 1 ? (pageId[..colon], pageId[(colon + 1)..]) : null;
    }

    private Task<ProviderResult<SitePage>> PutAsync(Site site, string wiki, string path, string body, string? etag, string subject, CancellationToken ct) =>
        SendAsync(
            site, HttpMethod.Put,
            $"{site.Project}/_apis/wiki/wikis/{Uri.EscapeDataString(wiki)}/pages?path={Uri.EscapeDataString(path)}&{Api}",
            new JsonObject { ["content"] = body }, etag, subject, (json, tag) => ToFound(json, tag, wiki).Page, ct);

    private static Found ToFound(JsonElement page, string? etag, string wiki)
    {
        var id = page.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.Number
            ? wiki + ":" + value.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)
            : throw new JsonException("the page carries no id");
        var path = Text(page, "path") ?? throw new JsonException("the page carries no path");
        var tag = etag ?? string.Empty;
        return new Found(id, path, tag, new SitePage(
            id, path[(path.LastIndexOf('/') + 1)..], VersionOf(tag), Text(page, "content") ?? string.Empty, Text(page, "remoteUrl")));
    }

    private static List<(string Id, string Path)> Flatten(JsonElement page)
    {
        var pages = new List<(string, string)>();
        if (page.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && Text(page, "path") is { } path && path != "/")
        {
            pages.Add((id.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture), path));
        }

        if (page.TryGetProperty("subPages", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                pages.AddRange(Flatten(child));
            }
        }

        return pages;
    }

    /// <summary>
    /// A page's version as the port's number: stable for one ETag and different for another. Taken from a hash, so it
    /// says whether the page moved, never by how much.
    /// </summary>
    public static int VersionOf(string etag)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(etag.Trim('"')));
        return (int)(BitConverter.ToUInt32(bytes, 0) & 0x3FFFFFFF);
    }

    /// <summary>A title as a page name: the characters a wiki path cannot hold are replaced, nothing else is changed.</summary>
    public static string Segment(string title)
    {
        var name = new StringBuilder(title.Length);
        foreach (var c in title.Trim())
        {
            name.Append(c is '/' or '\\' or '#' or '?' ? '-' : c);
        }

        return name.Length == 0 ? "page" : name.ToString();
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private async Task<ProviderResult<TOut>> SendAsync<TOut>(
        Site site,
        HttpMethod method,
        string url,
        JsonNode? payload,
        string? ifMatch,
        string subject,
        Func<JsonElement, string?, TOut> map,
        CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = site.Authorization;
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (ifMatch is { Length: > 0 })
            {
                request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            }

            if (payload is not null)
            {
                request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            }

            response = await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ProviderCallFailure.Catches(ex, ct))
        {
            // Never left (not sent) or left with no answer (the site is asked): the one shared rule decides.
            return ProviderCallFailure.Result<TOut>("Azure DevOps", ex, ct);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NonAuthoritativeInformation)
            {
                return ProviderResult<TOut>.Refused(
                    $"Azure DevOps did not accept the wiki's credential for {subject}; it answered with its sign-in page.");
            }

            if (response.IsSuccessStatusCode)
            {
                try
                {
                    using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
                    return ProviderResult<TOut>.Ok(map(document.RootElement, response.Headers.ETag?.Tag));
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                {
                    return ProviderResult<TOut>.Indeterminate(ProviderFailure.UnreadableBody("Azure DevOps", subject, text));
                }
            }

            var reason = ProviderFailure.Describe("Azure DevOps", subject, response.StatusCode, text);
            return response.StatusCode switch
            {
                HttpStatusCode.NotFound => ProviderResult<TOut>.Gone(reason),
                HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed => ProviderResult<TOut>.Conflict(reason),
                HttpStatusCode.TooManyRequests => ProviderResult<TOut>.Unreachable(reason),
                >= HttpStatusCode.InternalServerError => ProviderResult<TOut>.Indeterminate(reason),
                _ => ProviderResult<TOut>.Refused(reason),
            };
        }
    }
}
