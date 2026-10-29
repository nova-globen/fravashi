using System.Diagnostics;
using AwesomeAssertions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Sessions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// The read-only auto-allow is scoped to the session's contained copy. "Read-only" was never the same
/// as "harmless": the agent runs in the studio's own container as the studio's own uid, so an unscoped
/// <c>Read</c> reaches <c>/proc/1/environ</c> (the host-bridge URL and token, the window token, the
/// integrity key), <c>/data/fravashi.db</c>, and the Data Protection key ring that decrypts the stored
/// provider credentials. Every one of those is a path an approved-without-reading tool call would have
/// handed over in silence.
/// <para>
/// So these tests are about the boundary, not the tool name: inside the copy runs unattended, and
/// anything that leaves it, or that cannot be pinned down at all, asks.
/// </para>
/// </summary>
public class AgentReadScopeTests : IDisposable
{
    private const string Copy = "/workspaces/w1";

    private readonly string _scratch =
        Path.Combine(Path.GetTempPath(), "fravashi-readscope-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            if (Directory.Exists(_scratch))
            {
                Directory.Delete(_scratch, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static AgentReadVerdict Check(string tool, string root, params string[] pairs)
    {
        var input = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < pairs.Length; i += 2)
        {
            input[pairs[i]] = pairs[i + 1];
        }

        return AgentReadScope.Check(tool, input, root);
    }

    // ---- inside the copy ------------------------------------------------------------------

    [Fact]
    public void A_read_inside_the_copy_still_runs_unattended()
    {
        var verdict = Check("Read", Copy, "file_path", "/workspaces/w1/src/Program.cs");

        verdict.Contained.Should().BeTrue();
        verdict.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void The_copy_itself_may_be_listed()
    {
        Check("LS", Copy, "path", Copy).Contained.Should().BeTrue();
        Check("LS", Copy, "path", Copy + "/").Contained.Should().BeTrue();
    }

    [Fact]
    public void A_relative_path_resolves_against_the_copy_the_child_runs_in()
    {
        // The CLI resolves a relative argument against the child's working directory, which IS the copy.
        Check("Read", Copy, "file_path", "src/Program.cs").Contained.Should().BeTrue();
        Check("Read", Copy, "file_path", "./docs/notes.md").Contained.Should().BeTrue();
    }

    [Fact]
    public void A_search_without_a_path_stays_in_the_working_directory()
    {
        // Grep and Glob default their path to the child's cwd, which is the copy.
        Check("Grep", Copy, "pattern", "TODO").Contained.Should().BeTrue();
        Check("Glob", Copy, "pattern", "**/*.cs").Contained.Should().BeTrue();
    }

    [Fact]
    public void A_glob_pattern_resolves_against_the_folder_the_call_names()
    {
        // ../lib from /workspaces/w1/src is still inside the copy, so it must not become a prompt.
        Check("Glob", Copy, "path", "/workspaces/w1/src", "pattern", "../lib/**/*.cs")
            .Contained.Should().BeTrue();
    }

    [Fact]
    public void A_tool_that_reads_nothing_off_the_disk_needs_no_path_at_all()
    {
        Check("ToolSearch", Copy, "query", "notebook").Contained.Should().BeTrue();
        Check("TaskList", Copy).Contained.Should().BeTrue();

        // ... and it is still fine when the studio does not know the copy: there is nothing to contain.
        AgentReadScope.Check("TaskList", new Dictionary<string, string>(), workspaceRoot: null)
            .Contained.Should().BeTrue();
    }

    // ---- out of the copy ------------------------------------------------------------------

    [Fact]
    public void A_read_of_pid_ones_environment_is_never_unattended()
    {
        // The whole point: PID 1 in this container holds FRAVASHI_BRIDGE_URL / FRAVASHI_BRIDGE_TOKEN,
        // the window token and the integrity key, and the agent shares its uid.
        var verdict = Check("Read", Copy, "file_path", "/proc/1/environ");

        verdict.Contained.Should().BeFalse();
        verdict.Reason.Should().Contain("OUTSIDE");
        verdict.Reason.Should().Contain("/proc/1/environ");
    }

    [Theory]
    [InlineData("/data/fravashi.db")]
    [InlineData("/data/keys/key-8c2f.xml")]
    [InlineData("/data/agent-sessions/abc/settings.json")]
    [InlineData("/data/logs/studio-2026-08-05.log")]
    public void A_read_of_the_studios_own_data_directory_is_never_unattended(string path) =>
        Check("Read", Copy, "file_path", path).Contained.Should().BeFalse();

    [Theory]
    [InlineData("/workspaces/w1/../../proc/1/environ")]
    [InlineData("/workspaces/w1/src/../../../data/fravashi.db")]
    [InlineData("../../proc/1/environ")]
    [InlineData("../w2/secrets.txt")]
    [InlineData("src/../../../etc/shadow")]
    public void A_traversal_out_of_the_copy_is_never_unattended(string path) =>
        Check("Read", Copy, "file_path", path).Contained.Should().BeFalse();

    [Fact]
    public void A_neighbour_whose_name_merely_starts_the_same_is_outside()
    {
        // A string prefix is not containment: /workspaces/w10 is a different workspace's copy.
        Check("Read", Copy, "file_path", "/workspaces/w10/secrets.txt").Contained.Should().BeFalse();
    }

    [Theory]
    [InlineData("Read", "file_path")]
    [InlineData("NotebookRead", "notebook_path")]
    [InlineData("LS", "path")]
    [InlineData("Grep", "path")]
    [InlineData("Glob", "path")]
    public void Every_read_tool_is_scoped_by_its_own_argument_name(string tool, string field)
    {
        // Naming the path differently must not walk past the boundary.
        Check(tool, Copy, field, "/workspaces/w1/inside").Contained.Should().BeTrue();
        Check(tool, Copy, field, "/proc/1/environ").Contained.Should().BeFalse();
    }

    [Fact]
    public void A_glob_pattern_that_traverses_out_is_never_unattended()
    {
        // The pattern is a path too, so wildcards do not excuse a "..".
        Check("Glob", Copy, "pattern", "../../proc/*/environ").Contained.Should().BeFalse();
        Check("Glob", Copy, "path", "/workspaces/w1/src", "pattern", "../../../data/*.db")
            .Contained.Should().BeFalse();
        Check("Glob", Copy, "pattern", "/data/**").Contained.Should().BeFalse();
    }

    // ---- a pattern is glob syntax, not two wildcards --------------------------------------
    //
    // Measured against the CLI in the container (claude-code 2.1.222, 2026-08-05): its Glob DOES
    // brace-expand and DOES honour character classes, it DOES match an absolute pattern anywhere on
    // disk, and it does NOT climb with a ".." (from /tmp/probe/a/b, "../inner.txt" and
    // "../../marker-*.txt" both returned nothing while "/tmp/probe/marker-*.txt" returned the file).
    // So the traversal spellings below do not all reach out of the copy on THAT build. The boundary
    // is not written against that build: a containment that only holds while one vendor's walker
    // happens to clamp "..", and that reads the pattern with a different grammar than the engine
    // does, is not a containment.

    [Theory]
    [InlineData("{..,zz}/{..,zz}/data/*.db")]
    [InlineData("{..,zz}/{..,zz}/keys/*.xml")]
    [InlineData("src/{..,zz}/{..,zz}/{..,zz}/proc/*/environ")]
    [InlineData("{.,x}{.,x}/{.,x}{.,x}/data/*.db")]
    // An absolute path is what that CLI's Glob does honour, and a brace list hides one just as well.
    [InlineData("{/data,zz}/keys/*.xml")]
    [InlineData("{/proc,zz}/1/environ")]
    public void A_brace_alternative_that_spells_a_traversal_is_never_unattended(string pattern)
    {
        // The engine brace-expands BEFORE it matches, so "{..,zz}" is one of the ways a pattern
        // writes "..". Replacing only "*" and "?" leaves that traversal looking like two ordinary
        // directory names.
        Check("Glob", Copy, "pattern", pattern).Contained.Should().BeFalse();
    }

    [Theory]
    [InlineData(".\\./.\\./data/*.db")]
    [InlineData("\\.\\./\\.\\./data/*.db")]
    [InlineData("src/.\\./.\\./.\\./proc/*/environ")]
    public void A_backslash_escaped_traversal_in_a_pattern_is_never_unattended(string pattern)
    {
        // A backslash escapes the next character, so ".\." is the two-character name ".." and the
        // engine traverses on it. (On Windows the platform's own parser already reads a backslash as
        // a separator, so the second spelling is caught twice over.)
        Check("Glob", Copy, "pattern", pattern).Contained.Should().BeFalse();
    }

    [Theory]
    [InlineData("[.][.]/[.][.]/data/*.db")]
    [InlineData("@(..|zz)/@(..|zz)/data/*.db")]
    [InlineData("+(..|zz)/+(..|zz)/data/*.db")]
    [InlineData("!(..)/!(..)/data/*.db")]
    public void A_wildcard_that_spells_a_traversal_is_never_unattended(string pattern)
    {
        // Character classes and extglob groups are matched against real directory entries, which
        // never include "..", so these cannot actually escape today. The studio still asks: what a
        // wildcard will match is not knowable here, and guessing is how the earlier hole was written.
        Check("Glob", Copy, "pattern", pattern).Contained.Should().BeFalse();
    }

    [Theory]
    [InlineData("**/*.cs")]
    [InlineData("src/**/*.{cs,razor}")]
    [InlineData("[abc]*.txt")]
    [InlineData("@(src|docs)/**")]
    [InlineData("src/!(bin)/**/*.cs")]
    [InlineData("../lib/**/*.{cs,razor}")]
    public void An_ordinary_pattern_that_stays_in_the_copy_still_runs_unattended(string pattern) =>
        Check("Glob", Copy, "path", "/workspaces/w1/src", "pattern", pattern)
            .Contained.Should().BeTrue();

    [Fact]
    public void A_traversal_hiding_behind_a_wildcard_is_never_unattended()
    {
        // Everything after the first wildcard is unknowable, so a ".." out there is not judged, it
        // is asked about.
        Check("Glob", Copy, "pattern", "*/../../data/*.db").Contained.Should().BeFalse();
        Check("Glob", Copy, "pattern", "**/../../../proc/1/environ").Contained.Should().BeFalse();
    }

    [Fact]
    public void Greps_own_pattern_is_the_regex_it_searches_for_not_a_path()
    {
        // Grep's "pattern" is content, not a location: brace syntax in it is a regex quantifier and
        // must never be read as a traversal.
        Check("Grep", Copy, "pattern", "{..,zz}/{..,zz}/data").Contained.Should().BeTrue();
        Check("Grep", Copy, "pattern", "a{2,3}b").Contained.Should().BeTrue();
    }

    [Fact]
    public void Greps_glob_filter_is_contained_like_any_other_pattern()
    {
        Check("Grep", Copy, "glob", "**/*.cs").Contained.Should().BeTrue();
        Check("Grep", Copy, "glob", "!{bin,obj}/**").Contained.Should().BeTrue();
        Check("Grep", Copy, "glob", "{..,zz}/{..,zz}/data/*.db").Contained.Should().BeFalse();
        Check("Grep", Copy, "glob", "/data/**").Contained.Should().BeFalse();
    }

    // ---- what cannot be worked out ---------------------------------------------------------

    [Theory]
    [InlineData("Read")]
    [InlineData("NotebookRead")]
    [InlineData("LS")]
    public void A_read_that_does_not_say_which_path_it_opens_asks(string tool)
    {
        // Fail closed: the studio must never guess a path on the agent's behalf.
        Check(tool, Copy, "offset", "0").Contained.Should().BeFalse();
        Check(tool, Copy, "file_path", "   ", "notebook_path", "", "path", "").Contained.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("workspaces/w1")]
    public void Without_a_known_copy_no_file_read_is_unattended(string? root)
    {
        var verdict = AgentReadScope.Check(
            "Read", new Dictionary<string, string> { ["file_path"] = "/workspaces/w1/a.cs" }, root);

        verdict.Contained.Should().BeFalse();
        verdict.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("Write")]
    [InlineData("Bash")]
    [InlineData("WebFetch")]
    [InlineData("SomeToolTheCliGrewLater")]
    public void A_tool_that_is_not_on_the_read_only_list_is_not_made_one_by_a_contained_path(string tool) =>
        Check(tool, Copy, "file_path", "/workspaces/w1/a.cs").Contained.Should().BeFalse();

    // ---- symlinks (a real reparse point, not a lexical one) ---------------------------------

    [Fact]
    public void A_link_planted_inside_the_copy_is_followed_out_of_it_and_asks()
    {
        var copy = NewDirectory("workspaces", "w1");
        var outside = NewDirectory("host-secrets");
        File.WriteAllText(Path.Combine(outside, "bridge.token"), "a-real-token");

        LinkDirectory(Path.Combine(copy, "escape"), outside);

        // Lexically this is inside the copy; really it is not, and the boundary follows the truth.
        Check("Read", copy, "file_path", Path.Combine(copy, "escape", "bridge.token"))
            .Contained.Should().BeFalse();
        Check("LS", copy, "path", Path.Combine(copy, "escape")).Contained.Should().BeFalse();
    }

    [Fact]
    public void A_link_that_stays_inside_the_copy_still_runs_unattended()
    {
        var copy = NewDirectory("workspaces", "w1");
        var inside = Directory.CreateDirectory(Path.Combine(copy, "lib")).FullName;
        File.WriteAllText(Path.Combine(inside, "a.cs"), "// hello");

        LinkDirectory(Path.Combine(copy, "shortcut"), inside);

        Check("Read", copy, "file_path", Path.Combine(copy, "shortcut", "a.cs"))
            .Contained.Should().BeTrue();
    }

    [Fact]
    public void A_real_file_inside_a_real_copy_is_not_disturbed_by_the_link_walk()
    {
        var copy = NewDirectory("workspaces", "w1");
        var file = Path.Combine(copy, "src", "Program.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "// hello");

        Check("Read", copy, "file_path", file).Contained.Should().BeTrue();
    }

    // ---- the studio's arithmetic must not disagree with the platform's ----------------------
    //
    // Round 4. Two escapes, one root cause: the containment was decided by path arithmetic done HERE,
    // and it disagreed with the thing that actually opens the file. Both are now judged by asking the
    // OS (realpath / GetFinalPathNameByHandle) AND by the calling program's own lexical arithmetic,
    // with containment required of both.

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(40)]
    public void A_pattern_climbs_from_the_depth_the_platform_gives_a_path_not_from_a_split_on_backslashes(int depth)
    {
        // Measured in the studio's own Linux container: Path.GetFullPath("/w/w1/a\b\c") yields ONE
        // component named "a\b\c", because a backslash is an ordinary filename character there. The
        // studio nonetheless SPLIT on it, so a one-deep base was measured as `depth` deep and a climb
        // of `depth` levels landed back inside the copy however far it really reached. Nothing had to
        // exist for that, and it did not stop scaling.
        //
        // On Windows those backslashes really are separators, the base really is `depth` deep, and the
        // same climb really does land back inside. That is the platform's own truth, not an escape, so
        // the expectation follows the platform rather than pretending one rule fits both.
        var deep = string.Join('\\', Enumerable.Range(0, depth).Select(i => "d" + i));
        var climb = string.Concat(Enumerable.Repeat("../", depth)) + "data/keys/*.xml";

        Check("Glob", Copy, "path", deep, "pattern", climb)
            .Contained.Should().Be(OperatingSystem.IsWindows());
    }

    [PosixFact]
    public void A_link_whose_own_name_holds_a_backslash_is_still_followed_out_of_the_copy()
    {
        // The other half of the same bug. The studio split "x\y" into two components and stat'ed
        // "<copy>/x" and "<copy>/x/y", neither of which is a link, so one approved
        //     ln -s /data '<copy>/x\y'
        // made every later read through it unattended forever, while the kernel opened straight into
        // the target. The name the kernel sees is the name that gets asked about now.
        var copy = NewDirectory("workspaces", "w1");
        var outside = NewDirectory("host-secrets");
        Directory.CreateDirectory(Path.Combine(outside, "keys"));
        File.WriteAllText(Path.Combine(outside, "keys", "key.xml"), "<key/>");

        Directory.CreateSymbolicLink(Path.Combine(copy, "x\\y"), outside);

        Check("Read", copy, "file_path", "x\\y/keys/key.xml").Contained.Should().BeFalse();
        Check("LS", copy, "path", "x\\y").Contained.Should().BeFalse();
    }

    [Fact]
    public void A_link_to_a_deeper_folder_inside_the_copy_buys_a_pattern_no_extra_climb()
    {
        // A link that stays INSIDE the copy still moved the boundary: the studio measured the
        // pattern's ".." from the link's TARGET (three levels down) while a glob walker counts them
        // from the name it was handed. Same string, two answers, and the walker's answer was outside.
        var copy = NewDirectory("workspaces", "w1");
        var deep = NewDirectory("workspaces", "w1", "a", "b", "c");
        LinkDirectory(Path.Combine(copy, "shortcut"), deep);

        Check("Glob", copy, "path", Path.Combine(copy, "shortcut"), "pattern", "../../../data/keys/*.xml")
            .Contained.Should().BeFalse();
    }

    [Fact]
    public void A_link_to_a_deeper_folder_inside_the_copy_buys_a_path_no_extra_climb()
    {
        // The same arithmetic reaches a plain path argument: a CLI resolves "shortcut/../../.." with
        // its own string arithmetic before it opens anything, and lands where the kernel would not.
        // This one was already refused before the fix, but only as a side effect of normalising
        // lexically FIRST and following links after. It is pinned here because the new rule is what
        // makes it deliberate: the lexical reading has to be inside, whatever order things happen in.
        var copy = NewDirectory("workspaces", "w1");
        var deep = NewDirectory("workspaces", "w1", "a", "b", "c");
        LinkDirectory(Path.Combine(copy, "shortcut"), deep);

        Check("Read", copy, "file_path", Path.Combine(copy, "shortcut", "..", "..", "..", "data", "key.xml"))
            .Contained.Should().BeFalse();
    }

    [Fact]
    public void A_climb_through_an_in_copy_link_that_lands_inside_either_way_still_runs_unattended()
    {
        // ... and this is why the rule is "inside under BOTH readings" rather than a blanket deny on
        // links: here the lexical answer and the kernel's answer are both inside the copy.
        var copy = NewDirectory("workspaces", "w1");
        var deep = NewDirectory("workspaces", "w1", "a", "b", "c");
        File.WriteAllText(Path.Combine(copy, "a", "b", "note.md"), "hi");
        LinkDirectory(Path.Combine(copy, "shortcut"), deep);

        // One level up is <copy>/a/b really and <copy> lexically, and both are inside the copy.
        Check("Read", copy, "file_path", Path.Combine(copy, "shortcut", "..", "note.md"))
            .Contained.Should().BeTrue();
        Check("Glob", copy, "path", Path.Combine(copy, "shortcut"), "pattern", "../*.md")
            .Contained.Should().BeTrue();
    }

    [PosixFact]
    public void A_dangling_link_out_of_the_copy_still_asks()
    {
        // The OS cannot canonicalise a link that points at nothing, but the link still says where a
        // read through it was aimed, so "not there" must not be read as "not a link".
        var copy = NewDirectory("workspaces", "w1");
        Directory.CreateSymbolicLink(Path.Combine(copy, "gone"), "/data/keys");

        Check("Read", copy, "file_path", "gone/key.xml").Contained.Should().BeFalse();
    }

    [PosixFact]
    public void A_symlink_loop_asks_rather_than_spinning()
    {
        var copy = NewDirectory("workspaces", "w1");
        Directory.CreateSymbolicLink(Path.Combine(copy, "loopA"), Path.Combine(copy, "loopB"));
        Directory.CreateSymbolicLink(Path.Combine(copy, "loopB"), Path.Combine(copy, "loopA"));

        Check("Read", copy, "file_path", "loopA/anything.txt").Contained.Should().BeFalse();
    }

    // ---- what a wildcard MATCHES, not what it spells (round 7) ------------------------------
    //
    // Refusing a magic segment whose own text spells ".." rested on the argument that such segments are
    // matched against real directory entries, which never include "..". True, and beside the point: a
    // real directory entry can be a SYMLINK out of the copy. A wildcard matches its NAME, a literal
    // segment after it traverses through it, and the read lands outside with nothing in the pattern
    // spelling a traversal at all.

    [Fact]
    public void A_wildcard_that_can_match_a_link_out_of_the_copy_is_never_unattended()
    {
        var copy = NewDirectory("workspaces", "w1");
        var outside = NewDirectory("host-secrets");
        File.WriteAllText(Path.Combine(outside, "keys.txt"), "a-real-token");
        Directory.CreateDirectory(Path.Combine(copy, "src"));
        File.WriteAllText(Path.Combine(copy, "src", "Program.cs"), "// hello");

        LinkDirectory(Path.Combine(copy, "out"), outside);

        // GROUND TRUTH first: "out" is an ordinary entry of the copy, so a wildcard matches its name,
        // and a literal tail after it opens the file outside.
        Directory.GetFileSystemEntries(copy).Select(Path.GetFileName).Should().Contain("out");
        File.ReadAllText(Path.Combine(copy, "out", "keys.txt")).Should().Be(
            "a-real-token", "the case only means something if the tail through the link really reads it");

        Check("Glob", copy, "pattern", "*/keys.txt").Contained.Should().BeFalse();
        Check("Glob", copy, "path", copy, "pattern", "**/*").Contained.Should().BeFalse();
        Check("Grep", copy, "glob", "*/keys.txt").Contained.Should().BeFalse();

        // ... and the prompt must not describe it as landing inside the copy either.
        var input = new Dictionary<string, string>(StringComparer.Ordinal) { ["pattern"] = "*/keys.txt" };
        AgentReadScope.Resolve("Glob", input, copy)!.Containment.Should().NotBe(
            AgentTargetContainment.Inside);
    }

    [Fact]
    public void A_wildcard_is_followed_through_an_in_copy_link_to_the_subtree_it_leads_to()
    {
        // The region a wildcard can walk is not the subtree under the pattern's prefix alone: a link
        // that stays inside the copy leads into another subtree, and the escaping link can be there.
        var copy = NewDirectory("workspaces", "w1");
        var elsewhere = NewDirectory("workspaces", "w1", "elsewhere");
        var outside = NewDirectory("host-secrets");
        File.WriteAllText(Path.Combine(outside, "keys.txt"), "a-real-token");

        var start = NewDirectory("workspaces", "w1", "src");
        LinkDirectory(Path.Combine(start, "shortcut"), elsewhere);
        LinkDirectory(Path.Combine(elsewhere, "out"), outside);

        Check("Glob", copy, "path", start, "pattern", "**/keys.txt").Contained.Should().BeFalse();
    }

    [Fact]
    public void An_ordinary_pattern_over_a_copy_with_no_link_out_of_it_still_runs_unattended()
    {
        // The control, and the whole feature: this must not become a blanket deny on the agent's
        // most-used read tool. A real copy, real subdirectories, a link that stays inside it.
        var copy = NewDirectory("workspaces", "w1");
        var lib = NewDirectory("workspaces", "w1", "lib");
        NewDirectory("workspaces", "w1", "src", "deep");
        File.WriteAllText(Path.Combine(copy, "src", "Program.cs"), "// hello");
        File.WriteAllText(Path.Combine(copy, "src", "deep", "More.cs"), "// hello");
        File.WriteAllText(Path.Combine(lib, "Lib.cs"), "// hello");
        LinkDirectory(Path.Combine(copy, "shortcut"), lib);

        foreach (var pattern in new[] { "**/*.cs", "*", "src/**/*.{cs,razor}", "[abc]*.txt", "**/*" })
        {
            Check("Glob", copy, "pattern", pattern).Contained.Should().BeTrue(
                $"{pattern} reaches nothing outside a copy that holds no link out of it");
        }

        Check("Glob", copy, "path", Path.Combine(copy, "src"), "pattern", "../lib/**/*.cs")
            .Contained.Should().BeTrue();
    }

    [PosixFact]
    public void A_region_the_studio_cannot_read_is_asked_about_rather_than_assumed_clean()
    {
        // Fail closed: a directory the walk cannot open may hold a link out of the copy, and "could
        // not look" must never read as "nothing there".
        if (OperatingSystem.IsWindows())
        {
            // PosixFact already skips this on Windows; the platform analyser wants it in the code too.
            return;
        }

        var copy = NewDirectory("workspaces", "w1");
        var closed = NewDirectory("workspaces", "w1", "closed");
        File.SetUnixFileMode(closed, UnixFileMode.None);

        try
        {
            // GROUND TRUTH: this run really cannot open it. Root ignores the mode bits, so under root
            // the construction cannot be posed at all and the test proves nothing rather than the
            // opposite. Saying so beats asserting a refusal the platform never produced.
            try
            {
                Directory.GetFileSystemEntries(closed);
                return;
            }
            catch (UnauthorizedAccessException)
            {
            }

            Check("Glob", copy, "pattern", "**/*.cs").Contained.Should().BeFalse();
        }
        finally
        {
            File.SetUnixFileMode(closed, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void A_pattern_of_plain_names_needs_no_walk_at_all()
    {
        // No wildcard, so nothing unknown is matched and the region question does not arise: the
        // escaping link next door does not make an exactly-spelled in-copy pattern ask.
        var copy = NewDirectory("workspaces", "w1");
        var outside = NewDirectory("host-secrets");
        LinkDirectory(Path.Combine(copy, "out"), outside);
        Directory.CreateDirectory(Path.Combine(copy, "src"));

        Check("Glob", copy, "pattern", "src/Program.cs").Contained.Should().BeTrue();
    }

    [Fact]
    public void A_file_that_does_not_exist_yet_is_still_contained_by_the_part_that_does()
    {
        // Neither realpath nor GetFinalPathNameByHandle can answer for a path that is not there, and an
        // absent leaf must not become a way to skip the check in either direction.
        var copy = NewDirectory("workspaces", "w1");
        var outside = NewDirectory("host-secrets");
        LinkDirectory(Path.Combine(copy, "escape"), outside);

        Check("Read", copy, "file_path", "docs/not-written-yet.md").Contained.Should().BeTrue();
        Check("Read", copy, "file_path", "escape/not-written-yet.md").Contained.Should().BeFalse();
    }

    private string NewDirectory(params string[] parts) =>
        Directory.CreateDirectory(Path.Combine([_scratch, .. parts])).FullName;

    /// <summary>
    /// A real reparse point, because a fake one would prove nothing about the walk. Windows refuses a
    /// symbolic link to an unprivileged user, but a directory JUNCTION is a reparse point anyone may
    /// create and it resolves through the same <c>LinkTarget</c> the studio reads.
    /// </summary>
    private static void LinkDirectory(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw;
            }
        }

        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", link, target },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        process!.WaitForExit();
        new DirectoryInfo(link).LinkTarget
            .Should().NotBeNull("this test needs a real reparse point for the studio to follow");
    }
}

/// <summary>
/// A fact about behaviour only a POSIX filesystem can be put into: a backslash inside a FILENAME, a
/// dangling symlink, a symlink loop. Windows either rejects the name outright or needs a privilege the
/// test run does not have, and the container the boundary actually guards is Linux. The skip is set in
/// the CONSTRUCTOR, at discovery time, because <c>Assert.Skip</c> needs xunit v3 and this repository
/// pins xunit 2.9.3 (see <c>RealClaudeCli</c>).
/// </summary>
internal sealed class PosixFactAttribute : FactAttribute
{
    public PosixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "POSIX-only: this needs a filename holding a backslash, a dangling link or a link loop.";
        }
    }
}
