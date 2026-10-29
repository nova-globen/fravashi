using AwesomeAssertions;
using Fravashi.Application.Mirroring;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.WorkItems;
using Xunit;

namespace Fravashi.Application.Tests.Mirroring;

/// <summary>
/// The field-ownership split as a translator (ADR 0018 decision 4). Two rules carry the weight: a field the
/// provider did not mention is not a clearing, and a value this cannot read is skipped rather than
/// defaulted.
/// </summary>
public class ProviderFieldsTests
{
    private static WorkItem Item(string description) => WorkItem.Restore(new WorkItemState
    {
        Id = WorkItemId.New(),
        Project = Fravashi.Domain.Projects.ProjectId.Default,
        Number = 1,
        Level = WorkItemLevel.Task,
        Title = "Imported",
        Description = description,
        CreatedUtc = DateTimeOffset.UtcNow,
        UpdatedUtc = DateTimeOffset.UtcNow,
    }).Value;

    private static WorkItemPatch Patch(params (string Field, string? Value)[] fields) =>
        ProviderFields.ToPatch(fields.ToDictionary(f => f.Field, f => f.Value, StringComparer.Ordinal));

    [Fact]
    public void The_import_placeholder_reads_as_no_description_toward_the_provider_and_a_real_one_reads_as_itself()
    {
        // Backlog 149: the site's item was empty; the sentence is ours, so the next sync must not send it there.
        ProviderFields.Read(Item(ProviderFields.NoDescription))[nameof(WorkItem.Description)].Should().BeEmpty();
        ProviderFields.Read(Item("A person wrote this."))[nameof(WorkItem.Description)].Should().Be("A person wrote this.");
    }

    [Fact]
    public void Every_carried_field_is_one_a_provider_can_own()
    {
        // If a Fravashi-owned field ever appeared here, a sync would start overwriting the agent layer.
        ProviderFields.Carried.Should().AllSatisfy(
            field => FieldOwnership.Of(field).Should().Be(FieldOwner.ProviderOwnable));
    }

    [Fact]
    public void The_status_and_the_flags_are_owned_by_a_provider_and_still_not_carried()
    {
        // Owned and carried are different questions. Status moves through a transition that has to ASK
        // which provider state is meant (ADR 0018 decision 6), and flags are a record with reasons that a
        // provider's bare yes-or-no cannot fill in.
        ProviderFields.Carried.Should().NotContain(nameof(WorkItem.Status))
            .And.NotContain(nameof(WorkItem.Resolution))
            .And.NotContain(nameof(WorkItem.Flags))
            .And.NotContain(nameof(WorkItem.Category));
    }

    [Fact]
    public void A_field_the_provider_never_mentioned_is_left_alone()
    {
        var patch = Patch((nameof(WorkItem.Title), "A title"));

        patch.Title.IsSet.Should().BeTrue();
        patch.Assignee.IsSet.Should().BeFalse("absent means no opinion, not set it to nothing");
        patch.Priority.IsSet.Should().BeFalse();
        patch.Parent.IsSet.Should().BeFalse();
    }

    [Fact]
    public void A_field_mentioned_as_empty_is_a_real_clearing_where_the_model_allows_one()
    {
        var patch = Patch(
            (nameof(WorkItem.Assignee), null),
            (nameof(WorkItem.Priority), string.Empty),
            (nameof(WorkItem.Parent), string.Empty));

        patch.Assignee.IsSet.Should().BeTrue();
        patch.Assignee.Value.Should().BeNull();
        patch.Priority.Value.Should().BeNull();
        patch.Parent.Value.Should().BeNull();
    }

    [Fact]
    public void An_empty_title_or_description_is_ignored_rather_than_sent_to_be_refused()
    {
        // Both are mandatory on the model, so an empty one from a provider is a gap in what it returned,
        // not an instruction to empty ours.
        var patch = Patch((nameof(WorkItem.Title), string.Empty), (nameof(WorkItem.Description), null));

        patch.Title.IsSet.Should().BeFalse();
        patch.Description.IsSet.Should().BeFalse();
    }

    [Theory]
    [InlineData("Task")]
    [InlineData("story")]
    [InlineData("")]
    [InlineData("nonsense")]
    public void A_level_this_cannot_read_is_skipped_rather_than_defaulted(string level)
    {
        // Defaulting would quietly demote a Feature to a Task because one provider spells its issue type
        // differently, and that is a data change nobody asked for. Note that "Task" is here on purpose:
        // the stable names are matched exactly and ordinally, so a case variant is unknown too.
        Patch((nameof(WorkItem.Level), level)).Level.IsSet.Should().BeFalse();
    }

    [Fact]
    public void A_level_this_can_read_is_taken()
    {
        Patch((nameof(WorkItem.Level), "feature")).Level.Value.Should().Be(WorkItemLevel.Feature);
    }

    [Fact]
    public void A_priority_this_cannot_read_leaves_the_local_one_alone_rather_than_clearing_it()
    {
        // The difference that matters: an ABSENT priority is left alone, an EMPTY one is a clearing, and an
        // unreadable one is left alone too. Treating the third like the second would erase a priority every
        // time a provider used a word we do not know.
        Patch((nameof(WorkItem.Priority), "Sev-1")).Priority.IsSet.Should().BeFalse();
    }
}
