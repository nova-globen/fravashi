using AwesomeAssertions;
using Fravashi.Domain.AgentSessions;
using Xunit;

namespace Fravashi.Domain.Tests;

/// <summary>
/// Plan 2e Task 1: the event vocabulary an agent-session runner streams while it parses the CLI's
/// stream-json output, the run counters those events accumulate, and the read-only tool policy the
/// approval broker defers to (decision 5: auto-allow reads, always ask for changes).
/// </summary>
public class AgentEventTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Every_event_is_an_agent_event_carrying_its_timestamp()
    {
        AgentEvent[] events =
        [
            new SessionStarted(Now, new AgentSessionId(Guid.Empty)),
            new SessionRegistered(Now, "cli-session", "claude-haiku-4-5-20251001"),
            new MessageEmitted(Now, TranscriptRole.Assistant, "hello"),
            new PartialText(Now, "he"),
            new TurnActivity(Now, AgentTurnPhase.Composing),
            new ToolCallStarted(Now, "toolu_1", "Write", "note.txt"),
            new ToolCallCompleted(Now, "toolu_1", "Write", true, "File created"),
            new ToolApprovalRequested(Now, "req-1", "Write", "note.txt"),
            new ToolApprovalResolved(Now, "req-1", "Write", true, "approved in the studio"),
            new StatsUpdated(Now, AgentStatsSnapshot.Empty),
            new TurnCompleted(Now, "done", false, "completed"),
            new SessionEnded(Now, AgentSessionState.Completed, null),
        ];

        events.Should().AllSatisfy(e => e.Timestamp.Should().Be(Now));
    }

    [Fact]
    public void Events_compare_by_value_so_the_registry_can_dedupe_and_tests_can_assert_them()
    {
        new ToolCallStarted(Now, "toolu_1", "Write", "note.txt")
            .Should().Be(new ToolCallStarted(Now, "toolu_1", "Write", "note.txt"));

        new ToolCallStarted(Now, "toolu_1", "Write", "note.txt")
            .Should().NotBe(new ToolCallStarted(Now, "toolu_2", "Write", "note.txt"));
    }

    [Theory]
    // The one reason the real CLI emitted in the 2026-08-04 spikes.
    [InlineData("completed", false, AgentSessionState.Completed)]
    [InlineData("cancelled", false, AgentSessionState.Cancelled)]
    [InlineData("canceled", false, AgentSessionState.Cancelled)]
    [InlineData("aborted", false, AgentSessionState.Cancelled)]
    [InlineData("interrupted", false, AgentSessionState.Cancelled)]
    [InlineData("COMPLETED", false, AgentSessionState.Completed)]
    // is_error wins over any reason: the run reported a failure.
    [InlineData("completed", true, AgentSessionState.Failed)]
    [InlineData("error_during_execution", false, AgentSessionState.Failed)]
    [InlineData("error", false, AgentSessionState.Failed)]
    // An unknown reason is not evidence of failure, so the run counts as finished and keeps its word.
    [InlineData("max_turns", false, AgentSessionState.Completed)]
    [InlineData(null, false, AgentSessionState.Completed)]
    public void A_terminal_reason_maps_to_a_session_state(string? reason, bool isError, AgentSessionState expected) =>
        AgentTerminalReason.ToSessionState(reason, isError).Should().Be(expected);

    [Theory]
    [InlineData(AgentSessionState.Pending, false)]
    [InlineData(AgentSessionState.Running, false)]
    [InlineData(AgentSessionState.Completed, true)]
    [InlineData(AgentSessionState.Failed, true)]
    [InlineData(AgentSessionState.Cancelled, true)]
    public void Terminal_states_are_the_three_ended_ones(AgentSessionState state, bool terminal) =>
        state.IsTerminal().Should().Be(terminal);

    [Fact]
    public void Stats_accumulate_across_turns_because_the_cli_reports_per_turn_totals()
    {
        var stats = new AgentRunStats();

        stats.AddTurn(inputTokens: 18, outputTokens: 290, cacheReadTokens: 41625, costUsd: 0.0200m);
        stats.AddTurn(inputTokens: 10, outputTokens: 55, cacheReadTokens: 24066, costUsd: 0.0188m);
        stats.IncrementToolCalls();
        stats.IncrementToolCalls();
        stats.IncrementRateLimitHits();

        var snapshot = stats.Snapshot();
        snapshot.InputTokens.Should().Be(28);
        snapshot.OutputTokens.Should().Be(345);
        snapshot.CacheReadTokens.Should().Be(65691);
        snapshot.TotalTokens.Should().Be(373);
        snapshot.CostUsd.Should().Be(0.0388m);
        snapshot.ToolCalls.Should().Be(2);
        snapshot.RateLimitHits.Should().Be(1);
        snapshot.Turns.Should().Be(2);
    }

    [Fact]
    public void Stats_ignore_negative_deltas_rather_than_throwing_mid_stream()
    {
        var stats = new AgentRunStats();

        stats.AddTurn(-5, -5, -5, -1m);

        stats.Snapshot().Should().Be(AgentStatsSnapshot.Empty with { Turns = 1 });
    }

    [Fact]
    public void The_first_context_reading_becomes_the_baseline_and_stays_while_the_level_moves()
    {
        // The first request of a session is dominated by what rides in EVERY request before anyone
        // types (system prompt, tool schemas, project instructions) plus the first message. Recording
        // it once is what lets the strip split "fixed prefix" from "conversation".
        var stats = new AgentRunStats();

        stats.ObserveContext(40_000);
        stats.ObserveContext(78_000);
        stats.ObserveContextWindow(200_000);

        var snapshot = stats.Snapshot();
        snapshot.BaselineTokens.Should().Be(40_000);
        snapshot.ContextTokens.Should().Be(78_000);
        snapshot.ConversationTokens.Should().Be(38_000);
        snapshot.BaselineFraction.Should().BeApproximately(0.2d, 0.0001);
        snapshot.ContextFraction.Should().BeApproximately(0.39d, 0.0001);
    }

    [Fact]
    public void A_compaction_below_the_baseline_never_yields_a_negative_conversation_or_an_overdrawn_split()
    {
        // The CLI can compact the conversation below the first reading. The split must not claim more
        // fixed prefix than there is context, and the conversation share must clamp at zero.
        var stats = new AgentRunStats();

        stats.ObserveContext(40_000);
        stats.ObserveContext(30_000);
        stats.ObserveContextWindow(200_000);

        var snapshot = stats.Snapshot();
        snapshot.ConversationTokens.Should().Be(0);
        snapshot.BaselineFraction.Should().Be(snapshot.ContextFraction);
    }

    [Fact]
    public void An_unknown_baseline_offers_no_split_at_all()
    {
        // Rows persisted before the baseline existed read back as zero: the strip then falls back to
        // the single-segment meter rather than inventing a prefix.
        var snapshot = new AgentStatsSnapshot(0, 0, 0, 0m, 0, 0, 0, ContextTokens: 50_000, ContextWindow: 200_000);

        snapshot.BaselineFraction.Should().BeNull();
        snapshot.ConversationTokens.Should().Be(0);
    }

    [Theory]
    [InlineData("Read")]
    [InlineData("Grep")]
    [InlineData("Glob")]
    [InlineData("NotebookRead")]
    [InlineData("LS")]
    [InlineData("ToolSearch")]
    [InlineData("TaskList")]
    [InlineData("read")] // the CLI's casing is not something to depend on
    public void Read_only_tools_are_auto_allowed(string tool) =>
        AgentToolSafety.IsReadOnly(tool).Should().BeTrue();

    [Theory]
    [InlineData("Read", "file_path", true)]
    [InlineData("NotebookRead", "notebook_path", true)]
    [InlineData("LS", "path", true)]
    // Their path defaults to the child's working directory, which is the contained copy.
    [InlineData("Grep", "path", false)]
    [InlineData("Glob", "path", false)]
    public void A_read_tool_that_opens_files_says_where_it_reads(string tool, string field, bool required)
    {
        // Being on the auto-allow list is necessary, never sufficient: the caller has to be able to
        // work out WHERE a call would read before letting it run unattended.
        var target = AgentToolSafety.TargetOf(tool);

        target.Should().NotBeNull();
        target!.ReadsTheFileSystem.Should().BeTrue();
        target.PathFields.Should().Contain(field);
        target.PathRequired.Should().Be(required);
    }

    [Fact]
    public void A_glob_declares_its_pattern_because_a_pattern_can_traverse()
    {
        AgentToolSafety.TargetOf("Glob")!.PatternField.Should().Be("pattern");

        // Grep's own "pattern" is the regex it searches FOR, so it is content and must never be
        // contained as a location. Its "glob" is the name filter, which is a path pattern like any
        // other and is contained like one.
        AgentToolSafety.TargetOf("Grep")!.PatternField
            .Should().Be("glob", "a grep pattern is a regex, not a path");
    }

    [Theory]
    [InlineData("ToolSearch")]
    [InlineData("TaskGet")]
    [InlineData("TaskList")]
    [InlineData("TaskOutput")]
    public void A_tool_that_asks_the_harness_rather_than_the_disk_has_nothing_to_contain(string tool) =>
        AgentToolSafety.TargetOf(tool)!.ReadsTheFileSystem.Should().BeFalse();

    [Theory]
    [InlineData("Write")]
    [InlineData("Bash")]
    [InlineData("SomeToolInventedNextRelease")]
    public void A_tool_outside_the_auto_allow_list_has_no_target_to_scope(string tool) =>
        AgentToolSafety.TargetOf(tool).Should().BeNull();

    [Theory]
    [InlineData("Write")]
    [InlineData("Edit")]
    [InlineData("Bash")]
    [InlineData("NotebookEdit")]
    [InlineData("Task")]
    [InlineData("Skill")]
    // Reaching the network with workspace context is not a "read" for approval purposes.
    [InlineData("WebFetch")]
    [InlineData("WebSearch")]
    // Anything the CLI grows next must ask, never be silently allowed.
    [InlineData("SomeToolInventedNextRelease")]
    [InlineData("")]
    public void Everything_else_asks_the_user(string tool) =>
        AgentToolSafety.IsReadOnly(tool).Should().BeFalse();
}
