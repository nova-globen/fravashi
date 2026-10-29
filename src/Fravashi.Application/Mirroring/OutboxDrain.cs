using Fravashi.Domain.Mirroring;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;

namespace Fravashi.Application.Mirroring;

/// <summary>What one pass of the queue did, so a caller can report it without counting rows again.</summary>
/// <param name="Sent">Writes that actually reached the provider.</param>
/// <param name="Confirmed">Writes the provider accepted.</param>
/// <param name="Requeued">Writes put back because the call never left, which is the outage path.</param>
/// <param name="Settled">Unknown writes resolved without a person.</param>
/// <param name="StillUnknown">Unknown writes nobody could settle; each is waiting for a person.</param>
/// <param name="Failed">Writes the provider refused for a reason that will not change.</param>
/// <param name="Blocked">Items whose queue could not move because of an entry ahead of it.</param>
public sealed record DrainReport(
    int Sent,
    int Confirmed,
    int Requeued,
    int Settled,
    int StillUnknown,
    int Failed,
    int Blocked)
{
    /// <summary>A pass that did nothing, which is the ordinary result of a drained queue.</summary>
    public static DrainReport Nothing { get; } = new(0, 0, 0, 0, 0, 0, 0);

    /// <summary>
    /// The items whose CREATION the provider confirmed in this pass. The reconcile pass that follows treats
    /// their status differently: the provider assigned it seconds ago, so there is nobody to ask about it.
    /// </summary>
    public IReadOnlySet<WorkItemId> Created { get; init; } = new HashSet<WorkItemId>();

    /// <summary>True when the provider could not be reached at all, so the product is in read-and-queue.</summary>
    public bool ProviderIsUnreachable => Requeued > 0 && Sent == 0;

    /// <summary>True when something in the queue is waiting for a person.</summary>
    public bool NeedsAttention => StillUnknown > 0 || Failed > 0;
}

/// <summary>
/// Drains the write outbox against one provider (ADR 0018 decisions 7 and 8). This is the piece that makes
/// "local-first" true rather than aspirational: writes are recorded locally and reconciled when the
/// provider is reachable, so losing the network degrades the product to read-and-queue instead of stopping
/// it.
/// <para>
/// The order of operations inside <see cref="SendAsync"/> is the load-bearing part, and it is the opposite
/// of what reads naturally. The entry is marked in flight and COMMITTED before the call is made, so the
/// only thing a crash can leave behind is an entry that says "we were about to do this". Marking it after
/// the call, which is the obvious way round, leaves a crash looking exactly like a write that never
/// happened, and that is the duplicate ADR 0018 decision 7 describes.
/// </para>
/// </summary>
public sealed class OutboxDrain
{
    private readonly IOutboxStore _outbox;
    private readonly IMirrorStore _mirror;
    private readonly TimeProvider _clock;

