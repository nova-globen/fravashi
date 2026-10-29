namespace Fravashi.Domain.Wiki;

/// <summary>
/// The stable lower-snake names the wiki's enums are stored and transmitted under (backlog 39), for the same
/// reason the tracker has its own: an ordinal in a column is a number that silently changes meaning the day
/// somebody adds a value in the middle of an enum.
/// <para>
/// A name that no value has is a corrupt database or a wrong wire message, so parsing throws rather than
/// defaulting. Nothing here is ever mapped to a "closest" value.
/// </para>
/// </summary>
public static class WikiNames
{
    private static readonly Dictionary<Type, Dictionary<object, string>> Registry = new()
    {
        [typeof(WikiItemType)] = Map(
            (WikiItemType.Plan, "plan"),
            (WikiItemType.Adr, "adr"),
            (WikiItemType.Report, "report"),
            (WikiItemType.Draft, "draft"),
            (WikiItemType.Evidence, "evidence"),
            (WikiItemType.Architecture, "architecture"),
            (WikiItemType.Governance, "governance"),
            (WikiItemType.Feature, "feature"),
            (WikiItemType.Legal, "legal"),
            (WikiItemType.Reference, "reference"),
            (WikiItemType.Branding, "branding"),
            (WikiItemType.Design, "design"),
            (WikiItemType.Prototype, "prototype"),
            (WikiItemType.Release, "release")),
        [typeof(WikiLinkKind)] = Map(
            (WikiLinkKind.WorkItem, "work_item"),
            (WikiLinkKind.WikiItem, "wiki_item"),
            (WikiLinkKind.PullRequest, "pull_request"),
            (WikiLinkKind.RepositoryFile, "repository_file")),
        [typeof(WikiReferenceKind)] = Map(
            (WikiReferenceKind.WorkItem, "work_item"),
            (WikiReferenceKind.WikiItem, "wiki_item"),
            (WikiReferenceKind.PullRequest, "pull_request")),
        [typeof(WikiPublicationState)] = Map(
            (WikiPublicationState.InFlight, "in_flight"),
            (WikiPublicationState.Published, "published"),
            (WikiPublicationState.NotSent, "not_sent"),
            (WikiPublicationState.Unresolved, "unresolved"),
            (WikiPublicationState.SiteChanged, "site_changed"),
            (WikiPublicationState.Conflict, "conflict"),
            (WikiPublicationState.Refused, "refused"),
            (WikiPublicationState.Gone, "gone")),
    };

    /// <summary>Every wiki enum that has stable names.</summary>
    public static IReadOnlyCollection<Type> EnumTypes => Registry.Keys;

    /// <summary>The stable name of a value.</summary>
    public static string ToName<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        NamesFor(typeof(TEnum)).TryGetValue(value, out var name)
            ? name
            : throw new ArgumentOutOfRangeException(
                nameof(value), value, $"{typeof(TEnum).Name} has no stable name for this value.");

    /// <summary>A stable name back to its value, exactly and ordinally.</summary>
    public static TEnum Parse<TEnum>(string? name)
        where TEnum : struct, Enum
    {
        foreach (var (value, stable) in NamesFor(typeof(TEnum)))
        {
            if (string.Equals(stable, name, StringComparison.Ordinal))
            {
                return (TEnum)value;
            }
        }

        throw new InvalidDataException($"'{name}' is not a stored name of {typeof(TEnum).Name}.");
    }

    /// <summary>Every value of an enum with its stable name.</summary>
    public static IReadOnlyDictionary<object, string> NamesFor(Type type) =>
        Registry.TryGetValue(type, out var names)
            ? names
            : throw new ArgumentOutOfRangeException(nameof(type), type, "That enum has no stable names.");

    private static Dictionary<object, string> Map<TEnum>(params (TEnum Value, string Name)[] pairs)
        where TEnum : struct, Enum =>
        pairs.ToDictionary(p => (object)p.Value, p => p.Name);
}
