using Fravashi.Application.Backup;
using Fravashi.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Fravashi.Infrastructure.Backup;

/// <summary>
/// Wires the studio's half of backup/export (backlog 79). Register AFTER <c>AddFravashiPersistence</c>
/// (the writer needs the connection factory and the provider store) and <c>AddUpdateNotifications</c>
/// (the manifest names the version the backup was taken by).
/// <para>
/// There is no restore service here on purpose: restoring needs the studio stopped, so it is the
/// launcher's <c>fravashi restore</c> verb and not a button inside the process being replaced.
/// </para>
/// </summary>
public static class BackupServiceCollectionExtensions
{
    public static IServiceCollection AddStudioBackup(this IServiceCollection services)
    {
        services.AddSingleton<IStudioBackupWriter>(sp => new StudioBackupWriter(
            sp.GetRequiredService<DataDirectory>(),
            sp.GetRequiredService<ISqliteConnectionFactory>(),
            sp.GetRequiredService<Application.Updates.IProductInfo>(),
            sp.GetRequiredService<Application.Persistence.IProviderConnectionStore>()));

        return services;
    }
}
