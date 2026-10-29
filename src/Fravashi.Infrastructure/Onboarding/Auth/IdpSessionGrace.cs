namespace Fravashi.Infrastructure.Onboarding.Auth;

/// <summary>What one refresh of the sign-in session came to (backlog 234, plan @64).</summary>
public enum IdpRefreshOutcome
{
    /// <summary>The session is not due for renewal yet; nothing was asked.</summary>
    Fresh,

    /// <summary>The identity provider renewed the session.</summary>
    Renewed,

    /// <summary>There is no session, or it carries no refresh token: nothing could be asked.</summary>
    NoSession,

    /// <summary>
    /// The identity provider could not be reached (slice A's <c>NetworkFailure</c> says so), or answered that it cannot
    /// serve now (5xx, 408, 429). The one outcome that opens the grace.
    /// </summary>
    Unreachable,

    /// <summary>The identity provider answered and did not renew (invalid_grant and friends, or no usable token). Locks as before 234.</summary>
    Refused,
}

/// <summary>A refresh's outcome with its reason in plain words (never a token).</summary>
public sealed record IdpRefreshResult(IdpRefreshOutcome Outcome, string? Reason = null)
{
    /// <summary>True when the session is fresh or was renewed.</summary>
    public bool Healthy => Outcome is IdpRefreshOutcome.Fresh or IdpRefreshOutcome.Renewed;
}

/// <summary>The identity provider could not be reached for a refresh, since when, why, and when the next attempt is due.</summary>
public sealed record IdpOutage(DateTimeOffset Since, string Reason, DateTimeOffset NextAttemptAt);

/// <summary>A refresh the identity provider refused, and what it said.</summary>
public sealed record IdpRefusal(DateTimeOffset At, string Words);

/// <summary>
/// The sign-in session's network health for this studio process (backlog 234, plan @64): an outage with its backoff, or
/// a refusal. One per process, shared by the refresh, the background loop, the gate and every page that shows it.
/// <para>
/// While an outage is recorded and nothing was refused, a session whose expiry has passed stays usable for
/// <see cref="Length"/> past that expiry (ruling 2026-10-07: one hour). The grace is anchored on the session's
/// expiry, never on the outage, so failing again cannot stretch it. One hour is shorter than the broker token's own
/// hour on purpose: a renewed access token is what makes #235's identity_rejected branch ask the broker again, and that
/// must happen before the sign-in itself lapses.
/// </para>
/// <para>
/// Kept in memory, as <c>BrokerTokenHealth</c> is: after a restart the first refresh finds the outage again. Nothing
/// here holds a token.
/// </para>
/// </summary>
public sealed class IdpSessionGrace(TimeProvider? clock = null)
{
    /// <summary>How long past the session's expiry a network-failed refresh keeps the person signed in (ruling: one hour).</summary>
    public static readonly TimeSpan Length = TimeSpan.FromHours(1);

    /// <summary>The first wait after a refresh that could not reach the identity provider (15 seconds, as the broker's).</summary>
    public static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(15);

    /// <summary>The longest wait between attempts; the wait doubles from <see cref="FirstRetry"/> up to this (2 minutes).</summary>
    public static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(2);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private IdpOutage? _outage;
    private IdpRefusal? _refusal;
    private TimeSpan _nextRetry = FirstRetry;

    /// <summary>Raised when the outage or the refusal begins, ends or changes.</summary>
    public event Action? Changed;

    /// <summary>The current outage, or null while the identity provider answers.</summary>
    public IdpOutage? Outage
    {
        get
        {
            lock (_gate)
            {
                return _outage;
            }
        }
    }

    /// <summary>The last refusal, or null when nothing was refused since the last renewal or sign-in.</summary>
    public IdpRefusal? Refusal
    {
        get
        {
            lock (_gate)
            {
                return _refusal;
            }
        }
    }

    /// <summary>When a session expiring at <paramref name="expiresUtc"/> locks while an outage holds; null when no grace applies.</summary>
    public DateTimeOffset? LocksAt(DateTimeOffset? expiresUtc)
    {
        lock (_gate)
        {
            return _outage is not null && _refusal is null && expiresUtc is { } expiry ? expiry + Length : null;
        }
    }

    /// <summary>True while a session that expired at <paramref name="expiresUtc"/> is still inside its grace at <paramref name="now"/>.</summary>
    public bool Admits(DateTimeOffset now, DateTimeOffset? expiresUtc) => LocksAt(expiresUtc) is { } locks && now < locks;

    /// <summary>True while an outage's next attempt is not due yet.</summary>
    public bool BackingOff(DateTimeOffset now) => Outage is { } down && now < down.NextAttemptAt;

    /// <summary>A refresh could not reach the identity provider: the outage starts or goes on, and the next attempt is put back on the backoff.</summary>
    public IdpOutage Unreachable(string reason)
    {
        var now = _clock.GetUtcNow();
        IdpOutage outage;
        bool began;
        lock (_gate)
        {
            began = _outage is null;
            outage = new IdpOutage(_outage?.Since ?? now, reason, now + _nextRetry);
            _outage = outage;
            _nextRetry = TimeSpan.FromTicks(Math.Min(_nextRetry.Ticks * 2, MaxRetry.Ticks));
        }

        if (began)
        {
            Changed?.Invoke();
        }

        return outage;
    }

    /// <summary>The identity provider refused: any outage and its grace end at once, and the words stand until a renewal or a sign-in.</summary>
    public void Refuse(string words)
    {
        lock (_gate)
        {
            _outage = null;
            _nextRetry = FirstRetry;
            _refusal = new IdpRefusal(_clock.GetUtcNow(), words);
        }

        Changed?.Invoke();
    }

    /// <summary>The session was renewed, or a new one was signed in or cleared: the outage and the refusal end silently.</summary>
    public void Renewed()
    {
        bool changed;
        lock (_gate)
        {
            changed = _outage is not null || _refusal is not null;
            _outage = null;
            _refusal = null;
            _nextRetry = FirstRetry;
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }
}
