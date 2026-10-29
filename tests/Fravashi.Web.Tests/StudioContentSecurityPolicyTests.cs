using System.Net;
using Fravashi.Application.Prototypes;
using Fravashi.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 287: the studio sends a Content-Security-Policy on every page response, so a javascript: URL or an inline
/// event handler that reaches a page runs nowhere (#286 measured one running in the studio's origin). The pipeline is
/// driven self-hosted with the real middleware, the real window gate and the real Not found re-execution, the way
/// StudioNotFoundTests does. What this cannot show is every page of the real app running clean under the policy:
/// that is the host's drive of the installed window after a release.
/// </summary>
public sealed class StudioContentSecurityPolicyTests
{
    private const string Window = "Mozilla/5.0 FravashiWindow";

    // ---- the policy -----------------------------------------------------------------------------------------------

    [Fact]
    public void Script_runs_from_the_studio_s_own_files_and_the_prototype_s_hash_only()
    {
        var script = Directive(StudioContentSecurityPolicy.Value, "script-src");

        Assert.Equal(["'self'", $"'{PrototypeRenderer.ScriptHash}'"], script);
        Assert.DoesNotContain(StudioContentSecurityPolicy.Value.Split(';'), d => d.Contains("unsafe-eval", StringComparison.Ordinal));
        Assert.DoesNotContain(script, s => s.Contains("unsafe", StringComparison.Ordinal) || s.EndsWith(':'));
    }

    [Theory]
    [InlineData("default-src", "'self'")]
    [InlineData("object-src", "'none'")]
    [InlineData("base-uri", "'self'")]
    [InlineData("frame-ancestors", "'none'")]
    [InlineData("form-action", "'self'")]
    public void The_fixed_directives_say_what_the_item_asks(string name, string value) =>
        Assert.Equal([value], Directive(StudioContentSecurityPolicy.Value, name));

    [Fact]
    public void Inline_style_is_the_one_inline_allowance()
    {
        Assert.Equal(["'self'", "'unsafe-inline'"], Directive(StudioContentSecurityPolicy.Value, "style-src"));
        Assert.Equal(["'self'", "blob:"], Directive(StudioContentSecurityPolicy.Value, "worker-src"));
    }

    // ---- the pipeline, self-hosted on a loopback port ---------------------------------------------------------------

    [Fact]
    public async Task A_page_response_carries_the_policy()
    {
        await using var host = await PipelineHost.StartAsync();

        var response = await host.GetAsync("/code?ws=abc", Window);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(StudioContentSecurityPolicy.Value, Header(response));
    }

    [Fact]
    public async Task The_not_found_page_and_the_window_refusal_carry_it_too()
    {
        await using var host = await PipelineHost.StartAsync();

        var notFound = await host.GetAsync("/nonexistent-page", Window);
        var refused = await host.GetAsync("/code", "Mozilla/5.0 Chrome/140");

        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal(StudioContentSecurityPolicy.Value, Header(notFound));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(StudioContentSecurityPolicy.Value, Header(refused));
    }

    [Fact]
    public async Task A_response_that_names_its_own_policy_keeps_it()
    {
        await using var host = await PipelineHost.StartAsync();

        var response = await host.GetAsync("/own-policy", Window);

        Assert.Equal("sandbox", Header(response));
    }

    // ---- the real Razor components endpoint -------------------------------------------------------------------------

