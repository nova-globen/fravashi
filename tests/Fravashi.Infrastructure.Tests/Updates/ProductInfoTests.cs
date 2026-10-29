using Fravashi.Infrastructure.Updates;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Updates;

/// <summary>
/// The single source of truth for the running version/variant (ADR 0006): the build-embedded environment
/// variables win, with an honest assembly-version fallback on a developer host.
/// </summary>
[Collection("ProductInfoEnv")]
public sealed class ProductInfoTests : IDisposable
{
    private readonly string? _version = Environment.GetEnvironmentVariable(ProductInfo.VersionEnvVar);
    private readonly string? _variant = Environment.GetEnvironmentVariable(ProductInfo.VariantEnvVar);

    [Fact]
    public void Reads_the_embedded_version_and_variant()
    {
        Environment.SetEnvironmentVariable(ProductInfo.VersionEnvVar, "3.4.5");
        Environment.SetEnvironmentVariable(ProductInfo.VariantEnvVar, "corp-vpn");

        var info = new ProductInfo();
        info.Version.Should().Be("3.4.5");
        info.Variant.Should().Be("corp-vpn");
    }

    [Fact]
    public void Falls_back_to_the_default_variant_and_a_nonempty_version()
    {
        Environment.SetEnvironmentVariable(ProductInfo.VersionEnvVar, null);
        Environment.SetEnvironmentVariable(ProductInfo.VariantEnvVar, null);

        var info = new ProductInfo();
        info.Variant.Should().Be(ProductInfo.DefaultVariant);
        info.Version.Should().NotBeNullOrWhiteSpace();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ProductInfo.VersionEnvVar, _version);
        Environment.SetEnvironmentVariable(ProductInfo.VariantEnvVar, _variant);
    }
}
