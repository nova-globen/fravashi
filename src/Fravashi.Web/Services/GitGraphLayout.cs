using Fravashi.Domain.Git;

namespace Fravashi.Web.Services;

/// <summary>
/// Lane assignment for the commit graph (the VS-Code-style swimlanes). Commits arrive in topo order
/// (newest first, children before parents); each row gets a dot lane, edges entering the dot from
/// children above, edges leaving toward parents below, and the set of unrelated lanes passing
/// through. Pure layout: the Razor side only draws what this computes.
/// </summary>
public static class GitGraphLayout
{
    /// <summary>An edge within one row, from a lane at the row's top/bottom edge to/from the dot.</summary>
    public sealed record Edge(int From, int To);

    public sealed record Row(
        GitGraphCommit Commit,
        int DotLane,
        IReadOnlyList<Edge> In,
        IReadOnlyList<Edge> Out,
        IReadOnlyList<int> Through);

    public sealed record Layout(IReadOnlyList<Row> Rows, int MaxLanes)
    {
        public static Layout Empty { get; } = new([], 0);
    }

    public static Layout Compute(IReadOnlyList<GitGraphCommit> commits)
    {
        var lanes = new List<string?>(); // the sha each active lane expects next (null = free slot)
        var rows = new List<Row>(commits.Count);
        var maxLanes = 0;

        foreach (var commit in commits)
        {
            var before = lanes.ToList();
            var waiting = Enumerable.Range(0, lanes.Count).Where(i => lanes[i] == commit.Sha).ToList();

            int dot;
            if (waiting.Count == 0)
            {
                // A tip nobody below waits for: it starts a lane (reusing a freed slot when possible).
                dot = lanes.IndexOf(null);
                if (dot < 0)
                {
                    lanes.Add(null);
                    dot = lanes.Count - 1;
                }
            }
            else
            {
                dot = waiting[0];
            }

            var inEdges = waiting.Select(w => new Edge(w, dot)).ToList();
            foreach (var merged in waiting.Skip(1))
            {
                lanes[merged] = null; // those children's lanes end here
            }

            var outEdges = new List<Edge>();
            if (commit.Parents.Count == 0)
            {
                lanes[dot] = null; // a root: the lane ends
            }
            else
            {
                lanes[dot] = commit.Parents[0]; // the first parent continues the dot's lane
                outEdges.Add(new Edge(dot, dot));
                foreach (var parent in commit.Parents.Skip(1))
                {
                    var existing = lanes.IndexOf(parent);
                    if (existing >= 0 && existing != dot)
                    {
                        outEdges.Add(new Edge(dot, existing)); // merge edge joins the lane already waiting
                    }
                    else if (existing != dot)
                    {
                        var slot = lanes.IndexOf(null);
                        if (slot < 0)
                        {
                            lanes.Add(parent);
                            slot = lanes.Count - 1;
                        }
                        else
                        {
                            lanes[slot] = parent;
                        }

                        outEdges.Add(new Edge(dot, slot));
                    }
                }
            }

            var through = Enumerable.Range(0, before.Count)
                .Where(i => before[i] is not null && !waiting.Contains(i))
                .ToList();

            var rowLanes = Math.Max(before.Count, lanes.Count);
            maxLanes = Math.Max(maxLanes, Math.Max(rowLanes, dot + 1));
            rows.Add(new Row(commit, dot, inEdges, outEdges, through));
        }

        return new Layout(rows, maxLanes);
    }
}
