using Fravashi.Application.Framework;
using Microsoft.Extensions.DependencyInjection;

namespace Fravashi.Infrastructure.Framework;

/// <summary>
/// Registers the Framework services that are not part of the persistence layer: the model-list probe and the
/// probe-then-catalog resolver (D03-12). The <see cref="IFrameworkArtifactStore"/> itself is registered with
/// the SQLite persistence layer.
/// </summary>
public static class FrameworkServiceCollectionExtensions
{
    public static IServiceCollection AddFrameworkArtifacts(this IServiceCollection services)
    {
        services.AddSingleton<IModelListProbe, CliModelListProbe>();
        services.AddSingleton<IModelListResolver, ModelListResolver>();
        return services;
    }
}
