using System.Net.Http.Json;
using System.Text.Json;
using Fravashi.Application.Updates;
using Microsoft.Extensions.Configuration;
using Fravashi.Domain;

namespace Fravashi.Infrastructure.Updates;

/// <summary>
/// <see cref="IStudioUpgrades"/> over fravashi.exe's upgrade operation (backlog 157), handed to this container as
/// <c>FRAVASHI_UPGRADE_URL</c> and <c>FRAVASHI_UPGRADE_TOKEN</c>, a token of its own. It sends nothing but a version: what
/// is installed is what the update channel offers, fetched and verified by the launcher on the host.
/// </summary>
public sealed class StudioUpgradeClient(HttpClient http, IConfiguration configuration) : IStudioUpgrades
{
    public const string UrlKey = "FRAVASHI_UPGRADE_URL";
    public const string TokenKey = "FRAVASHI_UPGRADE_TOKEN";
    private const string TokenHeader = "X-Fravashi-Upgrade";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool Available =>
        !string.IsNullOrWhiteSpace(configuration[UrlKey]) && !string.IsNullOrWhiteSpace(configuration[TokenKey]);

    public async Task<UpgradeOffer?> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!Available)
        {
            return null;
        }

        try
        {
            using var response = await SendAsync(HttpMethod.Get, "upgrade/check", null, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<UpgradeOffer>(Json, cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    public async Task<string?> ApplyAsync(string version, CancellationToken cancellationToken = default)
    {
        if (!Available)
        {
            return $"{ProductName.Executable} handed this studio no upgrade operation.";
        }

        try
        {
            using var response = await SendAsync(HttpMethod.Post, "upgrade/apply", new { version }, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("error", out var error) && error.GetString() is { Length: > 0 } text)
                {
                    return text;
                }
            }
            catch (JsonException)
            {
                // Fall through to the status.
            }

            return $"{ProductName.Executable} refused the upgrade ({(int)response.StatusCode}).";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return $"{ProductName.Executable}'s upgrade operation could not be reached ({ex.Message}).";
        }
    }

    public async Task<UpgradeProgress?> StatusAsync(CancellationToken cancellationToken = default)
    {
        if (!Available)
        {
            return null;
        }

        try
        {
            using var response = await SendAsync(HttpMethod.Get, "upgrade/status", null, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<UpgradeProgress>(Json, cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, new Uri(new Uri(configuration[UrlKey]!.TrimEnd('/') + "/"), path));
        request.Headers.Add(TokenHeader, configuration[TokenKey]);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        return http.SendAsync(request, cancellationToken);
    }
}
