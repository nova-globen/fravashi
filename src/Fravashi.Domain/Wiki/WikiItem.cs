using System.Globalization;
using System.Text;

namespace Fravashi.Domain.Wiki;

/// <summary>
/// What a wiki item IS (backlog 39). The wiki is the document store for everything that is not code, and the
/// type is what lets a reader, a query and a role-agent tell a plan from a legal note without reading it.
/// </summary>
public enum WikiItemType
{
    /// <summary>A plan an increment is executed under (ADR 0020's G2 reads these).</summary>
    Plan,

    /// <summary>An architecture decision record.</summary>
    Adr,

    /// <summary>A report: something measured, once, and written down.</summary>
    Report,

    /// <summary>A draft: thinking that is not a decision yet.</summary>
    Draft,

    /// <summary>Evidence that something works, or did.</summary>
    Evidence,

    /// <summary>How the system is built.</summary>
    Architecture,

    /// <summary>Rules the work is held to.</summary>
    Governance,

    /// <summary>What a feature is and why, for the people who did not build it.</summary>
    Feature,

    /// <summary>Licences, terms, obligations.</summary>
    Legal,

    /// <summary>A pointer to something outside, kept because it is needed again.</summary>
    Reference,

    /// <summary>The project's colours, logos and type. At most one per project (see the rules).</summary>
    Branding,

    /// <summary>
    /// A design: prose describing a screen, and ONE fenced block with the info string <c>mockup</c> holding its
    /// UI tree (ADR 0021, backlog 193). A broken tree is still saved and shown with its errors.
    /// </summary>
    Design,

    /// <summary>
    /// A prototype: a generated derivative of a design, never a second truth (ADR 0021 decision 6, backlog
    /// 211). Its body holds ONE fenced block with the info string <c>prototype</c>, a small JSON document
    /// naming the design it is a prototype OF, pinned to a version, and the overlay of direct edits kept on
    /// top of it. A broken document is still saved and shown with its errors.
    /// </summary>
    Prototype,

    /// <summary>
    /// A release's notes (work item #368): written when a release is saved, titled "Release vX.Y.Z", and linked to the
    /// release's work items. Publishing it anywhere is an explicit act, never a side effect of saving the release.
    /// </summary>
    Release,
}

/// <summary>What an explicit wiki link points at. The three numbered kinds mirror the reference syntax.</summary>
public enum WikiLinkKind
{
    /// <summary>A work item, by number.</summary>
    WorkItem,

    /// <summary>Another wiki item, by number.</summary>
    WikiItem,

    /// <summary>A pull request, by number.</summary>
    PullRequest,

    /// <summary>A file in a bound workspace, by repository-relative path.</summary>
    RepositoryFile,
}

/// <summary>
/// One typed link from a wiki item. References written in the BODY are links too and are derived from it
/// (<see cref="WikiReferences"/>); this is for the ones that are a fact about the item rather than a
/// sentence in it, and for repository files, which have no reference syntax because a path is not a number.
/// </summary>
/// <param name="Kind">What it points at.</param>
/// <param name="Target">A number for the numbered kinds, a repository-relative path for a file.</param>
/// <param name="Label">What to show instead of the raw target, or null.</param>
public sealed record WikiLink(WikiLinkKind Kind, string Target, string? Label = null)
{
    /// <summary>The link's number, for a numbered kind, or null.</summary>
    public int? Number =>
        Kind != WikiLinkKind.RepositoryFile
        && int.TryParse(Target, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
}

/// <summary>
/// A wiki item: a markdown document with a type, a number people reference it by, and typed links.
/// <para>
/// It cannot be constructed around its invariants. The constructor is private and every property is
/// get-only, so the only way in is <see cref="Create"/>, which checks them; a change is made by taking
/// <see cref="ToState"/>, changing it, and passing it back through. This is the same shape as
/// <c>WorkItem</c>, deliberately, because they are the two halves of one graph.
/// </para>
/// </summary>
public sealed record WikiItem
{
    private WikiItem(WikiItemState state)
    {
        Id = state.Id;
        Number = state.Number;
        Type = state.Type;
        Title = state.Title.Trim();
        Slug = state.Slug;
        Body = state.Body ?? string.Empty;
        Links = state.Links ?? [];
        ParentId = string.IsNullOrWhiteSpace(state.ParentId) ? null : state.ParentId;
        Version = state.Version;
        CreatedUtc = state.CreatedUtc;
        UpdatedUtc = state.UpdatedUtc;
    }

    /// <summary>The item's identity.</summary>
    public string Id { get; }

    /// <summary>The per-project number, referenced as <c>@n</c>.</summary>
    public int Number { get; }

    /// <summary>What kind of document this is.</summary>
    public WikiItemType Type { get; }

    /// <summary>The title; never blank.</summary>
    public string Title { get; }

    /// <summary>A url-safe name derived from the title, unique within the project.</summary>
    public string Slug { get; }

    /// <summary>The markdown body. May be empty: a document can exist before it says anything.</summary>
    public string Body { get; }

    /// <summary>Links that are a fact about the item rather than a sentence in it.</summary>
    public IReadOnlyList<WikiLink> Links { get; }

    /// <summary>
    /// The document this one sits under, by id, or null for one at the top. Documents form a tree the way the
    /// folders they are often imported from do (backlog 49), and a provider that nests pages is given the same tree.
    /// </summary>
    public string? ParentId { get; }

    /// <summary>The optimistic-concurrency version.</summary>
    public long Version { get; }

    /// <summary>When it was created.</summary>
    public DateTimeOffset CreatedUtc { get; }

    /// <summary>When it last changed.</summary>
    public DateTimeOffset UpdatedUtc { get; }

