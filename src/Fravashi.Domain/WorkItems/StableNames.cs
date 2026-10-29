using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.WorkItems.AgentLayer;

namespace Fravashi.Domain.WorkItems;

/// <summary>
/// The stable lower-snake names every work-item enum is stored and exchanged under, in both directions (plan
/// section 5: enums are stored as names, never ordinals, so reordering or renaming a C# member cannot corrupt
/// data). The names are written out here rather than derived from member names for exactly that reason.
/// An unknown name is REFUSED, never defaulted.
/// </summary>
public static class StableNames
{
    private static readonly Dictionary<Type, IReadOnlyDictionary<Enum, string>> Registry = new()
    {
        [typeof(WorkItemLevel)] = Map(
            (WorkItemLevel.Tier, "tier"),
            (WorkItemLevel.Feature, "feature"),
            (WorkItemLevel.Increment, "increment"),
            (WorkItemLevel.Task, "task")),
        [typeof(WorkItemKind)] = Map(
            (WorkItemKind.Standard, "standard"),
            (WorkItemKind.Spike, "spike"),
            (WorkItemKind.Bug, "bug")),
        [typeof(WorkItemStatus)] = Map(
            (WorkItemStatus.Triage, "triage"),
            (WorkItemStatus.Backlog, "backlog"),
            (WorkItemStatus.Ready, "ready"),
            (WorkItemStatus.InProgress, "in_progress"),
            (WorkItemStatus.InReview, "in_review"),
            (WorkItemStatus.Done, "done"),
            (WorkItemStatus.Cancelled, "cancelled")),
        [typeof(ArtifactRefRole)] = Map(
            (ArtifactRefRole.Context, "context"),
            (ArtifactRefRole.Plan, "plan"),
            (ArtifactRefRole.Mockup, "mockup"),
            (ArtifactRefRole.Prototype, "prototype"),
            (ArtifactRefRole.ApprovedDesign, "approved_design")),
        [typeof(ArtifactRefKind)] = Map(
            (ArtifactRefKind.Wiki, "wiki"),
            (ArtifactRefKind.Adr, "adr"),
            (ArtifactRefKind.Decision, "decision"),
            (ArtifactRefKind.File, "file"),
            (ArtifactRefKind.PullRequest, "pull_request"),
            (ArtifactRefKind.Url, "url"),
            (ArtifactRefKind.Design, "design")),
        [typeof(EvidenceKind)] = Map(
            (EvidenceKind.TestRun, "test_run"),
            (EvidenceKind.Screenshot, "screenshot"),
            (EvidenceKind.Log, "log"),
            (EvidenceKind.Build, "build"),
            (EvidenceKind.Review, "review"),
            (EvidenceKind.Other, "other"),
            (EvidenceKind.NoEvidenceReason, "no_evidence_reason")),
        [typeof(EvidenceResult)] = Map(
            (EvidenceResult.Pass, "pass"),
            (EvidenceResult.Fail, "fail"),
            (EvidenceResult.Partial, "partial")),
        [typeof(ScopeMode)] = Map(
            (ScopeMode.Allowed, "allowed"),
            (ScopeMode.Forbidden, "forbidden")),
        // Startability's own codes (backlog 41): they are not stored anywhere, but they cross the wire to an
        // agent through the studio's tool server, and a code an agent acts on is a name we owe stability to
        // exactly as much as one in a column.
        [typeof(StartBlocker)] = Map(
            (StartBlocker.NotReady, "not_ready"),
            (StartBlocker.BlockedFlag, "blocked_flag"),
            (StartBlocker.DeferredFlag, "deferred_flag"),
            (StartBlocker.OpenBlocker, "open_blocker"),
            (StartBlocker.PersonBlocker, "person_blocker")),
        [typeof(StartAdvisory)] = Map(
            (StartAdvisory.ReadinessUnchecked, "readiness_unchecked"),
            (StartAdvisory.Ambiguous, "ambiguous"),
            (StartAdvisory.NoAcceptanceCriteria, "no_acceptance_criteria")),
        [typeof(StatusCategory)] = Map(
            (StatusCategory.Proposed, "proposed"),
            (StatusCategory.InProgress, "in_progress"),
            (StatusCategory.Done, "done"),
            (StatusCategory.Cancelled, "cancelled")),
        [typeof(WorkItemResolution)] = Map(
            (WorkItemResolution.Completed, "completed"),
            (WorkItemResolution.WontDo, "wont_do"),
            (WorkItemResolution.Duplicate, "duplicate"),
            (WorkItemResolution.CannotReproduce, "cannot_reproduce")),
        [typeof(WorkItemPriority)] = Map(
            (WorkItemPriority.Critical, "critical"),
            (WorkItemPriority.High, "high"),
            (WorkItemPriority.Medium, "medium"),
            (WorkItemPriority.Low, "low")),
        [typeof(WorkItemLinkType)] = Map(
            (WorkItemLinkType.Blocks, "blocks"),
            (WorkItemLinkType.BlockedBy, "blocked_by"),
            (WorkItemLinkType.Relates, "relates"),
            (WorkItemLinkType.Duplicates, "duplicates"),
            (WorkItemLinkType.DuplicatedBy, "duplicated_by"),
            (WorkItemLinkType.Supersedes, "supersedes"),
            (WorkItemLinkType.SupersededBy, "superseded_by")),
        [typeof(WorkItemActorKind)] = Map(
            (WorkItemActorKind.User, "user"),
            (WorkItemActorKind.Session, "session"),
            (WorkItemActorKind.System, "system")),
        [typeof(FieldOwner)] = Map(
            (FieldOwner.ProviderOwnable, "provider_ownable"),
            (FieldOwner.FravashiOwned, "fravashi_owned")),
        // A work item's suggested model and effort (requested 2026-10-22, model and effort per work item): stored
        // on work_items and exchanged with agents under the same names the agent CLI itself takes, so the stored
        // name, the tool's enum and the flag value cannot drift apart.
        [typeof(AgentModelAlias)] = Map(
            (AgentModelAlias.Haiku, "haiku"),
            (AgentModelAlias.Sonnet, "sonnet"),
            (AgentModelAlias.Opus, "opus")),
        [typeof(AgentEffort)] = Map(
            (AgentEffort.Low, "low"),
            (AgentEffort.Medium, "medium"),
            (AgentEffort.High, "high"),
            (AgentEffort.XHigh, "xhigh"),
            (AgentEffort.Max, "max")),
    };

