using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Fravashi.Web.Services;

/// <summary>
/// Whether this circuit's window is connected, and a signal each time it connects again (backlog 264). A JavaScript call
/// made while the window is disconnected is not replayed when it reconnects (measured on v0.9.69), so a page that sends one needs to know when to send it again.
///
/// <para>
/// Registered scoped as itself AND as the <see cref="CircuitHandler"/> by forwarding to that one instance (Program.cs):
/// the server builds a circuit's handlers from the circuit's own scope, the scope pages are injected from, so the
/// handler the circuit calls and the object a page subscribes to are the same. Registering the type twice would make
/// two instances and the page would hear nothing.
/// </para>
///
/// <para>
/// The circuit's first connection-up happens before any page exists, so a subscriber hears reconnections only.
/// </para>
/// </summary>
public sealed class CircuitConnection : CircuitHandler
{
    private volatile bool _isUp = true;

    /// <summary>
    /// Raised after the window has connected again. Which thread raises it is not measured here, so a subscriber must not
    /// assume the renderer's dispatcher and marshals with InvokeAsync (the Code page does).
    /// </summary>
    public event Action? Reconnected;

    /// <summary>
    /// False between a connection-down and the next connection-up. True before the first signal: a page runs only in a
    /// circuit whose window has connected.
    /// </summary>
    public bool IsUp => _isUp;

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        ConnectionUp();
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        ConnectionDown();
        return Task.CompletedTask;
    }

    /// <summary>What <see cref="OnConnectionUpAsync"/> does; public so a component test can model a reconnection.</summary>
    public void ConnectionUp()
    {
        _isUp = true;
        Reconnected?.Invoke();
    }

    /// <summary>What <see cref="OnConnectionDownAsync"/> does; public so a component test can model a disconnection.</summary>
    public void ConnectionDown() => _isUp = false;
}