    /// <summary>How this item is referenced in anyone's prose.</summary>
    public WikiReference Reference => new(WikiReferenceKind.WikiItem, Number);

    /// <summary>The references written in the body, which are links the author made by writing them.</summary>
    public IReadOnlyList<WikiReference> BodyReferences =>
        [.. WikiReferences.Find(Body).Select(f => f.Reference).Distinct()];

    /// <summary>Builds an item after checking the invariants, or refuses with the reasons.</summary>
    public static WikiResult<WikiItem> Create(WikiItemState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var errors = new List<WikiError>();

        if (!Enum.IsDefined(state.Type))
        {
            errors.Add(new WikiError(WikiCodes.TypeUndefined, "type", "That is not a wiki item type."));
        }

        if (string.IsNullOrWhiteSpace(state.Title))
        {
            errors.Add(new WikiError(WikiCodes.TitleRequired, "title", "A wiki item needs a title."));
        }

        if (state.Number <= 0)
        {
            errors.Add(new WikiError(WikiCodes.NumberRequired, "number", "A wiki item needs a positive number."));
        }

        if (string.IsNullOrWhiteSpace(state.Slug))
        {
            errors.Add(new WikiError(WikiCodes.SlugRequired, "slug", "A wiki item needs a slug."));
        }

        foreach (var link in state.Links ?? [])
        {
            if (!Enum.IsDefined(link.Kind))
            {
                errors.Add(new WikiError(WikiCodes.LinkKindUndefined, "links", "That is not a link kind."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(link.Target))
            {
                errors.Add(new WikiError(WikiCodes.LinkTargetRequired, "links", "A link needs a target."));
                continue;
            }

            if (link.Kind != WikiLinkKind.RepositoryFile && link.Number is null or <= 0)
            {
                errors.Add(new WikiError(
                    WikiCodes.LinkNumberRequired,
                    "links",
                    $"A {link.Kind} link points at a positive number, not '{link.Target}'."));
            }

            // A link to a file is a path inside a repository, so it is relative and never climbs out. The
            // same reading as the agent's scope patterns: rooted, drive-lettered or any ".." segment is out.
            if (link.Kind == WikiLinkKind.RepositoryFile && !IsRootRelative(link.Target))
            {
                errors.Add(new WikiError(
                    WikiCodes.LinkPathNotRelative,
                    "links",
                    $"A file link is a path inside the repository; '{link.Target}' is not."));
            }
        }

        if (!string.IsNullOrWhiteSpace(state.ParentId) && state.ParentId == state.Id)
        {
            errors.Add(new WikiError(WikiCodes.ParentCycle, "parent", "A wiki item cannot sit under itself."));
        }

        // A wiki item cannot link to itself, in any reading: the link would render as a link to the page
        // the reader is already on, which is never what anyone meant.
        if ((state.Links ?? []).Any(l => l.Kind == WikiLinkKind.WikiItem && l.Number == state.Number))
        {
            errors.Add(new WikiError(WikiCodes.LinkSelf, "links", "A wiki item cannot link to itself."));
        }

        return errors.Count == 0
            ? WikiResult<WikiItem>.Ok(new WikiItem(state))
            : WikiResult<WikiItem>.Refused(errors);
    }

    /// <summary>The item's state, for changing it and passing it back through <see cref="Create"/>.</summary>
    public WikiItemState ToState() => new()
    {
        Id = Id,
        Number = Number,
        Type = Type,
        Title = Title,
        Slug = Slug,
        Body = Body,
        Links = Links,
        ParentId = ParentId,
        Version = Version,
        CreatedUtc = CreatedUtc,
        UpdatedUtc = UpdatedUtc,
    };

    /// <summary>
    /// A url-safe name from a title: lower case, ASCII letters and digits kept, everything else a hyphen,
    /// runs collapsed, ends trimmed. A title with nothing usable in it yields <c>item</c> rather than an
    /// empty string, because a slug is part of a URL and an empty one would make two items collide.
    /// </summary>
    public static string SlugFor(string? title)
    {
        var text = new StringBuilder();
        foreach (var c in (title ?? string.Empty).Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                text.Append(c);
            }
            else if (text.Length > 0 && text[^1] != '-')
            {
                text.Append('-');
            }
        }

        var slug = text.ToString().Trim('-');
        return slug.Length == 0 ? "item" : slug;
    }

    private static bool IsRootRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path[0] == '/' || path[0] == '\\')
        {
            return false;
        }

        if (path.Length > 1 && path[1] == ':')
        {
            return false;
        }

        // Split on BOTH separators whatever the platform: this string travels between machines, and a
        // segment that is ".." under either reading is one that climbs out on the machine that opens it.
        foreach (var segment in path.Split('/', '\\'))
        {
            if (segment == "..")
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>The settable state of a wiki item, for creating one and for changing one.</summary>
public sealed record WikiItemState
{
    /// <summary>The item's identity.</summary>
    public required string Id { get; init; }

    /// <summary>The per-project number.</summary>
    public required int Number { get; init; }

    /// <summary>What kind of document this is.</summary>
    public required WikiItemType Type { get; init; }

    /// <summary>The title.</summary>
    public required string Title { get; init; }

    /// <summary>The url-safe name.</summary>
    public required string Slug { get; init; }

    /// <summary>The markdown body.</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>Typed links.</summary>
    public IReadOnlyList<WikiLink> Links { get; init; } = [];

    /// <summary>The parent document's id, or null.</summary>
    public string? ParentId { get; init; }

    /// <summary>The version.</summary>
    public long Version { get; init; } = 1;

    /// <summary>When it was created.</summary>
    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>When it last changed.</summary>
    public DateTimeOffset UpdatedUtc { get; init; }
}
