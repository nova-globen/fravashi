using System.Text.Json;
using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Xunit;

namespace Fravashi.Application.Tests;

/// <summary>
/// Reading an incoming document. The cases that matter are the ones a LIVE turn cannot produce, because
/// a live document is constrained by the schema this build sent: a transcript row written by a newer
/// studio, a kind a later library added, a payload that does not match the kind beside it. ADR 0022's
/// promise is that a transcript stored under an older version still renders, so every one of those has
/// to come back readable rather than as an exception or a dropped turn.
/// </summary>
public class AgentTurnDocumentReaderTests
{
    private const string AnswerJson =
        """{"version":"1","kind":"answer","message":"17 times 3 is 51."}""";

    private const string QuestionsJson =
        """
        {"version":"1","kind":"questions","message":"Tell me about your coffee preferences!",
         "questions":[{"id":"coffee_preparation","question":"How do you take your coffee?",
         "multiSelect":false,"options":[{"label":"Black, no additions"}]}]}
        """;

    [Fact]
    public void An_answer_document_reads_as_a_typed_document()
    {
        var document = AgentTurnDocumentReader.Read(AnswerJson);

        document.Should().NotBeNull();
        document!.Version.Should().Be("1");
        document.Kind.Should().Be("answer");
        document.KnownKind.Should().Be(AgentTurnDocumentKind.Answer);
        document.Message.Should().Be("17 times 3 is 51.");
        document.PayloadJson.Should().BeNull("the answer envelope's message is the whole document");
        document.Degradation.Should().Be(AgentTurnDocumentDegradation.None);
        document.IsTyped.Should().BeTrue();
    }

    [Fact]
    public void A_questions_document_keeps_its_payload_as_raw_json()
    {
        var document = AgentTurnDocumentReader.Read(QuestionsJson);

        document.Should().NotBeNull();
        document!.KnownKind.Should().Be(AgentTurnDocumentKind.Questions);
        document.IsTyped.Should().BeTrue();
        document.PayloadJson.Should().NotBeNull();

        using var payload = JsonDocument.Parse(document.PayloadJson!);
        payload.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        payload.RootElement[0].GetProperty("id").GetString().Should().Be("coffee_preparation");

        // Measured 2026-08-06: the model omitted both optional fields on the very first document it
        // produced from this schema, so nothing downstream may assume they arrive.
        payload.RootElement[0].TryGetProperty("header", out _).Should().BeFalse();
        payload.RootElement[0].GetProperty("options")[0].TryGetProperty("description", out _).Should().BeFalse();
    }

    [Fact]
    public void A_document_captured_from_the_real_cli_reads_as_typed()
    {
        // Not invented. This is the literal `structured_output` of a turn driven through claude 2.1.222
        // in the fravashi container on 2026-08-06, with the schema this library assembles for its default
        // kind set and nothing else. The point of the fixture is that the two halves agree: the schema the
        // studio sends and the reader that reads what comes back are not each other's assumptions.
        var document = AgentTurnDocumentReader.Read(
            """{"version":"1","kind":"answer","message":"17 times 3 equals **51**."}""");

        document.Should().NotBeNull();
        document!.KnownKind.Should().Be(AgentTurnDocumentKind.Answer);
        document.IsTyped.Should().BeTrue();
        document.Message.Should().Be("17 times 3 equals **51**.");
    }

    [Fact]
    public void A_document_from_a_future_version_renders_its_message_only()
    {
        var document = AgentTurnDocumentReader.Read(
            """{"version":"2","kind":"answer","message":"written by a later studio","answer":{"tone":"dry"}}""");

        document.Should().NotBeNull();
        document!.Message.Should().Be("written by a later studio", "the message is never dropped");
        document.Version.Should().Be("2", "the version is kept verbatim so a later build can still read it");
        document.Kind.Should().Be("answer");
        document.Degradation.Should().Be(AgentTurnDocumentDegradation.UnknownVersion);
        document.IsTyped.Should().BeFalse();
        document.KnownKind.Should().BeNull(
            "a version bump means an old renderer would be wrong, so recognising the kind under it would be "
            + "exactly the wrong kind of helpfulness");
        document.PayloadJson.Should().BeNull();
    }

