namespace Fravashi.Application.Persistence;

/// <summary>
/// Key-value store for application configuration and settings, persisted in SQLite. Under ADR 0003 the
/// database is the single home for all runtime configuration. Keys use the .NET hierarchical separator
/// (e.g. <c>Tracker:Provider</c>, <c>Repository:Root</c>); the SQLite configuration provider surfaces
/// these values into <see cref="Microsoft.Extensions.Configuration.IConfiguration"/> at startup.
/// </summary>
public interface IConfigurationStore
{
    string? Get(string key);

    IReadOnlyDictionary<string, string?> GetAll();

    void Set(string key, string? value);

    void SetMany(IReadOnlyDictionary<string, string?> values);

    void Remove(string key);
}
