namespace Fravashi.Domain.WorkItems.AgentLayer;

/// <summary>What a piece of evidence IS, which decides how a reader should weigh it.</summary>
public enum EvidenceKind
{
    TestRun,
    Screenshot,
    Log,
    Build,
    Review,
    Other,

    /// <summary>
    /// Not evidence of anything checked: the written reason someone gave for moving an item to Done while some
    /// of its acceptance criteria had no evidence (requested 2026-10-22, evidence on Done). Only that move
    /// writes it, tied to the item and never to a criterion, and it never counts as evidence FOR a criterion.
    /// </summary>
    NoEvidenceReason,
}

/// <summary>What the evidence says happened.</summary>
public enum EvidenceResult
{
    Pass,
    Fail,

    /// <summary>Some of it held and some did not, which is a real outcome and not a missing one.</summary>
    Partial,
}

/// <summary>
/// One durable record that something was checked (vision 5.3, Evidence): a test run, a screenshot, a log, a
/// build, a review. It is APPEND-ONLY by design (plan section 5.2): the port offers no update and no delete,
/// so a wrong record is followed by a newer one rather than rewritten, and the history of what was believed
/// when stays readable. Deleting the CONTENT is item 75's lifecycle, not an edit of the record.
/// </summary>
/// <param name="Gate">The gate or check this speaks for, such as verify or design, or null.</param>
/// <param name="ArtefactRef">Where the bytes live; what that may be is item 75's to decide.</param>
/// <param name="Criterion">The acceptance criterion this proves, when it proves one.</param>
/// <param name="Scenario">The test scenario this is the result of, when it is one.</param>
/// <param name="Details">Open per-kind metadata; items 55 and 56 define what goes in it.</param>
/// <param name="Actor">Who recorded it: the user, a session, or the system.</param>
public sealed record EvidenceRecord(
    string Id,
    EvidenceKind Kind,
    EvidenceResult Result,
    string Summary,
    string? Gate = null,
    string? ArtefactRef = null,
    string? Criterion = null,
    string? Scenario = null,
    string? Details = null,
    DateTimeOffset RecordedUtc = default,
    WorkItemActor? Actor = null);
