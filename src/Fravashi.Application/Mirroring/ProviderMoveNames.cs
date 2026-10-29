using Fravashi.Domain.WorkItems;

namespace Fravashi.Application.Mirroring;

/// <summary>
/// Which of a provider's states a move this board made could mean, and what to say about it (backlog 48).
/// <para>
/// Our seven statuses are a PROJECTION, so the map back out of them is one to many: somebody can map
/// Shipped, Closed and Released all to Done, and then dropping a card in Done has three meanings on their
/// board. The mapping is what this product KNOWS about their vocabulary; the provider's workflow is what is
/// TRUE about it right now, for this one item. Both are used, in that order, and the second often settles
/// what the first could not.
/// </para>
/// <para>
/// It lives on its own because two callers need the same answer and must not drift apart: the board, which
/// can put the question to a person, and the reconcile pass, which cannot and records it instead. A board
/// that quietly settled a move the pass would have raised as a question would be two different products.
/// </para>
/// </summary>
public static class ProviderMoveNames
{
    /// <summary>
    /// How long the provider gets to say which states it offers, before the mapping's names are used
    /// instead. It exists because of WHERE this is asked from: dragging a card is meant to feel instant,
    /// and the gateway's HTTP client carries the default hundred-second timeout, so without a budget a
    /// silent network would leave a person looking at a board that said nothing for a minute and a half.
    /// Running out is not a failure. It is the same answer as an adapter that cannot be asked, and it
    /// takes the same path: the mapping's names, and a sentence saying the question went unanswered.
    /// </summary>
    public static readonly TimeSpan WorkflowBudget = TimeSpan.FromSeconds(4);

    /// <summary>The states to offer, and the sentence that goes with them.</summary>
    /// <param name="Candidates">What to offer, narrowed to what the workflow really allows when it can be asked.</param>
    /// <param name="Sentence">Why this is being asked, or why it cannot be.</param>
    /// <param name="FromWorkflow">True when the provider itself was asked, which is what makes the answer true rather than assumed.</param>
    public sealed record Offer(IReadOnlyList<string> Candidates, string Sentence, bool FromWorkflow)
    {
        /// <summary>The one state to send, or null while somebody still has to choose.</summary>
        public string? Settled => Candidates.Count == 1 ? Candidates[0] : null;
    }

    /// <summary>
    /// Works out what a move to <paramref name="status"/> could be called on the provider. The mapping's
    /// names come first, because those are the ones this board is known to read back as that status. They
    /// are then narrowed to what the workflow OFFERS for this item, so nobody is asked to pick a state the
    /// move would be refused for. When the workflow offers none of them, its own states are offered instead,
    /// said plainly: picking one is then a decision about their workflow, which is theirs to make.
    /// </summary>
    /// <param name="gateway">The provider to ask.</param>
    /// <param name="key">The provider's own key for the item.</param>
    /// <param name="reverse">What the board's mapping says this status is called over there.</param>
    /// <param name="status">Where the item moved to on this board.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="budget">
    /// How long the provider gets to answer. Defaults to <see cref="WorkflowBudget"/>. It is a parameter
    /// only so a test can prove the running-out path without spending the real budget waiting for it.
    /// </param>
    public static async Task<Offer> ResolveAsync(
        IProviderGateway gateway,
        string key,
        ReverseMapping reverse,
        WorkItemStatus status,
        CancellationToken ct = default,
        TimeSpan? budget = null)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(reverse);

        var here = StableNames.ToName(status);
        var mapped = reverse.How == ReverseReading.Several
            ? $"{gateway.Connection} has more than one state that reads as {here} here."
            : $"Nothing here says what {gateway.Connection} calls {here}.";

        if (!gateway.Capabilities.CanOfferTransitions)
        {
            return new Offer(reverse.Candidates, mapped + " Which one does this mean?", false);
        }

        var allowed = budget ?? WorkflowBudget;
        ProviderResult<IReadOnlyList<string>> offered;
        using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            waiting.CancelAfter(allowed);
            try
            {
                offered = await gateway.OfferedTransitionsAsync(key, waiting.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Ours ran out, not the caller's. The caller's cancellation is theirs and is never
                // swallowed into a fallback, because that would turn a person navigating away into a
                // sentence about the provider being slow.
                offered = ProviderResult<IReadOnlyList<string>>.Unreachable(
                    $"it did not answer within {allowed.TotalSeconds:0.#} seconds");
            }
        }

        if (!offered.IsOk)
        {
            // Not knowing what the workflow offers is no reason to refuse the move: the mapping's names are
            // what this product had before it could ask, and they are what it falls back to. Why the asking
            // failed rides along, because somebody choosing blind should know that they are.
            return new Offer(
                reverse.Candidates,
                $"{mapped} {gateway.Connection} could not be asked which states it offers "
                    + $"({offered.Reason ?? "no reason given"}). Which one does this mean?",
                false);
        }

        // An empty workflow and an adapter that cannot answer are DIFFERENT things and are never collapsed:
        // one means the item cannot move there at all, the other means we do not know.
        var states = offered.Value ?? [];
        if (states.Count == 0)
        {
            return new Offer(
                [],
                $"{gateway.Connection}'s workflow offers no other state for {key} from where it is now, so "
                    + $"this move stays on this board. It is {here} here.",
                true);
        }

        var both = reverse.Candidates.Where(c => states.Contains(c, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (both.Length > 0)
        {
            return new Offer(both, $"{mapped} Which one does this mean?", true);
        }

        return new Offer(
            states,
            $"Nothing {gateway.Connection} offers for {key} reads as {here} on this board. From where it is "
                + "now it offers these, and which one this means is yours to say.",
            true);
    }
}
