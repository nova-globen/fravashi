using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.AgentRuntimes.Broker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fravashi.Infrastructure.AgentRuntimes.Broker;

/// <summary>
/// Keeps the minted broker token fresh so the agent CLI always reads a currently-valid bearer (plan 04 B3).
/// It asks <see cref="IBrokerTokenService.RefreshIfNeededAsync"/> to re-mint from the stored SSO identity when the token
/// is missing or near expiry. It is tier-gated at its core: when the broker is not active (the single-user default) the
/// refresher idles - it returns before the first step.
/// <para>
/// Backlog 235 (plan @60) changed WHEN it asks. It used to tick every <see cref="Interval"/> whatever happened, so a mint
/// that could not reach the broker waited five minutes for its next chance, and two missed ticks near expiry let the
/// token lapse. Now each step answers its own next wait (<see cref="NextWait"/>): during an outage, exactly until the
/// backoff's next attempt (15 s doubling to 2 minutes, <see cref="BrokerTokenHealth"/>), so the first attempt after the
/// broker comes back mints; otherwise until the renewal window opens, never longer than <see cref="Interval"/>. A refusal
/// is not on the backoff at all: the step holds it back (see <see cref="BrokerTokenService.RenewAsync"/>) and only a new
/// sign-in or the person's own Mint asks again.
/// </para>
/// </summary>
public sealed class BrokerTokenRefreshHostedService : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    // A step whose next time is already past (a slow mint) still pauses, so the loop cannot spin.
    private static readonly TimeSpan MinimumWait = TimeSpan.FromSeconds(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly BrokerOptions _broker;
    private readonly BrokerTokenHealth _health;
    private readonly TimeProvider _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger<BrokerTokenRefreshHostedService> _logger;

    public BrokerTokenRefreshHostedService(
        IServiceScopeFactory scopes,
        BrokerOptions broker,
        ILogger<BrokerTokenRefreshHostedService>? logger = null,
        BrokerTokenHealth? health = null,
        TimeProvider? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _scopes = scopes;
        _broker = broker ?? BrokerOptions.Disabled;
        _clock = clock ?? TimeProvider.System;
        _health = health ?? new BrokerTokenHealth(_clock);
        _delay = delay ?? ((wait, token) => Task.Delay(wait, _clock, token));
        _logger = logger ?? NullLogger<BrokerTokenRefreshHostedService>.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // In the single-user default there is nothing to refresh, ever; do not even spin the timer.
        if (!_broker.IsActive)
        {
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var wait = await StepAsync(stoppingToken).ConfigureAwait(false);
                await _delay(wait, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    /// <summary>One pass: renew if due and allowed, then answer how long to wait before the next pass.</summary>
    public async Task<TimeSpan> StepAsync(CancellationToken cancellationToken)
    {
        BrokerConnectionStatus? status = null;
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var broker = scope.ServiceProvider.GetRequiredService<IBrokerTokenService>();
            var result = await broker.RefreshIfNeededAsync(cancellationToken).ConfigureAwait(false);
            if (result is { Success: true })
            {
                _logger.LogInformation("Re-minted the broker token; valid until {ExpiresAt:o}.", result.ExpiresAt);
            }
            else if (result is { Success: false, Failure: not BrokerMintFailure.Refused })
            {
                // A refusal said its own warning once, when it happened; repeating it every pass would only be noise.
                _logger.LogWarning("A broker-token re-mint was due but failed: {Error}", result.Error);
            }

            status = broker.GetStatus();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The background broker-token refresh pass failed; it will retry.");
        }

        return NextWait(_clock.GetUtcNow(), _health.Outage, _health.Refusal is not null, status);
    }

    /// <summary>
    /// How long to wait before the next pass: until an outage's next attempt; else until the stored token's renewal
    /// window opens (<see cref="BrokerTokenService.RenewAhead"/> before its expiry); never longer than <see cref="Interval"/>
    /// and never shorter than a second. While a refusal stands it is the plain <see cref="Interval"/>: nothing is asked
    /// then, and the pass only looks for a new sign-in. A window that is already open but was not renewed (an answer that
    /// was no token) is looked at again after <see cref="BrokerTokenHealth.FirstRetry"/>, never in a tight loop.
    /// </summary>
    public static TimeSpan NextWait(DateTimeOffset now, BrokerOutage? outage, bool refused, BrokerConnectionStatus? status)
    {
        var wait = Interval;
        if (refused)
        {
            wait = Interval;
        }
        else if (outage is not null)
        {
            wait = outage.NextAttemptAt - now;
        }
        else if (status is { TokenValid: true, ExpiresAt: { } expires })
        {
            var opens = expires - BrokerTokenService.RenewAhead - now;
            wait = opens <= TimeSpan.Zero ? BrokerTokenHealth.FirstRetry : opens < Interval ? opens : Interval;
        }

        return wait < MinimumWait ? MinimumWait : wait > Interval ? Interval : wait;
    }
}
