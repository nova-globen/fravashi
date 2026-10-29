using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Web.Services;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Work item #366: the rules of "Follow the agent" on the Code page, without rendering. Which tool paths map to a
/// file of this workspace, when unsaved work or the person's own activity keeps follow away, and what text a
/// pending change would leave.
/// </summary>
public class AgentFollowRulesTests
{
    private const string Copy = "/workspaces/w1";

    // ---- tracking a call to its outcome (work item #371) ----

    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;

    [Fact]
    public void A_file_call_ran_only_when_its_result_comes_back_as_a_success()
    {
        var calls = new Dictionary<string, (AgentFollowToolKind, string)>();

        var started = AgentFollowRules.Track(calls, new ToolCallStarted(At, "t1", "Read", "/workspaces/w1/a.cs"), Copy);
        var ran = AgentFollowRules.Track(calls, new ToolCallCompleted(At, "t1", "Read", true, "text"), Copy);

        Assert.Equal(new AgentFollowCallStep(AgentFollowCallOutcome.Started, AgentFollowToolKind.Read, "a.cs"), started);
        Assert.Equal(new AgentFollowCallStep(AgentFollowCallOutcome.Ran, AgentFollowToolKind.Read, "a.cs"), ran);
        Assert.Empty(calls);
    }

    [Fact]
    public void A_result_marked_as_an_error_is_a_refusal_whether_the_studio_refused_it_or_it_failed()
    {
        var calls = new Dictionary<string, (AgentFollowToolKind, string)>();
        AgentFollowRules.Track(calls, new ToolCallStarted(At, "t1", "Write", "/workspaces/w1/follow-check.txt"), Copy);

        var step = AgentFollowRules.Track(
            calls, new ToolCallCompleted(At, "t1", "Write", false, "Every work item this session named is closed."), Copy);

        Assert.Equal(new AgentFollowCallStep(AgentFollowCallOutcome.Refused, AgentFollowToolKind.Write, "follow-check.txt"), step);
        Assert.Empty(calls);
    }

    [Fact]
    public void A_refusal_nobody_was_asked_about_ends_the_matching_call_and_a_late_result_then_means_nothing()
    {
        var calls = new Dictionary<string, (AgentFollowToolKind, string)>();
        AgentFollowRules.Track(calls, new ToolCallStarted(At, "t1", "Write", "/workspaces/w1/follow-check.txt"), Copy);
        AgentFollowRules.Track(calls, new ToolCallStarted(At, "t2", "Read", "/workspaces/w1/follow-check.txt"), Copy);

        var step = AgentFollowRules.Track(
            calls, new ToolCallRefused(At, "Write", "/workspaces/w1/follow-check.txt", "closed"), Copy);

        Assert.Equal(new AgentFollowCallStep(AgentFollowCallOutcome.Refused, AgentFollowToolKind.Write, "follow-check.txt"), step);
        Assert.Equal(["t2"], calls.Keys);
        Assert.Null(AgentFollowRules.Track(calls, new ToolCallCompleted(At, "t1", "Write", true, "ok"), Copy));
    }

    [Fact]
    public void Calls_follow_does_not_track_are_ignored()
    {
        var calls = new Dictionary<string, (AgentFollowToolKind, string)>();

        Assert.Null(AgentFollowRules.Track(calls, new ToolCallStarted(At, "t1", "Bash", "ls"), Copy));
        Assert.Null(AgentFollowRules.Track(calls, new ToolCallStarted(At, "t2", "Read", "/etc/passwd"), Copy));
        Assert.Null(AgentFollowRules.Track(calls, new ToolCallCompleted(At, "t9", "Read", true, "x"), Copy));
        Assert.Null(AgentFollowRules.Track(calls, new ToolCallRefused(At, "Write", "/workspaces/w1/a.cs", "no"), Copy));
        Assert.Empty(calls);
    }

    // ---- path mapping ----

