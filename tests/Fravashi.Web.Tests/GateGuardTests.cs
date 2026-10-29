using System.Reflection;
using Bunit;
using Bunit.TestDoubles;
using Fravashi.Application.Git;
using Fravashi.Domain.Git;
using Fravashi.Domain.Repository;
using Fravashi.Application.Integrity;
using Fravashi.Application.Notifications;
using Fravashi.Application.Persistence;
using Fravashi.Application.Updates;
using Fravashi.Application.Variants;
using Fravashi.Application.Vpn;
using Fravashi.Infrastructure.Bridge;
using Fravashi.Infrastructure.Integrity;
using Fravashi.Infrastructure.Notifications;
using Fravashi.Infrastructure.Onboarding.Auth;
using Fravashi.Web.Components;
using Fravashi.Web.Components.Auth;
using Fravashi.Web.Components.Integrity;
using Fravashi.Web.Components.Layout;
using Fravashi.Web.Components.Vpn;
using Fravashi.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 282 (plan @100): the full-page gates were HTTP middleware only, so inside a live circuit an in-app navigation
/// went past them (measured on v0.9.74: a rail click on /integrity-failed rendered /wiki with no document load). Three
/// things are held here. The lock pages render without the activity rail and sidebar. The circuit-side guard turns an
/// in-app navigation a closed gate would refuse into a full page load, and leaves it alone otherwise. And the guard and
/// the middleware ask ONE decision (<see cref="StudioGates"/>), built from the gates' own static policy classes, so the
/// two cannot drift apart.
/// </summary>
public sealed class GateGuardTests
{
    // ---- the lock pages ---------------------------------------------------------------------------------------------

    public static TheoryData<Type, string> LockPages => new()
    {
        { typeof(SignIn), "signin-gate" },
        { typeof(IntegrityFailed), "integrity-failed" },
        { typeof(SecureConnection), "vpn-gate" },
    };

    [Theory]
    [MemberData(nameof(LockPages))]
    public void A_lock_page_renders_inside_the_minimal_layout_with_no_activity_rail_or_sidebar(Type page, string testId)
    {
        Assert.Equal(typeof(MinimalWindowLayout), page.GetCustomAttribute<LayoutAttribute>()?.LayoutType);

        using var ctx = Context(GateState.AllOpen with { SignInRequired = true });
        // Routed exactly as Routes.razor routes it, MainLayout as the default, so a page that lost its own @layout
        // would come back with the rail (and fail here).
        var cut = ctx.Render<RouteView>(parameters => parameters
            .Add(p => p.RouteData, new RouteData(page, new Dictionary<string, object?>()))
            .Add(p => p.DefaultLayout, typeof(MainLayout)));

        Assert.NotEmpty(cut.FindAll($"[data-testid={testId}]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=window-titlebar]"));
        Assert.Empty(cut.FindAll(".studio-activitybar"));
        Assert.Empty(cut.FindAll(".studio-sidebar"));
    }

    /// <summary>
    /// The branch switcher is a git checkout on the host folder that navigates nowhere, so neither the middleware nor
    /// the guard would ever see it: a page that stays reachable while a gate is closed does not offer it.
    /// </summary>
    [Fact]
    public void The_minimal_layout_offers_no_branch_switcher_and_does_not_ask_git_for_the_branch()
    {
        using var ctx = Context(GateState.AllOpen);
        var git = new OnBranchMainGit();
        ctx.Services.AddSingleton<IGitService>(git);

        var cut = ctx.Render<MinimalWindowLayout>(parameters =>
            parameters.Add(p => p.Body, "<p data-testid=\"layout-body-marker\">body</p>"));

        Assert.NotEmpty(cut.FindAll("[data-testid=window-titlebar]"));
        Assert.Empty(cut.FindAll("[data-testid=titlebar-branch]"));
        Assert.Equal(0, git.BranchAsked);
    }

    [Fact]
    public void The_title_bar_elsewhere_still_offers_the_branch_switcher()
    {
        using var ctx = Context(GateState.AllOpen);
        var git = new OnBranchMainGit();
        ctx.Services.AddSingleton<IGitService>(git);

        var cut = ctx.Render<Fravashi.Web.Components.Shell.WindowTitleBar>();

        Assert.NotEmpty(cut.FindAll("[data-testid=titlebar-branch]"));
        Assert.Equal(1, git.BranchAsked);
    }

    /// <summary>A repository on branch main that answers only that question; anything else throws.</summary>
    private sealed class OnBranchMainGit : IGitService
    {
        public int BranchAsked { get; private set; }

