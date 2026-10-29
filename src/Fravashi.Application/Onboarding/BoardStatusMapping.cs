namespace Fravashi.Application.Onboarding;

/// <summary>
/// The canonical category - the 4-value contract every board maps onto losslessly
/// (the board migration study of 2026-06-19, section 5.2, naming-conventions "Category").
/// </summary>
public enum CanonicalCategory
{
    Proposed,
    InProgress,
    Done,
    Cancelled,
}

/// <summary>
/// One canonical status in the 7-value lifecycle (report §5.2, naming-conventions "Status"). Each status
/// belongs to exactly one <see cref="CanonicalCategory"/> and carries the native names it commonly maps
/// from, shown to the user in the O15 mapping step.
/// </summary>
/// <param name="Status">The canonical status name (one of the fixed seven).</param>
/// <param name="Category">The category this status folds into on a 4-bucket board.</param>
/// <param name="NativeExamples">Native board statuses that commonly map to this one (guidance for the user).</param>
public sealed record CanonicalStatus(string Status, CanonicalCategory Category, string NativeExamples);

/// <summary>
/// One row of the user's confirmed board-status mapping: a native board status name mapped to a canonical
/// status. Persisted during O15 so sync is deterministic (report §5).
/// </summary>
/// <param name="CanonicalStatus">The canonical status this native value maps to.</param>
/// <param name="NativeStatus">The native board status name the user's board uses.</param>
public sealed record StatusMappingEntry(string CanonicalStatus, string NativeStatus);

/// <summary>
/// The confirmed board-structure and status mapping collected in O15. Structure is fixed by the report
/// (Tier ⊃ Feature ⊃ Increment ⊃ Task); only the status names differ per board, so the editable part is the
/// per-status native names.
/// </summary>
/// <param name="Provider">The board provider this mapping is for (or <c>Local</c>/<c>None</c>).</param>
/// <param name="Entries">The confirmed native→canonical status rows.</param>
public sealed record BoardStatusMapping(string Provider, IReadOnlyList<StatusMappingEntry> Entries)
{
    /// <summary>
    /// The fixed canonical status catalog (report §5.2). The onboarding step seeds the editable mapping
    /// from this and lets the user adjust the native names.
    /// </summary>
    public static IReadOnlyList<CanonicalStatus> Catalog { get; } =
    [
        new("Triage", CanonicalCategory.Proposed, "New, Open, Inbox"),
        new("Backlog", CanonicalCategory.Proposed, "Backlog, To Do, Approved, Accepted"),
        new("Ready", CanonicalCategory.Proposed, "Selected for Dev, Ready, Bet"),
        new("In Progress", CanonicalCategory.InProgress, "Active, Doing, Committed"),
        new("In Review", CanonicalCategory.InProgress, "Resolved, In Review, Testing, QA"),
        new("Done", CanonicalCategory.Done, "Closed, Completed, Shipped, Done"),
        new("Cancelled", CanonicalCategory.Cancelled, "Removed, Won't Do, Rejected, Duplicate"),
    ];

    /// <summary>A mapping seeded with the first native example per canonical status - the proposed default.</summary>
    public static BoardStatusMapping Default(string provider) =>
        new(provider, Catalog
            .Select(c => new StatusMappingEntry(c.Status, c.NativeExamples.Split(',')[0].Trim()))
            .ToList());

    /// <summary>
    /// Every native name listed under more than one canonical status (backlog 181), in the order first listed. Such
    /// a mapping says two things about one state, and reading it back would have to pick one of them, which backlog
    /// 48 forbids, so it is refused on confirm. Names are split on "; " the way the reader splits them and compared
    /// ignoring case; the same name twice under ONE status is one name and not a duplicate.
    /// </summary>
    public IReadOnlyList<DuplicateNativeStatus> Duplicates()
    {
        var listed = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var entry in Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.CanonicalStatus) || string.IsNullOrWhiteSpace(entry.NativeStatus))
            {
                continue;
            }

            var status = entry.CanonicalStatus.Trim();
            foreach (var native in entry.NativeStatus.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!listed.TryGetValue(native, out var statuses))
                {
                    listed[native] = statuses = [];
                    order.Add(native);
                }

                if (!statuses.Contains(status, StringComparer.OrdinalIgnoreCase))
                {
                    statuses.Add(status);
                }
            }
        }

        return order
            .Where(native => listed[native].Count > 1)
            .Select(native => new DuplicateNativeStatus(native, listed[native]))
            .ToList();
    }
}

/// <summary>A native name a mapping lists under more than one canonical status (backlog 181).</summary>
/// <param name="NativeStatus">The name, as first written.</param>
/// <param name="CanonicalStatuses">The statuses it is listed under, in the order they were listed.</param>
public sealed record DuplicateNativeStatus(string NativeStatus, IReadOnlyList<string> CanonicalStatuses)
{
    /// <summary>A sentence for a person naming the state and where it is listed.</summary>
    public string Sentence => $"'{NativeStatus}' is listed under {string.Join(" and ", CanonicalStatuses)}.";
}
