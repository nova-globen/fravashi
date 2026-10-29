using Fravashi.Application.Persistence;
using Microsoft.Extensions.Configuration;

namespace Fravashi.Infrastructure.Persistence;

/// <summary>
/// <see cref="IConfigurationReloader"/> that re-reads every configuration source via
/// <see cref="IConfigurationRoot.Reload"/>. After onboarding writes to the settings table this re-runs the
/// SQLite provider's <c>Load</c>, refreshing <see cref="IConfiguration"/> and every
/// <c>IOptionsMonitor&lt;T&gt;</c> binding. Registration-time reads (notably the tracker-provider
/// selection) are NOT re-run - those require <see cref="IApplicationRestarter"/>.
/// </summary>
public sealed class ConfigurationRootReloader : IConfigurationReloader
{
    private readonly IConfiguration _configuration;

    public ConfigurationRootReloader(IConfiguration configuration) => _configuration = configuration;

    public void Reload()
    {
        if (_configuration is IConfigurationRoot root)
        {
            root.Reload();
        }
    }
}