    [Fact]
    public async Task A_page_rendered_by_the_razor_components_endpoint_carries_exactly_the_studio_s_policy()
    {
        // Blazor's interactive server render mode adds "frame-ancestors 'self'" while the page renders, so with its
        // default the studio's policy was never sent (the host measured it on 7acce2e). This drives that endpoint.
        await using var host = await RazorHost.StartAsync(StudioContentSecurityPolicy.LeaveFrameAncestorsToTheStudio);

        var response = await host.GetAsync(CspPage.Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("csp page", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var header = Assert.Single(response.Headers.GetValues(StudioContentSecurityPolicy.HeaderName));
        Assert.Equal(StudioContentSecurityPolicy.Value, header);
        Assert.Equal(["'self'", $"'{PrototypeRenderer.ScriptHash}'"], Directive(header, "script-src"));
        Assert.Equal(["'none'"], Directive(header, "frame-ancestors"));
    }

    [Fact]
    public async Task With_blazor_s_default_its_header_wins_which_is_why_the_option_is_set()
    {
        // The control: proves the host above really reaches the code path that pre-empted the policy.
        await using var host = await RazorHost.StartAsync(_ => { });

        var response = await host.GetAsync(CspPage.Route);

        var header = Assert.Single(response.Headers.GetValues(StudioContentSecurityPolicy.HeaderName));
        Assert.Equal("frame-ancestors 'self'", header);
    }

    [Fact]
    public void Program_switches_blazor_s_frame_ancestors_header_off()
    {
        var program = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Fravashi.Web", "Program.cs"));

        Assert.Contains(
            ".AddInteractiveServerRenderMode(StudioContentSecurityPolicy.LeaveFrameAncestorsToTheStudio)",
            program,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Program_wires_it_outermost()
    {
        var program = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Fravashi.Web", "Program.cs"));

        var policy = program.IndexOf("app.UseStudioContentSecurityPolicy();", StringComparison.Ordinal);
        var error = program.IndexOf("app.UseStudioError();", StringComparison.Ordinal);
        var notFound = program.IndexOf("app.UseStudioNotFound();", StringComparison.Ordinal);

        Assert.True(policy >= 0, "Program.cs wires UseStudioContentSecurityPolicy");
        Assert.True(policy < error && policy < notFound, "ahead of the error and Not found re-execution");
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private static string[] Directive(string policy, string name) =>
        policy.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Single(parts => parts[0] == name)[1..];

    private static string? Header(HttpResponseMessage response) =>
        response.Headers.TryGetValues(StudioContentSecurityPolicy.HeaderName, out var values) ? values.Single() : null;

    /// <summary>The policy first, then the Not found re-execution and the window-only gate as Program.cs runs them.</summary>
    private sealed class PipelineHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private PipelineHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        private HttpClient Client { get; }

        public static async Task<PipelineHost> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(StudioNotFoundTests.Assets);
            var app = builder.Build();

            app.UseStudioContentSecurityPolicy();
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

            app.MapGet("/code", () => Results.Content("<p>code page</p>", "text/html"));
            app.MapGet(StudioNotFound.PagePath, () => Results.Content("<p>not found</p>", "text/html"));
            app.MapGet("/own-policy", (HttpContext context) =>
            {
                context.Response.Headers[StudioContentSecurityPolicy.HeaderName] = "sandbox";
                return Results.Content("<p>own</p>", "text/html");
            });

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

    /// <summary>A routable page the Razor components endpoint renders statically, as it renders every studio page.</summary>
    [Route(Route)]
    public sealed class CspPage : ComponentBase
    {
        public const string Route = "/csp-page";

        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            builder.AddMarkupContent(0, "<!DOCTYPE html><html><body><p>csp page</p></body></html>");
    }

    /// <summary>
    /// The policy middleware, antiforgery and <c>MapRazorComponents().AddInteractiveServerRenderMode</c> as Program.cs
    /// wires them, with the render mode's options supplied by the test.
    /// </summary>
    private sealed class RazorHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly HttpClient _client;

        private RazorHost(WebApplication app, HttpClient client)
        {
            _app = app;
            _client = client;
        }

        public static async Task<RazorHost> StartAsync(Action<ServerComponentsEndpointOptions> renderMode)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddRazorComponents().AddInteractiveServerComponents();
            var app = builder.Build();

            app.UseStudioContentSecurityPolicy();
            app.UseAntiforgery();
            app.MapRazorComponents<CspPage>().AddInteractiveServerRenderMode(renderMode);

            await app.StartAsync();
            return new RazorHost(app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
        }

        public Task<HttpResponseMessage> GetAsync(string path) => _client.GetAsync(path);

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fravashi.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root (Fravashi.sln) was not found.");
    }
}
