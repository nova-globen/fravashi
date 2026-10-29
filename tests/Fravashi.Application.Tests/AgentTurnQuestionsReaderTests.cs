using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Xunit;

namespace Fravashi.Application.Tests;

/// <summary>
/// Backlog item 92: the questions payload becomes controls. Everything here is about what arrives rather
/// than what the schema promises, because the schema is permissive on purpose (spec decision 2) and the
/// model has already been measured omitting its optional fields.
/// </summary>
public class AgentTurnQuestionsReaderTests
{
    /// <summary>
    /// A real document, captured from claude 2.1.222 in the container on 2026-08-06 given the assembled
    /// answer + questions schema and no other prompting. Note what the model left out: no <c>header</c>
    /// and no option <c>description</c>, both optional and both absent.
    /// </summary>
    private const string MeasuredQuestionsDocument =
        "{\"version\":\"1\",\"kind\":\"questions\",\"message\":\"Tell me about your coffee preferences!\","
        + "\"questions\":[{\"id\":\"coffee_preparation\",\"question\":\"How do you take your coffee?\","
        + "\"multiSelect\":false,\"options\":[{\"label\":\"Black, no additions\"},{\"label\":\"With milk\"}]}]}";

    /// <summary>
    /// A SECOND real capture, driven on 2026-08-07 through claude 2.1.223 in the <c>fravashi</c> container
    /// against <c>claude-haiku-4-5-20251001</c> with the assembled default schema (605 bytes, answer +
    /// questions) and the prompt "add response caching ... ask me where the cached responses should live,
    /// and give me options with the trade-offs". The <c>questions</c> array is verbatim; the envelope's
    /// long markdown <c>message</c> is elided to its first sentence, because only the payload is under
    /// test here and the prose is the assistant bubble's problem.
    /// <para>
    /// It is here because of what it shows, which the smaller fixture does not: the model asked THREE
    /// questions at once, and it put every trade-off in the prose rather than in an option
    /// <c>description</c>, so a renderer that assumes one question per turn or a description per option
    /// would look broken on the first real thing a user sees.
    /// </para>
    /// </summary>
    private const string MeasuredThreeQuestionDocument =
        "{\"version\":\"1\",\"kind\":\"questions\",\"message\":\"Before we add response caching, I need to "
        + "understand your setup and preferences.\",\"questions\":["
        + "{\"id\":\"tech_stack\",\"question\":\"What tech stack is your API built with?\",\"options\":["
        + "{\"label\":\"Node.js / Express\"},{\"label\":\"Python / Flask or FastAPI\"},"
        + "{\"label\":\"Python / Django\"},{\"label\":\"Go\"},{\"label\":\"Java / Spring\"},"
        + "{\"label\":\"C# / .NET\"},{\"label\":\"Ruby on Rails\"},{\"label\":\"Other\"}]},"
        + "{\"id\":\"scale\",\"question\":\"How many instances/replicas of your API run in production?\","
        + "\"options\":[{\"label\":\"Single server only\"},{\"label\":\"Multiple servers (2-10)\"},"
        + "{\"label\":\"Large scale (10+ replicas)\"},{\"label\":\"Not sure / still in development\"}]},"
        + "{\"id\":\"persistence\",\"question\":\"Should cached responses survive a server restart?\","
        + "\"options\":[{\"label\":\"Yes, persistence is important\"},"
        + "{\"label\":\"No, ephemeral cache is fine\"},{\"label\":\"Not sure yet\"}]}]}";

    private static AgentTurnDocument Document(string json) =>
        AgentTurnDocumentReader.Read(json) ?? throw new InvalidOperationException("the fixture must read as a document");

    [Fact]
    public void The_three_questions_the_cli_really_asked_all_read_back()
    {
        var questions = AgentTurnQuestionsReader.Read(Document(MeasuredThreeQuestionDocument));

        questions.Select(q => q.Id).Should().Equal("tech_stack", "scale", "persistence");
        questions.Should().AllSatisfy(q => q.MultiSelect.Should().BeFalse());
        questions[0].Options.Should().HaveCount(8);
        questions.SelectMany(q => q.Options).Should().AllSatisfy(
            option => option.Description.Should().BeNull(
                "the model put every trade-off in the prose, so the controls must look right without one"));
    }

    [Fact]
    public void The_answer_composed_from_that_capture_is_the_text_the_cli_understood()
    {
        // Not a formatting preference: this exact string was written back to the same CLI as the next
        // user turn on 2026-08-07, and the model answered it as a specific recommendation for a
        // single-server .NET API with an ephemeral cache. Change the format and that evidence is stale.
        var questions = AgentTurnQuestionsReader.Read(Document(MeasuredThreeQuestionDocument));

        var text = AgentTurnQuestionsReader.Compose(
        [
            new AgentTurnQuestionAnswer(questions[0], ["C# / .NET"]),
            new AgentTurnQuestionAnswer(questions[1], ["Single server only"]),
            new AgentTurnQuestionAnswer(questions[2], ["No, ephemeral cache is fine"]),
        ]);

        text.Should().Be(
            "What tech stack is your API built with? C# / .NET\n"
            + "How many instances/replicas of your API run in production? Single server only\n"
            + "Should cached responses survive a server restart? No, ephemeral cache is fine");
    }

