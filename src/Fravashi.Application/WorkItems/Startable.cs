using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;

namespace Fravashi.Application.WorkItems;

/// <summary>
/// What to ask "what can start now" (plan section 4.4). Everything here NARROWS the answer; nothing here
/// changes the four rules, so a caller cannot ask for an item that is not startable to be called startable.
/// </summary>
/// <param name="Parent">Only items under this one, for a board that is showing a feature.</param>
/// <param name="Assignee">Only items assigned to this person, or unassigned ones when null.</param>
/// <param name="IncludeAdvisoriesOnly">
/// When true, a qualifying item carrying an advisory is still returned as startable, which is the rule
/// (decision D10). It is here as an explicit false for a caller that wants the clean set only.
/// </param>
/// <param name="Limit">At most this many startable items, in board order.</param>
public sealed record StartableQuery(
    WorkItemId? Parent = null,
    string? Assignee = null,
    bool IncludeAdvisoriesOnly = true,
    int Limit = 100);

/// <summary>
/// The answer: what can start now, in board order, and everything that cannot with EVERY reason it cannot
/// (plan section 4.4), so a board and an agent can both say why rather than just showing less.
/// </summary>
/// <param name="Startable">Items that pass all four rules, ordered by rank then number.</param>
/// <param name="Held">Items that do not, each with its reasons, in the same order.</param>
public sealed record StartabilityReport(
    IReadOnlyList<StartableItem> Startable,
    IReadOnlyList<StartableItem> Held)
{
    /// <summary>An empty answer, for a project that has no items at all.</summary>
    public static StartabilityReport Empty { get; } = new([], []);
}

/// <summary>One item with its verdict, so a caller never has to re-derive why.</summary>
public sealed record StartableItem(WorkItem Item, StartabilityVerdict Verdict);

/// <summary>
/// What a caller supplies to record evidence (increment C2). The id, the stamp and the actor are the
/// tracker's to assign: evidence is a record of something that happened, and letting a caller backdate it
/// would make the append-only log unreadable.
/// </summary>
/// <param name="Criterion">The acceptance criterion this proves; it must be one the item has.</param>
/// <param name="Scenario">The scenario this is the result of; it must be one the item has.</param>
public sealed record NewEvidence
{
    /// <summary>What the evidence is.</summary>
    public required EvidenceKind Kind { get; init; }

    /// <summary>What it says happened.</summary>
    public required EvidenceResult Result { get; init; }

    /// <summary>A sentence a person can read. Never blank.</summary>
    public required string Summary { get; init; }

    /// <summary>The gate or check it speaks for, such as verify or design.</summary>
    public string? Gate { get; init; }

    /// <summary>Where the bytes live; item 75 owns what that may be.</summary>
    public string? ArtefactRef { get; init; }

    /// <summary>The acceptance criterion it proves, by id.</summary>
    public string? Criterion { get; init; }

    /// <summary>The test scenario it is the result of, by id.</summary>
    public string? Scenario { get; init; }

    /// <summary>Open per-kind metadata, as JSON text; items 55 and 56 define it.</summary>
    public string? Details { get; init; }
}
