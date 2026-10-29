using Bunit;
using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.Notifications;
using Fravashi.Application.Onboarding;
using Fravashi.Application.Persistence;
using Fravashi.Infrastructure.AgentRuntimes;
using Fravashi.Infrastructure.Notifications;
using Fravashi.Web.Components.Pages;
using Fravashi.Web.Components.Settings;
using Fravashi.Web.Components.Shell;
using Fravashi.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// T1 - the Settings shell: the bottom-rail entry point, the cockpit-style tabs, the Environment tab's
/// /sessions-style step sidebar (reusing the same wizard step components without forking them), and the
/// Framework tab's honest empty shell.
/// </summary>
public class SettingsPageTests
{
    private static BunitContext PageContext(
        out FakeConfigurationStore config,
        out FakeProviderConnectionStore connections,
        FakeOnboardingState? onboarding = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddFluentUIComponents();

        config = new FakeConfigurationStore();
        connections = new FakeProviderConnectionStore();

        ctx.Services.AddSingleton<IConfigurationStore>(config);
        ctx.Services.AddSingleton<IProviderConnectionStore>(connections);
        ctx.Services.AddSingleton<ISecretStore>(new FakeSecretStore());
        ctx.Services.AddSingleton<IOnboardingState>(onboarding ?? new FakeOnboardingState(complete: true));
        ctx.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        // The Environment overview lists the stored git hosts (plan 2c).
        ctx.Services.AddSingleton<Fravashi.Application.Persistence.IGitHostStore>(new FakeGitHostStore());
        ctx.Services.AddSingleton<IAgentRuntimeService>(new FakeAgentRuntimeService());
        ctx.Services.AddSingleton<IAgentRuntimeStatusReport>(new InMemoryAgentRuntimeStatusReport());

        // Access probes + suggester the wiki/board/mapping steps inject (any section may render).
        ctx.Services.AddSingleton<IWikiAccessProbe>(new FakeWikiAccessProbe(ProbeResults.Verified));
        ctx.Services.AddSingleton<IBoardAccessProbe>(new FakeBoardAccessProbe(ProbeResults.Verified));
        ctx.Services.AddSingleton<IBoardStatesProbe>(new FakeBoardStatesProbe(BoardStatesResult.Failed("n/a")));
        ctx.Services.AddSingleton<IBoardMappingSuggester>(
            new FakeBoardMappingSuggester(BoardMappingSuggestion.Failed("no runtime")));

        ctx.Services.AddSingleton(new Infrastructure.Persistence.DataDirectory(Path.GetTempPath()));
        // The Environment overview hosts the sandbox-posture card (F4); with no posture file it renders the
        // honest "outside a container" state.
        ctx.Services.AddSingleton<Fravashi.Application.Sandbox.ISandboxPosture>(
            new Fravashi.Infrastructure.Sandbox.FileSandboxPosture(
                new Infrastructure.Persistence.DataDirectory(Path.GetTempPath()),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Fravashi.Infrastructure.Sandbox.FileSandboxPosture>.Instance));
        // The Environment overview also hosts the integrity self-check card (F3); with no evaluation yet it renders
        // the honest report-only state.
        ctx.Services.AddSingleton<Fravashi.Application.Integrity.IIntegrityStatusProvider>(
            new Fravashi.Infrastructure.Integrity.IntegrityStatusProvider());
        // Backlog 252: the overview's host-bridge row; with no bridge configured (as here) it says nothing.
        ctx.Services.AddSingleton(new Fravashi.Infrastructure.Bridge.BridgeReachMonitor(
            new ConfigurationBuilder().Build(), new Fravashi.Infrastructure.Bridge.HttpBridgeReachProbe()));
        ctx.Services.AddScoped<OnboardingDraft>();
        ctx.Services.AddScoped<ThemeState>();
        ctx.Services.AddScoped<AgentPanelState>();
        ctx.Services.AddScoped<ScratchpadState>();
        ctx.Services.AddProviderSignInForTests();
        ctx.Services.AddSingleton<INotificationHistory>(new InMemoryNotificationHistory());
        // Backlog 74: the Environment tab's Diagnostics section (opt-in product events + the bundle).
        ctx.Services.AddSingleton<Fravashi.Application.Telemetry.IProductEventStore>(new NoEventStore());
        ctx.Services.AddSingleton<Fravashi.Application.Telemetry.IDiagnosticBundleWriter>(new NoBundleWriter());
        return ctx;
    }

