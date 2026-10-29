namespace Fravashi.Domain.WorkItems;

/// <summary>
/// One stored edge between two work items, always in NORMAL form (plan section 4.2): the type is a forward
/// type, an inverse reading has been turned round (<c>a blocked_by b</c> is the edge <c>b blocks a</c>), and a
/// symmetric <c>relates</c> holds its two ids in ordinal order. Because the constructor is private and the
/// properties get-only, a non-normal edge cannot exist, so an item's <c>BlockedBy</c> and <c>Blocks</c> are two
/// reads of one edge set and cannot drift apart.
/// </summary>
public sealed record WorkItemLink
{
    private WorkItemLink(WorkItemId source, WorkItemLinkType type, WorkItemId target)
    {
        Source = source;
        Type = type;
        Target = target;
    }

    /// <summary>The source of the forward edge.</summary>
    public WorkItemId Source { get; }

    /// <summary>The forward type.</summary>
    public WorkItemLinkType Type { get; }

    /// <summary>The target of the forward edge.</summary>
    public WorkItemId Target { get; }

    /// <summary>
    /// Normalises a link as a caller states it. A link from an item to itself is refused with any type
    /// (<c>link_self</c>, a model invariant).
    /// </summary>
    public static DomainResult<WorkItemLink> Normalise(WorkItemId from, WorkItemLinkType type, WorkItemId to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Not a link type.");
        }

        if (from == to)
        {
            return DomainResult.Refused<WorkItemLink>(
            [
                new ValidationError(
                    WorkItemRules.Codes.LinkSelf,
                    "link",
                    $"An item cannot be linked to itself (item {from.Value}, type {StableNames.ToName(type)})."),
            ]);
        }

        var forward = type.ToForward();
        if (forward == WorkItemLinkType.Relates)
        {
            return from.CompareTo(to) <= 0
                ? DomainResult.Ok(new WorkItemLink(from, forward, to))
                : DomainResult.Ok(new WorkItemLink(to, forward, from));
        }

        return type.IsForward()
            ? DomainResult.Ok(new WorkItemLink(from, forward, to))
            : DomainResult.Ok(new WorkItemLink(to, forward, from));
    }

    /// <summary>True when the edge touches the item at either end.</summary>
    public bool Touches(WorkItemId item) => Source == item || Target == item;

    /// <summary>How the edge reads from <paramref name="item"/>: the forward type from its source, the inverse from its target.</summary>
    public WorkItemLinkType ReadingFrom(WorkItemId item) =>
        item == Source ? Type
        : item == Target ? Type.Inverse()
        : throw new ArgumentException("The item is not an end of this link.", nameof(item));

    /// <summary>The end of the edge that is not <paramref name="item"/>.</summary>
    public WorkItemId OtherEnd(WorkItemId item) =>
        item == Source ? Target
        : item == Target ? Source
        : throw new ArgumentException("The item is not an end of this link.", nameof(item));
}
