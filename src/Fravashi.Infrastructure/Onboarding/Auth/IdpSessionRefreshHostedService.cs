using Fravashi.Application.Network;
using Fravashi.Infrastructure.Network;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fravashi.Infrastructure.Onboarding.Auth;

/// <summary>
/// Keeps the IdP session fresh (plan Phase 3): refresh the envelope when it is expiring. A REFUSED refresh is left
/// alone on purpose: the envelope expires, the sign-in gate closes, and the person signs in again. Never throws.
/// <para>
/// Backlog 234 (plan @64) changed WHEN it asks, in the shape backlog 235 gave the broker's refresher. It used to tick
/// every <see cref="Interval"/> whatever happened. Now each step answers its own next wait (<see cref="NextWait"/>):
/// while the identity provider cannot be reached, until the backoff's next attempt (15 s doubling to 2 minutes,
/// <see cref="IdpSessionGrace"/>) or the next reachability probe, whichever comes first; otherwise until the refresh
/// window opens, never longer than <see cref="Interval"/>. Between attempts it probes the ISSUER's host (slice A's
/// <see cref="NetworkReachability"/>, its own instance: the one #229 registered aims at the model API), and when that
/// probe goes from unreachable to reachable the refresh is asked at once, outside the backoff.
/// </para>
/// </summary>
public sealed class IdpSessionRefreshHostedService : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    // A step whose next time is already past still pauses, so the loop cannot spin.
    private static readonly TimeSpan MinimumWait = TimeSpan.FromSeconds(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<IdpSessionRefreshHostedService> _logger;
    private readonly IdpSessionGrace _grace;
    private readonly TimeProvider _clock;
    private readonly INetworkConnector _connector;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private NetworkReachability? _probe;

    public IdpSessionRefreshHostedService(
        IServiceScopeFactory scopes,
        ILogger<IdpSessionRefreshHostedService>? logger = null,
        IdpSessionGrace? grace = null,
        TimeProvider? clock = null,
        INetworkConnector? connector = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _scopes = scopes;
        _logger = logger ?? NullLogger<IdpSessionRefreshHostedService>.Instance;
        _clock = clock ?? TimeProvider.System;
        _grace = grace ?? new IdpSessionGrace(_clock);
        _connector = connector ?? new SocketNetworkConnector();
        _delay = delay ?? ((wait, token) => Task.Delay(wait, _clock, token));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
            // Shutdown.
        }
    }

    /// <summary>One pass: probe or renew as the outage allows, then answer how long to wait before the next pass.</summary>
    public async Task<TimeSpan> StepAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset? expires = null;
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var idp = scope.ServiceProvider.GetRequiredService<IdpAuthService>();
            if (!idp.IsConfigured)
            {
                return Interval;
            }

            expires = scope.ServiceProvider.GetRequiredService<IdpSessionState>().ExpiresUtc;
            if (_grace.BackingOff(_clock.GetUtcNow()))
            {
                if (!await IssuerCameBackAsync(idp.IssuerEndpoint, cancellationToken).ConfigureAwait(false))
                {
                    return NextWait(_clock.GetUtcNow(), _grace.Outage, _grace.Refusal is not null, expires, _probe?.Current.NextProbeAt);
                }

                _logger.LogInformation("The identity provider's host answers again; renewing the sign-in now.");
            }

            var result = await idp.RefreshAsync(force: true, cancellationToken).ConfigureAwait(false);
            expires = scope.ServiceProvider.GetRequiredService<IdpSessionState>().ExpiresUtc;
            Report(result, expires);
            if (_grace.Outage is null)
            {
                // The next outage starts its probe afresh, so an old "unreachable" cannot fake a recovery.
                _probe = null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("The sign-in refresh pass failed ({Kind}); the next pass retries.", ex.GetType().Name);
        }

        return NextWait(_clock.GetUtcNow(), _grace.Outage, _grace.Refusal is not null, expires, _probe?.Current.NextProbeAt);
    }

    /// <summary>
    /// How long to wait before the next pass: during an outage until its next attempt or the next probe, whichever is
    /// first; while a refusal stands, the plain <see cref="Interval"/>; otherwise until the session's refresh window
    /// opens. Never longer than <see cref="Interval"/>, never shorter than a second.
    /// </summary>
    public static TimeSpan NextWait(
        DateTimeOffset now, IdpOutage? outage, bool refused, DateTimeOffset? expires, DateTimeOffset? nextProbe)
    {
        var wait = Interval;
        if (refused)
        {
            wait = Interval;
        }
        else if (outage is not null)
        {
            wait = outage.NextAttemptAt - now;
            var probe = (nextProbe ?? now + NetworkReachability.FirstRetry) - now;
            if (probe < wait)
            {
                wait = probe;
            }
        }
        else if (expires is { } expiry)
        {
            var opens = expiry - IdpAuthService.RefreshAhead - now;
            wait = opens <= TimeSpan.Zero ? IdpSessionGrace.FirstRetry : opens;
        }

        return wait < MinimumWait ? MinimumWait : wait > Interval ? Interval : wait;
    }

    // Steps the probe once its own next time has come, and answers true only on the step where it goes from
    // unreachable to reachable: a host that answers TCP while the IdP says 503 stays on the backoff.
    private async Task<bool> IssuerCameBackAsync(NetworkEndpoint? issuer, CancellationToken cancellationToken)
    {
        if (issuer is null)
        {
            return false;
        }

        if (_probe is null || _probe.Current.Target != issuer)
        {
            _probe = new NetworkReachability(issuer, _connector, _clock);
        }

        if (_probe.Current.NextProbeAt is { } due && _clock.GetUtcNow() < due)
        {
            return false;
        }

        var before = _probe.Current.Reachable;
        await _probe.StepAsync(cancellationToken).ConfigureAwait(false);
        return before == false && _probe.Current.Reachable == true;
    }

    // Reasons are built from a host and plain words, never from a token, so they are safe to log.
    private void Report(IdpRefreshResult result, DateTimeOffset? expires)
    {
        switch (result.Outcome)
        {
            case IdpRefreshOutcome.Renewed:
                _logger.LogInformation("Renewed the sign-in session; valid until {ExpiresAt:o}.", expires);
                break;
            case IdpRefreshOutcome.Unreachable:
                _logger.LogWarning(
                    "The sign-in could not be renewed: {Reason}. Signed in until {LocksAt:o}; next attempt at {NextAttemptAt:o}.",
                    result.Reason, _grace.LocksAt(expires), _grace.Outage?.NextAttemptAt);
                break;
            case IdpRefreshOutcome.Refused:
                _logger.LogWarning("The identity provider refused to renew the sign-in: {Reason}.", result.Reason);
                break;
        }
    }
}
