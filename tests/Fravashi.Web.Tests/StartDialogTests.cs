using Bunit;
using Fravashi.Application.Notifications;
using Fravashi.Application.Persistence;
using Fravashi.Infrastructure.Bridge;
using Fravashi.Infrastructure.Notifications;
using Fravashi.Web.Components.Shell;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Work item #350: the start dialog, which took over the folder half of the old home surface (recents, open folder,
/// clone; plan 2b-ii) with its backlog 14 recents machinery, and the title bar's workspace menu (close, switch).
/// Where it is shown and whether it can be closed is <see cref="StartDialogHostTests"/>.
/// </summary>
public class StartDialogTests
{
    private sealed class FakeWorkspaceStore : IWorkspaceStore
    {
        public List<WorkspaceRecord> Records { get; } = [];

        public IReadOnlyList<WorkspaceRecord> Recent(int max = 10) => Records.Take(max).ToList();

        public WorkspaceRecord? Get(string id) => Records.FirstOrDefault(r => r.Id == id);

        public void Touch(string id, string name, string rootPath)
        {
            Records.RemoveAll(r => r.Id == id);
            Records.Insert(0, new WorkspaceRecord(id, name, rootPath, DateTimeOffset.UtcNow));
        }

        public void Remove(string id) => Records.RemoveAll(r => r.Id == id);
    }

