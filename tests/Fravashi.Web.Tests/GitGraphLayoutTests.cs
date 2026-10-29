using Fravashi.Domain.Git;
using Fravashi.Web.Services;
using Xunit;

namespace Fravashi.Web.Tests;

public class GitGraphLayoutTests
{
    private static GitGraphCommit Commit(string sha, params string[] parents) =>
        new(sha, sha[..Math.Min(7, sha.Length)], $"commit {sha}", "ada", DateTimeOffset.UtcNow, parents, []);

    [Fact]
    public void A_linear_history_stays_in_one_lane()
    {
        var layout = GitGraphLayout.Compute([
            Commit("c3", "c2"),
            Commit("c2", "c1"),
            Commit("c1"),
        ]);

        Assert.Equal(1, layout.MaxLanes);
        Assert.All(layout.Rows, row => Assert.Equal(0, row.DotLane));
        Assert.All(layout.Rows, row => Assert.Empty(row.Through));
        // The root has no parent: no outgoing edge.
        Assert.Empty(layout.Rows[^1].Out);
    }

    [Fact]
    public void A_side_branch_gets_its_own_lane_and_passes_through_unrelated_rows()
    {
        // main: m2 -> m1; feature f1 also has parent m1 (branched off m1), f1 is a separate tip.
        var layout = GitGraphLayout.Compute([
            Commit("m2", "m1"),
            Commit("f1", "m1"),
            Commit("m1"),
        ]);

        Assert.Equal(2, layout.MaxLanes);
        Assert.Equal(0, layout.Rows[0].DotLane); // m2 owns lane 0
        Assert.Equal(1, layout.Rows[1].DotLane); // f1 is a new tip: lane 1
        Assert.Contains(0, layout.Rows[1].Through); // m2's lane runs through f1's row
        // m1 joins both children: two incoming edges.
        Assert.Equal(0, layout.Rows[2].DotLane);
        Assert.Equal(2, layout.Rows[2].In.Count);
    }

    [Fact]
    public void A_merge_commit_opens_an_edge_to_the_second_parent_lane()
    {
        // merge M has parents m1 (first) and f1 (second); then both parents, then root.
        var layout = GitGraphLayout.Compute([
            Commit("M", "m1", "f1"),
            Commit("f1", "r"),
            Commit("m1", "r"),
            Commit("r"),
        ]);

        var merge = layout.Rows[0];
        Assert.Equal(0, merge.DotLane);
        Assert.Equal(2, merge.Out.Count); // one edge per parent
        Assert.Contains(merge.Out, e => e.To != merge.DotLane); // the second parent branches out
        Assert.Equal(2, layout.MaxLanes);

        // f1 sits on the lane the merge opened for it.
        var f1 = layout.Rows[1];
        Assert.Equal(1, f1.DotLane);
        Assert.Contains(0, f1.Through);
    }

    [Fact]
    public void Freed_lanes_are_reused_by_later_tips()
    {
        // Two independent chains: a2 -> a1 (root), then b1 (root). After a1 closes lane 0,
        // b1 must reuse lane 0 rather than growing the lane count.
        var layout = GitGraphLayout.Compute([
            Commit("a2", "a1"),
            Commit("a1"),
            Commit("b1"),
        ]);

        Assert.Equal(1, layout.MaxLanes);
        Assert.Equal(0, layout.Rows[2].DotLane);
    }
}
