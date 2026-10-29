using Fravashi.Domain.Wiki;

namespace Fravashi.Application.Wiki;

/// <summary>What a caller supplies to create a wiki item. The id, number, slug and stamps are the store's.</summary>
public sealed record NewWikiItem
{
    /// <summary>What kind of document this is.</summary>
    public required WikiItemType Type { get; init; }

    /// <summary>The title; mandatory.</summary>
    public required string Title { get; init; }

    /// <summary>The markdown body. May be empty.</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>Typed links.</summary>
    public IReadOnlyList<WikiLink> Links { get; init; } = [];

    /// <summary>The id of the document this one sits under, in the same project, or null for the top.</summary>
    public string? ParentId { get; init; }
}

/// <summary>A change of parent: the new parent's id, or null to move the document to the top.</summary>
public sealed record WikiParentChange(string? ParentId);

/// <summary>
/// A change to a wiki item. A field left null is not mentioned and stays as it is; <see cref="Links"/> is
/// replaced whole when given, because a list with no ids of its own has no other honest reading.
/// </summary>
public sealed record WikiItemEdit
{
    /// <summary>A new type.</summary>
    public WikiItemType? Type { get; init; }

    /// <summary>A new title. The slug follows it, so an item keeps a readable address.</summary>
    public string? Title { get; init; }

    /// <summary>A new body.</summary>
    public string? Body { get; init; }

    /// <summary>The whole link list.</summary>
    public IReadOnlyList<WikiLink>? Links { get; init; }

    /// <summary>A new place in the tree. Null leaves it where it is; a change carrying null moves it to the top.</summary>
    public WikiParentChange? Parent { get; init; }
}

/// <summary>Which wiki items a listing asks for. Everything left null matches everything.</summary>
public sealed record WikiItemQuery
{
    /// <summary>Only items of these types.</summary>
    public IReadOnlyCollection<WikiItemType>? Types { get; init; }

    /// <summary>A substring of the title or the body, ASCII case-insensitive.</summary>
    public string? Text { get; init; }

    /// <summary>At most this many, newest first. Defaults to 200.</summary>
    public int Limit { get; init; } = 200;
}

/// <summary>
/// The authored wiki (backlog 39): the document store for everything that is not code. It is a different
/// thing from <see cref="IWikiSyncService"/>'s mirror of Confluence and GitHub wiki, which keeps doing its
/// own job; item 83 decides what, if anything, flows between them.
/// <para>
/// A refusal is a <see cref="WikiResult{T}"/> carrying the model's codes, never an exception, so a form and
/// an agent both get something they can act on.
/// </para>
/// </summary>
public interface IWikiLibrary : IWikiDocuments
{
    /// <summary>The item with this number IN THIS PROJECT, or null. This is what <c>@n</c> resolves through.</summary>
    Task<WikiItem?> GetByNumberAsync(int number, CancellationToken ct = default);

    /// <summary>The item with this slug in this project, or null.</summary>
    Task<WikiItem?> GetBySlugAsync(string slug, CancellationToken ct = default);

    /// <summary>This project's items matching the query, newest first.</summary>
    Task<IReadOnlyList<WikiItem>> ListAsync(WikiItemQuery query, CancellationToken ct = default);

    /// <summary>
    /// Every wiki item of this project that points at something, whether it said so in its body or in a typed
    /// link. This is what makes a link navigable in both directions without anyone maintaining the other end by
    /// hand. A reference is a number, and a number means something only inside a project.
    /// </summary>
    Task<IReadOnlyList<WikiItem>> BacklinksAsync(WikiReference target, CancellationToken ct = default);

    /// <summary>Creates an item in this project, allocating its number and a slug that is free there.</summary>
    Task<WikiResult<WikiItem>> CreateAsync(NewWikiItem item, CancellationToken ct = default);
}

/// <summary>
/// A wiki document addressed by its ID (backlog 121). An id is unique across every project, so whoever holds
/// one needs no project to use it, and this narrow view is what they are given: it cannot look anything up by
/// number or slug, list, or create, which are the acts that mean something only inside a project. That is the
/// compiler holding the rule that the publisher, the summariser and the gates never reach into a project nobody
/// named. A change keeps the document in the project it is in.
/// </summary>
public interface IWikiDocuments
{
    /// <summary>The item with this id, or null.</summary>
    Task<WikiItem?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// The item with this id as it stood at a specific version (backlog 209): its title and body as last
    /// saved at that version, with everything else (links, parent, slug) read from the document as it is
    /// now, because those are structure rather than the content a reader asked an old version of. Null when
    /// there is no such document, the version asked for is higher than the one it is at, or no snapshot of
    /// that exact version survived (a document saved before this existed and not touched since).
    /// </summary>
    Task<WikiItem?> GetVersionAsync(string id, long version, CancellationToken ct = default);

    /// <summary>Changes an item. A stale version is refused with <c>conflict</c> rather than overwriting.</summary>
    Task<WikiResult<WikiItem>> UpdateAsync(
        string id, WikiItemEdit edit, long expectedVersion, CancellationToken ct = default);

    /// <summary>Deletes an item. True when one was deleted, false when there was none.</summary>
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// The project the document is in, or null when there is no such document. For what acts on a document by id and
    /// still has to honour a project's own settings (backlog 142: a project that publishes to no wiki).
    /// </summary>
    Task<Fravashi.Domain.Projects.ProjectId?> ProjectOfAsync(string id, CancellationToken ct = default);
}

/// <summary>
/// The wiki, a project at a time (backlog 121), shaped like <c>ITrackerResolver</c> on purpose: the library a
/// caller gets is bound to the project it named, so nothing downstream can forget to say which.
/// </summary>
public interface IWikiLibraries
{
    /// <summary>The wiki of <paramref name="project"/>.</summary>
    IWikiLibrary For(Fravashi.Domain.Projects.ProjectId project);

    /// <summary>Documents by id, in whatever project they are.</summary>
    IWikiDocuments Documents { get; }
}
