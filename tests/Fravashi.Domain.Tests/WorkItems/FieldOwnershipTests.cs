using System.Reflection;
using AwesomeAssertions;
using Fravashi.Domain.WorkItems;
using Xunit;

namespace Fravashi.Domain.Tests.WorkItems;

/// <summary>Plan section 1.6: every field of the aggregate says who owns it.</summary>
public class FieldOwnershipTests
{
    private static readonly List<string> Properties = typeof(WorkItem)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Select(p => p.Name)
        .ToList();

    [Fact]
    public void Every_public_property_of_the_aggregate_is_classified()
    {
        Properties.Should().NotBeEmpty();
        Properties.Should().OnlyContain(name => FieldOwnership.Classification.ContainsKey(name));
    }

    [Fact]
    public void The_classification_names_no_field_the_aggregate_does_not_have()
    {
        FieldOwnership.Classification.Keys.Should().BeEquivalentTo(Properties);
    }

    [Theory]
    [InlineData(nameof(WorkItem.Title))]
    [InlineData(nameof(WorkItem.Description))]
    [InlineData(nameof(WorkItem.Level))]
    [InlineData(nameof(WorkItem.Kind))]
    [InlineData(nameof(WorkItem.Parent))]
    [InlineData(nameof(WorkItem.Assignee))]
    [InlineData(nameof(WorkItem.Priority))]
    [InlineData(nameof(WorkItem.Status))]
    public void The_fields_the_plan_names_are_provider_ownable(string field)
    {
        FieldOwnership.Of(field).Should().Be(FieldOwner.ProviderOwnable);
    }

    [Theory]
    [InlineData(nameof(WorkItem.Id))]
    [InlineData(nameof(WorkItem.Version))]
    [InlineData(nameof(WorkItem.Rank))]
    public void What_fravashi_records_about_the_item_is_fravashi_owned(string field)
    {
        FieldOwnership.Of(field).Should().Be(FieldOwner.FravashiOwned);
    }

    [Fact]
    public void An_unclassified_name_throws()
    {
        var act = () => FieldOwnership.Of("NoSuchField");

        act.Should().Throw<ArgumentException>();
    }
}
