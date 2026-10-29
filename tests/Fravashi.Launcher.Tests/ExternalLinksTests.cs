using Fravashi.Launcher;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests;

/// <summary>
/// Which new windows the studio window hands to the person's browser (seen 2026-10-02: the Claude sign-in link opened a
/// bare WebView window that holds none of the person's sign-ins).
/// </summary>
public sealed class ExternalLinksTests
{
    private const string Studio = "http://localhost:9090/code?ws=9e4d0a84e941ace1";

    [Theory]
    [InlineData("https://claude.com/cai/oauth/authorize?code=true")]
    [InlineData("https://dev.azure.com/nova-globen-ab/Fravashi/_workitems/edit/157")]
    [InlineData("http://example.com/")]
    [InlineData("http://localhost:3000/")]
    public void A_web_address_that_is_not_the_studio_opens_in_the_browser(string link) =>
        ExternalLinks.OpensInBrowser(link, Studio).Should().BeTrue();

    [Theory]
    [InlineData("http://localhost:9090/wiki/1")]
    [InlineData("HTTP://LOCALHOST:9090/board")]
    public void The_studio_itself_never_leaves_the_window(string link) =>
        ExternalLinks.OpensInBrowser(link, Studio).Should().BeFalse("a browser is refused there by the window-only gate");

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:privacy")]
    [InlineData("not a url")]
    [InlineData(null)]
    public void Nothing_but_http_and_https_is_handed_out(string? link) =>
        ExternalLinks.OpensInBrowser(link, Studio).Should().BeFalse();
}