        public bool IsAvailable => true;

        private static NotSupportedException Unused() => new("only the current branch is asked in this test");

        public Task<string> GetCurrentBranchAsync(CancellationToken cancellationToken = default)
        {
            BranchAsked++;
            return Task.FromResult("main");
        }

        public Task<IReadOnlyList<GitBranch>> GetBranchesAsync(CancellationToken cancellationToken = default) => throw Unused();
        public Task CheckoutAsync(string branch, CancellationToken cancellationToken = default) => throw Unused();
        public Task CreateBranchAsync(string name, string baseBranch, bool checkout, CancellationToken cancellationToken = default) => throw Unused();
        public Task<IReadOnlyList<RepositoryNode>> ListTreeAsync(string reference, string path, CancellationToken cancellationToken = default) => throw Unused();
        public Task<RepositoryFile?> ReadFileAsync(string reference, string path, CancellationToken cancellationToken = default) => throw Unused();
        public Task<IReadOnlyList<GitCommit>> GetCommitsAsync(string reference, int max = 50, string? path = null, CancellationToken cancellationToken = default) => throw Unused();
        public Task<GitCommitDetail?> GetCommitAsync(string sha, CancellationToken cancellationToken = default) => throw Unused();
        public Task<string> GetDiffAsync(string sha, string? path = null, CancellationToken cancellationToken = default) => throw Unused();
        public Task<IReadOnlyList<GitCommit>> GetFileHistoryAsync(string path, string reference, int max = 50, CancellationToken cancellationToken = default) => throw Unused();
        public Task<string?> GetRemoteUrlAsync(CancellationToken cancellationToken = default) => throw Unused();
        public Task<IReadOnlyList<GitRemoteBranch>> GetRemoteBranchesAsync(CancellationToken cancellationToken = default) => throw Unused();
        public Task CheckoutTrackingAsync(GitRemoteBranch branch, CancellationToken cancellationToken = default) => throw Unused();
        public Task<IReadOnlyList<GitGraphCommit>> GetGraphAsync(int max = 150, CancellationToken cancellationToken = default) => throw Unused();
        public Task<GitLineBlame?> GetLineBlameAsync(string path, int line, CancellationToken cancellationToken = default) => throw Unused();
        public Task<IReadOnlyList<GitFileChange>> GetDiffFilesAsync(string baseRef, string headRef, CancellationToken cancellationToken = default) => throw Unused();
        public Task<string?> GetMergeBaseAsync(string first, string second, CancellationToken cancellationToken = default) => throw Unused();
    }

    // ---- the guard --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/wiki", "/wiki")]
    [InlineData("/sign%2Din", "/sign-in")]
    [InlineData("/x.y%2Fz", "/x.y%2Fz")]
    [InlineData("/x.y%2fz", "/x.y%2fz")]
    [InlineData("/a%252Fb", "/a%2Fb")]
    [InlineData("/git/history/src%2FProgram.cs", "/git/history/src%2FProgram.cs")]
    public void The_guard_asks_about_the_path_as_the_middleware_will_see_it(string encoded, string requestPath)
    {
        Assert.Equal(requestPath, GateGuard.AsRequestPath(encoded));
    }

    [Fact]
    public void With_no_gate_closed_an_in_app_navigation_goes_through_untouched()
    {
        using var ctx = Context(GateState.AllOpen);
        var nav = ctx.Services.GetRequiredService<BunitNavigationManager>();
        ctx.Render<GateGuard>();

        nav.NavigateTo("/wiki");

        var entry = Assert.Single(nav.History);
        Assert.Equal("/wiki", entry.Uri);
        Assert.Equal(NavigationState.Succeeded, entry.State);
        Assert.False(entry.Options.ForceLoad);
    }

    public static TheoryData<string, GateState, string> ClosedGates => new()
    {
        { "integrity", GateState.AllOpen with { IntegrityFailed = true }, "/wiki" },
        { "critical-update", GateState.AllOpen with { UpdateCritical = true }, "/board" },
        { "vpn", GateState.AllOpen with { VpnDown = true }, "/code" },
        // No credential is stored, so the session is required and not usable, and the refresh finds no session before
        // any HTTP call: the gate closes without a network.
        { "sign-in", GateState.AllOpen with { SignInRequired = true }, "/wiki" },
        { "onboarding", GateState.AllOpen with { OnboardingIncomplete = true }, "/settings" },
    };

