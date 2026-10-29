using System.Text.Json;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Mirroring;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;

namespace Fravashi.Application.Mirroring;

/// <summary>
/// Which provider a project's board follows, if any (backlog 47). Null is an ordinary answer and not a fault:
/// with no provider the local board IS the source of truth (ADR 0018 decision 1).
/// </summary>
public interface IProviderGateways
{
    /// <summary>The gateway for <paramref name="project"/>, or null when it follows no provider.</summary>
    IProviderGateway? For(ProjectId project);
}

/// <summary>What became of a move this board made, once the provider was considered (backlog 47/48).</summary>
public enum ProviderMoveOutcome
{
    /// <summary>Nothing to send: the project follows no provider, or the item is not on it.</summary>
    NothingToSend,

    /// <summary>Queued. The next sync tells the provider, in the name this board mapped for it.</summary>
    Queued,

    /// <summary>The move is ambiguous on the other side, and which state is meant is the person's to say.</summary>
    NeedsAChoice,
}

/// <summary>
/// What a move this board made means for the provider (backlog 48). Our seven statuses are a PROJECTION, so
/// the map back out is one to many: dropping a card in Done can mean any of somebody's finished states, and
/// this product does not pick one of them quietly.
/// </summary>
/// <param name="Outcome">Queued, nothing to send, or a question.</param>
/// <param name="Sentence">What happened or what is being asked, in words.</param>
/// <param name="Choices">The states to offer, when one is being asked for. Empty otherwise.</param>
public sealed record ProviderMove(ProviderMoveOutcome Outcome, string Sentence, IReadOnlyList<string> Choices)
{
    /// <summary>Nothing to send, which is the ordinary answer on a board that follows no provider.</summary>
    public static ProviderMove Nothing { get; } = new(ProviderMoveOutcome.NothingToSend, string.Empty, []);
}

/// <summary>What came of one act on the provider sync surface: whether it happened, and a sentence for a person.</summary>
/// <param name="Happened">True when the act was carried out (an item queued, a sync run).</param>
/// <param name="Sentence">What happened or why not, in words.</param>
public sealed record ProviderSyncResult(bool Happened, string Sentence);

