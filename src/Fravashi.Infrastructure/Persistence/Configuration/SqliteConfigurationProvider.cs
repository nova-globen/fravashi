using Fravashi.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;

namespace Fravashi.Infrastructure.Persistence.Configuration;

/// <summary>
/// Loads the <c>settings</c> table into the .NET configuration pipeline. Because keys are stored with the
/// native <c>:</c> hierarchy separator (e.g. <c>Tracker:Provider</c>), every existing
/// <c>Configure&lt;T&gt;</c> / <c>configuration["..."]</c> binding keeps working unchanged - the studio's
/// adapters do not know their values now come from SQLite. Added after the appsettings/user-secrets
/// sources, so database values win. <see cref="Load"/> also bootstraps the schema, so the very first
/// access to configuration creates the database if it is absent (ADR 0003 V4: no startup ceremony).
/// </summary>
public sealed class SqliteConfigurationProvider : ConfigurationProvider
{
    private readonly string _databasePath;

    public SqliteConfigurationProvider(string databasePath) => _databasePath = databasePath;

    public override void Load()
    {
        var factory = new SqliteConnectionFactory(new SqliteStoreOptions(_databasePath));
        SqliteSchema.EnsureCreated(factory);

        var store = new SqliteConfigurationStore(factory);
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in store.GetAll())
        {
            // Skip internal bookkeeping keys (e.g. the schema-version marker) - they are not configuration.
            if (key.StartsWith("__", StringComparison.Ordinal))
            {
                continue;
            }

            data[key] = value;
        }

        Data = data;
    }
}
