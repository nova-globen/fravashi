using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Web.Tests;

/// <summary>
/// An <see cref="IAgentBrowser"/> for component tests (backlog 159): it holds one page per session in memory, so the
/// session pane's browser band can be rendered, clicked and typed into without a real Chromium.
/// </summary>
public sealed class FakeAgentBrowser : IAgentBrowser
{
    private readonly Dictionary<AgentSessionId, BrowserPageState> _pages = [];

    public List<string> Typed { get; } = [];

    public List<(double X, double Y)> Clicks { get; } = [];

    public int Presses { get; private set; }

    public bool IsAvailable => true;

    public string? UnavailableReason => null;

    public event Action<AgentSessionId>? Changed;

    public void Show(AgentSessionId session, string url, string title)
    {
        _pages[session] = new BrowserPageState(url, title, 200, [], []);
        Changed?.Invoke(session);
    }

    public BrowserPageState? Current(AgentSessionId session) => _pages.GetValueOrDefault(session);

    public Task<BrowserOutcome> OpenAsync(AgentSessionId session, string url, CancellationToken cancellationToken = default)
    {
        Show(session, url, url);
        return Task.FromResult(new BrowserOutcome(_pages[session]));
    }

    public Task<(BrowserOutcome Outcome, string? Content, bool Truncated)> ReadAsync(
        AgentSessionId session, BrowserReadMode mode, int max, CancellationToken cancellationToken = default) =>
        Task.FromResult((new BrowserOutcome(Current(session)), (string?)"page text", false));

    public Task<BrowserOutcome> ClickAsync(AgentSessionId session, BrowserTarget target, CancellationToken cancellationToken = default) =>
        Task.FromResult(new BrowserOutcome(Current(session)));

    public Task<BrowserOutcome> FillAsync(
        AgentSessionId session, BrowserTarget target, string text, bool submit, CancellationToken cancellationToken = default) =>
        Task.FromResult(new BrowserOutcome(Current(session)));

    public Task<BrowserOutcome> PressAsync(AgentSessionId session, string key, CancellationToken cancellationToken = default)
    {
        Presses++;
        return Task.FromResult(new BrowserOutcome(Current(session)));
    }

    public Task<(BrowserOutcome Outcome, byte[]? Jpeg)> ScreenshotAsync(
        AgentSessionId session, bool fullPage, CancellationToken cancellationToken = default) =>
        Task.FromResult((new BrowserOutcome(Current(session)), (byte[]?)[0xFF, 0xD8, 0xFF]));

    public Task CloseAsync(AgentSessionId session)
    {
        _pages.Remove(session);
        Changed?.Invoke(session);
        return Task.CompletedTask;
    }

    /// <summary>
    /// How many pictures to serve before answering "no new picture", or null for every time. The pane re-renders on
    /// each picture, every second and a half, and a test that acts on the pane must not race those renders: bunit
    /// records a stale handler as the renderer's unhandled exception and rethrows it on the next event, so no retry
    /// gets past it (seen on the release runner 2026-09-29, after a retry had been taken for the fix).
    /// </summary>
    public int? FramesToServe { get; set; }

    public Task<byte[]?> FrameAsync(AgentSessionId session, CancellationToken cancellationToken = default)
    {
        if (!_pages.ContainsKey(session) || FramesToServe is <= 0)
        {
            return Task.FromResult<byte[]?>(null);
        }

        if (FramesToServe is { } left)
        {
            FramesToServe = left - 1;
        }

        return Task.FromResult<byte[]?>([0xFF, 0xD8, 0xFF, 0xE0]);
    }

    public Task<BrowserOutcome> ClickAtAsync(AgentSessionId session, double x, double y, CancellationToken cancellationToken = default)
    {
        Clicks.Add((x, y));
        return Task.FromResult(new BrowserOutcome(Current(session)));
    }

    public Task<BrowserOutcome> TypeTextAsync(AgentSessionId session, string text, CancellationToken cancellationToken = default)
    {
        Typed.Add(text);
        return Task.FromResult(new BrowserOutcome(Current(session)));
    }
}
