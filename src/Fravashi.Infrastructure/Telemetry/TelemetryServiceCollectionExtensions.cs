using Fravashi.Application.Telemetry;
using Fravashi.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fravashi.Infrastructure.Telemetry;

/// <summary>
/// Wires local product-event recording and the diagnostic bundle (backlog 74).
/// <para>
/// This replaced the old <c>StudioTelemetry</c> meter, which declared OpenTelemetry counters for an
/// Aspire dashboard the product removed. Nothing exported them, five of its six counters had no caller
/// at all, and two counted an autopilot that does not exist, so it was scaffolding pointing at
/// something gone. What it is replaced by is smaller on purpose: events recorded in the studio's own
/// database, behind an opt-in that defaults off, with <b>no remote destination anywhere in the
/// stack</b>.
/// </para>
/// <para>
/// Register AFTER <c>AddFravashiPersistence</c> (the store needs the connection factory),
/// <c>AddUpdateNotifications</c> (the bundle names the running version) and
/// <c>AddSandboxHardening</c> (the bundle reports the launcher's posture).
/// </para>
/// </summary>
public static class TelemetryServiceCollectionExtensions
{
    public static IServiceCollection AddProductTelemetry(this IServiceCollection services)
    {
        services.AddSingleton<IProductEventStore, SqliteProductEventStore>();
        services.AddSingleton<IProductEventRecorder>(sp => new ProductEventRecorder(
            sp.GetRequiredService<Application.Persistence.IConfigurationStore>(),
            sp.GetRequiredService<IProductEventStore>(),
            sp.GetService<ILogger<ProductEventRecorder>>()));
        services.AddSingleton<IDiagnosticBundleWriter>(sp => new DiagnosticBundleWriter(
            sp.GetRequiredService<DataDirectory>(),
            sp.GetRequiredService<Application.Updates.IProductInfo>(),
            sp.GetRequiredService<Application.Sandbox.ISandboxPosture>(),
            sp.GetRequiredService<ISqliteConnectionFactory>(),
            sp.GetRequiredService<IProductEventStore>()));

        return services;
    }
}
