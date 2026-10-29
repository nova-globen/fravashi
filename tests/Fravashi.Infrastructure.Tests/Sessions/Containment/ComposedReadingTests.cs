using AwesomeAssertions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Sessions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions.Containment;

/// <summary>
/// Round SIX, named and pinned: the containment check required each of its two readings to be inside
/// the copy INDEPENDENTLY, and the reading a caller actually performs is neither of them but their
/// COMPOSITION.
/// <para>
/// A real opener collapses <c>..</c> as string arithmetic and hands the RESULT to the kernel. So "the
/// as-written walk stays inside" and "the collapsed spelling stays inside" together say nothing about
/// where the collapsed spelling LANDS: it can name a symlink the as-written walk never reaches. With
/// <c>nested</c> pointing deeper inside the copy and <c>out</c> pointing out of it,
/// <c>nested/../out/keys.txt</c> was auto-allowed and read the real secret.
/// </para>
/// <para>
/// Nothing here states where a path should land. Every case reads its ground truth from a REAL opener
/// over the real fixture tree first (<c>File.ReadAllText</c> and <c>Directory.GetFiles</c> both resolve
/// then open, exactly as node's <c>path.resolve</c> plus <c>fs.open</c> and python's
/// <c>os.path.abspath</c> plus <c>open</c> do, all three measured in the studio's container on
/// 2026-08-06), and only then asks the studio what it believes. If the platform stopped behaving that
/// way, these would fail on the ground-truth line rather than quietly pass.
/// </para>
/// <para>
/// The last one is the control. This must not become a blanket deny: a link that stays inside the copy
/// still runs unattended, which is the point of having a contained copy at all.
/// </para>
/// </summary>
public class ComposedReadingTests
{
    /// <summary>What the fixture writes inside the copy, and what it writes outside it.</summary>
    private const string InsideText = "inside";

    private const string SecretText = "secret";

    /// <summary>The construction, spelled once: through a link that goes in, then out through one that leaves.</summary>
    private const string Escape = "nested/../out/keys.txt";

    [PlatformOracleFact]
    public void A_read_whose_dot_dot_is_collapsed_onto_a_link_out_of_the_copy_is_not_auto_allowed()
    {
        using var fixture = Fixture();

        // GROUND TRUTH, from an opener rather than from a model of one. Neither reading taken alone
        // leaves the copy: as written the kernel climbs out of where "nested" landed onto a name that
        // is not there, and the collapsed spelling is "<copy>/out/keys.txt".
        File.ReadAllText(Path.Combine(fixture.Copy, Escape)).Trim().Should().Be(
            SecretText,
            "the case only means something if a real read of this value returns the file OUTSIDE the copy");
        File.ReadAllText(Path.Combine(fixture.Copy, "nested", "keys.txt")).Trim().Should().Be(
            InsideText, "and the in-copy file of the same name has to be distinguishable from it");

        var input = Call(("file_path", Escape));

        AgentReadScope.Check("Read", input, fixture.Copy).Contained.Should().BeFalse(
            "a read that returns a file outside the contained copy must never run unattended");

        AgentReadScope.Resolve("Read", input, fixture.Copy)!.Containment.Should().NotBe(
            AgentTargetContainment.Inside, "and the user must not be told it lands inside the copy either");
    }

    /// <summary>
    /// The same shape through a PATTERN, which is the half that is live today: the CLI collapses
    /// <c>..</c> in a <c>file_path</c> before the hook fires, patterns demonstrably arrive
    /// un-collapsed, and <c>Place</c> resolves a pattern's literal prefix through the same reading.
    /// </summary>
    [PlatformOracleFact]
    public void A_pattern_whose_literal_prefix_collapses_onto_a_link_out_of_the_copy_is_not_auto_allowed()
    {
        using var fixture = Fixture();

        // GROUND TRUTH: where a walker that resolves "nested/../out" as text really ends up.
        var prefix = Path.GetFullPath(Path.Combine(fixture.Copy, "nested", "..", "out"));
        Directory.GetFiles(prefix).Select(file => File.ReadAllText(file).Trim())
            .Should().NotBeEmpty("the prefix has to reach real files for the case to mean anything")
            .And.AllBe(SecretText, "and every one of them is outside the copy");

        var input = Call(("path", "nested"), ("pattern", "../out/*"));

        AgentReadScope.Check("Glob", input, fixture.Copy).Contained.Should().BeFalse(
            "a pattern whose prefix collapses onto a link out of the copy must never run unattended");

        AgentReadScope.Resolve("Glob", input, fixture.Copy)!.Containment.Should().NotBe(
            AgentTargetContainment.Inside);
    }

    /// <summary>
    /// The prompt's own honesty, held to the same standard as the machine gate. On this shape the
    /// requested value and the as-written destination are BOTH in-copy names, so a prompt showing only
    /// those would flag "outside" over two reassuring paths and name the real destination nowhere.
    /// </summary>
    [PlatformOracleFact]
    public void The_prompt_names_the_destination_the_collapsed_spelling_really_reaches()
    {
        using var fixture = Fixture();

        var target = AgentReadScope.Resolve("Read", Call(("file_path", Escape)), fixture.Copy);

        target.Should().NotBeNull();
        target!.Containment.Should().Be(AgentTargetContainment.Outside);
        target.Collapsed.Should().NotBeNullOrWhiteSpace(
            "the reading that leaves the copy is the one the user has to be shown");
        target.Collapsed.Should().StartWith(
            fixture.Secrets, "and it is the secrets tree, which is what the read would really have opened");
    }

    /// <summary>
    /// NOT a blanket deny. A link inside the copy, a chain of two, and a <c>..</c> that climbs back
    /// through one are all ordinary in a real working copy, and every one still runs unattended.
    /// </summary>
    [PlatformOracleFact]
    public void A_legitimate_path_through_a_link_that_stays_inside_the_copy_still_runs_unattended()
    {
        using var fixture = Fixture();

        string[] honest =
        [
            "nested/keys.txt",       // straight through a link deeper inside the copy
            "hop/keys.txt",          // through a chain of two of them
            "nested/c/leaf.txt",     // below the link's destination
            "nested/../src",         // the escape's own shape, landing on a real in-copy directory
            "nested/../src/main.txt",
            "src/deep/a/b/keys.txt", // the same file spelled with no link at all
        ];

        foreach (var value in honest)
        {
            // GROUND TRUTH first: the collapsed spelling really is an in-copy destination.
            var landed = Path.GetFullPath(Path.Combine(fixture.Copy, value));
            (File.Exists(landed) || Directory.Exists(landed)).Should().BeTrue(
                $"the fixture has to actually hold {value} for allowing it to prove anything");

            AgentReadScope.Check("LS", Call(("path", value)), fixture.Copy).Contained.Should().BeTrue(
                $"{value} stays inside the contained copy under every reading, so it must not start asking");
        }

        // A glob over the copy, and one over a link inside it, both stay unattended.
        AgentReadScope.Check("Glob", Call(("path", "nested"), ("pattern", "**/*.txt")), fixture.Copy)
            .Contained.Should().BeTrue("an ordinary pattern under an in-copy link is not an escape");
    }

    private static ContainmentFixture Fixture()
    {
        var fixture = ContainmentFixture.Create();
        fixture.HasSymlinks.Should().BeTrue(
            "this escape is spelled with symlinks and cannot be posed without them");
        return fixture;
    }

    private static AgentToolInput Call(params (string Field, string Value)[] fields)
    {
        var values = fields.ToDictionary(f => f.Field, f => f.Value, StringComparer.Ordinal);
        return AgentToolInput.Create(values, values.Keys);
    }
}