    [Theory]
    [MemberData(nameof(ClosedGates))]
    public void With_a_gate_closed_an_in_app_navigation_becomes_a_full_page_load(string gate, GateState state, string path)
    {
        using var ctx = Context(state);
        var nav = ctx.Services.GetRequiredService<BunitNavigationManager>();
        ctx.Render<GateGuard>();

        nav.NavigateTo(path);

        AssertPreventedThenForced(nav, path, gate);
    }

    /// <summary>
    /// The in-app navigation prevented, and one forced load of the same target that went through. The two entries'
    /// order in bunit's history depends on when the nested forced navigation completes, so it is not asserted.
    /// </summary>
    private static void AssertPreventedThenForced(BunitNavigationManager nav, string path, string because)
    {
        var history = nav.History.ToList();
        Assert.Equal(2, history.Count);
        Assert.All(history, entry => Assert.Equal(path, entry.Uri));
        var prevented = Assert.Single(history, entry => entry.State == NavigationState.Prevented);
        Assert.False(prevented.Options.ForceLoad, because);
        var forced = Assert.Single(history, entry => entry.Options.ForceLoad);
        Assert.Equal(NavigationState.Succeeded, forced.State);
    }

    /// <summary>
    /// The server's NavigationManager (RemoteNavigationManager) runs every location-changing handler for a programmatic
    /// NavigateTo, a forced load included, where bunit's does not. Under that behaviour the guard's own forced load must
    /// pass through it once rather than be prevented and forced again for ever (caught in review: with a gate that
    /// answers without awaiting, that loop is a stack overflow of the studio).
    /// </summary>
    [Theory]
    [MemberData(nameof(ClosedGates))]
    public void Under_the_servers_navigation_manager_the_forced_load_passes_the_guard_exactly_once(string gate, GateState state, string path)
    {
        using var ctx = Context(state);
        var nav = new ServerLikeNavigationManager();
        ctx.Services.AddSingleton<NavigationManager>(nav);
        ctx.Render<GateGuard>();

        nav.ClickLink(path);

        Assert.Equal(2, nav.HandlerRuns);
        var sent = Assert.Single(nav.Sent);
        Assert.Equal(path, sent.Uri);
        Assert.True(sent.ForceLoad, gate);
    }

    [Fact]
    public void With_a_gate_closed_a_navigation_to_a_path_it_exempts_stays_in_app()
    {
        using var ctx = Context(GateState.AllOpen with { IntegrityFailed = true });
        var nav = ctx.Services.GetRequiredService<BunitNavigationManager>();
        ctx.Render<GateGuard>();

        nav.NavigateTo(IntegrityLock.FailedPath);

        var entry = Assert.Single(nav.History);
        Assert.Equal(NavigationState.Succeeded, entry.State);
        Assert.False(entry.Options.ForceLoad);
    }

    /// <summary>
    /// #372: the sign-in page sends the window to the identity provider. That is another site, not an in-app route: the
    /// guard used to ask the sign-in gate about it, which tried to renew the expired session on every Sign in click and
    /// recorded the refusal again. The way back in is a full page load the middleware answers.
    /// </summary>
    [Fact]
    public void A_navigation_that_leaves_the_studio_is_not_the_guards_to_decide()
    {
        using var ctx = Context(GateState.AllOpen with { SignInRequired = true, IntegrityFailed = true });
        var nav = ctx.Services.GetRequiredService<BunitNavigationManager>();
        ctx.Render<GateGuard>();

        nav.NavigateTo("https://idp.example.test/connect/authorize?client_id=fravashi-studio", forceLoad: true);

        var entry = Assert.Single(nav.History);
        Assert.Equal(NavigationState.Succeeded, entry.State);
    }

    [Fact]
    public void A_gate_that_cannot_be_asked_forces_the_full_load_rather_than_letting_the_navigation_through()
    {
        using var ctx = Context(GateState.AllOpen);
        ctx.Services.AddSingleton<IIntegrityStatusProvider>(new ThrowingIntegrityStatus());
        var nav = ctx.Services.GetRequiredService<BunitNavigationManager>();
        ctx.Render<GateGuard>();

        nav.NavigateTo("/wiki");

        AssertPreventedThenForced(nav, "/wiki", "a gate that throws");
    }

