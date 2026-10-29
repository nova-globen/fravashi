using Bunit;
using Fravashi.Application.Updates;
using Fravashi.Domain.AgentSessions;
using Fravashi.Web.Components.Updates;
using Fravashi.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// T5 update surfaces: the Type A tool-update card (available list, checkbox + button, progress, empty state),
/// the Type B available banner + critical lock panel, and the lock's path policy.
/// </summary>
public class UpdateComponentsTests
{
    private static BunitContext Context(FakeToolUpdateService? tools = null, IProductUpdateStatus? status = null, IProductInfo? product = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddFluentUIComponents();
        ctx.Services.AddSingleton<IToolUpdateService>(tools ?? new FakeToolUpdateService());
        ctx.Services.AddSingleton(status ?? new FakeProductUpdateStatus(ProductUpdateState.None));
        ctx.Services.AddSingleton(product ?? new FakeProductInfo("1.0.0", "full"));
        return ctx;
    }

    /// <summary>A row whose installed CLI drifted off the pin: a real repair the card may offer.</summary>
    private static ToolUpdateInfo OffPin(AgentTool tool, string name, string installed, string pinned) =>
        new(tool, name, "pkg", pinned, installed, pinned, NewerPublished: false, OffPin: true, Note: null);

    /// <summary>A row at the pin with a newer release published: reported, never offered (ADR 0023).</summary>
    private static ToolUpdateInfo NewerPublished(AgentTool tool, string name, string pinned, string latest) =>
        new(tool, name, "pkg", pinned, pinned, latest, NewerPublished: true, OffPin: false, Note: null);

    // --- Type A: the tool-update card ---

    [Fact]
    public void Card_offers_a_repair_only_when_the_installed_cli_drifted_off_the_pin()
    {
        var tools = new FakeToolUpdateService();
        tools.AvailableList.Add(OffPin(AgentTool.Claude, "Claude Code", "1.0.0", "1.2.0"));
        var cut = Context(tools).Render<UpdatesCard>();

        Assert.Contains("Claude Code", cut.Markup);
        Assert.Contains("1.0.0", cut.Markup);
        Assert.Contains("1.2.0", cut.Markup);
        Assert.Single(cut.FindAll("input[type=checkbox]"));
        Assert.Single(cut.FindAll("button.tool-update-btn"));
    }

    [Fact]
    public void Card_reports_a_newer_release_without_offering_to_install_it()
    {
        // The UI-layer guard for "promotion is a deliberate act": the row is honest that something newer
        // exists and gives the user nothing to click that would install it.
        var tools = new FakeToolUpdateService();
        tools.AvailableList.Add(NewerPublished(AgentTool.Claude, "Claude Code", "2.1.222", "2.2.0"));
        var cut = Context(tools).Render<UpdatesCard>();

        Assert.Contains("2.1.222", cut.Markup);
        Assert.Contains("2.2.0", cut.Markup);
        Assert.NotEmpty(cut.FindAll("[data-testid=tool-update-pinned-Claude]"));
        Assert.Empty(cut.FindAll("button.tool-update-btn"));
        Assert.Empty(cut.FindAll("input[type=checkbox]"));
    }

    [Fact]
    public void The_pinned_and_published_versions_are_separated_by_a_space()
    {
        // Work item #363: Razor drops the whitespace beside an @if block, and the row read
        // "Pinned 2.1.222published 2.2.0" in the window. The text a person reads is what is held here.
        var tools = new FakeToolUpdateService();
        tools.AvailableList.Add(NewerPublished(AgentTool.Claude, "Claude Code", "2.1.222", "2.2.0"));
        var cut = Context(tools).Render<UpdatesCard>();

        var versions = cut.Find("[data-testid=update-offer-cli-Claude] .tool-update-versions").TextContent;
        Assert.Contains("Pinned 2.1.222 published 2.2.0", System.Text.RegularExpressions.Regex.Replace(versions, @"\s+", " "));
        Assert.DoesNotContain("2.1.222published", versions);
    }

    [Fact]
    public void Card_shows_the_pinned_empty_state()
    {
        var cut = Context().Render<UpdatesCard>();
        Assert.Contains("at their pinned versions", cut.Markup);
    }

