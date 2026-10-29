using AwesomeAssertions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Sessions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions.Containment;

/// <summary>
/// Backlog 202: a Read, Glob or Grep that stays inside THIS session's own agent memory
/// (<c>SessionFrameworkProjector</c>'s shared <c>/data/agent-memory/&lt;key&gt;</c>, reached through the
/// session-side link at <c>&lt;config home&gt;/projects/&lt;key&gt;/memory</c>) runs unattended, exactly as a
/// read inside the workspace copy does. The memory folder sits right beside things that must keep asking
/// (the session's settings, its approval script, a transcript), so every negative case here is one of
/// those near misses rather than an unrelated path.
/// <para>
/// Every <c>Check</c>/<c>Resolve</c> call below passes BOTH <c>fixture.MemoryLink</c> (the session-side
/// name) and <c>fixture.MemoryShared</c> (the real folder it is supposed to resolve to). That second
/// value is not decorative: a memory root handed in with no pinned target is dropped before anything
/// else is measured (<c>AgentReadScope.ContainmentRoots</c>), so leaving it out would make every "still
/// asks" case here pass for the wrong reason.
/// </para>
/// <para>
/// Ground truth is the real fixture tree (<see cref="ContainmentFixture"/>), never a restated model of
/// the check: every "still asks" case below is a real <c>..</c> chain or a real symlink, not a string
/// the test merely asserts should be refused.
/// </para>
/// </summary>
public class MemoryContainmentTests
{
    [PlatformOracleFact]
    public void A_read_inside_this_session_own_memory_runs_unattended()
    {
        using var fixture = Fixture();
        var path = Path.Combine(fixture.MemoryLink, "proof-marker.md");

        File.ReadAllText(path).Trim().Should().Be(
            "remembered", "the case only means something if this really is the shared memory file");

        var input = Call(("file_path", path));

        AgentReadScope.Check("Read", input, fixture.Copy, fixture.MemoryLink, fixture.MemoryShared)
            .Contained.Should().BeTrue(
                "a read of this session's own memory, reached the way the CLI reaches it, must not ask");

        AgentReadScope.Resolve("Read", input, fixture.Copy, fixture.MemoryLink, fixture.MemoryShared)!
            .Containment.Should().Be(AgentTargetContainment.Inside);
    }

    /// <summary>
    /// The exact shape of the #195 trial: a Glob with no <c>path</c> field, whose <c>pattern</c> is the
    /// session-side memory link's own absolute path plus a wildcard. Scoped to the "notes" subfolder
    /// rather than the memory root: the fixture's root also holds a link out of memory (for
    /// <see cref="A_link_inside_memory_that_points_out_of_it_is_not_auto_allowed"/>), and round 7 of this
    /// boundary means a wildcard rooted somewhere that escaping link is reachable from correctly has to
    /// ask, which "notes" (a sibling of it, holding nothing but ordinary files) is provably clear of.
    /// </summary>
    [PlatformOracleFact]
    public void A_glob_of_the_memory_folder_with_a_wildcard_pattern_runs_unattended()
    {
        using var fixture = Fixture();
        var input = Call(("pattern", fixture.MemoryLink + "/notes/*"));

        AgentReadScope.Check("Glob", input, fixture.Copy, fixture.MemoryLink, fixture.MemoryShared)
            .Contained.Should().BeTrue("listing this session's own memory is exactly the call the trial saw ask");
    }

    [PlatformOracleFact]
    public void A_grep_under_the_memory_folder_runs_unattended()
    {
        using var fixture = Fixture();
        var input = Call(("path", Path.Combine(fixture.MemoryLink, "notes")), ("glob", "*.md"));

        AgentReadScope.Check("Grep", input, fixture.Copy, fixture.MemoryLink, fixture.MemoryShared)
            .Contained.Should().BeTrue();
    }

    [PlatformOracleFact]
    public void A_read_of_a_note_that_does_not_exist_yet_under_memory_runs_unattended()
    {
        using var fixture = Fixture();
        var path = Path.Combine(fixture.MemoryLink, "brand-new-note.md");
        File.Exists(path).Should().BeFalse("the case only means something if the leaf is genuinely absent");

        var input = Call(("file_path", path));

        AgentReadScope.Check("Read", input, fixture.Copy, fixture.MemoryLink, fixture.MemoryShared)
            .Contained.Should().BeTrue(
                "a leaf that does not exist yet must not be a way to dodge the check in either direction");
    }

    [PlatformOracleFact]
    public void A_link_inside_memory_that_points_out_of_it_is_not_auto_allowed()
    {
        using var fixture = Fixture();
        var path = Path.Combine(fixture.MemoryLink, "escape", "keys.txt");

        File.ReadAllText(path).Trim().Should().Be(
            "secret", "the case only means something if this really reaches the secrets tree");

        var input = Call(("file_path", path));

        AgentReadScope.Check("Read", input, fixture.Copy, fixture.MemoryLink, fixture.MemoryShared)
            .Contained.Should().BeFalse("a link an agent's own note holds must not be trusted to stay inside memory");

        AgentReadScope.Resolve("Read", input, fixture.Copy, fixture.MemoryLink, fixture.MemoryShared)!
            .Containment.Should().NotBe(AgentTargetContainment.Inside);
    }

