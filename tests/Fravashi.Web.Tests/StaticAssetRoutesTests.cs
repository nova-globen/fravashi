using System.Net;
using Fravashi.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 283 (plan @101): every redirecting gate used to exempt any path with a dot in its last segment, so a page whose
/// catch-all route ends in a file name (<c>/code/README.md</c>, <c>/git/history/src/Program.cs</c>) passed them all. What
/// is a static asset is now <see cref="StaticAssetRoutes"/>, read from the endpoints MapStaticAssets maps. These tests map
/// the studio's REAL static web assets manifest (the one Program.cs's MapStaticAssets reads) and drive the gate
/// middleware exactly as Program.cs wires it, with every gate closed.
/// </summary>
public sealed class StaticAssetRoutesTests
{
    private const string Manifest = "Fravashi.Web.staticwebassets.endpoints.json";

    [Fact]
    public void The_routes_are_the_files_the_studio_really_serves_and_no_page()
    {
        using var app = StudioAssetsApp();
        var routes = new StaticAssetRoutes(new CompositeEndpointDataSource(((IEndpointRouteBuilder)app).DataSources));

        foreach (var asset in new[]
        {
            "/app.css", "/studio.css", "/codeEditor.js", "/reconnect.js", "/favicon.ico", "/brand/fravashi.png",
            "/vendor/monaco/vs/loader.js", "/_framework/blazor.web.js", "/APP.CSS",
        })
        {
            Assert.True(routes.Serves(asset), asset);
        }

        foreach (var page in new[]
        {
            "/", "/code", "/code/README.md", "/code/src/a.b/c.cs", "/git/history/x.json", "/git/history/src/Program.cs",
            "/code/app.css", "/app.css/x", "/nothing.css", "/vendor/monaco/vs/missing.js", "/app%2Ecss", "app.css",
        })
        {
            Assert.False(routes.Serves(page), page);
        }
    }

    [Fact]
    public void Program_registers_it_from_the_apps_endpoints_and_maps_the_static_assets()
    {
        var program = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Fravashi.Web", "Program.cs"));

        Assert.Contains(
            "builder.Services.AddSingleton(services => new StaticAssetRoutes(services.GetRequiredService<EndpointDataSource>()));",
            program,
            StringComparison.Ordinal);
        Assert.Contains("app.MapStaticAssets();", program, StringComparison.Ordinal);
    }

    [Fact]
    public void No_gate_and_no_page_or_asset_decision_asks_the_shape_of_a_path_any_more()
    {
        var services = Path.Combine(RepositoryRoot(), "src", "Fravashi.Web", "Services");
        foreach (var file in new[]
        {
            "IntegrityLock.cs", "CriticalUpdateLock.cs", "VpnGate.cs", "SignInGate.cs", "OnboardingGate.cs",
            "StudioGates.cs", "StudioNotFound.cs", "StudioError.cs",
        })
        {
            var source = File.ReadAllText(Path.Combine(services, file));
            Assert.DoesNotContain("HasExtension(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("GetExtension(", source, StringComparison.Ordinal);
        }
    }

    public static TheoryData<string> DottedPages => new()
    {
        "/code/README.md", "/code/src/a.b/c.cs", "/git/history/x.json", "/git/history/src/Program.cs", "/workitems/1.2",
    };

    [Theory]
    [MemberData(nameof(DottedPages))]
    public async Task Over_http_a_dotted_page_path_is_redirected_by_a_closed_gate(string path)
    {
        await using var host = await GatedHost.StartAsync();

        var response = await host.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(IntegrityLock.FailedPath, response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("/app.css")]
    [InlineData("/codeEditor.js")]
    [InlineData("/vendor/monaco/vs/loader.js")]
    [InlineData("/favicon.ico")]
    [InlineData("/brand/fravashi.png")]
    public async Task Over_http_a_real_static_asset_passes_every_gate_closed_at_once(string path)
    {
        await using var host = await GatedHost.StartAsync();

        var response = await host.Client.GetAsync(path);

        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal([path], host.Passed);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private static WebApplication StudioAssetsApp(WebApplicationBuilder? builder = null)
    {
        builder ??= WebApplication.CreateSlimBuilder();
        var app = builder.Build();
        app.MapStaticAssets(Path.Combine(AppContext.BaseDirectory, Manifest));
        return app;
    }

    /// <summary>
    /// The studio's gate middleware exactly as Program.cs wires it, with every one of the five gates closed, ahead of
    /// the studio's real static asset endpoints. A request that passes the gates is recorded as it leaves them, so a test
    /// can tell "the gates let it through" from whatever the asset endpoint then answers.
    /// </summary>
    private sealed class GatedHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private GatedHost(WebApplication app, HttpClient client, System.Collections.Concurrent.ConcurrentQueue<string> passed)
        {
            _app = app;
            Client = client;
            Passed = passed;
        }

        public HttpClient Client { get; }

        /// <summary>Every request path the gates let through, in order.</summary>
        public System.Collections.Concurrent.ConcurrentQueue<string> Passed { get; }

        public static async Task<GatedHost> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            GateGuardTests.Services(new GateGuardTests.GateState(true, true, true, true, true), builder.Services);
            // Registered after the test set, so this one, Program.cs's own registration, is what the gates resolve.
            builder.Services.AddSingleton(services => new StaticAssetRoutes(services.GetRequiredService<EndpointDataSource>()));
            var app = builder.Build();
            var passed = new System.Collections.Concurrent.ConcurrentQueue<string>();

            app.Use(async (context, next) =>
            {
                var path = context.Request.Path.Value ?? "/";
                if (await StudioGates.RedirectAsync(path, context.RequestServices, context.RequestAborted) is { } target)
                {
                    context.Response.Redirect(target);
                    return;
                }

                passed.Enqueue(path);
                await next();
            });

            app.MapStaticAssets(Path.Combine(AppContext.BaseDirectory, Manifest));
            app.MapGet("/code/{*FilePath}", () => Results.Text("a page"));
            app.MapGet("/git/history/{*HistoryPath}", () => Results.Text("a page"));
            app.MapGet("/workitems/{number}", () => Results.Text("a page"));

            await app.StartAsync();
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri(app.Urls.First()),
            };
            return new GatedHost(app, client, passed);
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
