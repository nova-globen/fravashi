using Bunit;
using Fravashi.Application.Git;
using Fravashi.Application.Notifications;
using Fravashi.Application.Persistence;
using Fravashi.Infrastructure.Bridge;
using Fravashi.Infrastructure.Notifications;
using Fravashi.Web.Components.Shell;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

public class WindowTitleBarTests
{
    private static BunitContext BarContext(
        IGitService? git = null, string? activeWorkspaceId = null, IWorkspaceStore? store = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(git ?? new UnavailableGitService());
        ctx.Services.AddSingleton<INotificationHistory>(new InMemoryNotificationHistory());
        // Backlog 23: the bar carries the agent-panel toggle. Nothing here attaches a panel surface,
        // so it draws nothing and the caption-control assertions below stay about caption controls.
        ctx.Services.AddSingleton(new Fravashi.Web.Services.AgentPanelState());
        ctx.Services.AddSingleton(new Fravashi.Web.Services.ScratchpadState());
        // Work item #350: the workspace button opens the start dialog through this.
        ctx.Services.AddSingleton(new Fravashi.Web.Services.StartDialogState());
        ctx.Services.AddLogging();
        // The bar names the active workspace in its title; null means no workspace bound.
        var values = new Dictionary<string, string?>();
        if (activeWorkspaceId is not null)
        {
            values[BridgeWorkspaceContext.ActiveIdKey] = activeWorkspaceId;
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        ctx.Services.AddSingleton(new BridgeWorkspaceContext(configuration));
        ctx.Services.AddSingleton<IWorkspaceStore>(store ?? new NamedWorkspaceStore());
        return ctx;
    }

    /// <summary>A recents list the workspace menu can read, with the same known workspace as above.</summary>
    private sealed class RecentsStore : IWorkspaceStore
    {
        public List<WorkspaceRecord> Records { get; } =
        [
            new("ws-known", "MyRepo", @"C:\src\MyRepo", DateTimeOffset.UtcNow),
            new("ws-other", "Gadgets", @"C:\src\Gadgets", DateTimeOffset.UtcNow),
        ];

        public IReadOnlyList<WorkspaceRecord> Recent(int max = 10) => Records.Take(max).ToList();

        public WorkspaceRecord? Get(string id) => Records.FirstOrDefault(r => r.Id == id);

        public void Touch(string id, string name, string rootPath)
        {
            Records.RemoveAll(r => r.Id == id);
            Records.Insert(0, new WorkspaceRecord(id, name, rootPath, DateTimeOffset.UtcNow));
        }

        public void Remove(string id) => Records.RemoveAll(r => r.Id == id);
    }

    /// <summary>A host bridge that answers registration from a script and records what it was asked.</summary>
    private sealed class ScriptedBridge(BridgeWorkspaceContext context, Func<string, BridgeRegistration> onRegister)
        : BridgeClient(new HttpClient(), context)
    {
        public List<string> Registered { get; } = [];

        public override Task<BridgeRegistration> RegisterWorkspaceAsync(string path, CancellationToken ct)
        {
            Registered.Add(path);
            return Task.FromResult(onRegister(path));
        }
    }

    /// <summary>A bar bound to ws-known, with the bridge configured and scripted.</summary>
    private static (BunitContext Ctx, RecentsStore Store, ScriptedBridge Bridge) MenuContext(
        Func<string, BridgeRegistration> onRegister, string? activeWorkspaceId = "ws-known", IGitService? git = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(git ?? new UnavailableGitService());
        var notifications = new InMemoryNotificationHistory();
        ctx.Services.AddSingleton<INotificationHistory>(notifications);
        ctx.Services.AddSingleton(new Fravashi.Web.Services.AgentPanelState());
        ctx.Services.AddSingleton(new Fravashi.Web.Services.ScratchpadState());
        // Work item #350: the workspace button opens the start dialog through this.
        ctx.Services.AddSingleton(new Fravashi.Web.Services.StartDialogState());
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

        var context = new BridgeWorkspaceContext(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        ctx.Services.AddSingleton(context);
        var store = new RecentsStore();
        ctx.Services.AddSingleton<IWorkspaceStore>(store);
        var bridge = new ScriptedBridge(context, onRegister);
        ctx.Services.AddSingleton<BridgeClient>(bridge);
        return (ctx, store, bridge);
    }

    /// <summary>
    /// Work item #350: the title bar's workspace button and, beside it, the dialog it opens: the bar plus the host the
    /// layout wraps the studio in, around a page that says when it has been rendered again.
    /// </summary>
    private static readonly Microsoft.AspNetCore.Components.RenderFragment BarAndPage = builder =>
        {
            builder.OpenComponent<WindowTitleBar>(0);
            builder.CloseComponent();
            builder.OpenComponent<StartDialogHost>(1);
            builder.AddAttribute(2, nameof(StartDialogHost.ChildContent), (Microsoft.AspNetCore.Components.RenderFragment)(page =>
            {
                page.OpenComponent<PageMarker>(0);
                page.CloseComponent();
            }));
            builder.CloseComponent();
        };

    /// <summary>A page that counts its own initialisations, so "the page is unchanged" is a fact and not a hope.</summary>
    private sealed class PageMarker : Microsoft.AspNetCore.Components.ComponentBase
    {
        public static int Created;

        private readonly int _instance = Interlocked.Increment(ref Created);

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "p");
            builder.AddAttribute(1, "data-testid", "page-marker");
            builder.AddContent(2, $"page {_instance}");
            builder.CloseElement();
        }
    }

