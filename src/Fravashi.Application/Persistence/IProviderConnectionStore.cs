namespace Fravashi.Application.Persistence;

/// <summary>Persists <see cref="ProviderConnection"/> records (source / wiki / board) in SQLite.</summary>
public interface IProviderConnectionStore
{
    IReadOnlyList<ProviderConnection> GetAll();

    IReadOnlyList<ProviderConnection> GetByKind(ProviderKind kind);

    ProviderConnection? Get(string id);

    void Upsert(ProviderConnection connection);

    void Remove(string id);
}
