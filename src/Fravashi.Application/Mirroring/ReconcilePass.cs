using System.Text.Json;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Mirroring;
using Fravashi.Domain.WorkItems;

namespace Fravashi.Application.Mirroring;

/// <summary>What one reconciliation pass found and did.</summary>
/// <param name="Checked">Mirrored items the provider was asked about.</param>
/// <param name="Updated">Items whose provider-owned fields the provider had moved, so our copy followed.</param>
/// <param name="Queued">Items we had moved, so a write went on the outbox.</param>
/// <param name="Conflicts">Fields that changed on both sides and are now waiting for a person.</param>
/// <param name="Tombstoned">Items the provider no longer has.</param>
/// <param name="Unreachable">True when the provider stopped answering, so the pass is incomplete.</param>
public sealed record ReconcileReport(
    int Checked,
    int Updated,
    int Queued,
    int Conflicts,
    int Tombstoned,
    bool Unreachable)
{
    /// <summary>A pass that had nothing to do.</summary>
    public static ReconcileReport Nothing { get; } = new(0, 0, 0, 0, 0, false);

    /// <summary>The finding in one sentence, for a card or a log line.</summary>
    public string Summary() => Unreachable
        ? $"The provider stopped answering after {Checked} item(s); nothing was lost and the pass will resume."
        : Checked == 0
            ? "There is nothing mirrored to reconcile."
            : $"{Checked} item(s) checked: {Updated} updated here, {Queued} queued to send, "
                + $"{Conflicts} waiting for a decision, {Tombstoned} no longer on the provider.";
}

/// <summary>
/// Pulls what the provider now holds and reconciles it against what we hold (ADR 0018 decisions 4 and 5).
/// <para>
/// The rule it exists to enforce is one sentence of that ADR: "provider-owned fields reconcile to the
/// provider; Fravashi-owned fields are never overwritten by a sync; and a deletion on the provider side
/// tombstones the item locally rather than destroying its history". Nothing here touches the agent layer,
/// the evidence, the link graph or the execution provenance, and the reason is not politeness: no external
/// tracker carries those, so a sync that treated their absence on the provider as a deletion would erase
/// them on the first pass.
/// </para>
/// <para>
/// When both sides moved the same field, nothing is written either way. The disagreement is recorded with
/// all three values and a person decides. "The provider wins" without qualification is data loss wearing
/// the costume of a policy.
/// </para>
/// </summary>
public sealed class ReconcilePass
{
    private readonly IMirrorStore _mirror;
    private readonly IOutboxStore _outbox;
    private readonly TimeProvider _clock;
    private readonly Func<string> _newId;

    private readonly Func<ProviderStatusMap> _statuses;

    /// <summary>Creates a pass over the mirroring sidecar and the write queue.</summary>
    /// <param name="mirror">Where the provider facts and the conflicts live.</param>
    /// <param name="outbox">Where a local change is queued when the provider has to be told.</param>
    /// <param name="clock">The clock every stamp comes from.</param>
    /// <param name="newId">
    /// How a new operation or conflict id is made. Injected so a test can make the ids predictable; the
    /// default is a GUID, because an operation id has to be unique across crashes and machines.
    /// </param>
    /// <param name="statuses">
    /// What this board's provider statuses mean (backlog 47), asked for at each use rather than fixed here:
    /// this is a singleton and somebody can change the mapping in settings while the studio runs. The default
    /// understands the shipped catalog and a provider's own category, which is what an install that never
    /// opened the mapping step gets.
    /// </param>
    public ReconcilePass(
        IMirrorStore mirror,
        IOutboxStore outbox,
        TimeProvider? clock = null,
        Func<string>? newId = null,
        Func<ProviderStatusMap>? statuses = null)
    {
        _mirror = mirror ?? throw new ArgumentNullException(nameof(mirror));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _clock = clock ?? TimeProvider.System;
        _newId = newId ?? (() => Guid.NewGuid().ToString("n"));
        _statuses = statuses ?? (() => ProviderStatusMap.From(null));
    }