    /// <summary>An install that has recorded nothing, which is every install until someone opts in.</summary>
    private sealed class NoEventStore : Fravashi.Application.Telemetry.IProductEventStore
    {
        public void Append(Fravashi.Domain.Telemetry.ProductEvent productEvent)
        {
        }

        public IReadOnlyList<Fravashi.Domain.Telemetry.ProductEvent> Recent(int max = 2000) => [];

        public IReadOnlyList<Fravashi.Domain.Telemetry.ProductEvent> Since(
            Fravashi.Domain.Telemetry.ProductEventKind kind, DateTimeOffset fromUtc, int max = 20000) => [];

        public long Count() => 0;
    }

    private sealed class NoBundleWriter : Fravashi.Application.Telemetry.IDiagnosticBundleWriter
    {
        public Fravashi.Application.Telemetry.DiagnosticBundle Write() =>
            new("/data/diagnostics/none.zip", 0, []);
    }

    // -- UI1: Settings entry point at the bottom of the activity rail --

    [Fact]
    public void Activity_rail_has_a_settings_button_that_navigates_to_settings()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        // The rail's bottom group also hosts the agent-panel toggle, the theme toggle and the
        // notification center.
        ctx.Services.AddSingleton<ThemeState>();
        ctx.Services.AddSingleton<AgentPanelState>();
        ctx.Services.AddScoped<ScratchpadState>();
        ctx.Services.AddSingleton<INotificationHistory>(new InMemoryNotificationHistory());
        ctx.Services.AddSingleton<IWaitingForPersonList>(new StubWaitingForPersonList());

        var cut = ctx.Render<ActivityBar>(p => p.Add(c => c.CurrentArea, StudioArea.Home));