    [PlatformOracleTheory]
    [InlineData("../../../settings.json", "the approval hook's settings")]
    [InlineData("../../../approve.sh", "the approval script, which carries the session's secret")]
    [InlineData("../abcd1234.jsonl", "a transcript file right beside the link")]
    public void A_dot_dot_chain_from_the_memory_link_into_the_session_directory_still_asks(
        string suffix, string why)
    {
        using var fixture = Fixture();
        var path = Path.Combine(fixture.MemoryLink, suffix);

        // GROUND TRUTH: the lexical collapse really does land on the sibling file, not on memory.
        var landed = Path.GetFullPath(path);
        File.Exists(landed).Should().BeTrue($"the fixture has to actually hold {suffix} for this to prove anything");
        landed.Should().NotStartWith(fixture.MemoryLink, $"{why} is not under the memory link at all");

        var input = Call(("file_path", path));

        AgentReadScope.Check("Read", input, fixture.Copy, fixture.MemoryLink, fixture.MemoryShared)
            .Contained.Should().BeFalse($"{why} sits right beside the memory link and must keep asking");
    }

    [PlatformOracleFact]
    public void A_pattern_that_climbs_from_memory_into_the_session_directory_still_asks()
    {
        using var fixture = Fixture();
        var input = Call(("path", fixture.MemoryLink), ("pattern", "../../../*.json"));

        AgentReadScope.Check("Glob", input, fixture.Copy, fixture.MemoryLink, fixture.MemoryShared)
            .Contained.Should().BeFalse("the literal prefix collapses off the memory link entirely, landing beside settings.json");
    }

    [PlatformOracleFact]
    public void Another_workspaces_memory_named_directly_is_not_auto_allowed()
    {
        using var fixture = Fixture();
        var path = Path.Combine(fixture.OtherMemoryShared, "proof-marker.md");
        File.Exists(path).Should().BeTrue();

        var input = Call(("file_path", path));

        AgentReadScope.Check("Read", input, fixture.Copy, fixture.MemoryLink, fixture.MemoryShared)
            .Contained.Should().BeFalse("this session's memory root resolves to w1key, never to another workspace's key");

        AgentReadScope.Resolve("Read", input, fixture.Copy, fixture.MemoryLink, fixture.MemoryShared)!
            .Containment.Should().NotBe(AgentTargetContainment.Inside);
    }

    [PlatformOracleFact]
    public void A_memory_read_with_no_memory_root_registered_keeps_asking()
    {
        using var fixture = Fixture();
        var path = Path.Combine(fixture.MemoryLink, "proof-marker.md");

        // No memoryRoot handed in at all: the session was never linked, or the studio never learned
        // about it, and every memory read keeps asking exactly as it did before backlog 202.
        AgentReadScope.Check("Read", Call(("file_path", path)), fixture.Copy).Contained.Should().BeFalse();
    }

    [PlatformOracleFact]
    public void A_memory_root_with_no_pinned_target_is_dropped_and_keeps_asking()
    {
        using var fixture = Fixture();
        var path = Path.Combine(fixture.MemoryLink, "proof-marker.md");

        // The link is named but nothing says where it is SUPPOSED to point, which is exactly the
        // half-registered shape RegisterSession refuses to keep: AgentReadScope has to refuse it too.
        AgentReadScope.Check("Read", Call(("file_path", path)), fixture.Copy, fixture.MemoryLink)
            .Contained.Should().BeFalse();
    }

    /// <summary>
    /// The hole a host review of this work found before it was committed: re-locating the link on every
    /// call is necessary (a studio-recorded canonical answer could itself go stale) but is NOT, on its
    /// own, enough, because the candidate and the root would then both be resolved through the SAME live
    /// link at the SAME moment and necessarily agree with each other regardless of where the link
    /// actually points. One later approved command that re-points the session-side link (standing in for
    /// <c>ln -sfn /data &lt;link&gt;</c>) must not turn every memory read into a read of wherever the
    /// link now leads.
    /// </summary>
    [PlatformOracleFact]
    public void A_memory_link_re_pointed_at_a_parent_folder_is_not_trusted_and_keeps_asking()
    {
        using var fixture = Fixture();
        RepointMemoryLink(fixture, fixture.Root);

        var path = Path.Combine(fixture.MemoryLink, "secrets", "keys.txt");
        File.ReadAllText(path).Trim().Should().Be(
            "secret", "the re-point only means something if it really reaches the secrets tree now");

        var input = Call(("file_path", path));

        // memoryTarget is UNCHANGED: it is what the studio recorded when it linked memory, never
        // re-derived from the link, which is the whole point of pinning it independently.
        AgentReadScope.Check("Read", input, fixture.Copy, fixture.MemoryLink, fixture.MemoryShared)
            .Contained.Should().BeFalse(
                "the link no longer points at the folder the studio registered, so it must not be trusted");
    }

