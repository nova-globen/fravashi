namespace Fravashi.Domain.WorkItems;

/// <summary>Who is authoritative for a work-item field once a provider is configured (ADR 0018 decision 4).</summary>
public enum FieldOwner
{
    /// <summary>A field a configured provider genuinely supports and is then authoritative for.</summary>
    ProviderOwnable,

    /// <summary>A field no external tracker has, or that Fravashi records about the item; always Fravashi's.</summary>
    FravashiOwned,
}

/// <summary>
/// The field ownership split, declared as data in the model (plan section 1.6) so item 82 does not have to
/// invent it. On the local board every field is Fravashi's in practice; the classification starts to matter
/// when item 47 configures a provider. Every public property of <see cref="WorkItem"/> must appear here, and
/// a test enforces that by reflection.
/// </summary>
public static class FieldOwnership
{
    /// <summary>
    /// The classification, keyed by <see cref="WorkItem"/> property name.
    /// <para>
    /// The plan names title, description, level and kind, parent, assignee, priority and status as provider
    /// ownable. The category follows the status it is derived from. Resolution and the two flags are classified
    /// provider ownable as well, because ADR 0018 gives the provider "the human-facing lifecycle and its
    /// transitions" and both are provider data (a Jira resolution, ADO's Blocked field, plan section 1.4).
    /// Identity, board order, the spike timebox, the external reference, the version and the timestamps are
    /// what Fravashi records about the item.
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, FieldOwner> Classification { get; } = new Dictionary<string, FieldOwner>(StringComparer.Ordinal)
    {
        [nameof(WorkItem.Id)] = FieldOwner.FravashiOwned,
        [nameof(WorkItem.Project)] = FieldOwner.FravashiOwned,
        [nameof(WorkItem.Number)] = FieldOwner.FravashiOwned,
        [nameof(WorkItem.Level)] = FieldOwner.ProviderOwnable,
        [nameof(WorkItem.Kind)] = FieldOwner.ProviderOwnable,
        [nameof(WorkItem.Title)] = FieldOwner.ProviderOwnable,
        [nameof(WorkItem.Description)] = FieldOwner.ProviderOwnable,
        [nameof(WorkItem.Parent)] = FieldOwner.ProviderOwnable,
        [nameof(WorkItem.Status)] = FieldOwner.ProviderOwnable,
        [nameof(WorkItem.Category)] = FieldOwner.ProviderOwnable,
        [nameof(WorkItem.Resolution)] = FieldOwner.ProviderOwnable,
        [nameof(WorkItem.Flags)] = FieldOwner.ProviderOwnable,

        // Backlog 298 (ADR 0031 decision 1): never mirrored, since no provider has a concept of it.
        [nameof(WorkItem.MarkedForAutopilot)] = FieldOwner.FravashiOwned,
        [nameof(WorkItem.Priority)] = FieldOwner.ProviderOwnable,
        [nameof(WorkItem.Assignee)] = FieldOwner.ProviderOwnable,
        [nameof(WorkItem.Rank)] = FieldOwner.FravashiOwned,
        [nameof(WorkItem.SpikeTimebox)] = FieldOwner.FravashiOwned,
        [nameof(WorkItem.External)] = FieldOwner.FravashiOwned,
        [nameof(WorkItem.Version)] = FieldOwner.FravashiOwned,
        [nameof(WorkItem.CreatedUtc)] = FieldOwner.FravashiOwned,
        [nameof(WorkItem.UpdatedUtc)] = FieldOwner.FravashiOwned,

        // The agent layer (increment C1) is Fravashi's throughout, and that is the point of owning the model:
        // no external tracker carries an addressable acceptance criterion, a readiness checklist with an
        // ambiguity score, a curated context bundle, a change budget, a design reference or an executable
        // scenario. A provider that grows one of these later gets reclassified deliberately, item by item.
        [nameof(WorkItem.AcceptanceCriteria)] = FieldOwner.FravashiOwned,
        [nameof(WorkItem.Readiness)] = FieldOwner.FravashiOwned,
        [nameof(WorkItem.Ambiguity)] = FieldOwner.FravashiOwned,
        [nameof(WorkItem.PersonBlockers)] = FieldOwner.FravashiOwned,
        [nameof(WorkItem.ActivePersonBlockers)] = FieldOwner.FravashiOwned,
        [nameof(WorkItem.Refs)] = FieldOwner.FravashiOwned,
        [nameof(WorkItem.ChangeBudget)] = FieldOwner.FravashiOwned,
        [nameof(WorkItem.ScopePatterns)] = FieldOwner.FravashiOwned,
        [nameof(WorkItem.NextAction)] = FieldOwner.FravashiOwned,
        [nameof(WorkItem.Scenarios)] = FieldOwner.FravashiOwned,

        // Requested 2026-10-22 (model and effort per work item): what to run the work on is the studio's own
        // concern, and no external tracker has a field for it.
        [nameof(WorkItem.SuggestedModel)] = FieldOwner.FravashiOwned,
        [nameof(WorkItem.SuggestedEffort)] = FieldOwner.FravashiOwned,

        // Work item #368: which of this project's releases carried the item is the studio's own record.
        [nameof(WorkItem.ReleasedIn)] = FieldOwner.FravashiOwned,
    };

    /// <summary>Who owns the named field. An unclassified name is a programming fault and throws.</summary>
    public static FieldOwner Of(string propertyName) =>
        Classification.TryGetValue(propertyName, out var owner)
            ? owner
            : throw new ArgumentException($"'{propertyName}' is not a classified work item field.", nameof(propertyName));
}
