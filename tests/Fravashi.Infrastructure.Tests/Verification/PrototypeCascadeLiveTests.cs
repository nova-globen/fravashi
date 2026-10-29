using System.Text.Json;
using AwesomeAssertions;
using Fravashi.Application.Mockups;
using Fravashi.Application.Prototypes;
using Fravashi.Application.Verification;
using Fravashi.Infrastructure.Browser;
using Microsoft.Playwright;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Verification;

/// <summary>
/// Backlog 237 (plan @50) read in a real browser, beside #223's live tier and gated the same way
/// (<c>FRAVASHI_BROWSER_LIVE=1</c>, the studio container): a page rule written with type selectors alone paints the
/// buttons, and general page rules change nothing about which text variant, dialog or hidden state shows.
/// <c>PrototypeCascadeTests</c> holds the same by specificity everywhere else.
/// </summary>
public sealed class PrototypeCascadeLiveTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("FRAVASHI_BROWSER_LIVE") == "1";

    private sealed class BrowserLiveFactAttribute : FactAttribute
    {
        public BrowserLiveFactAttribute() => Skip = Enabled
            ? null
            : "Set FRAVASHI_BROWSER_LIVE=1 where a Chromium is installed (the studio container) to drive a real browser.";
    }

    /// <summary>What the page shows, read with getComputedStyle.</summary>
    private const string Probe = """
        () => {
          const shown = e => getComputedStyle(e).display !== "none";
          const name = e => (e.closest("[data-mk-anchor]") || e).getAttribute("data-mk-anchor") + " " + e.className;
          const pick = s => Array.from(document.querySelectorAll(s)).map(e => name(e) + " " + (shown(e) ? "shown" : "none"));
          return JSON.stringify({
            buttons: Array.from(document.querySelectorAll("button.mk-button")).map(b => getComputedStyle(b).backgroundColor),
            variants: pick(".mk-text-variant, .mk-text-base"),
            closed: pick("[hidden], dialog:not([open]), .mk-state-hidden"),
          });
        }
        """;

    private sealed record Seen(string[] Buttons, string[] Variants, string[] Closed);

    [BrowserLiveFact]
    public async Task A_type_only_page_rule_paints_the_buttons_and_general_rules_reveal_nothing()
    {
        var read = MockupReader.Read(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Mockups", "Fixtures", "every-feature.json")));
        read.IsValid.Should().BeTrue(string.Join("; ", read.Errors));
        var design = read.Document!;

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(PlaywrightAgentBrowser.LaunchOptions(jitless: true));

        async Task<Seen> SeeAsync(IReadOnlyList<PrototypeOperation> overlay)
        {
            var page = await browser.NewPageAsync();
            await page.SetContentAsync(PrototypeRenderer.Render(design, overlay).Html);
            var json = await page.EvaluateAsync<string>(Probe);
            await page.CloseAsync();
            return JsonSerializer.Deserialize<Seen>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        }

        var plain = await SeeAsync([]);
        var adjusted = await SeeAsync(
        [
            new PrototypePageCssOperation("button { background-color: #1d4ed8; color: white; }"),
            new PrototypePageCssOperation("span { display: inline; }"),
            new PrototypePageCssOperation("dialog, section, p { display: block; }"),
            new PrototypePageCssOperation("* { display: block; }"),
        ]);

        plain.Buttons.Should().NotBeEmpty().And.AllSatisfy(c => c.Should().Be("rgb(236, 238, 241)"));
        adjusted.Buttons.Should().HaveCount(plain.Buttons.Length).And.AllSatisfy(c => c.Should().Be("rgb(29, 78, 216)"));

        plain.Variants.Should().Contain(v => v.EndsWith(" none", StringComparison.Ordinal), "the fixture has variants that start hidden");
        adjusted.Variants.Should().Equal(plain.Variants);
        plain.Closed.Should().NotBeEmpty().And.AllSatisfy(c => c.Should().EndWith(" none"));
        adjusted.Closed.Should().Equal(plain.Closed);
    }

    /// <summary>
    /// Backlog 238 (plan @52) in the real engine: "make the heading bigger" as a setCss on the screen's title part makes
    /// the heading 1.6rem (25.6px) and changes no cell, header, caption or button; the same edit on the screen itself
    /// leaves the heading at the base size, which is the defect the window proof (#217) found.
    /// </summary>
    [BrowserLiveFact]
    public async Task A_style_on_a_title_part_sizes_the_heading_and_nothing_else()
    {
        var read = MockupReader.Read(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Mockups", "Fixtures", "every-feature.json")));
        read.IsValid.Should().BeTrue(string.Join("; ", read.Errors));
        var design = read.Document!;

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(PlaywrightAgentBrowser.LaunchOptions(jitless: true));

        const string Sizes = """
            () => JSON.stringify({
              heading: getComputedStyle(document.querySelector('[data-mk-part-of="home"][data-mk-part="title"]')).fontSize,
              others: Array.from(document.querySelectorAll('[data-mk-anchor="home"] td, [data-mk-anchor="home"] th, [data-mk-anchor="home"] caption, [data-mk-anchor="home"] button, [data-mk-anchor="home"] h2'))
                .map(e => getComputedStyle(e).fontSize),
            })
            """;

        async Task<Sized> SeeAsync(IReadOnlyList<PrototypeOperation> overlay)
        {
            var page = await browser.NewPageAsync();
            await page.SetContentAsync(PrototypeRenderer.Render(design, overlay).Html);
            var json = await page.EvaluateAsync<string>(Sizes);
            await page.CloseAsync();
            return JsonSerializer.Deserialize<Sized>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        }

        static PrototypeSetCssOperation Bigger(PrototypeAnchor anchor) =>
            new(anchor, new Dictionary<string, string>(StringComparer.Ordinal) { ["font-size"] = "1.6rem" });

        var plain = await SeeAsync([]);
        var title = await SeeAsync([Bigger(new PrototypeAnchor("home", null, DesignPart.Title))]);
        var screen = await SeeAsync([Bigger(new PrototypeAnchor("home", null))]);

        plain.Heading.Should().Be("20px", "the base look is 1.25rem");
        title.Heading.Should().Be("25.6px");
        title.Others.Should().NotBeEmpty().And.Equal(plain.Others);
        screen.Heading.Should().Be("20px", "an edit on the screen no longer reaches into its heading");
    }

    private sealed record Sized(string Heading, string[] Others);
}
