using Fravashi.Domain.WorkItems;

namespace Fravashi.Application.Mirroring;

/// <summary>
/// How a call to a provider ended. The split that matters is between the last three: they are three
/// genuinely different states of knowledge, and collapsing them is what produces duplicates.
/// </summary>
public enum ProviderOutcome
{
    /// <summary>The provider answered and did what was asked.</summary>
    Ok,

    /// <summary>The provider answered that the thing addressed is not there.</summary>
    Gone,

    /// <summary>The provider answered that our version is stale.</summary>
    Conflict,

    /// <summary>The provider answered and refused. Trying again unchanged will be refused again.</summary>
    Refused,

    /// <summary>
    /// The call provably never reached the provider (no route, refused connection, no credentials). Nothing
    /// happened there, so the write goes back on the queue and the product degrades to read-and-queue,
    /// which is what ADR 0018 decision 8 promises an outage does.
    /// </summary>
    Unreachable,

    /// <summary>
    /// The call left and no answer came back (a timeout, a dropped connection mid-request). Whether the
    /// provider acted is UNKNOWN. This is the same state a crash leaves behind, and it is handled the same
    /// way: ask before sending anything again. An adapter that reports this as Unreachable because it is
    /// simpler has just reintroduced the duplicate.
    /// </summary>
    Indeterminate,
}

/// <summary>
/// One page of what a provider holds, newest changed first (backlog 182). <see cref="Cursor"/> null means
/// this was the last page: an import that keeps asking for the next one with it stops here, having seen
/// everything the provider has, however many pages that took.
/// </summary>
/// <param name="Items">The items on this page.</param>
/// <param name="Cursor">
/// The provider's own token for the NEXT page, round-tripped back into <see cref="IProviderGateway.ListAsync"/>
/// unexamined. Null when there is no next page.
/// </param>
public sealed record ProviderPage(IReadOnlyList<ProviderItem> Items, string? Cursor);

/// <summary>An item as the provider has it: its key, its own version token, and its provider-owned fields.</summary>
/// <param name="Key">The provider's key, e.g. <c>PROJ-12</c>.</param>
/// <param name="ProviderVersion">Its version token (etag, updated stamp) kept verbatim, or null if it has none.</param>
/// <param name="Fields">Provider-owned field values by <see cref="WorkItem"/> property name.</param>
/// <param name="NativeStatus">The provider's own status name, as it says it.</param>
/// <param name="NativeCategory">
/// The provider's own COARSE category for that status (Jira's <c>statusCategory</c>, Azure DevOps' state
/// category), or null when it has none. It is what makes "an unknown native status degrades to its category
/// rather than breaking" possible at all: without it, an unrecognised name can only be left alone.
/// </param>
public sealed record ProviderItem(
    string Key,
    string? ProviderVersion,
    IReadOnlyDictionary<string, string?> Fields,
    string? NativeStatus,
    string? NativeCategory = null);

/// <summary>The result of one provider call.</summary>
/// <typeparam name="T">What a successful call returns.</typeparam>
/// <param name="Outcome">How it ended.</param>
/// <param name="Value">The value on <see cref="ProviderOutcome.Ok"/>; default otherwise.</param>
/// <param name="Reason">Why it ended that way, in words, for anything but Ok.</param>
public sealed record ProviderResult<T>(ProviderOutcome Outcome, T? Value, string? Reason)
{
    /// <summary>True when the provider answered and did what was asked.</summary>
    public bool IsOk => Outcome == ProviderOutcome.Ok;

    /// <summary>
    /// The network failure behind an <see cref="ProviderOutcome.Unreachable"/> or <see cref="ProviderOutcome.Indeterminate"/>
    /// (backlog 232), classified by the one shared rule, or null when the provider answered or nothing was called. It is
    /// what tells "Jira unreachable since 14:02" apart from a 429 or a missing configuration, which are not outages.
    /// </summary>
    public Fravashi.Application.Network.NetworkFailure? Network { get; init; }

    /// <summary>
    /// This failure as a result of another type: the outcome, the reason and the network failure carried over, no value.
    /// An adapter re-wraps an inner call's failure with this, so the network failure is never dropped on the way out.
    /// </summary>
    public ProviderResult<TOther> As<TOther>() => new(Outcome, default, Reason) { Network = Network };

    /// <summary>A success.</summary>
    public static ProviderResult<T> Ok(T value) => new(ProviderOutcome.Ok, value, null);

    /// <summary>The provider does not have it.</summary>
    public static ProviderResult<T> Gone(string reason) => new(ProviderOutcome.Gone, default, reason);

    /// <summary>The provider says our version is stale.</summary>
    public static ProviderResult<T> Conflict(string reason) => new(ProviderOutcome.Conflict, default, reason);

    /// <summary>The provider refused it.</summary>
    public static ProviderResult<T> Refused(string reason) => new(ProviderOutcome.Refused, default, reason);

    /// <summary>The call never left.</summary>
    public static ProviderResult<T> Unreachable(string reason) => new(ProviderOutcome.Unreachable, default, reason);

    /// <summary>The call left and nothing came back.</summary>
    public static ProviderResult<T> Indeterminate(string reason) => new(ProviderOutcome.Indeterminate, default, reason);
}