    [PlatformOracleFact]
    public void A_memory_link_re_pointed_at_a_sibling_workspaces_memory_is_not_trusted_and_keeps_asking()
    {
        using var fixture = Fixture();
        RepointMemoryLink(fixture, fixture.OtherMemoryShared);

        var path = Path.Combine(fixture.MemoryLink, "proof-marker.md");
        File.ReadAllText(path).Trim().Should().Be(
            "not yours", "the re-point only means something if it really reaches the other workspace's memory");

        var input = Call(("file_path", path));

        AgentReadScope.Check("Read", input, fixture.Copy, fixture.MemoryLink, fixture.MemoryShared)
            .Contained.Should().BeFalse(
                "a link pointed at a DIFFERENT workspace's memory is not this session's memory, whatever its name");
    }

    /// <summary>
    /// The second step of the same argument, caught by a host review of the first fix before it was
    /// committed: re-pointing the SESSION-SIDE link is not the only way to make the link and its
    /// expected target agree with each other for the wrong reason. Removing the REAL shared folder and
    /// replacing it with a symlink (standing in for <c>rm -r /data/agent-memory/w1key &amp;&amp;
    /// ln -s /data /data/agent-memory/w1key</c>) makes both the link and the registered expected target
    /// resolve to the same place without the link itself ever changing.
    /// </summary>
    [PlatformOracleFact]
    public void A_memory_link_whose_expected_target_is_itself_replaced_by_a_symlink_is_not_trusted_and_keeps_asking()
    {
        using var fixture = Fixture();
        ReplaceWithSymlink(fixture.MemoryShared, fixture.Root);

        var path = Path.Combine(fixture.MemoryLink, "secrets", "keys.txt");
        File.ReadAllText(path).Trim().Should().Be(
            "secret", "the replacement only means something if this really reaches the secrets tree now");

        var input = Call(("file_path", path));

        AgentReadScope.Check("Read", input, fixture.Copy, fixture.MemoryLink, fixture.MemoryShared)
            .Contained.Should().BeFalse(
                "the expected target's own name is now a link, not the fixed folder the studio registered");
    }

    [PlatformOracleFact]
    public void A_memory_link_runs_unattended_again_once_it_points_back_at_the_real_folder()
    {
        using var fixture = Fixture();
        RepointMemoryLink(fixture, fixture.Root);
        RepointMemoryLink(fixture, fixture.MemoryShared);

        var path = Path.Combine(fixture.MemoryLink, "proof-marker.md");
        var input = Call(("file_path", path));

        AgentReadScope.Check("Read", input, fixture.Copy, fixture.MemoryLink, fixture.MemoryShared)
            .Contained.Should().BeTrue(
                "the link points at exactly the folder the studio registered again, so trust is restored");
    }

    [PlatformOracleFact]
    public void An_ordinary_read_inside_the_copy_is_unaffected_by_a_memory_root_being_registered()
    {
        using var fixture = Fixture();
        var input = Call(("file_path", Path.Combine(fixture.Copy, "src", "main.txt")));

        AgentReadScope.Check("Read", input, fixture.Copy, fixture.MemoryLink, fixture.MemoryShared)
            .Contained.Should().BeTrue();
    }

    private static ContainmentFixture Fixture()
    {
        var fixture = ContainmentFixture.Create();
        fixture.HasSymlinks.Should().BeTrue(
            "the memory link is a symlink and this boundary cannot be posed without one");
        return fixture;
    }

    /// <summary>Replaces the session-side memory link with one pointing somewhere else, as one approved
    /// re-linking command would. The registered <c>memoryTarget</c> a caller then passes is UNCHANGED.</summary>
    private static void RepointMemoryLink(ContainmentFixture fixture, string newTarget)
    {
        Directory.Delete(fixture.MemoryLink);
        Directory.CreateSymbolicLink(fixture.MemoryLink, newTarget);
    }

    /// <summary>Replaces a REAL directory with a symlink to somewhere else, as one more conspicuous
    /// approved command (removing a folder and relinking its name) would.</summary>
    private static void ReplaceWithSymlink(string path, string newTarget)
    {
        Directory.Delete(path, recursive: true);
        Directory.CreateSymbolicLink(path, newTarget);
    }

    private static AgentToolInput Call(params (string Field, string Value)[] fields)
    {
        var values = fields.ToDictionary(f => f.Field, f => f.Value, StringComparer.Ordinal);
        return AgentToolInput.Create(values, values.Keys);
    }
}
