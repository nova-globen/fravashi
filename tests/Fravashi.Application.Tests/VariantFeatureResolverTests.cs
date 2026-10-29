using Fravashi.Application.Variants;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Application.Tests;

/// <summary>
/// The variant feature-switch resolution (I1): the compiled-in defaults stand unless an image environment
/// variable explicitly overrides a switch. An empty/unset/unrecognised env value is NOT an override, so an
/// image that ships only the compiled variant (no runtime env) keeps its baked behaviour.
/// </summary>
public sealed class VariantFeatureResolverTests
{
    private static Func<string, string?> Env(params (string Key, string? Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Key, p => p.Value);
        return key => map.TryGetValue(key, out var v) ? v : null;
    }

    [Fact]
    public void No_env_keeps_the_compiled_defaults()
    {
        var flags = VariantFeatureResolver.Resolve(FeatureFlags.Full, Env());
        flags.Should().Be(FeatureFlags.Full);
    }

    [Fact]
    public void Env_overrides_each_switch_independently()
    {
        var flags = VariantFeatureResolver.Resolve(
            FeatureFlags.Full,
            Env((VariantFeatureResolver.GitHubLoginEnv, "false"), (VariantFeatureResolver.VpnRequiredEnv, "true")));

        flags.GitHubLogin.Should().BeFalse();
        flags.VpnRequired.Should().BeTrue();
    }

    [Fact]
    public void Broker_required_defaults_off_and_is_overridable_by_the_image_env()
    {
        // No env → the compiled default (full = off) stands.
        VariantFeatureResolver.Resolve(FeatureFlags.Full, Env()).BrokerRequired.Should().BeFalse();

        // The corp-broker image feature (FRAVASHI_BROKER_REQUIRED=true) turns it on.
        VariantFeatureResolver.Resolve(FeatureFlags.Full, Env((VariantFeatureResolver.BrokerRequiredEnv, "true")))
            .BrokerRequired.Should().BeTrue();
    }

    [Fact]
    public void A_broker_compiled_default_stands_without_env_and_is_independent_of_the_other_switches()
    {
        // Mirrors the corp-broker compiled default: broker on, SSO available, no VPN gate.
        var brokerVariant = new FeatureFlags(GitHubLogin: true, VpnRequired: false, BrokerRequired: true);

        var flags = VariantFeatureResolver.Resolve(brokerVariant, Env());

        flags.BrokerRequired.Should().BeTrue();  // no override → baked default stands
        flags.GitHubLogin.Should().BeTrue();
        flags.VpnRequired.Should().BeFalse();
    }

    [Fact]
    public void Env_can_turn_a_switched_off_compiled_default_back_on()
    {
        var lockedDown = new FeatureFlags(GitHubLogin: false, VpnRequired: true);

        var flags = VariantFeatureResolver.Resolve(
            lockedDown, Env((VariantFeatureResolver.GitHubLoginEnv, "on")));

        flags.GitHubLogin.Should().BeTrue();   // env override wins
        flags.VpnRequired.Should().BeTrue();   // no override → compiled default stands
    }

    [Fact]
    public void Auth_required_defaults_ON_and_a_publish_can_turn_it_off()
    {
        // The 2026-08-03 directive: auth-first is the default behavior of every build.
        VariantFeatureResolver.Resolve(FeatureFlags.Full, Env()).AuthRequired.Should().BeTrue();
        new FeatureFlags(GitHubLogin: true, VpnRequired: false).AuthRequired.Should().BeTrue();

        // A release published with AUTH_REQUIRED=false switches the gate off.
        VariantFeatureResolver.Resolve(FeatureFlags.Full, Env((VariantFeatureResolver.AuthRequiredEnv, "false")))
            .AuthRequired.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("maybe")]
    [InlineData(null)]
    public void Empty_or_unrecognised_env_is_not_an_override(string? value)
    {
        var flags = VariantFeatureResolver.Resolve(
            FeatureFlags.Full, Env((VariantFeatureResolver.GitHubLoginEnv, value)));

        flags.GitHubLogin.Should().BeTrue();
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("on", true)]
    [InlineData("YES", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("off", false)]
    [InlineData("No", false)]
    [InlineData("nonsense", null)]
    public void Override_parses_boolean_ish_values(string value, bool? expected)
        => VariantFeatureResolver.Override(value).Should().Be(expected);
}
