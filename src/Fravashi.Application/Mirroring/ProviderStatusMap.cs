using Fravashi.Application.Onboarding;
using Fravashi.Domain.WorkItems;

namespace Fravashi.Application.Mirroring;

/// <summary>How a provider's status was understood, most confident first.</summary>
public enum StatusReading
{
    /// <summary>The provider said nothing about status. Not the same as saying something unrecognised.</summary>
    NotSaid,

    /// <summary>A name the person mapped for this board in onboarding.</summary>
    Configured,

    /// <summary>
    /// A name the person mapped under more than one status (backlog 181), so what it means here is theirs to say
    /// and nothing moves. Only a mapping stored before confirming refused that can hold one.
    /// </summary>
    Ambiguous,

    /// <summary>A name the catalog ships as an example of a canonical status.</summary>
    Catalog,

    /// <summary>Nothing recognised the name, so the provider's own coarse CATEGORY placed it.</summary>
    Category,

    /// <summary>Nothing recognised the name and the provider gave no category either.</summary>
    Unknown,
}

/// <summary>How the reverse map answered: what this board's status is called on the provider (backlog 48).</summary>
public enum ReverseReading
{
    /// <summary>Exactly one native name is mapped to this status, so there is nothing to ask.</summary>
    One,

    /// <summary>Several are. The board's own names are offered and which one is meant is the person's to say.</summary>
    Several,

    /// <summary>None is. This product's example names are offered, and none of them is applied on its own.</summary>
    Unmapped,
}

/// <summary>
/// What this board's status is called on the provider (backlog 48). Our seven statuses are a PROJECTION, so
/// the map back out of them is one to many by nature: three of somebody's states can all read as Done here,
/// and which of them "move it to Done" means is a thing only they know.
/// </summary>
/// <param name="How">Whether there is one answer, several, or none.</param>
/// <param name="Candidates">The names to offer, in the order the person wrote them.</param>
public sealed record ReverseMapping(ReverseReading How, IReadOnlyList<string> Candidates)
{
    /// <summary>The one name to send, or null when somebody has to choose first.</summary>
    public string? Settled => How == ReverseReading.One ? Candidates[0] : null;
}

/// <summary>What a provider's status means here, and how that was decided.</summary>
/// <param name="Status">The canonical status, or null when nothing can be said and the item stays where it is.</param>
/// <param name="How">Which rule decided it.</param>
/// <param name="Said">A sentence for a person when the reading was less than exact; null when it was exact.</param>
public sealed record StatusTranslation(WorkItemStatus? Status, StatusReading How, string? Said);

/// <summary>
/// The per-board status mapping (backlog 47), read rather than restated: onboarding has collected
/// <see cref="BoardStatusMapping"/> since item 37 and nothing consumed it, so a board that followed a provider
/// had no way to know what "Awaiting sign-off" meant.
/// <para>
/// Three rules in order, and the order is the point. What the PERSON configured wins, because a board's own
/// vocabulary is theirs to declare. The shipped CATALOG of common names comes next, so an install that mapped
/// two statuses is not blind to the rest. Last, an unrecognised name degrades to the provider's own coarse
/// CATEGORY, which is this item's acceptance in one line: an unknown native status degrades to its category
/// rather than breaking.
/// </para>
/// <para>
/// The degrade is deliberately conservative. It moves an item only when the provider's category disagrees with
/// where the item already is: a provider that says "somewhere in progress" about an item this board has In
/// Review has contradicted nothing, and moving it to In Progress would throw away a distinction nobody
/// questioned. With no category either, NOTHING moves and the pass reports it, because guessing which of three
/// proposed states an unknown name means is worse than saying it is not understood.
/// </para>
/// </summary>
public sealed class ProviderStatusMap
{
    private readonly Dictionary<string, WorkItemStatus> _configured = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<WorkItemStatus, List<string>> _native = [];
    private readonly Dictionary<string, List<WorkItemStatus>> _ambiguous = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, WorkItemStatus> Catalog = BuildCatalog();

