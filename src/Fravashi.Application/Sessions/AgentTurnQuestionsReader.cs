using System.Text;
using System.Text.Json;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Application.Sessions;

/// <summary>
/// Turns a <see cref="AgentTurnDocumentKind.Questions"/> document's payload into questions the studio can
/// draw controls for, and turns the user's picks back into the text of the next user turn (backlog
/// item 92).
/// <para>
/// <b>It never throws and it never renders half a question.</b> The schema is permissive on purpose
/// (spec decision 2), so what arrives is what the model wrote rather than what a strict validator would
/// have forced: an option with no label, an entry with no options at all, an entry that is not even an
/// object. Every one of those is DROPPED rather than rendered as an empty button, because a control that
/// cannot be answered is worse than a question the studio admits it could not read. If nothing survives,
/// the caller renders the document's <c>message</c>, which is always present, and the user answers in
/// prose.
/// </para>
/// </summary>
public static class AgentTurnQuestionsReader
{
    /// <summary>
    /// The questions in a document, or an empty list when it carries none this build can render.
    /// <para>
    /// A degraded document (an unknown version, an unknown kind, a payload that is not the shape the kind
    /// declares) yields nothing on purpose: <see cref="AgentTurnDocument.IsTyped"/> is the whole test, and
    /// its message is what the caller shows instead.
    /// </para>
    /// </summary>
    public static IReadOnlyList<AgentTurnQuestion> Read(AgentTurnDocument? document)
    {
        if (document is not { KnownKind: AgentTurnDocumentKind.Questions, PayloadJson: { } payload }
            || !document.IsTyped)
        {
            return [];
        }

        try
        {
            using var parsed = JsonDocument.Parse(payload);
            if (parsed.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var questions = new List<AgentTurnQuestion>();
            foreach (var element in parsed.RootElement.EnumerateArray())
            {
                if (ReadQuestion(element) is { } question)
                {
                    questions.Add(question);
                }
            }

            return questions;
        }
        catch (JsonException)
        {
            // Unreachable for a payload that got here through AgentTurnDocumentReader, which already
            // parsed it. Reachable for a caller that hands over raw text, and not worth a crash.
            return [];
        }
    }

    /// <summary>
    /// True when a user turn was recorded after the transcript entry at <paramref name="index"/>, which is
    /// what "this question has been answered" means: the answer IS an ordinary user turn (ADR 0022,
    /// decision 4), whether it was clicked or typed.
    /// <para>
    /// Derived from the persisted transcript rather than held as UI state, and that is the point: a
    /// question stays open across a page reload, a circuit reconnect and a studio restart, because the
    /// only record of it having been answered is a row that outlives all three.
    /// </para>
    /// </summary>
    public static bool WasAnswered(IReadOnlyList<AgentEvent> transcript, int index)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        for (var i = index + 1; i < transcript.Count; i++)
        {
            if (transcript[i] is MessageEmitted { Role: TranscriptRole.User })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The text of the user turn a set of picks becomes. Each question is restated with its answer, so
    /// the turn is unambiguous when several were asked at once and reads as a sentence the user could
    /// have typed, which is exactly what it is once it is in the transcript.
    /// </summary>
    public static string Compose(IEnumerable<AgentTurnQuestionAnswer> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);

        var lines = new StringBuilder();
        foreach (var answer in answers)
        {
            var labels = answer.Labels.Where(label => !string.IsNullOrWhiteSpace(label)).ToArray();
            if (labels.Length == 0)
            {
                continue;
            }

            if (lines.Length > 0)
            {
                lines.Append('\n');
            }

            lines.Append(answer.Question.Question.Trim()).Append(' ').AppendJoin(", ", labels);
        }

        return lines.ToString();
    }

    private static AgentTurnQuestion? ReadQuestion(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (Text(element, "question") is not { Length: > 0 } question)
        {
            return null;
        }

        if (!element.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var choices = new List<AgentTurnQuestionOption>();
        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind == JsonValueKind.Object && Text(option, "label") is { Length: > 0 } label)
            {
                choices.Add(new AgentTurnQuestionOption(label, Text(option, "description")));
            }
        }

        if (choices.Count == 0)
        {
            return null;
        }

        return new AgentTurnQuestion(
            Text(element, "id") ?? string.Empty,
            question,
            choices,
            Text(element, "header"),
            element.TryGetProperty("multiSelect", out var multi) && multi.ValueKind == JsonValueKind.True);
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
