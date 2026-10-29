using System.Net;
using System.Reflection;
using Bunit;
using Fravashi.Web.Components.Layout;
using Fravashi.Web.Components.Pages;
using Fravashi.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 280 (plan @98): a GET for a page the studio does not route answers the studio's own Not found page inside
/// the shell, with its 404, instead of a bare 404 that WebView2 shows as Chromium's error page. API, framework,
/// liveness and static paths keep their plain 404, and a request a gate refuses is still refused: the not-found path
/// is wired ahead of every gate, so it is no way around one.
/// <para>
/// The pipeline is driven with the real middleware and the real window gate decisions, answering /not-found from a
/// stub; the page is rendered on its own, and its route and layout attribute are checked. What these cannot show,
/// the shell drawn around the real Blazor endpoint's answer and that answer keeping its 404, is what the host check
/// in the window after a release confirms.
/// </para>
/// </summary>
public sealed class StudioNotFoundTests
{
    private const string Window = "Mozilla/5.0 FravashiWindow";
    private const string PageMarker = "studio not found page";

    // ---- the decision -------------------------------------------------------------------------------------------------

    /// <summary>The static assets these tests treat as served, the way StaticAssetRoutes reads them from the app.</summary>
    internal static readonly StaticAssetRoutes Assets = new(["app.css", "codeEditor.js", "vendor/monaco/vs/loader.js", "favicon.ico"]);

    /// <summary>
    /// Backlog 283: a dot in the path is no longer what makes something an asset. A dotted page path is a page, and so
    /// is a missing file the app never served: the only assets are the routes StaticAssetRoutes knows.
    /// </summary>
    [Theory]
    [InlineData("GET", "/nonexistent-page")]
    [InlineData("GET", "/workitems/abc")]
    [InlineData("GET", "/settings/a/b/c/d/e/f")]
    [InlineData("GET", "/")]
    [InlineData("GET", "/code/README.md")]
    [InlineData("GET", "/git/history/x.json")]
    [InlineData("GET", "/workitems/1.2")]
    [InlineData("GET", "/missing.css")]
    [InlineData("GET", "/vendor/monaco/vs/missing.js")]
    public void An_unrouted_page_request_shows_the_page(string method, string path) =>
        Assert.True(StudioNotFound.ShowsPage(method, path, StatusCodes.Status404NotFound, Assets));

    [Theory]
    [InlineData("GET", "/api/nonexistent", 404)]
    [InlineData("GET", "/API/x", 404)]
    [InlineData("GET", "/healthz/x", 404)]
    [InlineData("GET", "/_framework/missing.js", 404)]
    [InlineData("GET", "/_blazor/x", 404)]
    [InlineData("GET", "/app.css", 404)]
    [InlineData("GET", "/APP.CSS", 404)]
    [InlineData("GET", "/vendor/monaco/vs/loader.js", 404)]
    [InlineData("GET", "/favicon.ico", 404)]
    [InlineData("POST", "/nonexistent-page", 404)]
    [InlineData("HEAD", "/nonexistent-page", 404)]
    [InlineData("GET", "/nonexistent-page", 400)]
    [InlineData("GET", "/nonexistent-page", 403)]
    [InlineData("GET", "/nonexistent-page", 405)]
    [InlineData("GET", "/nonexistent-page", 500)]
    public void Everything_else_keeps_its_plain_status(string method, string path, int status) =>
        Assert.False(StudioNotFound.ShowsPage(method, path, status, Assets));

    // ---- the pipeline, self-hosted on a loopback port ---------------------------------------------------------------

