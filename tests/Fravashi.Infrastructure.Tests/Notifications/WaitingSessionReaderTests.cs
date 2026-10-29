using AwesomeAssertions;
using Fravashi.Application.Notifications;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Notifications;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Notifications;

/// <summary>
/// Backlog 304: whether one session is waiting on a person, and on what, read the same way
/// <c>LiveSessionsCard.Describe</c>/<c>OpenQuestions</c> already decide it for the home page's live-sessions
/// card, but as a pure function over a small snapshot so it needs no real <c>LiveSessionRegistry</c>.
/// </summary>
public sealed class WaitingSessionReaderTests
{
    private static readonly AgentSessionId SessionId = new(Guid.NewGuid());

    private const string QuestionsDocument =
        "{\"version\":\"1\",\"kind\":\"questions\",\"message\":\"Which do you prefer?\","
        + "\"questions\":[{\"id\":\"q1\",\"question\":\"Tabs or spaces?\",\"options\":["
        + "{\"label\":\"Tabs\"},{\"label\":\"Spaces\"}]},"
        + "{\"id\":\"q2\",\"question\":\"Light or dark?\",\"options\":["
        + "{\"label\":\"Light\"},{\"label\":\"Dark\"}]}]}";

    [Fact]
    public void No_pending_approval_and_no_open_question_reads_as_not_waiting()
    {
        var snapshot = new WaitingSessionSnapshot(SessionId, "ws-1", TurnInFlight: false, Pending: [], Transcript: []);

        WaitingSessionReader.Read(snapshot).Should().BeNull();
    }

    [Fact]
    public void One_pending_approval_names_the_tool_and_how_many_more()
    {
        var now = DateTimeOffset.UtcNow;
        var pending = new[]
        {
            Approval("Bash", now.AddSeconds(-5)),
            Approval("Edit", now),
        };
        var snapshot = new WaitingSessionSnapshot(SessionId, "ws-1", TurnInFlight: false, Pending: pending, Transcript: []);

        var entry = WaitingSessionReader.Read(snapshot);

        entry.Should().NotBeNull();
        entry!.Kind.Should().Be(WaitingForPersonKind.Approval);
        entry.What.Should().Be("Waiting on your approval: Bash and 1 more");
        entry.SinceUtc.Should().Be(now.AddSeconds(-5), "the earliest pending approval is when the wait began");
        entry.SessionId.Should().Be(SessionId);
        entry.WorkspaceId.Should().Be("ws-1");
    }

    [Fact]
    public void Several_open_questions_name_the_first_and_how_many_more()
    {
        var document = AgentTurnDocumentReader.Read(QuestionsDocument)!;
        var turnAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var transcript = new AgentEvent[] { new TurnCompleted(turnAt, null, false, null, document) };
        var snapshot = new WaitingSessionSnapshot(SessionId, "ws-1", TurnInFlight: false, Pending: [], Transcript: transcript);

        var entry = WaitingSessionReader.Read(snapshot);

        entry.Should().NotBeNull();
        entry!.Kind.Should().Be(WaitingForPersonKind.Question);
        entry.What.Should().Be("Waiting on your answer: Tabs or spaces? (and 1 more)");
        entry.SinceUtc.Should().Be(turnAt);
    }

    [Fact]
    public void TurnInFlight_suppresses_a_question_even_with_one_unanswered_on_the_transcript()
    {
        var document = AgentTurnDocumentReader.Read(QuestionsDocument)!;
        var transcript = new AgentEvent[] { new TurnCompleted(DateTimeOffset.UtcNow, null, false, null, document) };
        var snapshot = new WaitingSessionSnapshot(SessionId, "ws-1", TurnInFlight: true, Pending: [], Transcript: transcript);

        WaitingSessionReader.Read(snapshot).Should().BeNull();
    }

    [Fact]
    public void An_answered_question_is_never_reported()
    {
        var document = AgentTurnDocumentReader.Read(QuestionsDocument)!;
        var transcript = new AgentEvent[]
        {
            new TurnCompleted(DateTimeOffset.UtcNow.AddMinutes(-2), null, false, null, document),
            new MessageEmitted(DateTimeOffset.UtcNow.AddMinutes(-1), TranscriptRole.User, "Tabs. Dark."),
        };
        var snapshot = new WaitingSessionSnapshot(SessionId, "ws-1", TurnInFlight: false, Pending: [], Transcript: transcript);

        WaitingSessionReader.Read(snapshot).Should().BeNull();
    }

    [Fact]
    public void A_pending_approval_wins_over_an_open_question()
    {
        var document = AgentTurnDocumentReader.Read(QuestionsDocument)!;
        var transcript = new AgentEvent[] { new TurnCompleted(DateTimeOffset.UtcNow, null, false, null, document) };
        var pending = new[] { Approval("Bash", DateTimeOffset.UtcNow) };
        var snapshot = new WaitingSessionSnapshot(SessionId, "ws-1", TurnInFlight: false, Pending: pending, Transcript: transcript);

        WaitingSessionReader.Read(snapshot)!.Kind.Should().Be(WaitingForPersonKind.Approval);
    }

    /// <summary>Work item #356: a studio tool waits in words, never by its raw mcp__ name.</summary>
    [Fact]
    public void A_pending_studio_tool_call_is_named_in_words()
    {
        var pending = new PendingToolApproval(
            "r1", SessionId, "mcp__fravashi__work_item_update", "#23", null,
            new Dictionary<string, string> { ["number"] = "23", ["description"] = "x" }, DateTimeOffset.UtcNow);
        var snapshot = new WaitingSessionSnapshot(SessionId, "ws-1", TurnInFlight: false, Pending: [pending], Transcript: []);

        var entry = WaitingSessionReader.Read(snapshot);

        entry!.What.Should().Be("Waiting on your approval: Update #23: description");
        entry.What.Should().NotContain("mcp__");
    }

    private static PendingToolApproval Approval(string toolName, DateTimeOffset requestedUtc) =>
        new(Guid.NewGuid().ToString("N"), SessionId, toolName, Detail: toolName, ToolUseId: null,
            Input: new Dictionary<string, string>(), RequestedUtc: requestedUtc);
}
