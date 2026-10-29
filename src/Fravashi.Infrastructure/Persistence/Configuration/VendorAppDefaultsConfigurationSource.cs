using Fravashi.Application.Onboarding.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;

namespace Fravashi.Infrastructure.Persistence.Configuration;

/// <summary>
/// Configuration source projecting the product's <b>shipped OAuth client ids</b>
/// (<see cref="VendorAppDefaults"/>) into the pipeline. Registered <b>below</b> the SQLite settings source so
/// a per-install (advanced) override always wins, and above nothing else - these are the baked-in defaults an
/// end user never has to supply. Only non-empty defaults are projected (an unregistered provider contributes
/// no key), so the honest "not available in this build" state is preserved. See ADR 0004.
/// </summary>
public sealed class VendorAppDefaultsConfigurationSource : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder)
        => new MemoryConfigurationProvider(new MemoryConfigurationSource
        {
            InitialData = VendorAppDefaults.AsConfiguration(),
        });
}