    [Fact]
    public void Card_update_button_triggers_the_update_and_shows_progress()
    {
        var tools = new FakeToolUpdateService();
        tools.AvailableList.Add(OffPin(AgentTool.Codex, "OpenAI Codex", "2.0.0", "2.1.0"));
        var cut = Context(tools).Render<UpdatesCard>();

        cut.Find("button.tool-update-btn").Click();

        Assert.Contains(AgentTool.Codex, tools.Updated);
        Assert.Contains("tool-update-progress", cut.Markup);
        Assert.Contains("Reinstalled", cut.Markup);
    }

    [Fact]
    public void Card_check_now_invokes_a_live_check()
    {
        var tools = new FakeToolUpdateService();
        var cut = Context(tools).Render<UpdatesCard>();

        cut.Find(".dash-panel-head button").Click();

        Assert.Equal(1, tools.Checks);
    }

    // --- Type B: the available banner ---

    [Fact]
    public void Banner_shows_for_an_available_update_and_dismisses()
    {
        var status = new FakeProductUpdateStatus(new ProductUpdateState(
            ProductUpdateSeverity.Available, "1.0.0", "1.1.0", "https://example/notes", DateTimeOffset.UtcNow));
        var cut = Context(status: status).Render<ProductUpdateBanner>();

        Assert.Contains("1.1.0", cut.Markup);
        Assert.Contains("fravashi update", cut.Markup);
        cut.Find(".product-update-banner-dismiss").Click();
        Assert.Equal(string.Empty, cut.Markup.Trim());
    }

    [Fact]
    public void Banner_is_absent_when_no_update_is_pending()
    {
        var cut = Context().Render<ProductUpdateBanner>();
        Assert.Equal(string.Empty, cut.Markup.Trim());
    }

    [Fact]
    public void Banner_says_the_channel_could_not_be_checked_with_its_reason_and_since_when()
    {
        // Backlog 230: a launcher that could not reach the channel records it beside the state; never "up to date".
        var since = DateTimeOffset.UtcNow.AddMinutes(-3);
        var status = new FakeProductUpdateStatus(ProductUpdateState.None with
        {
            ChannelUnreachableSince = since,
            ChannelUnreachableReason = "name not resolved",
        });
        var cut = Context(status: status).Render<ProductUpdateBanner>();

        var banner = cut.Find("[data-testid=product-update-unreachable]");
        Assert.Contains($"Could not check for updates since {PersonTime.Format(since, null, DateTimeOffset.UtcNow)}", banner.TextContent);
        Assert.Contains("(name not resolved)", banner.TextContent);
        Assert.Contains("unknown", banner.TextContent);
    }

    // --- Backlog 242: what the card says about the manifest's signature ---

    [Fact]
    public void Banner_says_an_offered_update_came_from_a_manifest_signed_by_the_release_key()
    {
        var status = new FakeProductUpdateStatus(new ProductUpdateState(
            ProductUpdateSeverity.Available, "1.0.0", "1.1.0", null, DateTimeOffset.UtcNow) { ManifestVerified = true });
        var cut = Context(status: status).Render<ProductUpdateBanner>();

        Assert.Contains("signed by this product's release key", cut.Find("[data-testid=product-update-verified]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=product-update-unsigned]"));
    }

    [Theory]
    [InlineData(ManifestSignatureRefusal.Unsigned, "no signature was published beside it")]
    [InlineData(ManifestSignatureRefusal.BadSignature, "the signature beside it does not match it")]
    public void Banner_says_a_refused_manifest_is_not_signed_and_that_nothing_from_it_is_used(ManifestSignatureRefusal refused, string why)
    {
        var since = DateTimeOffset.UtcNow.AddMinutes(-3);
        var status = new FakeProductUpdateStatus(ProductUpdateState.None with
        {
            ChannelUnreachableSince = since,
            ChannelUnreachableReason = "the update channel's manifest is not signed by this product's release key, so nothing from it is used (" + why + ")",
            ManifestRefused = refused,
        });
        var cut = Context(status: status).Render<ProductUpdateBanner>();

        var banner = cut.Find("[data-testid=product-update-unsigned]");
        Assert.Contains("The update channel's manifest is not signed by this product's release key, so nothing from it is used", banner.TextContent);
        Assert.Contains($"({why}, since {PersonTime.Format(since, null, DateTimeOffset.UtcNow)})", banner.TextContent);
        Assert.Contains("unknown", banner.TextContent);
        Assert.Empty(cut.FindAll("[data-testid=product-update-unreachable]"));
    }

