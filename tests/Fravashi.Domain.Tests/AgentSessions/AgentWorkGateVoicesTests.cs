using System.Reflection;
using AwesomeAssertions;
using Fravashi.Domain.AgentSessions;
using Xunit;

namespace Fravashi.Domain.Tests.AgentSessions;

/// <summary>
/// Backlog 127: every sentence the gate tells the MODEL has a short one for the PERSON. Held by reflection over
/// the constants, so a gate sentence added later without a person's version is a red build rather than fifteen
/// lines of tool names in somebody's transcript.
/// </summary>
public sealed class AgentWorkGateVoicesTests
{
    private static IEnumerable<string> GateSentences() =>
        typeof(AgentWorkGate).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name.EndsWith("Sentence", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!);

    [Fact]
    public void Every_sentence_written_for_the_model_has_one_written_for_the_person()
    {
        GateSentences().Should().HaveCountGreaterThan(5);
        foreach (var sentence in GateSentences())
        {
            var person = AgentWorkGate.ForThePerson(sentence);

            person.Should().NotBeNull($"the gate can say \"{sentence[..40]}...\" and a person has to be told something too");
            person.Should().NotContain("mcp__", "tool names are instructions to an agent, not information for a person");
            person!.Length.Should().BeLessThan(110);
        }
    }

    [Fact]
    public void A_sentence_is_recognised_inside_the_words_the_cli_wraps_around_it() =>
        AgentWorkGate.ForThePerson("PreToolUse:Edit hook error: " + AgentWorkGate.NoPlanSentence + " (denied)")
            .Should().Contain("no plan yet");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("The studio saw no answer within 4 hour(s), so the call was denied.")]
    public void Anything_that_is_not_a_gate_sentence_is_left_to_be_shown_verbatim(string? text) =>
        AgentWorkGate.ForThePerson(text).Should().BeNull();
}
