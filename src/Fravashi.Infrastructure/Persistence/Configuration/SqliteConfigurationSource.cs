using Microsoft.Extensions.Configuration;

namespace Fravashi.Infrastructure.Persistence.Configuration;

/// <summary>Configuration source that binds the SQLite <c>settings</c> table into the pipeline.</summary>
public sealed class SqliteConfigurationSource : IConfigurationSource
{
    private readonly string _databasePath;

    public SqliteConfigurationSource(string databasePath) => _databasePath = databasePath;

    public IConfigurationProvider Build(IConfigurationBuilder builder)
        => new SqliteConfigurationProvider(_databasePath);
}