    private ProviderStatusMap(BoardStatusMapping? mapping)
    {
        foreach (var entry in mapping?.Entries ?? [])
        {
            if (Canonical(entry.CanonicalStatus) is not { } status || string.IsNullOrWhiteSpace(entry.NativeStatus))
            {
                continue;
            }

            // The mapping step's "several states" mode stores one entry per status with its names joined by "; ".
            // Read as one name, "Done; Closed; Completed" was sent to Azure DevOps as a state called that and refused
            // (seen moving this repository's project there, 2026-09-24); a board mapped one name per status never
            // showed it. Each name is its own.
            foreach (var native in entry.NativeStatus.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                // A name listed under two statuses used to read back as whichever entry came last, which is a
                // silent pick (backlog 181). Every status it is listed under is kept so the question can name them.
                if (_configured.TryGetValue(native, out var earlier) && earlier != status)
                {
                    var under = _ambiguous.TryGetValue(native, out var seen) ? seen : _ambiguous[native] = [earlier];
                    if (!under.Contains(status))
                    {
                        under.Add(status);
                    }
                }
                else
                {
                    _configured[native] = status;
                }

                // Every name is kept, not just the first: the reverse map is one to many and the extras are
                // exactly what makes it a question. The same name written twice is still one name.
                var names = _native.TryGetValue(status, out var known) ? known : _native[status] = [];
                if (!names.Contains(native, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(native);
                }
            }
        }
    }

    /// <summary>The map for a board's stored mapping; null means nothing was configured, which is not an error.</summary>
    public static ProviderStatusMap From(BoardStatusMapping? mapping) => new(mapping);

    /// <summary>What a provider's status means for an item that is currently at <paramref name="current"/>.</summary>
    public StatusTranslation Translate(string? native, string? category, WorkItemStatus current)
    {
        if (string.IsNullOrWhiteSpace(native))
        {
            return new StatusTranslation(null, StatusReading.NotSaid, null);
        }

        var name = native.Trim();

        // Before the catalog and the category: the person DID list this name, twice over, and falling through to
        // a guess would be the same silent pick in another form. Nothing moves and the question names both sides.
        if (_ambiguous.TryGetValue(name, out var listedUnder))
        {
            return new StatusTranslation(
                null,
                StatusReading.Ambiguous,
                $"'{name}' is listed under {string.Join(" and ", listedUnder.Select(Display))} in this board's "
                + "mapping, so what it means here is yours to say and the item was left where it is. List it under "
                + "one status in Settings > Project > Board and status mapping.");
        }

        if (_configured.TryGetValue(name, out var configured))
        {
            return new StatusTranslation(configured, StatusReading.Configured, null);
        }

        if (Catalog.TryGetValue(name, out var known))
        {
            return new StatusTranslation(known, StatusReading.Catalog, null);
        }

        if (CategoryOf(category) is not { } coarse)
        {
            return new StatusTranslation(
                null,
                StatusReading.Unknown,
                $"'{name}' is not a status this board knows, and the provider did not say which kind of state it "
                + "is, so the item was left where it is. Map it under Settings > Project > Board and status mapping.");
        }

        var settled = current.Category() == coarse ? current : Representative(coarse);
        return new StatusTranslation(
            settled,
            StatusReading.Category,
            $"'{name}' is not a status this board knows. The provider calls it {coarse}, so the item is read as "
            + $"{StableNames.ToName(settled)}. Map it under Settings > Project > Board and status mapping to be exact.");
    }

    /// <summary>
    /// The one native name to ask a provider for when this board moves an item, or NULL when that cannot be
    /// answered without a person: several names are mapped to this status, or none is. Answering anyway is
    /// what backlog 48 forbids, and the reason is not tidiness. The names on the other side belong to
    /// somebody's workflow, and picking one of three, or guessing from a catalog of examples this product
    /// ships, moves their work into a state they did not choose and cannot see us choosing.
    /// </summary>
    public string? ToNative(WorkItemStatus status) => ReverseMap(status).Settled;

    /// <summary>
    /// What this board's status is called on the provider, with everything a person needs to settle it when
    /// there is more than one answer. Unmapped offers the catalog's examples, which are named as examples
    /// wherever they are shown: they are what this product would suggest, not what that board uses.
    /// </summary>
    public ReverseMapping ReverseMap(WorkItemStatus status)
    {
        if (_native.TryGetValue(status, out var configured) && configured.Count > 0)
        {
            return new ReverseMapping(
                configured.Count == 1 ? ReverseReading.One : ReverseReading.Several, configured.ToArray());
        }

        return new ReverseMapping(ReverseReading.Unmapped, Examples(status));
    }

    /// <summary>
    /// The canonical status that stands for a whole category when nothing finer is known. Proposed is read as
    /// Backlog rather than Triage: an item the provider is tracking has been considered by somebody, and
    /// Triage would say the opposite.
    /// </summary>
    private static WorkItemStatus Representative(StatusCategory category) => category switch
    {
        StatusCategory.Proposed => WorkItemStatus.Backlog,
        StatusCategory.InProgress => WorkItemStatus.InProgress,
        StatusCategory.Done => WorkItemStatus.Done,
        _ => WorkItemStatus.Cancelled,
    };

    /// <summary>
    /// A provider's own coarse category, by the names the two providers this product targets use for them
    /// (Jira's statusCategory keys and names, Azure DevOps' state categories), or null when it said nothing
    /// this recognises. Unrecognised is never silently read as Proposed: that would move finished work back.
    /// </summary>
    private static StatusCategory? CategoryOf(string? category) => category?.Trim().ToUpperInvariant() switch
    {
        "NEW" or "TO DO" or "TODO" or "PROPOSED" or "OPEN" => StatusCategory.Proposed,
        "INDETERMINATE" or "IN PROGRESS" or "INPROGRESS" or "DOING" or "ACTIVE" or "RESOLVED" => StatusCategory.InProgress,
        "DONE" or "COMPLETE" or "COMPLETED" or "CLOSED" => StatusCategory.Done,
        "REMOVED" or "CANCELLED" or "CANCELED" => StatusCategory.Cancelled,
        _ => null,
    };

    /// <summary>The canonical status a mapping entry names, by the catalog's display name.</summary>
    private static WorkItemStatus? Canonical(string? displayName) =>
        WorkItemStatuses.All.FirstOrDefault(
            s => string.Equals(Display(s), displayName?.Trim(), StringComparison.OrdinalIgnoreCase)) is var found
            && string.Equals(Display(found), displayName?.Trim(), StringComparison.OrdinalIgnoreCase)
            ? found
            : null;

    /// <summary>The catalog's examples for a status: what this product would suggest a board might call it.</summary>
    private static IReadOnlyList<string> Examples(WorkItemStatus status) =>
        BoardStatusMapping.Catalog.FirstOrDefault(c => string.Equals(c.Status, Display(status), StringComparison.Ordinal))
            ?.NativeExamples.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        ?? [];

    private static string Display(WorkItemStatus status) => status switch
    {
        WorkItemStatus.InProgress => "In Progress",
        WorkItemStatus.InReview => "In Review",
        _ => status.ToString(),
    };

    private static Dictionary<string, WorkItemStatus> BuildCatalog()
    {
        var names = new Dictionary<string, WorkItemStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var canonical in BoardStatusMapping.Catalog)
        {
            var status = WorkItemStatuses.All.FirstOrDefault(
                s => string.Equals(Display(s), canonical.Status, StringComparison.Ordinal));
            foreach (var example in canonical.NativeExamples.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                // First writer wins: the catalog is ordered from the earliest state to the latest, so a name
                // two rows claim ("Done" is an example of both Backlog's row and Done's? it is not, but the
                // property holds for any future overlap) reads as the earlier one rather than flipping.
                names.TryAdd(example, status);
            }

            names.TryAdd(canonical.Status, status);
        }

        return names;
    }
}
