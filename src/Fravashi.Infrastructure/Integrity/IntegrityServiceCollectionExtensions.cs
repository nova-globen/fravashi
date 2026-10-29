using Fravashi.Application.Integrity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Fravashi.Infrastructure.Integrity;

/// <summary>
/// Registers the F3 tamper-evidence services (ADR 0007): the runtime-shape probe, the launcher-record reader, the
/// shared status holder, and the fail-closed integrity <c>BackgroundService</c>. Call after persistence (the record
/// reader needs <c>DataDirectory</c>).
/// </summary>
public static class IntegrityServiceCollectionExtensions
{
    public static IServiceCollection AddIntegrityChecks(this IServiceCollection services)
    {
        services.AddSingleton<IRuntimeShapeProbe, LinuxRuntimeShapeProbe>();
        services.AddSingleton<FileIntegrityRecordReader>();

        // One instance behind both the concrete type (the service writes it) and the interface (readers).
        services.AddSingleton<IntegrityStatusProvider>();
        services.AddSingleton<IIntegrityStatusProvider>(sp => sp.GetRequiredService<IntegrityStatusProvider>());

        services.AddSingleton<IntegrityCheckService>();
        services.AddHostedService(sp => sp.GetRequiredService<IntegrityCheckService>());
        return services;
    }
}
