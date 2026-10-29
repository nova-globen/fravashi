using System.Reflection;
using Fravashi.Application.Updates;

namespace Fravashi.Infrastructure.Updates;

/// <summary>
/// The single source of truth for the running product's version + variant (ADR 0006). It reads the values the
/// build embedded into the image - <c>FRAVASHI_VERSION</c> and <c>FRAVASHI_VARIANT</c> environment variables set
/// in the Dockerfile from the release pipeline's <c>--build-arg</c>s - because the running image <em>is</em> that
/// release. On a developer host (no build-time embedding) it falls back to the app assembly's informational
/// version and the default variant, so the reported value always reflects what is actually running rather than a
/// guess.
/// </summary>
public sealed class ProductInfo : IProductInfo
{
    /// <summary>Environment variable the Dockerfile sets from the <c>VERSION</c> build arg.</summary>
    public const string VersionEnvVar = "FRAVASHI_VERSION";

    /// <summary>Environment variable the Dockerfile sets from the <c>VARIANT</c> build arg.</summary>
    public const string VariantEnvVar = "FRAVASHI_VARIANT";

    /// <summary>The default variant when nothing is embedded (matches the launcher's <c>DefaultVariant</c>).</summary>
    public const string DefaultVariant = "full";

    public ProductInfo()
    {
        Version = ResolveVersion();
        Variant = Clean(Environment.GetEnvironmentVariable(VariantEnvVar)) ?? DefaultVariant;
    }

    public string Version { get; }

    public string Variant { get; }

    private static string ResolveVersion()
    {
        var embedded = Clean(Environment.GetEnvironmentVariable(VersionEnvVar));
        if (embedded is not null)
        {
            return embedded;
        }

        // Host-dev fallback: the assembly's informational version (strip any +build-metadata git suffix).
        var informational = typeof(ProductInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (Clean(informational) is { } info)
        {
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }

        return "0.0.0";
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