    [Fact]
    public void Lock_panel_says_a_refused_manifest_neither_raises_nor_lifts_the_recorded_requirement()
    {
        var status = new FakeProductUpdateStatus(new ProductUpdateState(
            ProductUpdateSeverity.Critical, "1.0.0", "2.0.0", null, DateTimeOffset.UtcNow)
        {
            ChannelUnreachableSince = DateTimeOffset.UtcNow,
            ManifestRefused = ManifestSignatureRefusal.BadSignature,
        });
        var cut = Context(status: status).Render<UpdateRequiredPanel>();

        var note = cut.Find("[data-testid=update-lock-unsigned]");
        Assert.Contains("not signed by this product's release key", note.TextContent);
        Assert.Contains("last verified check", note.TextContent);
        Assert.Empty(cut.FindAll("[data-testid=update-lock-unreachable]"));
    }

    [Fact]
    public void Lock_panel_keeps_the_requirement_when_the_channel_cannot_be_checked()
    {
        var status = new FakeProductUpdateStatus(new ProductUpdateState(
            ProductUpdateSeverity.Critical, "1.0.0", "2.0.0", null, DateTimeOffset.UtcNow)
        {
            ChannelUnreachableSince = DateTimeOffset.UtcNow,
            ChannelUnreachableReason = "no network route",
        });
        var cut = Context(status: status).Render<UpdateRequiredPanel>();

        Assert.Contains("2.0.0", cut.Markup);
        Assert.Contains("still stands", cut.Find("[data-testid=update-lock-unreachable]").TextContent);
    }

    // --- Backlog 246: the update surfaces say the person's hour, not the container's ---

    private static readonly DateTimeOffset Noon = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static BunitContext InZone(string zone, FakeToolUpdateService? tools = null, IProductUpdateStatus? status = null)
    {
        var ctx = Context(tools, status);
        ctx.Services.AddSingleton(PersonZones.Fixed(zone));
        ctx.Services.AddSingleton<TimeProvider>(new FixedClock(Noon.AddMinutes(5)));
        return ctx;
    }

    [Theory]
    [InlineData("Europe/Stockholm", "14:00")]
    [InlineData("America/Los_Angeles", "05:00")]
    [InlineData("Asia/Kolkata", "17:30")]
    public void Banner_and_lock_panel_say_since_in_the_persons_zone(string zone, string hour)
    {
        var banner = InZone(zone, status: new FakeProductUpdateStatus(ProductUpdateState.None with
        {
            ChannelUnreachableSince = Noon,
            ChannelUnreachableReason = "name not resolved",
        })).Render<ProductUpdateBanner>();
        Assert.Contains(
            $"Could not check for updates since {hour} (name not resolved). Whether a newer Fravashi exists is unknown.",
            System.Text.RegularExpressions.Regex.Replace(banner.Find("[data-testid=product-update-unreachable]").TextContent, @"\s+", " "),
            StringComparison.Ordinal);

        var lockPanel = InZone(zone, status: new FakeProductUpdateStatus(new ProductUpdateState(
            ProductUpdateSeverity.Critical, "1.0.0", "2.0.0", null, Noon)
        {
            ChannelUnreachableSince = Noon,
            ChannelUnreachableReason = "no network route",
        })).Render<UpdateRequiredPanel>();
        Assert.Equal(hour, lockPanel.Find("[data-testid=update-lock-since]").TextContent);
    }

    [Theory]
    [InlineData("Europe/Stockholm", "2026-10-07 14:00")]
    [InlineData("Asia/Kolkata", "2026-10-07 17:30")]
    public void Tool_update_card_says_last_checked_in_the_persons_zone(string zone, string said)
    {
        var tools = new FakeToolUpdateService { LastCheckedUtc = Noon };
        var cut = InZone(zone, tools).Render<UpdatesCard>();

        Assert.Equal(said, cut.Find("[data-testid=tool-updates-checked-at]").TextContent);
    }