    [Fact]
    public void The_workspace_button_names_the_folder_or_says_none_is_open()
    {
        var (bound, _, _) = MenuContext(_ => BridgeRegistration.Missing);
        using (bound)
        {
            var cut = bound.Render<WindowTitleBar>();
            Assert.Contains("MyRepo", cut.Find("[data-testid=titlebar-workspace-trigger]").TextContent);
            // A button that opens a dialog, not a dropdown with a panel of its own.
            Assert.Equal("dialog", cut.Find("[data-testid=titlebar-workspace-trigger]").GetAttribute("aria-haspopup"));
            Assert.Empty(cut.FindAll("[data-testid=titlebar-workspace-panel]"));
        }

        var (unbound, _, _) = MenuContext(_ => BridgeRegistration.Missing, activeWorkspaceId: null);
        using (unbound)
        {
            var cut = unbound.Render<WindowTitleBar>();
            Assert.Contains("No folder open", cut.Find("[data-testid=titlebar-workspace-trigger]").TextContent);
        }
    }

    [Fact]
    public async Task The_workspace_button_opens_the_start_dialog_over_the_page_and_closing_it_leaves_the_page_as_it_was()
    {
        var (ctx, _, _) = MenuContext(_ => BridgeRegistration.Missing);
        using var _ctx = ctx;
        var cut = ctx.Render(BarAndPage);
        var page = cut.Find("[data-testid=page-marker]").TextContent;
        Assert.Empty(cut.FindAll("[data-testid=start-dialog]"));

        await cut.InvokeAsync(() => cut.Find("[data-testid=titlebar-workspace-trigger]").Click());

        cut.WaitForAssertion(() =>
        {
            var dialog = cut.Find("[data-testid=start-dialog]");
            Assert.Equal("true", dialog.GetAttribute("data-dismissible"));
            Assert.Equal("true", cut.Find("[data-testid=titlebar-workspace-trigger]").GetAttribute("aria-expanded"));
        });
        // Drawn OVER the page: the page is still there, the very same instance.
        Assert.Equal(page, cut.Find("[data-testid=page-marker]").TextContent);

        await cut.InvokeAsync(() => cut.Find("[data-testid=start-dialog-close]").Click());
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=start-dialog]")));
        Assert.Equal(page, cut.Find("[data-testid=page-marker]").TextContent);

