using AwesomeAssertions;
using Fravashi.Application.Network;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Sessions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Backlog 229: which failed turns, and which process endings, are the network's, read off the pinned CLI's own
/// recorded output (Sessions/Fixtures/Network, 2.1.280, 2026-10-07) and off the recordings of the other two error
/// endings, which must never be mistaken for it.
/// </summary>
public sealed class NetworkTurnFailureTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static List<AgentEvent> Replay(params string[] path)
    {
        var parser = new ClaudeStreamJsonParser(() => At);
        var file = Path.Combine([AppContext.BaseDirectory, "Sessions", "Fixtures", .. path]);
        List<AgentEvent> events = [.. File.ReadAllLines(file).SelectMany(parser.Parse)];
        events.AddRange(parser.Drain());
        return events;
    }

    /// <summary>The verdict the registry reaches for a recording's last turn, the way the registry reaches it.</summary>
    private static NetworkFailure Verdict(IReadOnlyList<AgentEvent> events)
    {
        string? cliApiError = null;
        var verdict = NetworkFailure.None;
        foreach (var e in events)
        {
            switch (e)
            {
                case ApiErrorReported reported:
                    cliApiError = reported.Text;
                    break;
                case TurnCompleted turn:
                    verdict = NetworkTurnFailure.Of(turn, cliApiError);
                    cliApiError = null;
                    break;
            }
        }

        return verdict;
    }

    [Theory]
    [InlineData("claude-api-error-name-not-resolved.jsonl", NetworkFailureKind.NameNotResolved)]
    [InlineData("claude-api-error-connection-refused.jsonl", NetworkFailureKind.Refused)]
    public void A_turn_the_network_failed_is_recognised_from_the_recording(string file, NetworkFailureKind kind)
    {
        var events = Replay("Network", file);

        var turn = events.OfType<TurnCompleted>().Single();
        turn.IsError.Should().BeTrue();
        turn.TerminalReason.Should().Be("api_error");
        turn.ResultText.Should().BeNull("the result repeats the synthetic assistant message, which carries the words");
        turn.ErrorText.Should().BeNull("the measured result line has no errors array");
        var message = events.OfType<MessageEmitted>().Single(m => m.Role == TranscriptRole.Assistant).Text;
        message.Should().StartWith("API Error: ");
        events.OfType<ApiErrorReported>().Single().Text
            .Should().Be(message, "the CLI marked that message as its own, which is what lets it decide");

        var verdict = Verdict(events);
        verdict.Kind.Should().Be(kind);
        verdict.NeverLeft.Should().BeTrue("a lookup and a refused connect both fail before anything is written");
    }

    [Fact]
    public void A_black_holed_host_is_a_network_failure_that_may_have_left()
    {
        // Recorded against 10.255.255.1: ten retries over 27.7 minutes, each a hung connect, then "Request timed out".
        // The text names no phase, so slice A's rule reads it as left with no answer; the studio never resends it.
        var events = Replay("Network", "claude-api-error-request-timed-out.jsonl");

        events.OfType<ApiRetrying>().Should().HaveCount(10);
        events.OfType<ApiErrorReported>().Single().Text.Should().Be("Request timed out");
        var verdict = Verdict(events);
        verdict.Kind.Should().Be(NetworkFailureKind.LeftWithNoAnswer);
        verdict.NeverLeft.Should().BeFalse();
    }

    [Fact]
    public void The_clis_own_retries_come_through_as_progress()
    {
        var retries = Replay("Network", "claude-api-error-name-not-resolved.jsonl").OfType<ApiRetrying>().ToList();

        retries.Should().HaveCount(10);
        retries.Select(r => r.Attempt).Should().Equal(Enumerable.Range(1, 10));
        retries.Should().OnlyContain(r => r.MaxRetries == 10);
        retries[0].Delay.Should().Be(TimeSpan.FromMilliseconds(554));
    }

    [Theory]
    [InlineData("claude-typed-budget-exhausted.jsonl")]
    [InlineData("claude-typed-retries-exhausted.jsonl")]
    [InlineData("claude-multi-turn.jsonl")]
    [InlineData("claude-typed-turn.jsonl")]
    public void A_recorded_ending_that_is_not_the_network_is_not_called_one(string file) =>
        Verdict(Replay(file)).IsNetwork.Should().BeFalse();

    [Theory]
    [InlineData("API Error: 401 {\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key\"}}")]
    [InlineData("API Error: 400 {\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"prompt is too long\"}}")]
    [InlineData("API Error: 403 {\"error\":\"not_allowlisted\"}")]
    [InlineData("API Error: Repeated 529 Overloaded errors")]
    [InlineData("Claude Code is unable to respond to this request, which appears to violate our Usage Policy")]
    public void A_refusal_a_bad_request_or_an_overload_is_not_the_network(string said)
    {
        var turn = new TurnCompleted(At, said, true, "api_error");

        NetworkTurnFailure.Of(turn, said).IsNetwork.Should().BeFalse();
    }

    [Fact]
    public void The_agents_own_words_never_decide_even_when_they_name_a_network_error()
    {
        // A turn that failed for another reason (here the turn limit) after the agent wrote about somebody's network
        // code. Its result string is the agent's last words; only the CLI's own error message or errors may decide.
        var prose = "Your client fails with connect ECONNREFUSED 10.0.0.5:5432 and then a socket hang up.";
        var turn = new TurnCompleted(At, prose, true, "error_max_turns");

        NetworkTurnFailure.Of(turn, cliApiError: null).IsNetwork.Should().BeFalse();
        NetworkTurnFailure.Of(turn, cliApiError: "API Error: 401 {\"type\":\"error\"}").IsNetwork.Should().BeFalse();
    }

    [Fact]
    public void Only_a_message_the_cli_marked_as_its_own_is_reported_as_its_api_error()
    {
        var parser = new ClaudeStreamJsonParser(() => At);
        const string Prose = "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\","
            + "\"text\":\"API Error: Can't reach the API server (ENOTFOUND)\"}]}}";
        const string Marked = "{\"type\":\"assistant\",\"is_api_error_message\":true,\"message\":{\"model\":\"<synthetic>\","
            + "\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"API Error: Can't reach the API server (ENOTFOUND)\"}]}}";

        parser.Parse(Prose).OfType<ApiErrorReported>().Should().BeEmpty("the agent may write those very words");
        parser.Parse(Marked).OfType<ApiErrorReported>().Should().ContainSingle();
    }

    [Fact]
    public void A_budget_or_validation_ending_is_never_the_network_whatever_its_words()
    {
        NetworkTurnFailure.Of(new TurnCompleted(At, null, true, AgentTurnEnding.BudgetExhausted, ErrorText: "ECONNRESET"), null)
            .IsNetwork.Should().BeFalse();
        NetworkTurnFailure.Of(new TurnCompleted(At, null, true, AgentTurnEnding.RetriesExhausted, ErrorText: "ECONNRESET"), null)
            .IsNetwork.Should().BeFalse();
    }

    [Fact]
    public void A_turn_that_succeeded_is_never_the_network()
    {
        // An agent that merely TALKS about a network error ended its turn fine.
        var said = "The build failed with getaddrinfo EAI_AGAIN nuget.org; I will retry.";
        NetworkTurnFailure.Of(new TurnCompleted(At, said, false, "completed"), said).IsNetwork.Should().BeFalse();
    }

    [Fact]
    public void A_reset_with_no_proof_of_the_phase_may_have_left()
    {
        var said = "API Error: Connection error. read ECONNRESET";

        var verdict = NetworkTurnFailure.Of(new TurnCompleted(At, said, true, "api_error"), said);

        verdict.Kind.Should().Be(NetworkFailureKind.LeftWithNoAnswer);
        verdict.NeverLeft.Should().BeFalse();
    }

    [Fact]
    public void An_exit_is_judged_by_its_own_words_then_by_the_turn_before_it()
    {
        var name = new NetworkFailure(NetworkFailureKind.NameNotResolved, "ENOTFOUND");

        NetworkTurnFailure.OfExit(new SessionEnded(At, AgentSessionState.Failed, "connect ECONNREFUSED 127.0.0.1:443"), null)
            .Kind.Should().Be(NetworkFailureKind.Refused);
        NetworkTurnFailure.OfExit(new SessionEnded(At, AgentSessionState.Failed, "The agent CLI exited with code 1."), name)
            .Should().Be(name);
        NetworkTurnFailure.OfExit(new SessionEnded(At, AgentSessionState.Failed, "The agent CLI exited with code 1."), null)
            .IsNetwork.Should().BeFalse();
    }

    [Fact]
    public void A_stop_a_clean_exit_or_an_ending_already_resumable_is_never_the_network()
    {
        var name = new NetworkFailure(NetworkFailureKind.NameNotResolved, "ENOTFOUND");

        NetworkTurnFailure.OfExit(new SessionEnded(At, AgentSessionState.Cancelled, null), name).IsNetwork.Should().BeFalse();
        NetworkTurnFailure.OfExit(new SessionEnded(At, AgentSessionState.Completed, null), name).IsNetwork.Should().BeFalse();
        NetworkTurnFailure.OfExit(new SessionEnded(At, AgentSessionState.Failed, ClaudeSessionRunner.ExhaustedEnding, Resumable: true), name)
            .IsNetwork.Should().BeFalse();
    }
}
