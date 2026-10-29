using Fravashi.Domain.Wiki;

namespace Fravashi.Application.Migration;

/// <summary>
/// What one file becomes when it is imported (item #373): work items, or a wiki document of a type. Stored and sent as
/// its <see cref="Name"/>: <c>work_items</c>, or the wiki type's own stable name (<c>plan</c>, <c>adr</c>, ...).
/// </summary>
public readonly record struct ImportTarget
{
    /// <summary>The name <see cref="WorkItems"/> is stored under.</summary>
    public const string WorkItemsName = "work_items";

    private ImportTarget(WikiItemType? documentType) => DocumentType = documentType;

    /// <summary>The file becomes work items (a backlog keeps its numbers; a checklist gives one item per box).</summary>
    public static ImportTarget WorkItems { get; } = new(null);

    /// <summary>The wiki type, or null for <see cref="WorkItems"/>.</summary>
    public WikiItemType? DocumentType { get; }

    /// <summary>True when the file becomes work items.</summary>
    public bool IsWorkItems => DocumentType is null;

    /// <summary>The file becomes a wiki document of <paramref name="type"/>.</summary>
    public static ImportTarget Document(WikiItemType type) => new(type);

    /// <summary>
    /// The wiki types a file can be imported as, in the order the preview's "Becomes" list offers them. Designs,
    /// prototypes and releases are left out: each has a body of its own shape that a markdown file is not.
    /// </summary>
    public static IReadOnlyList<WikiItemType> DocumentTypes { get; } =
    [
        WikiItemType.Plan, WikiItemType.Adr, WikiItemType.Reference, WikiItemType.Architecture, WikiItemType.Governance,
        WikiItemType.Evidence, WikiItemType.Draft, WikiItemType.Report, WikiItemType.Legal, WikiItemType.Feature,
    ];

    /// <summary>The stored name.</summary>
    public string Name => DocumentType is { } type ? WikiNames.ToName(type) : WorkItemsName;

    /// <summary>The words a person reads for it: "Work items", "Plan", "ADR".</summary>
    public string Label => DocumentType switch
    {
        null => "Work items",
        WikiItemType.Adr => "ADR",
        { } type => type.ToString(),
    };

    /// <summary>The target a stored name names, or null when it names none this list offers.</summary>
    public static ImportTarget? Parse(string? name)
    {
        if (string.Equals(name, WorkItemsName, StringComparison.Ordinal))
        {
            return WorkItems;
        }

        foreach (var type in DocumentTypes)
        {
            if (string.Equals(WikiNames.ToName(type), name, StringComparison.Ordinal))
            {
                return Document(type);
            }
        }

        return null;
    }
}

/// <summary>One file a person chose to import, and what it becomes.</summary>
public sealed record ImportChoice(string Path, ImportTarget Target);

/// <summary>A person's change to one row of the preview: whether it is checked, and what it becomes.</summary>
/// <param name="Include">Checked or not.</param>
/// <param name="Target">The stored name of what it becomes (<see cref="ImportTarget.Name"/>).</param>
public sealed record ImportOverride(bool Include, string Target);

/// <summary>
/// The files to import and what each becomes (item #373, <c>docs/plans/first-open-setup.md</c> section 3.5): the scan's
/// defaults with a person's changes laid over them. Only the paths are held; the text is read at import time, and only
/// for these files.
/// </summary>
public sealed record ImportSelection(IReadOnlyList<ImportChoice> Files)
{
    /// <summary>Nothing to import.</summary>
    public static ImportSelection None { get; } = new([]);

    /// <summary>True when nothing is chosen.</summary>
    public bool IsEmpty => Files.Count == 0;

    /// <summary>The files that become work items, in path order.</summary>
    public IEnumerable<ImportChoice> WorkItemFiles => Files.Where(f => f.Target.IsWorkItems).OrderBy(f => f.Path, StringComparer.Ordinal);

    /// <summary>The files that become documents, each with its type.</summary>
    public IReadOnlyDictionary<string, WikiItemType> DocumentTypes =>
        Files.Where(f => f.Target.DocumentType is not null)
            .ToDictionary(f => f.Path, f => f.Target.DocumentType!.Value, StringComparer.Ordinal);

    /// <summary>
    /// The scan's defaults with <paramref name="overrides"/> laid over them. A file too large to import is never chosen,
    /// whatever an override says; an override naming a target that does not parse keeps the row's own target.
    /// </summary>
    public static ImportSelection From(
        IEnumerable<ScanCandidate> candidates, IReadOnlyDictionary<string, ImportOverride>? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var chosen = new List<ImportChoice>();
        foreach (var candidate in candidates)
        {
            if (candidate.TooLarge)
            {
                continue;
            }

            var include = candidate.Include;
            var target = candidate.Suggested;
            if (overrides is not null && overrides.TryGetValue(candidate.Path, out var change))
            {
                include = change.Include;
                target = ImportTarget.Parse(change.Target) ?? target;
            }

            if (include)
            {
                chosen.Add(new ImportChoice(candidate.Path, target));
            }
        }

        return new ImportSelection(chosen);
    }
}
