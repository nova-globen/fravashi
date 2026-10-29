using Bunit;
using Fravashi.Application.Integrity;
using Fravashi.Web.Components.Integrity;
using Fravashi.Web.Components.Settings;
using Fravashi.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// The F3 integrity surfaces (T8): the honest Settings status card (a PASS shows the values it compared) and the
/// loud fail-closed lock panel; plus the lock's path policy. Assertion-gap rule: an all-pass render must reflect a
/// real evaluation, and a seeded FAIL must render the locked state.
/// </summary>
public class IntegrityComponentsTests
{
    private static BunitContext Context(IntegrityStatus status)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IIntegrityStatusProvider>(new FakeIntegrityStatus(status));
        return ctx;
    }

    private static IntegrityCheck Pass(string name) =>
        new("studio", name, IntegrityCheckStatus.Pass, "x", "x", "matched");

    private static IntegrityCheck Fail(string name) =>
        new("studio", name, IntegrityCheckStatus.Fail, "full", "corp-vpn", "mismatch");

    private static IntegrityStatus Enforced(params IntegrityCheck[] checks) =>
        new(IntegrityMode.Enforced, "enforced", true, checks, DateTimeOffset.UnixEpoch, "wslc");

    [Fact]
    public void Card_renders_all_pass_in_enforced_mode()
    {
        var cut = Context(Enforced(Pass("Record signature"), Pass("env FRAVASHI_VARIANT"), Pass("Non-root uid")))
            .Render<IntegrityStatusCard>();

        Assert.Equal("enforced", cut.Find("[data-testid=integrity-mode]").GetAttribute("data-mode"));
        Assert.Contains("3 passed, 0 failed", cut.Markup);
        Assert.DoesNotContain("failing closed", cut.Markup);
        Assert.Equal(3, cut.FindAll("[data-testid=integrity-list] li").Count);
    }

    [Fact]
    public void Each_check_shows_its_compared_values_apart_from_its_detail()
    {
        // Backlog 188: the card shares the posture list's markup, so it had the same run-together line.
        var cut = Context(Enforced(Pass("Record signature"), Fail("env FRAVASHI_VARIANT")))
            .Render<IntegrityStatusCard>();

        foreach (var (slug, detail) in new[] { ("record-signature", "matched"), ("env-fravashi-variant", "mismatch") })
        {
            var desc = cut.Find($"[data-testid=integrity-check-{slug}-desc]");
            var compared = cut.Find($"[data-testid=integrity-check-{slug}-detail]");

            Assert.Equal(detail, desc.TextContent.Trim());
            Assert.StartsWith("expected", compared.TextContent.Trim());
            Assert.False(desc.Contains(compared), "the compared values must not sit inside the detail");
        }
    }

    [Fact]
    public void Card_renders_the_failed_locked_state_when_a_check_fails_under_enforcement()
    {
        var cut = Context(Enforced(Pass("Record signature"), Fail("env FRAVASHI_VARIANT")))
            .Render<IntegrityStatusCard>();

        Assert.Contains("FAILED", cut.Markup);
        Assert.NotNull(cut.Find("[data-testid=integrity-failclosed]"));
        var failRow = cut.Find("[data-testid=integrity-check-env-fravashi-variant]");
        Assert.Equal("fail", failRow.GetAttribute("data-status"));
    }

    [Fact]
    public void Card_shows_the_enforced_reason_without_repeating_the_enforced_label()
    {
        // Backlog 277: the card prefixes "Enforced" itself, so a ModeReason that starts with the same word
        // rendered as "Enforced - Enforced - ...".
        var status = new IntegrityStatus(
            IntegrityMode.Enforced, "A genuine mismatch will fail the app closed and stop the container.",
            Provisioned: true, Checks: [], EvaluatedUtc: DateTimeOffset.UnixEpoch, Runtime: "wslc");

        var cut = Context(status).Render<IntegrityStatusCard>();

        var line = cut.Find("[data-testid=integrity-mode]").TextContent;
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(line, "Enforced"));
    }

    [Fact]
    public void Card_shows_report_only_reason_outside_a_container()
    {
        var status = new IntegrityStatus(
            IntegrityMode.ReportOnly, "Running outside a launcher-managed container - report-only.",
            Provisioned: false, Checks: [], EvaluatedUtc: DateTimeOffset.UnixEpoch, Runtime: null);

        var cut = Context(status).Render<IntegrityStatusCard>();

        Assert.Equal("report-only", cut.Find("[data-testid=integrity-mode]").GetAttribute("data-mode"));
        Assert.Contains("outside a launcher-managed container", cut.Markup);
    }

    [Fact]
    public void Failed_panel_lists_the_failing_checks()
    {
        var cut = Context(Enforced(Pass("Record signature"), Fail("Non-root uid")))
            .Render<IntegrityFailedPanel>();

        Assert.NotNull(cut.Find("[data-testid=integrity-failed]"));
        Assert.Contains("Integrity check failed", cut.Markup);
        Assert.Contains("Non-root uid", cut.Find("[data-testid=integrity-failed-list]").TextContent);
        Assert.Contains(Fravashi.Domain.ProductName.Command, cut.Markup);
    }

    [Theory]
    [InlineData("/", false)]
    [InlineData("/board", false)]
    [InlineData("/integrity-failed", true)]
    [InlineData("/healthz", true)]
    [InlineData("/_blazor", true)]
    // Backlog 283: no path is exempt for looking like a file; StudioGates lets a real static asset through.
    [InlineData("/app.css", false)]
    [InlineData("/code/README.md", false)]
    [InlineData("/git/history/x.json", false)]
    [InlineData("/Error", true)]
    [InlineData("/Error/", true)]
    public void Lock_path_policy_exempts_only_the_page_liveness_framework_and_assets(string path, bool exempt)
    {
        Assert.Equal(exempt, IntegrityLock.IsExempt(path));
    }

    private sealed class FakeIntegrityStatus(IntegrityStatus status) : IIntegrityStatusProvider
    {
        public IntegrityStatus Current => status;
    }
}
