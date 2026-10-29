namespace Fravashi.Domain.WorkItems.AgentLayer;

/// <summary>
/// The agent layer's entries all carry an id of their own, because something else points at them: evidence
/// proves a criterion or a scenario, a readiness box is ticked by id, and a list is replaced whole on update
/// while keeping the ids the caller passed back (plan section 5.2). An id the caller does not supply is minted
/// by the store, so a caller that has never seen the item can still author a list.
/// </summary>
public static class AgentLayerEntry
{
    /// <summary>A new entry id. Plain text rather than a typed id: these are addressable, never resolvable.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>The id when the caller passed one back, otherwise a new one.</summary>
    public static string IdOr(string? supplied) =>
        string.IsNullOrWhiteSpace(supplied) ? NewId() : supplied.Trim();
}

/// <summary>One acceptance criterion: the done-test a verifier can check (vision 5.3, Intent).</summary>
/// <param name="Id">Stable across a list replacement that passes it back; evidence points at it.</param>
/// <param name="Text">What must be true. Never blank.</param>
public sealed record AcceptanceCriterion(string Id, string Text);

/// <summary>One definition-of-ready line and whether it is ticked (vision 5.3, Readiness).</summary>
public sealed record ReadinessItem(string Id, string Text, bool Checked);

/// <summary>
/// How ambiguous the item is thought to be, 0 (clear) to 100 (nobody knows what this means), with who said so
/// and when. An agent reads it to decide whether it can safely pick the item up unattended.
/// </summary>
/// <param name="Score">0 to 100 inclusive; anything else is refused.</param>
/// <param name="AssessedBy">Who assessed it, as that source names itself, or null.</param>
/// <param name="AssessedUtc">When, or null when it was never assessed.</param>
public sealed record AmbiguityAssessment(int Score, string? AssessedBy, DateTimeOffset? AssessedUtc)
{
    /// <summary>The highest score that still counts as workable without a human first (advisory, not a rule).</summary>
    public const int WorkableCeiling = 40;
}

/// <summary>
/// A blocker that is a PERSON rather than another item: a decision, an approval, an external action. It is
/// cleared, never deleted, so the history of what the work waited on survives (plan section 4).
/// </summary>
/// <param name="Person">Who is being waited on, as the author names them.</param>
/// <param name="Reason">What is being waited for. Never blank.</param>
/// <param name="ClearedUtc">When it stopped blocking, or null while it still does.</param>
public sealed record PersonBlocker(
    string Id,
    string Person,
    string Reason,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? ClearedUtc)
{
    /// <summary>True while the item is still waiting on this person.</summary>
    public bool IsActive => ClearedUtc is null;
}

/// <summary>What an <see cref="ArtifactRef"/> is FOR, which is what makes at-most-one meaningful per role.</summary>
public enum ArtifactRefRole
{
    /// <summary>The curated context bundle: what to read before starting. Many allowed.</summary>
    Context,

    /// <summary>The plan this item is executed under. At most one.</summary>
    Plan,

    /// <summary>The mockup the design is drawn in. At most one.</summary>
    Mockup,

    /// <summary>The interactive prototype. At most one.</summary>
    Prototype,

    /// <summary>The approved design the verifier checks against. At most one.</summary>
    ApprovedDesign,
}

/// <summary>What KIND of thing a ref points at. The target is opaque here; item 39 resolves wiki and PR ones.</summary>
public enum ArtifactRefKind
{
    Wiki,
    Adr,
    Decision,
    File,
    PullRequest,
    Url,
    Design,
}

/// <summary>
/// A pointer from an item to something that is not code: a wiki page, an ADR, a decision, a file in a bound
/// workspace, a pull request, a URL, a design. The <paramref name="Target"/> is opaque text in item 37 (plan
/// section 5.2): nothing here resolves it, and the item that owns each kind resolves it later.
/// </summary>
/// <param name="WorkspaceId">Which bound workspace a <see cref="ArtifactRefKind.File"/> target is in.</param>
/// <param name="Version">
/// For <see cref="ArtifactRefRole.ApprovedDesign"/>: the design document's version at the moment it was
/// approved (backlog 209), so an edit after approval cannot silently change what was agreed to. Null for a
/// reference written before pinning existed, which reads as "approved, nothing to compare against" rather
/// than as a document at version zero.
/// </param>
public sealed record ArtifactRef(
    string Id,
    ArtifactRefRole Role,
    ArtifactRefKind Kind,
    string Target,
    string? WorkspaceId = null,
    string? Label = null,
    string? Note = null,
    int? Version = null)
{
    /// <summary>The roles that hold at most one ref, because a second would make "the design" ambiguous.</summary>
    public static IReadOnlySet<ArtifactRefRole> SingleValueRoles { get; } = new HashSet<ArtifactRefRole>
    {
        ArtifactRefRole.Plan,
        ArtifactRefRole.Mockup,
        ArtifactRefRole.Prototype,
        ArtifactRefRole.ApprovedDesign,
    };
}

/// <summary>Whether a scope pattern opens an area to an unattended session or closes it.</summary>
public enum ScopeMode
{
    Allowed,
    Forbidden,
}

/// <summary>
/// One guardrail path pattern: a glob RELATIVE to a workspace root, never rooted and never climbing out, so a
/// pattern cannot silently name somewhere outside the work. A null <paramref name="WorkspaceId"/> means every
/// workspace bound to the project.
/// </summary>
public sealed record ScopePattern(string Id, ScopeMode Mode, string Pattern, string? WorkspaceId = null);

/// <summary>
/// The change budget: how much an unattended session may touch before it has to stop and ask. The file count
/// is the blunt half; <see cref="ScopePattern"/>s are the precise half, and they are stored beside it.
/// </summary>
/// <param name="MaxFiles">The most files one attempt may change, or null for no stated ceiling.</param>
public sealed record ChangeBudget(int? MaxFiles)
{
    /// <summary>No stated budget, which is what an item carries until someone sets one.</summary>
    public static ChangeBudget None { get; } = new((int?)null);

    /// <summary>True when nothing about the budget has been said, so it never needs storing.</summary>
    public bool IsEmpty => MaxFiles is null;
}

/// <summary>
/// One step of a test scenario: what to do and what must then be true. <paramref name="Data"/> is deliberately
/// open JSON (plan section 5.2): what an executable step needs is spike 78's and item 55's to decide, and
/// guessing its shape now would be a schema nobody can honour.
/// </summary>
/// <param name="Command">
/// What to run to check this step, or null when a person checks it (backlog 55). A step that carries one
/// is a runnable check; a step that does not is still a step, and a run that reaches it reports it as not
/// run rather than as passed, because nobody checked it.
/// </param>
public sealed record ScenarioStep(
    string Id, string Action, string Expected, string? Data = null, string? Command = null)
{
    /// <summary>True when this step can be checked by running something.</summary>
    public bool IsRunnable => !string.IsNullOrWhiteSpace(Command);
}

/// <summary>A named, ordered test scenario: the verifier's executable script (vision 5.3, Verification).</summary>
public sealed record TestScenario(string Id, string Title, IReadOnlyList<ScenarioStep> Steps)
{
    /// <summary>A scenario with no steps yet, which is a legitimate half-written one.</summary>
    public static TestScenario Named(string title) => new(AgentLayerEntry.NewId(), title, []);
}
