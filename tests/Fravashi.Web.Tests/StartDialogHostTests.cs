using Bunit;
using Fravashi.Application.Git;
using Fravashi.Application.Notifications;
using Fravashi.Application.Persistence;
using Fravashi.Infrastructure.Bridge;
using Fravashi.Infrastructure.Notifications;
using Fravashi.Infrastructure.Onboarding.Auth;
using Fravashi.Web.Components.Code;
using Fravashi.Web.Components.Git;
using Fravashi.Web.Components.Layout;
using Fravashi.Web.Components.Pages;
using Fravashi.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Work item #350: "no folder open means no studio". Every page is routed the way Routes.razor routes it, MainLayout
/// as the default, in a window whose bridge is configured but which has no folder, while the recents still hold the
/// folder an earlier window showed. The layout must show the start dialog and nothing else: not the activity rail,
/// not the page, and not anything the page would have read from the previous folder. The pages' own services are
/// deliberately NOT registered, so a page that was so much as instantiated would fail its test loudly.
/// </summary>
public sealed class StartDialogHostTests
{
    private const string PreviousFolder = "PreviousRepoThatMustNotLeak";

    /// <summary>Home and one page of each area: board, wiki, code, git, sessions, settings and provider sync.</summary>
    public static TheoryData<Type> EveryArea => new()
    {
        typeof(Home),
        typeof(WorkItemsPage),
        typeof(WikiLibraryPage),
        typeof(CodePage),
        typeof(GitPage),
        typeof(SessionsPage),
        typeof(SettingsPage),
        typeof(ProviderSyncPage),
    };

    private sealed class PreviousFolderStore : IWorkspaceStore
    {
        private readonly List<WorkspaceRecord> _records =
        [
            new("prev", PreviousFolder, @"C:\repos\previous", DateTimeOffset.UtcNow),
        ];

        public IReadOnlyList<WorkspaceRecord> Recent(int max = 10) => [.. _records.Take(max)];

        public WorkspaceRecord? Get(string id) => _records.FirstOrDefault(r => r.Id == id);

        public void Touch(string id, string name, string rootPath)
        {
        }

        public void Remove(string id)
        {
        }
    }

    private static BunitContext LayoutContext(string? activeWorkspaceId)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        GateGuardTests.Services(GateGuardTests.GateState.AllOpen, ctx.Services);
        ctx.Services.AddFluentUIComponents();
        ctx.Services.AddSingleton(new IdpSessionGrace());
        ctx.Services.AddSingleton<ThemeState>();
        ctx.Services.AddSingleton(new AgentPanelState());
        ctx.Services.AddSingleton(new ScratchpadState());
        ctx.Services.AddSingleton(new StartDialogState());
        ctx.Services.AddSingleton<IGitService>(new UnavailableGitService());
        ctx.Services.AddSingleton<INotificationHistory>(new InMemoryNotificationHistory());
        ctx.Services.AddSingleton<IWaitingForPersonList>(new StubWaitingForPersonList());
        ctx.Services.AddLogging();

        var values = new Dictionary<string, string?>
        {
            [BridgeWorkspaceContext.UrlKey] = "http://127.0.0.1:9",
            [BridgeWorkspaceContext.TokenKey] = "t",
        };
        if (activeWorkspaceId is not null)
        {
            values[BridgeWorkspaceContext.ActiveIdKey] = activeWorkspaceId;
        }

        var workspace = new BridgeWorkspaceContext(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        ctx.Services.AddSingleton(workspace);
        ctx.Services.AddSingleton(new BridgeClient(new HttpClient(), workspace));
        ctx.Services.AddSingleton<IWorkspaceStore>(new PreviousFolderStore());
        return ctx;
    }

    [Theory]
    [MemberData(nameof(EveryArea))]
    public void With_no_folder_open_every_area_shows_the_start_dialog_and_nothing_of_the_studio(Type page)
    {
        using var ctx = LayoutContext(activeWorkspaceId: null);

        var cut = ctx.Render<RouteView>(parameters => parameters
            .Add(p => p.RouteData, new RouteData(page, new Dictionary<string, object?>()))
            .Add(p => p.DefaultLayout, typeof(MainLayout)));

        Assert.NotNull(cut.Find("[data-testid=start-screen]"));
        var dialog = cut.Find("[data-testid=start-dialog]");
        Assert.Equal("false", dialog.GetAttribute("data-dismissible"));
        Assert.Empty(cut.FindAll("[data-testid=start-dialog-close]"));
        Assert.Empty(cut.FindAll("[data-testid=start-close-workspace]"));
        // No way past it: nothing continues without a folder.
        Assert.DoesNotContain("without", dialog.TextContent, StringComparison.OrdinalIgnoreCase);

        // Nothing of the studio: no rail, no page body, no scratchpad.
        Assert.Empty(cut.FindAll(".studio-shell"));
        Assert.Empty(cut.FindAll(".studio-activitybar"));
        Assert.Empty(cut.FindAll(".studio-main"));
        // The previous folder is only a row to open in the recents, never something the page shows.
        Assert.Contains(PreviousFolder, cut.Find("[data-testid=start-recents-list]").TextContent);
        Assert.Single(cut.FindAll("[data-testid=start-recent]"));
        var outsideTheRecents = cut.Find("[data-testid=start-screen]").TextContent
            .Replace(cut.Find("[data-testid=start-recents-list]").TextContent, string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(PreviousFolder, outsideTheRecents, StringComparison.Ordinal);
        // The title bar is still there, so the window can be moved, minimised and closed.
        Assert.NotEmpty(cut.FindAll("[data-testid=window-titlebar]"));
    }

    [Fact]
    public void With_a_folder_open_the_studio_renders_and_the_dialog_is_not_shown()
    {
        using var ctx = LayoutContext(activeWorkspaceId: "prev");

        var cut = ctx.Render<MainLayout>(parameters =>
            parameters.Add(p => p.Body, "<p data-testid=\"layout-body-marker\">body</p>"));

        Assert.NotNull(cut.Find("[data-testid=layout-body-marker]"));
        Assert.NotEmpty(cut.FindAll(".studio-activitybar"));
        Assert.Empty(cut.FindAll("[data-testid=start-screen]"));
        Assert.Empty(cut.FindAll("[data-testid=start-dialog]"));
    }

    [Fact]
    public void Without_a_bridge_there_is_no_folder_to_open_so_the_studio_is_not_locked()
    {
        // A host `dotnet run` against the local clone: the clone IS the folder, and nothing could be picked.
        using var ctx = LayoutContext(activeWorkspaceId: null);
        ctx.Services.AddSingleton(new BridgeWorkspaceContext(new ConfigurationBuilder().Build()));

        var cut = ctx.Render<MainLayout>(parameters =>
            parameters.Add(p => p.Body, "<p data-testid=\"layout-body-marker\">body</p>"));

        Assert.NotNull(cut.Find("[data-testid=layout-body-marker]"));
        Assert.Empty(cut.FindAll("[data-testid=start-screen]"));
    }

    [Fact]
    public void The_pages_that_must_stay_reachable_are_never_replaced_by_the_dialog()
    {
        // MinimalWindowLayout (the error page, the lock and sign-in pages): reachable whatever the window shows.
        using var ctx = LayoutContext(activeWorkspaceId: null);

        var cut = ctx.Render<MinimalWindowLayout>(parameters =>
            parameters.Add(p => p.Body, "<p data-testid=\"layout-body-marker\">body</p>"));

        Assert.NotNull(cut.Find("[data-testid=layout-body-marker]"));
        Assert.Empty(cut.FindAll("[data-testid=start-screen]"));
    }
}