    [Fact]
    public void A_document_of_an_unknown_kind_renders_its_message_only()
    {
        var document = AgentTurnDocumentReader.Read(
            """{"version":"1","kind":"spreadsheet","message":"a kind a later library added","spreadsheet":[]}""");

        document.Should().NotBeNull();
        document!.Message.Should().Be("a kind a later library added");
        document.Kind.Should().Be("spreadsheet", "the kind is kept verbatim so a later build can read it");
        document.KnownKind.Should().BeNull();
        document.Degradation.Should().Be(AgentTurnDocumentDegradation.UnknownKind);
        document.IsTyped.Should().BeFalse();
    }

    [Fact]
    public void A_kind_name_is_read_case_sensitively()
    {
        // The schema constrains the model to the exact spelling, so a different one came from somewhere
        // else and is not this library's 'questions'.
        var document = AgentTurnDocumentReader.Read(
            """{"version":"1","kind":"Questions","message":"nearly","Questions":[]}""");

        document!.Degradation.Should().Be(AgentTurnDocumentDegradation.UnknownKind);
    }

    [Fact]
    public void A_kind_whose_payload_is_missing_degrades_to_its_message()
    {
        // The price of the flat, permissive envelope (spec decision 2): the kind/payload pairing is not
        // in the schema the model had to satisfy, so it is checked here, in one branch.
        var document = AgentTurnDocumentReader.Read(
            """{"version":"1","kind":"questions","message":"I have some questions."}""");

        document.Should().NotBeNull();
        document!.Message.Should().Be("I have some questions.");
        document.KnownKind.Should().Be(AgentTurnDocumentKind.Questions, "the kind itself was readable");
        document.PayloadJson.Should().BeNull();
        document.Degradation.Should().Be(AgentTurnDocumentDegradation.MissingPayload);
        document.IsTyped.Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"version":"1","kind":"questions","message":"m","questions":{}}""")]
    [InlineData("""{"version":"1","kind":"questions","message":"m","questions":"one, actually"}""")]
    [InlineData("""{"version":"1","kind":"questions","message":"m","questions":null}""")]
    [InlineData("""{"version":"1","kind":"blocked","message":"m","blocked":[]}""")]
    public void A_payload_of_the_wrong_shape_degrades_the_same_way(string json)
    {
        var document = AgentTurnDocumentReader.Read(json);

        document.Should().NotBeNull();
        document!.Degradation.Should().Be(AgentTurnDocumentDegradation.MissingPayload);
        document.PayloadJson.Should().BeNull();
        document.Message.Should().Be("m");
    }

    [Fact]
    public void An_unknown_property_beside_a_known_kind_is_left_alone()
    {
        // v1 sets no additionalProperties gate on purpose, so a document carrying more than the library
        // knows is still a good document.
        var document = AgentTurnDocumentReader.Read(
            """{"version":"1","kind":"answer","message":"fine","confidence":0.9}""");

        document!.IsTyped.Should().BeTrue();
        document.KnownKind.Should().Be(AgentTurnDocumentKind.Answer);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"version\":\"1\",\"kind\":\"answer\"")]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("null")]
    [InlineData("""{"kind":"answer","message":"no version"}""")]
    [InlineData("""{"version":"1","message":"no kind"}""")]
    [InlineData("""{"version":"1","kind":"answer"}""")]
    [InlineData("""{"version":"1","kind":"answer","message":null}""")]
    [InlineData("""{"version":"1","kind":"answer","message":42}""")]
    [InlineData("""{"version":true,"kind":"answer","message":"a version that is no version"}""")]
    [InlineData("""{"version":{"n":1},"kind":"answer","message":"an object version"}""")]
    [InlineData("""{"version":"","kind":"answer","message":"a blank version"}""")]
    [InlineData("""{"version":"1","kind":"","message":"a blank kind"}""")]
    public void What_is_not_a_document_is_reported_as_no_document(string? json)
    {
        // Null is not "an empty document": it is the caller's evidence that the turn was untyped, which
        // is the only honest reading when there is no message to render and nothing to mark.
        var read = () => AgentTurnDocumentReader.Read(json);

        read.Should().NotThrow();
        read().Should().BeNull();
    }

    [Fact]
    public void A_version_written_as_a_number_is_the_same_version()
    {
        // Measured in the installed v0.9.27 on 2026-09-21: of fourteen turn-ending calls in one conversation,
        // thirteen carried "version":1 and one carried "version":"1". The schema demanded the string, the
        // harness's error ("must be equal to constant") never names the constant, and two turns of three
        // burned all five attempts and ended with no answer. A numeral and its spelling are one version.
        var document = AgentTurnDocumentReader.Read("""{"version":1,"kind":"answer","message":"typed"}""");

        // The validator compares numbers by value, so 1.0 gets past it and has to read the same way here.
        AgentTurnDocumentReader.Read("""{"version":1.0,"kind":"answer","message":"typed"}""")!
            .IsTyped.Should().BeTrue();

        document.Should().NotBeNull();
        document!.Version.Should().Be(AgentTurnDocument.CurrentVersion);
        document.IsTyped.Should().BeTrue();
    }

    [Fact]
    public void A_number_that_is_another_version_is_still_an_unknown_version()
    {
        var document = AgentTurnDocumentReader.Read("""{"version":2,"kind":"answer","message":"later"}""");

        document.Should().NotBeNull();
        document!.Degradation.Should().Be(AgentTurnDocumentDegradation.UnknownVersion);
    }

    [Fact]
    public void The_schema_lets_the_version_be_written_either_way()
    {
        using var schema = JsonDocument.Parse(AgentTurnDocumentSchema.For(AgentTurnDocumentSchema.DefaultKinds));

        var allowed = schema.RootElement.GetProperty("properties").GetProperty("version").GetProperty("enum")
            .EnumerateArray().Select(value => (value.ValueKind, value.GetRawText())).ToArray();

        allowed.Should().BeEquivalentTo(new[]
        {
            (JsonValueKind.String, "\"" + AgentTurnDocument.CurrentVersion + "\""),
            (JsonValueKind.Number, AgentTurnDocument.CurrentVersion),
        });
    }

    [Fact]
    public void An_empty_message_is_still_a_document()
    {
        var document = AgentTurnDocumentReader.Read("""{"version":"1","kind":"answer","message":""}""");

        document.Should().NotBeNull("the model wrote nothing, which is not the same as writing no document");
        document!.Message.Should().BeEmpty();
        document.IsTyped.Should().BeTrue();
    }

    [Fact]
    public void An_element_and_its_text_read_the_same_way()
    {
        // The terminal result line carries the document twice, as a parsed structured_output object and
        // as the same JSON in the result string, so both entry points have to agree.
        using var parsed = JsonDocument.Parse(QuestionsJson);

        AgentTurnDocumentReader.Read(parsed.RootElement).Should().Be(AgentTurnDocumentReader.Read(QuestionsJson));
    }

    [Fact]
    public void Every_kind_the_library_sends_reads_back_as_itself()
    {
        foreach (var kind in AgentTurnDocumentKinds.All)
        {
            var name = AgentTurnDocumentKinds.Name(kind);
            var payload = AgentTurnDocumentSchema.PayloadValueKind(kind) switch
            {
                JsonValueKind.Array => $",\"{name}\":[]",
                JsonValueKind.Object => $",\"{name}\":{{}}",
                _ => string.Empty,
            };

            var document = AgentTurnDocumentReader.Read(
                $$"""{"version":"1","kind":"{{name}}","message":"round trip"{{payload}}}""");

            document.Should().NotBeNull();
            document!.KnownKind.Should().Be(kind);
            document.Degradation.Should().Be(
                AgentTurnDocumentDegradation.None,
                $"a document of kind '{name}' shaped the way this library's own schema describes it must read "
                + "as typed, or the library and the reader disagree");
        }
    }
}
