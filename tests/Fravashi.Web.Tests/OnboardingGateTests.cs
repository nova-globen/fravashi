using Fravashi.Web.Services;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// The first-run onboarding gate's path policy, extracted to OnboardingGate (Services/OnboardingGate.cs) from
/// Program.cs's previously-inline check so it is unit-tested like the other four gates (backlog 281: the
/// extraction is what let the Error page exemption gain a real test rather than only a line in Program.cs).
/// </summary>
public sealed class OnboardingGateTests
{
    [Theory]
    [InlineData("/", false)]
    [InlineData("/board", false)]
    [InlineData("/onboarding", true)]
    [InlineData("/onboarding/welcome", true)]
    [InlineData("/sign-in", true)]
    [InlineData("/auth/callback/idp", true)]
    [InlineData("/update-required", true)]
    [InlineData("/integrity-failed", true)]
    [InlineData("/secure-connection", true)]
    [InlineData("/Error", true)]
    [InlineData("/Error/", true)]
    [InlineData("/healthz", true)]
    [InlineData("/api/onboarding/status", true)]
    [InlineData("/_blazor", true)]
    // Backlog 283: no path is exempt for looking like a file; StudioGates lets a real static asset through.
    [InlineData("/app.css", false)]
    [InlineData("/code/README.md", false)]
    [InlineData("/git/history/x.json", false)]
    public void Gate_exempts_every_other_gate_s_page_liveness_framework_and_assets(string path, bool exempt) =>
        Assert.Equal(exempt, OnboardingGate.IsExempt(path));
}