    [Fact]
    public void The_guard_stops_asking_once_it_is_gone()
    {
        using var ctx = Context(GateState.AllOpen with { IntegrityFailed = true });
        var nav = ctx.Services.GetRequiredService<BunitNavigationManager>();
        var cut = ctx.Render<GateGuard>();
        cut.Instance.Dispose();

        nav.NavigateTo("/wiki");

        Assert.Equal(NavigationState.Succeeded, Assert.Single(nav.History).State);
    }

    /// <summary>
    /// Backlog 283: a page whose route ends in a file name is a page. With a gate closed, an in-app navigation to one
    /// becomes a full load like any other page, where the dot used to exempt it from every gate.
    /// </summary>
    [Theory]
    [InlineData("/code/README.md")]
    [InlineData("/code/src/a.b/c.cs")]
    [InlineData("/git/history/x.json")]
    [InlineData("/git/history/src/Program.cs")]
    public void With_a_gate_closed_an_in_app_navigation_to_a_dotted_page_becomes_a_full_page_load(string path)
    {
        using var ctx = Context(GateState.AllOpen with { SignInRequired = true });
        var nav = ctx.Services.GetRequiredService<BunitNavigationManager>();
        ctx.Render<GateGuard>();

        nav.NavigateTo(path);

        AssertPreventedThenForced(nav, path, path);
    }

    [Theory]
    [InlineData("/code/README.md")]
    [InlineData("/code/src/a.b/c.cs")]
    [InlineData("/git/history/x.json")]
    [InlineData("/workitems/1.2")]
    [InlineData("/nothing.css")]
    public async Task Every_closed_gate_redirects_a_dotted_page_path(string path)
    {
        foreach (var state in EachGateClosedAlone)
        {
            using var provider = Services(state).BuildServiceProvider();
            Assert.NotNull(await StudioGates.RedirectAsync(path, provider, CancellationToken.None));
        }
    }

    [Theory]
    [InlineData("/app.css")]
    [InlineData("/codeEditor.js")]
    [InlineData("/vendor/monaco/vs/loader.js")]
    [InlineData("/favicon.ico")]
    [InlineData("/_framework/blazor.web.js")]
    public async Task A_static_asset_the_app_serves_passes_every_gate_closed_at_once(string path)
    {
        using var provider = Services(new GateState(true, true, true, true, true)).BuildServiceProvider();

        Assert.Null(await StudioGates.RedirectAsync(path, provider, CancellationToken.None));
    }

    private static readonly GateState[] EachGateClosedAlone =
    [
        GateState.AllOpen with { IntegrityFailed = true },
        GateState.AllOpen with { UpdateCritical = true },
        GateState.AllOpen with { VpnDown = true },
        GateState.AllOpen with { SignInRequired = true },
        GateState.AllOpen with { OnboardingIncomplete = true },
    ];

    // ---- one decision, two askers -----------------------------------------------------------------------------------

    [Fact]
    public void The_gates_are_the_five_static_policies_in_the_middlewares_order()
    {
        var expected = new (string Name, string Target, Type Policy)[]
        {
            ("integrity", IntegrityLock.FailedPath, typeof(IntegrityLock)),
            ("critical-update", CriticalUpdateLock.UpdatePath, typeof(CriticalUpdateLock)),
            ("vpn", VpnGate.GatePath, typeof(VpnGate)),
            ("sign-in", SignInGate.GatePath, typeof(SignInGate)),
            ("onboarding", OnboardingGate.GatePath, typeof(OnboardingGate)),
        };

        Assert.Equal(expected.Length, StudioGates.InOrder.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            var gate = StudioGates.InOrder[i];
            Assert.Equal(expected[i].Name, gate.Name);
            Assert.Equal(expected[i].Target, gate.Target);
            // The very method, not a copy of its rules.
            Assert.Equal(expected[i].Policy.GetMethod("IsExempt"), gate.IsExempt.Method);
        }
    }

    public static TheoryData<string> Paths => new()
    {
        "/", "/wiki", "/board", "/code", "/git", "/sessions", "/settings", "/welcome", "/workitems/1",
        "/sign-in", "/auth/callback/idp", "/integrity-failed", "/update-required", "/secure-connection",
        "/onboarding", "/onboarding/runtimes", "/Error", "/healthz", "/api/onboarding/status", "/_blazor", "/app.css",
        // Backlog 283: dotted page paths, and real assets in a few spellings.
        "/code/README.md", "/code/src/a.b/c.cs", "/git/history/x.json", "/git/history/src/Program.cs", "/workitems/1.2",
        "/code/app.css", "/app.css/x", "/codeEditor.js", "/CodeEditor.JS", "/vendor/monaco/vs/loader.js", "/favicon.ico",
        "/app%2Ecss", "/nothing.css",
    };

