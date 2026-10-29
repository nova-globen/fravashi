using Bunit;
using Fravashi.Application.Updates;
using Fravashi.Application.Wiki;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Projects;
using Fravashi.Domain.Wiki;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Wiki;
using Fravashi.Infrastructure.WorkItems;
using Fravashi.Web.Components.Dashboard;
using Fravashi.Web.Components.Pages;
using Fravashi.Web.Components.Updates;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Work item #358, Home's cards: the Updates card's Fravashi offer (Install through the upgrade operation
/// studio_upgrade uses, Ignore until a newer one), the Wiki card's insights (documents by type, drafts, unplanned work
/// items). The Autopilot and Recent dispatches cards are driven by
/// <see cref="AutopilotCardTests"/>, which moved there from the retired autopilot page's own tests; the Progress card's two figures
/// by <see cref="HomeDashboardTests"/>.
/// </summary>
public sealed class HomeCardsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fravashi-home-cards-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---- Updates: Fravashi itself ----------------------------------------------------------------------------------

    private static BunitContext UpdatesContext(FakeStudioUpgrades upgrades, FakeConfigurationStore? settings = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddFluentUIComponents();
        ctx.Services.AddNoUpdates();
        ctx.Services.AddSingleton<IStudioUpgrades>(upgrades);
        ctx.Services.AddSingleton<Fravashi.Application.Persistence.IConfigurationStore>(settings ?? new FakeConfigurationStore());
        return ctx;
    }

    [Fact]
    public void The_updates_card_lists_the_studio_offer_with_install_and_ignore()
    {
        var upgrades = new FakeStudioUpgrades { Offer = new UpgradeOffer("0.9.93", "0.9.94", false, null) };
        using var ctx = UpdatesContext(upgrades);

        var cut = ctx.Render<UpdatesCard>();

        cut.WaitForAssertion(() =>
        {
            var row = cut.Find("[data-testid=update-offer-studio]");
            Assert.Contains("0.9.94", row.TextContent);
            Assert.Contains("installed 0.9.93", row.TextContent);
            Assert.Contains("replaces the studio's container image", row.TextContent);
        });
        Assert.NotNull(cut.Find("[data-testid=update-studio-install]"));
        Assert.NotNull(cut.Find("[data-testid=update-studio-ignore]"));
        // The agent CLI is still on the same card.
        Assert.Contains("Agent CLI", cut.Markup);
        Assert.Contains("No update check has run yet.", cut.Markup);
    }

    [Fact]
    public void A_folder_that_is_not_set_up_is_not_offered_its_projects_releases()
    {
        // Item #373: a folder in no project has no releases; the link would only open the not-set-up state.
        using (var bound = UpdatesContext(new FakeStudioUpgrades()))
        {
            Assert.NotNull(bound.Render<UpdatesCard>().Find("[data-testid=updates-releases-link]"));
        }

        using var ctx = UpdatesContext(new FakeStudioUpgrades());
        ctx.Services.AddSingleton<Fravashi.Application.Projects.ICurrentProject>(new NoProjectHere());
        Assert.Empty(ctx.Render<UpdatesCard>().FindAll("[data-testid=updates-releases-link]"));
    }

    private sealed class NoProjectHere : Fravashi.Application.Projects.ICurrentProject
    {
        public Fravashi.Domain.Projects.ProjectId? Id => null;
    }

    [Fact]
    public async Task Install_asks_first_then_starts_exactly_the_offered_version_through_the_upgrade_operation()
    {
        var upgrades = new FakeStudioUpgrades { Offer = new UpgradeOffer("0.9.93", "0.9.94", false, null) };
        using var ctx = UpdatesContext(upgrades);
        var cut = ctx.Render<UpdatesCard>();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=update-studio-install]")));

        await cut.InvokeAsync(() => cut.Find("[data-testid=update-studio-install]").Click());

        Assert.Empty(upgrades.Applied);
        Assert.Contains("0.9.94", cut.Find("[data-testid=update-studio-confirm]").TextContent);

        await cut.InvokeAsync(() => cut.Find("[data-testid=update-studio-install-confirm]").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(["0.9.94"], upgrades.Applied);
            Assert.Contains("replacing the studio's image", cut.Find("[data-testid=update-studio-message]").TextContent);
        });
    }

    [Fact]
    public async Task A_refused_install_says_why()
    {
        var upgrades = new FakeStudioUpgrades
        {
            Offer = new UpgradeOffer("0.9.93", "0.9.94", false, null),
            Refusal = "An upgrade is already running.",
        };
        using var ctx = UpdatesContext(upgrades);
        var cut = ctx.Render<UpdatesCard>();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=update-studio-install]")));

        await cut.InvokeAsync(() => cut.Find("[data-testid=update-studio-install]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=update-studio-install-confirm]").Click());

        cut.WaitForAssertion(() =>
            Assert.Contains("An upgrade is already running.", cut.Find("[data-testid=update-studio-message]").TextContent));
    }

    [Fact]
    public async Task An_ignored_studio_offer_stays_hidden_until_a_newer_one_is_offered()
    {
        var settings = new FakeConfigurationStore();
        var upgrades = new FakeStudioUpgrades { Offer = new UpgradeOffer("0.9.93", "0.9.94", false, null) };
        using (var ctx = UpdatesContext(upgrades, settings))
        {
            var cut = ctx.Render<UpdatesCard>();
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=update-studio-ignore]")));

            await cut.InvokeAsync(() => cut.Find("[data-testid=update-studio-ignore]").Click());

            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=update-offer-studio]")));
            Assert.Equal("0.9.94", settings.Get(UpdateOfferIgnores.KeyPrefix + UpdateOfferIgnores.Studio));
            Assert.Contains("1 ignored offer hidden", cut.Find("[data-testid=update-ignored]").TextContent);
        }

        // The same version again (a new circuit, a restart): still hidden.
        using (var ctx = UpdatesContext(upgrades, settings))
        {
            var cut = ctx.Render<UpdatesCard>();
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=update-ignored]")));
            Assert.Empty(cut.FindAll("[data-testid=update-offer-studio]"));
        }

        // A newer one: shown again.
        upgrades.Offer = new UpgradeOffer("0.9.93", "0.9.95", false, null);
        using (var ctx = UpdatesContext(upgrades, settings))
        {
            var cut = ctx.Render<UpdatesCard>();
            cut.WaitForAssertion(() => Assert.Contains("0.9.95", cut.Find("[data-testid=update-offer-studio]").TextContent));
        }
    }

    [Fact]
    public void Without_the_upgrade_operation_the_launchers_record_is_shown_with_no_install_button()
    {
        var ctx = new BunitContext();
        using var _ = ctx;
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddFluentUIComponents();
        ctx.Services.AddNoUpdates(new ProductUpdateState(ProductUpdateSeverity.Available, "1.0.0", "1.1.0", null, DateTimeOffset.UtcNow));

        var cut = ctx.Render<UpdatesCard>();

        Assert.Contains("1.1.0", cut.Find("[data-testid=update-offer-studio]").TextContent);
        Assert.Contains("fravashi update", cut.Find("[data-testid=update-offer-studio-console]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=update-studio-install]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=update-studio-ignore]"));
    }

    [Theory]
    [InlineData(null, "0.9.94", false)]
    [InlineData("0.9.94", "0.9.94", true)]
    [InlineData("0.9.94", "0.9.93", true)]
    [InlineData("0.9.94", "0.9.95", false)]
    [InlineData("not-a-version", "0.9.95", false)]
    [InlineData("not-a-version", "not-a-version", true)]
    public void An_ignored_version_hides_only_itself_and_older_ones(string? ignored, string offered, bool hidden) =>
        Assert.Equal(hidden, UpdateOfferIgnores.Hides(ignored, offered));

    private sealed class FakeStudioUpgrades : IStudioUpgrades
    {
        public UpgradeOffer? Offer { get; set; }

        public string? Refusal { get; init; }

        public List<string> Applied { get; } = [];

        public bool Available => true;

        public Task<UpgradeOffer?> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(Offer);

        public Task<string?> ApplyAsync(string version, CancellationToken cancellationToken = default)
        {
            if (Refusal is null)
            {
                Applied.Add(version);
            }

            return Task.FromResult(Refusal);
        }

        public Task<UpgradeProgress?> StatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<UpgradeProgress?>(null);
    }

    // ---- Wiki: insights ---------------------------------------------------------------------------------------------

    private (BunitContext Ctx, SqliteWikiLibrary Wiki, ITracker Board) WikiContext()
    {
        Directory.CreateDirectory(_directory);
        var factory = new SqliteConnectionFactory(new SqliteStoreOptions(Path.Combine(_directory, "test.db"), null, Pooling: false));
        SqliteSchema.EnsureCreated(factory);
        var wiki = new SqliteWikiLibrary(factory, TimeProvider.System, ProjectId.Default);
        var trackers = new LocalTrackerResolver(factory, TimeProvider.System);

        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddFluentUIComponents();
        ctx.Services.AddSingleton<IWikiSyncService>(new StubWikiSyncService());
        ctx.Services.AddSingleton<IWikiLibraries>(new OneWiki(wiki));
        ctx.Services.AddSingleton<ITrackerResolver>(trackers);
        return (ctx, wiki, trackers.For(ProjectId.Default));
    }

    private static async Task<WikiItem> DocAsync(SqliteWikiLibrary wiki, WikiItemType type, string title, string body = "")
    {
        var created = await wiki.CreateAsync(new NewWikiItem { Type = type, Title = title, Body = body });
        return created.Value!;
    }

    private static async Task<WorkItem> ItemAsync(ITracker board, string title, WorkItemStatus status = WorkItemStatus.Ready)
    {
        var created = await board.CreateAsync(
            new NewWorkItem { Level = WorkItemLevel.Task, Title = title, Description = "The problem and the outcome.", Status = status },
            MutationContext.User);
        return created.Value;
    }

    [Fact]
    public async Task The_wiki_card_charts_the_documents_by_type_and_counts_the_drafts()
    {
        var (ctx, wiki, _) = WikiContext();
        using var _ctx = ctx;
        await DocAsync(wiki, WikiItemType.Plan, "Plan one");
        await DocAsync(wiki, WikiItemType.Plan, "Plan two");
        await DocAsync(wiki, WikiItemType.Design, "A screen");
        await DocAsync(wiki, WikiItemType.Draft, "Some thinking");

        var cut = ctx.Render<WikiSyncCard>();

        cut.WaitForAssertion(() => Assert.Equal("4", cut.Find("[data-testid=wiki-insight-documents]").TextContent));
        Assert.Equal("1", cut.Find("[data-testid=wiki-insight-drafts]").TextContent);
        Assert.Equal("/wiki?type=draft", cut.Find("[data-testid=wiki-insight-drafts]").GetAttribute("href"));

        var bars = cut.Find("[data-testid=wiki-insight-by-type]").Children;
        Assert.Equal(3, bars.Length);
        Assert.Equal("wiki-type-plan", bars[0].GetAttribute("data-testid"));
        Assert.Equal("2", bars[0].GetAttribute("data-count"));
        Assert.Equal("/wiki?type=plan", bars[0].QuerySelector("a")!.GetAttribute("href"));
        Assert.Equal("1", cut.Find("[data-testid=wiki-type-design]").GetAttribute("data-count"));
        Assert.Empty(cut.FindAll("[data-testid=wiki-type-adr]"));
        // No wiki is published to in this wiring, so there is no "not on the wiki" figure to show.
        Assert.Empty(cut.FindAll("[data-testid=wiki-insight-unpublished]"));
        Assert.DoesNotContain("page(s) in the local wiki", cut.Markup);
    }

    [Fact]
    public async Task The_wiki_card_counts_the_open_items_no_plan_covers_and_links_each()
    {
        var (ctx, wiki, board) = WikiContext();
        using var _ctx = ctx;
        var coveredByBody = await ItemAsync(board, "Covered by a plan's body");
        var coveredByLink = await ItemAsync(board, "Covered by a plan's typed link");
        var unplanned = await ItemAsync(board, "Nobody planned this");
        await ItemAsync(board, "Finished long ago", WorkItemStatus.Done);
        var mentionedByADraft = await ItemAsync(board, "Only a draft mentions this");

        await DocAsync(wiki, WikiItemType.Plan, "Plan A", $"This plan covers #{coveredByBody.Number}.");
        var planB = await wiki.CreateAsync(new NewWikiItem
        {
            Type = WikiItemType.Plan,
            Title = "Plan B",
            Links = [new WikiLink(WikiLinkKind.WorkItem, coveredByLink.Number.ToString(System.Globalization.CultureInfo.InvariantCulture))],
        });
        Assert.NotNull(planB.Value);
        await DocAsync(wiki, WikiItemType.Draft, "A draft", $"Thinking about #{mentionedByADraft.Number}.");

        var cut = ctx.Render<WikiSyncCard>();

        cut.WaitForAssertion(() => Assert.Equal("2", cut.Find("[data-testid=wiki-insight-unplanned]").TextContent));
        Assert.Equal("/board", cut.Find("[data-testid=wiki-insight-unplanned]").GetAttribute("href"));
        var listed = cut.FindAll("[data-testid=wiki-unplanned-item]");
        Assert.Equal(2, listed.Count);
        Assert.Equal($"/workitems/{unplanned.Number}", listed[0].GetAttribute("href"));
        Assert.Contains("Nobody planned this", listed[0].TextContent);
        Assert.Contains("Only a draft mentions this", listed[1].TextContent);
    }

    [Fact]
    public void An_item_whose_agent_layer_names_a_plan_is_not_unplanned()
    {
        var item = SeededTrackers.Item(ProjectId.Default, 4, WorkItemStatus.Ready);
        var withPlan = WorkItem.Restore(item.ToState() with
        {
            Refs = [new ArtifactRef("r1", ArtifactRefRole.Plan, ArtifactRefKind.Wiki, "@9")],
        }).Value;
        var without = SeededTrackers.Item(ProjectId.Default, 5, WorkItemStatus.InProgress);

        var unplanned = WikiInsights.Unplanned([withPlan, without], []);

        Assert.Equal([5], unplanned.Select(i => i.Number));
    }

    [Theory]
    [InlineData("/#autopilot", "w 1", "/?ws=w%201#autopilot")]
    [InlineData("/git", "w1", "/git?ws=w1")]
    public void A_parked_entry_in_another_folder_keeps_the_cards_fragment_after_the_workspace(string route, string workspace, string expected) =>
        Assert.Equal(expected, WaitingForYouPanel.WithWorkspace(route, workspace));
}
