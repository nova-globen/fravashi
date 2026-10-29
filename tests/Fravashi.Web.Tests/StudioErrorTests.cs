using System.Net;
using System.Reflection;
using Bunit;
using Fravashi.Application.Git;
using Fravashi.Application.Notifications;
using Fravashi.Application.Persistence;
using Fravashi.Infrastructure.Bridge;
using Fravashi.Infrastructure.Notifications;
using Fravashi.Web.Components.Layout;
using Fravashi.Web.Components.Pages;
using Fravashi.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 281 (plan @99): a page request whose handler throws answers the studio's own Error page inside the
/// shell, with its 500, instead of what backlog 280 made it answer with: a bare 404 (nothing routed at /Error)
/// re-executed again into the Not found page. An API (or other machine) request, told apart by its ORIGINAL path
/// (the request path is already rewritten to /Error by the time StudioError's guard runs), keeps a plain 500 with
/// no body, and the exception's own text never reaches either body.
/// <para>
/// The pipeline is driven with the real middleware and the real window gate decisions, answering /Error from a
/// stub; the page is rendered on its own, and its route and layout attribute are checked. What these cannot show,
/// the shell drawn around the real Blazor endpoint's answer and that answer keeping its 500, is what the host check
/// in the window after a release confirms (same gap backlog 280 carries for /not-found).
/// </para>
/// </summary>
public sealed class StudioErrorTests
{
    private const string Window = "Mozilla/5.0 FravashiWindow";
    private const string PageMarker = "studio error page";
    private const string ExceptionMessage = "studio error test exception, never shown to anyone";

    // ---- the decision -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/boom")]
    [InlineData("/workitems/abc")]
    [InlineData("/")]
    [InlineData("/code/README.md")]
    [InlineData("/code/src/a.b/c.cs")]
    [InlineData("/git/history/x.json")]
    public void A_page_s_original_path_shows_the_page(string originalPath) =>
        Assert.True(StudioError.ShowsPage(originalPath, StudioNotFoundTests.Assets));

    [Theory]
    [InlineData("/api/boom")]
    [InlineData("/API/x")]
    [InlineData("/healthz/x")]
    [InlineData("/_blazor/x")]
    [InlineData("/app.css")]
    [InlineData("/codeEditor.js")]
    [InlineData("/vendor/monaco/vs/loader.js")]
    public void A_machine_or_asset_path_keeps_its_plain_status(string originalPath) =>
        Assert.False(StudioError.ShowsPage(originalPath, StudioNotFoundTests.Assets));

    /// <summary>
    /// Every gate that exempts the Error page, and the direct-request guard in UseStudioError, both ask this one
    /// predicate rather than rolling their own StartsWith: a trailing-slash request is the case that caught them
    /// disagreeing in review (the gates exempted "/Error" as a prefix, the guard refused only the exact path).
    /// </summary>
    [Theory]
    [InlineData("/Error", true)]
    [InlineData("/Error/", true)]
    [InlineData("/error/x", true)]
    [InlineData("/ErrorPage", true)]
    [InlineData("/", false)]
    [InlineData("/board", false)]
    public void IsPagePath_agrees_with_every_caller(string path, bool isErrorPath) =>
        Assert.Equal(isErrorPath, StudioError.IsPagePath(path));

    // ---- the pipeline, self-hosted on a loopback port ---------------------------------------------------------------

