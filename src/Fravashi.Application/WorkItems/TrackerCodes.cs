namespace Fravashi.Application.WorkItems;

/// <summary>
/// The refusal codes the PORT adds on top of the domain's (<c>WorkItemRules.Codes</c>). They are not model
/// rules: they judge a call (its operation id, its placement anchors, its project), so they live beside the port
/// rather than in the domain's tier registry.
/// </summary>
public static class TrackerCodes
{
    /// <summary>An operation id already recorded for a different operation or different items.</summary>
    public const string OperationIdReused = "operation_id_reused";

    /// <summary>A placement anchor that is not in the destination status column.</summary>
    public const string PlacementAnchorStatus = "placement_anchor_status";

    /// <summary>A placement anchor that is the item being placed.</summary>
    public const string PlacementAnchorSelf = "placement_anchor_self";

    /// <summary>A placement anchor that is not an item of this project.</summary>
    public const string PlacementAnchorNotFound = "placement_anchor_not_found";

    /// <summary>A move with neither anchor.</summary>
    public const string MoveAnchorRequired = "move_anchor_required";

    /// <summary>The tracker's project does not exist.</summary>
    public const string ProjectNotFound = "project_not_found";

    /// <summary>Evidence naming an acceptance criterion the item does not have.</summary>
    public const string EvidenceCriterionNotFound = "evidence_criterion_not_found";

    /// <summary>Evidence naming a test scenario the item does not have.</summary>
    public const string EvidenceScenarioNotFound = "evidence_scenario_not_found";

    /// <summary>Evidence with no summary, which nobody could read later.</summary>
    public const string EvidenceSummaryRequired = "evidence_summary_required";

    /// <summary>A list replacement that would drop an acceptance criterion evidence points at.</summary>
    public const string CriterionHasEvidence = "criterion_has_evidence";

    /// <summary>A list replacement that would drop a test scenario evidence points at.</summary>
    public const string ScenarioHasEvidence = "scenario_has_evidence";

    /// <summary>
    /// A move into Done while an acceptance criterion has no evidence and no written reason was given (owner
    /// request 2026-10-22, evidence on Done). The message names each criterion without evidence; the same move
    /// with a reason is allowed and stores the reason on the item.
    /// </summary>
    public const string DoneNeedsEvidence = "done_needs_evidence";
}
