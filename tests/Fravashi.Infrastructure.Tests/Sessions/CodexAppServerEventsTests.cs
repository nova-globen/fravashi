// RETIRED to v2. Decided, 2026-09-22: Codex and Copilot are not part of this version and support for
// them is a v2 feature. The decision, everything that was measured about these two CLIs, and the
// read-containment question it parks are all in docs/v2.md, section 1 ("Codex and Copilot runtimes").
//
// The whole file is disabled rather than deleted, behind a symbol nothing defines. It is kept because the
// protocol it speaks was read off the pinned binary's own generated schema and bundled code (docs/v2.md
// Appendix B), and because a live Codex turn proved part of it against a real signed-in CLI. Re-deriving
// either in v2 would cost far more than carrying inert text here.
//
// Before restoring any of it, read docs/v2.md section 1.2 (the uid question, which is what stands in front of
// offering these runtimes at all) and 1.5 (a real, unfixed defect in CodexSessionRunner).
#if V2_CODEX_COPILOT

using System.Text.Json;
using AwesomeAssertions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Sessions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Backlog 66. The endings and oddities of the Codex app-server stream that a happy-path runner test does not
/// reach. Every notification is CONSTRUCTED from the pinned 0.146.0's generated schema (<c>v2/Turn.ts</c>,
/// <c>v2/ErrorNotification.ts</c>, <c>v2/ThreadItem.ts</c>), not captured.
/// </summary>
public class CodexAppServerEventsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<AgentEvent> Map(CodexAppServerEvents events, string method, string json)
    {
        using var document = JsonDocument.Parse(json);
        return events.Map(method, document.RootElement.Clone());
    }

    private static CodexAppServerEvents Parser() => new(() => Now);

    [Theory]
    [InlineData("completed", false, AgentSessionState.Completed)]
    [InlineData("interrupted", false, AgentSessionState.Cancelled)]
    [InlineData("failed", true, AgentSessionState.Failed)]
    public void A_turn_ending_keeps_the_cli_status_and_maps_to_the_right_state(string status, bool isError, AgentSessionState state)
    {
        var error = status == "failed"
            ? "{\"message\":\"usage limit reached\",\"codexErrorInfo\":null,\"additionalDetails\":null}"
            : "null";
        var events = Map(
            Parser(),
            "turn/completed",
            "{\"threadId\":\"t\",\"turn\":{\"id\":\"turn-1\",\"items\":[],\"itemsView\":\"full\",\"status\":\""
            + status + "\",\"error\":" + error + ",\"startedAt\":1,\"completedAt\":2,\"durationMs\":1}}");

        var turn = events.OfType<TurnCompleted>().Single();
        turn.IsError.Should().Be(isError);
        turn.TerminalReason.Should().Be(status);
        AgentTerminalReason.ToSessionState(turn.TerminalReason, turn.IsError).Should().Be(state);
        if (isError)
        {
            turn.ErrorText.Should().Be("usage limit reached");
        }
    }

    [Fact]
    public void An_error_the_cli_will_retry_is_said_as_a_retry_and_counted()
    {
        var parser = Parser();

        var events = Map(parser, "error", """
            {"error":{"message":"stream disconnected","codexErrorInfo":null,"additionalDetails":null},
             "willRetry":true,"threadId":"t","turnId":"turn-1"}
            """);

        events.OfType<MessageEmitted>().Single().Text.Should().Be("Codex hit an error and is retrying: stream disconnected");
        Map(parser, "turn/completed", """{"threadId":"t","turn":{"id":"turn-1","status":"completed","error":null}}""")
            .OfType<StatsUpdated>().Single().Stats.RateLimitHits.Should().Be(1);
    }

    [Fact]
    public void A_file_change_remembers_its_paths_until_it_completes()
    {
        var parser = Parser();

        Map(parser, "item/started", """
            {"threadId":"t","turnId":"turn-1","startedAtMs":1,"item":{"type":"fileChange","id":"fc_9","status":"inProgress",
             "changes":[{"path":"a.txt","kind":{"type":"update","move_path":"b.txt"},"diff":"-x\n+y\n"}]}}
            """).OfType<ToolCallStarted>().Single().Detail.Should().Be("update a.txt -> b.txt");

        parser.FileChangesFor("fc_9").Should().ContainSingle().Which.MovePath.Should().Be("b.txt");

        Map(parser, "item/completed", """
            {"threadId":"t","turnId":"turn-1","completedAtMs":2,"item":{"type":"fileChange","id":"fc_9","status":"completed",
             "changes":[{"path":"a.txt","kind":{"type":"update","move_path":"b.txt"},"diff":"-x\n+y\n"}]}}
            """).OfType<ToolCallCompleted>().Single().Success.Should().BeTrue();

        parser.FileChangesFor("fc_9").Should().BeNull("an approval for a finished item must find nothing to approve");
    }

    [Fact]
    public void A_command_that_exited_non_zero_is_not_a_success()
    {
        var parser = Parser();

        var events = Map(parser, "item/completed", """
            {"threadId":"t","turnId":"turn-1","completedAtMs":2,"item":{"type":"commandExecution","id":"cmd_2",
             "command":"false","cwd":"/w","processId":null,"source":"agent","status":"completed","commandActions":[],
             "aggregatedOutput":"","exitCode":1,"durationMs":3}}
            """);

        events.OfType<ToolCallStarted>().Should().ContainSingle("a completion with no start we saw still opens its card");
        events.OfType<ToolCallCompleted>().Single().Success.Should().BeFalse();
    }

    [Theory]
    [InlineData("item/started", """{"item":"not an object"}""")]
    [InlineData("item/completed", """{"item":{"type":"commandExecution"}}""")]
    [InlineData("thread/tokenUsage/updated", """{"tokenUsage":null}""")]
    [InlineData("turn/started", """[]""")]
    [InlineData("some/futureNotification", """{"anything":1}""")]
    public void A_malformed_or_unknown_notification_produces_nothing_and_throws_nothing(string method, string json)
    {
        var events = Map(Parser(), method, json);

        events.Where(e => e is not TurnActivity).Should().BeEmpty();
    }

    [Fact]
    public void Reasoning_and_user_echoes_are_not_tool_cards()
    {
        var parser = Parser();

        Map(parser, "item/started", """{"item":{"type":"reasoning","id":"rs","summary":[],"content":[]}}""").Should().BeEmpty();
        Map(parser, "item/completed", """{"item":{"type":"userMessage","id":"u","clientId":null,"content":[]}}""").Should().BeEmpty();
        Map(parser, "item/completed", """{"item":{"type":"agentMessage","id":"m","text":"","phase":null,"memoryCitation":null}}""")
            .Should().BeEmpty("an empty message is not a bubble");
    }
}
#endif
