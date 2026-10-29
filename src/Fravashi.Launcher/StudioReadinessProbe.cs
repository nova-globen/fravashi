namespace Fravashi.Launcher;

/// <summary>
/// Bounded wait for the studio's <c>/healthz</c> between container start and opening the window, so
/// the first paint is the studio rather than the reconnect overlay. The probe delegate is injectable
/// for tests; the default uses one shared <see cref="HttpClient"/> with a short per-request timeout.
/// Failure is non-fatal by contract: the caller opens the window anyway and reconnect.js owns the
/// in-page waiting experience.
/// </summary>
public sealed class StudioReadinessProbe
{
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(2) };

    private readonly Func<string, CancellationToken, Task<bool>> _probe;

    public StudioReadinessProbe(Func<string, CancellationToken, Task<bool>>? probe = null)
        => _probe = probe ?? DefaultProbe;

    /// <summary>Polls <paramref name="baseUrl"/><c>/healthz</c> until success or <paramref name="timeout"/>.</summary>
    public async Task<bool> WaitUntilReadyAsync(
        string baseUrl, TimeSpan timeout, TimeSpan pollInterval, CancellationToken cancellationToken = default)
    {
        var url = $"{baseUrl.TrimEnd('/')}/healthz";
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await ProbeOnce(url, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private async Task<bool> ProbeOnce(string url, CancellationToken cancellationToken)
    {
        try
        {
            return await _probe(url, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            // Not up yet (connection refused, reset, or slow): keep polling until the deadline.
            return false;
        }
    }

    private static async Task<bool> DefaultProbe(string url, CancellationToken cancellationToken)
    {
        using var response = await SharedClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }
}