    /// <summary>
    /// For every path, with each gate closed alone and with all of them closed, the decision says exactly what the
    /// gates' own policy classes say, in the middleware's order, except that a static asset the app serves passes
    /// them all (backlog 283). Fails if StudioGates grows a rule of its own.
    /// </summary>
    [Theory]
    [MemberData(nameof(Paths))]
    public async Task The_decision_matches_each_gates_own_policy_for_every_path(string path)
    {
        var cases = new (GateState State, (Func<string, bool> IsExempt, string Target)[] Closed)[]
        {
            (GateState.AllOpen, []),
            (GateState.AllOpen with { IntegrityFailed = true }, [(IntegrityLock.IsExempt, IntegrityLock.FailedPath)]),
            (GateState.AllOpen with { UpdateCritical = true }, [(CriticalUpdateLock.IsExempt, CriticalUpdateLock.UpdatePath)]),
            (GateState.AllOpen with { VpnDown = true }, [(VpnGate.IsExempt, VpnGate.GatePath)]),
            (GateState.AllOpen with { SignInRequired = true }, [(SignInGate.IsExempt, SignInGate.GatePath)]),
            (GateState.AllOpen with { OnboardingIncomplete = true }, [(OnboardingGate.IsExempt, OnboardingGate.GatePath)]),
            (new GateState(true, true, true, true, true),
                [(IntegrityLock.IsExempt, IntegrityLock.FailedPath), (CriticalUpdateLock.IsExempt, CriticalUpdateLock.UpdatePath),
                 (VpnGate.IsExempt, VpnGate.GatePath), (SignInGate.IsExempt, SignInGate.GatePath),
                 (OnboardingGate.IsExempt, OnboardingGate.GatePath)]),
        };

        foreach (var (state, closed) in cases)
        {
            using var provider = Services(state).BuildServiceProvider();
            var expected = TestAssets.Serves(path)
                ? null
                : closed.Where(g => !g.IsExempt(path)).Select(g => g.Target).FirstOrDefault();

            Assert.Equal(expected, await StudioGates.RedirectAsync(path, provider, CancellationToken.None));
        }
    }

