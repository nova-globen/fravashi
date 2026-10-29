using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;

namespace Fravashi.Domain.Mirroring;

/// <summary>
/// What a queued write asks the provider to do. Deliberately short: the outbox carries only what a provider
/// OWNS (ADR 0018 decision 4), so evidence, the link graph, readiness, scenarios and the rest of the agent
/// layer never appear here. There is no delete, because Fravashi does not delete on a provider's behalf.
/// </summary>
public enum OutboxKind
{
    /// <summary>The item does not exist on the provider yet.</summary>
    Create,

    /// <summary>Provider-owned fields of an existing item changed here.</summary>
    Update,

    /// <summary>The item's status changed here and the provider's own state has to follow.</summary>
    Transition,
}

/// <summary>
/// Where a queued write has got to. The states are not a convenience: <see cref="InFlight"/> and
/// <see cref="Unresolved"/> exist ONLY to make ADR 0018 decision 7 true, and removing them would put the
/// duplicate back.
/// </summary>
public enum OutboxState
{
    /// <summary>Recorded here and not attempted yet. Safe to send, safe to drop, safe to retry.</summary>
    Pending,

    /// <summary>
    /// We are about to call the provider, or were when we stopped existing. This row is committed BEFORE the
    /// call leaves, so a crash at the worst possible moment leaves exactly this and nothing else.
    /// </summary>
    InFlight,

    /// <summary>The provider accepted it and we recorded what came back.</summary>
    Confirmed,

    /// <summary>
    /// An <see cref="InFlight"/> entry was found after a restart, so whether the provider acted is unknown.
    /// It is never retried from here: recovery has to ASK the provider first.
    /// </summary>
    Unresolved,

    /// <summary>The provider refused it for a reason that will not change by trying again.</summary>
    Failed,

    /// <summary>A person decided this write will not be made.</summary>
    Abandoned,
}

/// <summary>
/// One durable queued write (ADR 0018 decisions 7 and 8). Its <see cref="Id"/> IS the client-generated
/// operation id: the same value that goes into the local operation ledger, and the same value that is
/// stamped onto the provider so a crashed creation can be found again rather than made twice.
/// </summary>
/// <param name="Id">The client-generated operation id, unique per project, and the idempotency key.</param>
/// <param name="Project">The project whose graph this write belongs to.</param>
/// <param name="Item">The local item the write is about.</param>
/// <param name="Kind">What the provider is being asked to do.</param>
/// <param name="Payload">The provider-owned fields, serialised. Opaque here on purpose.</param>
/// <param name="ExpectedVersion">Our version of the item when the write was queued, for optimistic concurrency.</param>
/// <param name="State">Where it has got to.</param>
/// <param name="Attempts">How many times it has actually been sent.</param>
/// <param name="QueuedUtc">When it was recorded here.</param>
/// <param name="LastAttemptUtc">When it was last sent, or null if it never was.</param>
/// <param name="ResultKey">The provider's key once it is confirmed.</param>
/// <param name="Failure">Why it failed or what is unknown about it, in words a person can act on.</param>
public sealed record OutboxEntry(
    string Id,
    ProjectId Project,
    WorkItemId Item,
    OutboxKind Kind,
    string Payload,
    long ExpectedVersion,
    OutboxState State,
    int Attempts,
    DateTimeOffset QueuedUtc,
    DateTimeOffset? LastAttemptUtc,
    string? ResultKey,
    string? Failure)
{
    /// <summary>
    /// True while this entry is a change that is still on its way. Disjoint from <see cref="NeedsAPerson"/>
    /// on purpose: the two drive different sentences in the freshness indicator, and an entry counted in
    /// both would be reported twice and would keep saying "waiting to reach jira" about a write that is not
    /// going anywhere.
    /// </summary>
    public bool IsPendingWrite => State is OutboxState.Pending or OutboxState.InFlight;

    /// <summary>True when nothing further will happen without a person.</summary>
    public bool NeedsAPerson => State is OutboxState.Unresolved or OutboxState.Failed;
}