    /// <summary>Every enum type that has stable names.</summary>
    public static IReadOnlyCollection<Type> EnumTypes => Registry.Keys;

    /// <summary>The stable name of a value. A value with no registered name is a programming fault and throws.</summary>
    public static string ToName<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        var names = NamesFor(typeof(TEnum));
        return names.TryGetValue(value, out var name)
            ? name
            : throw new ArgumentOutOfRangeException(nameof(value), value, $"{typeof(TEnum).Name} has no stable name for this value.");
    }

    /// <summary>
    /// Parses a stable name, exactly and ordinally. An unknown, blank or differently cased name is refused
    /// with <c>unknown_storage_name</c> concerning <paramref name="field"/>, never mapped to a default.
    /// </summary>
    public static DomainResult<TEnum> Parse<TEnum>(string? name, string field)
        where TEnum : struct, Enum
    {
        foreach (var (value, stable) in NamesFor(typeof(TEnum)))
        {
            if (string.Equals(stable, name, StringComparison.Ordinal))
            {
                return DomainResult.Ok((TEnum)value);
            }
        }

        return DomainResult.Refused<TEnum>(
        [
            new ValidationError(
                WorkItemRules.Codes.UnknownStorageName,
                field,
                $"'{name}' is not a stored name of {typeof(TEnum).Name}."),
        ]);
    }

    /// <summary>Every value of the enum with its stable name.</summary>
    public static IReadOnlyDictionary<TEnum, string> NamesOf<TEnum>()
        where TEnum : struct, Enum =>
        NamesFor(typeof(TEnum)).ToDictionary(p => (TEnum)p.Key, p => p.Value);

    /// <summary>Every value of a registered enum type with its stable name, untyped.</summary>
    public static IReadOnlyDictionary<Enum, string> NamesFor(Type enumType) =>
        Registry.TryGetValue(enumType, out var names)
            ? names
            : throw new ArgumentException($"{enumType.Name} has no stable names.", nameof(enumType));

    private static Dictionary<Enum, string> Map<TEnum>(params (TEnum Value, string Name)[] pairs)
        where TEnum : struct, Enum =>
        pairs.ToDictionary(p => (Enum)p.Value, p => p.Name);
}
