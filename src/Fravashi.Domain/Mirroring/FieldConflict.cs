using Fravashi.Domain.WorkItems;

namespace Fravashi.Domain.Mirroring;

/// <summary>What comparing one field across a sync boundary turns out to mean.</summary>
public enum ConflictVerdict
{
    /// <summary>Neither side moved since the last reconciliation.</summary>
    Unchanged,

    /// <summary>Only we changed it, so the provider has to be told.</summary>
    LocalOnly,

    /// <summary>Only the provider changed it, so our copy follows.</summary>
    ProviderOnly,

    /// <summary>Both changed it and landed on the same value, so there is nothing to argue about.</summary>
    Converged,

    /// <summary>Both changed it to different values. A person decides; nothing is overwritten.</summary>
    Conflict,

    /// <summary>
    /// The field is Fravashi's, so a sync has no opinion about it at all (ADR 0018 decision 5:
    /// Fravashi-owned fields are never overwritten by a sync).
    /// </summary>
    NotTheProviders,
}

/// <summary>Which side a person chose when settling a conflict.</summary>
public enum ConflictChoice
{
    /// <summary>Our value holds and the provider is told.</summary>
    Local,

    /// <summary>The provider's value holds and our copy follows.</summary>
    Provider,
}

/// <summary>
/// One provider-owned field that changed on both sides since the last reconciliation, recorded rather than
/// resolved (ADR 0018 decision 5: "conflict is surfaced, never silently resolved. 'The provider wins'
/// without qualification is data loss wearing the costume of a policy").
/// <para>
/// All three values are kept, including the BASE, because without the value as it stood at the last sync a
/// reader cannot tell which side actually moved, and a conflict nobody can interpret is a conflict nobody
/// will settle.
/// </para>
/// </summary>
/// <param name="Id">The conflict's own id, so a resolution can name exactly one.</param>
/// <param name="Item">The item the field belongs to.</param>
/// <param name="Field">The <see cref="WorkItem"/> property name, which must be provider ownable.</param>
/// <param name="BaseValue">The value as it stood at the last reconciliation.</param>
/// <param name="LocalValue">What it says here now.</param>
/// <param name="ProviderValue">What the provider says now.</param>
/// <param name="DetectedUtc">When the disagreement was noticed.</param>
/// <param name="Choice">What a person decided, or null while it is still open.</param>
/// <param name="ResolvedUtc">When they decided, or null while it is still open.</param>
public sealed record FieldConflict(
    string Id,
    WorkItemId Item,
    string Field,
    string? BaseValue,
    string? LocalValue,
    string? ProviderValue,
    DateTimeOffset DetectedUtc,
    ConflictChoice? Choice,
    DateTimeOffset? ResolvedUtc)
{
    /// <summary>
    /// Something a person needs to know about this disagreement that is not one of the three values: why a
    /// move was refused, which of several names a status could be sent as. It exists so the three value
    /// slots stay VALUES. They used to carry a sentence when there was no value to put there, which read
    /// badly in the question and, worse, is what settling now rebases the agreed value onto.
    /// </summary>
    public string? Note { get; init; }

    /// <summary>True while the disagreement is still waiting for a person.</summary>
    public bool IsOpen => Choice is null;

    /// <summary>The disagreement stated as a question, which is the only form a person can answer.</summary>
    public string Question(string connection) =>
        $"{Field} says '{Show(LocalValue)}' here and '{Show(ProviderValue)}' on {connection}"
        + (BaseValue is null ? "." : $"; it was '{Show(BaseValue)}' when they last agreed.")
        + (Note is { Length: > 0 } note ? " " + note : string.Empty);

    private static string Show(string? value) => value is { Length: > 0 } ? value : "nothing";
}

/// <summary>
/// Comparing one field across a sync boundary. Pure, so the rule can be argued with directly rather than
/// inferred from what a sync happened to do.
/// </summary>
public static class ConflictDetection
{
    /// <summary>
    /// What the three values mean together. A Fravashi-owned field returns
    /// <see cref="ConflictVerdict.NotTheProviders"/> without comparing anything, because a sync has no
    /// standing over it and silently treating an absent provider value as a deletion is how an agent field
    /// set gets erased by a tracker that never had it.
    /// </summary>
    public static ConflictVerdict Compare(string field, string? baseValue, string? local, string? provider)
    {
        ArgumentException.ThrowIfNullOrEmpty(field);

        if (FieldOwnership.Of(field) == FieldOwner.FravashiOwned)
        {
            return ConflictVerdict.NotTheProviders;
        }

        var localMoved = !Same(baseValue, local);
        var providerMoved = !Same(baseValue, provider);

        if (!localMoved && !providerMoved)
        {
            return ConflictVerdict.Unchanged;
        }

        if (localMoved && !providerMoved)
        {
            return ConflictVerdict.LocalOnly;
        }

        if (!localMoved)
        {
            return ConflictVerdict.ProviderOnly;
        }

        // Both moved. Landing on the same value is agreement, not a fight, and asking a person to choose
        // between two identical answers would teach them to click through the conflict surface.
        return Same(local, provider) ? ConflictVerdict.Converged : ConflictVerdict.Conflict;
    }

    /// <summary>
    /// Whether two field values are the same. Null and empty are the same thing here: a tracker that
    /// returns an empty description for one that was never set has not changed it, and treating that as a
    /// move would manufacture a conflict on the first sync of half the items on a board.
    /// </summary>
    private static bool Same(string? left, string? right) =>
        string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);
}
