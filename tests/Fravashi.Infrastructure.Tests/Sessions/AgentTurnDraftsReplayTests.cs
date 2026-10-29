using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Sessions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Backlog 25: every captured CLI recording, replayed through the real parser, reads as ONE answer per
/// documented turn once <see cref="AgentTurnDrafts"/> has folded what the document replaced. Two of the
/// typed captures revise the prose rather than repeat it, so without the fold they show the answer twice.
/// </summary>
public class AgentTurnDraftsReplayTests
{
    public static TheoryData<string> Recordings()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Sessions", "Fixtures");
        TheoryData<string> data = [];
        foreach (var file in Directory.GetFiles(directory, "*.jsonl").Select(Path.GetFileName).Order(StringComparer.Ordinal))
        {
            data.Add(file!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Recordings))]
    public void Every_documented_turn_shows_its_documents_message_once_and_nothing_after_the_last_tool_but_it(string recording)
    {
        var parser = new ClaudeStreamJsonParser(() => DateTimeOffset.UnixEpoch);
        var path = Path.Combine(AppContext.BaseDirectory, "Sessions", "Fixtures", recording);
        List<AgentEvent> transcript = [.. File.ReadAllLines(path).SelectMany(parser.Parse)];
        var drafts = AgentTurnDrafts.Superseded(transcript);

        // What the reader sees open: across the whole turn, and after its last tool card.
        List<string> turn = [];
        List<string> stretch = [];
        for (var index = 0; index < transcript.Count; index++)
        {
            switch (transcript[index])
            {
                case MessageEmitted { Role: TranscriptRole.Assistant } message when !drafts.Contains(index):
                    turn.Add(message.Text);
                    stretch.Add(message.Text);
                    break;

                case MessageEmitted { Role: TranscriptRole.Assistant }:
                    break;

                case MessageEmitted:
                    turn.Clear();
                    stretch.Clear();
                    break;

                case ToolCallStarted or ToolCallCompleted:
                    stretch.Clear();
                    break;

                case TurnCompleted { Document.Message: { } answer } when !string.IsNullOrWhiteSpace(answer):
                    turn.Count(t => t.Trim() == answer.Trim()).Should().Be(
                        1, $"a documented turn in {recording} shows its answer exactly once");

                    // Either the answer closes the turn on its own, or (claude-typed-retry-with-tool-turn: the
                    // answer came first, then a Read, then an identical document) nothing follows the last tool.
                    stretch.Should().BeSubsetOf([answer], $"after the last tool in {recording} only the answer may remain");
                    turn.Clear();
                    stretch.Clear();
                    break;

                case TurnCompleted:
                    turn.Clear();
                    stretch.Clear();
                    break;
            }
        }

        // Only a documented turn's own stretch is ever folded: a recording with no document keeps every word open.
        if (!transcript.OfType<TurnCompleted>().Any(t => !string.IsNullOrWhiteSpace(t.Document?.Message)))
        {
            drafts.Should().BeEmpty();
        }
    }

    [Fact]
    public void The_revised_answer_capture_shows_the_answer_twice_without_the_fold_and_once_with_it()
    {
        var parser = new ClaudeStreamJsonParser(() => DateTimeOffset.UnixEpoch);
        var path = Path.Combine(AppContext.BaseDirectory, "Sessions", "Fixtures", "claude-typed-revised-answer-turn.jsonl");
        List<AgentEvent> transcript = [.. File.ReadAllLines(path).SelectMany(parser.Parse)];

        AgentTurnDrafts.Superseded(transcript).Should().NotBeEmpty(
            "this capture's document revises the prose after the tool, which is the double reading being fixed");
    }
}
