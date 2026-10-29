using Fravashi.Application.Updates;
using Microsoft.Extensions.DependencyInjection;

namespace Fravashi.Infrastructure.Updates;

/// <summary>
/// Registers the update-notification services (F1 / ADR 0006). Call after persistence and the agent runtimes
/// (needs <c>IConfigurationStore</c>, <c>IAgentRuntimeService</c>, <c>INotificationHistory</c>, and
/// <c>DataDirectory</c>). Two distinct kinds live here:
/// <list type="bullet">
///   <item>Type A - in-container CLI updates: the registry client, the availability service + its cache, and the
///   daily background check. These notify but never lock.</item>
///   <item>Type B - the product self-update: the app-side reader of the launcher-written critical/available state
///   (<see cref="FileProductUpdateStatus"/>); the launcher owns the update itself.</item>
/// </list>
/// It also registers <see cref="IProductInfo"/> - the single source of truth for the running version + variant.
/// </summary>
public static class UpdatesServiceCollectionExtensions
{
    public static IServiceCollection AddUpdateNotifications(this IServiceCollection services)
    {
        // The running product's own version + variant (embedded into the image at build time).
        services.AddSingleton<IProductInfo, ProductInfo>();

        // Type A - in-container tool updates.
        services.AddHttpClient<IPackageRegistry, NpmPackageRegistry>();
        services.AddSingleton<IToolUpdateService, ToolUpdateService>();
        services.AddHostedService<ToolUpdateCheckService>();

        // Type B - the app side of the product self-update: read the launcher's evaluated state.
        services.AddSingleton<IProductUpdateStatus, FileProductUpdateStatus>();
        return services;
    }
}
