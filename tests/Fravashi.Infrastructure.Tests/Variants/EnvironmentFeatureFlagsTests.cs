using Fravashi.Application.Updates;
using Fravashi.Application.Variants;
using Fravashi.Infrastructure.Variants;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Variants;

/// <summary>
/// The runtime feature-flag resolution (I1): the compiled variant defaults (this test assembly compiles as
/// <c>full</c>) unless the image environment overrides a switch, and the reported variant identity prefers the
/// image's IProductInfo value.
/// </summary>
public sealed class EnvironmentFeatureFlagsTests
{
    private sealed record FakeProduct(string Version, string Variant) : IProductInfo;

    private static Func<string, string?> Env(params (string Key, string? Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Key, p => p.Value);
        return key => map.TryGetValue(key, out var v) ? v : null;
    }

    [Fact]
    public void Full_compiled_default_with_no_env_is_everything_on()
    {
        var flags = new EnvironmentFeatureFlags(new FakeProduct("1.0.0", "full"), Env());

        flags.Variant.Should().Be("full");
        flags.Flags.Should().Be(FeatureFlags.Full);
    }

    [Fact]
    public void Image_env_overrides_switch_the_features_and_variant_identity_follows_product_info()
    {
        var flags = new EnvironmentFeatureFlags(
            new FakeProduct("2.0.0", "corp-vpn"),
            Env((VariantFeatureResolver.GitHubLoginEnv, "false"), (VariantFeatureResolver.VpnRequiredEnv, "true")));

        flags.Variant.Should().Be("corp-vpn");
        flags.Flags.GitHubLogin.Should().BeFalse();
        flags.Flags.VpnRequired.Should().BeTrue();
    }

    [Fact]
    public void Blank_product_variant_falls_back_to_the_compiled_name()
    {
        var flags = new EnvironmentFeatureFlags(new FakeProduct("1.0.0", ""), Env());
        flags.Variant.Should().Be(CompiledVariant.Name); // "full" in this build
    }

    [Fact]
    public void Corp_broker_image_feature_turns_broker_required_on_and_the_variant_identity_follows()
    {
        // A corp-broker image: FRAVASHI_VARIANT=corp-broker + FRAVASHI_BROKER_REQUIRED=true. The app reports the
        // tier identity and resolves BrokerRequired on, while SSO stays available (no GitHub-login override) and
        // there is no VPN gate - matching the corp-broker compiled default.
        var flags = new EnvironmentFeatureFlags(
            new FakeProduct("3.0.0", "corp-broker"),
            Env((VariantFeatureResolver.BrokerRequiredEnv, "true")));

        flags.Variant.Should().Be("corp-broker");
        flags.Flags.BrokerRequired.Should().BeTrue();
        flags.Flags.GitHubLogin.Should().BeTrue();
        flags.Flags.VpnRequired.Should().BeFalse();
    }
}