    private static (BunitContext Ctx, FakeWorkspaceStore Store) WelcomeContext(
        bool bridgeConfigured, string? activeWorkspaceId = null)
    {
        var ctx = new BunitContext();
        var store = new FakeWorkspaceStore();
        var values = new Dictionary<string, string?>();
        if (bridgeConfigured)
        {
            values[BridgeWorkspaceContext.UrlKey] = "http://127.0.0.1:9";
            values[BridgeWorkspaceContext.TokenKey] = "t";
        }

        if (activeWorkspaceId is not null)
        {
            values[BridgeWorkspaceContext.ActiveIdKey] = activeWorkspaceId;
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var context = new BridgeWorkspaceContext(configuration);
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(configuration);
        ctx.Services.AddSingleton<IWorkspaceStore>(store);
        ctx.Services.AddSingleton(context);
        ctx.Services.AddSingleton(new BridgeClient(new HttpClient(), context));
        ctx.Services.AddLogging();
        // The merged page renders the dashboard cards below the welcome sections (backlog 33).
        ctx.Services.AddNoUpdates();
        ctx.Services.AddSingleton<INotificationHistory>(new InMemoryNotificationHistory());
        ctx.Services.AddSingleton<IWaitingForPersonList>(new StubWaitingForPersonList());
        ctx.Services.AddSessionSurface();
        // And the insight cards (backlog 36), which are part of rendering Home at all.
        ctx.Services.AddInsightCards();
        return (ctx, store);
    }

    [Fact]
    public void EmptyState_ShowsTheHints()
    {
        var (ctx, _) = WelcomeContext(bridgeConfigured: false);
        using var _ctx = ctx;

        var cut = ctx.Render<StartDialog>();

        Assert.Contains("Nothing yet", cut.Markup);
        Assert.Contains("The host bridge is not connected", cut.Markup);
    }

    [Fact]
    public void Recents_RenderNameAndPath_AndTheOpenButton()
    {
        var (ctx, store) = WelcomeContext(bridgeConfigured: true);
        using var _ctx = ctx;
        store.Touch("bbbb", "gadgets", @"C:\repos\gadgets");
        store.Touch("aaaa", "widgets", @"C:\repos\widgets");

        var cut = ctx.Render<StartDialog>();

        Assert.Contains("widgets", cut.Markup);
        Assert.Contains(@"C:\repos\gadgets", cut.Markup);
        Assert.NotNull(cut.Find("[data-testid='start-open-folder']"));
        var rows = cut.FindAll(".welcome-recent");
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void Recents_leave_out_the_autopilots_worktrees()
    {
        // Backlog 319: the delivery proof showed its worktree in the recents as "2". A worktree is registered so the
        // studio can govern it, never offered back as a folder to open.
        var (ctx, store) = WelcomeContext(bridgeConfigured: true);
        using var _ctx = ctx;
        store.Touch("repo", "delivery-proof", @"C:\repos\delivery-proof");
        store.Touch("wt-2", "2", @"C:\repos\delivery-proof.fravashi-worktrees\2");

        var cut = ctx.Render<StartDialog>();

        var rows = cut.FindAll(".welcome-recent");
        Assert.Single(rows);
        Assert.Contains("delivery-proof", rows[0].TextContent);
        Assert.DoesNotContain("fravashi-worktrees", cut.Markup);
    }

    // ---- the recents list: when each was opened, grouped, and type-ahead (the Home rework's, kept) ----

    [Fact]
    public void Each_recent_says_when_it_was_last_opened_like_the_model_window()
    {
        var (ctx, store) = WelcomeContext(bridgeConfigured: true);
        using var _ctx = ctx;
        store.Touch("aaaa", "widgets", @"C:\repos\widgets");

        var cut = ctx.Render<StartDialog>();

        var row = cut.Find("[data-testid='start-recent']");
        Assert.Contains(@"C:\repos\widgets", row.TextContent);
        Assert.NotNull(row.QuerySelector(".start-recent-when time"));
        Assert.Contains("This week", cut.Find("[data-testid='start-recents-group']").TextContent);
    }

    [Fact]
    public async Task Typing_in_the_recents_filter_narrows_the_list_by_name_or_by_path_as_you_type()
    {
        var (ctx, store) = WelcomeContext(bridgeConfigured: true);
        using var _ctx = ctx;
        store.Touch("cccc", "sprockets", @"D:\work\sprockets");
        store.Touch("bbbb", "gadgets", @"C:\repos\gadgets");
        store.Touch("aaaa", "widgets", @"C:\repos\widgets");
        var cut = ctx.Render<StartDialog>();
        Assert.Equal(3, cut.FindAll(".welcome-recent").Count);

        await cut.InvokeAsync(() => cut.Find("[data-testid='start-recents-filter']").Input("GADG"));
        Assert.Equal("gadgets", Assert.Single(cut.FindAll(".welcome-recent .welcome-recent-name")).TextContent);

        // The path counts too: somebody who remembers the drive and not the name finds it.
        await cut.InvokeAsync(() => cut.Find("[data-testid='start-recents-filter']").Input(@"d:\work"));
        Assert.Equal("sprockets", Assert.Single(cut.FindAll(".welcome-recent .welcome-recent-name")).TextContent);

        await cut.InvokeAsync(() => cut.Find("[data-testid='start-recents-filter']").Input("nothing-like-this"));
        Assert.Empty(cut.FindAll(".welcome-recent"));
        Assert.Contains("No folder matches", cut.Find("[data-testid='start-recents-nomatch']").TextContent);
    }

    [Fact]
    public void With_no_recents_there_is_no_filter_to_type_into()
    {
        var (ctx, _) = WelcomeContext(bridgeConfigured: true);
        using var _ctx = ctx;

        var cut = ctx.Render<StartDialog>();

        Assert.Empty(cut.FindAll("[data-testid='start-recents-filter']"));
    }

    private sealed class ScriptedBridgeClient : BridgeClient
    {
        private readonly Func<string, BridgeWorkspaceDto?>? _onClone;
        private readonly Func<string, BridgeRegistration>? _onRegister;

        public ScriptedBridgeClient(
            BridgeWorkspaceContext context,
            Func<string, BridgeWorkspaceDto?>? onClone = null,
            Func<string, BridgeRegistration>? onRegister = null)
            : base(new HttpClient(), context)
        {
            _onClone = onClone;
            _onRegister = onRegister;
        }

        /// <summary>Every path the page asked the host to grant, in order.</summary>
        public List<string> Registered { get; } = [];

        public override Task<BridgeWorkspaceDto?> CloneAsync(string url, CancellationToken ct)
        {
            var result = _onClone is null ? null : _onClone(url);
            return Task.FromResult(result);
        }

        public override Task<BridgeRegistration> RegisterWorkspaceAsync(string path, CancellationToken ct)
        {
            Registered.Add(path);
            var result = _onRegister is null ? BridgeRegistration.Missing : _onRegister(path);
            return Task.FromResult(result);
        }
    }

    private static (BunitContext Ctx, FakeWorkspaceStore Store, ScriptedBridgeClient Client) ScriptedContext(
        Func<string, BridgeWorkspaceDto?>? onClone = null,
        Func<string, BridgeRegistration>? onRegister = null,
        bool bridgeConfigured = true)
    {
        var ctx = new BunitContext();
        var store = new FakeWorkspaceStore();
        var values = new Dictionary<string, string?>();
        if (bridgeConfigured)
        {
            values[BridgeWorkspaceContext.UrlKey] = "http://127.0.0.1:9";
            values[BridgeWorkspaceContext.TokenKey] = "t";
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var context = new BridgeWorkspaceContext(configuration);
        var client = new ScriptedBridgeClient(context, onClone, onRegister);
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(configuration);
        ctx.Services.AddSingleton<IWorkspaceStore>(store);
        ctx.Services.AddSingleton(context);
        ctx.Services.AddSingleton<BridgeClient>(client);
        ctx.Services.AddLogging();
        // The merged page renders the dashboard cards below the welcome sections (backlog 33).
        ctx.Services.AddNoUpdates();
        ctx.Services.AddSingleton<INotificationHistory>(new InMemoryNotificationHistory());
        ctx.Services.AddSingleton<IWaitingForPersonList>(new StubWaitingForPersonList());
        ctx.Services.AddSessionSurface();
        // And the insight cards (backlog 36), which are part of rendering Home at all.
        ctx.Services.AddInsightCards();
        return (ctx, store, client);
    }

    private static (BunitContext Ctx, FakeWorkspaceStore Store) CloneContext(Func<string, BridgeWorkspaceDto?> onClone)
    {
        var (ctx, store, _) = ScriptedContext(onClone: onClone);
        return (ctx, store);
    }

    [Fact]
    public void CloneSection_IsGatedOnTheBridge()
    {
        var (unconfigured, _) = WelcomeContext(bridgeConfigured: false);
        using (unconfigured)
        {
            var cut = unconfigured.Render<StartDialog>();
            Assert.NotNull(cut.Find("[data-testid='start-no-bridge']"));
            Assert.Empty(cut.FindAll("[data-testid='welcome-clone-url']"));
        }

        var (configured, _) = WelcomeContext(bridgeConfigured: true);
        using (configured)
        {
            var cut = configured.Render<StartDialog>();
            Assert.NotNull(cut.Find("[data-testid='welcome-clone-url']"));
            Assert.NotNull(cut.Find("[data-testid='welcome-clone-start']"));
        }
    }

    [Fact]
    public async Task Clone_Failure_ShowsTheBridgesMessage()
    {
        var (ctx, _) = CloneContext(_ => throw new InvalidOperationException("'fravashi' already exists in the chosen folder."));
        using var _ctx = ctx;
        var cut = ctx.Render<StartDialog>();

        await cut.InvokeAsync(() =>
        {
            cut.Find("[data-testid='welcome-clone-url']").Change("https://forge.example.test/nova-globen/fravashi.git");
            cut.Find("[data-testid='welcome-clone-start']").Click();
        });

        cut.WaitForAssertion(() =>
            Assert.Contains("already exists", cut.Find("[data-testid='welcome-clone-error']").TextContent));
    }

    [Fact]
    public async Task Clone_Success_TouchesRecentsAndNavigatesToTheWorkspace()
    {
        var (ctx, store) = CloneContext(_ => new BridgeWorkspaceDto("abc123", "fravashi", @"C:\repos\fravashi"));
        using var _ctx = ctx;
        var cut = ctx.Render<StartDialog>();

        await cut.InvokeAsync(() =>
        {
            cut.Find("[data-testid='welcome-clone-url']").Change("https://forge.example.test/nova-globen/fravashi.git");
            cut.Find("[data-testid='welcome-clone-start']").Click();
        });

        cut.WaitForAssertion(() =>
        {
            Assert.Single(store.Records, r => r.Id == "abc123");
            var nav = ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
            Assert.Contains("/code?ws=abc123", nav.Uri);
        });
    }

    [Fact]
    public async Task Clone_CancelledPicker_IsAQuietNoOp()
    {
        var (ctx, store) = CloneContext(_ => null);
        using var _ctx = ctx;
        var cut = ctx.Render<StartDialog>();

        await cut.InvokeAsync(() =>
        {
            cut.Find("[data-testid='welcome-clone-url']").Change("https://forge.example.test/nova-globen/fravashi.git");
            cut.Find("[data-testid='welcome-clone-start']").Click();
        });

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid='welcome-clone-error']")));
        Assert.Empty(store.Records);
    }

    // ---- Backlog 14: a fresh fravashi.exe has an empty bridge registry while recents are full ----

    [Fact]
    public async Task OpenRecent_registers_with_the_host_before_navigating()
    {
        var (ctx, store, client) = ScriptedContext(
            onRegister: _ => BridgeRegistration.Registered(
                new BridgeWorkspaceDto("abc123", "widgets", @"C:\repos\widgets")));
        using var _ctx = ctx;
        store.Touch("abc123", "widgets", @"C:\repos\widgets");
        var cut = ctx.Render<StartDialog>();

        await cut.InvokeAsync(() => cut.Find(".welcome-recent").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Equal([@"C:\repos\widgets"], client.Registered);
            var nav = ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
            Assert.Contains("/code?ws=abc123", nav.Uri);
        });
    }

    [Fact]
    public async Task OpenRecent_missing_folder_offers_removal_behind_a_confirmation_and_does_not_navigate()
    {
        var (ctx, store, _) = ScriptedContext(onRegister: _ => BridgeRegistration.Missing);
        using var _ctx = ctx;
        store.Touch("abc123", "widgets", @"C:\repos\widgets");
        var cut = ctx.Render<StartDialog>();
        var nav = ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        var before = nav.Uri;

        await cut.InvokeAsync(() => cut.Find(".welcome-recent").Click());

        cut.WaitForAssertion(() =>
            Assert.Contains(@"C:\repos\widgets", cut.Find("[data-testid='welcome-recent-error']").TextContent));
        Assert.Equal(before, nav.Uri);
        // Even here the removal is a deliberate second choice, never the button under the message.
        Assert.Empty(cut.FindAll("[data-testid='welcome-recent-remove']"));

        await cut.InvokeAsync(() => cut.Find("[data-testid='welcome-recent-forget-ask']").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid='welcome-recent-remove']").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(store.Records);
            Assert.Empty(cut.FindAll(".welcome-recent"));
            Assert.Empty(cut.FindAll("[data-testid='welcome-recent-error']"));
        });
    }

    // ---- The folder is on a drive that is not mapped yet: it is NOT gone ----

    [Fact]
    public async Task OpenRecent_unreachable_folder_does_not_claim_the_folder_is_gone()
    {
        var (ctx, store, _) = ScriptedContext(onRegister: _ => BridgeRegistration.Unreachable);
        using var _ctx = ctx;
        store.Touch("abc123", "widgets", @"Z:\repos\widgets");
        var cut = ctx.Render<StartDialog>();

        await cut.InvokeAsync(() => cut.Find(".welcome-recent").Click());

        cut.WaitForAssertion(() =>
        {
            var message = cut.Find("[data-testid='welcome-recent-error']").TextContent;
            Assert.Contains(@"Z:\repos\widgets", message);
            Assert.Contains("could not be reached", message);
            // The folder and its work are very probably fine, so nothing may say otherwise.
            Assert.DoesNotContain("not found", message);
            Assert.DoesNotContain("any more", message);
        });

        // Retry is the offered action; removal is not sitting under the uncertain claim.
        Assert.NotNull(cut.Find("[data-testid='welcome-recent-retry']"));
        Assert.Empty(cut.FindAll("[data-testid='welcome-recent-remove']"));
        Assert.Single(store.Records);
    }

    [Fact]
    public async Task OpenRecent_retry_opens_the_folder_once_the_drive_is_back()
    {
        var attempts = 0;
        var (ctx, store, _) = ScriptedContext(onRegister: _ =>
        {
            attempts++;
            return attempts == 1
                ? BridgeRegistration.Unreachable
                : BridgeRegistration.Registered(new BridgeWorkspaceDto("abc123", "widgets", @"Z:\repos\widgets"));
        });
        using var _ctx = ctx;
        store.Touch("abc123", "widgets", @"Z:\repos\widgets");
        var cut = ctx.Render<StartDialog>();

        await cut.InvokeAsync(() => cut.Find(".welcome-recent").Click());
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='welcome-recent-retry']")));

        await cut.InvokeAsync(() => cut.Find("[data-testid='welcome-recent-retry']").Click());

        cut.WaitForAssertion(() =>
        {
            var nav = ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
            Assert.Contains("/code?ws=abc123", nav.Uri);
        });
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Dropping_an_unreachable_recent_takes_two_deliberate_clicks()
    {
        var (ctx, store, _) = ScriptedContext(onRegister: _ => BridgeRegistration.Unreachable);
        using var _ctx = ctx;
        store.Touch("abc123", "widgets", @"Z:\repos\widgets");
        var cut = ctx.Render<StartDialog>();

        await cut.InvokeAsync(() => cut.Find(".welcome-recent").Click());
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='welcome-recent-forget-ask']")));

        // Asking is not doing, and the confirmation says what is (and is not) being deleted.
        await cut.InvokeAsync(() => cut.Find("[data-testid='welcome-recent-forget-ask']").Click());
        cut.WaitForAssertion(() => Assert.Contains(
            "Nothing on disk", cut.Find("[data-testid='welcome-recent-forget-hint']").TextContent));
        Assert.Single(store.Records);

        // Backing out keeps the row.
        await cut.InvokeAsync(() => cut.Find("[data-testid='welcome-recent-forget-cancel']").Click());
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid='welcome-recent-remove']")));
        Assert.Single(store.Records);

        // Confirming is still available for the user who knows the folder really is gone.
        await cut.InvokeAsync(() => cut.Find("[data-testid='welcome-recent-forget-ask']").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid='welcome-recent-remove']").Click());
        cut.WaitForAssertion(() => Assert.Empty(store.Records));
    }

    [Fact]
    public async Task OpenRecent_when_the_bridge_cannot_be_reached_reports_and_keeps_the_row()
    {
        var (ctx, store, _) = ScriptedContext(
            onRegister: _ => throw new HttpRequestException("no route to host"));
        using var _ctx = ctx;
        store.Touch("abc123", "widgets", @"C:\repos\widgets");
        var cut = ctx.Render<StartDialog>();
        var nav = ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        var before = nav.Uri;

        await cut.InvokeAsync(() => cut.Find(".welcome-recent").Click());

        cut.WaitForAssertion(() =>
            Assert.Contains("could not be reached", cut.Find("[data-testid='welcome-recent-error']").TextContent));
        // A transport failure says nothing about the folder, so it must not offer to delete the row
        // at all, not even behind the confirmation. Retry is the whole answer.
        Assert.Empty(cut.FindAll("[data-testid='welcome-recent-remove']"));
        Assert.Empty(cut.FindAll("[data-testid='welcome-recent-forget-ask']"));
        Assert.NotNull(cut.Find("[data-testid='welcome-recent-retry']"));
        Assert.Single(store.Records);
        Assert.Equal(before, nav.Uri);
    }

    [Fact]
    public async Task OpenRecent_without_a_bridge_navigates_as_before()
    {
        // Host `dotnet run`: the client has no base address, so calling it would throw.
        var (ctx, store, client) = ScriptedContext(bridgeConfigured: false);
        using var _ctx = ctx;
        store.Touch("abc123", "widgets", @"C:\repos\widgets");
        var cut = ctx.Render<StartDialog>();

        await cut.InvokeAsync(() => cut.Find(".welcome-recent").Click());

        cut.WaitForAssertion(() =>
        {
            var nav = ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
            Assert.Contains("/code?ws=abc123", nav.Uri);
        });
        Assert.Empty(client.Registered);
    }

    // ---- Work item #350: with a folder open the dialog says which, and offers to close it ----

    [Fact]
    public void Without_a_folder_there_is_no_close_of_any_kind_and_no_continue_without_one()
    {
        var (ctx, store) = WelcomeContext(bridgeConfigured: true);
        using var _ctx = ctx;
        store.Touch("abc123", "widgets", @"C:\repos\widgets");

        var cut = ctx.Render<StartDialog>(p => p.Add(c => c.Dismissible, false));

        Assert.Empty(cut.FindAll("[data-testid='start-close-workspace']"));
        Assert.Empty(cut.FindAll("[data-testid='start-dialog-close']"));
        Assert.Empty(cut.FindAll("[data-testid='start-recent-current']"));
        Assert.DoesNotContain(
            "without", cut.Find("[data-testid='start-dialog']").TextContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void With_a_folder_open_the_dialog_marks_it_and_offers_to_close_it()
    {
        var (ctx, store) = WelcomeContext(bridgeConfigured: true, activeWorkspaceId: "abc123");
        using var _ctx = ctx;
        store.Touch("other", "gadgets", @"C:\repos\gadgets");
        store.Touch("abc123", "widgets", @"C:\repos\widgets");

        var cut = ctx.Render<StartDialog>(p => p.Add(c => c.Dismissible, true));

        Assert.Contains("widgets", cut.Find("[data-testid='start-dialog-current']").TextContent);
        var current = cut.Find("[data-testid='start-recent-current']").Closest("button")!;
        Assert.Equal("abc123", current.GetAttribute("data-workspace-id"));
        Assert.NotNull(cut.Find("[data-testid='start-close-workspace']"));
        Assert.NotNull(cut.Find("[data-testid='start-dialog-close']"));
        // Opening a different one is still reachable from the folder-open state.
        Assert.Equal(2, cut.FindAll("[data-testid='start-recent']").Count);
    }

    [Fact]
    public void A_folder_the_store_no_longer_knows_is_still_named_by_its_id()
    {
        // Dev fallback: the settings row names an id the store has never seen.
        var (ctx, _) = WelcomeContext(bridgeConfigured: true, activeWorkspaceId: "abc123");
        using var _ctx = ctx;

        var cut = ctx.Render<StartDialog>(p => p.Add(c => c.Dismissible, true));

        Assert.Contains("abc123", cut.Find("[data-testid='start-dialog-current']").TextContent);
    }

    [Fact]
    public async Task Close_workspace_asks_the_host_and_does_not_navigate_itself_when_the_host_answers()
    {
        var (ctx, _) = WelcomeContext(bridgeConfigured: true, activeWorkspaceId: "abc123");
        using var _ctx = ctx;
        ctx.JSInterop.Setup<bool>("fravashiWindow.closeWorkspace").SetResult(true);
        var nav = ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var cut = ctx.Render<StartDialog>(p => p.Add(c => c.Dismissible, true));

        await cut.InvokeAsync(() => cut.Find("[data-testid='start-close-workspace']").Click());

        cut.WaitForAssertion(() =>
            Assert.Single(ctx.JSInterop.Invocations, i => i.Identifier == "fravashiWindow.closeWorkspace"));
        // The host owns the navigation (it clears the binding FIRST, or it would re-impose the ws).
        Assert.Empty(nav.History);
    }

    [Fact]
    public async Task Without_a_host_close_workspace_loads_home_with_no_workspace()
    {
        var (ctx, _) = WelcomeContext(bridgeConfigured: true, activeWorkspaceId: "abc123");
        using var _ctx = ctx;
        ctx.JSInterop.Setup<bool>("fravashiWindow.closeWorkspace").SetResult(false);
        var nav = ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var cut = ctx.Render<StartDialog>(p => p.Add(c => c.Dismissible, true));

        await cut.InvokeAsync(() => cut.Find("[data-testid='start-close-workspace']").Click());

        cut.WaitForAssertion(() =>
        {
            var entry = Assert.Single(nav.History);
            Assert.Equal("/", entry.Uri);
            Assert.True(entry.Options.ForceLoad);
        });
    }

    [Fact]
    public async Task Opening_a_recent_asks_the_host_to_show_the_id_it_answered_with_and_closes_the_dialog()
    {
        var (ctx, store, bridge) = ScriptedContext(onRegister: path => BridgeRegistration.Registered(
            new BridgeWorkspaceDto("ws-canonical", "gadgets", path)));
        using var _ctx = ctx;
        ctx.JSInterop.Setup<bool>("fravashiWindow.switchWorkspace", _ => true).SetResult(true);
        store.Touch("ws-old", "gadgets", @"C:\repos\gadgets");
        var closed = 0;
        var cut = ctx.Render<StartDialog>(p => p
            .Add(c => c.Dismissible, true)
            .Add(c => c.OnClose, () => closed++));

        await cut.InvokeAsync(() => cut.Find("[data-testid='start-recent']").Click());

        cut.WaitForAssertion(() =>
        {
            var ask = Assert.Single(ctx.JSInterop.Invocations, i => i.Identifier == "fravashiWindow.switchWorkspace");
            // The id the HOST answered with, not the stored row's, so the window binds to what it serves.
            Assert.Equal("ws-canonical", Assert.Single(ask.Arguments));
            Assert.Equal(1, closed);
        });
        Assert.Equal([@"C:\repos\gadgets"], bridge.Registered);
        Assert.Empty(ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().History);
        Assert.Null(store.Get("ws-old"));
    }

    [Fact]
    public async Task Escape_closes_a_dismissible_dialog_and_nothing_closes_one_that_is_not()
    {
        var (ctx, _) = WelcomeContext(bridgeConfigured: true);
        using var _ctx = ctx;
        var closed = 0;
        var dismissible = ctx.Render<StartDialog>(p => p
            .Add(c => c.Dismissible, true)
            .Add(c => c.OnClose, () => closed++));
        await dismissible.InvokeAsync(() => dismissible.Find("[data-testid='start-dialog']").KeyDown(key: "Escape"));
        Assert.Equal(1, closed);

        var locked = ctx.Render<StartDialog>(p => p
            .Add(c => c.Dismissible, false)
            .Add(c => c.OnClose, () => closed++));
        await locked.InvokeAsync(() => locked.Find("[data-testid='start-dialog']").KeyDown(key: "Escape"));
        Assert.Equal(1, closed);
    }
}