    [Fact]
    public void A_measured_questions_document_reads_as_a_question_with_its_options()
    {
        var questions = AgentTurnQuestionsReader.Read(Document(MeasuredQuestionsDocument));

        questions.Should().HaveCount(1);
        questions[0].Id.Should().Be("coffee_preparation");
        questions[0].Question.Should().Be("How do you take your coffee?");
        questions[0].MultiSelect.Should().BeFalse();
        questions[0].Header.Should().BeNull("the model omitted it, and it is optional");
        questions[0].Options.Select(o => o.Label).Should().Equal("Black, no additions", "With milk");
        questions[0].Options.Select(o => o.Description).Should().AllSatisfy(d => d.Should().BeNull());
    }

    [Fact]
    public void An_option_description_is_read_when_the_agent_supplies_one()
    {
        var questions = AgentTurnQuestionsReader.Read(Document(
            "{\"version\":\"1\",\"kind\":\"questions\",\"message\":\"m\",\"questions\":[{\"id\":\"q\","
            + "\"header\":\"Storage\",\"question\":\"Where should cached responses live?\",\"options\":["
            + "{\"label\":\"In memory\",\"description\":\"Fastest, lost on restart\"}]}]}"));

        questions.Should().ContainSingle();
        questions[0].Header.Should().Be("Storage");
        questions[0].Options[0].Description.Should().Be("Fastest, lost on restart");
    }

    [Fact]
    public void A_multi_select_question_says_so()
    {
        var questions = AgentTurnQuestionsReader.Read(Document(
            "{\"version\":\"1\",\"kind\":\"questions\",\"message\":\"m\",\"questions\":[{\"id\":\"q\","
            + "\"question\":\"Which may I touch?\",\"multiSelect\":true,\"options\":[{\"label\":\"src\"},"
            + "{\"label\":\"tests\"}]}]}"));

        questions.Should().ContainSingle();
        questions[0].MultiSelect.Should().BeTrue();
    }

    [Fact]
    public void A_question_with_no_id_still_renders()
    {
        // The schema requires an id and the studio does not key its controls by one, so a model that
        // omits it has still asked something answerable. Being strict here would cost the user the
        // question for the sake of a handle nothing uses.
        var questions = AgentTurnQuestionsReader.Read(Document(
            "{\"version\":\"1\",\"kind\":\"questions\",\"message\":\"m\",\"questions\":[{\"question\":\"Which?\","
            + "\"options\":[{\"label\":\"A\"}]}]}"));

        questions.Should().ContainSingle();
        questions[0].Id.Should().BeEmpty();
    }

    [Theory]
    // No question text: nothing to render above the buttons.
    [InlineData("[{\"id\":\"q\",\"options\":[{\"label\":\"A\"}]}]")]
    // No options at all: a question with nothing to pick is prose, not a control.
    [InlineData("[{\"id\":\"q\",\"question\":\"Which?\"}]")]
    // Options present but none of them labelled: every button would be blank.
    [InlineData("[{\"id\":\"q\",\"question\":\"Which?\",\"options\":[{\"description\":\"d\"}]}]")]
    // Options is not an array.
    [InlineData("[{\"id\":\"q\",\"question\":\"Which?\",\"options\":\"A\"}]")]
    // The entry is not even an object.
    [InlineData("[\"Which?\"]")]
    public void A_question_that_cannot_be_answered_is_dropped_rather_than_drawn_empty(string payload)
    {
        var questions = AgentTurnQuestionsReader.Read(Document(
            "{\"version\":\"1\",\"kind\":\"questions\",\"message\":\"m\",\"questions\":" + payload + "}"));

        questions.Should().BeEmpty("a control the user cannot answer is worse than no control");
    }

    [Fact]
    public void One_unreadable_question_does_not_take_the_readable_ones_with_it()
    {
        var questions = AgentTurnQuestionsReader.Read(Document(
            "{\"version\":\"1\",\"kind\":\"questions\",\"message\":\"m\",\"questions\":["
            + "{\"id\":\"a\",\"question\":\"First?\",\"options\":[{\"label\":\"A\"}]},"
            + "{\"id\":\"b\",\"options\":[{\"label\":\"B\"}]},"
            + "{\"id\":\"c\",\"question\":\"Third?\",\"options\":[{\"label\":\"C\"},{\"description\":\"no label\"}]}]}"));

        questions.Select(q => q.Question).Should().Equal("First?", "Third?");
        questions[1].Options.Select(o => o.Label).Should().Equal("C");
    }

    [Fact]
    public void An_answer_document_carries_no_questions()
    {
        var questions = AgentTurnQuestionsReader.Read(Document(
            "{\"version\":\"1\",\"kind\":\"answer\",\"message\":\"17 times 3 is 51.\"}"));

        questions.Should().BeEmpty();
    }

