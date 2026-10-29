using AwesomeAssertions;
using Fravashi.Application.Mockups;
using Fravashi.Application.Prototypes;
using Fravashi.Infrastructure.Browser;
using Microsoft.Playwright;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Verification;

/// <summary>
/// Backlog 239 (plan @51) in a real browser, gated like #223's live tier (<c>FRAVASHI_BROWSER_LIVE=1</c>, the studio
/// container): a label-only button shows its label, and the accessibility tree still names it by the label alone, which
/// is what #222's matcher reads. <c>DesignCheckLiveTests</c> then holds every fixture's prototype to its design.
/// </summary>
public sealed class PrototypePlaceholderLiveTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("FRAVASHI_BROWSER_LIVE") == "1";

    private sealed class BrowserLiveFactAttribute : FactAttribute
    {
        public BrowserLiveFactAttribute() => Skip = Enabled
            ? null
            : "Set FRAVASHI_BROWSER_LIVE=1 where a Chromium is installed (the studio container) to drive a real browser.";
    }

    [BrowserLiveFact]
    public async Task A_label_only_button_shows_its_label_and_keeps_exactly_that_accessible_name()
    {
        var read = MockupReader.Read(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Mockups", "Fixtures", "every-feature.json")));
        read.IsValid.Should().BeTrue(string.Join("; ", read.Errors));

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(PlaywrightAgentBrowser.LaunchOptions(jitless: true));
        var page = await browser.NewPageAsync();
        await page.SetContentAsync(PrototypeRenderer.Render(read.Document!, []).Html);

        var help = page.Locator("[data-mk-anchor=\"help\"]");
        (await help.InnerTextAsync()).Trim().Should().Be("Help", "the box shows the label");
        (await help.Locator(".mk-placeholder").IsVisibleAsync()).Should().BeTrue();
        (await help.AriaSnapshotAsync()).Trim().Should().Be("- button \"Help\"", "the accessible name is the design's label, once");
        (await page.GetByRole(AriaRole.Button, new() { Name = "Help", Exact = true }).CountAsync()).Should().Be(1);
    }
}
