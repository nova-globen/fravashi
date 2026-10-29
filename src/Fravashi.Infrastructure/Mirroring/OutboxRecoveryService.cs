using Fravashi.Application.Mirroring;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Fravashi.Domain;

namespace Fravashi.Infrastructure.Mirroring;

/// <summary>
/// Settles, when the studio starts, every provider write the previous process left in flight (backlog 146).
/// <para>
/// Backlog 82's first rule commits an outbox entry as in flight BEFORE its call leaves, so a crash can only leave an
/// entry saying "we were about to do this". That entry has to become an unknown the next drain looks up by its operation
/// id; left in flight, it blocked every later write to its item for ever, because the drain sends nothing for an item
/// with a write in flight. The recovery existed and only the tests called it. It runs here, once, at start, and not
/// before each drain: at start nothing can legitimately be in flight, whereas a drain running in another window at the
/// same moment can.
/// </para>
/// </summary>
public sealed class OutboxRecoveryService(OutboxDrain drain, ILogger<OutboxRecoveryService>? logger = null) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var recovered = await drain.RecoverAsync(cancellationToken).ConfigureAwait(false);
        if (recovered > 0)
        {
            logger?.LogWarning(
                $"{{Count}} provider write(s) were in flight when {ProductName.Short} stopped; each will be looked up by its operation id at the next sync before anything is sent.",
                recovered);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
