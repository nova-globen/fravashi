using AwesomeAssertions;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;

namespace Fravashi.Domain.Tests.WorkItems;

/// <summary>Builders and assertions shared by the work item model tests.</summary>
internal static class WorkItemFixture
{
    private static int _next;

    public static WorkItemState State(
        WorkItemLevel level = WorkItemLevel.Task,
        WorkItemKind kind = WorkItemKind.Standard,
        WorkItemStatus status = WorkItemStatus.Triage,
        WorkItemId? parent = null,
        ProjectId? project = null,
        string title = "A title",
        string description = "The problem and the outcome.",
        WorkItemResolution? resolution = null,
        WorkItemFlags? flags = null,
        TimeSpan? timebox = null)
    {
        var number = Interlocked.Increment(ref _next);
        return new WorkItemState
        {
            Id = new WorkItemId("item-" + number.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Project = project ?? ProjectId.Default,
            Number = number,
            Level = level,
            Kind = kind,
            Title = title,
            Description = description,
            Parent = parent,
            Status = status,
            Resolution = resolution,
            Flags = flags ?? WorkItemFlags.None,
            SpikeTimebox = timebox,
        };
    }

    /// <summary>An item restored through the invariant tier only, so mirrored shapes can be built.</summary>
    public static WorkItem Item(
        WorkItemLevel level = WorkItemLevel.Task,
        WorkItemKind kind = WorkItemKind.Standard,
        WorkItemStatus status = WorkItemStatus.Triage,
        WorkItemId? parent = null,
        ProjectId? project = null,
        string description = "The problem and the outcome.",
        WorkItemResolution? resolution = null,
        WorkItemFlags? flags = null) =>
        Restored(State(level, kind, status, parent, project, description: description, resolution: resolution, flags: flags));

    public static WorkItem Restored(WorkItemState state)
    {
        var result = WorkItem.Restore(state);
        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors.Select(e => e.Code)));
        return result.Value;
    }

    public static WorkItemLink Link(WorkItem from, WorkItemLinkType type, WorkItem to) =>
        WorkItemLink.Normalise(from.Id, type, to.Id).Value;

    public static WorkItemLink Link(WorkItemId from, WorkItemLinkType type, WorkItemId to) =>
        WorkItemLink.Normalise(from, type, to).Value;

    public static void ShouldBeRefusedWith(this IReadOnlyList<ValidationError> errors, string code, string? field = null)
    {
        errors.Should().Contain(e => e.Code == code, "the refusal should carry {0}, but was: {1}", code, string.Join(", ", errors.Select(e => e.Code)));
        if (field is not null)
        {
            errors.Where(e => e.Code == code).Should().OnlyContain(e => e.Field == field);
        }

        errors.Should().OnlyContain(e => WorkItemRules.Tiers.ContainsKey(e.Code), "every refusal code is registered with a tier");
        errors.Should().OnlyContain(e => !string.IsNullOrWhiteSpace(e.Message), "every refusal carries a sentence");
    }

    public static void ShouldBeRefusedWith<T>(this DomainResult<T> result, string code, string? field = null)
    {
        result.IsValid.Should().BeFalse();
        result.Errors.ShouldBeRefusedWith(code, field);
    }

    public static void ShouldBeRefusedWith(this DomainResult result, string code, string? field = null)
    {
        result.IsValid.Should().BeFalse();
        result.Errors.ShouldBeRefusedWith(code, field);
    }

    public static T ShouldBeValid<T>(this DomainResult<T> result)
    {
        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors.Select(e => e.Code + ": " + e.Message)));
        return result.Value;
    }
}
