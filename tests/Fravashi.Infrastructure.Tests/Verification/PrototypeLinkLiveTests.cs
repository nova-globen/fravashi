using AwesomeAssertions;
using Fravashi.Application.Mockups;
using Fravashi.Application.Prototypes;
using Fravashi.Infrastructure.Browser;
using Microsoft.Playwright;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Verification;

/// <summary>
/// Backlog 290 in a real browser, gated like #223's live tier (<c>FRAVASHI_BROWSER_LIVE=1</c>, the studio container): a
/// prototype's link is role link by its name, and clicking it navigates nowhere, whatever target the design gives it, in a
/// view render (where the design's own <c>go</c> still moves between its screens) and in an edit render, and also when an
/// overlay's <c>setAttribute</c> has given the link a real <c>href</c>, which the overlay guard allows.
/// </summary>
public sealed class PrototypeLinkLiveTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("FRAVASHI_BROWSER_LIVE") == "1";

    private sealed class BrowserLiveFactAttribute : FactAttribute
    {
        public BrowserLiveFactAttribute() => Skip = Enabled
            ? null
            : "Set FRAVASHI_BROWSER_LIVE=1 where a Chromium is installed (the studio container) to drive a real browser.";
    }

    private const string Design = """
        {"mockup": 1, "screens": [
          {"type": "window", "id": "not-found", "text": "Not found", "children": [
            {"type": "link", "id": "go-home", "text": "Go to the start page", "href": "https://example.invalid/", "on": {"click": {"go": "start"}}},
            {"type": "link", "id": "elsewhere", "text": "Somewhere else", "href": "https://example.invalid/elsewhere"}
          ]},
          {"type": "window", "id": "start", "text": "Start", "children": []}
        ]}
        """;

    [BrowserLiveFact]
    public async Task A_prototype_link_is_a_link_and_following_it_navigates_nowhere()
    {
        var read = MockupReader.Read(Design);
        read.IsValid.Should().BeTrue(string.Join("; ", read.Errors));

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(PlaywrightAgentBrowser.LaunchOptions(jitless: true));
        // An overlay may set an attribute named href (PrototypeHtmlGuard refuses only handlers, data-mk- and script URLs).
        IReadOnlyList<PrototypeOperation> overlaid =
        [
            new PrototypeSetAttributeOperation(new PrototypeAnchor("elsewhere", null), "href", "https://example.invalid/overlay"),
        ];
        PrototypeRenderer.Render(read.Document!, overlaid).Html.Should().Contain("href=\"https://example.invalid/overlay\"");

        foreach (var (edit, overlay) in new[] { (false, (IReadOnlyList<PrototypeOperation>)[]), (true, []), (false, overlaid) })
        {
            var page = await browser.NewPageAsync();
            var requests = new List<string>();
            page.Request += (_, request) => requests.Add(request.Url);
            await page.SetContentAsync(PrototypeRenderer.Render(read.Document!, overlay, new PrototypeRenderOptions(Edit: edit)).Html);
            var before = page.Url;

            var elsewhere = page.GetByRole(AriaRole.Link, new() { Name = "Somewhere else", Exact = true });
            (await elsewhere.CountAsync()).Should().Be(1, "the prototype draws the design's link as a link");
            await elsewhere.ClickAsync();
            await page.GetByRole(AriaRole.Link, new() { Name = "Go to the start page", Exact = true }).ClickAsync();
            await page.WaitForTimeoutAsync(300);

            page.Url.Should().Be(before, "a link in a prototype is never followed (edit: {0})", edit);
            requests.Should().BeEmpty("nothing loads when a prototype's link is clicked (edit: {0})", edit);

            // The design's own go still moves between its screens in a view render; an edit render acts on nothing.
            (await page.Locator("[data-mk-anchor=\"start\"]").IsVisibleAsync()).Should().Be(!edit);
            await page.CloseAsync();
        }
    }
}
