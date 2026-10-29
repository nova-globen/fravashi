using System.Text.Json;

namespace Fravashi.Launcher;

/// <summary>
/// What <c>/healthz</c> answered. The schema version is the part a restore reports: the launcher can read
/// the version recorded in the archive, but only the running studio can say what version it opened the
/// restored database AT, which is the difference between "the files were copied" and "the product came
/// back up on them".
/// </summary>
/// <param name="Ok">True when the studio answered a success status.</param>
/// <param name="SchemaVersion">The schema version the studio reports, when it reports one.</param>
public sealed record StudioHealth(bool Ok, int? SchemaVersion);

/// <summary>
/// Polls the studio's <c>/healthz</c> until it answers or the deadline passes, and reads the schema
/// version out of the response. Separate from <see cref="StudioReadinessProbe"/>, which answers only
/// "can the window be opened yet" and is on the launch hot path.
/// </summary>
public static class StudioHealthProbe
{
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(3) };

    /// <summary>Polls <paramref name="baseUrl"/><c>/healthz</c>; returns null when it never answered.</summary>
    public static StudioHealth? Poll(string baseUrl, TimeSpan timeout, TimeSpan pollInterval)
    {
        var url = $"{baseUrl.TrimEnd('/')}/healthz";
        var deadline = DateTimeOffset.UtcNow + timeout;
        do
        {
            if (ReadOnce(url) is { } health)
            {
                return health;
            }

            Thread.Sleep(pollInterval);
        }
        while (DateTimeOffset.UtcNow < deadline);

        return null;
    }

    private static StudioHealth? ReadOnce(string url)
    {
        try
        {
            using var response = SharedClient.GetAsync(url).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return new StudioHealth(true, ReadSchemaVersion(body));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            // Not up yet (refused, reset, or slow): the caller keeps polling until its deadline.
            return null;
        }
    }

    /// <summary>
    /// Reads <c>schemaVersion</c> out of the health document, tolerating its absence: an older studio
    /// image answers <c>{"status":"ok"}</c> and that is still a healthy studio, just one that cannot say
    /// which schema it opened.
    /// </summary>
    internal static int? ReadSchemaVersion(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            // The kind is checked before the value is read: TryGetInt32 THROWS on a string or a null
            // rather than answering false, so a health document with a schemaVersion of any other shape
            // would have taken the restore's final report down with it (caught by the test below it).
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("schemaVersion", out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var version)
                    ? version
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