    /// <summary>
    /// Reconciles every mirrored item of the tracker's project against the provider. An item that is
    /// already tombstoned is skipped: it has nothing left to reconcile, and asking again would produce the
    /// same answer every pass forever.
    /// </summary>
    /// <param name="gateway">The provider to check against.</param>
    /// <param name="tracker">The board being reconciled.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="justCreated">
    /// Items this same sync just created on the provider, if any. Their status has no base and no history, and
    /// the provider assigned it seconds ago, so for them the provider's status is adopted rather than asked
    /// about. See <see cref="SettleStatusAsync"/>.
    /// </param>
    public async Task<ReconcileReport> RunAsync(
        IProviderGateway gateway,
        ITracker tracker,
        CancellationToken ct = default,
        IReadOnlySet<WorkItemId>? justCreated = null)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(tracker);

        var project = tracker.Project;
        var states = await _mirror.ForProjectAsync(project, ct).ConfigureAwait(false);

        var checkedCount = 0;
        var updated = 0;
        var queued = 0;
        var conflicts = 0;
        var tombstoned = 0;

        foreach (var (id, state) in states)
        {
            if (state.IsTombstoned)
            {
                continue;
            }

            var item = await tracker.GetAsync(id, ct).ConfigureAwait(false);
            if (item is null)
            {
                // The mirror knows of an item the board does not. That is a graph defect rather than a sync
                // problem, and ReferentialRepair is what reports it; this pass has nothing to compare.
                continue;
            }

            var answer = await gateway.GetAsync(state.Key, ct).ConfigureAwait(false);
            checkedCount++;

            if (answer.Outcome == ProviderOutcome.Gone)
            {
                await _mirror.TombstoneAsync(
                    project,
                    id,
                    answer.Reason ?? $"{gateway.Connection} no longer has {state.Key}.",
                    _clock.GetUtcNow(),
                    ct).ConfigureAwait(false);
                tombstoned++;
                continue;
            }

            if (!answer.IsOk)
            {
                // Unreachable or no answer at all. Stopping is right: carrying on would make one call per
                // item against a provider that is not answering, and the pass resumes from here next time
                // with nothing lost, because nothing has been written.
                return new ReconcileReport(checkedCount - 1, updated, queued, conflicts, tombstoned, Unreachable: true);
            }

            var outcome = await ReconcileOneAsync(
                gateway, tracker, item, state, answer.Value!, justCreated?.Contains(id) == true, ct).ConfigureAwait(false);
            updated += outcome.Updated ? 1 : 0;
            queued += outcome.Queued ? 1 : 0;
            conflicts += outcome.Conflicts;
        }

