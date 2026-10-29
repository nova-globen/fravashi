using Bunit;
using Fravashi.Application.Onboarding;
using Fravashi.Web.Components.Onboarding;
using Xunit;

namespace Fravashi.Web.Tests;

public class OnboardingProbeViewTests
{
    [Fact]
    public void Passed_check_renders_a_verified_badge_and_names_its_mechanism()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var result = new AccessProbeResult([new AccessCheck("Read", AccessProbeOutcome.Passed, "git ls-remote", "reachable")]);
        var cut = ctx.Render<ProbeResultView>(p => p.Add(c => c.Result, result));

        var row = cut.Find(".ob-probe-row");
        Assert.Equal("Passed", row.GetAttribute("data-outcome"));
        Assert.Contains("✓ Verified", row.TextContent);
        Assert.Contains("via git ls-remote", row.TextContent);
    }

    [Fact]
    public void Failed_check_renders_failed_never_verified()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<ProbeResultView>(p => p.Add(c => c.Result, ProbeResults.Failed));

        Assert.Contains("✗ Failed", cut.Markup);
        Assert.DoesNotContain("✓ Verified", cut.Markup);
    }

    [Fact]
    public void NotVerifiable_check_renders_recorded_not_verified_never_success()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var result = new AccessProbeResult(
            [new AccessCheck("Write", AccessProbeOutcome.NotVerifiable, "no non-destructive push probe", "deferred")]);
        var cut = ctx.Render<ProbeResultView>(p => p.Add(c => c.Result, result));

        Assert.Contains("Recorded, not verified", cut.Markup);
        Assert.DoesNotContain("✓ Verified", cut.Markup);
    }

    [Fact]
    public void NotSupported_check_renders_not_yet_supported()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var result = new AccessProbeResult(
            [new AccessCheck("Read", AccessProbeOutcome.NotSupported, "in-app local board", "recorded, not yet a working store")]);
        var cut = ctx.Render<ProbeResultView>(p => p.Add(c => c.Result, result));

        Assert.Contains("Not yet supported", cut.Markup);
        Assert.DoesNotContain("✓ Verified", cut.Markup);
    }

    [Fact]
    public void No_result_renders_not_tested_yet()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<ProbeResultView>();

        Assert.Contains("Not tested yet", cut.Markup);
    }
}
