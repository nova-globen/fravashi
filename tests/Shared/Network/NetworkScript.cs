namespace Fravashi.Testing.Network;

// Backlog 228 (plan @49): the faked network every slice of 219 tests against. This folder is SOURCE shared by
// linking (<Compile Include="..\Shared\Network\*.cs" LinkBase="Shared\Network" />), the same idiom the test projects
// already use for fixtures, so it needs no project of its own and no package. NetworkScript, ScriptedShapes and
// ScriptedHttpHandler need only the BCL (a launcher test can link them); ScriptedConnector needs Fravashi.Application.

/// <summary>A clock the test moves by hand. Nothing in the faked network waits in real time.</summary>
public sealed class FakeNetworkClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public FakeNetworkClock()
        : this(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero))
    {
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>What the faked network does with one call.</summary>
public enum NetworkBehaviour
{
    /// <summary>The call goes through.</summary>
    Healthy,

    /// <summary>The name cannot be looked up (a dead resolver, F1).</summary>
    NameFails,

    /// <summary>The connection is refused.</summary>
    Refuses,

    /// <summary>No route: the link is down (F3, F4) or the VPN is between tunnels (F2).</summary>
    NoRoute,

    /// <summary>The connection breaks before the request is written.</summary>
    ResetBeforeSend,

    /// <summary>The whole request arrives, then the connection is reset before any answer (an open connection on F2).</summary>
    ResetAfterWrite,

    /// <summary>The request was in flight when the link went down: no answer until the client's own timeout.</summary>
    Hangs,
}

/// <summary>
/// One phase of a script. It ends when <paramref name="For"/> has passed since it began or when <paramref name="Calls"/>
/// calls have been played, whichever is given; the last phase of a script lasts for ever.
/// </summary>
public sealed record NetworkPhase(NetworkBehaviour Behaviour, TimeSpan? For = null, int? Calls = null);

/// <summary>
/// A fault played on a <see cref="FakeNetworkClock"/>: an ordered list of phases, starting when the script is made.
/// A phase that ends by its calls hands over at the moment of the call that used it up, so after F3's one hung call
/// the thirty seconds without a route start at the same instant the call was made, which is when the link went down.
/// The four faults of plan @48 are built by <see cref="F1"/> to <see cref="F4"/>.
/// </summary>
public sealed class NetworkScript
{
    private readonly FakeNetworkClock _clock;
    private readonly NetworkPhase[] _phases;
    private readonly object _lock = new();
    private int _index;
    private int _callsInPhase;
    private DateTimeOffset _phaseStart;

    public NetworkScript(FakeNetworkClock clock, params NetworkPhase[] phases)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(phases);
        if (phases.Length == 0)
        {
            throw new ArgumentException("A script needs at least one phase.", nameof(phases));
        }

        _clock = clock;
        _phases = phases;
        _phaseStart = clock.GetUtcNow();
    }

    /// <summary>The clock the script is played on.</summary>
    public FakeNetworkClock Clock => _clock;

    /// <summary>F1, a DNS change: names fail for 60 seconds, then everything works.</summary>
    public static NetworkScript F1(FakeNetworkClock clock) => new(clock,
        new(NetworkBehaviour.NameFails, For: TimeSpan.FromSeconds(60)),
        new(NetworkBehaviour.Healthy));

    /// <summary>
    /// F2, a VPN reconnect: the open connection is reset after its request was written, then 15 seconds with no
    /// route, then names fail until 45 seconds in (the resolver moved), then everything works.
    /// </summary>
    public static NetworkScript F2(FakeNetworkClock clock) => new(clock,
        new(NetworkBehaviour.ResetAfterWrite, Calls: 1),
        new(NetworkBehaviour.NoRoute, For: TimeSpan.FromSeconds(15)),
        new(NetworkBehaviour.NameFails, For: TimeSpan.FromSeconds(30)),
        new(NetworkBehaviour.Healthy));

    /// <summary>F3, the link down for 30 seconds: the call in flight hangs, every new one has no route.</summary>
    public static NetworkScript F3(FakeNetworkClock clock) => LinkDown(clock, TimeSpan.FromSeconds(30));

    /// <summary>F4, the link down for 5 minutes, long enough to outlast every built in retry.</summary>
    public static NetworkScript F4(FakeNetworkClock clock) => LinkDown(clock, TimeSpan.FromMinutes(5));

    private static NetworkScript LinkDown(FakeNetworkClock clock, TimeSpan down) => new(clock,
        new(NetworkBehaviour.Hangs, Calls: 1),
        new(NetworkBehaviour.NoRoute, For: down),
        new(NetworkBehaviour.Healthy));

    /// <summary>Plays one call: what the network does with a call made now.</summary>
    public NetworkBehaviour Next()
    {
        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            while (true)
            {
                var phase = _phases[_index];
                var last = _index == _phases.Length - 1;
                if (!last && phase.For is { } length && now >= _phaseStart + length)
                {
                    Move(_phaseStart + length);
                    continue;
                }

                if (!last && phase.Calls is { } calls && ++_callsInPhase >= calls)
                {
                    Move(now);
                }

                return phase.Behaviour;
            }
        }
    }

    private void Move(DateTimeOffset start)
    {
        _index++;
        _callsInPhase = 0;
        _phaseStart = start;
    }
}
