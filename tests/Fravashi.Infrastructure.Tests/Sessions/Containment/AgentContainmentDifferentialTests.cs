using System.Globalization;
using AwesomeAssertions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Sessions;
using Xunit;
using Xunit.Abstractions;

namespace Fravashi.Infrastructure.Tests.Sessions.Containment;

/// <summary>A fact that needs the platform oracle, and says why it is skipped when it cannot run.</summary>
public sealed class PlatformOracleFactAttribute : FactAttribute
{
    public PlatformOracleFactAttribute() => Skip = PlatformOracle.SkipReason;
}

/// <summary>The theory form of <see cref="PlatformOracleFactAttribute"/>, for a parametrized case.</summary>
public sealed class PlatformOracleTheoryAttribute : TheoryAttribute
{
    public PlatformOracleTheoryAttribute() => Skip = PlatformOracle.SkipReason;
}

/// <summary>
/// The test that should have existed before any of the four escapes.
/// <para>
/// Every one of them had the same shape: the studio's answer about where a path lands disagreed with
/// where the path really lands. No unit test could see that, because the unit tests asserted the
/// studio's model back to itself. An assertion written from the same understanding as the code cannot
/// notice that the understanding is wrong.
/// </para>
/// <para>
/// So this test never states where a path should land. It generates a corpus of adversarial calls
/// (<see cref="ContainmentCorpus"/>), asks the OPERATING SYSTEM what each one would really reach over a
/// real tree (<see cref="PlatformOracle"/>), asks the studio what it believes, and compares.
/// </para>
/// <para>
/// The comparison is ONE-SIDED on purpose, because the two mistakes are not each other's mirror image.
/// A verdict of "ask" over a call that would have been harmless costs the user one click. A verdict of
/// "contained" over a call that leaves the copy hands over the bridge token, the database and the key
/// ring with nobody in the loop. So the only thing asserted is the dangerous direction: the studio must
/// never say contained when the platform says the call reaches something outside the copy, or when
/// there is no path there to place at all. Over-asking is measured and reported, never failed.
/// </para>
/// <para>
/// This test's only real home is LINUX, and on the Windows dev host it skips with its reason, so a
/// green suite there has not measured this boundary at all. README.md beside this file has the exact
/// commands for running it where it runs for real; the studio image ships no SDK, so it takes an SDK
/// container over the repository rather than the studio's own.
/// </para>
/// <para>
/// Both of the studio's containment answers are held to it: <see cref="AgentReadScope.Check"/>, which
/// decides whether a read runs UNATTENDED, and <see cref="AgentReadScope.Resolve"/>, which is what the
/// approval prompt tells the user about a call they are being asked to allow. A prompt that says
/// "inside the copy" over a path that is not is the same breach with an extra click in it.
/// </para>
/// </summary>
public class AgentContainmentDifferentialTests
{
    private readonly ITestOutputHelper _output;

    public AgentContainmentDifferentialTests(ITestOutputHelper output) => _output = output;

    [PlatformOracleFact]
    public void No_adversarial_call_is_called_contained_when_the_platform_says_it_leaves_the_copy()
    {
        using var fixture = ContainmentFixture.Create();
        var corpus = ContainmentCorpus.Generate(fixture);

        // Backlog 202 (and 338's own third root): ground truth's "inside" region is the copy UNION this
        // session's own agent memory UNION its own saved tool output, the same roots
        // AgentReadScope.Check/Resolve are handed below, and for the same reason: a call is contained
        // the moment it lands in any one of them.
        var truth = PlatformOracle.Judge(
            fixture.Copy, corpus, fixture.HasSymlinks ? fixture.MemoryShared : null, fixture.ToolResults);

        var breaches = new List<string>();
        var notJudged = new List<string>();
        var counts = new Dictionary<PlatformTruth, int>();
        var overAsked = 0;

        foreach (var probe in corpus)
        {
            var verdict = truth[probe.Index];
            counts[verdict] = counts.GetValueOrDefault(verdict) + 1;

            // A case the platform did not place is NOT a quiet pass. The oracle used to reach
            // "nothing was reachable" by exhaustion and park it in a bucket nothing is asserted
            // against, which is how it came to share the exact blind spot of the code it audits.
            // So the two are separate answers now and both of them are reported.
            if (verdict is PlatformTruth.Unknown or PlatformTruth.Undetermined)
            {
                notJudged.Add($"{verdict}: {probe.Describe()}");
                continue;
            }

            var input = probe.ToInput();
            var memoryRoot = fixture.HasSymlinks ? fixture.MemoryLink : null;
            var memoryTarget = fixture.HasSymlinks ? fixture.MemoryShared : null;
            var scope = AgentReadScope.Check(probe.Tool, input, fixture.Copy, memoryRoot, memoryTarget, fixture.ToolResults);
            var shown = AgentReadScope.Resolve(probe.Tool, input, fixture.Copy, memoryRoot, memoryTarget, fixture.ToolResults);

            var dangerous = verdict is PlatformTruth.Escapes or PlatformTruth.NotAPath;
            if (dangerous && scope.Contained)
            {
                breaches.Add($"AUTO-ALLOWED {probe.Describe()} [platform: {verdict}] -> {scope.Reason}");
            }

            if (dangerous && shown?.Containment == AgentTargetContainment.Inside)
            {
                breaches.Add(
                    $"PROMPTED AS INSIDE {probe.Describe()} [platform: {verdict}] -> {shown.Canonical}");
            }

            if (verdict == PlatformTruth.Inside && !scope.Contained)
            {
                overAsked++;
            }
        }

        Report(fixture, corpus, counts, overAsked, notJudged, breaches);

        notJudged.Should().BeEmpty(
            "the platform has to answer for every case; a case it could not place is a hole in the "
            + "oracle, not a pass, and an oracle that cannot see a construction cannot fail over it:\n"
            + string.Join("\n", notJudged.Take(40)));

        breaches.Should().BeEmpty(
            "a call the operating system says reaches outside the contained copy must never be auto-allowed "
            + "or described to the user as inside it. Each line below is a live escape:\n"
            + string.Join("\n", breaches));
    }

