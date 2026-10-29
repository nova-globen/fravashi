using AwesomeAssertions;
using Fravashi.Domain.AgentSessions;
using Xunit;

namespace Fravashi.Domain.Tests;

/// <summary>
/// The document envelope and its kind vocabulary (ADR 0022). The vocabulary is a wire contract: the
/// names here are the strings the schema pins the model to and the property names its payloads sit
/// under, so they are spelled out one case at a time rather than derived from the C# names.
/// </summary>
public class AgentTurnDocumentTests
{
    [Fact]
    public void Every_kind_has_its_own_wire_name()
    {
        var names = AgentTurnDocumentKinds.All.Select(AgentTurnDocumentKinds.Name).ToArray();

        names.Should().OnlyHaveUniqueItems("the wire name is also the envelope property the payload sits under");
        names.Should().AllSatisfy(name => name.Should().NotBeNullOrWhiteSpace());
        names.Should().Equal(
            "answer", "questions", "plan", "proposal", "findings", "blocked", "advice", "follow_up_drafts");
    }

    [Fact]
    public void A_wire_name_round_trips()
    {
        foreach (var kind in AgentTurnDocumentKinds.All)
        {
            AgentTurnDocumentKinds.TryParse(AgentTurnDocumentKinds.Name(kind), out var parsed).Should().BeTrue();
            parsed.Should().Be(kind);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Answer")]
    [InlineData(" answer")]
    [InlineData("spreadsheet")]
    public void A_name_the_library_does_not_know_is_refused_rather_than_guessed(string? name)
    {
        AgentTurnDocumentKinds.TryParse(name, out _).Should().BeFalse();
    }

    [Fact]
    public void A_kind_outside_the_vocabulary_has_no_wire_name()
    {
        // Guards the cast a later refactor could make by accident: an unnamed kind must throw here rather
        // than reach the wire as a spelling nobody chose.
        var name = () => AgentTurnDocumentKinds.Name((AgentTurnDocumentKind)999);

        name.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void A_document_is_typed_only_when_nothing_degraded()
    {
        new AgentTurnDocument("1", "answer", "hello", AgentTurnDocumentKind.Answer).IsTyped.Should().BeTrue();

        foreach (var degradation in Enum.GetValues<AgentTurnDocumentDegradation>()
                     .Where(value => value != AgentTurnDocumentDegradation.None))
        {
            new AgentTurnDocument("1", "answer", "hello", Degradation: degradation)
                .IsTyped.Should().BeFalse($"a document degraded by {degradation} can only render its message");
        }
    }

    [Fact]
    public void The_envelope_version_this_build_writes_is_one()
    {
        AgentTurnDocument.CurrentVersion.Should().Be("1");
    }
}