/// <summary>
/// The two acts that make a round trip with a provider possible from the product (backlog 47, increment 2).
/// Item 82 built the machinery (the outbox, the drain, the reconcile pass) and nothing ever STARTED it: no item
/// was ever sent to a provider, so no item ever had a mirror state, so the reconcile pass had nothing to check.
/// <para>
/// <see cref="SendAsync"/> queues the CREATION of a local item on the provider. It only queues: the outbox's own
/// rules (in flight before the call leaves, a lost creation looked up by its stamp and never blindly resent) are
/// what send it, in <see cref="SyncNowAsync"/>, which drains the queue and then reconciles what is mirrored.
/// Both are explicit acts by a person, never a side effect of saving, for the same reason publishing a wiki
/// document is: they write to somebody's tracker.
/// </para>
/// </summary>
public sealed class ProviderSync(
    IProviderGateways gateways,
    ITrackerResolver trackers,
    IOutboxStore outbox,
    IMirrorStore mirror,
    OutboxDrain drain,
    ReconcilePass reconcile,
    TimeProvider? clock = null,
    Func<string>? newId = null,
    Func<ProviderStatusMap>? statuses = null,
    Fravashi.Application.Network.ProviderOutages? outages = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Func<string> _newId = newId ?? (() => Guid.NewGuid().ToString("N"));
    private readonly Func<ProviderStatusMap> _statuses = statuses ?? (() => ProviderStatusMap.From(null));

    /// <summary>
    /// The gateway an act CALLS through (backlog 232): the project's own, with every answer reported to the outage
    /// record when there is one, so the sync page can say since when the provider has not answered.
    /// </summary>
    private IProviderGateway? Calling(ProjectId project) => gateways.For(project) switch
    {
        null => null,
        { } gateway when outages is not null => new ObservedProviderGateway(gateway, outages),
        { } gateway => gateway,
    };

    /// <summary>
    /// The outage line for this project's provider, or null while it answers (backlog 232): "Jira unreachable since
    /// 14:02 (name not resolved); 3 change(s) waiting; nothing lost." The count is every write still open for it.
    /// </summary>
    public async Task<string?> OutageAsync(ProjectId project, CancellationToken ct = default) =>
        (await OutageSentenceAsync(project, ct).ConfigureAwait(false))?.Plain();

    /// <summary>
    /// <see cref="OutageAsync"/> as parts, which is what the sync page renders, so its "since" is in the person's zone
    /// (backlog 246).
    /// </summary>
    public async Task<Fravashi.Application.Time.StampedWords?> OutageSentenceAsync(ProjectId project, CancellationToken ct = default)
    {
        if (gateways.For(project) is not { } gateway || outages?.For(gateway.Connection) is not { } outage)
        {
            return null;
        }

        var waiting = await outbox.OpenAsync(project, ct).ConfigureAwait(false);
        return outage.SentenceWithWaiting(waiting.Count);
    }

    /// <summary>
    /// The providers this project's items are mirrored to that it no longer follows, with how many items each (backlog
    /// 145). While there are any, every act here refuses: a sync would ask the new provider for the old keys and
    /// tombstone every item it cannot find, which is the destruction the move has to prevent.
    /// </summary>
    public async Task<IReadOnlyList<(string Connection, int Items)>> ForeignAsync(ProjectId project, CancellationToken ct = default)
    {
        if (gateways.For(project) is not { } gateway)
        {
            return [];
        }

        return [.. (await mirror.ForProjectAsync(project, ct).ConfigureAwait(false)).Values
            .Where(state => !string.Equals(state.Connection, gateway.Connection, StringComparison.Ordinal)
                && !string.Equals(state.Connection, "unknown", StringComparison.Ordinal))
            .GroupBy(state => state.Connection)
            .Select(group => (group.Key, group.Count()))];
    }

    private async Task<ProviderSyncResult?> RefusedForForeignAsync(ProjectId project, IProviderGateway gateway, CancellationToken ct)
    {
        var foreign = await ForeignAsync(project, ct).ConfigureAwait(false);
        return foreign.Count == 0
            ? null
            : new ProviderSyncResult(false,
                $"This board's items are mirrored to {string.Join(" and ", foreign.Select(f => $"{f.Connection} ({f.Items})"))}, and it "
                + $"now follows {gateway.Connection}. Nothing is sent until you start following {gateway.Connection} under Provider sync, "
                + "so no old key is looked up on the new provider.");
    }

    /// <summary>
    /// Moves this project to the provider it now follows (backlog 145): every mirror record of another provider is
    /// kept as history on its item and closed, what was still waiting to go there is abandoned with the reason, and the
    /// items are then free to be sent to the new one. Nothing on either provider is changed by this.
    /// </summary>
    public async Task<ProviderSyncResult> FollowAsync(ProjectId project, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (gateways.For(project) is not { } gateway)
        {
            return new ProviderSyncResult(false, "This project follows no provider, so there is nothing to move to.");
        }

        var foreign = await ForeignAsync(project, ct).ConfigureAwait(false);
        if (foreign.Count == 0)
        {
            return new ProviderSyncResult(false, $"Every mirrored item here is already on {gateway.Connection}.");
        }

        var now = _clock.GetUtcNow();
        var retired = 0;
        foreach (var (connection, _) in foreign)
        {
            retired += await mirror.RetireAsync(project, connection, now, ct).ConfigureAwait(false);
        }

        var abandoned = 0;
        foreach (var entry in await outbox.OpenAsync(project, ct).ConfigureAwait(false))
        {
            if (await outbox.MarkAsync(
                    project, entry.Id, OutboxState.Abandoned, null,
                    $"The project stopped following {string.Join(" and ", foreign.Select(f => f.Connection))} before this was sent.",
                    countAttempt: false, now, ct).ConfigureAwait(false))
            {
                abandoned++;
            }
        }

        return new ProviderSyncResult(true,
            $"{retired} item(s) that were on {string.Join(" and ", foreign.Select(f => f.Connection))} keep that as history, "
            + (abandoned > 0 ? $"{abandoned} change(s) still waiting to go there were abandoned, " : string.Empty)
            + $"and this board now follows {gateway.Connection}. Send everything sends them there.");
    }

    /// <summary>The provider this project follows, as a person would name it, or null when it follows none.</summary>
    public string? ConnectionFor(ProjectId project) => gateways.For(project)?.Connection;

    /// <summary>Whether this project's provider can be ASKED what it holds, so importing is offered at all.</summary>
    public bool CanImportFor(ProjectId project) => gateways.For(project)?.Capabilities.CanList == true;

    /// <summary>
    /// Where one item stands against the project's provider, for the item's own page: a sentence, and whether
    /// sending it is an act that makes sense right now. Null when the project follows no provider, so the page
    /// shows nothing at all rather than a control that can only refuse.
    /// </summary>
    public async Task<(string Sentence, bool CanSend)?> StandingAsync(ProjectId project, WorkItemId item, CancellationToken ct = default)
    {
        if (gateways.For(project) is not { } gateway)
        {
            return null;
        }

        if (await mirror.GetAsync(project, item, ct).ConfigureAwait(false) is { } known)
        {
            if (!string.Equals(known.Connection, gateway.Connection, StringComparison.Ordinal))
            {
                return ($"On {known.Connection} as {known.Key}; this board now follows {gateway.Connection}.", false);
            }

            return (known.IsTombstoned
                ? $"Was on {gateway.Connection} as {known.Key}, and is no longer there."
                : $"On {gateway.Connection} as {known.Key}.", false);
        }

        var history = await mirror.HistoryAsync(project, item, ct).ConfigureAwait(false);
        var before = history.Count == 0
            ? string.Empty
            : " It was on " + string.Join(", then ", history.Reverse().Select(h => $"{h.Connection} as {h.Key}")) + ".";

        var waiting = await outbox.ForItemAsync(project, item, ct).ConfigureAwait(false);
        return waiting.Any(entry => entry.Kind == OutboxKind.Create && entry.State is not (OutboxState.Abandoned or OutboxState.Confirmed))
            ? ($"Waiting to be sent to {gateway.Connection}. Sync now, under Provider sync, sends it.{before}", false)
            : ($"Not on {gateway.Connection}.{before}", gateway.Capabilities.CanWrite);
    }

    /// <summary>Queues the creation of a local item on the project's provider.</summary>
    public async Task<ProviderSyncResult> SendAsync(ProjectId project, WorkItemId item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(item);

        if (gateways.For(project) is not { } gateway)
        {
            return new ProviderSyncResult(false, "This project follows no provider, so there is nowhere to send it.");
        }

        if (!gateway.Capabilities.CanWrite)
        {
            return new ProviderSyncResult(false, $"The {gateway.Connection} connection can only be read, so nothing was queued.");
        }

        if (await RefusedForForeignAsync(project, gateway, ct).ConfigureAwait(false) is { } foreignRefusal)
        {
            return foreignRefusal;
        }

        if (await trackers.For(project).GetAsync(item, ct).ConfigureAwait(false) is not { } stored)
        {
            return new ProviderSyncResult(false, "That item is not on this board any more.");
        }

        if (await mirror.GetAsync(project, item, ct).ConfigureAwait(false) is { } known)
        {
            return new ProviderSyncResult(false, known.IsTombstoned
                ? $"#{stored.Number} was on {gateway.Connection} as {known.Key} and is no longer there. It is not sent again by itself."
                : $"#{stored.Number} is already on {gateway.Connection} as {known.Key}.");
        }

        // One creation per item, ever waiting: a second press must not queue a second issue. A CONFIRMED creation is not
        // waiting: it reached a provider the project may since have moved away from (backlog 145), and while the item
        // is mirrored there the check above already answers.
        var waiting = await outbox.ForItemAsync(project, item, ct).ConfigureAwait(false);
        if (waiting.Any(entry => entry.Kind == OutboxKind.Create && entry.State is not (OutboxState.Abandoned or OutboxState.Confirmed)))
        {
            return new ProviderSyncResult(false, $"#{stored.Number} is already waiting to be sent to {gateway.Connection}.");
        }

        await outbox.QueueAsync(
            new OutboxEntry(
                _newId(),
                project,
                item,
                OutboxKind.Create,
                JsonSerializer.Serialize(ProviderFields.Read(stored)),
                stored.Version,
                OutboxState.Pending,
                0,
                _clock.GetUtcNow(),
                null,
                null,
                null),
            ct).ConfigureAwait(false);

        return new ProviderSyncResult(true, $"#{stored.Number} is queued for {gateway.Connection}. Sync now sends it.");
    }

    /// <summary>
    /// Queues the creation of EVERY item of this board that is not on the provider yet and not already waiting to
    /// be (backlog 49: a whole imported backlog sent at once, where one button per item was 140 presses). Each goes
    /// through <see cref="SendAsync"/>, so every rule of sending one holds for each; a tombstoned item, one whose
    /// earlier copy was removed on the provider, is not sent again by this either. Sync now sends what is queued.
    /// </summary>
    public async Task<ProviderSyncResult> SendAllAsync(ProjectId project, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (gateways.For(project) is not { } gateway)
        {
            return new ProviderSyncResult(false, "This project follows no provider, so there is nowhere to send anything.");
        }

        if (!gateway.Capabilities.CanWrite)
        {
            return new ProviderSyncResult(false, $"The {gateway.Connection} connection can only be read, so nothing was queued.");
        }

        if (await RefusedForForeignAsync(project, gateway, ct).ConfigureAwait(false) is { } foreignRefusal)
        {
            return foreignRefusal;
        }

        var tracker = trackers.For(project);
        var items = new List<WorkItem>();
        string? cursor = null;
        string? loopGuard = null;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var page = await tracker.QueryAsync(new WorkItemQuery { Limit = WorkItemQuery.MaxLimit, Cursor = cursor }, ct)
                .ConfigureAwait(false);
            items.AddRange(page.Items);

            // The same loop guard ImportAsync carries: a page that never advances must stop this rather than
            // spin for ever, keeping what was already read rather than losing it.
            var next = page.NextCursor;
            if (next is { Length: > 0 })
            {
                if (page.Items.Count == 0)
                {
                    loopGuard = "kept answering with an empty page";
                    break;
                }

                if (!seenCursors.Add(next))
                {
                    loopGuard = "kept answering with the same page";
                    break;
                }
            }

            cursor = next;
        }
        while (cursor is not null);

        var queued = 0;
        foreach (var item in items.OrderBy(i => i.Number))
        {
            if (await mirror.GetAsync(project, item.Id, ct).ConfigureAwait(false) is not null)
            {
                continue;
            }

            if ((await SendAsync(project, item.Id, ct).ConfigureAwait(false)).Happened)
            {
                queued++;
            }
        }

        var guardSuffix = loopGuard is not null
            ? $" The board {loopGuard}; stopped after reading {items.Count} item(s), so more of the board may remain unsent."
            : string.Empty;

        return queued == 0
            ? new ProviderSyncResult(
                loopGuard is not null,
                $"Everything on this board is already on {gateway.Connection} or waiting to be sent." + guardSuffix)
            : new ProviderSyncResult(true, $"{queued} item(s) are queued for {gateway.Connection}. Sync now sends them." + guardSuffix);
    }

    /// <summary>
    /// Tells the provider about a move this board just made (backlog 47 increment 5). Until now only the
    /// reconcile pass queued anything, so a card dragged into another column changed nothing on the provider
    /// until the NEXT sync noticed and worked out that we must have moved it. This says so directly.
    /// <para>
    /// It queues only when the reverse map has ONE answer (backlog 48). With several, or with none, the
    /// provider's own state names are handed back for a person to choose from, because which of somebody's
    /// three finished states "Done" means is theirs to say. A choice that is not offered is refused rather
    /// than passed through: the map is what this product knows about that board's vocabulary.
    /// </para>
    /// </summary>
    /// <param name="project">The project whose board moved.</param>
    /// <param name="item">The item that moved.</param>
    /// <param name="status">Where it moved TO on this board.</param>
    /// <param name="chosen">The provider's own state name, once a person has picked one. Null asks.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<ProviderMove> MovedAsync(
        ProjectId project,
        WorkItemId item,
        WorkItemStatus status,
        string? chosen = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (Calling(project) is not { } gateway)
        {
            return ProviderMove.Nothing;
        }

        // Not on the provider is not a failure and not a question: sending an item there is its own act, and
        // a tombstoned one is deliberately not resurrected by a move somebody made here.
        if (await mirror.GetAsync(project, item, ct).ConfigureAwait(false) is not { IsTombstoned: false } known
            || !string.Equals(known.Connection, gateway.Connection, StringComparison.Ordinal))
        {
            // Not mirrored to the provider this board follows now (backlog 145), so a move here is not that provider's.
            return ProviderMove.Nothing;
        }

        if (await trackers.For(project).GetAsync(item, ct).ConfigureAwait(false) is not { } stored)
        {
            return ProviderMove.Nothing;
        }

        var reverse = _statuses().ReverseMap(status);
        string native;
        if (reverse.Settled is { } only && chosen is not { Length: > 0 })
        {
            native = only;
        }
        else
        {
            // The mapping cannot answer on its own, so the WORKFLOW is asked: which states does this
            // provider offer for this item, as it stands, right now. That is a better question than the one
            // the mapping answers, because it is about real states that are really reachable rather than
            // names this product suggested, and often it settles the matter outright.
            var offer = await ProviderMoveNames
                .ResolveAsync(gateway, known.Key, reverse, status, ct).ConfigureAwait(false);
            if (offer.Candidates.Count == 0)
            {
                return new ProviderMove(ProviderMoveOutcome.NeedsAChoice, offer.Sentence, []);
            }

            if (chosen is { Length: > 0 } picked)
            {
                if (!offer.Candidates.Contains(picked.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    return new ProviderMove(
                        ProviderMoveOutcome.NeedsAChoice,
                        $"'{picked.Trim()}' is not one of the states offered for {StableNames.ToName(status)}.",
                        offer.Candidates);
                }

                native = picked.Trim();
            }
            else if (offer.Settled is { } narrowed)
            {
                // The workflow left exactly one way to say it, so there is nothing to ask after all.
                native = narrowed;
            }
            else
            {
                return new ProviderMove(ProviderMoveOutcome.NeedsAChoice, offer.Sentence, offer.Candidates);
            }
        }

        await outbox.QueueAsync(
            new OutboxEntry(
                _newId(),
                project,
                item,
                OutboxKind.Transition,
                JsonSerializer.Serialize(new Dictionary<string, string?>(StringComparer.Ordinal) { ["NativeStatus"] = native }),
                stored.Version,
                OutboxState.Pending,
                0,
                _clock.GetUtcNow(),
                null,
                null,
                null),
            ct).ConfigureAwait(false);

        return new ProviderMove(
            ProviderMoveOutcome.Queued,
            $"Queued for {gateway.Connection} as '{native}'. Sync now sends it.",
            []);
    }

    /// <summary>
    /// Brings items the OTHER way: the ones the provider has that this board has never seen. It is the second
    /// half of this item's acceptance, and it needs the provider to be ASKED what it holds, which is a thing
    /// only an adapter that says it <see cref="ProviderCapabilities.CanList"/> can do.
    /// <para>
    /// An item that arrives is mirrored under its provider key at once, which is what stops the next import
    /// bringing a second copy and what lets the reconcile pass reason about it from then on. An item the board
    /// REFUSES (a provider's title this model will not take) does not stop the rest and is named in the answer,
    /// because an import that silently brought nine of ten would be worse than one that brought none.
    /// </para>
    /// </summary>
    public async Task<ProviderSyncResult> ImportAsync(ProjectId project, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (Calling(project) is not { } gateway)
        {
            return new ProviderSyncResult(false, "This project follows no provider, so there is nothing to import from.");
        }

        if (!gateway.Capabilities.CanList)
        {
            return new ProviderSyncResult(
                false, $"{gateway.Connection} cannot be asked what it holds, so nothing can be imported from it.");
        }

        if (await RefusedForForeignAsync(project, gateway, ct).ConfigureAwait(false) is { } foreignRefusal)
        {
            return foreignRefusal;
        }

        var tracker = trackers.For(project);
        var known = (await mirror.ForProjectAsync(project, ct).ConfigureAwait(false))
            .Values.Select(state => state.Key).ToHashSet(StringComparer.Ordinal);
        var statuses = _statuses();

        var brought = 0;
        var refused = new List<string>();
        string? cursor = null;
        var sawAPage = false;
        string? stoppedBecause = null;
        string? loopGuard = null;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var held = await gateway.ListAsync(cursor, ct).ConfigureAwait(false);
            if (!held.IsOk)
            {
                if (!sawAPage)
                {
                    // The very first page never arrived: nothing was imported, so the ordinary refusal stands.
                    return new ProviderSyncResult(
                        false,
                        held.Outcome == ProviderOutcome.Unreachable
                            ? $"{gateway.Connection} could not be reached, so nothing was imported."
                            : held.Reason ?? $"{gateway.Connection} did not say what it holds.");
                }

                // A LATER page failed. What already arrived is kept (it is mirrored already, below), and the
                // sentence says so rather than the false "Nothing new": more of the provider was never asked.
                stoppedBecause = held.Outcome == ProviderOutcome.Unreachable
                    ? $"{gateway.Connection} could not be reached"
                    : held.Reason ?? $"{gateway.Connection} stopped answering";
                break;
            }

            sawAPage = true;
            foreach (var theirs in held.Value!.Items)
            {
                if (!known.Add(theirs.Key))
                {
                    continue;
                }

                var created = await tracker.CreateAsync(NewFrom(theirs, statuses), new MutationContext(WorkItemActor.System, _newId()) { FromProvider = true }, ct)
                    .ConfigureAwait(false);
                if (!created.IsOk)
                {
                    refused.Add($"{theirs.Key} ({string.Join("; ", created.Errors.Select(e => e.Message))})");
                    continue;
                }

                // Mirrored immediately, with the status this board understood the provider to hold, so the next
                // pass has a base to reason from and the next import knows this key is already here.
                var seen = new Dictionary<string, string?>(theirs.Fields, StringComparer.Ordinal)
                {
                    [nameof(WorkItem.Status)] = StableNames.ToName(created.Value.Status),
                };
                await mirror.ConfirmAsync(
                    project,
                    created.Value.Id,
                    new ProviderConfirmation(gateway.Connection, theirs.Key, theirs.ProviderVersion, created.Value.Version, seen),
                    _clock.GetUtcNow(),
                    ct).ConfigureAwait(false);
                brought++;
            }

            // The loop trusts the provider to advance the cursor; it must not trust it to do so forever. An
            // empty page that still promises more, or the same page handed back again, would otherwise spin
            // for ever rather than stopping, so both are treated as the provider stopping, not as "no more".
            var next = held.Value.Cursor;
            if (next is { Length: > 0 })
            {
                if (held.Value.Items.Count == 0)
                {
                    loopGuard = "kept answering with an empty page";
                    break;
                }

                if (!seenCursors.Add(next))
                {
                    loopGuard = "kept answering with the same page";
                    break;
                }
            }

            cursor = next;
        }
        while (cursor is not null);

        var said = loopGuard is not null
            ? $"{gateway.Connection} {loopGuard}; stopped after {brought} item(s)."
            : stoppedBecause is not null
                ? $"{brought} item(s) came from {gateway.Connection} before it stopped answering ({stoppedBecause}), and more remain."
                : brought == 0
                    ? $"Nothing new on {gateway.Connection}."
                    : $"{brought} item(s) came from {gateway.Connection}.";
        if (refused.Count > 0)
        {
            said += $" This board refused {refused.Count}: {string.Join(", ", refused)}.";
        }

        return new ProviderSyncResult(
            brought > 0 || refused.Count > 0 || stoppedBecause is not null || loopGuard is not null, said);
    }

    /// <summary>
    /// A provider's item as a new local one. LEVEL is Task and KIND is standard because this adapter carries
    /// neither (what an issue type means is per-board mapping, which is item 47's own acceptance), and PARENT
    /// is deliberately dropped: the provider names a parent by ITS key, which may not be here yet, and
    /// inventing a placement is worse than leaving one for a person.
    /// </summary>
    private static NewWorkItem NewFrom(ProviderItem theirs, ProviderStatusMap statuses)
    {
        var fields = theirs.Fields;
        var read = statuses.Translate(theirs.NativeStatus, theirs.NativeCategory, WorkItemStatus.Triage);
        var status = read.Status ?? WorkItemStatus.Triage;

        return new NewWorkItem
        {
            Level = WorkItemLevel.Task,
            Title = fields.GetValueOrDefault(nameof(WorkItem.Title)) ?? string.Empty,
            Description = fields.GetValueOrDefault(nameof(WorkItem.Description)) is { Length: > 0 } description
                ? description
                : ProviderFields.NoDescription,
            Status = status,
            Resolution = status == WorkItemStatus.Cancelled ? WorkItemResolution.WontDo : null,
            Assignee = fields.GetValueOrDefault(nameof(WorkItem.Assignee)),
            Priority = StableNames.Parse<WorkItemPriority>(
                fields.GetValueOrDefault(nameof(WorkItem.Priority)), "priority") is { IsValid: true } parsed
                ? parsed.Value
                : null,
        };
    }

    /// <summary>Sends what is waiting, then reconciles what is mirrored, and says what came of both.</summary>
    public async Task<ProviderSyncResult> SyncNowAsync(ProjectId project, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (Calling(project) is not { } gateway)
        {
            return new ProviderSyncResult(false, "This project follows no provider, so there is nothing to sync with.");
        }

        if (await RefusedForForeignAsync(project, gateway, ct).ConfigureAwait(false) is { } foreignRefusal)
        {
            return foreignRefusal;
        }

        var sent = await drain.DrainAsync(gateway, project, ct).ConfigureAwait(false);
        if (sent.ProviderIsUnreachable)
        {
            // Read-and-queue (ADR 0018 decision 8): nothing is lost, and asking the same silent provider for
            // every mirrored item next would only make the person wait to be told the same thing. When the outage
            // is known it is named in the page's own words (backlog 232), since when and why.
            return new ProviderSyncResult(
                false,
                await OutageAsync(project, ct).ConfigureAwait(false)
                    ?? $"{gateway.Connection} could not be reached. {sent.Requeued} change(s) are still queued and nothing was lost.");
        }

        var checkedNow = await reconcile.RunAsync(gateway, trackers.For(project), ct, sent.Created).ConfigureAwait(false);

        // What the reconcile just queued is sent in the same act: an item created with work already done on it has
        // its status queued as a transition by that pass, and leaving it for the next press would show the provider
        // an open issue for something finished until somebody pressed again.
        if (checkedNow.Queued > 0 && !checkedNow.Unreachable)
        {
            var followed = await drain.DrainAsync(gateway, project, ct).ConfigureAwait(false);
            sent = sent with
            {
                Sent = sent.Sent + followed.Sent,
                Confirmed = sent.Confirmed + followed.Confirmed,
                Requeued = sent.Requeued + followed.Requeued,
                Settled = sent.Settled + followed.Settled,
                StillUnknown = sent.StillUnknown + followed.StillUnknown,
                Failed = sent.Failed + followed.Failed,
                Blocked = sent.Blocked + followed.Blocked,
            };
        }

        var sending = sent == DrainReport.Nothing
            ? "Nothing was waiting to be sent."
            : $"{sent.Confirmed + sent.Settled} change(s) reached {gateway.Connection}"
                + (sent.NeedsAttention ? $", and {sent.StillUnknown + sent.Failed} need a decision below." : ".");

        return new ProviderSyncResult(true, sending + " " + checkedNow.Summary());
    }
}
