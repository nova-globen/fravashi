using Fravashi.Domain.Telemetry;

namespace Fravashi.Application.Telemetry;

/// <summary>
/// Where a product event goes (backlog 74). One implementation writes to the studio's own SQLite
/// database and only while the opt-in switch is on; <see cref="Off"/> is the honest no-op for a host
/// that records nothing at all.
/// <para>
/// <b>Local only.</b> There is no remote destination behind this interface and none is added by it:
/// the recorded events live in the same database as everything else the install owns, and the only
/// way they leave the machine is the user exporting a diagnostic bundle and sending it themselves.
/// </para>
/// <para>
/// Recording must never be able to break the thing it is measuring, so implementations swallow their
/// own failures. A producer calls <see cref="Record"/> unconditionally and does not ask
/// <see cref="IsEnabled"/> first: the switch is checked in one place, which is what keeps a new
/// producer from accidentally being the one that forgets.
/// </para>
/// </summary>
public interface IProductEventRecorder
{
    /// <summary>True when the opt-in switch is on and events are actually being written.</summary>
    bool IsEnabled { get; }

    /// <summary>Records an event, or does nothing at all when the switch is off.</summary>
    void Record(ProductEvent productEvent);

    /// <summary>A recorder that records nothing, for hosts and tests that want no telemetry seam.</summary>
    public static IProductEventRecorder Off { get; } = new NoRecorder();

    private sealed class NoRecorder : IProductEventRecorder
    {
        public bool IsEnabled => false;

        public void Record(ProductEvent productEvent)
        {
        }
    }
}