        return new ReconcileReport(checkedCount, updated, queued, conflicts, tombstoned, Unreachable: false);
    }

    private readonly record struct ItemOutcome(bool Updated, bool Queued, int Conflicts);

    private async Task<ItemOutcome> ReconcileOneAsync(
        IProviderGateway gateway,
        ITracker tracker,
        WorkItem item,
        MirrorState state,
        ProviderItem theirs,
        bool justCreated,
        CancellationToken ct)
    {
        var project = tracker.Project;
        var now = _clock.GetUtcNow();
        var baseFields = await _mirror.BaseFieldsAsync(project, item.Id, ct).ConfigureAwait(false);
        var ours = ProviderFields.Read(item);

        var takeFromProvider = new Dictionary<string, string?>(StringComparer.Ordinal);
        var sendToProvider = new Dictionary<string, string?>(StringComparer.Ordinal);
        var disagreements = 0;

        foreach (var field in ProviderFields.Carried)
        {
            // A field the provider does not carry at all is not a clearing. Absent means "I have no opinion
            // about this", and treating that as "set it to nothing" is how a sync empties a description.
            if (!theirs.Fields.TryGetValue(field, out var theirValue))
            {
                continue;
            }

            var ourValue = ours.GetValueOrDefault(field);
            var baseValue = baseFields.GetValueOrDefault(field);

            switch (ConflictDetection.Compare(field, baseValue, ourValue, theirValue))
            {
                case ConflictVerdict.ProviderOnly:
                    takeFromProvider[field] = theirValue;
                    break;

                case ConflictVerdict.LocalOnly:
                    sendToProvider[field] = ourValue;
                    break;

                case ConflictVerdict.Conflict:
                    await _mirror.RecordConflictAsync(
                        project,
                        new FieldConflict(_newId(), item.Id, field, baseValue, ourValue, theirValue, now, null, null),
                        ct).ConfigureAwait(false);
                    disagreements++;
                    break;

                default:
                    // Unchanged, Converged, or not the provider's field at all. Nothing to do, and that is
                    // the overwhelmingly common case on every pass after the first.
                    break;
            }
        }

        var updated = false;
        if (takeFromProvider.Count > 0)
        {
            var result = await tracker.UpdateAsync(
                item.Id,
                ProviderFields.ToPatch(takeFromProvider),
                item.Version,
                new MutationContext(WorkItemActor.System, _newId()) { FromProvider = true },
                ct).ConfigureAwait(false);

            // A refusal here is not swallowed into a silent no-op: it means the provider holds a value our
            // model will not accept, which is a mapping problem somebody has to see. It is recorded as a
            // conflict so it surfaces where every other disagreement does.
            if (result.IsOk)
            {
                updated = true;
            }
            else
            {
                await _mirror.RecordConflictAsync(
                    project,
                    new FieldConflict(
                        _newId(),
                        item.Id,
                        string.Join(", ", takeFromProvider.Keys),
                        null,
                        "what this board holds",
                        $"a value from {gateway.Connection} this board refused: "
                            + string.Join("; ", result.Errors.Select(e => e.Message)),
                        now,
                        null,
                        null),
                    ct).ConfigureAwait(false);
                disagreements++;
            }
        }

        var queued = false;
        if (sendToProvider.Count > 0)
        {
            await _outbox.QueueAsync(
                new OutboxEntry(
                    _newId(),
                    project,
                    item.Id,
                    OutboxKind.Update,
                    JsonSerializer.Serialize(sendToProvider),
                    item.Version,
                    OutboxState.Pending,
                    0,
                    now,
                    null,
                    null,
                    null),
                ct).ConfigureAwait(false);
            queued = true;
        }

        // Status is settled apart from the fields because it is a TRANSITION, not a patch: the board has
        // rules about what may follow what, and a status that rode the field loop would be written by a patch
        // that silently carries no status at all. The reading is the same three-way one, so the same "who
        // moved it" logic decides, and both sides moving is a question rather than a winner.
        var settled = await SettleStatusAsync(gateway, tracker, item, updated, theirs, baseFields, justCreated, ct)
            .ConfigureAwait(false);

        // The base becomes what the provider holds NOW, whatever was decided about each field. That is what
        // makes the next pass able to tell which side moved; leaving the old base would make every field
        // that was just reconciled look moved again on the very next run.
        var confirmed = new Dictionary<string, string?>(theirs.Fields, StringComparer.Ordinal);
        if (settled.Seen is { } seenStatus)
        {
            // The status the provider was UNDERSTOOD to hold rides in the base beside the fields, which is
            // what lets the next pass tell a provider that moved it from one that never did. A status nobody
            // could read leaves no base, so it stays reportable instead of quietly becoming the new normal.
            confirmed[nameof(WorkItem.Status)] = StableNames.ToName(seenStatus);
        }

        await _mirror.ConfirmAsync(
            project,
            item.Id,
            new ProviderConfirmation(gateway.Connection, theirs.Key, theirs.ProviderVersion, item.Version, confirmed),
            now,
            ct).ConfigureAwait(false);

        return new ItemOutcome(
            updated || settled.Moved,
            queued || settled.Queued,
            disagreements + settled.Disagreements);
    }

    private readonly record struct StatusOutcome(WorkItemStatus? Seen, bool Moved, bool Queued, int Disagreements);

    /// <summary>
    /// Settles one item's status against the provider's (backlog 47). What the provider says is translated
    /// first, because a name this board does not know is not a status change: it is either the provider's
    /// category speaking or nothing at all, and both of those are reported rather than acted on blindly.
    /// </summary>
    private async Task<StatusOutcome> SettleStatusAsync(
        IProviderGateway gateway,
        ITracker tracker,
        WorkItem item,
        bool alreadyUpdated,
        ProviderItem theirs,
        IReadOnlyDictionary<string, string?> baseFields,
        bool justCreated,
        CancellationToken ct)
    {
        var project = tracker.Project;
        var now = _clock.GetUtcNow();

        // The item may have just taken the provider's fields, which moved its version on.
        var current = alreadyUpdated ? await tracker.GetAsync(item.Id, ct).ConfigureAwait(false) ?? item : item;

        var statuses = _statuses();
        var read = statuses.Translate(theirs.NativeStatus, theirs.NativeCategory, current.Status);
        if (read.Said is { Length: > 0 } said)
        {
            // A degrade and an unreadable status are both things somebody should fix in the mapping, so they
            // are written where every other thing needing attention is written rather than only into a log.
            //
            // The provider's value here is what the name was UNDERSTOOD to mean, when anything was, and the
            // raw name only when nothing was. That is not presentation: settling this rebases the agreed
            // value onto one of these two, and a raw provider name recorded where a canonical one belongs
            // matches neither side on the next pass, so "keep what is here" would re-ask for ever instead of
            // queueing the status the person just said they wanted kept. The raw name is not lost; the
            // sentence below names it, which is the whole reason that sentence exists.
            await _mirror.RecordConflictAsync(
                project,
                new FieldConflict(
                    _newId(),
                    item.Id,
                    nameof(WorkItem.Status),
                    null,
                    StableNames.ToName(current.Status),
                    read.Status is { } understood ? StableNames.ToName(understood) : theirs.NativeStatus,
                    now,
                    null,
                    null) { Note = Explained(null) },
                ct).ConfigureAwait(false);
        }

        // An open conflict for the same field is one ROW: RecordConflictAsync updates rather than adding a
        // second. So a status that was both hard to read AND in disagreement would have the second record
        // clobber the first's explanation, and the sentence about the mapping would vanish exactly when it
        // was most wanted. Every conflict recorded below carries the reading, whichever one lands last.
        string? Explained(string? extra) => (extra, read.Said) switch
        {
            ({ Length: > 0 } one, { Length: > 0 } two) => one + " " + two,
            ({ Length: > 0 } one, _) => one,
            (_, { Length: > 0 } two) => two,
            _ => null,
        };

        if (read.Status is not { } theirStatus)
        {
            // Nothing readable: the item stays where it is, and no base is written for a status nobody read. A name
            // the mapping lists under two statuses is a question in the same way an unknown one is (backlog 181).
            return new StatusOutcome(
                null, false, false, read.How is StatusReading.Unknown or StatusReading.Ambiguous ? 1 : 0);
        }

        var ours = StableNames.ToName(current.Status);
        var baseStatus = baseFields.GetValueOrDefault(nameof(WorkItem.Status));
        var theirName = StableNames.ToName(theirStatus);

        if (baseStatus is null)
        {
            // Nothing here has ever recorded what the provider's status was: every item mirrored by a build
            // that did not read status is in that position. With no base, "who moved it" has no answer, so
            // agreement is simply learned, and a DISAGREEMENT is a question rather than a winner. Guessing
            // either way would be wrong in a way nobody could see: adopting the provider's status would move
            // work somebody is doing, and keeping ours would quietly send it back the other way.
            if (string.Equals(ours, theirName, StringComparison.Ordinal))
            {
                return new StatusOutcome(theirStatus, false, false, read.Said is null ? 0 : 1);
            }

            if (justCreated)
            {
                // Except for an item this same sync just created there. Then "who moved it" DOES have an
                // answer: nobody. The provider opened the item in its own initial status, which is a thing it
                // decides and not a move anybody made. Measured live against Jira on 2026-09-22: an item sent
                // from Triage became an issue in "To Do", and asking the person to choose made a question out of
                // the ordinary act of sending, so an item still in TRIAGE takes the provider's opening status.
                // An item already further along is different (backlog 49: a whole backlog sent at once, most of
                // it Done). Its status here is somebody's decision and the provider's opening status is not,
                // so adopting it would have walked every finished item back to "To Do". Its own status is sent.
                return current.Status == WorkItemStatus.Triage
                    ? await AdoptAsync().ConfigureAwait(false)
                    : await SendOursAsync().ConfigureAwait(false);
            }

            await _mirror.RecordConflictAsync(
                project,
                new FieldConflict(
                    _newId(), item.Id, nameof(WorkItem.Status), null, ours, theirName, now, null, null)
                {
                    Note = Explained(null),
                },
                ct).ConfigureAwait(false);
            return new StatusOutcome(null, false, false, 1 + (read.Said is null ? 0 : 1));
        }

        // Taking the provider's status, used both when the provider moved it and when the provider ASSIGNED
        // it moments ago on a creation. A board that refuses the move is a question, and leaves no base:
        // recording one would make the refusal look settled on the very next pass.
        async Task<StatusOutcome> AdoptAsync()
        {
            var moved = await tracker.TransitionAsync(
                current.Id,
                theirStatus,
                // Cancelling needs a resolution and a provider cannot give one. "Will not do" is what a
                // removed item means on every tracker this targets, and it is said here rather than
                // guessed further down.
                theirStatus == WorkItemStatus.Cancelled ? WorkItemResolution.WontDo : null,
                null,
                null,
                current.Version,

                // The provider's own move, not an act here: the rules that judge a person's or an agent's move
                // (evidence on Done, requested 2026-10-22) must not refuse it, or the stores would disagree.
                new MutationContext(WorkItemActor.System, _newId()) { FromProvider = true },
                ct).ConfigureAwait(false);

            if (moved.IsOk)
            {
                return new StatusOutcome(theirStatus, true, false, read.Said is null ? 0 : 1);
            }

            // The provider holds a state this board will not move to. Not swallowed, and no base either:
            // recording one would make the refusal look settled on the next pass.
            await _mirror.RecordConflictAsync(
                project,
                new FieldConflict(
                    _newId(), item.Id, nameof(WorkItem.Status), baseStatus, ours, theirName, now, null, null)
                {
                    Note = Explained("This board refused that move: "
                        + string.Join("; ", moved.Errors.Select(e => e.Message))),
                },
                ct).ConfigureAwait(false);
            return new StatusOutcome(null, false, false, 1);
        }

        // Sending the status HERE to the provider, used when this board moved it and when an item was just created
        // there with work already done on it. Resolved the way a drag resolves it, so the two cannot differ.
        async Task<StatusOutcome> SendOursAsync()
        {
            var reverse = statuses.ReverseMap(current.Status);
            var native = reverse.Settled;
            string? asked = null;
            if (native is null)
            {
                // The same question the board puts to a person, asked here too, so the two surfaces
                // cannot drift: a move the board would have sent without asking must not become a
                // question simply because it arrived through a sync instead of through a drag.
                var offer = await ProviderMoveNames
                    .ResolveAsync(gateway, theirs.Key, reverse, current.Status, ct).ConfigureAwait(false);
                native = offer.Settled;

                // The board shows the states as buttons; a conflict has only a sentence, so they are
                // named in it. A question that does not say what the answers are is not one.
                asked = offer.Candidates.Count == 0
                    ? offer.Sentence
                    : offer.Sentence + " It could be sent as " + string.Join(" or ", offer.Candidates) + ".";
            }

            if (native is null)
            {
                // Backlog 48: our statuses are a projection, so the map back out is one to many. Which of
                // somebody's three finished states "Done" means is theirs to say, and this pass has nobody
                // to ask, so the question is recorded where every other one is rather than sent as a guess.
                // No base either: writing one would make the guess look settled on the next pass.
                await _mirror.RecordConflictAsync(
                    project,
                    new FieldConflict(
                        _newId(), item.Id, nameof(WorkItem.Status), baseStatus, ours, theirName, now, null, null)
                    {
                        Note = Explained($"It was moved here and not sent. {asked} "
                            + "Settle it here, or say which under Settings > Project > Board and status mapping."),
                    },
                    ct).ConfigureAwait(false);
                return new StatusOutcome(null, false, false, 1);
            }

            await _outbox.QueueAsync(
                new OutboxEntry(
                    _newId(),
                    project,
                    item.Id,
                    OutboxKind.Transition,
                    JsonSerializer.Serialize(new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["NativeStatus"] = native,
                    }),
                    current.Version,
                    OutboxState.Pending,
                    0,
                    now,
                    null,
                    null,
                    null),
                ct).ConfigureAwait(false);
            return new StatusOutcome(theirStatus, false, true, read.Said is null ? 0 : 1);
        }

        switch (ConflictDetection.Compare(nameof(WorkItem.Status), baseStatus, ours, theirName))
        {
            case ConflictVerdict.ProviderOnly:
                return await AdoptAsync().ConfigureAwait(false);

            case ConflictVerdict.LocalOnly:
                return await SendOursAsync().ConfigureAwait(false);

            case ConflictVerdict.Conflict:
                await _mirror.RecordConflictAsync(
                    project,
                    new FieldConflict(
                        _newId(), item.Id, nameof(WorkItem.Status), baseStatus, ours, theirName, now, null, null)
                    {
                        Note = Explained(null),
                    },
                    ct).ConfigureAwait(false);

                // No base: a disagreement nobody has settled must still read as a disagreement next time.
                return new StatusOutcome(null, false, false, 1 + (read.Said is null ? 0 : 1));

            default:
                return new StatusOutcome(theirStatus, false, false, read.Said is null ? 0 : 1);
        }
    }
}
