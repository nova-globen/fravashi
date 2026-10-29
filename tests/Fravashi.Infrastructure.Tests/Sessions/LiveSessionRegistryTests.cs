using System.Threading.Channels;
using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Sessions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Plan 2e Task 5: the registry is where a session stops belonging to a circuit. Both UI surfaces
/// watch the same object, the transcript is complete for whoever attaches later (decision 6),
/// and a workspace runs one session at a time (decision 2).
/// </summary>
public class LiveSessionRegistryTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fravashi-registry-" + Guid.NewGuid().ToString("N"));

    private readonly SqliteSessionStore _store;
    private readonly AgentApprovalBroker _broker = new(TimeSpan.FromSeconds(5));
    private readonly ScriptedRunner _runner = new();
    private readonly LiveSessionRegistry _registry;

    public LiveSessionRegistryTests()
    {
        Directory.CreateDirectory(_directory);
        var factory = new SqliteConnectionFactory(
            new SqliteStoreOptions(Path.Combine(_directory, "fravashi.db"), Pooling: false));
        SqliteSchema.EnsureCreated(factory);
        _store = new SqliteSessionStore(factory);
        _registry = new LiveSessionRegistry(_runner, _store, _broker);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _registry.Dispose();
        _broker.Dispose();
        // No ClearAllPools: it is process-global and racing parallel SQLite classes; Pooling: false above
        // makes it unnecessary (backlog 89).
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 8, 4, 18, 0, 0, TimeSpan.Zero);

    private SessionStartRequest StartRequest(string prompt = "write the readme", AgentSessionId? resumeOf = null) =>
        new("w1", "/workspaces/w1", prompt, ResumeOf: resumeOf);

    private static async Task Eventually(Func<bool> condition, string because = "")
    {
        // A DEADLINE, not a fixed number of attempts. The budgets here used to be 2, 5, 10 and 15
        // seconds with no principle behind the numbers, and the 2 second ones failed on CI: run 103
        // and run 107 on 2026-09-19 went red on commits that changed only markdown, because the
        // runner was a shared host that also ran production, and a contended machine needs longer to
        // reach a turn boundary than this dev host does. A generous ceiling costs nothing when the
        // condition is met quickly, since the loop exits the moment it is true; it only matters on a
        // machine slow enough that the old limit was measuring the machine rather than the behaviour.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10); // poll interval inside a wait helper, not an arm-window guess (backlog 107)
        }

        condition().Should().BeTrue(because);
    }

    /// <summary>A runner the test drives by hand, one event at a time.</summary>
    private sealed class ScriptedRunner : IAgentSessionRunner
    {
        private readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>();

        public AgentSessionRequest? LastRequest { get; private set; }

        public List<string> Sent { get; } = [];

        public bool Stopped { get; private set; }

        public IAsyncEnumerable<AgentEvent> RunAsync(AgentSessionRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return _events.Reader.ReadAllAsync(cancellationToken);
        }

        public Task<bool> SendAsync(AgentSessionId sessionId, string text, CancellationToken cancellationToken = default)
        {
            Sent.Add(text);
            return Task.FromResult(true);
        }

        public List<IReadOnlyList<AgentImageAttachment>> SentImages { get; } = [];

        public Task<bool> SendAsync(
            AgentSessionId sessionId,
            string text,
            IReadOnlyList<AgentImageAttachment> images,
            CancellationToken cancellationToken = default)
        {
            Sent.Add(text);
            SentImages.Add(images);
            return Task.FromResult(true);
        }

        public bool Stop(AgentSessionId sessionId)
        {
            Stopped = true;
            Emit(new SessionEnded(Now, AgentSessionState.Cancelled, null));
            _events.Writer.TryComplete();
            return true;
        }

        public List<string?> ModelChanges { get; } = [];

        public bool ChangeModel(AgentSessionId sessionId, string? model)
        {
            ModelChanges.Add(model);
            return true;
        }

        public List<AgentEffort?> EffortChanges { get; } = [];

        public bool ChangeEffort(AgentSessionId sessionId, AgentEffort? effort)
        {
            EffortChanges.Add(effort);
            return true;
        }

        public void Emit(AgentEvent e) => _events.Writer.TryWrite(e);

        public void Complete() => _events.Writer.TryComplete();

        public void Finish(AgentSessionState state = AgentSessionState.Completed)
        {
            Emit(new SessionEnded(Now, state, null));
            _events.Writer.TryComplete();
        }
    }

    // ---- starting -----------------------------------------------------------------------

    [Fact]
    public async Task Starting_a_session_runs_the_agent_in_the_workspace_copy()
    {
        var session = _registry.Start(StartRequest());

        await Eventually(() => _runner.LastRequest is not null);
        _runner.LastRequest!.SessionId.Should().Be(session.Id);
        _runner.LastRequest.WorkspaceId.Should().Be("w1");
        _runner.LastRequest.WorkingDirectory.Should().Be("/workspaces/w1");
        _runner.LastRequest.Prompt.Should().Be("write the readme");
        _runner.LastRequest.DocumentKinds.Should().BeEmpty("the typed-turn gate is off unless the studio was told otherwise");
        session.IsRunning.Should().BeTrue();
    }

    [Fact]
    public async Task A_studio_with_typed_turns_switched_on_asks_every_session_for_a_document()
    {
        // The gate is resolved once, where the registry is wired, and rides on the session's request:
        // --json-schema is a PROCESS argument and one process serves a whole conversation, so the kind
        // set is per session. Nothing needs it to vary per turn while v1 sends one kind.
        var runner = new ScriptedRunner();
        using var registry = new LiveSessionRegistry(
            runner, _store, _broker, documentKinds: AgentTurnDocumentSchema.DefaultKinds);

        registry.Start(new SessionStartRequest("w2", "/workspaces/w2", "write the readme"));

        await Eventually(() => runner.LastRequest is not null);
        runner.LastRequest!.DocumentKinds.Should().Equal(AgentTurnDocumentSchema.DefaultKinds);
    }

    [Fact]
    public void The_users_own_turn_opens_the_transcript()
    {
        var session = _registry.Start(StartRequest("write the readme"));

        session.Transcript.OfType<MessageEmitted>().Single()
            .Should().BeEquivalentTo(new { Role = TranscriptRole.User, Text = "write the readme" });
    }

    [Fact]
    public void A_workspace_runs_one_session_at_a_time()
    {
        _registry.Start(StartRequest());

        var act = () => _registry.Start(StartRequest("and another"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*already has a session running*");
    }

    [Fact]
    public async Task A_finished_session_makes_room_for_the_next_one()
    {
        var first = _registry.Start(StartRequest());
        _runner.Finish();
        await Eventually(() => !first.IsRunning);

        var second = _registry.Start(StartRequest("the next thing"));

        second.Id.Should().NotBe(first.Id);
        _registry.RunningFor("w1").Should().Be(second);
    }

    [Fact]
    public async Task Starting_fresh_dismisses_the_latest_session_on_every_surface_at_once()
    {
        // The fresh state used to live in the pane that was clicked, so any other pane resolving the
        // workspace's latest session kept serving the replaced conversation (the Code panel's stale
        // transcript). Dismissal is registry state now, and it fires SessionsChanged so live panes
        // re-resolve rather than waiting for their next parameter pass.
        var replaced = _registry.Start(StartRequest("the first conversation"));
        _runner.Finish();
        await Eventually(() => !replaced.IsRunning);
        var notified = 0;
        _registry.SessionsChanged += () => notified++;

        _registry.StartFresh("w1");

        notified.Should().BeGreaterThan(0, "every pane must learn the conversation was replaced");
        _registry.LatestFor("w1").Should().BeNull("the replaced conversation is not the default view any more");
        _registry.Find(replaced.Id).Should().Be(replaced, "it is dismissed, not erased");
        _registry.History("w1").Should().Contain(r => r.Id == replaced.Id);
    }

    [Fact]
    public async Task The_next_session_lifts_the_dismissal_and_becomes_the_latest()
    {
        var replaced = _registry.Start(StartRequest("the first conversation"));
        _runner.Finish();
        await Eventually(() => !replaced.IsRunning);
        _registry.StartFresh("w1");

        var fresh = _registry.Start(StartRequest("the second conversation"));

        _registry.LatestFor("w1").Should().Be(fresh);
    }

    [Fact]
    public void Starting_fresh_in_a_workspace_with_no_session_changes_nothing()
    {
        var notified = 0;
        _registry.SessionsChanged += () => notified++;

        _registry.StartFresh("w-empty");

        notified.Should().Be(0, "there is nothing to dismiss and nothing for a pane to re-resolve");
        _registry.LatestFor("w-empty").Should().BeNull();
    }

    // ---- the stream ---------------------------------------------------------------------

    [Fact]
    public async Task Streamed_events_become_the_transcript_and_notify_the_ui()
    {
        var session = _registry.Start(StartRequest());
        var notifications = 0;
        session.Changed += () => notifications++;

        _runner.Emit(new SessionRegistered(Now, "cli-session-1", "claude-haiku-4-5-20251001"));
        _runner.Emit(new ToolCallStarted(Now, "toolu_1", "Write", "/workspaces/w1/README.md"));
        _runner.Emit(new MessageEmitted(Now, TranscriptRole.Assistant, "Done."));

        await Eventually(() => session.Transcript.OfType<MessageEmitted>().Count() == 2);
        session.ProviderSessionId.Should().Be("cli-session-1");
        session.Model.Should().Be("claude-haiku-4-5-20251001");
        session.Transcript.OfType<ToolCallStarted>().Should().ContainSingle();
        notifications.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task The_message_being_typed_shows_live_and_is_replaced_by_the_finished_one()
    {
        var session = _registry.Start(StartRequest());

        _runner.Emit(new PartialText(Now, "Wri"));
        _runner.Emit(new PartialText(Now, "ting"));
        await Eventually(() => session.PartialText == "Writing");

        _runner.Emit(new MessageEmitted(Now, TranscriptRole.Assistant, "Writing the readme."));

        await Eventually(() => session.PartialText.Length == 0);
        session.Transcript.OfType<MessageEmitted>().Last().Text.Should().Be("Writing the readme.");
        _store.Transcript(session.Id).OfType<PartialText>().Should().BeEmpty();
    }

    [Fact]
    public async Task A_turn_whose_answer_the_transcript_already_carries_leaves_nothing_hanging_below_it()
    {
        // What the user actually sees, not what the parser emitted. The document repeats a bubble the
        // transcript already carries, so it commits no message of its own; the prose it repeats was
        // STREAMED, so its preview has to be gone by the time the turn closes or the finished turn has
        // a bubble hanging under it saying the same thing again.
        var session = _registry.Start(StartRequest());
        var parser = new ClaudeStreamJsonParser(() => Now);
        string[] turn =
        [
            """{"type":"stream_event","event":{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"The answer is 42."}}}""",
            """{"type":"assistant","message":{"id":"msg_a","content":[{"type":"text","text":"The answer is 42."}]}}""",
            """{"type":"assistant","message":{"id":"msg_a","content":[{"type":"tool_use","id":"toolu_r","name":"Read","input":{"file_path":"/x"}}]}}""",
            """{"type":"result","is_error":false,"terminal_reason":"completed","result":"ignored","structured_output":{"version":"1","kind":"answer","message":"The answer is 42."}}""",
        ];

        foreach (var e in turn.SelectMany(parser.Parse))
        {
            _runner.Emit(e);
        }

        await Eventually(() => session.Transcript.OfType<TurnCompleted>().Any());
        session.PartialText.Should().BeEmpty("the turn is over, so nothing is still being typed");
        session.Transcript.OfType<MessageEmitted>().Where(m => m.Role == TranscriptRole.Assistant)
            .Should().ContainSingle();
    }

    [Fact]
    public async Task Prose_the_reader_watched_being_typed_never_blanks_before_it_is_a_bubble()
    {
        // The third live defect of the held-prose design, and it was in this class rather than in the
        // parser: a message committed mid-turn clears the WHOLE preview, and while prose was held to
        // the result line the preview also held prose nothing had committed. So the answer the reader
        // watched appear vanished off the screen the moment a LATER message's narration committed.
        //
        // Driven as the user sees it: after each step, either the text is in the transcript or it is in
        // the preview, and never in neither.
        var session = _registry.Start(StartRequest());
        var parser = new ClaudeStreamJsonParser(() => Now);

        foreach (var e in Lines(
            """{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"The answer is 42."}}}""").SelectMany(parser.Parse))
        {
            _runner.Emit(e);
        }

        await Eventually(() => session.PartialText == "The answer is 42.");

        foreach (var e in Lines(
            """{"type":"assistant","message":{"id":"msg_a","content":[{"type":"text","text":"The answer is 42."}]}}""",
            """{"type":"stream_event","event":{"type":"message_start","message":{"role":"assistant"}}}""",
            """{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Let me double check that."}}}""",
            """{"type":"assistant","message":{"id":"msg_b","content":[{"type":"text","text":"Let me double check that."}]}}""",
            """{"type":"assistant","message":{"id":"msg_b","content":[{"type":"tool_use","id":"toolu_r","name":"Read","input":{"file_path":"/x"}}]}}""").SelectMany(parser.Parse))
        {
            _runner.Emit(e);
        }

        await Eventually(() => session.Transcript.OfType<ToolCallStarted>().Any());

        // The reader watched both appear, and both are still on the screen.
        session.Transcript.OfType<MessageEmitted>()
            .Where(m => m.Role == TranscriptRole.Assistant).Select(m => m.Text)
            .Should().Equal("The answer is 42.", "Let me double check that.");
        session.PartialText.Should().BeEmpty();
    }

    private static string[] Lines(params string[] lines) => lines;

    [Fact]
    public async Task The_phase_says_what_the_agent_is_doing_and_never_enters_the_transcript()
    {
        // Under a schema a turn can stream no text at all, so this is what the working indicator has.
        var session = _registry.Start(StartRequest());

        _runner.Emit(new TurnActivity(Now, AgentTurnPhase.Thinking));
        await Eventually(() => session.Phase == AgentTurnPhase.Thinking);

        _runner.Emit(new TurnActivity(Now, AgentTurnPhase.Composing));
        await Eventually(() => session.Phase == AgentTurnPhase.Composing);

        _runner.Emit(new TurnCompleted(Now, null, false, "completed"));

        await Eventually(() => session.Phase is null);
        session.Transcript.OfType<TurnActivity>().Should().BeEmpty();
        _store.Transcript(session.Id).OfType<TurnActivity>().Should().BeEmpty();
    }

    [Fact]
    public async Task Usage_is_kept_on_the_session_for_the_footer()
    {
        var session = _registry.Start(StartRequest());

        _runner.Emit(new StatsUpdated(Now, new AgentStatsSnapshot(18, 290, 41625, 0.02m, 2, 0, 1)));

        await Eventually(() => session.Stats.CostUsd == 0.02m);
        session.Stats.TotalTokens.Should().Be(308);
    }

    [Fact]
    public async Task A_session_that_ends_records_how_it_ended()
    {
        var session = _registry.Start(StartRequest());

        _runner.Emit(new SessionEnded(Now, AgentSessionState.Failed, "the CLI is not logged in"));

        await Eventually(() => !session.IsRunning);
        session.State.Should().Be(AgentSessionState.Failed);
        session.FailureReason.Should().Be("the CLI is not logged in");
        await Eventually(
            () => _store.Find(session.Id)?.State == AgentSessionState.Failed,
            "the stored record follows the live one");
    }

    // ---- surviving the window -----------------------------------------------------------

    [Fact]
    public async Task A_surface_that_attaches_late_sees_the_whole_conversation()
    {
        // The window closed and came back: the session is still here, with everything in it.
        var session = _registry.Start(StartRequest());
        _runner.Emit(new MessageEmitted(Now, TranscriptRole.Assistant, "working on it"));
        await Eventually(() => session.Transcript.Count >= 2);

        var reattached = _registry.RunningFor("w1");

        reattached.Should().BeSameAs(session);
        reattached!.Transcript.OfType<MessageEmitted>().Select(m => m.Text)
            .Should().ContainInOrder("write the readme", "working on it");
    }

    [Fact]
    public async Task The_transcript_is_persisted_as_it_arrives_not_only_at_the_end()
    {
        var session = _registry.Start(StartRequest());

        _runner.Emit(new MessageEmitted(Now, TranscriptRole.Assistant, "working on it"));

        await Eventually(() => _store.Transcript(session.Id).Count >= 2);
        _store.Transcript(session.Id).OfType<MessageEmitted>().Select(m => m.Text)
            .Should().ContainInOrder("write the readme", "working on it");
    }

    [Fact]
    public async Task A_resumed_conversation_comes_back_on_the_model_it_was_running_on()
    {
        // Seen on the installed 0.9.24 (backlog 126): a conversation started on Haiku was resumed after a
        // container restart, and the resumed child's command line, read from /proc, carried --resume and no
        // --model at all. The pane's model field starts empty on every page load and was the only thing
        // resume asked, so a conversation begun on the cheap model came back on the CLI's default. The
        // decision of 2026-09-15 is that a resumed conversation is the SAME conversation, and the
        // model is part of what it is. It is already on record: every start writes what the CLI registered.
        var first = _registry.Start(StartRequest("the first thing"));
        _runner.Emit(new SessionRegistered(Now, "cli-session-1", "claude-haiku-4-5-20251001"));
        await Eventually(() => first.ProviderSessionId is not null);
        _runner.Finish();
        await Eventually(() => !first.IsRunning);
        await Eventually(() => _store.Transcript(first.Id).OfType<SessionRegistered>().Any());

        _registry.Resume(first.Id, "/workspaces/w1");

        await Eventually(() => _runner.LastRequest?.ResumeSessionId is not null);
        _runner.LastRequest!.Model.Should().Be(
            "claude-haiku-4-5-20251001", "nobody picked a model, so the conversation keeps its own");
    }

    [Fact]
    public async Task A_model_the_user_picked_for_the_resume_still_wins_over_the_recorded_one()
    {
        var first = _registry.Start(StartRequest("the first thing"));
        _runner.Emit(new SessionRegistered(Now, "cli-session-1", "claude-haiku-4-5-20251001"));
        await Eventually(() => first.ProviderSessionId is not null);
        _runner.Finish();
        await Eventually(() => !first.IsRunning);

        _registry.Resume(first.Id, "/workspaces/w1", "sonnet");

        await Eventually(() => _runner.LastRequest?.ResumeSessionId is not null);
        _runner.LastRequest!.Model.Should().Be("sonnet");
    }

    [Fact]
    public async Task A_resumed_session_continues_the_cli_conversation_and_shows_the_earlier_turns()
    {
        var first = _registry.Start(StartRequest("the first thing"));
        _runner.Emit(new SessionRegistered(Now, "cli-session-1", null));
        await Eventually(() => first.ProviderSessionId is not null);
        _runner.Finish();
        await Eventually(() => !first.IsRunning);

        var resumed = _registry.Start(StartRequest("carry on", resumeOf: first.Id));

        await Eventually(() => _runner.LastRequest?.ResumeSessionId is not null);
        _runner.LastRequest!.ResumeSessionId.Should().Be("cli-session-1");
        resumed.Transcript.OfType<MessageEmitted>().Select(m => m.Text)
            .Should().ContainInOrder("the first thing", "carry on");
    }

    // ---- continuing after a turn exhausted its attempts (backlog 25 increment 6) -------------

    /// <summary>A session whose turn exhausted its validation attempts and whose process then exited.</summary>
    private async Task<LiveAgentSession> ExhaustedSession()
    {
        var session = _registry.Start(StartRequest("the first thing"));
        _runner.Emit(new SessionRegistered(Now, "cli-session-1", null));
        _runner.Emit(new TurnCompleted(
            Now, null, IsError: true, AgentTurnEnding.RetriesExhausted, ValidationFailures: 5));
        _runner.Emit(new SessionEnded(Now, AgentSessionState.Failed, ClaudeSessionRunner.ExhaustedEnding, Resumable: true));
        _runner.Complete();
        await Eventually(() => !session.IsRunning);
        await Eventually(() => _store.Find(session.Id)?.State == AgentSessionState.Failed);
        return session;
    }

    [Fact]
    public async Task A_conversation_whose_process_ended_after_an_exhausted_turn_reports_itself_resumable()
    {
        var session = await ExhaustedSession();

        session.IsResumable.Should().BeTrue();
        session.ProviderSessionId.Should().Be("cli-session-1", "the id --resume needs is kept");
        _registry.CanContinue(session.Id).Should().BeTrue();
    }

    [Fact]
    public async Task Continuing_resumes_the_same_cli_conversation_and_says_nothing_on_the_users_behalf()
    {
        var session = await ExhaustedSession();
        var messagesBefore = session.Transcript.OfType<MessageEmitted>().Count();

        var continued = _registry.Continue(session.Id, "/workspaces/w1");

        await Eventually(() => _runner.LastRequest?.SessionId == continued.Id);
        _runner.LastRequest!.ResumeSessionId.Should().Be("cli-session-1");
        _runner.LastRequest.Prompt.Should().BeEmpty("nothing is resent: the failed turn was already tried five times");
        continued.Transcript.OfType<MessageEmitted>().Should().HaveCount(messagesBefore, "the earlier turns are shown and nothing new was said");
        continued.Phase.Should().BeNull("nothing was submitted, so nothing is being worked on");
        continued.ProviderSessionId.Should().Be("cli-session-1", "a save before the next turn must not lose the id");
        continued.Title.Should().Be(session.Title);
    }

    [Fact]
    public async Task A_stored_conversation_that_ended_resumably_can_still_be_continued()
    {
        // After a studio restart the live object is gone; the ending that made it resumable is in the store.
        var session = await ExhaustedSession();
        using var restarted = new LiveSessionRegistry(new ScriptedRunner(), _store, _broker);

        restarted.CanContinue(session.Id).Should().BeTrue();
    }

    [Fact]
    public async Task A_conversation_that_simply_failed_is_not_offered_a_continue()
    {
        var session = _registry.Start(StartRequest());
        _runner.Emit(new SessionRegistered(Now, "cli-session-1", null));
        _runner.Finish(AgentSessionState.Failed);
        await Eventually(() => !session.IsRunning);

        session.IsResumable.Should().BeFalse();
        _registry.CanContinue(session.Id).Should().BeFalse();
        Assert.Throws<InvalidOperationException>(() => _registry.Continue(session.Id, "/workspaces/w1"));
    }

    // ---- talking back -------------------------------------------------------------------

    [Fact]
    public async Task A_follow_up_turn_goes_to_the_running_agent_and_into_the_transcript()
    {
        var session = _registry.Start(StartRequest());

        (await _registry.SendAsync(session.Id, "also add a licence")).Should().BeTrue();

        _runner.Sent.Should().ContainSingle().Which.Should().Be("also add a licence");
        session.Transcript.OfType<MessageEmitted>().Select(m => m.Text).Should().Contain("also add a licence");
    }

    private static AgentImageAttachment Png(byte marker) =>
        AgentImageAttachment.TryCreate([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, marker], out _)!;

    [Fact]
    public async Task A_follow_up_with_pasted_images_sends_them_and_keeps_them_above_the_words()
    {
        // Backlog 26: the images go to the runner with the turn, and the transcript records them as their own
        // row immediately BEFORE the user's message, in memory and in the store alike.
        var session = _registry.Start(StartRequest());
        var images = new[] { Png(1), Png(2) };

        (await _registry.SendAsync(session.Id, "what is wrong here?", images)).Should().BeTrue();

        _runner.SentImages.Should().ContainSingle().Which.Should().Equal(images);
        foreach (var transcript in new[] { session.Transcript, _store.Transcript(session.Id) })
        {
            var userTurn = transcript.ToList().FindLastIndex(e => e is MessageEmitted { Role: TranscriptRole.User });
            transcript[userTurn].Should().BeOfType<MessageEmitted>().Which.Text.Should().Be("what is wrong here?");
            transcript[userTurn - 1].Should().BeOfType<UserImagesAttached>().Which.Images.Should().Equal(images);
        }
    }

    [Fact]
    public async Task A_conversation_can_start_with_an_image_and_no_words()
    {
        var session = _registry.Start(StartRequest(prompt: string.Empty) with { Images = [Png(3)] });

        await Eventually(() => _runner.LastRequest is not null);
        _runner.LastRequest!.PromptImages.Should().ContainSingle().Which.Should().Be(Png(3));
        session.Title.Should().Be("Pasted image");
        session.Transcript.OfType<UserImagesAttached>().Should().ContainSingle();
    }

    [Fact]
    public async Task A_turn_with_more_images_than_one_may_carry_is_refused_before_anything_is_sent()
    {
        var session = _registry.Start(StartRequest());
        var tooMany = Enumerable.Range(0, AgentImageAttachment.MaxPerTurn + 1).Select(i => Png((byte)i)).ToList();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _registry.SendAsync(session.Id, "look", tooMany));

        _runner.Sent.Should().BeEmpty();
        session.Transcript.OfType<UserImagesAttached>().Should().BeEmpty();
    }

    [Fact]
    public async Task A_follow_up_to_a_finished_session_is_refused()
    {
        var session = _registry.Start(StartRequest());
        _runner.Finish();
        await Eventually(() => !session.IsRunning);

        (await _registry.SendAsync(session.Id, "hello?")).Should().BeFalse();
        _runner.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Stopping_a_session_stops_the_agent()
    {
        var session = _registry.Start(StartRequest());

        _registry.Stop(session.Id).Should().BeTrue();

        _runner.Stopped.Should().BeTrue();
        await Eventually(() => session.State == AgentSessionState.Cancelled);
    }

    // ---- changing the model --------------------------------------------------------------

    [Fact]
    public async Task The_model_changes_only_between_turns_never_under_a_running_one()
    {
        // Starting submits the opening turn, so the session begins mid-turn: the picker's enable state
        // and this refusal are the same fact, and the registry is the backstop behind the composer.
        var session = _registry.Start(StartRequest());
        _runner.Emit(new SessionRegistered(Now, "cli-session-1", "claude-haiku-4-5"));
        await Eventually(() => session.ProviderSessionId is not null);

        session.TurnInFlight.Should().BeTrue("the opening turn is still being answered");
        _registry.ChangeModel(session.Id, "claude-opus-4-5").Should().BeFalse();
        _runner.ModelChanges.Should().BeEmpty("ending the child mid-turn would throw the answer away");

        _runner.Emit(new TurnCompleted(Now, "done", false, "completed"));
        await Eventually(() => !session.TurnInFlight);

        _registry.ChangeModel(session.Id, "claude-opus-4-5").Should().BeTrue();
        _runner.ModelChanges.Should().ContainSingle().Which.Should().Be("claude-opus-4-5");
    }

    [Fact]
    public async Task A_finished_conversation_has_no_model_to_change()
    {
        var session = _registry.Start(StartRequest());
        _runner.Finish();
        await Eventually(() => !session.IsRunning);

        _registry.ChangeModel(session.Id, "claude-opus-4-5").Should().BeFalse();
        _runner.ModelChanges.Should().BeEmpty();
    }

    // ---- effort (requested 2026-10-22, model and effort per work item) ----------------------

    [Fact]
    public async Task A_start_without_an_effort_passes_none_and_records_nothing()
    {
        var session = _registry.Start(StartRequest());

        await Eventually(() => _runner.LastRequest is not null);
        _runner.LastRequest!.Effort.Should().BeNull();
        session.Effort.Should().BeNull();
        _store.Transcript(session.Id).OfType<EffortChosen>().Should().BeEmpty("Default leaves the transcript as it was");
    }

    [Fact]
    public async Task A_start_with_an_effort_passes_it_and_a_resume_naming_none_keeps_it()
    {
        var first = _registry.Start(StartRequest("the first thing") with { Effort = AgentEffort.High });
        await Eventually(() => _runner.LastRequest is not null);
        _runner.LastRequest!.Effort.Should().Be(AgentEffort.High);
        first.Effort.Should().Be(AgentEffort.High);

        _runner.Emit(new SessionRegistered(Now, "cli-session-1", null));
        await Eventually(() => first.ProviderSessionId is not null);
        _runner.Finish();
        await Eventually(() => !first.IsRunning);

        _registry.Resume(first.Id, "/workspaces/w1");

        await Eventually(() => _runner.LastRequest?.ResumeSessionId is not null);
        _runner.LastRequest!.Effort.Should().Be(
            AgentEffort.High, "nobody picked an effort, so the conversation keeps its own, as it keeps its model");
        _store.Transcript(first.Id).OfType<EffortChosen>().Should().ContainSingle(
            "a resume that leaves the effort where it was records nothing new");
    }

    [Fact]
    public async Task A_resume_of_a_conversation_the_studio_no_longer_holds_reads_the_effort_from_the_transcript()
    {
        var first = _registry.Start(StartRequest("the first thing") with { Effort = AgentEffort.Max });
        _runner.Emit(new SessionRegistered(Now, "cli-session-1", null));
        await Eventually(() => first.ProviderSessionId is not null);
        _runner.Finish();
        await Eventually(() => !first.IsRunning);

        // A fresh registry over the same store: the studio restarted and holds nothing in memory.
        using var restarted = new LiveSessionRegistry(_runner, _store, _broker);
        restarted.Resume(first.Id, "/workspaces/w1", effort: AgentEffort.Low);

        await Eventually(() => _runner.LastRequest?.ResumeSessionId is not null);
        _runner.LastRequest!.Effort.Should().Be(AgentEffort.Low, "a pick wins over the recorded effort");
        _store.Transcript(first.Id).OfType<EffortChosen>().Select(e => e.Effort)
            .Should().Equal(AgentEffort.Max, AgentEffort.Low);
    }

    [Fact]
    public async Task The_effort_changes_only_between_turns_and_the_change_is_recorded()
    {
        var session = _registry.Start(StartRequest());
        _runner.Emit(new SessionRegistered(Now, "cli-session-1", "claude-haiku-4-5"));
        await Eventually(() => session.ProviderSessionId is not null);

        _registry.ChangeEffort(session.Id, AgentEffort.Medium).Should().BeFalse("the opening turn is still being answered");
        _runner.EffortChanges.Should().BeEmpty();

        _runner.Emit(new TurnCompleted(Now, "done", false, "completed"));
        await Eventually(() => !session.TurnInFlight);

        _registry.ChangeEffort(session.Id, AgentEffort.Medium).Should().BeTrue();
        _runner.EffortChanges.Should().ContainSingle().Which.Should().Be(AgentEffort.Medium);
        _runner.ModelChanges.Should().BeEmpty("an effort change is not a model change");
        session.Effort.Should().Be(AgentEffort.Medium);
        _store.Transcript(session.Id).OfType<EffortChosen>().Should().ContainSingle()
            .Which.Effort.Should().Be(AgentEffort.Medium);
    }

    // ---- approvals ----------------------------------------------------------------------

    [Fact]
    public async Task A_tool_call_waiting_on_the_user_shows_on_the_session()
    {
        var session = _registry.Start(StartRequest());
        _broker.RegisterSession(session.Id);

        var pending = _broker.RequestAsync(
            session.Id, "Write", "toolu_1", new Dictionary<string, string> { ["file_path"] = "/workspaces/w1/README.md" });

        await Eventually(() => session.Pending.Count == 1);
        session.Pending[0].ToolName.Should().Be("Write");
        session.Pending[0].Detail.Should().Be("/workspaces/w1/README.md");
        session.Transcript.OfType<ToolApprovalRequested>().Should().ContainSingle();

        _registry.Answer(session.Pending[0].RequestId, approved: true, "approved in the studio").Should().BeTrue();

        (await pending).Approved.Should().BeTrue();
        await Eventually(() => session.Pending.Count == 0);
        session.Transcript.OfType<ToolApprovalResolved>().Single().Approved.Should().BeTrue();
    }

    [Fact]
    public async Task Denying_a_tool_call_is_recorded_with_its_reason()
    {
        var session = _registry.Start(StartRequest());
        _broker.RegisterSession(session.Id);
        var pending = _broker.RequestAsync(session.Id, "Bash", "toolu_1", new Dictionary<string, string> { ["command"] = "rm -rf /" });
        await Eventually(() => session.Pending.Count == 1);

        _registry.Answer(session.Pending[0].RequestId, approved: false, "not that command");

        (await pending).Approved.Should().BeFalse();
        await Eventually(() => session.Transcript.OfType<ToolApprovalResolved>().Any());
        var resolved = session.Transcript.OfType<ToolApprovalResolved>().Single();
        resolved.Approved.Should().BeFalse();
        resolved.Reason.Should().Be("not that command");
        resolved.DecidedBy.Should().Be(ToolApprovalDecidedBy.User);
    }

    [Fact]
    public async Task A_refusal_the_studio_imposed_reaches_the_transcript_as_the_studios_own()
    {
        // Backlog 114: the transcript event carries WHO decided, so the reader is never told they
        // refused a call they were never asked about.
        var session = _registry.Start(StartRequest());
        _broker.RegisterSession(session.Id);
        var pending = _broker.RequestAsync(session.Id, "Bash", "toolu_1", new Dictionary<string, string> { ["command"] = "rm -rf /" });
        await Eventually(() => session.Pending.Count == 1);

        _broker.ReleaseSession(session.Id);

        (await pending).Approved.Should().BeFalse();
        await Eventually(() => session.Transcript.OfType<ToolApprovalResolved>().Any());
        var resolved = session.Transcript.OfType<ToolApprovalResolved>().Single();
        resolved.Approved.Should().BeFalse();
        resolved.DecidedBy.Should().Be(ToolApprovalDecidedBy.Studio);
    }

    // ---- no refusal is invisible (backlog 117) -----------------------------------------------

    [Fact]
    public async Task A_refused_again_call_is_in_the_transcript_every_time_with_its_count()
    {
        // The rejected item-114 design returned a durable refusal before Requested fired, so an agent
        // could retry a refused call forever and the transcript showed nothing. Here the broker holds no
        // registration for the session, so every call is refused without asking.
        var session = _registry.Start(StartRequest());
        var bash = new Dictionary<string, string> { ["command"] = "curl evil.example" };

        for (var attempt = 0; attempt < 3; attempt++)
        {
            (await _broker.RequestAsync(session.Id, "Bash", "toolu_1", bash)).Approved.Should().BeFalse();
        }

        (await _broker.RequestAsync(session.Id, "Bash", "toolu_2", new Dictionary<string, string> { ["command"] = "ls" }))
            .Approved.Should().BeFalse();

        var refusals = session.Transcript.OfType<ToolCallRefused>().ToList();
        refusals.Select(r => (r.Detail, r.RepeatCount)).Should().Equal(
            ("curl evil.example", 1), ("curl evil.example", 2), ("curl evil.example", 3), ("ls", 1));
        refusals.Should().OnlyContain(r => r.ToolName == "Bash" && r.Reason.Length > 0);
        session.Pending.Should().BeEmpty();

        // And it is durable: the stored transcript carries the same rows and the same counts.
        await Eventually(() => _store.Transcript(session.Id).OfType<ToolCallRefused>().Count() == 4);
        _store.Transcript(session.Id).OfType<ToolCallRefused>().Select(r => r.RepeatCount).Should().Equal(1, 2, 3, 1);
    }

    [Fact]
    public async Task A_repeated_timeout_refusal_carries_its_count_and_the_users_own_answers_are_not_counted()
    {
        var broker = new AgentApprovalBroker(TimeSpan.FromMilliseconds(60));
        using var registry = new LiveSessionRegistry(_runner, _store, broker);
        var session = registry.Start(StartRequest());
        broker.RegisterSession(session.Id);
        var write = new Dictionary<string, string> { ["file_path"] = "/workspaces/w1/a.md", ["content"] = "x" };

        (await broker.RequestAsync(session.Id, "Write", "toolu_1", write)).Approved.Should().BeFalse();
        (await broker.RequestAsync(session.Id, "Write", "toolu_2", write)).Approved.Should().BeFalse();

        var resolved = session.Transcript.OfType<ToolApprovalResolved>().ToList();
        resolved.Should().HaveCount(2);
        resolved.Should().OnlyContain(r => r.DecidedBy == ToolApprovalDecidedBy.Studio);
        resolved.Select(r => r.RepeatCount).Should().Equal(1, 2);
        session.Transcript.OfType<ToolCallRefused>().Should().BeEmpty("a parked refusal is recorded once, as its resolution");
        await Eventually(() => _store.Transcript(session.Id).OfType<ToolApprovalResolved>().Count() == 2);
        _store.Transcript(session.Id).OfType<ToolApprovalResolved>().Select(r => r.RepeatCount).Should().Equal(1, 2);

        broker.Dispose();
    }

    [Fact]
    public async Task A_denial_the_user_gave_is_not_counted_as_an_automatic_refusal()
    {
        var session = _registry.Start(StartRequest());
        _broker.RegisterSession(session.Id);
        var bash = new Dictionary<string, string> { ["command"] = "rm -rf /" };

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var pending = _broker.RequestAsync(session.Id, "Bash", "toolu_1", bash);
            await Eventually(() => session.Pending.Count == 1);
            _registry.Answer(session.Pending[0].RequestId, approved: false, "no");
            (await pending).Approved.Should().BeFalse();
            await Eventually(() => session.Pending.Count == 0);
        }

        session.Transcript.OfType<ToolApprovalResolved>().Select(r => r.RepeatCount).Should().Equal(1, 1);
    }

    [Theory]
    [InlineData("""{"timestamp":"2026-08-13T09:00:00.0000000+00:00","requestId":"r","toolName":"Bash","approved":false,"reason":"x","decidedBy":"Studio"}""", 1)]
    [InlineData("""{"timestamp":"2026-08-13T09:00:00.0000000+00:00","requestId":"r","toolName":"Bash","approved":false,"reason":"x","decidedBy":"Studio","repeatCount":0}""", 1)]
    [InlineData("""{"timestamp":"2026-08-13T09:00:00.0000000+00:00","requestId":"r","toolName":"Bash","approved":false,"reason":"x","decidedBy":"Studio","repeatCount":4}""", 4)]
    public void A_stored_resolution_reads_its_repeat_count_back_and_an_older_row_reads_as_once(string json, int expected) =>
        AgentEventJson.Deserialize("approval_resolved", json).Should().BeOfType<ToolApprovalResolved>()
            .Which.RepeatCount.Should().Be(expected);

    [Fact]
    public void A_first_resolution_is_written_exactly_as_before_the_count_existed()
    {
        var once = AgentEventJson.Serialize(new ToolApprovalResolved(Now, "r", "Bash", false, "x"));
        once.Should().NotContain("repeatCount");

        var refused = new ToolCallRefused(Now, "Bash", "ls", "The studio has no running session to ask about this call.", 3);
        AgentEventJson.KindOf(refused).Should().Be("tool_refused");
        AgentEventJson.Deserialize("tool_refused", AgentEventJson.Serialize(refused)).Should().Be(refused);
    }

    [Fact]
    public async Task A_read_inside_the_copy_never_shows_up_as_something_to_approve()
    {
        var session = _registry.Start(StartRequest());
        _broker.RegisterSession(session.Id, "/workspaces/w1");

        var decision = await _broker.RequestAsync(
            session.Id, "Read", "toolu_1", new Dictionary<string, string> { ["file_path"] = "/workspaces/w1/a.cs" });

        decision.Approved.Should().BeTrue();
        session.Pending.Should().BeEmpty();
        session.Transcript.OfType<ToolApprovalRequested>().Should().BeEmpty();
    }

    [Fact]
    public async Task A_read_that_leaves_the_copy_reaches_the_transcript_with_its_reason()
    {
        var session = _registry.Start(StartRequest());
        _broker.RegisterSession(session.Id, "/workspaces/w1");

        var pending = _broker.RequestAsync(
            session.Id, "Read", "toolu_1", new Dictionary<string, string> { ["file_path"] = "/proc/1/environ" });

        await Eventually(() => session.Pending.Count == 1);
        session.Pending[0].Warning.Should().NotBeNullOrWhiteSpace();
        session.Transcript.OfType<ToolApprovalRequested>().Single()
            .Warning.Should().NotBeNullOrWhiteSpace("the transcript has to keep why the studio asked");

        _registry.Answer(session.Pending[0].RequestId, approved: false, "no");
        (await pending).Approved.Should().BeFalse();
    }

    // ---- the autopilot's own rule list and judge (ADR 0031, backlog 299) ----------------------

    [Fact]
    public async Task A_rule_allow_on_an_autopilot_session_reaches_the_transcript_naming_the_rule()
    {
        var session = _registry.Start(StartRequest());
        _broker.RegisterSession(session.Id, "/workspaces/w1");
        _broker.SetAutopilot(session.Id, true);

        var decision = await _broker.RequestAsync(
            session.Id, "Bash", "toolu_1", new Dictionary<string, string> { ["command"] = "dotnet build" });

        decision.Approved.Should().BeTrue();
        session.Pending.Should().BeEmpty("the rule list answered it, so nobody was asked");
        var allowed = session.Transcript.OfType<ToolCallAutopilotAllowed>().Should().ContainSingle().Which;
        allowed.ToolName.Should().Be("Bash");
        allowed.Reason.Should().Contain("rule list");

        // And it is durable: the stored transcript carries the same row.
        await Eventually(() => _store.Transcript(session.Id).OfType<ToolCallAutopilotAllowed>().Any());
        _store.Transcript(session.Id).OfType<ToolCallAutopilotAllowed>().Single().Reason.Should().Be(allowed.Reason);
    }

    // ---- renaming (backlog 109) ---------------------------------------------------------------

    [Fact]
    public void A_conversation_is_titled_from_its_first_prompt_until_it_is_renamed()
    {
        var session = _registry.Start(StartRequest("write the readme"));

        session.Title.Should().Be("write the readme");
        session.CustomTitle.Should().BeNull();
        _store.Find(session.Id)!.DisplayTitle.Should().Be("write the readme");
    }

    [Fact]
    public void Renaming_a_live_session_changes_what_every_surface_shows_and_persists_it()
    {
        var session = _registry.Start(StartRequest("write the readme"));
        var listNotified = 0;
        var sessionNotified = 0;
        _registry.SessionsChanged += () => listNotified++;
        session.Changed += () => sessionNotified++;

        _registry.Rename(session.Id, "  Readme rewrite  ").Should().BeTrue();

        session.Title.Should().Be("Readme rewrite");
        session.DerivedTitle.Should().Be("write the readme", "the derived title stays as the fallback");
        listNotified.Should().BeGreaterThan(0, "the list shows the title");
        sessionNotified.Should().BeGreaterThan(0, "the pane shows the title");
        var stored = _store.Find(session.Id)!;
        stored.CustomTitle.Should().Be("Readme rewrite");
        stored.Title.Should().Be("write the readme");
    }

    [Fact]
    public async Task A_renamed_sessions_name_survives_the_saves_its_own_lifecycle_makes()
    {
        // SessionRegistered and SessionEnded each re-save the record from the live session. Neither may
        // put the derived title back.
        var session = _registry.Start(StartRequest("write the readme"));
        _registry.Rename(session.Id, "Readme rewrite");

        _runner.Emit(new SessionRegistered(Now, "cli-session-1", null));
        await Eventually(() => _store.Find(session.Id)?.ProviderSessionId == "cli-session-1");
        _runner.Finish();
        await Eventually(() => _store.Find(session.Id)?.State == AgentSessionState.Completed);

        _store.Find(session.Id)!.DisplayTitle.Should().Be("Readme rewrite");
    }

    [Fact]
    public void Renaming_a_stored_only_session_persists_through_the_store()
    {
        // The studio restarted: the conversation is a row, not a live object, and the list still offers
        // the rename.
        var id = AgentSessionId.New();
        _store.Save(new SessionRecord(
            id, "w1", AgentTool.Claude, "an older conversation", "cli-1",
            AgentSessionState.Completed, Now, Now));
        _registry.Find(id).Should().BeNull("this one is not live");
        var notified = 0;
        _registry.SessionsChanged += () => notified++;

        _registry.Rename(id, "The old one").Should().BeTrue();

        notified.Should().BeGreaterThan(0);
        var stored = _store.Find(id)!;
        stored.CustomTitle.Should().Be("The old one");
        stored.Title.Should().Be("an older conversation");
        _registry.History("w1").Single().DisplayTitle.Should().Be("The old one");
    }

    [Fact]
    public void A_blank_or_unchanged_rename_leaves_the_name_alone()
    {
        var session = _registry.Start(StartRequest("write the readme"));
        _registry.Rename(session.Id, "Readme rewrite");
        var notified = 0;
        _registry.SessionsChanged += () => notified++;

        _registry.Rename(session.Id, "   ").Should().BeFalse();
        _registry.Rename(session.Id, "Readme rewrite").Should().BeFalse();
        _registry.Rename(AgentSessionId.New(), "nobody").Should().BeFalse();

        notified.Should().Be(0);
        session.Title.Should().Be("Readme rewrite");
        _store.Find(session.Id)!.CustomTitle.Should().Be("Readme rewrite");
    }

    [Fact]
    public async Task A_resumed_conversation_keeps_the_name_it_was_given()
    {
        var first = _registry.Start(StartRequest("the first thing"));
        _runner.Emit(new SessionRegistered(Now, "cli-session-1", null));
        await Eventually(() => first.ProviderSessionId is not null);
        _runner.Finish();
        await Eventually(() => !first.IsRunning);
        _registry.Rename(first.Id, "The long one");

        var resumed = _registry.Start(StartRequest("carry on", resumeOf: first.Id));

        // Resumed IN PLACE (owner report 2026-09-15): the same conversation, so the same name, derived title
        // and row, rather than a second row that borrowed the name.
        resumed.Id.Should().Be(first.Id);
        resumed.Title.Should().Be("The long one");
        resumed.DerivedTitle.Should().Be("the first thing");
        _store.Find(first.Id)!.CustomTitle.Should().Be("The long one");
        _registry.History("w1").Should().ContainSingle();
    }

    [Fact]
    public void The_history_list_comes_from_the_store()
    {
        var session = _registry.Start(StartRequest());

        _registry.History("w1").Should().ContainSingle().Which.Id.Should().Be(session.Id);
        _registry.History("w2").Should().BeEmpty();
    }

    // ---- backlog 34: the home surface's cross-workspace list and its delete ------------------

    [Fact]
    public async Task The_recent_conversations_list_spans_every_workspace()
    {
        var first = _registry.Start(StartRequest());
        _runner.Finish();
        await Eventually(() => !first.IsRunning);
        var second = _registry.Start(new SessionStartRequest("w2", "/workspaces/w2", "fix the build"));

        var recent = _registry.RecentConversations();

        // The set, not the order: two sessions started milliseconds apart can share a clock tick here,
        // and the ordering is pinned against explicit stamps in SessionStoreTests.
        recent.Select(r => r.Id).Should().BeEquivalentTo([second.Id, first.Id]);
    }

    [Fact]
    public async Task Deleting_a_conversation_forgets_it_and_its_transcript_and_tells_the_lists()
    {
        var session = _registry.Start(StartRequest());
        var notified = 0;
        _registry.SessionsChanged += () => notified++;
        _runner.Finish();
        // Waiting for the END of the session is not enough: the pump applies the event and raises
        // SessionsChanged a moment later, so the count is zeroed only once that notification is in.
        await Eventually(() => !session.IsRunning && notified > 0);
        notified = 0;

        _registry.Delete(session.Id).Should().BeTrue();

        _store.Find(session.Id).Should().BeNull();
        _store.Transcript(session.Id).Should().BeEmpty();
        _registry.History("w1").Should().BeEmpty();
        // Dropped from the LIVE set too, or the sessions sidebar would keep listing a conversation
        // whose transcript is gone.
        _registry.Find(session.Id).Should().BeNull();
        notified.Should().Be(1);
    }

    [Fact]
    public void A_running_conversation_is_refused_rather_than_deleted_under_its_own_pump()
    {
        var session = _registry.Start(StartRequest());
        var notified = 0;
        _registry.SessionsChanged += () => notified++;

        _registry.Delete(session.Id).Should().BeFalse();

        _store.Find(session.Id).Should().NotBeNull();
        _registry.Find(session.Id).Should().NotBeNull();
        notified.Should().Be(0);
    }

    // ---- rows whose process is gone (owner report 2026-09-15) --------------------------------

    [Fact]
    public void A_stored_row_still_marked_running_with_no_process_here_reads_as_ended_and_resumable()
    {
        // A container recreate kills the CLI without the pump ever seeing an ending, so the row says
        // Running for ever ("hi. testing" and "use test-skill" on the owner's list). No process in THIS
        // studio means it is not running, and the row and the transcript are corrected to say so.
        var orphan = AgentSessionId.New();
        _store.Save(new SessionRecord(
            orphan, "w1", AgentTool.Claude, "hi. testing", "cli-orphan", AgentSessionState.Running, Now, null));
        var neverRegistered = AgentSessionId.New();
        _store.Save(new SessionRecord(
            neverRegistered, "w1", AgentTool.Claude, "use test-skill", null, AgentSessionState.Running, Now.AddMinutes(1), null));
        var lastSaid = Now.AddMinutes(7);
        _store.Append(orphan, [new MessageEmitted(lastSaid, TranscriptRole.Assistant, "the last thing it said")]);

        var listed = _registry.History("w1");

        // Ended when it last said anything, not whenever the list happened to be opened (which, with the
        // registry's real clock, is today: days of made-up duration).
        listed.Single(r => r.Id == orphan).EndedUtc.Should().Be(lastSaid);
        _store.Find(orphan)!.EndedUtc.Should().Be(lastSaid);
        listed.Single(r => r.Id == neverRegistered).EndedUtc.Should().Be(Now.AddMinutes(1), "with nothing said, its start");

        listed.Should().OnlyContain(r => r.State == AgentSessionState.Cancelled && r.EndedUtc != null);
        _registry.RecentConversations().Should().OnlyContain(r => r.State == AgentSessionState.Cancelled);
        _store.Find(orphan)!.State.Should().Be(AgentSessionState.Cancelled, "the stored state is corrected, not only the shown one");
        _store.Transcript(orphan).OfType<SessionEnded>().Should().ContainSingle()
            .Which.FailureReason.Should().Contain("Resume");
        _registry.CanResume(orphan).Should().BeTrue("it has the provider id --resume needs");
        _registry.CanResume(neverRegistered).Should().BeFalse("with no provider id there is nothing to resume");
    }

    [Fact]
    public void A_restart_reason_left_by_the_launcher_replaces_the_generic_ending_for_this_processs_orphans()
    {
        // Backlog 218: the launcher can leave a plain-words reason for a restart it caused on purpose (its
        // DNS recovery is the first writer). Every orphan THIS process finds was orphaned by the same
        // restart that started it, so one value read at construction covers every one of them.
        using var registry = new LiveSessionRegistry(
            _runner, _store, _broker, restartReason: "Fravashi restarted its studio because the network changed.");

        var orphan = AgentSessionId.New();
        _store.Save(new SessionRecord(
            orphan, "w1", AgentTool.Claude, "hi", "cli-orphan", AgentSessionState.Running, Now, null));

        registry.History("w1");

        _store.Transcript(orphan).OfType<SessionEnded>().Should().ContainSingle()
            .Which.FailureReason.Should().Be(
                "Fravashi restarted its studio because the network changed. Resume continues the conversation.");
    }

    [Fact]
    public void A_session_that_is_really_running_here_is_never_marked_ended_by_the_list()
    {
        var session = _registry.Start(StartRequest());

        _registry.History("w1").Single().State.Should().Be(AgentSessionState.Running);
        _store.Find(session.Id)!.State.Should().Be(AgentSessionState.Running);
        session.IsRunning.Should().BeTrue();
    }

    // ---- backlog 156: a conversation left mid-turn by a restart is resumed -----------------------

    [Fact]
    public async Task At_start_a_conversation_left_mid_turn_is_resumed_and_told_and_an_idle_one_is_left_alone()
    {
        using var registry = new LiveSessionRegistry(_runner, _store, _broker, sync: new CopyOnlySync());
        var now = DateTimeOffset.UtcNow;

        // Mid-turn: a person's prompt and a tool call after the last completed turn, and nothing after them.
        var midTurn = AgentSessionId.New();
        _store.Save(new SessionRecord(midTurn, "w-mid", AgentTool.Claude, "t", "prov-mid", AgentSessionState.Running, now, null));
        _store.Append(midTurn,
        [
            new MessageEmitted(now, TranscriptRole.User, "first"),
            new TurnCompleted(now, "done", false, "completed"),
            new MessageEmitted(now, TranscriptRole.User, "second"),
            new ToolCallStarted(now, "toolu_1", "Edit", "/workspaces/w-mid/a.cs"),
        ]);

        // Idle: its last turn completed. Left for a person, exactly as before.
        var idle = AgentSessionId.New();
        _store.Save(new SessionRecord(idle, "w-idle", AgentTool.Claude, "t", "prov-idle", AgentSessionState.Running, now, null));
        _store.Append(idle, [new MessageEmitted(now, TranscriptRole.User, "x"), new TurnCompleted(now, "ok", false, "completed")]);

        // Mid-turn but never announced a provider id: nothing for --resume to name.
        var orphan = AgentSessionId.New();
        _store.Save(new SessionRecord(orphan, "w-orphan", AgentTool.Claude, "t", null, AgentSessionState.Running, now, null));
        _store.Append(orphan, [new MessageEmitted(now, TranscriptRole.User, "x")]);

        registry.ResumeInterruptedConversations().Should().Be(1);

        // The conversation is started on a background task, so the request is waited for, not read at once.
        await Eventually(() => _runner.LastRequest is not null);
        _runner.LastRequest!.SessionId.Should().Be(midTurn, "the conversation continues under its own id");
        _runner.LastRequest.ResumeSessionId.Should().Be("prov-mid");
        _runner.LastRequest.Prompt.Should().Be(LiveSessionRegistry.InterruptedTurnPrompt);
        _store.Transcript(midTurn).OfType<MessageEmitted>()
            .Should().Contain(m => m.Role == TranscriptRole.System && m.Text == LiveSessionRegistry.InterruptedTurnNotice);

        // Backlog 184: the studio wrote that prompt, so it is recorded as the studio's and never as the person's.
        _store.Transcript(midTurn).OfType<MessageEmitted>().Where(m => m.Text == LiveSessionRegistry.InterruptedTurnPrompt)
            .Should().ContainSingle().Which.Role.Should().Be(TranscriptRole.StudioPrompt);

        // The idle row was not started and, unread so far, still says what it said; a read settles it as before.
        _store.Find(idle)!.State.Should().Be(AgentSessionState.Running);
        registry.History("w-idle").Single().State.Should().Be(AgentSessionState.Cancelled);
    }

    [Fact]
    public async Task A_conversation_resumed_after_a_restart_the_launcher_explained_is_told_that_reason_and_so_is_the_person()
    {
        // Backlog 218, seen in the installed v0.9.59: the reason reached only the stored ending, which the transcript
        // shows as "Session stopped.", and the agent got the generic prompt, so nobody was told the network changed.
        const string reason = "Fravashi restarted its studio because this PC's network changed.";
        using var registry = new LiveSessionRegistry(_runner, _store, _broker, sync: new CopyOnlySync(), restartReason: reason);
        var now = DateTimeOffset.UtcNow;
        var midTurn = AgentSessionId.New();
        _store.Save(new SessionRecord(midTurn, "w-mid", AgentTool.Claude, "t", "prov-mid", AgentSessionState.Running, now, null));
        _store.Append(midTurn, [new MessageEmitted(now, TranscriptRole.User, "go"), new ToolCallStarted(now, "toolu_1", "Bash", "dotnet build")]);

        registry.ResumeInterruptedConversations().Should().Be(1);

        await Eventually(() => _runner.LastRequest is not null);
        _runner.LastRequest!.Prompt.Should().StartWith(reason).And.Contain("your last step may be half done");
        _store.Transcript(midTurn).OfType<MessageEmitted>()
            .Should().Contain(m => m.Role == TranscriptRole.System && m.Text.StartsWith(reason, StringComparison.Ordinal)
                && m.Text.Contains("resumed automatically", StringComparison.Ordinal));
    }

    // ---- backlog 157: the upgrade a conversation scheduled, at the end of its turn -----------------------

    [Fact]
    public async Task A_scheduled_upgrade_is_carried_out_when_the_turn_ends_and_the_conversation_is_not_stopped()
    {
        var upgrades = new Tools.StudioUpgradeToolTests.OfferingUpgrades("0.9.45");
        var schedule = new StudioUpgradeSchedule(_directory);
        using var registry = new LiveSessionRegistry(_runner, _store, _broker, upgrades: upgrades, upgradeSchedule: schedule);
        var session = registry.Start(StartRequest());
        _runner.Emit(new SessionRegistered(Now, "cli-1", "claude-opus-5-5"));
        schedule.Schedule(session.Id, "0.9.45").Should().BeNull();

        _runner.Emit(new TurnCompleted(Now, "done", false, "completed"));
        await Eventually(() => upgrades.Applied.Count == 1);

        upgrades.Applied.Should().Equal("0.9.45");
        _runner.Stopped.Should().BeFalse("a stop would make the row terminal, and the resume skips terminal rows");
        await Eventually(() => session.Transcript.OfType<MessageEmitted>().Any(m => m.Text.StartsWith("Upgrading Fravashi to 0.9.45", StringComparison.Ordinal)));
        File.Exists(Path.Combine(_directory, StudioUpgradeSchedule.FolderName, session.Id.Value.ToString("N") + ".txt"))
            .Should().BeTrue("the marker is written BEFORE fravashi.exe is asked, because asking may end this process");
    }

    [Fact]
    public async Task An_upgrade_the_host_refused_is_said_and_the_conversation_simply_goes_on()
    {
        var upgrades = new Tools.StudioUpgradeToolTests.OfferingUpgrades("0.9.45") { Refusal = "An upgrade to 0.9.45 is already running." };
        var schedule = new StudioUpgradeSchedule(_directory);
        using var registry = new LiveSessionRegistry(_runner, _store, _broker, upgrades: upgrades, upgradeSchedule: schedule);
        var session = registry.Start(StartRequest());
        schedule.Schedule(session.Id, "0.9.45");

        _runner.Emit(new TurnCompleted(Now, "done", false, "completed"));

        await Eventually(() => session.Transcript.OfType<MessageEmitted>().Any(m =>
            m.Text == "The upgrade to 0.9.45 did not start: An upgrade to 0.9.45 is already running."));
        schedule.TakeResume(session.Id).Should().BeNull("a refused upgrade resumes nothing at the next start");
        session.IsRunning.Should().BeTrue();
    }

    [Fact]
    public async Task At_start_a_conversation_that_asked_for_the_upgrade_is_resumed_on_it_though_its_turn_had_ended()
    {
        var schedule = new StudioUpgradeSchedule(_directory);
        using var registry = new LiveSessionRegistry(_runner, _store, _broker, sync: new CopyOnlySync(), upgradeSchedule: schedule);
        var now = DateTimeOffset.UtcNow;
        var asked = AgentSessionId.New();
        _store.Save(new SessionRecord(asked, "w-up", AgentTool.Claude, "t", "prov-up", AgentSessionState.Running, now, null));
        _store.Append(asked, [new MessageEmitted(now, TranscriptRole.User, "cut the release and upgrade"), new TurnCompleted(now, "ok", false, "completed")]);
        schedule.MarkResume(asked, "0.9.45");

        registry.ResumeInterruptedConversations().Should().Be(1, "it is not mid-turn, and it is resumed all the same");

        // Started on a background task (CI run 279 read it too early and found nothing), so it is waited for.
        await Eventually(() => _runner.LastRequest is not null);
        _runner.LastRequest!.SessionId.Should().Be(asked);
        _runner.LastRequest.ResumeSessionId.Should().Be("prov-up");
        _runner.LastRequest.Prompt.Should().Be(LiveSessionRegistry.UpgradedPrompt("0.9.45"));
        _store.Transcript(asked).OfType<MessageEmitted>()
            .Should().Contain(m => m.Role == TranscriptRole.System && m.Text == LiveSessionRegistry.UpgradedNotice("0.9.45"));
        schedule.TakeResume(asked).Should().BeNull("the marker is read once");
        _store.Transcript(asked).OfType<MessageEmitted>().Where(m => m.Text == LiveSessionRegistry.UpgradedPrompt("0.9.45"))
            .Should().ContainSingle().Which.Role.Should().Be(TranscriptRole.StudioPrompt, "the studio wrote it, not the person");

        // In the order it happened: the previous run's ending, then the note (it read "Session stopped." under the
        // note in the first real upgrade, 2026-10-02).
        var transcript = _store.Transcript(asked);
        var ending = transcript.ToList().FindLastIndex(e => e is SessionEnded);
        var note = transcript.ToList().FindIndex(e => e is MessageEmitted { Role: TranscriptRole.System } m && m.Text == LiveSessionRegistry.UpgradedNotice("0.9.45"));
        ending.Should().BeGreaterThanOrEqualTo(0).And.BeLessThan(note);
    }

    [Fact]
    public void Mid_turn_is_read_off_the_stored_transcript()
    {
        var now = DateTimeOffset.UtcNow;
        LiveSessionRegistry.WasMidTurn([]).Should().BeFalse();
        LiveSessionRegistry.WasMidTurn([new MessageEmitted(now, TranscriptRole.User, "go")]).Should().BeTrue();
        LiveSessionRegistry.WasMidTurn([new MessageEmitted(now, TranscriptRole.User, "go"), new TurnCompleted(now, "ok", false, "completed")])
            .Should().BeFalse();
        LiveSessionRegistry.WasMidTurn([new TurnCompleted(now, "ok", false, "completed"), new ToolCallStarted(now, "t", "Bash", "ls")])
            .Should().BeTrue();
        LiveSessionRegistry.WasMidTurn([new MessageEmitted(now, TranscriptRole.User, "go"), new SessionEnded(now, AgentSessionState.Cancelled, null)])
            .Should().BeFalse("an ending after the prompt means nothing was in flight");
        LiveSessionRegistry.WasMidTurn([new MessageEmitted(now, TranscriptRole.Assistant, "reply")]).Should().BeFalse();
        LiveSessionRegistry.WasMidTurn([new TurnCompleted(now, "ok", false, "completed"), new MessageEmitted(now, TranscriptRole.StudioPrompt, "go on")])
            .Should().BeTrue("a resume the studio sent starts a turn, so a second restart right after it must resume it again");
    }

    [Fact]
    public async Task A_prompt_a_person_types_is_still_theirs_on_the_first_turn_and_after_a_hand_resume()
    {
        var first = _registry.Start(StartRequest("the first thing"));
        _runner.Emit(new SessionRegistered(Now, "cli-session-1", "claude-haiku-4-5-20251001"));
        await Eventually(() => first.ProviderSessionId is not null);
        _runner.Finish();
        await Eventually(() => !first.IsRunning);

        var resumed = _registry.Resume(first.Id, "/workspaces/w1");
        await Eventually(() => _runner.LastRequest?.ResumeSessionId is not null);
        resumed.Id.Should().Be(first.Id);
        (await _registry.SendAsync(resumed.Id, "and now the tests")).Should().BeTrue();

        var messages = _store.Transcript(first.Id).OfType<MessageEmitted>().ToList();
        messages.Should().ContainSingle(m => m.Text == "the first thing").Which.Role.Should().Be(TranscriptRole.User);
        messages.Should().ContainSingle(m => m.Text == "and now the tests").Which.Role.Should().Be(TranscriptRole.User);
        messages.Should().NotContain(m => m.Role == TranscriptRole.StudioPrompt, "the studio sent nothing here");
    }

    /// <summary>A sync engine that has a copy for every workspace and nothing else: enough to resume against.</summary>
    private sealed class CopyOnlySync : Fravashi.Application.AgentSync.IWorkspaceAgentSync
    {
        public void EnsureStarted(string workspaceId)
        {
        }

        public string? CopyPathFor(string workspaceId) => "/workspaces/" + workspaceId;

        public object? StatusFor(string workspaceId) => null;
    }
}
