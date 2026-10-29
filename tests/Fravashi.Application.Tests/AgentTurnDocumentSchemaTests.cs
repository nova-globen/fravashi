using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Xunit;

namespace Fravashi.Application.Tests;

/// <summary>
/// The schema library is what the studio hands the CLI on every turn (ADR 0022), so two things about it
/// are load bearing and neither is visible by reading the code: the bytes it produces are the bytes that
/// were measured against the real CLI, and its size is a tax charged on every turn a user ever runs.
/// Both get an assertion here.
/// </summary>
public class AgentTurnDocumentSchemaTests
{
    /// <summary>
    /// The measured answer-only schema, copied from <c>docs/plans/typed-agent-turns.md</c> exactly as it
    /// was sent to claude 2.1.222 on 2026-08-06. The assembler is allowed to be rewritten; the wire form
    /// the measurement stands on is not allowed to change without a new measurement.
    /// </summary>
    private const string MeasuredAnswerSchema =
        "{\"type\":\"object\",\"required\":[\"version\",\"kind\",\"message\"],\"properties\":"
        + "{\"version\":{\"const\":\"1\"},\"kind\":{\"enum\":[\"answer\"]},\"message\":{\"type\":\"string\","
        + "\"description\":\"Markdown prose for the user. This is what the user reads.\"}}}";

    [Fact]
    public void The_default_kind_set_is_what_the_studio_can_render()
    {
        // A kind is not SENT until something renders it: one nothing renders buys a document the studio
        // can only print as prose, at a cost on every turn. Questions joined the set when backlog item 92
        // built the controls; the remaining four still have schemas here and no renderer anywhere.
        AgentTurnDocumentSchema.DefaultKinds.Should().Equal(
            AgentTurnDocumentKind.Answer, AgentTurnDocumentKind.Questions);
    }

    /// <summary>
    /// The ONE place the sent form has left the measured one, and why it left without the new measurement
    /// the summary above asks for. In the installed v0.9.27 on 2026-09-21 the model wrote the version as the
    /// numeral on thirteen of fourteen turn-ending calls, the string const refused each, and two turns of
    /// three ended with no answer. The version now permits both spellings. Measuring that against the pinned
    /// CLI was attempted the same day and could not be done from a session: the container's CLI has no
    /// sign-in of its own since the broker became the route, and neither has the host once a session's own
    /// variables are removed, as ADR 0023 requires. So this substitution is UNMEASURED, and says so, until a
    /// release carries it and a live turn in the installed window ends typed (backlog 139 holds that).
    /// </summary>
    private const string MeasuredVersionConstraint = "{\"const\":\"1\"}";

    private const string SentVersionConstraint = "{\"enum\":[\"1\",1]}";

    [Fact]
    public void The_answer_only_schema_is_the_measured_bytes_but_for_the_versions_second_spelling()
    {
        MeasuredAnswerSchema.Should().Contain(MeasuredVersionConstraint);

        AgentTurnDocumentSchema.For([AgentTurnDocumentKind.Answer]).Should().Be(
            MeasuredAnswerSchema.Replace(MeasuredVersionConstraint, SentVersionConstraint, StringComparison.Ordinal));
    }

    [Fact]
    public void The_default_schema_stays_under_a_kilobyte()
    {
        // The per-turn tax guard. ADR 0022 measured about 140 tokens per KB of schema, charged on every
        // turn, so a payload added carelessly should fail the build rather than quietly bill every user.
        var bytes = Encoding.UTF8.GetByteCount(AgentTurnDocumentSchema.For(AgentTurnDocumentSchema.DefaultKinds));

        bytes.Should().Be(608, "605 was the size measured on 2026-08-06, and the version's second spelling added 3");
        bytes.Should().BeLessThan(1024);
    }

    [Fact]
    public void Every_assembled_schema_is_valid_json()
    {
        foreach (var kind in AgentTurnDocumentKinds.All)
        {
            var schema = AgentTurnDocumentSchema.For([AgentTurnDocumentKind.Answer, kind]);

            var read = () => JsonDocument.Parse(schema).Dispose();
            read.Should().NotThrow($"the schema sent for '{AgentTurnDocumentKinds.Name(kind)}' has to parse");
        }

        var everything = () => JsonDocument.Parse(AgentTurnDocumentSchema.For(AgentTurnDocumentKinds.All)).Dispose();
        everything.Should().NotThrow("the whole library assembled at once has to parse too");
    }

    [Fact]
    public void A_set_including_questions_carries_the_questions_payload()
    {
        var schema = AgentTurnDocumentSchema.For([AgentTurnDocumentKind.Answer, AgentTurnDocumentKind.Questions]);

        using var document = JsonDocument.Parse(schema);
        var properties = document.RootElement.GetProperty("properties");

        properties.GetProperty("kind").GetProperty("enum").EnumerateArray()
            .Select(element => element.GetString())
            .Should().Equal("answer", "questions");
        properties.TryGetProperty("questions", out var payload).Should().BeTrue();
        payload.GetProperty("type").GetString().Should().Be("array");

        // The measured size of this set, and the reason the studio does not just send everything.
        Encoding.UTF8.GetByteCount(schema).Should().Be(608);
    }