/// <summary>
/// What a provider adapter can actually do, declared rather than inferred. The archived
/// <c>IWorkItemTracker</c> reported "writable" whenever a token string was non-empty, so a read-scoped
/// token passed and then failed at write time; nothing here is derived from a credential being present.
/// </summary>
/// <param name="CanWrite">Whether writes may be attempted at all.</param>
/// <param name="CanSearchByOperationId">
/// Whether the adapter can find an item by the operation id Fravashi stamped on it when it created it.
/// This is the single capability that makes a crashed creation recoverable; an adapter that cannot do it
/// leaves unresolved writes for a person, which is the honest outcome rather than a hidden duplicate.
/// </param>
/// <param name="CanTombstone">Whether a provider-side deletion can be observed at all.</param>
/// <param name="CanList">
/// Whether the adapter can be ASKED what the provider has, rather than only about items it already knows the
/// key of. It is what makes "items created in the provider appear here" possible; an adapter without it is not
/// broken, it simply cannot be imported from, and the product says that rather than showing nothing to import.
/// </param>
/// <param name="CanOfferTransitions">
/// Whether the adapter can be asked which states the provider's own workflow offers for one item right now.
/// It is what lets a person choose between the provider's REAL options instead of names this product guessed
/// at, and what lets an ambiguous move settle itself when the workflow leaves only one of them reachable.
/// </param>
public sealed record ProviderCapabilities(
    bool CanWrite,
    bool CanSearchByOperationId,
    bool CanTombstone,
    bool CanList = false,
    bool CanOfferTransitions = false)
{
    /// <summary>A provider that can only be read, which is a legitimate configuration and not a fault.</summary>
    public static ProviderCapabilities ReadOnly { get; } = new(false, false, true);
}

/// <summary>
/// The narrow port a provider adapter implements (item 47) and the reconciliation machinery (item 82)
/// drives. Deliberately smaller than <c>ITracker</c>: it carries only what a provider OWNS, in the
/// provider's own terms, and knows nothing about the local board, the agent layer or the link graph.
/// <para>
/// Every write takes the client operation id, and an adapter is expected to STAMP it somewhere the
/// provider will keep and can be searched (a label, a property, a trailer on the description). That stamp
/// is what <see cref="FindByOperationIdAsync"/> looks for, and it is the only thing that can tell a
/// crashed-but-accepted creation from one that never happened.
/// </para>
/// </summary>
public interface IProviderGateway
{
    /// <summary>The connection's name, as the user sees it.</summary>
    string Connection { get; }

    /// <summary>What this adapter can do, never inferred from a token being present.</summary>
    ProviderCapabilities Capabilities { get; }

    /// <summary>Creates the item on the provider, stamping the operation id so it can be found again.</summary>
    Task<ProviderResult<ProviderItem>> CreateAsync(string payload, string operationId, CancellationToken ct = default);

    /// <summary>Changes provider-owned fields of an existing item.</summary>
    Task<ProviderResult<ProviderItem>> UpdateAsync(string key, string payload, string? providerVersion, string operationId, CancellationToken ct = default);

    /// <summary>Moves the item's native state.</summary>
    Task<ProviderResult<ProviderItem>> TransitionAsync(string key, string payload, string? providerVersion, string operationId, CancellationToken ct = default);

    /// <summary>
    /// The item carrying this operation id, or Ok(null) when the provider definitely does not have one.
    /// Ok(null) is a real answer and means the write never landed; anything else means we still do not know.
    /// </summary>
    Task<ProviderResult<ProviderItem?>> FindByOperationIdAsync(string operationId, CancellationToken ct = default);

    /// <summary>The item with this key, or <see cref="ProviderOutcome.Gone"/> when the provider no longer has it.</summary>
    Task<ProviderResult<ProviderItem>> GetAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// One page of what the provider has, newest first (backlog 182). Pass the previous call's
    /// <see cref="ProviderPage.Cursor"/> back to ask for the next page; null asks for the first one. A caller
    /// that keeps asking while the answer carries a cursor sees everything the provider has, however many
    /// pages that takes; it never stops at a fixed count of its own choosing. Only meaningful when
    /// <see cref="ProviderCapabilities.CanList"/> says so; the default refuses, so an adapter that has not
    /// implemented it can never be mistaken for a provider that holds nothing.
    /// </summary>
    Task<ProviderResult<ProviderPage>> ListAsync(string? cursor, CancellationToken ct = default) =>
        Task.FromResult(ProviderResult<ProviderPage>.Refused(
            $"{Connection} cannot be asked what it has, so nothing can be imported from it."));

    /// <summary>
    /// The states this provider's workflow offers for one item AS IT STANDS, in the provider's own names
    /// (backlog 48). Asked when a move this board made could mean more than one thing over there, so the
    /// person chooses between states that really exist and are really reachable, rather than between names
    /// this product suggested. An empty list is a real answer and means the workflow offers nothing from
    /// here; refusing is what an adapter that cannot answer says, and the two are never confused.
    /// </summary>
    /// <param name="key">The provider's own key for the item.</param>
    /// <param name="ct">Cancellation.</param>
    Task<ProviderResult<IReadOnlyList<string>>> OfferedTransitionsAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(ProviderResult<IReadOnlyList<string>>.Refused(
            $"{Connection} cannot be asked which states it offers for an item."));
}