/// <summary>
/// The rules of the queue, as pure functions, so they can be argued with in tests without a database or a
/// provider. The two that carry the weight:
/// <list type="number">
/// <item>
/// An entry found <see cref="OutboxState.InFlight"/> after a restart is <see cref="OutboxState.Unresolved"/>
/// and never simply retried. The provider may or may not have acted, and a blind retry is exactly the
/// duplicate ADR 0018 decision 7 describes.
/// </item>
/// <item>
/// Writes for ONE item are strictly ordered, and an entry that needs a person BLOCKS every later entry for
/// that item. Letting a transition overtake the creation it depends on, or run while the creation's outcome
/// is unknown, would produce a provider-side state nobody asked for. Different items do not block each
/// other, because there is no ordering between them to protect.
/// </item>
/// </list>
/// </summary>
public static class OutboxDiscipline
{
    /// <summary>
    /// What a restart does to an entry. Only <see cref="OutboxState.InFlight"/> moves; everything else was
    /// already a settled fact before the process stopped and stays exactly as it was.
    /// </summary>
    public static OutboxEntry OnRestart(OutboxEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return entry.State == OutboxState.InFlight
            ? entry with
            {
                State = OutboxState.Unresolved,
                Failure = $"This write was in flight when {ProductName.Short} stopped, so whether the provider acted on "
                    + "it is unknown. It will be looked up by its operation id before anything is sent again.",
            }
            : entry;
    }

    /// <summary>
    /// The next entry to send for one item, given that item's entries in queue order, or null when nothing
    /// may be sent. Nothing may be sent when the oldest unfinished entry needs a person: that entry is the
    /// blockage, and skipping past it is the failure mode this whole type exists to prevent.
    /// </summary>
    public static OutboxEntry? NextToSend(IEnumerable<OutboxEntry> entriesForOneItem)
    {
        ArgumentNullException.ThrowIfNull(entriesForOneItem);

        foreach (var entry in entriesForOneItem.OrderBy(e => e.QueuedUtc).ThenBy(e => e.Id, StringComparer.Ordinal))
        {
            switch (entry.State)
            {
                case OutboxState.Confirmed:
                case OutboxState.Abandoned:
                    continue;

                // Already out there. One write per item at a time, or two calls race over the same version.
                case OutboxState.InFlight:
                    return null;

                case OutboxState.Unresolved:
                case OutboxState.Failed:
                    return null;

                case OutboxState.Pending:
                    return entry;

                default:
                    return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether an entry whose outcome is unknown can be settled without a person.
    /// <para>
    /// Only a CREATION can be made twice, and that is worth stating because it shrinks the problem to one
    /// operation. An update and a transition in this queue carry the DESIRED VALUE rather than a delta
    /// (there is no "append" and no "increment" here, by construction), so applying one a second time lands
    /// in exactly the same place and retrying it is safe whatever the provider did the first time. A
    /// creation has no such property: run twice, it makes two items, and there is no key yet for an
    /// external key to match on. So a creation needs the provider to be searchable for the operation id we
    /// stamped on it, and a provider that cannot be searched leaves a genuine unknown. The honest response
    /// to an unknown is to ask a person, not to pick the outcome that makes the queue drain.
    /// </para>
    /// </summary>
    public static bool CanBeSettledAutomatically(OutboxEntry entry, bool providerCanSearchByOperationId)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.State != OutboxState.Unresolved)
        {
            return false;
        }

        return entry.Kind != OutboxKind.Create || providerCanSearchByOperationId;
    }

    /// <summary>
    /// What to tell a person about an unresolved entry a provider cannot be searched for. Written as the
    /// choice they actually have, because "reconciliation failed" tells them nothing they can act on.
    /// </summary>
    public static string UnresolvedSentence(OutboxEntry entry, string connection)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var what = entry.Kind switch
        {
            OutboxKind.Create => "create this item on",
            OutboxKind.Update => "change this item on",
            _ => "move this item on",
        };

        return $"{ProductName.Short} was trying to {what} {connection} when it stopped, and {connection} cannot be "
            + "asked whether it went through. Check there and then either mark this as done or send it again.";
    }
}