    [Theory]
    [InlineData("/workspaces/w1/README.md", "README.md")]
    [InlineData("/workspaces/w1/src/App/Program.cs", "src/App/Program.cs")]
    [InlineData("/workspaces/w1/.github/workflows/ci.yml", ".github/workflows/ci.yml")]
    public void A_path_inside_the_contained_copy_maps_to_the_workspace_relative_path(string path, string expected)
    {
        Assert.Equal(expected, AgentFollowRules.MapToWorkspace(Copy, path));
    }

    [Fact]
    public void A_copy_root_written_with_a_trailing_slash_maps_the_same_way()
    {
        Assert.Equal("a/b.cs", AgentFollowRules.MapToWorkspace(Copy + "/", Copy + "/a/b.cs"));
    }

    [Theory]
    [InlineData("/workspaces/w10/README.md")]          // another copy whose name starts with this one's
    [InlineData("/workspaces/w1")]                     // the copy itself
    [InlineData("/workspaces/w1/")]                    // the copy itself, as a directory
    [InlineData("/data/keys/ring.json")]               // outside every copy
    [InlineData("/workspaces/w1/../w2/secret.txt")]    // climbs out
    [InlineData("/workspaces/w1/src/../../w2/a.cs")]   // climbs out further down
    [InlineData("/workspaces/w1/./README.md")]         // a dot segment is not a plain path
    [InlineData("/workspaces/w1//README.md")]          // nor is an empty segment
    [InlineData("/workspaces/w1/src/")]                // a directory, not a file
    [InlineData("/workspaces/w1/.git/config")]         // .git never syncs
    [InlineData("/workspaces/w1/src\\..\\..\\x")]      // a backslash is never read as a separator
    [InlineData("workspaces/w1/README.md")]            // relative
    [InlineData("")]
    public void Anything_that_is_not_a_plain_file_inside_the_copy_is_ignored(string path)
    {
        Assert.Null(AgentFollowRules.MapToWorkspace(Copy, path));
    }