    /// <summary>Creates a drain over the durable queue and the mirroring sidecar.</summary>
    public OutboxDrain(IOutboxStore outbox, IMirrorStore mirror, TimeProvider? clock = null)
    {
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _mirror = mirror ?? throw new ArgumentNullException(nameof(mirror));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// What a restart has to do before anything else: nothing can legitimately be in flight at the moment
    /// this process starts, so every such entry is an unknown and is marked as one. Answers how many.
    /// </summary>
    public Task<int> RecoverAsync(CancellationToken ct = default) => _outbox.RecoverInFlightAsync(ct);

    /// <summary>
    /// One pass over a project's queue. Unknowns are settled first, because an unknown blocks its item and
    /// settling it is what lets the rest of that item's writes move. Then one write per item is sent: one,
    /// not all of them, because two calls about the same item would race over the same expected version.
    /// </summary>
    public async Task<DrainReport> DrainAsync(IProviderGateway gateway, ProjectId project, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(project);

        var open = await _outbox.OpenAsync(project, ct).ConfigureAwait(false);
        if (open.Count == 0)
        {
            return DrainReport.Nothing;
        }

        var sent = 0;
        var confirmed = 0;
        var requeued = 0;
        var settled = 0;
        var unknown = 0;
        var failed = 0;
        var blocked = 0;
        var created = new HashSet<WorkItemId>();

        foreach (var group in open.GroupBy(e => e.Item))
        {
            var entries = group.ToList();

            foreach (var entry in entries.Where(e => e.State == OutboxState.Unresolved))
            {
                var outcome = await SettleAsync(gateway, entry, ct).ConfigureAwait(false);
                switch (outcome)
                {
                    case SettleOutcome.Settled:
                        settled++;
                        break;
                    case SettleOutcome.Confirmed:
                        settled++;
                        confirmed++;
                        if (entry.Kind == OutboxKind.Create)
                        {
                            created.Add(group.Key);
                        }

                        break;
                    default:
                        unknown++;
                        break;
                }
            }

            // Re-read, because settling may have moved an entry out of the way of the one behind it.
            var current = await _outbox.ForItemAsync(project, group.Key, ct).ConfigureAwait(false);
            var next = OutboxDiscipline.NextToSend(current);
            if (next is null)
            {
                if (current.Any(e => e.NeedsAPerson))
                {
                    blocked++;
                }

                continue;
            }

            if (!gateway.Capabilities.CanWrite)
            {
                // Not a failure of this write: the connection is configured for reading. Saying so once per
                // entry is how a person finds out, rather than watching a queue that never moves.
                await _outbox.MarkAsync(
                    project,
                    next.Id,
                    OutboxState.Failed,
                    null,
                    $"{gateway.Connection} is configured for reading only, so this change cannot be sent.",
                    countAttempt: false,
                    _clock.GetUtcNow(),
                    ct).ConfigureAwait(false);
                failed++;
                continue;
            }

            var result = await SendAsync(gateway, next, ct).ConfigureAwait(false);
            switch (result)
            {
                case SendOutcome.Confirmed:
                    sent++;
                    confirmed++;
                    if (next.Kind == OutboxKind.Create)
                    {
                        created.Add(group.Key);
                    }

                    break;
                case SendOutcome.Requeued:
                    requeued++;
                    break;
                case SendOutcome.Unknown:
                    sent++;
                    unknown++;
                    break;
                default:
                    sent++;
                    failed++;
                    break;
            }
        }

        return new DrainReport(sent, confirmed, requeued, settled, unknown, failed, blocked) { Created = created };
    }

    private enum SettleOutcome
    {
        StillUnknown,
        Settled,
        Confirmed,
    }

    private enum SendOutcome
    {
        Confirmed,
        Requeued,
        Unknown,
        Failed,
    }

    /// <summary>
    /// Resolves one write whose outcome is unknown. An update or a transition needs no lookup at all
    /// (<see cref="OutboxDiscipline.CanBeSettledAutomatically"/> explains why) and simply goes back on the
    /// queue. A creation is looked up by the operation id stamped on it; only a definite "the provider does
    /// not have it" is treated as "it never landed", because every other answer is still an unknown.
    /// </summary>
    private async Task<SettleOutcome> SettleAsync(IProviderGateway gateway, OutboxEntry entry, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();

        if (!OutboxDiscipline.CanBeSettledAutomatically(entry, gateway.Capabilities.CanSearchByOperationId))
        {
            await _outbox.MarkAsync(
                entry.Project,
                entry.Id,
                OutboxState.Unresolved,
                null,
                OutboxDiscipline.UnresolvedSentence(entry, gateway.Connection),
                countAttempt: false,
                now,
                ct).ConfigureAwait(false);
            return SettleOutcome.StillUnknown;
        }

        if (entry.Kind != OutboxKind.Create)
        {
            await _outbox.MarkAsync(entry.Project, entry.Id, OutboxState.Pending, null, null, countAttempt: false, now, ct)
                .ConfigureAwait(false);
            return SettleOutcome.Settled;
        }

        var found = await gateway.FindByOperationIdAsync(entry.Id, ct).ConfigureAwait(false);
        if (!found.IsOk)
        {
            // We asked and did not get an answer. That is still an unknown, and pretending otherwise here
            // would be exactly the shortcut this whole path exists to refuse.
            return SettleOutcome.StillUnknown;
        }

        if (found.Value is { } item)
        {
            await ConfirmAsync(gateway, entry, item, ct).ConfigureAwait(false);
            return SettleOutcome.Confirmed;
        }

        // A definite no. The creation never landed, so sending it again cannot duplicate anything.
        await _outbox.MarkAsync(entry.Project, entry.Id, OutboxState.Pending, null, null, countAttempt: false, now, ct)
            .ConfigureAwait(false);
        return SettleOutcome.Settled;
    }

    private async Task<SendOutcome> SendAsync(IProviderGateway gateway, OutboxEntry entry, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();

        // Committed BEFORE the call. Everything in this class depends on this line being first.
        await _outbox.MarkAsync(entry.Project, entry.Id, OutboxState.InFlight, null, null, countAttempt: true, now, ct)
            .ConfigureAwait(false);

        var key = await KeyForAsync(entry, ct).ConfigureAwait(false);
        if (entry.Kind != OutboxKind.Create && key is null)
        {
            await _outbox.MarkAsync(
                entry.Project,
                entry.Id,
                OutboxState.Failed,
                null,
                "This change is for an item that has no key on the provider yet, so there is nothing to change there.",
                countAttempt: false,
                now,
                ct).ConfigureAwait(false);
            return SendOutcome.Failed;
        }

        var state = await _mirror.GetAsync(entry.Project, entry.Item, ct).ConfigureAwait(false);

        var result = entry.Kind switch
        {
            OutboxKind.Create => await gateway.CreateAsync(entry.Payload, entry.Id, ct).ConfigureAwait(false),
            OutboxKind.Update => await gateway.UpdateAsync(key!, entry.Payload, state?.ProviderVersion, entry.Id, ct).ConfigureAwait(false),
            _ => await gateway.TransitionAsync(key!, entry.Payload, state?.ProviderVersion, entry.Id, ct).ConfigureAwait(false),
        };

        var finished = _clock.GetUtcNow();

        switch (result.Outcome)
        {
            case ProviderOutcome.Ok:
                await ConfirmAsync(gateway, entry, result.Value!, ct).ConfigureAwait(false);
                return SendOutcome.Confirmed;

            case ProviderOutcome.Unreachable:
                // The call provably never left, so nothing happened there and this is safe to send again.
                // This is the whole outage story: the change is not lost, the product keeps working.
                await _outbox.MarkAsync(entry.Project, entry.Id, OutboxState.Pending, null, result.Reason, countAttempt: false, finished, ct)
                    .ConfigureAwait(false);
                return SendOutcome.Requeued;

            case ProviderOutcome.Indeterminate:
                // The call left and nothing came back. Identical to a crash, handled identically.
                await _outbox.MarkAsync(entry.Project, entry.Id, OutboxState.Unresolved, null, result.Reason, countAttempt: false, finished, ct)
                    .ConfigureAwait(false);
                return SendOutcome.Unknown;

            case ProviderOutcome.Gone:
                await _mirror.TombstoneAsync(
                    entry.Project,
                    entry.Item,
                    result.Reason ?? $"{gateway.Connection} answered that it no longer has this item.",
                    finished,
                    ct).ConfigureAwait(false);
                await _outbox.MarkAsync(
                    entry.Project,
                    entry.Id,
                    OutboxState.Failed,
                    null,
                    $"{gateway.Connection} no longer has this item, so the change has nowhere to go.",
                    countAttempt: false,
                    finished,
                    ct).ConfigureAwait(false);
                return SendOutcome.Failed;

            default:
                await _outbox.MarkAsync(entry.Project, entry.Id, OutboxState.Failed, null, result.Reason, countAttempt: false, finished, ct)
                    .ConfigureAwait(false);
                return SendOutcome.Failed;
        }
    }

    private async Task ConfirmAsync(IProviderGateway gateway, OutboxEntry entry, ProviderItem item, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();

        // No base fields: a write's response says what the provider now holds for the fields the write
        // touched, not for all of them, and recording a partial answer as the base would make every field
        // it did not mention look moved on the next comparison.
        await _mirror.ConfirmAsync(
            entry.Project,
            entry.Item,
            new ProviderConfirmation(gateway.Connection, item.Key, item.ProviderVersion, entry.ExpectedVersion),
            now,
            ct).ConfigureAwait(false);

        await _outbox.MarkAsync(entry.Project, entry.Id, OutboxState.Confirmed, item.Key, null, countAttempt: false, now, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The provider key for an item: the mirror's if it has one, otherwise the key a creation earlier in
    /// this item's queue came back with. The second case is the one that matters, because a creation and
    /// the update behind it can drain in the same pass.
    /// </summary>
    private async Task<string?> KeyForAsync(OutboxEntry entry, CancellationToken ct)
    {
        var state = await _mirror.GetAsync(entry.Project, entry.Item, ct).ConfigureAwait(false);
        if (state is not null)
        {
            return state.Key;
        }

        var entries = await _outbox.ForItemAsync(entry.Project, entry.Item, ct).ConfigureAwait(false);
        return entries
            .Where(e => e.State == OutboxState.Confirmed && e.ResultKey is { Length: > 0 })
            .OrderByDescending(e => e.QueuedUtc)
            .Select(e => e.ResultKey)
            .FirstOrDefault();
    }
}