    [Fact]
    public void The_middleware_and_the_guard_both_ask_studio_gates_and_neither_holds_a_rule_of_its_own()
    {
        var root = RepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "src", "Fravashi.Web", "Program.cs"));
        var guard = File.ReadAllText(Path.Combine(root, "src", "Fravashi.Web", "Components", "GateGuard.razor"));
        var routes = File.ReadAllText(Path.Combine(root, "src", "Fravashi.Web", "Components", "Routes.razor"));

        Assert.Contains("StudioGates.RedirectAsync(path, context.RequestServices, context.RequestAborted)", program, StringComparison.Ordinal);
        Assert.Contains("StudioGates.RedirectAsync(path, Services, context.CancellationToken)", guard, StringComparison.Ordinal);
        Assert.Contains("<GateGuard />", routes, StringComparison.Ordinal);

        foreach (var policy in new[] { "IntegrityLock", "CriticalUpdateLock", "VpnGate", "SignInGate", "OnboardingGate" })
        {
            Assert.DoesNotContain($"{policy}.IsExempt", program, StringComparison.Ordinal);
            Assert.DoesNotContain($"{policy}.IsExempt", guard, StringComparison.Ordinal);
        }

        foreach (var condition in new[] { "IsFailClosed", "IsLocked", "VpnRequired", "IsConnected", "SignInGate.AdmitsAsync", "IsComplete)" })
        {
            Assert.DoesNotContain(condition, guard, StringComparison.Ordinal);
            Assert.DoesNotContain(condition, program[program.IndexOf("app.Use(async (context, next) =>", StringComparison.Ordinal)..], StringComparison.Ordinal);
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    public sealed record GateState(
        bool IntegrityFailed, bool UpdateCritical, bool VpnDown, bool SignInRequired, bool OnboardingIncomplete)
    {
        public static GateState AllOpen { get; } = new(false, false, false, false, false);
    }

    /// <summary>The static assets these tests treat as served, as StaticAssetRoutes reads them from the app's endpoints.</summary>
    internal static readonly StaticAssetRoutes TestAssets =
        new(["app.css", "codeEditor.js", "vendor/monaco/vs/loader.js", "favicon.ico", "_framework/blazor.web.js"]);

    internal static IServiceCollection Services(GateState state, IServiceCollection? services = null)
    {
        services ??= new ServiceCollection();
        services.AddSingleton(TestAssets);
        var integrity = new IntegrityStatusProvider();
        if (state.IntegrityFailed)
        {
            integrity.Set(new IntegrityStatus(
                IntegrityMode.Enforced, "enforced in this test", Provisioned: true,
                [new IntegrityCheck("studio", "Variant", IntegrityCheckStatus.Fail, "full", "corp-vpn", "does not match")],
                DateTimeOffset.UnixEpoch, "podman"));
        }

        services.AddSingleton<IIntegrityStatusProvider>(integrity);
        services.AddSingleton<IProductUpdateStatus>(new FixedUpdateStatus(state.UpdateCritical
            ? new ProductUpdateState(ProductUpdateSeverity.Critical, "1.0.0", "1.0.1", null, null)
            : ProductUpdateState.None));
        var flags = new FakeFeatureFlags(
            "full", new FeatureFlags(GitHubLogin: true, VpnRequired: state.VpnDown, AuthRequired: state.SignInRequired));
        services.AddSingleton<IFeatureFlags>(flags);
        services.AddSingleton<IVpnConnectivity>(new UnconfiguredVpnConnectivity());
        var settings = new FakeConfigurationStore();
        var secrets = new FakeSecretStore();
        services.AddSingleton<IConfigurationStore>(settings);
        services.AddSingleton<ISecretStore>(secrets);
        services.AddSingleton(new IdpSessionState(settings, secrets, flags, env: _ => null));
        services.AddSingleton(new IdpAuthService(
            new HttpClient(), settings, secrets, new InMemoryAuthCodeStateStore(), new FakeConfigurationReloader(), env: _ => null));
        services.AddSingleton(VerificationRun.Decide(null, null, null));
        services.AddSingleton<IOnboardingState>(new FakeOnboardingState(complete: !state.OnboardingIncomplete));
        return services;
    }

    private static BunitContext Context(GateState state)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        Services(state, ctx.Services);
        // What MinimalWindowLayout's title bar reads, as StudioErrorTests registers it.
        ctx.Services.AddSingleton<IGitService>(new UnavailableGitService());
        ctx.Services.AddSingleton<INotificationHistory>(new InMemoryNotificationHistory());
        ctx.Services.AddSingleton(new AgentPanelState());
        ctx.Services.AddSingleton(new ScratchpadState());
        ctx.Services.AddSingleton(new StartDialogState());
        ctx.Services.AddSingleton(new BridgeWorkspaceContext(new ConfigurationBuilder().Build()));
        ctx.Services.AddSingleton<IWorkspaceStore>(new EmptyWorkspaceStore());
        // What the sign-in page subscribes to for reconnections (#372).
        ctx.Services.AddSingleton(new CircuitConnection());
        return ctx;
    }

    private sealed class FixedUpdateStatus(ProductUpdateState state) : IProductUpdateStatus
    {
        public ProductUpdateState Current => state;
    }

    /// <summary>
    /// A NavigationManager that behaves as the server's does where it matters here: an intercepted link click asks the
    /// handlers, and so does every NavigateTo, forced or not, before anything is sent to the browser. Bounded, so a
    /// guard that loops fails the test instead of the test host.
    /// </summary>
    private sealed class ServerLikeNavigationManager : NavigationManager
    {
        private const int MaxRuns = 10;

        public ServerLikeNavigationManager() => Initialize("http://localhost/", "http://localhost/integrity-failed");

        public int HandlerRuns { get; private set; }

        public List<(string Uri, bool ForceLoad)> Sent { get; } = [];

        public void ClickLink(string uri) => Ask(uri, intercepted: true, forceLoad: false).GetAwaiter().GetResult();

        protected override void NavigateToCore(string uri, NavigationOptions options) =>
            _ = Ask(uri, intercepted: false, options.ForceLoad);

        protected override void SetNavigationLockState(bool value)
        {
        }

        private async Task Ask(string uri, bool intercepted, bool forceLoad)
        {
            if (++HandlerRuns > MaxRuns)
            {
                return;
            }

            if (await NotifyLocationChangingAsync(uri, null, intercepted))
            {
                Sent.Add((uri, forceLoad));
            }
        }
    }

    private sealed class ThrowingIntegrityStatus : IIntegrityStatusProvider
    {
        public IntegrityStatus Current => throw new InvalidOperationException("the integrity status is unavailable in this test");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fravashi.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root (Fravashi.sln) was not found.");
    }
}
