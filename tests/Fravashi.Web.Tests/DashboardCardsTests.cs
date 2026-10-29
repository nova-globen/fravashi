using Bunit;
using Fravashi.Application.Wiki;
using Fravashi.Web.Components.Dashboard;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// T6 dashboard cards: the Wiki sync card's state rendering (skipped/running/failed/succeeded),
/// progress, manual action, and the post-onboarding auto-start trigger. (The repository clone
/// card retired with the in-container clone model, plan 2c decision 6.)
/// </summary>
public class DashboardCardsTests
{
    private static BunitContext Context()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddFluentUIComponents();
        return ctx;
    }

    [Fact]
    public void WikiCard_ShowsLocalOnlyState_WhenNoOnlineWiki()
    {
        var svc = new FakeWikiService
        {
            IsConfiguredValue = false,
            StateValue = WikiSyncState.Empty with { Phase = WikiSyncPhase.Skipped, Message = "Using the local wiki - nothing to sync." },
        };
        var ctx = Context();
        ctx.Services.AddSingleton<IWikiSyncService>(svc);

        var cut = ctx.Render<WikiSyncCard>();

        Assert.Contains("Using the local wiki", cut.Markup);
        Assert.Empty(cut.FindAll("button.studio-link-btn"));
    }

    [Fact]
    public void WikiCard_ShowsRunningProgress_WithCounts()
    {
        var svc = new FakeWikiService
        {
            IsConfiguredValue = true,
            StateValue = new WikiSyncState(
                WikiSyncPhase.Running, "Fetching pages (2/4)…", 2, 4, 0, 0, "Guide", "Confluence", 0, null),
        };
        var ctx = Context();
        ctx.Services.AddSingleton<IWikiSyncService>(svc);

        var cut = ctx.Render<WikiSyncCard>();

        Assert.Contains("Fetching pages (2/4)", cut.Markup);
        Assert.Contains("Guide", cut.Markup);
        Assert.Contains("width:50%", cut.Markup);
    }

    [Fact]
    public void WikiCard_ShowsSuccess_WithPageCount_AndConflictWarning()
    {
        var svc = new FakeWikiService
        {
            IsConfiguredValue = true,
            StateValue = new WikiSyncState(
                WikiSyncPhase.Succeeded, "Synced 3 page(s) from Confluence.", 3, 3, 3, 1, null, "Confluence", 3, DateTimeOffset.UtcNow),
        };
        var ctx = Context();
        ctx.Services.AddSingleton<IWikiSyncService>(svc);

        var cut = ctx.Render<WikiSyncCard>();

        Assert.Contains("Synced 3 page(s)", cut.Markup);
        // Work item #358 (#354): the page count that repeated the sync message is gone.
        Assert.DoesNotContain("page(s) in the local wiki", cut.Markup);
        Assert.Contains("conflict", cut.Markup);
        Assert.NotEmpty(cut.FindAll("[role=alert]"));
    }

    [Fact]
    public void WikiCard_ClickingSyncNow_TriggersSync()
    {
        var svc = new FakeWikiService { IsConfiguredValue = true, StateValue = WikiSyncState.Empty };
        var ctx = Context();
        ctx.Services.AddSingleton<IWikiSyncService>(svc);

        var cut = ctx.Render<WikiSyncCard>();
        cut.Find("button.studio-link-btn").Click();

        Assert.Equal(1, svc.SyncNowCalls);
    }

    [Fact]
    public void WikiCard_OnRender_FiresThePostOnboardingTrigger()
    {
        var svc = new FakeWikiService { IsConfiguredValue = true, StateValue = WikiSyncState.Empty };
        var ctx = Context();
        ctx.Services.AddSingleton<IWikiSyncService>(svc);

        ctx.Render<WikiSyncCard>();

        Assert.Equal(1, svc.EnsureStartedCalls);
    }

    private sealed class FakeWikiService : IWikiSyncService
    {
        public event Action? Changed { add { } remove { } }
        public WikiSyncState StateValue { get; set; } = WikiSyncState.Empty;
        public bool IsConfiguredValue { get; set; }
        public int EnsureStartedCalls { get; private set; }
        public int SyncNowCalls { get; private set; }

        public WikiSyncState State => StateValue;
        public bool IsConfigured => IsConfiguredValue;
        public Task EnsureStartedAsync(CancellationToken cancellationToken = default) { EnsureStartedCalls++; return Task.CompletedTask; }
        public Task SyncNowAsync(CancellationToken cancellationToken = default) { SyncNowCalls++; return Task.CompletedTask; }
    }
}
