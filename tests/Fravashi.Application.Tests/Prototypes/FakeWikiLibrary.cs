using Fravashi.Application.Wiki;
using Fravashi.Domain.Projects;
using Fravashi.Domain.Wiki;

namespace Fravashi.Application.Tests.Prototypes;

/// <summary>
/// The smallest <see cref="IWikiLibrary"/> that lets <see cref="PrototypeReader"/>'s tests give it a design
/// to resolve, with no SQLite and no real store: only <see cref="GetByNumberAsync"/> is ever called by the
/// reader, so that is the only member with real behaviour.
/// </summary>
internal sealed class FakeWikiLibrary : IWikiLibrary
{
    private readonly Dictionary<int, WikiItem> _items = new();

    /// <summary>Adds a document, numbered and versioned as given, for the reader to find.</summary>
    public FakeWikiLibrary With(int number, WikiItemType type, long version = 1, string title = "A document")
    {
        var created = WikiItem.Create(new WikiItemState
        {
            Id = $"id-{number}",
            Number = number,
            Type = type,
            Title = title,
            Slug = $"item-{number}",
            Version = version,
            CreatedUtc = DateTimeOffset.UnixEpoch,
            UpdatedUtc = DateTimeOffset.UnixEpoch,
        });

        _items[number] = created.Value;
        return this;
    }

    public Task<WikiItem?> GetByNumberAsync(int number, CancellationToken ct = default) =>
        Task.FromResult(_items.GetValueOrDefault(number));

    public Task<WikiItem?> GetBySlugAsync(string slug, CancellationToken ct = default) =>
        throw new NotSupportedException("Not needed by PrototypeReader.");

    public Task<IReadOnlyList<WikiItem>> ListAsync(WikiItemQuery query, CancellationToken ct = default) =>
        throw new NotSupportedException("Not needed by PrototypeReader.");

    public Task<IReadOnlyList<WikiItem>> BacklinksAsync(WikiReference target, CancellationToken ct = default) =>
        throw new NotSupportedException("Not needed by PrototypeReader.");

    public Task<WikiResult<WikiItem>> CreateAsync(NewWikiItem item, CancellationToken ct = default) =>
        throw new NotSupportedException("Not needed by PrototypeReader.");

    public Task<WikiItem?> GetAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_items.Values.FirstOrDefault(i => i.Id == id));

    public Task<WikiItem?> GetVersionAsync(string id, long version, CancellationToken ct = default) =>
        throw new NotSupportedException("Not needed by PrototypeReader.");

    public Task<WikiResult<WikiItem>> UpdateAsync(
        string id, WikiItemEdit edit, long expectedVersion, CancellationToken ct = default) =>
        throw new NotSupportedException("Not needed by PrototypeReader.");

    public Task<bool> DeleteAsync(string id, CancellationToken ct = default) =>
        throw new NotSupportedException("Not needed by PrototypeReader.");

    public Task<ProjectId?> ProjectOfAsync(string id, CancellationToken ct = default) =>
        throw new NotSupportedException("Not needed by PrototypeReader.");
}