    [Fact]
    public void A_degraded_document_renders_its_message_rather_than_controls()
    {
        // A questions document written under a version this build does not know: item 92's acceptance
        // says an older document still renders, and the way it renders is as its message (item 25 owns
        // the versioning). Handing a payload this build did not validate to a control renderer is
        // exactly what the degradation exists to prevent.
        var newer = Document(
            "{\"version\":\"9\",\"kind\":\"questions\",\"message\":\"m\",\"questions\":[{\"id\":\"q\","
            + "\"question\":\"Which?\",\"options\":[{\"label\":\"A\"}]}]}");

        newer.Degradation.Should().Be(AgentTurnDocumentDegradation.UnknownVersion);
        newer.Message.Should().Be("m");
        AgentTurnQuestionsReader.Read(newer).Should().BeEmpty();
    }

    [Fact]
    public void A_questions_document_whose_payload_is_missing_renders_its_message()
    {
        var withoutPayload = Document("{\"version\":\"1\",\"kind\":\"questions\",\"message\":\"Pick one.\"}");

        withoutPayload.Degradation.Should().Be(AgentTurnDocumentDegradation.MissingPayload);
        AgentTurnQuestionsReader.Read(withoutPayload).Should().BeEmpty();
    }

    [Fact]
    public void Nothing_at_all_is_not_an_error()
    {
        AgentTurnQuestionsReader.Read(null).Should().BeEmpty();
    }

    // ---- answered or not, read off the transcript ---------------------------------------------

    private static readonly DateTimeOffset When = new(2026, 8, 7, 9, 0, 0, TimeSpan.Zero);

    private static TurnCompleted Asked() =>
        new(When, null, false, "completed", Document(MeasuredQuestionsDocument));

    [Fact]
    public void A_question_with_no_user_turn_after_it_is_still_open()
    {
        IReadOnlyList<AgentEvent> transcript =
        [
            new MessageEmitted(When, TranscriptRole.User, "make coffee"),
            new MessageEmitted(When, TranscriptRole.Assistant, "Tell me about your coffee preferences!"),
            Asked(),
        ];

        AgentTurnQuestionsReader.WasAnswered(transcript, 2).Should().BeFalse();
    }

    [Fact]
    public void A_user_turn_after_the_question_answers_it_however_it_was_written()
    {
        // Clicking an option and typing prose produce the SAME thing: an ordinary user turn. So a user
        // who ignores the controls and answers in their own words closes the question too, which is the
        // whole reason the answered state is read off the transcript rather than off a button press.
        IReadOnlyList<AgentEvent> transcript =
        [
            Asked(),
            new MessageEmitted(When, TranscriptRole.User, "actually, with oat milk"),
        ];

        AgentTurnQuestionsReader.WasAnswered(transcript, 0).Should().BeTrue();
    }

    [Fact]
    public void The_agents_own_turns_after_a_question_do_not_answer_it()
    {
        IReadOnlyList<AgentEvent> transcript =
        [
            Asked(),
            new MessageEmitted(When, TranscriptRole.Assistant, "still waiting"),
            new StatsUpdated(When, AgentStatsSnapshot.Empty),
        ];

        AgentTurnQuestionsReader.WasAnswered(transcript, 0).Should().BeFalse();
    }

    // ---- what the answer says --------------------------------------------------------------

    private static AgentTurnQuestion Question(string text, params string[] options) =>
        new("id", text, [.. options.Select(o => new AgentTurnQuestionOption(o))]);

    [Fact]
    public void An_answer_restates_the_question_so_the_turn_stands_on_its_own()
    {
        var text = AgentTurnQuestionsReader.Compose(
            [new AgentTurnQuestionAnswer(Question("Where should cached responses live?", "Redis"), ["Redis"])]);

        text.Should().Be("Where should cached responses live? Redis");
    }

    [Fact]
    public void Several_picks_for_one_question_are_one_answer()
    {
        var text = AgentTurnQuestionsReader.Compose(
            [new AgentTurnQuestionAnswer(Question("Which may I touch?", "src", "tests"), ["src", "tests"])]);

        text.Should().Be("Which may I touch? src, tests");
    }

    [Fact]
    public void Several_questions_answer_one_per_line()
    {
        var text = AgentTurnQuestionsReader.Compose(
        [
            new AgentTurnQuestionAnswer(Question("First?", "A"), ["A"]),
            new AgentTurnQuestionAnswer(Question("Second?", "B"), ["B"]),
        ]);

        text.Should().Be("First? A\nSecond? B");
    }

    [Fact]
    public void A_question_nobody_answered_contributes_nothing_rather_than_an_empty_line()
    {
        var text = AgentTurnQuestionsReader.Compose(
        [
            new AgentTurnQuestionAnswer(Question("First?", "A"), []),
            new AgentTurnQuestionAnswer(Question("Second?", "B"), ["B"]),
        ]);

        text.Should().Be("Second? B");
    }
}
