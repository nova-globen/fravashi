using Fravashi.Application.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fravashi.Infrastructure.Notifications;

/// <summary>
/// Polls <see cref="IWaitingForPersonList"/> on an interval and raises a toast through
/// <see cref="IWaitingChannel"/> for each NEW entry (ADR 0031 decision 12, backlog 304), the same shape
/// <c>AutopilotEngineHost</c> already uses: a <see cref="PeriodicTimer"/>, a failed poll logged and skipped
/// rather than ending the loop, and an internal <see cref="PollOnceAsync"/> tests call directly instead of
/// waiting on the timer. Registered unconditionally: a channel with no bridge configured is a safe no-op
/// (<see cref="WindowsToastWaitingChannel"/>'s own posture), the same way <c>BridgeReachHostedService</c>
/// already runs in every test host that builds the full DI graph.
/// </summary>
public sealed class WaitingNotificationHost : BackgroundService
{
    /// <summary>How often the host looks for newly waiting entries.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(10);

    private readonly IWaitingForPersonList _list;
    private readonly IReadOnlyList<IWaitingChannel> _channels;
    private readonly TimeSpan _interval;
    private readonly ILogger _logger;
    private HashSet<string> _previousKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// Every <paramref name="channels"/> is notified of each new entry, so a later channel (phone push, email,
    /// Telegram, Slack, Teams, docs/v2.md) is added by registering a second <see cref="IWaitingChannel"/>, not
    /// by widening this constructor again.
    /// </summary>
    public WaitingNotificationHost(
        IWaitingForPersonList list,
        IEnumerable<IWaitingChannel> channels,
        TimeSpan? interval = null,
        ILogger<WaitingNotificationHost>? logger = null)
    {
        _list = list ?? throw new ArgumentNullException(nameof(list));
        ArgumentNullException.ThrowIfNull(channels);
        _channels = [.. channels];
        _interval = interval ?? DefaultInterval;
        _logger = logger ?? NullLogger<WaitingNotificationHost>.Instance;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_interval);
            do
            {
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>One poll: read, diff against the last poll, and notify for each new entry. Internal for tests.</summary>
    internal async Task PollOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<WaitingForPersonEntry> current;
        try
        {
            current = await _list.CurrentAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read what is waiting on a person.");
            return;
        }

        var diff = WaitingNotificationTracker.Diff(_previousKeys, current);
        _previousKeys = [.. diff.NextKeys];

        foreach (var entry in diff.NewEntries)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            foreach (var channel in _channels)
            {
                try
                {
                    await channel.NotifyAsync(entry, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Raising a waiting toast for {Kind} through {Channel} failed.", entry.Kind, channel.GetType().Name);
                }
            }
        }
    }
}
