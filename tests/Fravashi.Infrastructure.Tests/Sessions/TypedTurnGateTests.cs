using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Sessions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// The switch that decides whether a session's turns are typed (increments 4 and 6 of
/// <c>docs/plans/typed-agent-turns.md</c>).
/// <para>
/// It is ON by default since 2026-09-14. It was off while outcome iii (five validation attempts exhausted,
/// the CLI exits 1) could end a whole conversation; increment 6 made that ending resumable, so the default
/// flipped and only an explicit false keeps turns untyped.
/// </para>
/// </summary>
public class TypedTurnGateTests
{
    private static IConfiguration Configured(string? value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Sessions:TypedTurns"] = value })
            .Build();

    [Fact]
    public void A_studio_that_was_told_nothing_types_its_turns()
    {
        SessionsServiceCollectionExtensions.TypedTurnKindsFor(Configured(null))
            .Should().Equal(AgentTurnDocumentSchema.DefaultKinds);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    public void Only_an_explicit_false_leaves_the_turns_untyped(string value)
    {
        SessionsServiceCollectionExtensions.TypedTurnKindsFor(Configured(value)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("true")]
    [InlineData("")]
    [InlineData("yes please")]
    public void Anything_that_is_not_a_readable_false_keeps_the_default(string value)
    {
        // The default is the shipping protocol, so a typo in a settings row must not switch it away.
        SessionsServiceCollectionExtensions.TypedTurnKindsFor(Configured(value))
            .Should().Equal(AgentTurnDocumentSchema.DefaultKinds);
    }

    [Fact]
    public void The_kinds_v1_ships_are_only_the_ones_something_renders()
    {
        // The per-turn tax is the reason (ADR 0022: about 140 tokens per KB, charged on every turn).
        // Sending a kind nothing renders buys a document the studio can only print as prose. Questions
        // earned its place when backlog item 92 built the controls; the other four still render nowhere.
        AgentTurnDocumentSchema.DefaultKinds.Should().Equal(
            AgentTurnDocumentKind.Answer, AgentTurnDocumentKind.Questions);
    }
}
