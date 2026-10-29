using AwesomeAssertions;
using Fravashi.Domain.WorkItems;
using Xunit;
using static Fravashi.Domain.Tests.WorkItems.WorkItemFixture;

namespace Fravashi.Domain.Tests.WorkItems;

/// <summary>Plan section 5: enums are stored as stable lower-snake names, never ordinals.</summary>
public class StableNamesTests
{
    [Fact]
    public void Every_work_item_enum_has_stable_names()
    {
        StableNames.EnumTypes.Should().BeEquivalentTo(
        [
            typeof(WorkItemLevel), typeof(WorkItemKind), typeof(WorkItemStatus), typeof(StatusCategory),
            typeof(WorkItemResolution), typeof(WorkItemPriority), typeof(WorkItemLinkType), typeof(WorkItemActorKind),
            typeof(FieldOwner),
            // The agent layer's own enums (increment C1), stored as names like every other.
            typeof(Fravashi.Domain.WorkItems.AgentLayer.ArtifactRefRole),
            typeof(Fravashi.Domain.WorkItems.AgentLayer.ArtifactRefKind),
            typeof(Fravashi.Domain.WorkItems.AgentLayer.ScopeMode),
            // Evidence's own enums (increment C2).
            typeof(Fravashi.Domain.WorkItems.AgentLayer.EvidenceKind),
            typeof(Fravashi.Domain.WorkItems.AgentLayer.EvidenceResult),
            // Startability's codes (backlog 41): not stored, but an agent acts on them over the tool server.
            typeof(StartBlocker),
            typeof(StartAdvisory),
            // A work item's suggested model and effort (requested 2026-10-22).
            typeof(Fravashi.Domain.AgentSessions.AgentModelAlias),
            typeof(Fravashi.Domain.AgentSessions.AgentEffort),
        ]);
    }

    [Fact]
    public void Every_enum_value_has_a_unique_lower_snake_name()
    {
        foreach (var type in StableNames.EnumTypes)
        {
            var names = StableNames.NamesFor(type);

            names.Keys.Should().BeEquivalentTo(Enum.GetValues(type).Cast<Enum>(), "every value of {0} is named", type.Name);
            names.Values.Should().OnlyHaveUniqueItems();
            names.Values.Should().OnlyContain(n => System.Text.RegularExpressions.Regex.IsMatch(n, "^[a-z]+(_[a-z]+)*$"));
        }
    }

    [Fact]
    public void Every_name_round_trips()
    {
        RoundTrip<WorkItemLevel>();
        RoundTrip<WorkItemKind>();
        RoundTrip<WorkItemStatus>();
        RoundTrip<StatusCategory>();
        RoundTrip<WorkItemResolution>();
        RoundTrip<WorkItemPriority>();
        RoundTrip<WorkItemLinkType>();
        RoundTrip<WorkItemActorKind>();
        RoundTrip<FieldOwner>();
        RoundTrip<StartBlocker>();
        RoundTrip<StartAdvisory>();
        RoundTrip<Fravashi.Domain.AgentSessions.AgentModelAlias>();
        RoundTrip<Fravashi.Domain.AgentSessions.AgentEffort>();
    }

    [Fact]
    public void Suggested_model_and_effort_are_stored_under_the_names_the_cli_takes()
    {
        foreach (var alias in Fravashi.Domain.AgentSessions.AgentModelAliases.All)
        {
            StableNames.ToName(alias).Should().Be(Fravashi.Domain.AgentSessions.AgentModelAliases.CliId(alias));
        }

        foreach (var effort in Fravashi.Domain.AgentSessions.AgentEfforts.All)
        {
            StableNames.ToName(effort).Should().Be(Fravashi.Domain.AgentSessions.AgentEfforts.CliValue(effort));
        }
    }

    [Theory]
    [InlineData(WorkItemStatus.InProgress, "in_progress")]
    [InlineData(WorkItemStatus.InReview, "in_review")]
    [InlineData(WorkItemStatus.Cancelled, "cancelled")]
    public void Status_names_match_the_schema(WorkItemStatus status, string name)
    {
        StableNames.ToName(status).Should().Be(name);
    }

    [Fact]
    public void Link_names_match_the_schema_and_the_read_names()
    {
        WorkItemLinkTypes.Forward.Select(StableNames.ToName).Should().Equal("blocks", "relates", "duplicates", "supersedes");
        StableNames.ToName(WorkItemLinkType.BlockedBy).Should().Be("blocked_by");
        StableNames.ToName(WorkItemResolution.WontDo).Should().Be("wont_do");
        StableNames.ToName(WorkItemResolution.CannotReproduce).Should().Be("cannot_reproduce");
    }

    [Theory]
    [InlineData("started")]
    [InlineData("Done")]
    [InlineData("DONE")]
    [InlineData("")]
    [InlineData(" done")]
    [InlineData("5")]
    [InlineData(null)]
    public void An_unknown_name_is_refused_rather_than_defaulted(string? name)
    {
        StableNames.Parse<WorkItemStatus>(name, "status").ShouldBeRefusedWith(WorkItemRules.Codes.UnknownStorageName, "status");
    }

    [Fact]
    public void An_undefined_value_has_no_name()
    {
        var act = () => StableNames.ToName((WorkItemLevel)17);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static void RoundTrip<TEnum>()
        where TEnum : struct, Enum
    {
        foreach (var value in Enum.GetValues<TEnum>())
        {
            var name = StableNames.ToName(value);
            StableNames.Parse<TEnum>(name, "field").ShouldBeValid().Should().Be(value);
        }

        StableNames.NamesOf<TEnum>().Should().HaveCount(Enum.GetValues<TEnum>().Length);
    }
}
