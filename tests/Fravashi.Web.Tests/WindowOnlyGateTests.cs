using Fravashi.Web.Services;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>The window-only access rules (ADR 0017): only the studio window shell browses the studio.</summary>
public class WindowOnlyGateTests
{
    [Theory]
    [InlineData("/healthz")]
    [InlineData("/api/onboarding/status")]
    [InlineData("/auth/callback/atlassian")]
    public void Machine_endpoints_stay_open(string path) =>
        Assert.True(WindowOnlyGate.IsExempt(path));

    [Theory]
    [InlineData("/")]
    [InlineData("/code")]
    [InlineData("/git/local")]
    [InlineData("/_framework/blazor.web.js")]
    [InlineData("/_blazor")]
    [InlineData("/onboarding")]
    [InlineData("/integrity-failed")]
    public void Everything_browsable_is_gated(string path) =>
        Assert.False(WindowOnlyGate.IsExempt(path));

    [Fact]
    public void A_plain_browser_user_agent_is_refused()
    {
        Assert.False(WindowOnlyGate.IsAllowed(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/126.0 Safari/537.36",
            requiredToken: null));
        Assert.False(WindowOnlyGate.IsAllowed(string.Empty, requiredToken: null));
        Assert.False(WindowOnlyGate.IsAllowed(null, requiredToken: null));
    }

    [Fact]
    public void The_window_marker_is_enough_when_no_token_is_configured() =>
        Assert.True(WindowOnlyGate.IsAllowed(
            "Mozilla/5.0 ... Chrome/126.0 Safari/537.36 Edg/126.0 FravashiWindow/1.0",
            requiredToken: null));

    [Fact]
    public void A_configured_token_must_be_presented()
    {
        const string agentWithToken = "Mozilla/5.0 ... FravashiWindow/1.0 tk/abc123";
        const string agentWithoutToken = "Mozilla/5.0 ... FravashiWindow/1.0";
        const string agentWrongToken = "Mozilla/5.0 ... FravashiWindow/1.0 tk/zzz999";

        Assert.True(WindowOnlyGate.IsAllowed(agentWithToken, "abc123"));
        Assert.False(WindowOnlyGate.IsAllowed(agentWithoutToken, "abc123"));
        Assert.False(WindowOnlyGate.IsAllowed(agentWrongToken, "abc123"));
    }

    [Fact]
    public void The_marker_is_required_even_with_a_matching_token_elsewhere_in_the_agent() =>
        Assert.False(WindowOnlyGate.IsAllowed("Mozilla/5.0 tk/abc123", "abc123"));
}
