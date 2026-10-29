using System.Text.Json;
using System.Threading.Channels;
using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.Sessions.Tools;
using Fravashi.Infrastructure.WorkItems;
using NSubstitute;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Backlog 244: the operator's attention per conversation. Every decision the approval broker makes is counted with its
/// tool kind, its outcome and its wait; a person's follow-up turn is guidance and the opening prompt is not; a commit
/// review's verdict is counted; and the three reads (the pane's line, the work item's roll-up, the session_attention
/// tool) agree with each other and with the transcript the studio already keeps.
/// </summary>
public sealed class SessionAttentionTests : IDisposable
{
    private const string Copy = "/workspaces/w1";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fravashi-attention-" + Guid.NewGuid().ToString("N"));

    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteSessionAttention _attention;
    private DateTimeOffset _now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    public SessionAttentionTests()
    {
        Directory.CreateDirectory(_directory);
        _factory = new SqliteConnectionFactory(
            new SqliteStoreOptions(Path.Combine(_directory, "fravashi.db"), Pooling: false));
        SqliteSchema.EnsureCreated(_factory);
        _attention = new SqliteSessionAttention(_factory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private AgentApprovalBroker Broker(
        TimeSpan? timeout = null, ISessionWorkItems? work = null, ICommitReviewer? reviewer = null) =>
        new(
            timeout ?? TimeSpan.FromMinutes(5),
            clock: () => _now,
            work: work,
            reviewer: reviewer is null ? null : () => reviewer,
            attention: _attention);

    private static Dictionary<string, string> Writing(string path) =>
        new() { ["file_path"] = path, ["content"] = "x" };

    /// <summary>Parks a call, waits <paramref name="wait"/> of studio time, answers it as the person would.</summary>
    private async Task<ToolApprovalDecision> AskAndAnswer(
        AgentApprovalBroker broker, AgentSessionId session, string tool, Dictionary<string, string> input,
        TimeSpan wait, bool approved, string? reason = null)
    {
        PendingToolApproval? seen = null;
        void Seen(PendingToolApproval p) => seen = p;
        broker.Requested += Seen;
        try
        {
            var pending = broker.RequestAsync(session, tool, "toolu_" + Guid.NewGuid().ToString("N"), input);
            seen.Should().NotBeNull($"{tool} should have asked");
            _now += wait;
            broker.Answer(seen!.RequestId, approved, ToolApprovalDecision.UserReason(approved, reason));
            return await pending;
        }
        finally
        {
            broker.Requested -= Seen;
        }
    }

    // ---- every decision, its kind, its outcome, its wait ------------------------------------------------

    [Fact]
    public async Task Every_decision_is_counted_with_its_kind_its_outcome_and_its_wait()
    {
        using var broker = Broker();
        var session = AgentSessionId.New();
        broker.RegisterSession(session, Copy, typedTurns: true);

        // Auto-allowed by the studio's own rules: a read inside the copy, and a graph creation.
        (await broker.RequestAsync(session, "Read", "t1", new Dictionary<string, string> { ["file_path"] = Copy + "/a.cs" }))
            .Approved.Should().BeTrue();
        (await broker.RequestAsync(
                session, AgentWorkGate.StudioToolPrefix + "work_item_create", "t2",
                new Dictionary<string, string> { ["title"] = "x" }))
            .Approved.Should().BeTrue();

        // Answered by a person, each after its own wait.
        (await AskAndAnswer(broker, session, "Write", Writing(Copy + "/n.md"), TimeSpan.FromSeconds(30), approved: true))
            .Approved.Should().BeTrue();
        (await AskAndAnswer(broker, session, "Bash", new() { ["command"] = "dotnet test" }, TimeSpan.FromSeconds(200), approved: false))
            .Approved.Should().BeFalse();
        (await AskAndAnswer(
                broker, session, "Edit", new() { ["file_path"] = Copy + "/n.md", ["old_string"] = "a", ["new_string"] = "b" },
                TimeSpan.FromSeconds(30), approved: false, reason: "use the helper"))
            .Approved.Should().BeFalse();

        // The turn-ending document is how a turn ends, not a call anyone answers: not counted.
        (await broker.RequestAsync(session, AgentTurnOutcomeTool.Name, "t9", new Dictionary<string, string>()))
            .Approved.Should().BeTrue();

        var counted = _attention.For(session);
        counted.Asked.Should().Be(3);
        counted.Count(AttentionOutcome.Approved).Should().Be(1);
        counted.Count(AttentionOutcome.Denied).Should().Be(1);
        counted.Count(AttentionOutcome.DeniedWithReason).Should().Be(1);
        counted.Auto.Should().Be(2, "the read and the graph creation, and not the turn-ending document");
        counted.Refused.Should().Be(0);
        counted.Waited.Should().Be(TimeSpan.FromSeconds(260));
        counted.AskedByKind.Should().BeEquivalentTo(new Dictionary<AttentionToolKind, int>
        {
            [AttentionToolKind.Write] = 1,
            [AttentionToolKind.Shell] = 1,
            [AttentionToolKind.Edit] = 1,
        });
        counted.Line.Should().Be(
            "3 approvals asked: 1 approved, 1 denied with a reason, 1 denied; waited 4 min 20 s in all; 2 auto");
    }

    [Fact]
    public async Task A_denial_with_a_reason_is_counted_apart_from_a_plain_denial()
    {
        using var broker = Broker();
        var session = AgentSessionId.New();
        broker.RegisterSession(session, Copy);

        await AskAndAnswer(broker, session, "Bash", new() { ["command"] = "a" }, TimeSpan.Zero, approved: false);
        await AskAndAnswer(broker, session, "Bash", new() { ["command"] = "b" }, TimeSpan.Zero, approved: false, reason: "   ");
        await AskAndAnswer(broker, session, "Bash", new() { ["command"] = "c" }, TimeSpan.Zero, approved: false, reason: "not that one");

        var counted = _attention.For(session);
        counted.Count(AttentionOutcome.Denied).Should().Be(2, "only whitespace is no reason at all");
        counted.Count(AttentionOutcome.DeniedWithReason).Should().Be(1);
    }

    [Fact]
    public async Task A_refusal_without_asking_is_counted_apart_from_an_answer_and_from_an_auto_allow()
    {
        // The work gate refuses a change from a session with no work item, and nobody is asked.
        var work = Substitute.For<ISessionWorkItems>();
        work.HasWork(Arg.Any<AgentSessionId>()).Returns(false);
        using var broker = Broker(work: work);
        var session = AgentSessionId.New();
        broker.RegisterSession(session, Copy);

        (await broker.RequestAsync(session, "Write", "t1", Writing(Copy + "/a.md"))).Approved.Should().BeFalse();
        (await broker.RequestAsync(session, "Read", "t2", new Dictionary<string, string> { ["file_path"] = Copy + "/a.md" }))
            .Approved.Should().BeTrue();
        broker.Refuse(session, "Bash", string.Empty, "The hook's payload could not be read.");

        var counted = _attention.For(session);
        counted.Refused.Should().Be(2);
        counted.Auto.Should().Be(1);
        counted.Asked.Should().Be(0, "nobody was asked about any of them");
        counted.Waited.Should().Be(TimeSpan.Zero);
        counted.Line.Should().Be("0 approvals asked; 1 auto, 2 refused without asking");
    }

    [Fact]
    public async Task A_card_nobody_answered_in_time_is_a_timeout_and_one_the_session_ended_on_is_withdrawn()
    {
        var session = AgentSessionId.New();
        using (var broker = Broker(TimeSpan.FromMilliseconds(60)))
        {
            broker.RegisterSession(session, Copy);
            (await broker.RequestAsync(session, "Write", "t1", Writing(Copy + "/a.md"))).Approved.Should().BeFalse();
        }

        using (var broker = Broker())
        {
            broker.RegisterSession(session, Copy);
            var pending = broker.RequestAsync(session, "Bash", "t2", new Dictionary<string, string> { ["command"] = "x" });
            broker.ReleaseSession(session);
            (await pending).Approved.Should().BeFalse();
        }

        var counted = _attention.For(session);
        counted.Count(AttentionOutcome.TimedOut).Should().Be(1);
        counted.Count(AttentionOutcome.Withdrawn).Should().Be(1);
        counted.Asked.Should().Be(2, "both were put in front of a person, whether or not anyone answered");
    }

    [Fact]
    public async Task A_trusted_edit_is_counted_as_auto_and_never_as_an_answer()
    {
        var trust = Substitute.For<IWorkspaceTrust>();
        trust.For("ws").Returns(WorkspaceTrust.Edits);
        // A REAL folder as the copy, not the Linux-shaped Copy constant: a trusted edit has to be inside the copy under
        // the kernel's reading too, and on Windows "/workspaces/w1" is placed nowhere, so the edit asked and, with no
        // timeout, the test waited for an answer forever (found 2026-10-12, a Windows run of this class never ended).
        // The short timeout turns any such regression into a failure instead of a hang.
        using var broker = new AgentApprovalBroker(
            TimeSpan.FromSeconds(10), clock: () => _now, trust: trust, attention: _attention);
        var session = AgentSessionId.New();
        broker.RegisterSession(session, _directory, workspaceId: "ws");

        (await broker.RequestAsync(session, "Write", "t1", Writing(Path.Combine(_directory, "a.md"))))
            .Approved.Should().BeTrue();

        var counted = _attention.For(session);
        counted.Auto.Should().Be(1);
        counted.Asked.Should().Be(0);
    }

    // ---- commit reviews --------------------------------------------------------------------------------

    [Theory]
    [InlineData(CommitReviewOutcome.Passed, "1 review: pass")]
    [InlineData(CommitReviewOutcome.Failed, "1 review: fail")]
    [InlineData(CommitReviewOutcome.Off, "1 review: not reviewed")]
    public async Task A_reviews_verdict_is_counted_on_the_building_conversation(CommitReviewOutcome outcome, string said)
    {
        var role = new CommitReviewerRole(true, AgentTool.Claude, "m");
        var reviewer = Substitute.For<ICommitReviewer>();
        reviewer.Role.Returns(role);
        reviewer.ReviewAsync(Arg.Any<AgentSessionId>(), Arg.Any<CommitReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommitReview(outcome, role, Summary: "s")));
        using var broker = Broker(reviewer: reviewer);
        var session = AgentSessionId.New();
        broker.RegisterSession(session, Copy);
        var commit = new Dictionary<string, string> { ["message"] = "fix: a thing" };

        if (outcome == CommitReviewOutcome.Failed)
        {
            (await broker.RequestAsync(session, AgentReviewTools.Commit, "t1", commit)).Approved.Should().BeFalse();
        }
        else
        {
            await AskAndAnswer(broker, session, AgentReviewTools.Commit, commit, TimeSpan.FromSeconds(5), approved: true);
        }

        var counted = _attention.For(session);
        counted.Count(outcome).Should().Be(1);
        counted.Line.Should().EndWith(said);
        if (outcome == CommitReviewOutcome.Failed)
        {
            counted.Refused.Should().Be(1, "a failed review refuses the commit and nobody is asked");
            counted.Asked.Should().Be(0);
        }
        else
        {
            counted.AskedByKind.Should().ContainKey(AttentionToolKind.Commit);
        }
    }

    // ---- guidance turns --------------------------------------------------------------------------------

    [Fact]
    public async Task A_persons_follow_up_turn_is_guidance_and_the_opening_prompt_is_not()
    {
        var store = new SqliteSessionStore(_factory);
        using var broker = Broker();
        var runner = new ScriptedRunner();
        using var registry = new LiveSessionRegistry(runner, store, broker, attention: _attention);

        var session = registry.Start(new SessionStartRequest("w1", Copy, "write the readme"));
        _attention.For(session.Id).GuidanceTurns.Should().Be(0, "the opening prompt is not guidance");

        (await registry.SendAsync(session.Id, "also add a licence")).Should().BeTrue();
        (await registry.SendAsync(session.Id, "and keep it short")).Should().BeTrue();

        var counted = _attention.For(session.Id);
        counted.GuidanceTurns.Should().Be(2);
        counted.Line.Should().Contain("2 guidance turns");
        runner.Finish();
    }

    [Fact]
    public void Deleting_a_conversation_forgets_its_counts()
    {
        var session = AgentSessionId.New();
        _attention.Record(AttentionEntry.Turn(session, _now));
        _attention.Record(AttentionEntry.Turn(session, _now));
        _attention.For(session).GuidanceTurns.Should().Be(1);

        _attention.Forget(session);

        _attention.For(session).Should().BeEquivalentTo(SessionAttention.Empty);
    }

    // ---- the three reads agree, and agree with the transcript ------------------------------------------

    [Fact]
    public async Task The_pane_the_item_page_and_the_tool_read_the_same_counts_and_they_agree_with_the_transcript()
    {
        var store = new SqliteSessionStore(_factory);
        using var broker = Broker(TimeSpan.FromSeconds(30));
        var runner = new ScriptedRunner();
        using var registry = new LiveSessionRegistry(runner, store, broker, attention: _attention);
        var session = registry.Start(new SessionStartRequest("w1", Copy, "build it"));
        var secret = broker.RegisterSession(session.Id, Copy);

        await AskAndAnswer(broker, session.Id, "Write", Writing(Copy + "/a.md"), TimeSpan.FromSeconds(12), approved: true);
        await AskAndAnswer(broker, session.Id, "Bash", new() { ["command"] = "x" }, TimeSpan.FromSeconds(3), approved: false, reason: "no");
        await AskAndAnswer(broker, session.Id, "Bash", new() { ["command"] = "y" }, TimeSpan.FromSeconds(5), approved: false);
        broker.Refuse(session.Id, "Write", string.Empty, "refused");
        await broker.RequestAsync(session.Id, "Read", "t", new Dictionary<string, string> { ["file_path"] = Copy + "/a.md" });
        (await registry.SendAsync(session.Id, "now the tests")).Should().BeTrue();

        // The work item the conversation is linked to.
        var trackers = new LocalTrackerResolver(_factory, TimeProvider.System);
        var item = (await trackers.For(ProjectId.Default).CreateAsync(
            new NewWorkItem { Level = WorkItemLevel.Task, Title = "Counted", Description = "Its conversations are counted." }, MutationContext.User)).Value;
        new SqliteSessionWorkItems(_factory, TimeProvider.System).Link(session.Id, item.Id, ProjectId.Default, "user");

        // 1. The pane's line.
        var pane = _attention.For(session.Id);

        // 2. The work item page's roll-up (one conversation, so its total is that conversation).
        var page = _attention.ForItem(item.Id);
        page.Sessions.Should().ContainSingle().Which.Session.Should().Be(session.Id);

        // 3. The tool, through the real tool server, for this session and for the item.
        var server = new StudioToolServer(
            broker,
            [new SessionAttentionTool(trackers, new WorkItemChanges(), _attention)],
            new FileToolCallLedger(_directory),
            "1.0.0");
        var mine = await CallAsync(server, secret, "{}");
        var rolled = await CallAsync(server, secret, $"{{\"number\":{item.Number}}}");

        pane.Line.Should().Be(
            "3 approvals asked: 1 approved, 1 denied with a reason, 1 denied; waited 20 s in all; 1 auto, 1 refused without asking; 1 guidance turn");
        page.Sessions[0].Attention.Line.Should().Be(pane.Line);
        page.Total.Line.Should().Be(pane.Line);
        mine.GetProperty("line").GetString().Should().Be(pane.Line);
        rolled.GetProperty("line").GetString().Should().Be(pane.Line);
        rolled.GetProperty("sessions")[0].GetProperty("line").GetString().Should().Be(pane.Line);
        mine.GetProperty("asked").GetInt32().Should().Be(3);
        mine.GetProperty("waitedSeconds").GetInt64().Should().Be(20);

        // And the transcript the studio already keeps says the same about everything it recorded: the cards asked and
        // how each was answered, the refusal and the turns. Only the auto-allowed read is the count's alone.
        await Eventually(() => store.Transcript(session.Id).OfType<ToolApprovalResolved>().Count() == 3
            && store.Transcript(session.Id).OfType<ToolCallRefused>().Count() == 1);
        var transcript = store.Transcript(session.Id);
        transcript.OfType<ToolApprovalRequested>().Should().HaveCount(pane.Asked);
        var fromTranscript = SessionAttention.From(AttentionReconstruction.From(session.Id, transcript));
        fromTranscript.Asked.Should().Be(pane.Asked);
        foreach (var outcome in SessionAttention.AskedOutcomes)
        {
            fromTranscript.Count(outcome).Should().Be(pane.Count(outcome), outcome.ToString());
        }

        fromTranscript.AskedByKind.Should().BeEquivalentTo(pane.AskedByKind);
        fromTranscript.Refused.Should().Be(pane.Refused);
        fromTranscript.GuidanceTurns.Should().Be(pane.GuidanceTurns);
        fromTranscript.Auto.Should().Be(0, "the transcript never kept the reads the studio allowed without asking");
        runner.Finish();
    }

    [Fact]
    public void A_work_items_roll_up_adds_its_conversations_together()
    {
        var a = AgentSessionId.New();
        var b = AgentSessionId.New();
        _attention.Record(AttentionEntry.Decision(a, AttentionToolKind.Shell, AttentionOutcome.Approved, TimeSpan.FromSeconds(10), _now));
        _attention.Record(AttentionEntry.Turn(a, _now));
        _attention.Record(AttentionEntry.Turn(a, _now.AddMinutes(1)));
        _attention.Record(AttentionEntry.Decision(b, AttentionToolKind.Edit, AttentionOutcome.DeniedWithReason, TimeSpan.FromSeconds(50), _now));
        _attention.Record(AttentionEntry.ReviewOf(b, CommitReviewOutcome.Passed, _now));
        var item = new WorkItemId(Guid.NewGuid().ToString("N"));
        var links = new SqliteSessionWorkItems(_factory, TimeProvider.System);
        links.Link(a, item, ProjectId.Default, "user");
        links.Link(b, item, ProjectId.Default, "user");

        var rolled = _attention.ForItem(item);

        rolled.Sessions.Should().HaveCount(2);
        rolled.Total.Line.Should().Be(
            "2 approvals asked: 1 approved, 1 denied with a reason; waited 1 min in all; 1 guidance turn; 1 review: pass");
    }

    // ---- the schema step ------------------------------------------------------------------------------

    [Fact]
    public void The_schema_step_migrates_an_existing_database_and_reconstructs_what_its_transcripts_can_say()
    {
        // A database at schema 37, the version before this step, with one conversation's transcript in it.
        var factory = new SqliteConnectionFactory(
            new SqliteStoreOptions(Path.Combine(_directory, "v37.db"), Pooling: false));
        using (var connection = factory.Open())
        {
            SchemaMigrator.Run(connection, [.. SqliteSchema.Migrations.Where(m => m.Version <= 37)]);
            SchemaMigrator.StoredVersion(connection).Should().Be(37);
        }

        var session = AgentSessionId.New();
        var at = _now;
        var old = new SqliteSessionStore(factory);
        old.Save(new SessionRecord(session, "w1", AgentTool.Claude, "old", null, AgentSessionState.Completed, at, at));
        old.Append(session,
        [
            new MessageEmitted(at, TranscriptRole.User, "build it"),
            new ToolApprovalRequested(at, "r1", "Write", "/workspaces/w1/a.md"),
            new ToolApprovalResolved(at.AddSeconds(40), "r1", "Write", true, ToolApprovalDecision.UserApprovedReason, ToolApprovalDecidedBy.User),
            new ToolApprovalRequested(at.AddMinutes(1), "r2", "Bash", "x"),
            new ToolApprovalResolved(
                at.AddMinutes(1).AddSeconds(20), "r2", "Bash", false,
                ToolApprovalDecision.UserReason(false, "wrong folder"), ToolApprovalDecidedBy.User),
            new ToolApprovalRequested(at.AddMinutes(2), "r3", "Edit", "x"),
            new ToolApprovalResolved(
                at.AddHours(4).AddMinutes(2), "r3", "Edit", false,
                "The studio saw no answer within 4 hour(s), so the call was denied."),
            new ToolCallRefused(at.AddMinutes(3), "Bash", "cat a", "Use Read."),
            new ToolCallTrusted(at.AddMinutes(4), "Edit", "a.md", AgentApprovalBroker.TrustedEditReason),
            new MessageEmitted(at.AddMinutes(5), TranscriptRole.User, "now the tests"),
            new MessageEmitted(at.AddMinutes(5), TranscriptRole.StudioPrompt, "a studio prompt is no person's turn"),
            new MessageEmitted(at.AddMinutes(6), TranscriptRole.System, new CommitReview(
                CommitReviewOutcome.Failed, new CommitReviewerRole(true, AgentTool.Claude, "m")).TranscriptNote),
        ]);

        SqliteSchema.EnsureCreated(factory);

        using (var connection = factory.Open())
        {
            SchemaMigrator.StoredVersion(connection).Should().Be(SqliteSchema.CurrentVersion);
        }

        var counted = new SqliteSessionAttention(factory).For(session);
        counted.Reconstructed.Should().BeTrue();
        counted.Asked.Should().Be(3);
        counted.Count(AttentionOutcome.Approved).Should().Be(1);
        counted.Count(AttentionOutcome.DeniedWithReason).Should().Be(1);
        counted.Count(AttentionOutcome.TimedOut).Should().Be(1);
        counted.Refused.Should().Be(1);
        counted.Auto.Should().Be(1, "the trusted edit is in the transcript; the reads never were");
        counted.GuidanceTurns.Should().Be(1);
        counted.Count(CommitReviewOutcome.Failed).Should().Be(1);
        counted.Waited.Should().Be(TimeSpan.FromHours(4) + TimeSpan.FromSeconds(60));

        // A replay of the step adds nothing twice.
        using (var connection = factory.Open())
        {
            using var transaction = connection.BeginTransaction();
            SqliteSchema.Migrations.Single(m => m.Version == 38).Apply(connection, transaction);
            transaction.Commit();
        }

        new SqliteSessionAttention(factory).For(session).Asked.Should().Be(3);
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static async Task<JsonElement> CallAsync(StudioToolServer server, string secret, string arguments)
    {
        var body =
            $"{{\"method\":\"tools/call\",\"params\":{{\"name\":\"{SessionAttentionTool.ToolName}\",\"arguments\":{arguments}}},\"jsonrpc\":\"2.0\",\"id\":2}}";
        var response = await server.HandleAsync(new StudioToolHttpRequest("POST", secret, "2025-11-25", null, body));
        response.StatusCode.Should().Be(200);
        using var json = JsonDocument.Parse(response.Json!);
        var result = json.RootElement.GetProperty("result");
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        result.GetProperty("isError").GetBoolean().Should().BeFalse(text);
        return JsonDocument.Parse(text).RootElement.GetProperty("data").Clone();
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !condition())
        {
            await Task.Delay(10);
        }

        condition().Should().BeTrue();
    }

    /// <summary>A runner that only takes turns: nothing in these tests needs it to say anything back.</summary>
    private sealed class ScriptedRunner : IAgentSessionRunner
    {
        private readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>();

        public IAsyncEnumerable<AgentEvent> RunAsync(AgentSessionRequest request, CancellationToken cancellationToken = default) =>
            _events.Reader.ReadAllAsync(cancellationToken);

        public Task<bool> SendAsync(AgentSessionId sessionId, string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> SendAsync(
            AgentSessionId sessionId, string text, IReadOnlyList<AgentImageAttachment> images, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public bool Stop(AgentSessionId sessionId)
        {
            Finish();
            return true;
        }

        public bool ChangeModel(AgentSessionId sessionId, string? model) => true;

        public void Finish()
        {
            _events.Writer.TryWrite(new SessionEnded(DateTimeOffset.UtcNow, AgentSessionState.Completed, null));
            _events.Writer.TryComplete();
        }
    }
}