    /// <summary>
    /// The corpus itself is load-bearing, so it is checked rather than trusted: a generator that
    /// quietly stopped producing the constructions that broke this boundary would turn the differential
    /// green without changing a thing about the boundary.
    /// </summary>
    [Fact]
    public void The_corpus_still_contains_every_construction_that_has_broken_this_boundary()
    {
        using var fixture = ContainmentFixture.Create();
        var corpus = ContainmentCorpus.Generate(fixture);

        _output.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"corpus: {corpus.Count} cases"));
        _output.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"fixture: symlinks={fixture.HasSymlinks} backslash names={fixture.HasBackslashNames}"));

        // Not tied to symlinks or to POSIX filenames, so these must be present on every platform.
        var required = new[]
        {
            "padding-120",          // "./" padding long enough to cross the display cap (round 2)
            "backslash-value",      // backslashes in the path argument (round 4)
            "brace",                // brace expansion (round 3)
            "nested-brace",
            "character-class",
            "extglob",
            "escaped-dot",          // a backslash-escaped dot spelling ".."
            "climb-5",              // exactly as deep as the deepest real directory the corpus starts from
            "climb-9",              // more ".." than the copy is deep
            "absolute",
            "trailing-separator",
            "prefix-neighbour",     // /workspaces/w10 measured against /workspaces/w1
            "missing-leaf",
            "missing-parent",
            "non-string-path",      // a JSON value that is not a string where a path was expected
            "non-string-pattern",

            // Backlog 338: this session's own saved tool output needs no symlink support to exist at all,
            // unlike memory's own session-side link, so these are required on every platform too.
            "toolresults-inside",
            "toolresults-missing-leaf",
            "toolresults-other-session",
            "toolresults-session-climb",
        };

        foreach (var tag in required)
        {
            corpus.Count(c => c.Tags.Contains(tag))
                .Should().BeGreaterThan(0, $"the corpus must still generate {tag} cases");
        }

        if (fixture.HasSymlinks)
        {
            corpus.Count(c => c.Tags.Contains("symlink-deeper-inside")).Should().BeGreaterThan(0);
            corpus.Count(c => c.Tags.Contains("symlink-outside")).Should().BeGreaterThan(0);
            corpus.Count(c => c.Tags.Contains("symlink-loop")).Should().BeGreaterThan(0);
            corpus.Count(c => c.Tags.Contains("dangling-symlink")).Should().BeGreaterThan(0);

            // Round 6: a link going deeper IN, then a ".." , then a link going OUT. Each reading stays
            // inside on its own and only the composition of the two leaves, so this construction has to
            // keep being generated or the differential goes quiet about the whole family.
            corpus.Count(c => c.Tags.Contains("symlink-deeper-inside") && c.Tags.Contains("through-escaping-link"))
                .Should().BeGreaterThan(0);

            // The same shape as a PATTERN, which is the half that is live: a file_path arrives with
            // its ".." already collapsed by the CLI, patterns demonstrably do not. The corpus emitted
            // the roots and the escaping link and never crossed them here, so the whole family existed
            // only as one hand-written case.
            corpus.Count(c =>
                    c.Shape == ContainmentShape.Pattern
                    && c.Tags.Contains("symlink-deeper-inside")
                    && c.Tags.Contains("through-escaping-link"))
                .Should().BeGreaterThan(
                    0, "a pattern that climbs out of a link going deeper in has to keep being generated");

            // The depth that joins a root the corpus already emits to a link it already emits. Five
            // is how far "src/deep/a/b/c/" climbs to reach the copy root, and it was the one depth
            // missing from a list that looked exhaustive.
            corpus.Count(c => c.Tags.Contains("climb-5") && c.Tags.Contains("through-escaping-link"))
                .Should().BeGreaterThan(0);

            // Round 7: a wildcard MATCHING the escaping link's name, with a literal tail behind it so
            // the engine traverses through it. The previous round crossed the escaping link with tails
            // that SPELL it; nothing generated the half where the pattern spells nothing at all and the
            // name comes from the directory, which is the half no "does this segment spell .." rule
            // can ever see.
            corpus.Count(c => c.Tags.Contains("wildcard-matched-link"))
                .Should().BeGreaterThan(
                    0, "a wildcard that can match a link out of the copy has to keep being generated");

            corpus.Count(c => c.Tags.Contains("wildcard-matched-link") && c.Tags.Contains("no-path-field"))
                .Should().BeGreaterThan(
                    0, "and crossed with the call shape the agent sends most often, which names no path");

            foreach (var spelling in new[] { "star", "globstar", "question-mark", "character-class", "extglob" })
            {
                corpus.Count(c => c.Tags.Contains("wildcard-matched-link") && c.Tags.Contains(spelling))
                    .Should().BeGreaterThan(0, $"a {spelling} segment matches a name just as well");
            }

            // Backlog 202: the new root. A plain read inside it, a leaf that does not exist yet, a link
            // inside it pointing out, a ".." chain climbing into the session directory, and another
            // workspace's key named directly all have to keep being generated, as both a path and (where
            // it fits) a pattern.
            foreach (var tag in new[]
                     {
                         "memory-inside", "memory-missing-leaf", "memory-link-out",
                         "memory-session-climb", "memory-other-key",
                     })
            {
                corpus.Count(c => c.Tags.Contains(tag))
                    .Should().BeGreaterThan(0, $"the corpus must still generate {tag} cases");
            }

            corpus.Count(c => c.Shape == ContainmentShape.Pattern && c.Tags.Contains("memory-link-out"))
                .Should().BeGreaterThan(
                    0, "a wildcard rooted where memory's own escaping link is reachable must keep asking too");

            // Backlog 338: the third root. Its own escaping link needs a real symlink to exist at all, so
            // this one case (unlike the four added to `required` above) is gated here.
            corpus.Count(c => c.Tags.Contains("toolresults-link-out"))
                .Should().BeGreaterThan(0, "the corpus must still generate toolresults-link-out cases");

            corpus.Count(c => c.Shape == ContainmentShape.Pattern && c.Tags.Contains("toolresults-link-out"))
                .Should().BeGreaterThan(
                    0, "a wildcard rooted where tool-results' own escaping link is reachable must keep asking too");
        }

        if (fixture.HasBackslashNames)
        {
            corpus.Count(c => c.Tags.Contains("backslash-entry-name")).Should().BeGreaterThan(0);
        }

        // Generated, not hand-written: a corpus small enough to have been typed out is a corpus that
        // only holds the escapes somebody already thought of.
        corpus.Should().HaveCountGreaterThan(
            500, "the corpus is a cross product of constructions, not a list of remembered incidents");

        corpus.Select(c => c.Tool).Distinct().Should().HaveCountGreaterThan(
            4, "every auto-allowed tool that touches the filesystem has to be measured");
    }

    private void Report(
        ContainmentFixture fixture,
        IReadOnlyList<ContainmentCase> corpus,
        IReadOnlyDictionary<PlatformTruth, int> counts,
        int overAsked,
        IReadOnlyList<string> notJudged,
        IReadOnlyList<string> breaches)
    {
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"copy:    {fixture.Copy}"));
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"corpus:  {corpus.Count} cases"));
        foreach (var truth in Enum.GetValues<PlatformTruth>())
        {
            _output.WriteLine(string.Create(
                CultureInfo.InvariantCulture, $"  platform says {truth,-12}: {counts.GetValueOrDefault(truth)}"));
        }

        // The size of the bucket nothing is asserted against is itself a measurement. NoReach is a
        // real answer ("an open of this returns nothing"), but a large one means most of the corpus
        // is proving less than the case count suggests, so it is stated rather than left implicit.
        var noReach = counts.GetValueOrDefault(PlatformTruth.NoReach);
        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"not asserted against, the platform reaches nothing at all: {noReach} of {corpus.Count}"));
        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture, $"NOT JUDGED, a hole in the oracle rather than a pass: {notJudged.Count}"));

        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"over-asked, a nuisance and never a failure (platform says inside, studio still asks): {overAsked}"));
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"breaches: {breaches.Count}"));

        foreach (var line in notJudged.Take(20))
        {
            _output.WriteLine("  " + line);
        }

        foreach (var breach in breaches.Take(40))
        {
            _output.WriteLine("  " + breach);
        }
    }
}
