namespace Fravashi.Domain.AgentSessions;

/// <summary>
/// The commit reviewer's three blocking failure classes (classes 1 and 2 from the reviewer's first prompt,
/// class 3 added by backlog 259), held as the one source both the reviewer's own prompt
/// (<c>AgentCommitReviewer.Instructions</c>) and a building session's first-turn note
/// (<see cref="AgentFirstTurnBriefing"/>, work item #333) read, so the two can never drift apart. The three
/// sentences are the reviewer's own wording, unchanged: moving them here changes nothing it says.
/// </summary>
public static class CommitReviewFailureClasses
{
    /// <summary>Class 1: a test that proves a call happened rather than what it does.</summary>
    public const string AppliedOrRecorded =
        "A test that asserts an operation was APPLIED or RECORDED rather than what it DOES. A green test that only "
        + "checks a call was made, a value was stored or a flag was set proves nothing about the effect a person "
        + "would see. Say which effect is unproven and what a test of it would assert.";

    /// <summary>Class 2: a safety property named without showing it holds in every path.</summary>
    public const string SafetyPropertyClaimed =
        "A SAFETY PROPERTY CLAIMED WITHOUT PROOF. A comment, a name or an outcome note that says something is safe, "
        + "contained, idempotent, never-sent or fail-closed, where the code does not show it in every path. Name the path.";

    /// <summary>Class 3: a factual claim that reaches further than the evidence for it.</summary>
    public const string ClaimWiderThanEvidence =
        "A CLAIM WIDER THAN ITS EVIDENCE. For each factual claim the change ADDS to a document, an ADR or a comment "
        + "(a measurement, a \"does not\", a \"never\", a version, a date), find its evidence in this change or in "
        + "the item it cites. If the evidence is narrower than the claim (one configuration, one version, a confound "
        + "the sentence does not name), quote the sentence and say what the evidence covers and what it does not. "
        + "Plans, intent and ordinary explanation are not claims; only what is presented as measured or established is.";

    /// <summary>All three, in the reviewer's own order.</summary>
    public static readonly IReadOnlyList<string> All = [AppliedOrRecorded, SafetyPropertyClaimed, ClaimWiderThanEvidence];

    /// <summary>
    /// Each class's title clause only (up to its first full stop), for a brief mention outside the review itself,
    /// such as <see cref="AgentFirstTurnBriefing"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> ShortNames =
        [.. All.Select(c => c[..c.IndexOf('.', StringComparison.Ordinal)])];
}
