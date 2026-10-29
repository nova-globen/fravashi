using Fravashi.Domain.WorkItems.AgentLayer;

namespace Fravashi.Web.Components.WorkItems;

/// <summary>
/// What a person typed into a criterion's "Record evidence" form on the item page (follow-up filed 2026-10-22,
/// record evidence against a criterion). The page turns it into the tracker's <c>NewEvidence</c>, the same call
/// the <c>work_item_evidence</c> tool makes, so the two paths store the same record.
/// </summary>
/// <param name="CriterionId">The acceptance criterion it proves, by id.</param>
/// <param name="Result">Pass or fail.</param>
/// <param name="Summary">How it was checked and what was seen; the tracker refuses a blank one.</param>
/// <param name="Reference">Where to look (a commit sha, a docs/evidences path, a test name), or null.</param>
public sealed record CriterionEvidenceDraft(string CriterionId, EvidenceResult Result, string Summary, string? Reference);
