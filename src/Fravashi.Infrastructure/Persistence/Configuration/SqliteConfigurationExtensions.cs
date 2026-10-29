using Microsoft.Extensions.Configuration;

namespace Fravashi.Infrastructure.Persistence.Configuration;

public static class SqliteConfigurationExtensions
{
    /// <summary>
    /// Adds the product's shipped OAuth client ids (<see cref="Application.Onboarding.Auth.VendorAppDefaults"/>)
    /// as a configuration source. Call this <b>before</b> <see cref="AddFravashiDatabase"/> so a per-install
    /// (advanced) override in the settings table wins over the baked-in default. See ADR 0004.
    /// </summary>
    public static IConfigurationBuilder AddFravashiVendorDefaults(this IConfigurationBuilder builder)
        => builder.Add(new VendorAppDefaultsConfigurationSource());

    /// <summary>
    /// Adds the SQLite <c>settings</c> table at <paramref name="databasePath"/> as a configuration source.
    /// Call this after the default appsettings/user-secrets sources so database values take precedence.
    /// </summary>
    public static IConfigurationBuilder AddFravashiDatabase(this IConfigurationBuilder builder, string databasePath)
        => builder.Add(new SqliteConfigurationSource(databasePath));

    /// <summary>
    /// Adds the secret→adapter bridge to the configuration pipeline and returns its source so the host can
    /// wire the secret / connection stores into it once the DI container is built (the secret store depends
    /// on Data Protection, which the host owns). Add this AFTER <see cref="AddFravashiDatabase"/> so a
    /// bridged token wins over any stale value in the settings table. The bridge projects nothing until
    /// <see cref="SecretBridgeConfigurationProvider.Attach"/> is called; a token then surfaces at its
    /// adapter key on the next reload.
    /// </summary>
    public static SecretBridgeConfigurationSource AddFravashiSecretBridge(this IConfigurationBuilder builder)
    {
        var source = new SecretBridgeConfigurationSource();
        builder.Add(source);
        return source;
    }

    /// <summary>
    /// Adds the non-secret coordinate→adapter bridge to the configuration pipeline and returns its source so the
    /// host can wire the connection / settings stores into it once the DI container is built. Add this AFTER
    /// <see cref="AddFravashiDatabase"/> so a projected coordinate wins over any stale value in the settings
    /// table, alongside <see cref="AddFravashiSecretBridge"/> (the two write disjoint keys - tokens vs
    /// coordinates). The bridge projects nothing until
    /// <see cref="CoordinateBridgeConfigurationProvider.Attach"/> is called; the domain/org/project a connection
    /// carries then surface at their adapter keys on the next reload, so a verified tracker is actually live.
    /// </summary>
    public static CoordinateBridgeConfigurationSource AddFravashiCoordinateBridge(this IConfigurationBuilder builder)
    {
        var source = new CoordinateBridgeConfigurationSource();
        builder.Add(source);
        return source;
    }
}