        // Escape closes it too.
        await cut.InvokeAsync(() => cut.Find("[data-testid=titlebar-workspace-trigger]").Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=start-dialog]")));
        await cut.InvokeAsync(() => cut.Find("[data-testid=start-dialog]").KeyDown(key: "Escape"));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=start-dialog]")));
        Assert.Equal(page, cut.Find("[data-testid=page-marker]").TextContent);
    }

    [Fact]
    public async Task With_no_folder_open_the_page_is_never_rendered_and_the_dialog_cannot_be_closed()
    {
        var (ctx, _, _) = MenuContext(_ => BridgeRegistration.Missing, activeWorkspaceId: null);
        using var _ctx = ctx;
        var before = PageMarker.Created;
        var cut = ctx.Render(BarAndPage);

        Assert.NotNull(cut.Find("[data-testid=start-screen]"));
        Assert.Equal("false", cut.Find("[data-testid=start-dialog]").GetAttribute("data-dismissible"));
        Assert.Empty(cut.FindAll("[data-testid=start-dialog-close]"));
        Assert.Empty(cut.FindAll("[data-testid=page-marker]"));

        // The button has nothing more to open, and Escape closes nothing.
        await cut.InvokeAsync(() => cut.Find("[data-testid=titlebar-workspace-trigger]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=start-dialog]").KeyDown(key: "Escape"));
        Assert.Single(cut.FindAll("[data-testid=start-dialog]"));
        Assert.Empty(cut.FindAll("[data-testid=page-marker]"));
        Assert.Equal(before, PageMarker.Created);
    }

    [Fact]
    public async Task The_branch_selector_beside_it_still_switches_branches()
    {
        var git = new SwitchableGitService();
        var (ctx, _, _) = MenuContext(_ => BridgeRegistration.Missing, git: git);
        using var _ctx = ctx;
        var cut = ctx.Render(BarAndPage);

        await cut.InvokeAsync(() => cut.Find("[data-testid=titlebar-workspace-trigger]").Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=start-dialog]")));
        await cut.InvokeAsync(() => cut.Find("[data-testid=start-dialog-close]").Click());

        cut.WaitForAssertion(() =>
            Assert.Contains("main", cut.Find("[data-testid=titlebar-branch-trigger]").TextContent));
        await cut.InvokeAsync(() => cut.Find("[data-testid=titlebar-branch-trigger]").Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=titlebar-branch-panel]")));
        await cut.InvokeAsync(() => cut.Find("[data-testid=titlebar-branch-opt-local-feature-x]").Click());

        cut.WaitForAssertion(() => Assert.Equal(["feature/x"], git.Checkouts));
        Assert.Empty(cut.FindAll("[data-testid=start-dialog]"));
    }

    [Fact]
    public void The_repository_switcher_sits_immediately_left_of_the_branch_dropdown()
    {
        var (ctx, _, _) = MenuContext(_ => BridgeRegistration.Missing, git: new SwitchableGitService());
        using var _ctx = ctx;
        var cut = ctx.Render<WindowTitleBar>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=titlebar-branch]")));
        var slot = cut.Find("[data-testid=titlebar-workspace-slot]");
        // Beside the branch chip in the nav group, not in the menu strip at the far left.
        Assert.Equal("window-titlebar-nav", slot.ParentElement!.GetAttribute("data-testid"));
        Assert.Empty(cut.Find("[data-testid=window-titlebar-menus]").Children);
        var branch = cut.Find("[data-testid=titlebar-branch]").ParentElement!;
        Assert.Same(slot.NextElementSibling, branch);
    }

    /// <summary>
    /// Backlog 67: the owner placed the scratchpad's button "next to the minimize button in the app title bar",
    /// so its position is part of the acceptance rather than a styling detail.
    /// </summary>
    [Fact]
    public void The_scratchpad_toggle_sits_immediately_left_of_minimise()
    {
        var (ctx, _, _) = MenuContext(_ => BridgeRegistration.Missing, git: new SwitchableGitService());
        using var _ctx = ctx;
        var cut = ctx.Render<WindowTitleBar>();

        var toggle = cut.Find("[data-testid=titlebar-scratchpad-toggle]");
        Assert.Equal("Minimize", toggle.NextElementSibling!.GetAttribute("title"));
    }

    /// <summary>One known workspace, so the title tests can tell name-found from name-degraded.</summary>
    private sealed class NamedWorkspaceStore : IWorkspaceStore
    {
        public IReadOnlyList<WorkspaceRecord> Recent(int max = 10) => [];

        public WorkspaceRecord? Get(string id) =>
            id == "ws-known" ? new WorkspaceRecord(id, "MyRepo", @"C:\src\MyRepo", DateTimeOffset.UtcNow) : null;

        public void Touch(string id, string name, string rootPath) { }

        public void Remove(string id) { }
    }

    [Fact]
    public void Renders_the_three_caption_controls_wired_to_the_window_bridge()
    {
        using var ctx = BarContext();

        var cut = ctx.Render<WindowTitleBar>();

        var buttons = cut.FindAll(".studio-caption-btn");
        Assert.Equal(3, buttons.Count);
        Assert.Equal("minimize", buttons[0].GetAttribute("data-window-action"));
        Assert.Equal("maximize", buttons[1].GetAttribute("data-window-action"));
        Assert.Equal("close", buttons[2].GetAttribute("data-window-action"));
        Assert.Contains("studio-caption-close", buttons[2].GetAttribute("class"));
    }

    [Fact]
    public void Has_back_forward_undo_and_redo_buttons()
    {
        using var ctx = BarContext();

        var cut = ctx.Render<WindowTitleBar>();

        var nav = cut.FindAll("[data-testid=window-titlebar-nav] .studio-titlebar-navbtn");
        Assert.Equal(4, nav.Count);
        Assert.Equal("back", nav[0].GetAttribute("data-window-action"));
        Assert.Equal("forward", nav[1].GetAttribute("data-window-action"));
        Assert.Equal("undo", nav[2].GetAttribute("data-window-action"));
        Assert.Equal("redo", nav[3].GetAttribute("data-window-action"));
    }

    [Fact]
    public void Without_a_configured_repository_there_is_no_branch_chip()
    {
        using var ctx = BarContext();

        var cut = ctx.Render<WindowTitleBar>();

        Assert.Empty(cut.FindAll("[data-testid=titlebar-branch]"));
    }

    [Fact]
    public void The_branch_chip_opens_a_dropdown_that_switches_branches()
    {
        var git = new SwitchableGitService();
        using var ctx = BarContext(git);
        var cut = ctx.Render<WindowTitleBar>();

        // The list loads on open, so the closed trigger has to name the branch before there is a list.
        cut.WaitForAssertion(() =>
            Assert.Contains("main", cut.Find("[data-testid=titlebar-branch-trigger]").TextContent));

        cut.InvokeAsync(() => cut.Find("[data-testid=titlebar-branch-trigger]").Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=titlebar-branch-panel]")));

        cut.InvokeAsync(() => cut.Find("[data-testid=titlebar-branch-opt-local-feature-x]").Click());

        cut.WaitForAssertion(() => Assert.Equal(["feature/x"], git.Checkouts));
        cut.WaitForAssertion(() =>
            Assert.Contains("feature/x", cut.Find("[data-testid=titlebar-branch-trigger]").TextContent));
    }

    [Fact]
    public void Remote_only_branches_are_listed_and_say_which_remote_they_are_on()
    {
        var git = new SwitchableGitService();
        using var ctx = BarContext(git);
        var cut = ctx.Render<WindowTitleBar>();

        cut.InvokeAsync(() => cut.Find("[data-testid=titlebar-branch-trigger]").Click());

        cut.WaitForAssertion(() =>
            Assert.NotEmpty(cut.FindAll("[data-testid=titlebar-branch-opt-remote-origin-epic-only-there]")));

        // The indicator says what it is in words, not only in a glyph.
        Assert.Equal(
            "on origin, not local yet",
            cut.Find("[data-testid=titlebar-branch-subtitle-remote-origin-epic-only-there]").TextContent.Trim());
        Assert.NotEqual(
            cut.Find("[data-testid=titlebar-branch-icon-local-main]").TextContent.Trim(),
            cut.Find("[data-testid=titlebar-branch-icon-remote-origin-epic-only-there]").TextContent.Trim());

        // origin/main is the SAME branch as the local main, and listing it twice would ask the user to
        // choose between two rows that do the same thing.
        Assert.Empty(cut.FindAll("[data-testid=titlebar-branch-opt-remote-origin-main]"));
    }

    [Fact]
    public void Choosing_a_remote_only_branch_creates_the_local_tracking_branch()
    {
        var git = new SwitchableGitService();
        using var ctx = BarContext(git);
        var cut = ctx.Render<WindowTitleBar>();

        cut.InvokeAsync(() => cut.Find("[data-testid=titlebar-branch-trigger]").Click());
        cut.WaitForAssertion(() =>
            Assert.NotEmpty(cut.FindAll("[data-testid=titlebar-branch-opt-remote-origin-epic-only-there]")));
        cut.InvokeAsync(() => cut.Find("[data-testid=titlebar-branch-opt-remote-origin-epic-only-there]").Click());

        cut.WaitForAssertion(() =>
        {
            var chosen = Assert.Single(git.TrackingCheckouts);
            Assert.Equal("origin", chosen.Remote);
            Assert.Equal("epic/only-there", chosen.Name);
        });

        // A plain checkout would have been the wrong call and would leave no tracking branch behind.
        Assert.Empty(git.Checkouts);
        cut.WaitForAssertion(() =>
            Assert.Contains("epic/only-there", cut.Find("[data-testid=titlebar-branch-trigger]").TextContent));
    }

    [Fact]
    public void Typing_filters_the_branch_list()
    {
        var git = new SwitchableGitService();
        using var ctx = BarContext(git);
        var cut = ctx.Render<WindowTitleBar>();

        cut.InvokeAsync(() => cut.Find("[data-testid=titlebar-branch-trigger]").Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=titlebar-branch-filter]")));

        cut.InvokeAsync(() => cut.Find("[data-testid=titlebar-branch-filter]").Input("epic"));

        cut.WaitForAssertion(() =>
        {
            Assert.NotEmpty(cut.FindAll("[data-testid=titlebar-branch-opt-remote-origin-epic-only-there]"));
            Assert.Empty(cut.FindAll("[data-testid=titlebar-branch-opt-local-main]"));
        });

        // A remote branch is findable by its remote's name, which is not in its label.
        cut.InvokeAsync(() => cut.Find("[data-testid=titlebar-branch-filter]").Input("origin/epic"));
        cut.WaitForAssertion(() =>
            Assert.NotEmpty(cut.FindAll("[data-testid=titlebar-branch-opt-remote-origin-epic-only-there]")));
    }

    [Fact]
    public void Each_branch_state_wears_the_git_pages_own_class_rather_than_a_copy_of_its_colour()
    {
        var git = new SwitchableGitService();
        using var ctx = BarContext(git);
        var cut = ctx.Render<WindowTitleBar>();

        cut.InvokeAsync(() => cut.Find("[data-testid=titlebar-branch-trigger]").Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=titlebar-branch-opt-local-main]")));

        // main is published and level, feature/x has never been pushed, release/1 is ahead and behind.
        Assert.Contains("git-b-synced", cut.Find("[data-testid=titlebar-branch-opt-local-main]").GetAttribute("class")!);
        Assert.Contains(
            "git-b-unpublished",
            cut.Find("[data-testid=titlebar-branch-opt-local-feature-x]").GetAttribute("class")!);
        Assert.Contains(
            "git-b-diverged",
            cut.Find("[data-testid=titlebar-branch-opt-local-release-1]").GetAttribute("class")!);

        // And the words are the Git page's words too.
        Assert.Equal(
            "not published",
            cut.Find("[data-testid=titlebar-branch-subtitle-local-feature-x]").TextContent.Trim());
        Assert.Equal(
            "↑2 ↓1",
            cut.Find("[data-testid=titlebar-branch-subtitle-local-release-1]").TextContent.Trim());
    }

    [Fact]
    public void Search_box_opens_the_command_palette_and_the_menu_strip_awaits_menus()
    {
        using var ctx = BarContext();

        var cut = ctx.Render<WindowTitleBar>();

        var search = cut.Find("[data-testid=window-titlebar-search]");
        Assert.False(search.HasAttribute("disabled"));
        Assert.Equal("palette", search.GetAttribute("data-window-action"));
        Assert.NotEmpty(cut.FindAll("[data-testid=window-titlebar-menus]"));
    }

    [Fact]
    public void With_no_workspace_the_title_is_plain_Fravashi()
    {
        using var ctx = BarContext();

        var cut = ctx.Render<WindowTitleBar>();

        Assert.Equal("Fravashi", cut.Find("[data-testid=window-titlebar-title]").TextContent.Trim());
    }

    [Fact]
    public void With_an_active_workspace_the_title_names_it_and_reaches_the_native_caption()
    {
        using var ctx = BarContext(activeWorkspaceId: "ws-known");

        var cut = ctx.Render<WindowTitleBar>();

        // The web-drawn bar reads as the window title without losing the palette affordance...
        var title = cut.Find("[data-testid=window-titlebar-title]");
        Assert.Equal("MyRepo - Fravashi", title.TextContent.Trim());
        Assert.Equal(
            "palette",
            cut.Find("[data-testid=window-titlebar-search]").GetAttribute("data-window-action"));

        // ...while the NATIVE caption pushed over the window bridge is chrome-worded, so the taskbar and
        // Alt+Tab read as the command the owner types (backlog 32), which since the rename is the product
        // name too; the two are kept separate so either can change alone.
        cut.WaitForAssertion(() =>
        {
            var push = Assert.Single(
                ctx.JSInterop.Invocations, i => i.Identifier == "fravashiWindow.setTitle");
            Assert.Equal("MyRepo - Fravashi", Assert.Single(push.Arguments));
        });
    }

    [Fact]
    public void A_workspace_the_store_no_longer_knows_still_shows_its_id_not_the_bare_product_name()
    {
        using var ctx = BarContext(activeWorkspaceId: "ws-unknown");

        var cut = ctx.Render<WindowTitleBar>();

        Assert.Equal(
            "ws-unknown - Fravashi",
            cut.Find("[data-testid=window-titlebar-title]").TextContent.Trim());
    }

    [Fact]
    public void Has_no_brand_icon_because_the_activity_bar_already_shows_it()
    {
        using var ctx = BarContext();

        var cut = ctx.Render<WindowTitleBar>();

        Assert.Empty(cut.FindAll(".studio-titlebar-brand"));
    }

    [Fact]
    public void Maximize_button_carries_both_the_maximize_and_restore_glyphs()
    {
        using var ctx = BarContext();

        var cut = ctx.Render<WindowTitleBar>();

        // CSS swaps them on html.window-maximized; both must exist in the markup.
        Assert.NotEmpty(cut.FindAll(".studio-caption-max"));
        Assert.NotEmpty(cut.FindAll(".studio-caption-restore"));
    }

    /// <summary>
    /// A minimal git for the branch dropdown: one branch of each state the list has to keep
    /// distinguishable, plus a remote carrying one branch that is already local and one that is not.
    /// </summary>
    private sealed class SwitchableGitService : IGitService
    {
        public List<string> Checkouts { get; } = [];

        public List<Fravashi.Domain.Git.GitRemoteBranch> TrackingCheckouts { get; } = [];

        private string _current = "main";

        public bool IsAvailable => true;

        public Task<string> GetCurrentBranchAsync(CancellationToken cancellationToken = default) => Task.FromResult(_current);

        public Task<IReadOnlyList<Fravashi.Domain.Git.GitBranch>> GetBranchesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Fravashi.Domain.Git.GitBranch>>([
                new Fravashi.Domain.Git.GitBranch("main", _current == "main", "origin/main"),
                new Fravashi.Domain.Git.GitBranch("feature/x", _current == "feature/x"),
                new Fravashi.Domain.Git.GitBranch("release/1", _current == "release/1", "origin/release/1", 2, 1),
            ]);

        public Task CheckoutAsync(string branch, CancellationToken cancellationToken = default)
        {
            Checkouts.Add(branch);
            _current = branch;
            return Task.CompletedTask;
        }

        public Task CreateBranchAsync(string name, string baseBranch, bool checkout, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Fravashi.Domain.Repository.RepositoryNode>> ListTreeAsync(string reference, string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Fravashi.Domain.Repository.RepositoryFile?> ReadFileAsync(string reference, string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Fravashi.Domain.Git.GitCommit>> GetCommitsAsync(string reference, int max = 50, string? path = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Fravashi.Domain.Git.GitCommitDetail?> GetCommitAsync(string sha, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> GetDiffAsync(string sha, string? path = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Fravashi.Domain.Git.GitCommit>> GetFileHistoryAsync(string path, string reference, int max = 50, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string?> GetRemoteUrlAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Fravashi.Domain.Git.GitRemoteBranch>> GetRemoteBranchesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Fravashi.Domain.Git.GitRemoteBranch>>([
                new Fravashi.Domain.Git.GitRemoteBranch("origin/main", "origin", "main"),
                new Fravashi.Domain.Git.GitRemoteBranch("origin/epic/only-there", "origin", "epic/only-there"),
            ]);

        public Task CheckoutTrackingAsync(Fravashi.Domain.Git.GitRemoteBranch branch, CancellationToken cancellationToken = default)
        {
            TrackingCheckouts.Add(branch);
            _current = branch.Name;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Fravashi.Domain.Git.GitGraphCommit>> GetGraphAsync(int max = 150, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Fravashi.Domain.Git.GitLineBlame?> GetLineBlameAsync(string path, int line, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Fravashi.Domain.Git.GitFileChange>> GetDiffFilesAsync(string baseRef, string headRef, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string?> GetMergeBaseAsync(string first, string second, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
