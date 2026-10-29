using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.Variants;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Application.Tests;

/// <summary>
/// The B4 tier composition on <see cref="BrokerOptions"/>: the publish-time <c>corp-broker</c> variant
/// (<see cref="FeatureFlags.BrokerRequired"/>) forces brokering on and forbids the local org-key path, while the
/// broker endpoint stays a deploy value; the endpoint is expressible as an ADR 0007 egress-allowlist entry; and
/// the tier composes coherently with the VPN switch (they are independent flags).
/// </summary>
public sealed class BrokerOptionsVariantTests
{
    [Fact]
    public void ForVariant_off_leaves_the_config_bound_options_unchanged()
    {
        var configured = new BrokerOptions { Enabled = false, BaseUrl = "https://broker.corp.internal" };

        var composed = configured.ForVariant(brokerRequired: false);

        composed.Should().BeSameAs(configured);
        composed.Enabled.Should().BeFalse();
        composed.LocalKeyForbidden.Should().BeFalse();
    }

    [Fact]
    public void ForVariant_corp_broker_forces_brokering_on_and_forbids_the_local_key_even_if_config_never_enabled_it()
    {
        // A corp-broker image where the operator set only the deploy BaseUrl, never Broker:Enabled.
        var configured = new BrokerOptions { Enabled = false, BaseUrl = "https://broker.corp.internal" };

        var composed = configured.ForVariant(brokerRequired: true);

        composed.Enabled.Should().BeTrue();
        composed.LocalKeyForbidden.Should().BeTrue();
        composed.IsActive.Should().BeTrue();              // BaseUrl present → brokering is live
        composed.BaseUrl.Should().Be("https://broker.corp.internal"); // still the deploy value
    }

    [Fact]
    public void ForVariant_corp_broker_with_no_base_url_forbids_the_local_key_but_is_inert_fail_closed()
    {
        // corp-broker image with no broker endpoint configured: the local-key path is still OFF, and the broker is
        // inert (IsActive false), so the credential layer injects nothing - it fails closed, never a local-key fallback.
        var composed = new BrokerOptions { Enabled = false, BaseUrl = null }.ForVariant(brokerRequired: true);

        composed.LocalKeyForbidden.Should().BeTrue();
        composed.IsActive.Should().BeFalse();
    }

    [Theory]
    [InlineData("https://broker.corp.internal", "broker.corp.internal")]
    [InlineData("https://broker.corp.internal:8443/v1", "broker.corp.internal:8443")]
    [InlineData("http://127.0.0.1:8080", "127.0.0.1:8080")]
    public void EgressAllowlistEntry_derives_the_host_the_egress_proxy_must_permit(string baseUrl, string expected)
    {
        new BrokerOptions { BaseUrl = baseUrl }.EgressAllowlistEntry.Should().Be(expected);
    }

    [Fact]
    public void EgressAllowlistEntry_is_null_when_no_base_url_is_configured()
    {
        new BrokerOptions { BaseUrl = null }.EgressAllowlistEntry.Should().BeNull();
    }

    [Fact]
    public void Corp_broker_and_vpn_switches_are_independent_and_compose()
    {
        // The two corporate switches are orthogonal flags: a build can require the broker, the VPN tunnel, both, or
        // neither. When both are on, the broker is reached over the tunnel (the studio joins the sidecar netns, so
        // ALL egress rides it - proven structurally by the launcher VPN netns test), and the org key stays off-device.
        var both = new FeatureFlags(GitHubLogin: true, VpnRequired: true, BrokerRequired: true);

        both.BrokerRequired.Should().BeTrue();
        both.VpnRequired.Should().BeTrue();

        var broker = new BrokerOptions { BaseUrl = "https://broker.corp.internal" }
            .ForVariant(both.BrokerRequired);
        broker.LocalKeyForbidden.Should().BeTrue();
        broker.IsActive.Should().BeTrue();
    }
}