        var button = cut.Find("[data-testid=activitybar-settings]");
        Assert.Equal("/settings", button.GetAttribute("href"));
    }

    // -- Work item #357: one page, one sidebar grouped by purpose, no tabs --

    /// <summary>The approved layout, group by group, in sidebar order.</summary>
    public static TheoryData<string, string> SectionsByGroup => new()
    {
        { "overview", "general" },
        { "runtimes", "agents" },
        { "models", "agents" },
        { "reviewer", "agents" },
        { "spending", "agents" },
        { "concurrency", "agents" },
        { "framework", "agents" },
        { "project", "project" },
        { "board", "project" },
        { "wiki", "project" },
        { "git-hosts", "connections" },
        { "broker", "connections" },
        { "data", "data" },
        { "backup", "data" },
        { "evidence", "data" },
        { "search", "advanced" },
        { "diagnostics", "advanced" },
    };

    [Fact]
    public void The_sidebar_has_the_six_groups_in_order_and_no_tabs_or_step_numbers()
    {
        using var ctx = PageContext(out _, out _);

        var cut = ctx.Render<SettingsPage>();

        var headings = cut.FindAll("[data-testid=settings-sidebar] .page-sidebar-heading").Select(h => h.TextContent.Trim());
        Assert.Equal(new[] { "General", "Agents", "Project", "Connections", "Data", "Advanced" }, headings);
        Assert.Empty(cut.FindAll("[data-testid^=settings-tab-]"));
        Assert.Empty(cut.FindAll("[data-testid=settings-sidebar] .page-sidebar-marker"));
        Assert.Equal(SettingsSections.All.Count, cut.FindAll("[data-testid^=settings-nav-]").Count);

        // A bare /settings is the overview.
        Assert.Contains("active", cut.Find("[data-testid=settings-nav-overview]").GetAttribute("class"));
        Assert.NotEmpty(cut.FindAll("[data-testid=settings-overview]"));
    }

    [Theory]
    [MemberData(nameof(SectionsByGroup))]
    public void Every_section_sits_under_its_group_and_links_to_its_own_address(string slug, string group)
    {
        using var ctx = PageContext(out _, out _);

        var cut = ctx.Render<SettingsPage>();

        var link = cut.Find($"[data-testid=settings-nav-{slug}]");
        Assert.Equal($"/settings/{slug}", link.GetAttribute("href"));

        // The nearest group heading above the link is its group.
        var entries = cut.Find("[data-testid=settings-sidebar]")
            .QuerySelectorAll(".page-sidebar-heading, .page-sidebar-item").ToList();
        var index = entries.FindIndex(e => e.GetAttribute("data-testid") == $"settings-nav-{slug}");
        var heading = entries.Take(index).Last(e => e.ClassList.Contains("page-sidebar-heading"));
        Assert.Equal($"settings-group-{group}", heading.GetAttribute("data-testid"));
    }

    [Theory]
    [InlineData("overview", "settings-overview")]
    [InlineData("runtimes", "runtime-card-Claude")]
    [InlineData("models", "default-models-settings")]
    [InlineData("reviewer", "commit-reviewer-settings")]
    [InlineData("spending", "spend-settings")]
    [InlineData("concurrency", "autopilot-concurrency-settings")]
    [InlineData("framework", "framework-detail")]
    [InlineData("board", "board-provider")]
    [InlineData("wiki", "wiki-provider")]
    [InlineData("git-hosts", "git-hosts-settings")]
    [InlineData("broker", "broker-settings")]
    [InlineData("data", "data-directory")]
    [InlineData("backup", "backup-settings")]
    [InlineData("evidence", "evidence-settings")]
    [InlineData("search", "search-settings")]
    [InlineData("diagnostics", "diagnostics-settings")]
    public void Every_section_renders_at_its_address(string slug, string testId)
    {
        using var ctx = PageContext(out _, out _);
        AddFrameworkServices(ctx);
        ctx.Services.AddSingleton<Fravashi.Application.Backup.IStudioBackupWriter>(new BackupSettingsSectionTests.RecordingBackupWriter());
        ctx.Services.AddSingleton<Fravashi.Application.Evidence.IEvidenceLifecycle>(new EvidenceSettingsSectionTests.FakeEvidence());

        var cut = ctx.Render<SettingsPage>(p => p.Add(c => c.Section, slug));

        Assert.NotEmpty(cut.FindAll($"[data-testid={testId}]"));
        Assert.Contains("active", cut.Find($"[data-testid=settings-nav-{slug}]").GetAttribute("class"));
    }

    [Fact]
    public void Board_and_status_mapping_is_one_page_with_the_board_first()
    {
        using var ctx = PageContext(out _, out _);

        var cut = ctx.Render<SettingsPage>(p => p.Add(c => c.Section, "board"));

        var markup = cut.Find("[data-testid=board-settings]").InnerHtml;
        var board = markup.IndexOf("board-provider", StringComparison.Ordinal);
        var mapping = markup.IndexOf("board-settings-mapping", StringComparison.Ordinal);
        Assert.True(board >= 0 && mapping > board, "the board connection comes first, then its status mapping");
        Assert.NotEmpty(cut.FindAll("[data-testid=board-settings-mapping] h2"));
    }

    [Fact]
    public void An_unknown_section_falls_back_to_the_overview()
    {
        using var ctx = PageContext(out _, out _);

        var cut = ctx.Render<SettingsPage>(p => p.Add(c => c.Section, "nonsense"));

        Assert.NotEmpty(cut.FindAll("[data-testid=settings-overview]"));
    }

    // -- Work item #357: every old address redirects to its new place --

    [Theory]
    [InlineData("/settings/environment", "/settings/overview")]
    [InlineData("/settings/environment/overview", "/settings/overview")]
    [InlineData("/settings/environment/runtimes", "/settings/runtimes")]
    [InlineData("/settings/environment/data", "/settings/data")]
    [InlineData("/settings/environment/wiki", "/settings/wiki")]
    [InlineData("/settings/environment/board", "/settings/board")]
    [InlineData("/settings/environment/mapping", "/settings/board")]
    [InlineData("/settings/environment/reviewer", "/settings/reviewer")]
    [InlineData("/settings/environment/git-hosts", "/settings/git-hosts")]
    [InlineData("/settings/environment/project", "/settings/project")]
    [InlineData("/settings/environment/spending", "/settings/spending")]
    [InlineData("/settings/environment/concurrency", "/settings/concurrency")]
    [InlineData("/settings/environment/search", "/settings/search")]
    [InlineData("/settings/environment/diagnostics", "/settings/diagnostics")]
    [InlineData("/settings/environment/backup", "/settings/backup")]
    [InlineData("/settings/environment/evidence", "/settings/evidence")]
    [InlineData("/settings/environment/source", "/settings/overview")]
    [InlineData("/settings/environment/board?signin=ok", "/settings/board?signin=ok")]
    public void An_old_environment_address_redirects_to_its_new_place(string oldAddress, string newAddress)
    {
        using var ctx = PageContext(out _, out _);
        AddFrameworkServices(ctx);
        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(oldAddress);
        var segments = new Uri(nav.Uri).AbsolutePath.Trim('/').Split('/');

        ctx.Render<SettingsPage>(p => p
            .Add(c => c.Section, segments.ElementAtOrDefault(1))
            .Add(c => c.Sub, segments.ElementAtOrDefault(2)));

        Assert.Equal(newAddress, "/" + nav.ToBaseRelativePath(nav.Uri));
    }

    [Theory]
    [InlineData("/settings/framework", null, null)]
    [InlineData("/settings/framework/skills", "skills", null)]
    [InlineData("/settings/framework/skills/new", "skills", "new")]
    public void A_framework_address_is_still_the_framework_sections_own(string address, string? kind, string? action)
    {
        using var ctx = PageContext(out _, out _);
        AddFrameworkServices(ctx);
        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(address);

        var cut = ctx.Render<SettingsPage>(p => p
            .Add(c => c.Section, "framework")
            .Add(c => c.Sub, kind)
            .Add(c => c.Action, action));

        Assert.Equal(address, "/" + nav.ToBaseRelativePath(nav.Uri));
        Assert.NotEmpty(cut.FindAll("[data-testid=framework-detail]"));
        Assert.Contains("active", cut.Find("[data-testid=settings-nav-framework]").GetAttribute("class"));
    }

    [Fact]
    public void SettingsSections_maps_every_old_environment_slug()
    {
        foreach (var old in new[] { "overview", "runtimes", "data", "wiki", "board", "reviewer", "git-hosts", "project",
                     "spending", "concurrency", "search", "diagnostics", "backup", "evidence" })
        {
            Assert.Equal($"/settings/{old}", SettingsSections.LegacyEnvironmentTarget(old));
        }

        Assert.Equal("/settings/board", SettingsSections.LegacyEnvironmentTarget("mapping"));
        Assert.Equal("/settings/overview", SettingsSections.LegacyEnvironmentTarget(null));
    }

    // -- Kept from the tabbed page: the sections' own behaviour at their new addresses --

    [Fact]
    public void Settings_page_bar_is_titled_Settings_and_carries_no_tab_nav()
    {
        using var ctx = PageContext(out _, out _);

        var cut = ctx.Render<SettingsPage>();

        Assert.Equal("Settings", cut.Find(".studio-page-bar-title").TextContent);
        Assert.Empty(cut.FindAll(".studio-page-bar .settings-nav"));
        Assert.Empty(cut.FindAll(".notification-center-button"));
    }

    [Fact]
    public void The_spending_section_renders_its_three_limits()
    {
        using var ctx = PageContext(out _, out _);

        var cut = ctx.Render<SettingsPage>(p => p.Add(c => c.Section, "spending"));

        Assert.NotEmpty(cut.FindAll("[data-testid=spend-limit-session]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=spend-limit-day]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=spend-limit-month]"));
    }

    [Fact]
    public void The_diagnostics_section_renders_its_switch_and_export()
    {
        using var ctx = PageContext(out _, out _);

        var cut = ctx.Render<SettingsPage>(p => p.Add(c => c.Section, "diagnostics"));

        Assert.NotEmpty(cut.FindAll("[data-testid=telemetry-enabled]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=diagnostics-export]"));
    }

    [Fact]
    public void The_wiki_section_hosts_the_real_wiki_step_with_persisted_values()
    {
        using var ctx = PageContext(out var config, out var connections);
        // A persisted wiki connection: the reused WikiStep (via the scoped OnboardingDraft) must show its base URL.
        config.Set("Onboarding:Wiki:Provider", OnboardingProviders.Confluence);
        connections.Upsert(new ProviderConnection("wiki", ProviderKind.Wiki,
            "https://persisted.atlassian.net", "wiki:token", "Verified", DateTimeOffset.UtcNow));

        var cut = ctx.Render<SettingsPage>(p => p.Add(c => c.Section, "wiki"));

        // The genuine step component is hosted (not a fork): its provider select and base-url field render,
        Assert.NotEmpty(cut.FindAll("[data-testid=wiki-provider]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=wiki-baseurl]"));
        // carrying the persisted value from the store (seeded through the scoped OnboardingDraft).
        Assert.Contains("https://persisted.atlassian.net", cut.Markup);
    }

    [Fact]
    public void The_overview_hosts_the_connection_and_signin_panels_but_not_the_broker()
    {
        using var ctx = PageContext(out _, out _);

        var cut = ctx.Render<SettingsPage>(p => p.Add(c => c.Section, "overview"));

        Assert.NotEmpty(cut.FindAll("[data-testid=settings-overview]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=settings-connections]"));      // connections + runtime status (S17)
        Assert.NotEmpty(cut.FindAll("[data-testid=settings-runtimes]"));
        // The re-run-onboarding entry point still uses the Settings re-entry flag.
        Assert.Equal("/onboarding?mode=settings",
            cut.Find("[data-testid=settings-onboarding]").GetAttribute("href"));
        // Work item #357: the broker card lives under Connections now.
        Assert.Empty(cut.FindAll("[data-testid=broker-settings]"));
    }

    [Fact]
    public void The_overview_shows_the_running_version()
    {
        using var ctx = PageContext(out _, out _);
        ctx.Services.AddSingleton<Fravashi.Application.Updates.IProductInfo>(new FixedProductInfo());

        var cut = ctx.Render<SettingsPage>();

        Assert.Contains("0.9.94", cut.Find("[data-testid=settings-version]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void The_overview_names_the_product_by_its_full_name_and_its_former_one()
    {
        using var ctx = PageContext(out _, out _);

        var cut = ctx.Render<SettingsPage>();

        Assert.Equal($"Nova Globen Fravashi (formerly {Fravashi.Domain.FormerNames.Product})", cut.Find("[data-testid=settings-product]").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid=settings-data-move]"));
    }

    [Fact]
    public void The_overview_says_why_the_data_is_still_in_the_old_folder_while_the_launcher_says_so()
    {
        // The launcher's data-folder move leaves its words in <data dir>/data-move.txt (DataMoveFile on its side,
        // DataMoveNotice here) while the data stays in %LOCALAPPDATA%\Fravashi, and removes the file once it moved.
        var dataDir = Path.Combine(Path.GetTempPath(), "fravashi-overview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        try
        {
            File.WriteAllText(Path.Combine(dataDir, Infrastructure.Persistence.DataMoveNotice.FileName),
                @"Data still at %LOCALAPPDATA%\Fravashi: the studio container could not be stopped first (busy).");
            using var ctx = PageContext(out _, out _);
            ctx.Services.AddSingleton(new Infrastructure.Persistence.DataDirectory(dataDir));

            var cut = ctx.Render<SettingsPage>();

            Assert.Equal(@"Data still at %LOCALAPPDATA%\Fravashi: the studio container could not be stopped first (busy).",
                cut.Find("[data-testid=settings-data-move]").TextContent.Trim());
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    private sealed class FixedProductInfo : Fravashi.Application.Updates.IProductInfo
    {
        public string Version => "0.9.94";

        public string Variant => "full";
    }

    [Fact]
    public void The_framework_section_lists_every_artifact_kind()
    {
        using var ctx = PageContext(out _, out _);
        AddFrameworkServices(ctx);

        var cut = ctx.Render<SettingsPage>(p => p.Add(c => c.Section, "framework"));

        foreach (var kind in new[] { "skills", "agents", "commands", "hooks", "mcp-servers", "plugins" })
        {
            Assert.Equal($"/settings/framework/{kind}", cut.Find($"[data-testid=framework-nav-{kind}]").GetAttribute("href"));
        }

        Assert.NotEmpty(cut.FindAll("[data-testid=framework-empty]"));
    }

    [Fact]
    public void The_framework_skills_kind_renders_the_skill_list()
    {
        using var ctx = PageContext(out _, out _);
        AddFrameworkServices(ctx);

        var cut = ctx.Render<SettingsPage>(p => p
            .Add(c => c.Section, "framework")
            .Add(c => c.Sub, "skills"));

        Assert.NotEmpty(cut.FindAll("[data-testid=skill-list]"));
        Assert.Equal("/settings/framework/skills/new", cut.Find("[data-testid=skill-new]").GetAttribute("href"));
    }

    // -- Backlog 294: the page bar's "Settings" is the view's one level-one heading --

    [Theory]
    [InlineData("overview", null, "Overview")]
    [InlineData("models", null, "Default models and effort")]
    [InlineData("framework", null, "Framework")]
    [InlineData("framework", "skills", "Skills")]
    [InlineData("framework", "agents", "Agents")]
    [InlineData("broker", null, "Credential broker")]
    public void A_settings_view_has_one_level_one_heading_and_its_section_at_level_two(
        string section, string? kind, string sectionHeading)
    {
        using var ctx = PageContext(out _, out _);
        AddFrameworkServices(ctx);

        var cut = ctx.Render<SettingsPage>(p => p
            .Add(c => c.Section, section)
            .Add(c => c.Sub, kind));

        var levelOne = Assert.Single(cut.FindAll("h1, [role=heading][aria-level=\"1\"]"));
        Assert.Equal("Settings", levelOne.TextContent.Trim());
        Assert.Contains(cut.FindAll("h2"), h => h.TextContent.Trim() == sectionHeading);
    }

    // -- Backlog 80: the Framework section admits that nothing consumes what it stores --

    /// <summary>
    /// Registers the services every Framework kind needs, so a test can render any of them.
    /// </summary>
    private static void AddFrameworkServices(BunitContext ctx)
    {
        ctx.Services.AddSingleton<Fravashi.Application.Framework.IFrameworkArtifactStore>(
            new FakeFrameworkArtifactStore());
        ctx.Services.AddSingleton<Fravashi.Application.Framework.IModelListResolver>(new FakeModelListResolver());
        ctx.Services.AddSingleton(new Fravashi.Web.Services.FluentIconCatalog());
    }

    [Theory]
    // The landing state, every artifact kind's list, and a form the user is actively filling in.
    [InlineData(null, null)]
    [InlineData("skills", null)]
    [InlineData("agents", null)]
    [InlineData("commands", null)]
    [InlineData("hooks", null)]
    [InlineData("mcp-servers", null)]
    [InlineData("plugins", null)]
    [InlineData("skills", "new")]
    [InlineData("hooks", "new")]
    public void Every_framework_route_says_artifacts_are_stored_but_not_applied_to_sessions(
        string? kind, string? action)
    {
        using var ctx = PageContext(out _, out _);
        AddFrameworkServices(ctx);

        var cut = ctx.Render<SettingsPage>(p => p
            .Add(c => c.Section, "framework")
            .Add(c => c.Sub, kind)
            .Add(c => c.Action, action));

        var notice = cut.Find("[data-testid=framework-not-applied]");
        // The wording must carry the actual consequence, not a vague "coming soon".
        Assert.Contains("not applied yet", notice.TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nothing writes them into an agent session", notice.TextContent,
            StringComparison.OrdinalIgnoreCase);
        // It reuses the studio's existing message convention rather than a bespoke look.
        Assert.Contains("ob-warning", notice.GetAttribute("class"));
        // It cannot be dismissed: the honesty gap must not be one click away.
        Assert.Empty(cut.FindAll("[data-testid=framework-not-applied] button"));
    }

    [Fact]
    public void Only_the_framework_section_carries_the_framework_notice()
    {
        using var ctx = PageContext(out _, out _);
        AddFrameworkServices(ctx);

        var cut = ctx.Render<SettingsPage>(p => p.Add(c => c.Section, "overview"));

        Assert.Empty(cut.FindAll("[data-testid=framework-not-applied]"));
    }
}
