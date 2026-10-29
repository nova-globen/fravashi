namespace Fravashi.Domain.WorkItems;

/// <summary>
/// Cycle detection for the two directional link types a cycle makes meaningless: <c>blocks</c> (an item
/// would transitively wait on itself) and <c>supersedes</c> (an item would transitively replace itself), plan
/// section 4.3. The store collects the edges reachable from the candidate's target; this decides.
/// <para>
/// It TERMINATES on a graph that already holds a cycle: a mirrored graph may (plan section 1.8), because the
/// refusal is an authoring rule and a provider's data is held to the invariants only.
/// </para>
/// </summary>
public static class BlockerGraph
{
    /// <summary>True when adding <paramref name="candidate"/> to <paramref name="edges"/> would close a cycle of its type.</summary>
    public static bool WouldCreateCycle(IEnumerable<WorkItemLink> edges, WorkItemLink candidate) =>
        FindCycle(edges, candidate) is not null;

    /// <summary>
    /// The cycle adding <paramref name="candidate"/> would close, as the items in order starting and ending
    /// with the candidate's source (<c>a, b, ..., a</c>), or null when it closes none. Only edges of the
    /// candidate's type are followed; an edge already present is not a new cycle.
    /// </summary>
    public static IReadOnlyList<WorkItemId>? FindCycle(IEnumerable<WorkItemLink> edges, WorkItemLink candidate)
    {
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Type is not (WorkItemLinkType.Blocks or WorkItemLinkType.Supersedes))
        {
            throw new ArgumentException("Only blocks and supersedes edges are checked for cycles.", nameof(candidate));
        }

        var outgoing = new Dictionary<WorkItemId, List<WorkItemId>>();
        foreach (var edge in edges)
        {
            if (edge.Type != candidate.Type || edge == candidate)
            {
                continue;
            }

            if (!outgoing.TryGetValue(edge.Source, out var targets))
            {
                targets = [];
                outgoing[edge.Source] = targets;
            }

            targets.Add(edge.Target);
        }

        // Breadth-first from the candidate's target, looking for its source. The visited set is what makes an
        // existing cycle terminate.
        var cameFrom = new Dictionary<WorkItemId, WorkItemId> { [candidate.Target] = candidate.Source };
        var queue = new Queue<WorkItemId>();
        queue.Enqueue(candidate.Target);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == candidate.Source)
            {
                return BuildPath(cameFrom, candidate);
            }

            if (!outgoing.TryGetValue(current, out var next))
            {
                continue;
            }

            foreach (var target in next)
            {
                if (cameFrom.TryAdd(target, current))
                {
                    queue.Enqueue(target);
                }
            }
        }

        return null;
    }

    private static List<WorkItemId> BuildPath(Dictionary<WorkItemId, WorkItemId> cameFrom, WorkItemLink candidate)
    {
        // Walk back from the source (reached last) to the target, then prepend the candidate's source.
        var reversed = new List<WorkItemId> { candidate.Source };
        var step = cameFrom[candidate.Source];
        while (step != candidate.Target)
        {
            reversed.Add(step);
            step = cameFrom[step];
        }

        reversed.Add(candidate.Target);
        reversed.Add(candidate.Source);
        reversed.Reverse();
        return reversed;
    }
}
