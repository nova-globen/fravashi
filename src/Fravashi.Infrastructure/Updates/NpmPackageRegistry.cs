using System.Text.Json;
using Fravashi.Application.Updates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Fravashi.Infrastructure.Updates;

/// <summary>
/// Reads a package's latest published version from the npm registry (Type A update check, ADR 0006): one
/// anonymous HTTPS GET of <c>{base}/{package}/latest</c>, from which it reads the manifest's <c>version</c>
/// (falling back to the <c>dist-tags.latest</c> shape). The base URL is <c>Updates:RegistryBaseUrl</c>
/// (default <c>https://registry.npmjs.org</c>) so a scenario/test can point it at a fake endpoint - the same
/// config-overridable seam the onboarding probes use. Any failure returns <c>null</c> (honest "unknown"),
/// never a fabricated version.
/// </summary>
public sealed class NpmPackageRegistry : IPackageRegistry
{
    /// <summary>Configuration key for the registry base URL (overridable for tests / air-gapped mirrors).</summary>
    public const string BaseUrlKey = "Updates:RegistryBaseUrl";

    /// <summary>The public npm registry, used when nothing overrides it.</summary>
    public const string DefaultBaseUrl = "https://registry.npmjs.org";

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly ILogger<NpmPackageRegistry> _logger;

    public NpmPackageRegistry(HttpClient http, IConfiguration configuration, ILogger<NpmPackageRegistry> logger)
    {
        _http = http;
        _logger = logger;
        var configured = configuration[BaseUrlKey];
        _baseUrl = (string.IsNullOrWhiteSpace(configured) ? DefaultBaseUrl : configured).TrimEnd('/');
        if (_http.Timeout == default || _http.Timeout > TimeSpan.FromSeconds(30))
        {
            _http.Timeout = TimeSpan.FromSeconds(15);
        }
    }

    public async Task<string?> GetLatestVersionAsync(string package, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(package))
        {
            return null;
        }

        var url = $"{_baseUrl}/{package}/latest";
        try
        {
            using var response = await _http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Registry returned {Status} for {Package}.", (int)response.StatusCode, package);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return ExtractVersion(document.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Offline, timeout, or a malformed response: honest "unknown", never a silent "up to date".
            _logger.LogDebug(ex, "Could not read the latest version of {Package} from the registry.", package);
            return null;
        }
    }

    /// <summary>Reads <c>version</c> from a <c>/latest</c> manifest, or <c>dist-tags.latest</c> from a packument.</summary>
    public static string? ExtractVersion(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String)
            {
                return Clean(version.GetString());
            }

            if (root.TryGetProperty("dist-tags", out var tags) &&
                tags.ValueKind == JsonValueKind.Object &&
                tags.TryGetProperty("latest", out var latest) &&
                latest.ValueKind == JsonValueKind.String)
            {
                return Clean(latest.GetString());
            }
        }

        return null;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
