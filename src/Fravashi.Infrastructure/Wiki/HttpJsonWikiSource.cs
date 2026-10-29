using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fravashi.Application.Wiki;

namespace Fravashi.Infrastructure.Wiki;

/// <summary>
/// A generic HTTP wiki read source: one <c>GET {base}/pages</c> returning a JSON array of pages. It backs the
/// <c>Wiki:SourceBaseUrl</c> config override - a real, config-overridable base URL (the same shape T5 used for
/// the update registry) so the sync can be driven end-to-end against a stand-in wiki server without a live
/// Confluence/ADO tenant. Site is authoritative; nothing is written back.
/// </summary>
public sealed class HttpJsonWikiSource(HttpClient http, string baseUrl) : IWikiSource
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public string Provider => "HTTP wiki";

    public async Task<IReadOnlyList<WikiSourcePage>> FetchAllAsync(
        IProgress<WikiFetchProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var url = $"{baseUrl.TrimEnd('/')}/pages";
        var payload = await http.GetFromJsonAsync<List<PageDto>>(url, Options, cancellationToken).ConfigureAwait(false)
            ?? [];

        var pages = new List<WikiSourcePage>(payload.Count);
        var fetched = 0;
        foreach (var dto in payload)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new WikiFetchProgress(fetched, payload.Count, dto.Title));
            pages.Add(new WikiSourcePage(
                Space: string.IsNullOrWhiteSpace(dto.Space) ? "wiki" : dto.Space,
                Title: dto.Title ?? dto.Slug ?? "Untitled",
                Slug: dto.Slug ?? (dto.Title ?? "page").Replace(' ', '-').ToLowerInvariant(),
                Body: dto.Body ?? string.Empty,
                Format: string.IsNullOrWhiteSpace(dto.Format) ? "markdown" : dto.Format,
                SourceRef: dto.Version));
            fetched++;
            progress?.Report(new WikiFetchProgress(fetched, payload.Count, dto.Title));
        }

        return pages;
    }

    private sealed record PageDto(
        [property: JsonPropertyName("space")] string? Space,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("slug")] string? Slug,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("format")] string? Format,
        [property: JsonPropertyName("version")] string? Version);
}
