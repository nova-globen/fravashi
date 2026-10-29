using Fravashi.Web.Services;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>Plan Phase 3 Task 4: the sign-in gate's exemption policy.</summary>
public class SignInGateTests
{
    [Theory]
    [InlineData("/sign-in")]
    [InlineData("/sign-in?error=x")]
    [InlineData("/auth/callback/idp")]
    [InlineData("/auth/callback/atlassian")]
    [InlineData("/update-required")]
    [InlineData("/integrity-failed")]
    [InlineData("/secure-connection")]
    [InlineData("/Error")]
    [InlineData("/Error/")]
    [InlineData("/healthz")]
    [InlineData("/api/onboarding/status")]
    [InlineData("/_blazor")]
    [InlineData("/_framework/blazor.web.js")]
    public void Exempt_paths_stay_reachable_while_signed_out(string path) =>
        Assert.True(SignInGate.IsExempt(path));

    [Theory]
    [InlineData("/")]
    [InlineData("/welcome")]
    [InlineData("/code")]
    [InlineData("/git")]
    [InlineData("/settings")]
    [InlineData("/onboarding")]
    // Backlog 283: no path is exempt for looking like a file; StudioGates lets a real static asset through.
    [InlineData("/studio.css")]
    [InlineData("/code/README.md")]
    [InlineData("/git/history/src/Program.cs")]
    public void Pages_are_gated(string path) =>
        Assert.False(SignInGate.IsExempt(path));

    [Fact]
    public async Task An_expired_session_that_can_be_refreshed_is_refreshed_instead_of_sent_to_sign_in()
    {
        var signedIn = false;
        var refreshes = 0;

        var admitted = await SignInGate.AdmitsAsync(
            () => true, () => signedIn, _ => { refreshes++; signedIn = true; return Task.FromResult(true); }, CancellationToken.None);

        Assert.True(admitted);
        Assert.Equal(1, refreshes);
    }

    [Fact]
    public async Task A_session_that_cannot_be_refreshed_still_goes_to_sign_in()
    {
        Assert.False(await SignInGate.AdmitsAsync(() => true, () => false, _ => Task.FromResult(false), CancellationToken.None));
    }

    [Fact]
    public async Task A_valid_session_and_an_ungated_install_never_touch_the_identity_provider()
    {
        var refreshes = 0;
        Task<bool> Refresh(CancellationToken _) { refreshes++; return Task.FromResult(true); }

        Assert.True(await SignInGate.AdmitsAsync(() => true, () => true, Refresh, CancellationToken.None));
        Assert.True(await SignInGate.AdmitsAsync(() => false, () => false, Refresh, CancellationToken.None));
        Assert.Equal(0, refreshes);
    }
}
