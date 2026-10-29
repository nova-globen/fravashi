using Bunit;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Web.Components.Sessions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 159: the session pane's browser band. It is there only while the session has a page, shows the page as a
/// picture, maps a person's click on the picture back onto the page's own 1280x800 viewport, sends what a person types
/// straight to the page, and closes the page when asked.
/// </summary>
public sealed class SessionBrowserPaneTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly FakeAgentBrowser _browser = new();
    private readonly AgentSessionId _session = AgentSessionId.New();

    public SessionBrowserPaneTests()
    {
        _ctx.Services.AddSingleton<IAgentBrowser>(_browser);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Nothing_is_shown_until_the_session_has_a_page_and_then_its_address_and_picture_are()
    {
        var cut = _ctx.Render<SessionBrowserPane>(p => p.Add(c => c.Session, _session));
        Assert.Empty(cut.FindAll("[data-testid=session-browser]"));

        _browser.Show(_session, "https://example.com/docs", "Docs");

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("https://example.com/docs", cut.Find("[data-testid=session-browser-url]").TextContent);
            Assert.StartsWith("data:image/jpeg;base64,", cut.Find("[data-testid=session-browser-frame]").GetAttribute("src"));
        });
    }

    [Fact]
    public async Task A_click_on_the_picture_lands_on_the_same_spot_of_the_page()
    {
        _browser.Show(_session, "https://example.com/", "Example");
        _browser.FramesToServe = 1; // one picture to click on, and no re-render racing the click after it
        _ctx.JSInterop.Setup<double[]>("fravashiBrowserPane.size", _ => true).SetResult([640.0, 400.0]);
        var cut = _ctx.Render<SessionBrowserPane>(p => p.Add(c => c.Session, _session));
        cut.WaitForAssertion(() => cut.Find("[data-testid=session-browser-frame]"));

        // The picture is drawn at half size, so a click at (320, 100) is the page's (640, 200).
        await ActAsync(cut, () => cut.Find("[data-testid=session-browser-frame]")
            .Click(new MouseEventArgs { OffsetX = 320, OffsetY = 100 }));

        cut.WaitForAssertion(() => Assert.Equal([(640.0, 200.0)], _browser.Clicks));
    }

    [Fact]
    public async Task What_a_person_types_goes_to_the_page_hidden_on_screen_and_Enter_and_Close_do_what_they_say()
    {
        _browser.Show(_session, "https://example.com/login", "Sign in");
        _browser.FramesToServe = 0; // typing needs no picture, and a picture's re-render would race each event
        var cut = _ctx.Render<SessionBrowserPane>(p => p.Add(c => c.Session, _session));
        cut.WaitForAssertion(() => cut.Find("[data-testid=session-browser-input]"));

        Assert.Equal("password", cut.Find("[data-testid=session-browser-input]").GetAttribute("type"));
        await ActAsync(cut, () => cut.Find("[data-testid=session-browser-input]").Input("hunter2"));
        await ActAsync(cut, () => cut.Find("[data-testid=session-browser-send]").Click());
        await ActAsync(cut, () => cut.Find("[data-testid=session-browser-enter]").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(["hunter2"], _browser.Typed);
            Assert.Equal(1, _browser.Presses);
            Assert.Equal(string.Empty, cut.Find("[data-testid=session-browser-input]").GetAttribute("value") ?? string.Empty);
        });

        await ActAsync(cut, () => cut.Find("[data-testid=session-browser-close]").Click());
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=session-browser]")));
        Assert.Null(_browser.Current(_session));
    }

    /// <summary>
    /// Finds and acts on the renderer's thread. What keeps it from racing is the fake serving no further pictures while
    /// a test acts (<see cref="FakeAgentBrowser.FramesToServe"/>): the pane re-renders on each picture, and two attempts
    /// at dodging that failed on loaded runners, wrapping in <c>InvokeAsync</c> and then retrying, because bunit keeps
    /// a stale handler as the renderer's unhandled exception and rethrows it on the next event.
    /// </summary>
    private static Task ActAsync(IRenderedComponent<SessionBrowserPane> cut, Action act) => cut.InvokeAsync(act);
}
