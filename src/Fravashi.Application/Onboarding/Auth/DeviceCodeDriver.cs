namespace Fravashi.Application.Onboarding.Auth;

/// <summary>
/// Drives a device-code poll loop to a terminal state, honouring the provider's interval, GitHub's
/// <c>slow_down</c> back-off, and the overall device-code expiry. It is deliberately HTTP-free and
/// clock-free: the caller supplies the single-poll function and a delay function, so the loop is unit-tested
/// with a scripted sequence of results and a no-op delay (no real waiting, no live IdP).
/// <para>
/// The wizard uses <see cref="RunAsync"/> on a background task and reports each transition through the
/// <paramref name="onPoll"/> callback so the UI can show "waiting for authorization…" live.
/// </para>
/// </summary>
public static class DeviceCodeDriver
{
    /// <summary>
    /// Polls until success, denial, expiry, error, or the deadline. <paramref name="pollOnce"/> performs one
    /// token-endpoint call; <paramref name="delay"/> waits the given seconds (real <c>Task.Delay</c> in
    /// production, a no-op in tests). <paramref name="onPoll"/>, when supplied, is invoked after every poll.
    /// </summary>
    public static async Task<DevicePollResult> RunAsync(
        DeviceCodeStart start,
        Func<string, CancellationToken, Task<DevicePollResult>> pollOnce,
        Func<int, CancellationToken, Task> delay,
        DateTimeOffset startedUtc,
        Func<DateTimeOffset> now,
        Action<DevicePollResult>? onPoll = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(pollOnce);
        ArgumentNullException.ThrowIfNull(delay);
        ArgumentNullException.ThrowIfNull(now);

        var interval = Math.Max(1, start.IntervalSeconds);
        var deadline = startedUtc.AddSeconds(start.ExpiresInSeconds);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (now() >= deadline)
            {
                var expired = DevicePollResult.Expired("The sign-in request expired before it was authorized. Start it again.");
                onPoll?.Invoke(expired);
                return expired;
            }

            await delay(interval, cancellationToken).ConfigureAwait(false);

            var result = await pollOnce(start.DeviceCode, cancellationToken).ConfigureAwait(false);
            onPoll?.Invoke(result);

            var (nextInterval, keepGoing) = DeviceCodePolling.Next(interval, result.State);
            interval = nextInterval;
            if (!keepGoing)
            {
                return result;
            }
        }
    }
}
