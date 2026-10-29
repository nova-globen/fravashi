using Fravashi.Domain.WorkItems;

namespace Fravashi.Application.WorkItems;

/// <summary>Which flags a query asks for.</summary>
public enum FlagFilter
{
    /// <summary>No filter.</summary>
    Any,

    /// <summary>Blocked set.</summary>
    Blocked,

    /// <summary>Deferred set.</summary>
    Deferred,

    /// <summary>Blocked or Deferred set.</summary>
    Either,

    /// <summary>Neither set.</summary>
    Neither,
}

/// <summary>Which items a query asks for by whether a release has taken them (work item #368).</summary>
public enum ReleaseFilter
{
    /// <summary>Released or not.</summary>
    Any,

    /// <summary>
    /// Everything except an ARCHIVED item: one some release has taken that is still Done. This is what the board shows.
    /// An item reopened after its release is back in play and shows again, still saying which release carried it.
    /// </summary>
    NotArchived,

    /// <summary>Only items no release has taken: what the board shows, archived items left out.</summary>
    Unreleased,

    /// <summary>Only items some release has taken.</summary>
    Released,
}

/// <summary>
/// "Linked to" in a query: every item X for which the link reads <c>Item Type X</c> from <see cref="Item"/>.
/// <c>(a, Blocks)</c> is what <c>a</c> blocks; <c>(a, BlockedBy)</c> is what blocks <c>a</c>.
/// </summary>
/// <param name="Item">The item the links are read from.</param>
/// <param name="Type">The reading, forward or inverse.</param>
public sealed record LinkedToFilter(WorkItemId Item, WorkItemLinkType Type);

/// <summary>
/// A provider-neutral work item query (plan section 3.2). Every filter left null or empty matches everything,
/// and the filters that are given must all match. Results are ordered by status in board order, then rank, then
/// number, and paged with an opaque keyset cursor.
/// </summary>
public sealed record WorkItemQuery
{
    /// <summary>The default page size.</summary>
    public const int DefaultLimit = 50;

    /// <summary>The largest page size.</summary>
    public const int MaxLimit = 500;

    /// <summary>Statuses to match.</summary>
    public IReadOnlyCollection<WorkItemStatus>? Statuses { get; init; }

    /// <summary>Categories to match.</summary>
    public IReadOnlyCollection<StatusCategory>? Categories { get; init; }

    /// <summary>Levels to match.</summary>
    public IReadOnlyCollection<WorkItemLevel>? Levels { get; init; }

    /// <summary>Kinds to match.</summary>
    public IReadOnlyCollection<WorkItemKind>? Kinds { get; init; }

    /// <summary>Only the children of this item.</summary>
    public WorkItemId? Parent { get; init; }

    /// <summary>Only items with no parent. Ignored when <see cref="Parent"/> is given.</summary>
    public bool NoParent { get; init; }

    /// <summary>The flags to match.</summary>
    public FlagFilter Flags { get; init; } = FlagFilter.Any;

    /// <summary>Only items assigned to this assignee, exactly.</summary>
    public string? Assignee { get; init; }

    /// <summary>A substring of the title or the description (ASCII case-insensitive).</summary>
    public string? Text { get; init; }

    /// <summary>Only items linked to an item in a reading.</summary>
    public LinkedToFilter? LinkedTo { get; init; }

    /// <summary>
    /// Only items that name the wiki document with this number (work item #365, the wiki's "Linked from"): an
    /// agent-layer reference to it (a plan, an approved design, context), or <c>@N</c> written in the title or the
    /// description. The written form is matched as the reference parser reads it (not after a letter, a digit or a
    /// marker, and not followed by a letter or a digit), except that a mention inside a code span still counts: the
    /// store answers this in one query rather than handing every description back to be parsed.
    /// </summary>
    public int? NamesWikiDocument { get; init; }

    /// <summary>
    /// Whether a release has taken the item (work item #368). The board asks for <see cref="ReleaseFilter.NotArchived"/>,
    /// which is a condition in the store's own query rather than a scan in the page.
    /// </summary>
    public ReleaseFilter Release { get; init; } = ReleaseFilter.Any;

    /// <summary>Only items taken by the release with exactly this version (work item #368, the list's "Released in").</summary>
    public string? ReleasedIn { get; init; }

    /// <summary>The page size, 1 to <see cref="MaxLimit"/>; out-of-range values are clamped.</summary>
    public int Limit { get; init; } = DefaultLimit;

    /// <summary>The <see cref="WorkItemPage.NextCursor"/> of the previous page, or null for the first page.</summary>
    public string? Cursor { get; init; }
}
