namespace Fravashi.Launcher;

/// <summary>What <see cref="HostDnsWatch.Evaluate"/> decided for one observation.</summary>
public enum DnsDriftDecision
{
    /// <summary>The current DNS servers match the baseline; nothing is wrong.</summary>
    Unchanged,

    /// <summary>They differ, but not for long enough yet - a flapping VPN must not restart anything.</summary>
    Debouncing,

    /// <summary>The same new set has held for the whole debounce window: act now.</summary>
    Act,
}

/// <summary>
/// The debounce decision behind backlog 218: whether the host's DNS servers have drifted from a known
/// baseline for long enough to act on it. Deliberately pure and clock-injected (an explicit
/// <see cref="DateTimeOffset"/> on every call, no timer inside it), so a flapping network - the VPN
/// reconnecting a few times a minute - is provable without waiting in real time: only a SINGLE value held
/// continuously for the whole window counts, and seeing the baseline again, or a second different value
/// mid-window, restarts the clock rather than accumulating toward a restart.
/// </summary>
public sealed class HostDnsWatch(TimeSpan debounce)
{
    /// <summary>
    /// Three polls at the default 15s interval (<see cref="StudioDnsRecovery.DefaultPollInterval"/>): long
    /// enough that a VPN reconnect's few-second blip never counts as "held," short enough that a genuine
    /// change still recovers in under a minute without anyone restarting the product by hand.
    /// </summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(45);

    private IReadOnlyList<string>? _pendingValue;
    private DateTimeOffset _pendingSinceUtc;

    /// <summary>
    /// Call once per poll with the CURRENT DNS servers. <paramref name="baseline"/> is what the container was
    /// last actually started with; it never changes between calls here - the caller re-reads it fresh every
    /// time so a baseline updated elsewhere (a normal restart, or <see cref="StudioDnsRecovery"/> acting) is
    /// always compared against, never a stale copy this instance cached.
    /// </summary>
    public DnsDriftDecision Evaluate(IReadOnlyList<string> baseline, IReadOnlyList<string> current, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);

        if (SameSet(baseline, current))
        {
            _pendingValue = null;
            return DnsDriftDecision.Unchanged;
        }

        if (_pendingValue is null || !SameSet(_pendingValue, current))
        {
            // First sight of this value, or it changed again before the window elapsed: the window restarts.
            _pendingValue = current;
            _pendingSinceUtc = now;
            return DnsDriftDecision.Debouncing;
        }

        return now - _pendingSinceUtc >= debounce ? DnsDriftDecision.Act : DnsDriftDecision.Debouncing;
    }

    /// <summary>Clears the pending candidate, e.g. once <see cref="DnsDriftDecision.Act"/> has been acted on.</summary>
    public void Reset() => _pendingValue = null;

    private static bool SameSet(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.ToHashSet(StringComparer.Ordinal).SetEquals(b);
}