    [Fact]
    public void A_path_with_a_nul_is_ignored()
    {
        Assert.Null(AgentFollowRules.MapToWorkspace(Copy, Copy + "/README.md\0.txt"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("workspaces/w1")]
    public void Without_a_usable_copy_root_nothing_maps(string? root)
    {
        Assert.Null(AgentFollowRules.MapToWorkspace(root, "/workspaces/w1/README.md"));
    }

    [Theory]
    [InlineData("Read", AgentFollowToolKind.Read)]
    [InlineData("Edit", AgentFollowToolKind.Edit)]
    [InlineData("MultiEdit", AgentFollowToolKind.Edit)]
    [InlineData("Write", AgentFollowToolKind.Write)]
    [InlineData("Bash", AgentFollowToolKind.None)]
    [InlineData("Grep", AgentFollowToolKind.None)]
    [InlineData(null, AgentFollowToolKind.None)]
    public void Only_the_file_tools_are_followed(string? tool, AgentFollowToolKind expected)
    {
        Assert.Equal(expected, AgentFollowRules.KindOf(tool));
    }

    // ---- the unsaved guard and the pause ----

    [Fact]
    public void Follow_acts_when_nothing_unsaved_is_in_the_way()
    {
        Assert.Equal(AgentFollowDecision.Follow,
            AgentFollowRules.Decide(true, false, "a.cs", "b.cs", _ => false));
    }

    [Fact]
    public void A_target_with_unsaved_changes_is_never_replaced()
    {
        Assert.Equal(AgentFollowDecision.TargetUnsaved,
            AgentFollowRules.Decide(true, false, "a.cs", "b.cs", p => p == "a.cs"));
    }

    [Fact]
    public void Follow_does_not_take_the_editor_away_from_unsaved_changes_in_another_file()
    {
        Assert.Equal(AgentFollowDecision.ActiveUnsaved,
            AgentFollowRules.Decide(true, false, "a.cs", "b.cs", p => p == "b.cs"));
    }

    [Fact]
    public void Off_paused_and_unmapped_come_before_anything_else()
    {
        Assert.Equal(AgentFollowDecision.Off, AgentFollowRules.Decide(false, true, "a.cs", null, _ => true));
        Assert.Equal(AgentFollowDecision.Ignored, AgentFollowRules.Decide(true, true, null, null, _ => true));
        Assert.Equal(AgentFollowDecision.Paused, AgentFollowRules.Decide(true, true, "a.cs", null, _ => true));
    }

    [Fact]
    public void The_pause_lasts_twenty_seconds_after_the_person_last_acted()
    {
        var pause = new AgentFollowPause();
        var start = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        Assert.False(pause.IsPaused(start));

        pause.PersonActed(start);

        Assert.True(pause.IsPaused(start.AddSeconds(19.9)));
        Assert.False(pause.IsPaused(start.AddSeconds(20)));
        Assert.Equal(TimeSpan.FromSeconds(5), pause.Remaining(start.AddSeconds(15)));

        // Acting again moves the end, it does not stack.
        pause.PersonActed(start.AddSeconds(15));
        Assert.True(pause.IsPaused(start.AddSeconds(34)));
        Assert.False(pause.IsPaused(start.AddSeconds(35)));
    }

    [Fact]
    public void Resume_ends_the_pause_at_once()
    {
        var pause = new AgentFollowPause();
        var now = DateTimeOffset.UnixEpoch;
        pause.PersonActed(now);

        pause.Resume();

        Assert.False(pause.IsPaused(now));
        Assert.Null(pause.Remaining(now));
    }

    // ---- the proposed text ----

    [Fact]
    public void An_edit_replaces_its_old_text_once()
    {
        var change = AgentFollowRules.ProposeEdit("one\ntwo\nthree\n", "two", "TWO", replaceAll: false);

        Assert.Null(change.Problem);
        Assert.Equal("one\nTWO\nthree\n", change.Proposed);
    }

    [Fact]
    public void Replace_all_replaces_every_occurrence()
    {
        var change = AgentFollowRules.ProposeEdit("a x a x a", "a", "b", replaceAll: true);

        Assert.Equal("b x b x b", change.Proposed);
    }

    [Fact]
    public void Old_text_that_is_not_in_the_file_gives_a_note_never_a_wrong_diff()
    {
        var change = AgentFollowRules.ProposeEdit("one\ntwo\n", "seven", "eight", replaceAll: false);

        Assert.Null(change.Proposed);
        Assert.Contains("not in the current file", change.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Old_text_found_more_than_once_without_replace_all_is_not_guessed()
    {
        var change = AgentFollowRules.ProposeEdit("a\na\n", "a", "b", replaceAll: false);

        Assert.Null(change.Proposed);
        Assert.Contains("2 times", change.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_old_text_only_fills_an_empty_file()
    {
        Assert.Equal("new", AgentFollowRules.ProposeEdit(string.Empty, string.Empty, "new", false).Proposed);
        Assert.Null(AgentFollowRules.ProposeEdit("x", string.Empty, "new", false).Proposed);
    }

    [Fact]
    public void A_pending_write_proposes_its_content_whatever_the_file_held()
    {
        var request = Pending("Write", ("file_path", Copy + "/a.txt"), ("content", "hello"));

        Assert.Equal("hello", AgentFollowRules.ProposeFor(request, current: null).Proposed);
        Assert.Equal("hello", AgentFollowRules.ProposeFor(request, current: "old").Proposed);
    }

    [Fact]
    public void A_pending_edit_reads_replace_all_from_its_flattened_value()
    {
        var request = Pending("Edit",
            ("file_path", Copy + "/a.txt"), ("old_string", "a"), ("new_string", "b"), ("replace_all", "true"));

        Assert.Equal("b b", AgentFollowRules.ProposeFor(request, "a a").Proposed);
    }

    [Fact]
    public void A_pending_edit_of_a_file_not_on_the_host_is_not_guessed()
    {
        var request = Pending("Edit", ("file_path", Copy + "/a.txt"), ("old_string", "a"), ("new_string", "b"));

        Assert.NotNull(AgentFollowRules.ProposeFor(request, current: null).Problem);
    }

    [Fact]
    public void A_cut_argument_is_read_from_its_raw_copy_never_from_the_display_copy()
    {
        var full = new string('x', 12_000);
        var request = Pending("Write", ("file_path", Copy + "/big.txt"), ("content", "[head ... tail]")) with
        {
            CutArguments = [new CutToolArgument("content", full.Length, 2_000, full)],
        };

        Assert.Equal(full, AgentFollowRules.ProposeFor(request, null).Proposed);
    }

    [Fact]
    public void A_cut_argument_too_long_for_the_card_is_not_guessed_at()
    {
        var request = Pending("Write", ("file_path", Copy + "/big.txt"), ("content", "[head ... tail]")) with
        {
            CutArguments = [new CutToolArgument("content", 300_000, 290_000, null)],
        };

        var change = AgentFollowRules.ProposeFor(request, null);

        Assert.Null(change.Proposed);
        Assert.NotNull(change.Problem);
    }

    [Fact]
    public void A_pending_multi_edit_applies_its_edits_in_order()
    {
        var request = Pending("MultiEdit",
            ("file_path", Copy + "/a.txt"),
            ("edits", """[{"old_string":"one","new_string":"1"},{"old_string":"1 two","new_string":"1 2","replace_all":false}]"""));

        Assert.Equal("1 2 three", AgentFollowRules.ProposeFor(request, "one two three").Proposed);
    }

    [Fact]
    public void A_multi_edit_with_one_edit_that_does_not_place_gives_the_note()
    {
        var request = Pending("MultiEdit",
            ("file_path", Copy + "/a.txt"),
            ("edits", """[{"old_string":"one","new_string":"1"},{"old_string":"nine","new_string":"9"}]"""));

        Assert.Null(AgentFollowRules.ProposeFor(request, "one two").Proposed);
    }

    [Fact]
    public void The_target_of_a_pending_change_is_its_mapped_file_path()
    {
        Assert.Equal("src/a.cs", AgentFollowRules.TargetOf(Pending("Edit", ("file_path", Copy + "/src/a.cs")), Copy));
        Assert.Null(AgentFollowRules.TargetOf(Pending("Edit", ("file_path", "/etc/passwd")), Copy));
        Assert.Null(AgentFollowRules.TargetOf(Pending("Bash", ("command", "ls")), Copy));
        Assert.Null(AgentFollowRules.TargetOf(Pending("Read", ("file_path", Copy + "/a.cs")), Copy));
    }

    [Fact]
    public void A_file_path_the_display_copy_cut_is_not_followed()
    {
        var request = Pending("Write", ("file_path", Copy + "/a.cs")) with
        {
            CutArguments = [new CutToolArgument("file_path", 20_000, 10_000, Copy + "/a.cs")],
        };

        Assert.Null(AgentFollowRules.TargetOf(request, Copy));
    }

    // ---- the lines a landed change touched ----

    [Fact]
    public void Changed_lines_are_what_lies_between_the_common_head_and_tail()
    {
        Assert.Equal((2, 3), AgentFollowRules.ChangedLines("a\nb\nc\nd", "a\nB\nC\nd"));
        Assert.Equal((2, 3), AgentFollowRules.ChangedLines("a\nd", "a\nb\nc\nd"));
        Assert.Null(AgentFollowRules.ChangedLines("same", "same"));
    }

    [Fact]
    public void A_pure_deletion_names_the_line_where_the_text_was()
    {
        Assert.Equal((2, 2), AgentFollowRules.ChangedLines("a\nb\nc", "a\nc"));
    }

    [Fact]
    public void A_new_file_is_changed_everywhere()
    {
        Assert.Equal((1, 3), AgentFollowRules.ChangedLines(string.Empty, "a\nb\nc"));
    }

    private static PendingToolApproval Pending(string tool, params (string Key, string Value)[] input) =>
        new(
            "req-1",
            new AgentSessionId(Guid.NewGuid()),
            tool,
            input.Length > 0 ? input[0].Value : string.Empty,
            "toolu_1",
            input.ToDictionary(i => i.Key, i => i.Value, StringComparer.Ordinal),
            DateTimeOffset.UnixEpoch);
}