    [Fact]
    public void A_kind_that_is_not_sent_leaves_no_slot_in_the_envelope()
    {
        var schema = AgentTurnDocumentSchema.For(AgentTurnDocumentSchema.DefaultKinds);

        using var document = JsonDocument.Parse(schema);
        var properties = document.RootElement.GetProperty("properties");

        foreach (var kind in AgentTurnDocumentKinds.All.Where(k => !AgentTurnDocumentSchema.DefaultKinds.Contains(k)))
        {
            properties.TryGetProperty(AgentTurnDocumentKinds.Name(kind), out _).Should().BeFalse(
                $"'{AgentTurnDocumentKinds.Name(kind)}' was not in the sent set, so its bytes must not be charged");
        }
    }

    [Fact]
    public void Every_kind_in_the_vocabulary_has_a_literal_schema()
    {
        // A kind cannot be added to the enum without a decision here: either a literal payload schema or
        // an explicit "this kind carries no payload". Answer is the only one allowed to be payload free.
        foreach (var kind in AgentTurnDocumentKinds.All)
        {
            var payload = AgentTurnDocumentSchema.PayloadSchema(kind);

            if (kind == AgentTurnDocumentKind.Answer)
            {
                payload.Should().BeNull("the answer envelope's message is the whole document");
                AgentTurnDocumentSchema.PayloadValueKind(kind).Should().BeNull();
                continue;
            }

            payload.Should().NotBeNullOrWhiteSpace(
                $"'{AgentTurnDocumentKinds.Name(kind)}' is in the vocabulary, so its payload must be defined");

            var read = () => JsonDocument.Parse(payload!).Dispose();
            read.Should().NotThrow();

            AgentTurnDocumentSchema.PayloadValueKind(kind).Should().BeOneOf(
                JsonValueKind.Object,
                JsonValueKind.Array);
        }
    }

    [Fact]
    public void A_payload_value_kind_is_read_out_of_the_schema_rather_than_restated()
    {
        foreach (var kind in AgentTurnDocumentKinds.All)
        {
            if (AgentTurnDocumentSchema.PayloadSchema(kind) is not { } payload)
            {
                continue;
            }

            using var document = JsonDocument.Parse(payload);
            var declared = document.RootElement.GetProperty("type").GetString();

            AgentTurnDocumentSchema.PayloadValueKind(kind).Should().Be(
                declared == "array" ? JsonValueKind.Array : JsonValueKind.Object,
                $"the reader's idea of what '{AgentTurnDocumentKinds.Name(kind)}' looks like must come from the "
                + "schema that was actually sent, not from a second copy of the answer");
        }
    }

    [Fact]
    public void Kinds_are_emitted_in_vocabulary_order_however_they_are_asked_for()
    {
        // Same set, same bytes: the size guard above is only meaningful if the assembler is deterministic.
        var forwards = AgentTurnDocumentSchema.For([AgentTurnDocumentKind.Answer, AgentTurnDocumentKind.Blocked]);
        var backwards = AgentTurnDocumentSchema.For([AgentTurnDocumentKind.Blocked, AgentTurnDocumentKind.Answer]);
        var repeated = AgentTurnDocumentSchema.For(
            [AgentTurnDocumentKind.Blocked, AgentTurnDocumentKind.Answer, AgentTurnDocumentKind.Blocked]);

        backwards.Should().Be(forwards);
        repeated.Should().Be(forwards);
    }

    [Fact]
    public void A_turn_with_no_kinds_is_refused()
    {
        var assemble = () => AgentTurnDocumentSchema.For([]);

        assemble.Should().Throw<ArgumentException>("a turn with no allowed kind has no way to end");
    }

    [Fact]
    public void The_schema_pins_the_version_the_model_may_write()
    {
        using var document = JsonDocument.Parse(AgentTurnDocumentSchema.For(AgentTurnDocumentSchema.DefaultKinds));

        // Pinned to one VALUE in its two spellings; the reader tests hold what each spelling reads as.
        document.RootElement.GetProperty("properties").GetProperty("version").GetProperty("enum")
            .EnumerateArray().Select(element => element.ToString())
            .Should().OnlyContain(text => text == AgentTurnDocument.CurrentVersion).And.HaveCount(2);
        AgentTurnDocumentSchema.Version.Should().Be(AgentTurnDocument.CurrentVersion);
    }

    [Fact]
    public void The_envelope_requires_only_what_a_renderer_cannot_do_without()
    {
        // v1 is permissive on purpose (spec decision 2): every extra constraint is another way to burn
        // one of the CLI's five validation attempts, and the fifth failure ends the turn and exits the
        // process. So: three required fields, and no additionalProperties gate.
        using var document = JsonDocument.Parse(AgentTurnDocumentSchema.For(AgentTurnDocumentKinds.All));

        document.RootElement.GetProperty("required").EnumerateArray()
            .Select(element => element.GetString())
            .Should().Equal("version", "kind", "message");
        document.RootElement.TryGetProperty("additionalProperties", out _).Should().BeFalse();
    }
}
