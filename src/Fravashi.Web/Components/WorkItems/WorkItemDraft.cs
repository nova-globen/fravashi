using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;

namespace Fravashi.Web.Components.WorkItems;

/// <summary>
/// What the create/edit form is editing (backlog 38). It is a mutable class rather than the aggregate on
/// purpose: <see cref="WorkItem"/> cannot be constructed without its invariants, so a half-typed title could
/// not be held in one, and the form has to be able to hold a state the domain would refuse in order to SHOW
/// the refusal. The domain still decides: nothing here validates anything.
/// </summary>
public sealed class WorkItemDraft
{
    private WorkItemDraft(WorkItem? existing)
    {
        Existing = existing;
    }

    /// <summary>The item being edited, or null when this draft creates one.</summary>
    public WorkItem? Existing { get; }

    /// <summary>True when saving this draft creates a new item.</summary>
    public bool IsNew => Existing is null;

    /// <summary>The title as typed, blank included.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The description as typed. Leaving it blank is how the domain's refusal gets shown.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>The containment level.</summary>
    public WorkItemLevel Level { get; set; } = WorkItemLevel.Task;

    /// <summary>The initial status, on a new item only; an existing item's status moves by transition.</summary>
    public WorkItemStatus Status { get; set; } = WorkItemStatus.Triage;

    /// <summary>The next concrete thing to do, or blank.</summary>
    public string NextAction { get; set; } = string.Empty;

    /// <summary>The model a conversation started from the item starts on, or null for Default (requested 2026-10-22).</summary>
    public AgentModelAlias? SuggestedModel { get; set; }

    /// <summary>The effort a conversation started from the item starts with, or null for Default.</summary>
    public AgentEffort? SuggestedEffort { get; set; }

    /// <summary>
    /// The acceptance criteria as typed, in order, blank rows included (backlog 178). Each keeps the id it was
    /// read with, so saving passes it back and evidence recorded against it survives an edit of its text or
    /// of its neighbours; a row added here has no id, and the store mints one.
    /// </summary>
    public List<CriterionDraft> Criteria { get; } = [];

    /// <summary>A blank draft for a new item.</summary>
    public static WorkItemDraft New() => new(null);

    /// <summary>The criteria as the tracker takes them: every row, in order, with the id it came with.</summary>
    public IReadOnlyList<AcceptanceCriterion> AcceptanceCriteria() =>
        [.. Criteria.Select(c => new AcceptanceCriterion(c.Id, c.Text))];

    /// <summary>A draft holding what an existing item currently says.</summary>
    public static WorkItemDraft Of(WorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var draft = new WorkItemDraft(item)
        {
            Title = item.Title,
            Description = item.Description,
            Level = item.Level,
            Status = item.Status,
            NextAction = item.NextAction ?? string.Empty,
            SuggestedModel = item.SuggestedModel,
            SuggestedEffort = item.SuggestedEffort,
        };
        draft.Criteria.AddRange(item.AcceptanceCriteria.Select(c => new CriterionDraft(c.Id) { Text = c.Text }));
        return draft;
    }
}

/// <summary>One acceptance criterion as typed on the form: the id it was read with (empty when new) and its text.</summary>
/// <param name="Id">The stored criterion's id, or empty for a row added on the form.</param>
public sealed class CriterionDraft(string id)
{
    /// <summary>The stored criterion's id, or empty for a row added on the form.</summary>
    public string Id { get; } = id;

    /// <summary>The text as typed, blank included; the domain decides whether a blank one may be saved.</summary>
    public string Text { get; set; } = string.Empty;
}
