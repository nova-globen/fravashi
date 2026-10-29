using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fravashi.Domain;

namespace Fravashi.Launcher.Releases;

/// <summary>What the running fravashi.exe answered when a console <c>fravashi update</c> handed it the update.</summary>
/// <param name="Applied">Whether the container now runs the new version.</param>
/// <param name="Said">What the running fravashi.exe said, in words.</param>
public sealed record ResidentUpgradeResult(bool Applied, string Said);

/// <summary>
/// Hands a console <c>fravashi update</c> to the fravashi.exe that is already running, through its upgrade operation (backlog
/// 157). A console holds no bridge, so an update it applied itself left the studio without one until the next restart
/// (backlog 160's honest record of that); the running fravashi.exe holds the bridge and the app runner, so the container it
/// recreates keeps them, and the dashboard's "run fravashi update" leaves a working studio.
/// </summary>
public interface IResidentUpgrade
{
    /// <summary>
    /// Asks the running fravashi.exe to install exactly <paramref name="version"/> and follows it to the end. Null when no
    /// running fravashi.exe answers, and then the console updates by itself as before.
    /// </summary>
    ResidentUpgradeResult? TryApply(string version, Action<string> log);
}

/// <summary>
/// <see cref="IResidentUpgrade"/> over the loopback address of the running fravashi.exe's upgrade operation. The port is the
/// one it recorded in launcher.json, and the token is this install's own, beside launcher.json.
/// </summary>
public sealed class HttpResidentUpgrade(
    string configDirectory,
    Func<int?> port,
    HttpClient? http = null,
    TimeSpan? pollInterval = null,
    TimeSpan? patience = null) : IResidentUpgrade
{
    /// <summary>Must match <c>Fravashi.Bridge.StudioUpgradeToken.FileName</c>; this library references no other project.</summary>
    public const string TokenFileName = "upgrade.token";

    /// <summary>Must match <c>Fravashi.Bridge.StudioUpgradeToken.Header</c>.</summary>
    public const string Header = "X-Fravashi-Upgrade";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    private readonly TimeSpan _poll = pollInterval ?? TimeSpan.FromSeconds(3);
    private readonly TimeSpan _patience = patience ?? TimeSpan.FromMinutes(45);

    public ResidentUpgradeResult? TryApply(string version, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (port() is not int listening || ReadToken() is not { } token)
        {
            return null;
        }

        var root = new Uri($"http://127.0.0.1:{listening}/");
        HttpResponseMessage started;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(root, "upgrade/apply"))
            {
                Content = JsonContent.Create(new { version }, options: Json),
            };
            request.Headers.Add(Header, token);
            started = _http.Send(request);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Nothing is listening: there is no running fravashi.exe, or one from before this feature.
            return null;
        }

        using (started)
        {
            if (started.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
            {
                // Something answers on that port that is not this install's upgrade operation.
                return null;
            }

            if (started.StatusCode != HttpStatusCode.Accepted)
            {
                return new ResidentUpgradeResult(false, ErrorOf(started) ?? $"The running {ProductName.Short} refused the update ({(int)started.StatusCode}).");
            }
        }

        log($"Handed the update to the running {ProductName.Short}, which keeps its host bridge and app runner. Following it...");
        var until = DateTimeOffset.UtcNow + _patience;
        while (DateTimeOffset.UtcNow < until)
        {
            Thread.Sleep(_poll);
            if (Status(root, token) is not { } status)
            {
                continue;
            }

            switch (status.State)
            {
                case "Succeeded":
                    return new ResidentUpgradeResult(true, status.Said ?? $"Updated to {version}.");
                case "Failed":
                    return new ResidentUpgradeResult(false, status.Said ?? "The update did not complete.");
            }
        }

        return new ResidentUpgradeResult(false, $"The running {ProductName.Short} is still updating to {version}; `{ProductName.Command} status` says when it is done.");
    }

    private string? ReadToken()
    {
        try
        {
            var path = Path.Combine(configDirectory, TokenFileName);
            return File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } token ? token : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private Progress? Status(Uri root, string token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(root, "upgrade/status"));
            request.Headers.Add(Header, token);
            using var response = _http.Send(request);
            return response.IsSuccessStatusCode
                ? JsonSerializer.Deserialize<Progress>(response.Content.ReadAsStream(), Json)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // The container being replaced takes nothing of fravashi.exe with it, but a slow answer is asked again.
            return null;
        }
    }

    private static string? ErrorOf(HttpResponseMessage response)
    {
        try
        {
            using var document = JsonDocument.Parse(response.Content.ReadAsStream());
            return document.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record Progress(string? State, string? Version, string? Said);
}