    [Fact]
    public async Task An_unknown_page_path_answers_the_not_found_page_with_its_404()
    {
        await using var host = await PipelineHost.StartAsync();

        var response = await host.GetAsync("/nonexistent-page?ws=abc", Window);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(PageMarker, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/nonexistent")]
    [InlineData("/healthz/nonexistent")]
    [InlineData("/_framework/nonexistent.js")]
    public async Task An_unknown_machine_path_is_still_a_plain_404(string path)
    {
        await using var host = await PipelineHost.StartAsync();

        var response = await host.GetAsync(path, Window);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Backlog 283: a dotted path the studio does not serve is a page request like any other, so it gets the page with
    /// its 404 rather than being taken for an asset by its shape.
    /// </summary>
    [Theory]
    [InlineData("/nonexistent.css")]
    [InlineData("/code-gone/README.md")]
    public async Task An_unknown_dotted_path_answers_the_not_found_page_with_its_404(string path)
    {
        await using var host = await PipelineHost.StartAsync();

        var response = await host.GetAsync(path, Window);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(PageMarker, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_post_to_an_unknown_page_is_still_a_plain_404()
    {
        await using var host = await PipelineHost.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/nonexistent-page");
        request.Headers.UserAgent.ParseAdd(Window);
        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_browser_the_window_gate_refuses_gets_the_refusal_and_never_the_page()
    {
        await using var host = await PipelineHost.StartAsync();

        foreach (var path in new[] { "/nonexistent-page", StudioNotFound.PagePath })
        {
            var response = await host.GetAsync(path, "Mozilla/5.0 Chrome/140");
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("Fravashi opens in its own window", body, StringComparison.Ordinal);
            Assert.DoesNotContain(PageMarker, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_redirecting_gate_still_redirects_an_unknown_page()
    {
        await using var host = await PipelineHost.StartAsync(signedIn: false);

        var response = await host.GetAsync("/nonexistent-page", Window);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(SignInGate.GatePath, response.Headers.Location?.OriginalString);
        Assert.DoesNotContain(PageMarker, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // ---- the page and its wiring in the studio --------------------------------------------------------------------

    [Fact]
    public void The_page_renders_its_bar_its_message_and_a_way_back()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<NotFoundPage>();

        Assert.Equal("Not found", cut.Find(".studio-page-bar-title").TextContent);
        Assert.Contains("nothing at this address", cut.Find("[data-testid=not-found] [role=alert]").TextContent, StringComparison.Ordinal);
        Assert.Equal("/", cut.Find("[data-testid=not-found-home]").GetAttribute("href"));
    }

    [Fact]
    public void The_page_is_routed_at_the_re_execute_path_and_names_the_shell_layout()
    {
        var page = typeof(NotFoundPage);

        Assert.Contains(page.GetCustomAttributes<RouteAttribute>(), r => r.Template == StudioNotFound.PagePath);
        Assert.Equal(typeof(MainLayout), page.GetCustomAttribute<LayoutAttribute>()?.LayoutType);
    }

    [Fact]
    public void The_router_shows_the_same_page_for_an_in_app_navigation_to_nothing()
    {
        var routes = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Fravashi.Web", "Components", "Routes.razor"));

        Assert.Contains("NotFoundPage=\"typeof(Pages.NotFoundPage)\"", routes, StringComparison.Ordinal);
        Assert.DoesNotContain("<NotFound>", routes, StringComparison.Ordinal);
    }

    [Fact]
    public void Program_wires_it_ahead_of_antiforgery_and_every_gate()
    {
        var program = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Fravashi.Web", "Program.cs"));

        var notFound = program.IndexOf("app.UseStudioNotFound();", StringComparison.Ordinal);
        var antiforgery = program.IndexOf("app.UseAntiforgery();", StringComparison.Ordinal);
        var firstGate = program.IndexOf("app.Use(async (context, next) =>", StringComparison.Ordinal);

        Assert.True(notFound >= 0, "Program.cs wires UseStudioNotFound");
        Assert.True(notFound < antiforgery, "before UseAntiforgery");
        Assert.True(antiforgery < firstGate, "and so before the first gate");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(program, @"UseStudioNotFound\(\)"));
        Assert.DoesNotContain("UseStatusCodePages", program, StringComparison.Ordinal);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The studio's order in miniature: <see cref="StudioNotFound.UseStudioNotFound"/> first, then the window-only gate
    /// exactly as Program.cs runs it, then a redirecting gate standing in for the sign-in gate, then the endpoints.
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

        public static async Task<PipelineHost> StartAsync(bool signedIn = true)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(Assets);
            var app = builder.Build();

            app.UseStudioNotFound();

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

            app.MapGet(StudioNotFound.PagePath, () => Results.Content($"<p>{PageMarker}</p>", "text/html"));

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