    [Fact]
    public void An_update_time_on_another_local_day_carries_its_date()
    {
        // 23:30 UTC on the 6th is 01:30 on the 7th in Stockholm: today there, so only the hour. In Los Angeles it is
        // 16:30 on the 6th, the day before the person's today, so the date is said with it.
        var lateUtc = new DateTimeOffset(2026, 10, 6, 23, 30, 0, TimeSpan.Zero);
        var status = new FakeProductUpdateStatus(ProductUpdateState.None with
        {
            ChannelUnreachableSince = lateUtc,
            ChannelUnreachableReason = "name not resolved",
        });

        Assert.Equal("01:30", InZone("Europe/Stockholm", status: status).Render<ProductUpdateBanner>()
            .Find("[data-testid=update-banner-since]").TextContent);
        Assert.Equal("2026-10-06 16:30", InZone("America/Los_Angeles", status: status).Render<ProductUpdateBanner>()
            .Find("[data-testid=update-banner-since]").TextContent);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // --- Type B: the critical lock panel ---

    [Fact]
    public void Lock_panel_explains_the_update_and_shows_versions()
    {
        var status = new FakeProductUpdateStatus(new ProductUpdateState(
            ProductUpdateSeverity.Critical, "1.0.0", "2.0.0", "https://example/notes", DateTimeOffset.UtcNow));
        var cut = Context(status: status, product: new FakeProductInfo("1.0.0", "full")).Render<UpdateRequiredPanel>();

        Assert.Contains("fravashi update", cut.Markup);
        Assert.Contains("2.0.0", cut.Markup);
    }

    // --- Type B: the lock path policy ---

    [Theory]
    [InlineData("/", false)]
    [InlineData("/board", false)]
    [InlineData("/settings/framework", false)]
    [InlineData("/update-required", true)]
    [InlineData("/healthz", true)]
    [InlineData("/_blazor", true)]
    // Backlog 283: no path is exempt for looking like a file; StudioGates lets a real static asset through.
    [InlineData("/app.css", false)]
    [InlineData("/code/README.md", false)]
    [InlineData("/git/history/x.json", false)]
    [InlineData("/Error", true)]
    [InlineData("/Error/", true)]
    public void Lock_exempts_only_infrastructure_and_the_lock_page(string path, bool exempt) =>
        Assert.Equal(exempt, CriticalUpdateLock.IsExempt(path));

    private sealed class FakeToolUpdateService : IToolUpdateService
    {
        public event Action? Changed;
        public DateTimeOffset? LastCheckedUtc { get; set; } = DateTimeOffset.UtcNow;
        public bool IsChecking { get; set; }
        public List<ToolUpdateInfo> AvailableList { get; } = new();
        public IReadOnlyList<ToolUpdateInfo> Available => AvailableList;
        public Dictionary<AgentTool, ToolUpdateProgress> ProgressMap { get; } = new();
        public List<AgentTool> Updated { get; } = new();
        public int Checks { get; private set; }

        public ToolUpdateProgress? ProgressFor(AgentTool tool) => ProgressMap.TryGetValue(tool, out var p) ? p : null;

        public Task<IReadOnlyList<ToolUpdateInfo>> CheckNowAsync(CancellationToken cancellationToken = default)
        {
            Checks++;
            Changed?.Invoke();
            return Task.FromResult(Available);
        }

        public Task UpdateAsync(AgentTool tool, CancellationToken cancellationToken = default)
        {
            Updated.Add(tool);
            ProgressMap[tool] = new ToolUpdateProgress(tool, ToolUpdatePhase.Succeeded, ["Reinstalled"], DateTimeOffset.UtcNow);
            Changed?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProductUpdateStatus(ProductUpdateState state) : IProductUpdateStatus
    {
        public ProductUpdateState Current => state;
    }

    private sealed class FakeProductInfo(string version, string variant) : IProductInfo
    {
        public string Version => version;
        public string Variant => variant;
    }
}
