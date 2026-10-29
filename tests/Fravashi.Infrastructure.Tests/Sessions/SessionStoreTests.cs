using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Insights;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Sessions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Plan 2e Task 5: sessions and their transcripts survive a studio restart, which is what makes the
/// list, the reopened transcript and the resume handle possible. Runs against a real SQLite file.
/// </summary>
public class SessionStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fravashi-sessionstore-" + Guid.NewGuid().ToString("N"));

    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteSessionStore _store;

    public SessionStoreTests()
    {
        Directory.CreateDirectory(_directory);
        _factory = new SqliteConnectionFactory(
            new SqliteStoreOptions(Path.Combine(_directory, "fravashi.db"), Pooling: false));
        SqliteSchema.EnsureCreated(_factory);
        _store = new SqliteSessionStore(_factory);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        // Do NOT call SqliteConnection.ClearAllPools() here. It is process-global: xunit runs test
        // classes in parallel, and clearing the pool from this class's Dispose force-closed pooled
        // connections belonging to OTHER SQLite-backed classes running at the same instant, which is what
        // made A_transcript_row_from_a_newer_build_is_skipped... fail intermittently (backlog 89). The
        // factory above is built with Pooling: false, so every connection releases its file handle on
        // close, nothing sits in a shared pool, and this delete does not race a lingering handle.
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static readonly DateTimeOffset Started = new(2026, 8, 4, 18, 0, 0, TimeSpan.Zero);

    private static SessionRecord Record(
        AgentSessionId id,
        string workspace = "w1",
        AgentSessionState state = AgentSessionState.Running,
        string? provider = "cli-session-1",
        DateTimeOffset? started = null) =>
        new(id, workspace, AgentTool.Claude, "Add the readme", provider, state, started ?? Started, null);

    [Fact]
    public void A_session_round_trips()
    {
        var id = AgentSessionId.New();

        _store.Save(Record(id));

        _store.Find(id).Should().Be(Record(id));
    }

    [Fact]
    public void Saving_again_updates_the_session_rather_than_duplicating_it()
    {
        var id = AgentSessionId.New();
        _store.Save(Record(id));

        _store.Save(Record(id, provider: "cli-session-2", state: AgentSessionState.Completed) with
        {
            EndedUtc = Started.AddMinutes(5),
        });

        var found = _store.Find(id);
        found!.State.Should().Be(AgentSessionState.Completed);
        found.ProviderSessionId.Should().Be("cli-session-2");
        found.EndedUtc.Should().Be(Started.AddMinutes(5));
        _store.ForWorkspace("w1").Should().ContainSingle();
    }

    [Fact]
    public void An_unknown_session_is_null_not_an_error() => _store.Find(AgentSessionId.New()).Should().BeNull();

    // ---- backlog 109: a title the owner chose ------------------------------------------------

    [Fact]
    public void A_custom_title_round_trips_beside_the_derived_one()
    {
        var id = AgentSessionId.New();

        _store.Save(Record(id) with { CustomTitle = "Readme rewrite" });

        var found = _store.Find(id)!;
        found.CustomTitle.Should().Be("Readme rewrite");
        found.Title.Should().Be("Add the readme", "a rename never overwrites the derived title");
        found.DisplayTitle.Should().Be("Readme rewrite");
    }

    [Fact]
    public void A_session_that_was_never_renamed_shows_its_derived_title()
    {
        var id = AgentSessionId.New();

        _store.Save(Record(id));

        var found = _store.Find(id)!;
        found.CustomTitle.Should().BeNull();
        found.DisplayTitle.Should().Be("Add the readme");
    }

    [Fact]
    public void Saving_a_record_that_carries_no_custom_title_keeps_the_stored_one()
    {
        // Every other Save caller builds its record from the live session's own state, but the store is
        // the last line: a record with a null custom title means "not renamed here", never "un-rename".
        var id = AgentSessionId.New();
        _store.Save(Record(id) with { CustomTitle = "Readme rewrite" });

        _store.Save(Record(id, state: AgentSessionState.Completed));

        var found = _store.Find(id)!;
        found.State.Should().Be(AgentSessionState.Completed);
        found.CustomTitle.Should().Be("Readme rewrite");
    }

    [Fact]
    public void A_later_rename_replaces_the_earlier_one()
    {
        var id = AgentSessionId.New();
        _store.Save(Record(id) with { CustomTitle = "first name" });

        _store.Save(Record(id) with { CustomTitle = "second name" });

        _store.Find(id)!.CustomTitle.Should().Be("second name");
    }

    [Fact]
    public void A_workspaces_sessions_come_back_newest_first_and_only_its_own()
    {
        _store.Save(Record(AgentSessionId.New(), started: Started));
        _store.Save(Record(AgentSessionId.New(), started: Started.AddHours(2)));
        _store.Save(Record(AgentSessionId.New(), workspace: "w2", started: Started.AddHours(1)));

        var sessions = _store.ForWorkspace("w1");

        sessions.Should().HaveCount(2);
        sessions[0].StartedUtc.Should().Be(Started.AddHours(2));
        _store.ForWorkspace("w2").Should().ContainSingle();
    }

    // ---- backlog 34: the cross-workspace recents query the home card reads ------------------

    [Fact]
    public void The_recent_query_spans_every_workspace_newest_first_and_stops_at_the_cap()
    {
        // Twelve conversations alternating between two workspaces. The newest TEN are what comes back,
        // and the cut is across BOTH lists: a per-workspace read could never produce this answer.
        for (var i = 0; i < 12; i++)
        {
            _store.Save(Record(
                AgentSessionId.New(), workspace: i % 2 == 0 ? "w1" : "w2", started: Started.AddHours(i)));
        }

        var recent = _store.Recent();

        recent.Should().HaveCount(10);
        recent[0].StartedUtc.Should().Be(Started.AddHours(11));
        recent[9].StartedUtc.Should().Be(Started.AddHours(2));
        recent.Should().BeInDescendingOrder(r => r.StartedUtc);
        recent.Select(r => r.WorkspaceId).Distinct().Should().BeEquivalentTo(["w1", "w2"]);
    }

    [Fact]
    public void The_recent_query_carries_the_title_the_owner_chose()
    {
        var id = AgentSessionId.New();
        _store.Save(Record(id) with { CustomTitle = "Readme rewrite" });

        _store.Recent().Should().ContainSingle().Which.DisplayTitle.Should().Be("Readme rewrite");
    }

    [Fact]
    public void The_recent_query_of_an_empty_store_is_empty_not_an_error() =>
        _store.Recent().Should().BeEmpty();

    [Fact]
    public void Every_kind_of_transcript_event_round_trips_in_order()
    {
        var id = AgentSessionId.New();
        _store.Save(Record(id));
        AgentEvent[] transcript =
        [
            new SessionStarted(Started, id),
            new SessionRegistered(Started, "cli-session-1", "claude-haiku-4-5-20251001"),
            new MessageEmitted(Started, TranscriptRole.User, "write the readme"),
            new ToolCallStarted(Started, "toolu_1", "Write", "/workspaces/w1/README.md"),
            new ToolApprovalRequested(Started, "req-1", "Write", "/workspaces/w1/README.md"),
            new ToolApprovalResolved(Started, "req-1", "Write", true, "approved in the studio"),
            new ToolCallCompleted(Started, "toolu_1", "Write", true, "File created"),
            new MessageEmitted(Started, TranscriptRole.Assistant, "Done."),
            new StatsUpdated(Started, new AgentStatsSnapshot(
                18, 290, 41625, 0.0200345m, 2, 1, 1,
                ContextTokens: 24434, ContextWindow: 200_000, BaselineTokens: 24322)),
            new TurnCompleted(Started, "Done.", false, "completed"),
            new SessionEnded(Started, AgentSessionState.Completed, null),
        ];

        _store.Append(id, transcript);

        _store.Transcript(id).Should().BeEquivalentTo(transcript, options => options.WithStrictOrdering());
    }

    [Fact]
    public void Who_refused_a_call_survives_the_round_trip()
    {
        // Backlog 114. The transcript is read back from here long after the session ended, so the
        // attribution has to be IN the row: a user's denial and a refusal the studio imposed while
        // nobody was there are both "not approved" and mean different things.
        var id = AgentSessionId.New();
        _store.Save(Record(id));
        AgentEvent[] transcript =
        [
            new ToolApprovalResolved(Started, "req-1", "Bash", false, "the user declined this change in the studio", ToolApprovalDecidedBy.User),
            new ToolApprovalResolved(Started, "req-2", "Write", false, "The studio saw no answer within 4 hour(s), so the call was denied."),
        ];

        _store.Append(id, transcript);

        var stored = _store.Transcript(id).OfType<ToolApprovalResolved>().ToList();
        stored[0].DecidedBy.Should().Be(ToolApprovalDecidedBy.User);
        stored[1].DecidedBy.Should().Be(ToolApprovalDecidedBy.Studio);
    }

    [Fact]
    public void A_resolution_stored_before_the_attribution_existed_reads_back_as_the_studios_own()
    {
        // The exact row shape this build's predecessor wrote: approved/reason and nothing else. It must
        // NOT read back as the user's denial, because that is the sentence that made an owner believe
        // the approval gate had been bypassed.
        var stored = AgentEventJson.Deserialize(
            "approval_resolved",
            """
            {"timestamp":"2026-08-13T09:00:00.0000000+00:00","requestId":"req-1","toolName":"Bash",
             "approved":false,"reason":"The studio stopped waiting for an answer."}
            """);

        stored.Should().BeOfType<ToolApprovalResolved>()
            .Which.DecidedBy.Should().Be(ToolApprovalDecidedBy.Studio);
    }

    [Fact]
    public void An_unreadable_attribution_reads_back_as_the_studios_own_too()
    {
        var stored = AgentEventJson.Deserialize(
            "approval_resolved",
            """
            {"timestamp":"2026-08-13T09:00:00.0000000+00:00","requestId":"req-1","toolName":"Bash",
             "approved":false,"reason":"no","decidedBy":"whoever"}
            """);

        stored.Should().BeOfType<ToolApprovalResolved>()
            .Which.DecidedBy.Should().Be(ToolApprovalDecidedBy.Studio);
    }

    [Fact]
    public void A_chosen_effort_round_trips_and_default_reads_back_as_none()
    {
        // Requested 2026-10-22 (model and effort per work item): the studio's own record of the effort, since the
        // CLI reports none. An unknown stored value reads back as Default, never as a guessed level.
        var at = new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
        foreach (var effort in new AgentEffort?[] { null, AgentEffort.Low, AgentEffort.XHigh, AgentEffort.Max })
        {
            var chosen = new EffortChosen(at, effort);
            AgentEventJson.KindOf(chosen).Should().Be("effort_chosen");
            AgentEventJson.Deserialize("effort_chosen", AgentEventJson.Serialize(chosen))
                .Should().BeOfType<EffortChosen>().Which.Effort.Should().Be(effort);
        }

        AgentEventJson.Deserialize(
                "effort_chosen", """{"timestamp":"2026-10-22T09:00:00.0000000+00:00","effort":"ludicrous"}""")
            .Should().BeOfType<EffortChosen>().Which.Effort.Should().BeNull();
    }

    [Fact]
    public void The_message_being_typed_is_not_persisted()
    {
        // PartialText is replaced by the finished message; storing it would double every reply.
        var id = AgentSessionId.New();
        _store.Save(Record(id));

        _store.Append(id, [new PartialText(Started, "he"), new PartialText(Started, "llo")]);

        _store.Transcript(id).Should().BeEmpty();
    }

    [Fact]
    public void The_working_indicator_is_not_persisted_either()
    {
        // A phase is true for the instant it was emitted; a transcript of them would be noise.
        var id = AgentSessionId.New();
        _store.Save(Record(id));

        _store.Append(id, [new TurnActivity(Started, AgentTurnPhase.Thinking), new TurnActivity(Started, AgentTurnPhase.Composing)]);

        _store.Transcript(id).Should().BeEmpty();
    }

    [Fact]
    public void A_typed_turn_keeps_its_document_across_a_restart()
    {
        var id = AgentSessionId.New();
        _store.Save(Record(id));
        var document = AgentTurnDocumentReader.Read(
            """{"version":"1","kind":"questions","message":"Tell me about your coffee.","questions":[{"id":"q1","question":"How do you take it?","options":[{"label":"Black"}]}]}""");
        var turn = new TurnCompleted(Started, null, false, "completed", document, 2, "Output does not match required schema: /id");

        _store.Append(id, [turn]);

        var read = _store.Transcript(id).OfType<TurnCompleted>().Single();
        read.Should().Be(turn);
        read.Document!.PayloadJson.Should().Contain("\"How do you take it?\"");
    }

    [Fact]
    public void How_a_typed_conversation_failed_and_whether_it_can_continue_survive_a_restart()
    {
        // Backlog 25: the untyped marker, the failed-turn note and Continue all read these back from the
        // store after a restart, so each has to come back exactly as it went in.
        var id = AgentSessionId.New();
        _store.Save(Record(id));
        AgentEvent[] events =
        [
            new SessionStarted(Started, id, TypedTurns: true),
            new TurnCompleted(
                Started, null, true, AgentTurnEnding.BudgetExhausted, ErrorText: "Reached maximum budget ($0.0001)"),
            new SessionEnded(Started, AgentSessionState.Failed, "closed", Resumable: true),
        ];

        _store.Append(id, events);

        _store.Transcript(id).Should().Equal(events);
    }

    [Fact]
    public void Rows_written_before_typed_turns_read_back_untyped_unfailed_and_not_resumable()
    {
        var id = AgentSessionId.New();
        _store.Save(Record(id));

        _store.Append(id, [new SessionStarted(Started, id), new SessionEnded(Started, AgentSessionState.Failed, "x")]);

        var read = _store.Transcript(id);
        read.OfType<SessionStarted>().Single().TypedTurns.Should().BeFalse();
        read.OfType<SessionEnded>().Single().Resumable.Should().BeFalse();
    }

    [Fact]
    public void A_turn_row_written_before_typed_documents_reads_back_as_the_untyped_turn_it_was()
    {
        var id = AgentSessionId.New();
        _store.Save(Record(id));

        _store.Append(id, [new TurnCompleted(Started, "Done.", false, "completed")]);

        var read = _store.Transcript(id).OfType<TurnCompleted>().Single();
        read.Document.Should().BeNull();
        read.ValidationFailures.Should().Be(0);
        read.LastValidationError.Should().BeNull();
        read.ResultText.Should().Be("Done.");
    }

    [Fact]
    public void A_document_stored_under_a_kind_this_build_does_not_know_still_reads_its_message()
    {
        // Stored in its WIRE shape, so the row goes through the same reader a live turn does and
        // degrades the same way rather than being frozen into what the writing build believed.
        var id = AgentSessionId.New();
        _store.Save(Record(id));
        var document = new AgentTurnDocument(
            "1", "diagram", "A later studio wrote this.", Degradation: AgentTurnDocumentDegradation.UnknownKind);

        _store.Append(id, [new TurnCompleted(Started, null, false, "completed", document)]);

        var read = _store.Transcript(id).OfType<TurnCompleted>().Single().Document;
        read.Should().NotBeNull();
        read!.Message.Should().Be("A later studio wrote this.");
        read.Degradation.Should().Be(AgentTurnDocumentDegradation.UnknownKind);
    }

    [Fact]
    public void Appending_twice_keeps_the_order_the_user_watched()
    {
        var id = AgentSessionId.New();
        _store.Save(Record(id));

        _store.Append(id, [new MessageEmitted(Started, TranscriptRole.User, "first")]);
        _store.Append(id, [new MessageEmitted(Started, TranscriptRole.Assistant, "second")]);

        _store.Transcript(id).OfType<MessageEmitted>().Select(m => m.Text)
            .Should().ContainInOrder("first", "second");
    }

    [Fact]
    public void A_transcript_row_from_a_newer_build_is_skipped_rather_than_breaking_the_transcript()
    {
        var id = AgentSessionId.New();
        _store.Save(Record(id));
        _store.Append(id, [new MessageEmitted(Started, TranscriptRole.User, "hello")]);
        using (var connection = _factory.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO agent_session_events (session_id, kind, payload, created_utc) " +
                "VALUES ($s, 'a_kind_from_the_future', '{\"timestamp\":\"2026-08-04T18:00:00+00:00\"}', $c);";
            command.Parameters.AddWithValue("$s", id.ToString());
            command.Parameters.AddWithValue("$c", Started.ToString("O"));
            command.ExecuteNonQuery();
        }

        _store.Transcript(id).Should().ContainSingle().Which.Should().BeOfType<MessageEmitted>();
    }

    [Fact]
    public void Deleting_a_session_takes_its_transcript_with_it()
    {
        var id = AgentSessionId.New();
        _store.Save(Record(id));
        _store.Append(id, [new MessageEmitted(Started, TranscriptRole.User, "hello")]);

        _store.Delete(id);

        _store.Find(id).Should().BeNull();
        _store.Transcript(id).Should().BeEmpty();
    }

    [Fact]
    public void The_transcript_of_a_session_that_was_never_written_is_empty() =>
        _store.Transcript(AgentSessionId.New()).Should().BeEmpty();

    // ---- backlog 19: the cross-session usage totals -----------------------------------------

    private static StatsUpdated Stats(decimal costUsd, long inputTokens, long outputTokens, DateTimeOffset? at = null) =>
        new(at ?? Started, new AgentStatsSnapshot(inputTokens, outputTokens, 0, costUsd, 0, 0, 1));

    [Fact]
    public void Usage_totals_sum_the_per_session_figures_and_reconcile()
    {
        // Two sessions, each folding a cumulative usage snapshot into its summary. The all-time totals are
        // the exact sum of those two per-session figures: reconcile means reconcile to the cent.
        var one = AgentSessionId.New();
        var two = AgentSessionId.New();
        _store.Save(Record(one, workspace: "w1"));
        _store.Save(Record(two, workspace: "w2"));

        // Cumulative snapshots: the last one per session is the whole session.
        _store.Append(one, [Stats(0.01m, 50, 100), Stats(0.03m, 100, 200)]);
        _store.Append(two, [Stats(0.05m, 400, 500)]);

        var totals = _store.ReadUsageTotals(
            new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));

        totals.AllTime.CostUsd.Should().Be(0.08m);
        totals.AllTime.TotalTokens.Should().Be(100 + 200 + 400 + 500);
        totals.AllTime.Sessions.Should().Be(2);
    }

    [Fact]
    public void A_later_cumulative_snapshot_replaces_the_earlier_one_rather_than_adding_to_it()
    {
        // The CLI reports the running total each turn, so folding must overwrite. Two Append calls (two
        // turns) must not double the cost.
        var id = AgentSessionId.New();
        _store.Save(Record(id));

        _store.Append(id, [Stats(0.02m, 100, 200)]);
        _store.Append(id, [Stats(0.05m, 300, 600)]);

        var totals = _store.ReadUsageTotals(
            new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));
        totals.AllTime.CostUsd.Should().Be(0.05m);
        totals.AllTime.TotalTokens.Should().Be(300 + 600);
    }

    [Fact]
    public void The_month_total_counts_only_sessions_started_this_month_while_all_time_counts_both()
    {
        var monthStart = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var lastMonth = AgentSessionId.New();
        var thisMonth = AgentSessionId.New();
        _store.Save(Record(lastMonth, workspace: "w1", started: monthStart.AddDays(-10)));
        _store.Save(Record(thisMonth, workspace: "w1", started: monthStart.AddDays(4)));
        _store.Append(lastMonth, [Stats(0.10m, 1000, 2000, monthStart.AddDays(-10))]);
        _store.Append(thisMonth, [Stats(0.04m, 100, 200, monthStart.AddDays(4))]);

        var totals = _store.ReadUsageTotals(monthStart, monthStart);

        totals.AllTime.CostUsd.Should().Be(0.14m);
        totals.AllTime.Sessions.Should().Be(2);
        totals.Month.CostUsd.Should().Be(0.04m);
        totals.Month.TotalTokens.Should().Be(300);
        totals.Month.Sessions.Should().Be(1);
    }

    [Fact]
    public void The_day_total_counts_only_sessions_started_in_that_utc_day()
    {
        // Backlog 62: the daily ceiling is enforced against THIS figure, so a session from yesterday
        // leaking into it would refuse runs the owner paid for a day earlier. The boundaries are UTC and
        // the stamps are stored in UTC, so a session five minutes before midnight is yesterday's.
        var dayStart = new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero);
        var monthStart = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var yesterday = AgentSessionId.New();
        var today = AgentSessionId.New();
        var alsoToday = AgentSessionId.New();
        _store.Save(Record(yesterday, workspace: "w1", started: dayStart.AddMinutes(-5)));
        _store.Save(Record(today, workspace: "w1", started: dayStart));
        _store.Save(Record(alsoToday, workspace: "w2", started: dayStart.AddHours(13)));
        _store.Append(yesterday, [Stats(1.00m, 1000, 2000, dayStart.AddMinutes(-5))]);
        _store.Append(today, [Stats(0.25m, 100, 200, dayStart)]);
        _store.Append(alsoToday, [Stats(0.75m, 300, 400, dayStart.AddHours(13))]);

        var totals = _store.ReadUsageTotals(dayStart, monthStart);

        // The day boundary is inclusive at its first instant and exclusive of everything before it.
        totals.Today.CostUsd.Should().Be(1.00m);
        totals.Today.TotalTokens.Should().Be(100 + 200 + 300 + 400);
        totals.Today.Sessions.Should().Be(2);

        // All three started this month, and the month figure is built from the same per-session rows.
        totals.Month.CostUsd.Should().Be(2.00m);
        totals.Month.Sessions.Should().Be(3);
        totals.AllTime.CostUsd.Should().Be(2.00m);
    }

    [Fact]
    public void Usage_totals_are_empty_when_no_session_has_reported_usage()
    {
        var id = AgentSessionId.New();
        _store.Save(Record(id));

        var totals = _store.ReadUsageTotals(
            new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));

        // A saved session with no stats counts as a session at zero cost, not as nothing.
        totals.AllTime.Should().Be(new UsageTotal(0m, 0, 1));
    }

    // ---- backlog 102: secrets never reach the database ---------------------------------------

    private const string FakeKey = "sk-" + "ant-api03-Fake0123456789Fake0123456789Fake0123456789FakeAA";

    [Fact]
    public void An_env_dump_in_a_tool_result_is_persisted_redacted()
    {
        var id = AgentSessionId.New();
        _store.Save(Record(id));
        // Key last: a key line mid-dump is the joined-run swallow acceptance, pinned in
        // TranscriptRedactorTests; this test is about the persistence path, not the shape.
        var dump = "HOME=/root\nPATH=/usr/bin\nANTHROPIC_API_KEY=" + FakeKey;

        _store.Append(id, [new ToolCallCompleted(Started, "toolu_1", "Bash", true, dump)]);

        var read = _store.Transcript(id).OfType<ToolCallCompleted>().Single();
        read.Detail.Should().NotContain(FakeKey);
        read.Detail.Should().Contain("ANTHROPIC_API_KEY=[redacted:ANTHROPIC_API_KEY]");
        read.Detail.Should().Contain("HOME=/root");

        // The DURABLE copy is clean, not just the read-back: the defect was the row itself.
        RawPayloads(id).Should().OnlyContain(p => !p.Contains(FakeKey));
    }

    [Fact]
    public void A_tool_completions_stderr_round_trips_beside_its_stdout()
    {
        // Backlog 104 D3: stderr is a distinct field, so a failed sub-command's error survives the store
        // instead of being folded into (or dropped beside) stdout.
        var id = AgentSessionId.New();
        _store.Save(Record(id));
        var completed = new ToolCallCompleted(
            Started, "toolu_1", "Bash", true, "part one ran\n", "part-two: command not found\n");

        _store.Append(id, [completed]);

        var read = _store.Transcript(id).OfType<ToolCallCompleted>().Single();
        read.Should().Be(completed);
        read.Detail.Should().Be("part one ran\n");
        read.Error.Should().Be("part-two: command not found\n");
    }

    [Fact]
    public void A_tool_completion_with_no_stderr_reads_back_with_a_null_error()
    {
        var id = AgentSessionId.New();
        _store.Save(Record(id));

        _store.Append(id, [new ToolCallCompleted(Started, "toolu_1", "Read", true, "contents")]);

        _store.Transcript(id).OfType<ToolCallCompleted>().Single().Error.Should().BeNull();
    }

    [Fact]
    public void Ordinary_code_in_a_tool_result_round_trips_untouched()
    {
        var id = AgentSessionId.New();
        _store.Save(Record(id));
        var code =
            "var TOKEN = ReadToken();\n" +
            "// commit d788bbf3c1a94be00e2f5cf05a1c6c02e6b7a4d9 introduced the TOKEN cache\n" +
            "return TOKEN;";

        _store.Append(id, [new ToolCallCompleted(Started, "toolu_1", "Read", true, code)]);

        _store.Transcript(id).OfType<ToolCallCompleted>().Single().Detail.Should().Be(code);
    }

    // ---- backlog 103: the store keeps the raw command, shortening is display-only ------------

    [Fact]
    public void A_500_char_command_reads_back_whole_from_tool_started_and_approval_requested_rows()
    {
        var id = AgentSessionId.New();
        _store.Save(Record(id));
        var raw = "run --flag " + new string('x', 489);
        raw.Length.Should().Be(500);

        _store.Append(id,
        [
            new ToolCallStarted(Started, "toolu_1", "Bash", raw),
            new ToolApprovalRequested(Started, "req-1", "Bash", raw),
        ]);

        _store.Transcript(id).OfType<ToolCallStarted>().Single().Detail.Should().Be(raw);
        _store.Transcript(id).OfType<ToolApprovalRequested>().Single().Detail.Should().Be(raw);

        // The DURABLE rows carry it too: the defect was the payload itself holding the literal
        // display elision ("...[N more]...") where the command belonged.
        RawPayloads(id).Should().OnlyContain(p => !p.Contains("more]"));
    }

    // ---- Backlog 36: the home surface's aggregates -------------------------------------------------

    [Fact]
    public void Session_days_key_a_session_by_the_utc_day_it_started_and_sum_that_day()
    {
        var day = new DateTimeOffset(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);
        var first = AgentSessionId.New();
        var second = AgentSessionId.New();
        var nextDay = AgentSessionId.New();
        var outside = AgentSessionId.New();
        _store.Save(Record(first, started: day));
        _store.Save(Record(second, started: day.AddHours(6)));
        _store.Save(Record(nextDay, started: day.AddDays(1)));
        _store.Save(Record(outside, started: day.AddDays(-5)));
        _store.Append(first, [Stats(0.10m, 10, 20, day)]);
        _store.Append(second, [Stats(0.25m, 10, 20, day.AddHours(6))]);
        _store.Append(nextDay, [Stats(1.00m, 10, 20, day.AddDays(1))]);
        _store.Append(outside, [Stats(9.00m, 10, 20, day.AddDays(-5))]);

        var days = _store.ReadSessionDays(
            new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 11));

        // Sparse: the window's two days both had sessions, and the one five days earlier is not in it.
        days.Should().HaveCount(2);
        days[0].Day.Should().Be(new DateOnly(2026, 9, 10));
        days[0].CostUsd.Should().Be(0.35m);
        days[0].SessionsStarted.Should().Be(2);
        days[1].CostUsd.Should().Be(1.00m);
        days.Sum(d => d.CostUsd).Should().Be(1.35m, "the $9.00 session started outside the window");
    }

    [Fact]
    public void A_day_with_no_session_is_absent_rather_than_a_zero_row()
    {
        // The card fills its own gaps, and the difference between "no session" and "a free session"
        // is exactly what tells an empty period from a period of zero-cost runs.
        var day = new DateTimeOffset(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);
        var id = AgentSessionId.New();
        _store.Save(Record(id, started: day));

        var days = _store.ReadSessionDays(new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 12));

        days.Should().ContainSingle();
        days[0].Day.Should().Be(new DateOnly(2026, 9, 10));
        days[0].CostUsd.Should().Be(0m, "the session recorded no usage, which is not the same as no session");
        days[0].SessionsStarted.Should().Be(1);
    }

    [Fact]
    public void Turn_days_count_completed_turns_and_nothing_else_in_the_transcript()
    {
        var day = new DateTimeOffset(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);
        var id = AgentSessionId.New();
        _store.Save(Record(id, started: day));
        _store.Append(id,
        [
            new MessageEmitted(day, TranscriptRole.User, "go"),
            new TurnCompleted(day.AddMinutes(1), "done", false, "completed"),
            new TurnCompleted(day.AddMinutes(2), "done", false, "completed"),
            new ToolCallStarted(day.AddMinutes(3), "t1", "Read", "readme.md"),
            new TurnCompleted(day.AddDays(1), "done", false, "completed"),
            // Outside the window: a turn from last month must not reach the fortnight's count.
            new TurnCompleted(day.AddDays(-20), "done", false, "completed"),
        ]);

        var days = _store.ReadTurnDays(new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 11));

        days.Should().HaveCount(2);
        days[0].Should().Be(new DayCount(new DateOnly(2026, 9, 10), 2));
        days[1].Should().Be(new DayCount(new DateOnly(2026, 9, 11), 1));
    }

    [Fact]
    public void Durations_take_the_middle_and_the_worst_of_the_sessions_that_ended()
    {
        var start = new DateTimeOffset(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);
        Ended(start, TimeSpan.FromMinutes(4));
        Ended(start, TimeSpan.FromMinutes(10));
        Ended(start, TimeSpan.FromHours(3));
        // Still running: no end stamp, so no duration, so it is not in the set.
        _store.Save(Record(AgentSessionId.New(), started: start));
        // Ended, but started before the window.
        Ended(start.AddDays(-40), TimeSpan.FromHours(20));

        var stats = _store.ReadSessionDurations(start.AddDays(-30));

        stats.Count.Should().Be(3);
        stats.Median.Should().Be(TimeSpan.FromMinutes(10));
        stats.Longest.Should().Be(TimeSpan.FromHours(3));
    }

    [Fact]
    public void Durations_say_nothing_when_no_session_has_ended()
    {
        var start = new DateTimeOffset(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);
        _store.Save(Record(AgentSessionId.New(), started: start));

        _store.ReadSessionDurations(start.AddDays(-30)).HasAny.Should().BeFalse();
    }

    private void Ended(DateTimeOffset started, TimeSpan ran)
    {
        var id = AgentSessionId.New();
        _store.Save(Record(id, state: AgentSessionState.Completed, started: started) with
        {
            EndedUtc = started + ran,
        });
    }

    private List<string> RawPayloads(AgentSessionId id)
    {
        var payloads = new List<string>();
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM agent_session_events WHERE session_id = $s;";
        command.Parameters.AddWithValue("$s", id.ToString());
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            payloads.Add(reader.GetString(0));
        }

        return payloads;
    }
}
