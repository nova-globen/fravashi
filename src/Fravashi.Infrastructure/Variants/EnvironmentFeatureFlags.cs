using Fravashi.Application.Updates;
using Fravashi.Application.Variants;

namespace Fravashi.Infrastructure.Variants;

/// <summary>
/// Resolves the running app's feature switches (I1): the compiled-in variant defaults
/// (<see cref="CompiledVariant"/>), overridden per switch by the image environment variables the Dockerfile sets
/// from its build-args. Resolved once at construction (the values are fixed for the process lifetime). The
/// reported <see cref="Variant"/> prefers the image's <c>FRAVASHI_VARIANT</c> (via <see cref="IProductInfo"/>,
/// the single source of truth) and falls back to the compiled name, so the identity the app shows always matches
/// the image it is running.
/// </summary>
public sealed class EnvironmentFeatureFlags : IFeatureFlags
{
    public EnvironmentFeatureFlags(IProductInfo productInfo, Func<string, string?>? env = null)
    {
        ArgumentNullException.ThrowIfNull(productInfo);
        env ??= Environment.GetEnvironmentVariable;

        Flags = VariantFeatureResolver.Resolve(CompiledVariant.Defaults, env);
        Variant = string.IsNullOrWhiteSpace(productInfo.Variant) ? CompiledVariant.Name : productInfo.Variant;
    }

    public string Variant { get; }

    public FeatureFlags Flags { get; }
}