    [Fact]
    public async Task A_page_handler_that_throws_answers_500_with_the_error_page()
    {
        await using var host = await PipelineHost.StartAsync();

        var response = await host.GetAsync("/boom", Window);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains(PageMarker, body, StringComparison.Ordinal);
        Assert.DoesNotContain(ExceptionMessage, body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Backlog 283: a page whose route ends in a file name is a page, so a throw there gets the Error page (the dot used
    /// to make it a plain 500), and signed out it never runs at all: the gate redirects it like any other page.
    /// </summary>
    [Theory]
    [InlineData(true, "/code/README.md")]
    [InlineData(false, "/code/README.md")]
    [InlineData(true, "/code/src/a.b/c.cs")]
    [InlineData(false, "/code/src/a.b/c.cs")]
    public async Task A_dotted_page_is_a_page_to_the_error_handler_and_to_the_gate(bool signedIn, string path)
    {
        await using var host = await PipelineHost.StartAsync(signedIn);

        var response = await host.GetAsync(path, Window);
        var body = await response.Content.ReadAsStringAsync();

        if (signedIn)
        {
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Contains(PageMarker, body, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal(SignInGate.GatePath, response.Headers.Location?.OriginalString);
            Assert.DoesNotContain(PageMarker, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task An_api_handler_that_throws_is_still_a_plain_500()
    {
        await using var host = await PipelineHost.StartAsync();

        var response = await host.GetAsync("/api/boom", Window);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(body);
        Assert.DoesNotContain(ExceptionMessage, body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The gate exemption above belongs to the page's ROUTE, not to a thrown request specifically: without this
    /// guard, a direct GET of /Error would carry no IExceptionHandlerPathFeature but would still be exempt from
    /// every redirecting gate and would start a live interactive circuit (the title bar's workspace and branch
    /// switchers included) while that gate is closed for whatever reason it is closed. Proven both signed in and
    /// signed out (the redirect happens before the stand-in sign-in gate ever runs) and with a trailing slash
    /// (every gate exempts "/Error" as a PREFIX; the guard has to agree on the same prefix or a slash alone
    /// would reopen this).
    /// </summary>
    [Theory]
    [InlineData(true, "/Error")]
    [InlineData(false, "/Error")]
    [InlineData(true, "/Error/")]
    [InlineData(false, "/Error/")]
    public async Task A_direct_request_for_the_error_page_without_an_exception_is_sent_home(bool signedIn, string path)
    {
        await using var host = await PipelineHost.StartAsync(signedIn);

        var response = await host.GetAsync(path, Window);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
        Assert.DoesNotContain(PageMarker, body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Program.cs skips UseExceptionHandler in Development, but the five gates exempt /Error whatever the
    /// environment is. Without the guard wired there too (UseStudioErrorGuard, called on its own), a direct GET
    /// of /Error on a dev host would pass every closed gate and reach the live page (caught in review).
    /// </summary>
    [Fact]
    public async Task The_direct_request_guard_still_runs_in_development_where_the_handler_is_skipped()
    {
        await using var host = await PipelineHost.StartAsync(development: true);

        var response = await host.GetAsync(StudioError.PagePath, Window);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
        Assert.DoesNotContain(PageMarker, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_browser_the_window_gate_refuses_gets_the_refusal_and_never_the_error_page()
    {
        await using var host = await PipelineHost.StartAsync();

        var response = await host.GetAsync("/boom", "Mozilla/5.0 Chrome/140");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("Fravashi opens in its own window", body, StringComparison.Ordinal);
        Assert.DoesNotContain(PageMarker, body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Signed out, the stand-in sign-in gate redirects an ORDINARY page before its handler ever runs, so /boom
    /// never throws and the Error page is never reached at all. This is the baseline the next test's exemption
    /// is measured against: without it, EVERY page behaves like this one, gate pages included.
    /// </summary>
    [Fact]
    public async Task A_closed_gate_redirects_an_ordinary_page_before_its_handler_can_throw()
    {
        await using var host = await PipelineHost.StartAsync(signedIn: false);

        var response = await host.GetAsync("/boom", Window);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(SignInGate.GatePath, response.Headers.Location?.OriginalString);
        Assert.DoesNotContain(PageMarker, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The gate stand-in reuses the real SignInGate.IsExempt, so /sign-in is reachable while signed out: that is
    /// how the real gate lets its own page render. A throw on that page re-executes to /Error same as any other
    /// page, and SignInGate now exempts /Error too, or this second pass would redirect straight back to /sign-in
    /// (a 302 instead of the 500) rather than reaching the error page. This is what makes the decision real rather
    /// than theoretical: without the exemption, this test fails with a redirect.
    /// </summary>
    [Fact]
    public async Task A_thrown_request_on_a_gate_exempt_page_still_reaches_the_error_page()
    {
        await using var host = await PipelineHost.StartAsync(signedIn: false);

        var response = await host.GetAsync(SignInGate.GatePath, Window);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains(PageMarker, body, StringComparison.Ordinal);
    }

    // ---- the page and its wiring in the studio --------------------------------------------------------------------

    [Fact]
    public void The_page_renders_its_bar_its_message_and_a_way_back()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<ErrorPage>();

        Assert.Equal("Something went wrong", cut.Find(".studio-page-bar-title").TextContent);
        Assert.Contains("went wrong on the studio's side", cut.Find("[data-testid=studio-error] [role=alert]").TextContent, StringComparison.Ordinal);
        var home = cut.Find("[data-testid=studio-error-home]");
        Assert.Equal("/", home.GetAttribute("href"));
        // The re-executed render is static (no circuit), so this anchor has no event handler of its own; disabling
        // enhanced nav keeps a click a normal full document navigation rather than a fetch-and-patch of this
        // already-at-a-500 page. Every gate runs on BOTH kinds of request either way: this is about the browser's
        // own rendering, not about which middleware the request passes through.
        Assert.Equal("false", home.GetAttribute("data-enhance-nav"));
    }

    [Fact]
    public void The_page_is_routed_at_the_re_execute_path_and_names_the_minimal_layout()
    {
        var page = typeof(ErrorPage);

        Assert.Contains(page.GetCustomAttributes<RouteAttribute>(), r => r.Template == StudioError.PagePath);
        Assert.Equal(typeof(MinimalWindowLayout), page.GetCustomAttribute<LayoutAttribute>()?.LayoutType);
    }

    /// <summary>
    /// Correction from the supervising host, 2026-10-10: MainLayout's activity rail and sidebar are full in-app
    /// navigation on the app's one persistent circuit, so a page exempted from every redirecting gate must not
    /// render them, or a person could navigate anywhere they link to without the gate ever running again.
    /// MinimalWindowLayout keeps only the window title bar.
    /// </summary>
    [Fact]
    public void The_minimal_layout_renders_the_title_bar_but_no_activity_rail_or_sidebar()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IGitService>(new UnavailableGitService());
        ctx.Services.AddSingleton<INotificationHistory>(new InMemoryNotificationHistory());
        ctx.Services.AddSingleton(new AgentPanelState());
        ctx.Services.AddSingleton(new ScratchpadState());
        ctx.Services.AddSingleton(new StartDialogState());
        ctx.Services.AddSingleton(new BridgeWorkspaceContext(new ConfigurationBuilder().Build()));
        ctx.Services.AddSingleton<IWorkspaceStore>(new EmptyWorkspaceStore());

        var cut = ctx.Render<MinimalWindowLayout>(parameters =>
            parameters.Add(p => p.Body, "<p data-testid=\"layout-body-marker\">body</p>"));

        Assert.NotEmpty(cut.FindAll("[data-testid=window-titlebar]"));
        Assert.Empty(cut.FindAll(".studio-activitybar"));
        Assert.Empty(cut.FindAll(".studio-sidebar"));
        // studio.css's .studio-shell reserves a 56px activity column by default; with no ActivityBar rendered at
        // all, that column would sit blank unless the shell also carries studio-shell-minimal's single-column
        // grid. Both no-sidebar and studio-shell-minimal are required on the element: the responsive rules key
        // off :not(.no-sidebar) and .no-sidebar at several widths, and only all three classes together
        // (specificity 0,3,0) always beat them (caught in review: a two-class version lost that fight below
        // 1100px and 768px).
        var shell = cut.Find(".studio-shell");
        Assert.Contains("no-sidebar", shell.ClassList);
        Assert.Contains("studio-shell-minimal", shell.ClassList);
        Assert.NotEmpty(cut.FindAll("[data-testid=layout-body-marker]"));
    }

    /// <summary>A workspace store whose Get always throws, to drive WindowTitleBar's own initialisation into an error.</summary>
    private sealed class FailingWorkspaceStore : IWorkspaceStore
    {
        public IReadOnlyList<WorkspaceRecord> Recent(int max = 10) => [];
        public WorkspaceRecord? Get(string id) => throw new InvalidOperationException("the workspace store is unavailable in this test");
        public void Touch(string id, string name, string rootPath) { }
        public void Remove(string id) { }
    }

    /// <summary>
    /// Item #281 asks for a page that "must not depend on anything that may have failed". The title bar this
    /// layout keeps reads the workspace store during its own initialisation; if THAT store is what originally
    /// threw, rendering the title bar throws again. The ErrorBoundary around it means the rest of the page (and
    /// the body the layout wraps) still renders, with a bare caption strip standing in for the title bar.
    /// </summary>
    [Fact]
    public void The_minimal_layout_still_renders_when_the_title_bar_s_own_reads_throw()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IGitService>(new UnavailableGitService());
        ctx.Services.AddSingleton<INotificationHistory>(new InMemoryNotificationHistory());
        ctx.Services.AddSingleton(new AgentPanelState());
        ctx.Services.AddSingleton(new ScratchpadState());
        ctx.Services.AddSingleton(new StartDialogState());
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [BridgeWorkspaceContext.ActiveIdKey] = "ws-known" })
            .Build();
        ctx.Services.AddSingleton(new BridgeWorkspaceContext(configuration));
        ctx.Services.AddSingleton<IWorkspaceStore>(new FailingWorkspaceStore());

        var cut = ctx.Render<MinimalWindowLayout>(parameters =>
            parameters.Add(p => p.Body, "<p data-testid=\"layout-body-marker\">body</p>"));

        Assert.Empty(cut.FindAll("[data-testid=window-titlebar]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=window-titlebar-fallback]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=layout-body-marker]"));
    }

    [Fact]
    public void Program_wires_it_ahead_of_studio_not_found_and_every_gate()
    {
        var program = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Fravashi.Web", "Program.cs"));

        var studioError = program.IndexOf("app.UseStudioError();", StringComparison.Ordinal);
        var studioNotFound = program.IndexOf("app.UseStudioNotFound();", StringComparison.Ordinal);
        var firstGate = program.IndexOf("app.Use(async (context, next) =>", StringComparison.Ordinal);

        Assert.True(studioError >= 0, "Program.cs wires UseStudioError");
        Assert.True(studioError < studioNotFound, "before UseStudioNotFound");
        Assert.True(studioNotFound < firstGate, "and so before the first gate");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(program, @"UseStudioError\(\)"));
        Assert.DoesNotContain("UseExceptionHandler", program, StringComparison.Ordinal);
        // The Development branch skips UseExceptionHandler but must still wire the direct-request guard on its
        // own (caught in review): the five gates exempt /Error whatever the environment is.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(program, @"UseStudioErrorGuard\(\);"));
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The studio's order in miniature: <see cref="StudioError.UseStudioError"/> first, then the window-only gate
    /// exactly as Program.cs runs it, then a redirecting gate standing in for the sign-in gate, then the endpoints
    /// that throw.
    /// </summary>
    private sealed class PipelineHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private PipelineHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public HttpClient Client { get; }

        private static IResult Boom() => throw new InvalidOperationException(ExceptionMessage);

        public static async Task<PipelineHost> StartAsync(bool signedIn = true, bool development = false)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(StudioNotFoundTests.Assets);
            var app = builder.Build();

            // Program.cs's own split: outside Development, UseStudioError wires the exception handler and the
            // guard together; in Development (where the framework's own developer exception page answers first)
            // only the guard runs, because the five gates exempt /Error unconditionally either way.
            if (development)
            {
                app.UseStudioErrorGuard();
            }
            else
            {
                app.UseStudioError();
            }

            app.Use(async (context, next) =>
            {
                var path = context.Request.Path.Value ?? "/";
                if (!WindowOnlyGate.IsExempt(path) &&
                    !WindowOnlyGate.IsAllowed(context.Request.Headers.UserAgent.ToString(), requiredToken: null))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    context.Response.ContentType = "text/html; charset=utf-8";
                    await context.Response.WriteAsync(WindowOnlyGate.BlockedHtml);
                    return;
                }

                await next();
            });

            app.Use(async (context, next) =>
            {
                if (!signedIn && !SignInGate.IsExempt(context.Request.Path.Value ?? "/"))
                {
                    context.Response.Redirect(SignInGate.GatePath);
                    return;
                }

                await next();
            });

            app.MapGet("/boom", Boom);
            app.MapGet("/api/boom", Boom);
            app.MapGet("/code/{*FilePath}", Boom);
            app.MapGet(SignInGate.GatePath, Boom);
            app.MapGet(StudioError.PagePath, () => Results.Content($"<p>{PageMarker}</p>", "text/html"));

            await app.StartAsync();
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri(app.Urls.First()),
            };
            return new PipelineHost(app, client);
        }

        public async Task<HttpResponseMessage> GetAsync(string path, string userAgent)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.UserAgent.ParseAdd(userAgent);
            return await Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
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
