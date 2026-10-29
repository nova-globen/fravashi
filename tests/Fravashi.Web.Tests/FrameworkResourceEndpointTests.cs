using System.Net;
using Fravashi.Domain.Framework;
using Fravashi.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 284: GET /api/framework/resources/{resourceId} sits under /api, which WindowOnlyGate.IsExempt treats
/// as a machine path, but it serves an uploaded resource's bytes rather than acting as one of the agent/session
/// machine endpoints. It asks WindowOnlyGate.IsAllowed itself, the same way Program.cs's outer gate does, so a
/// request carrying the window's own marker still gets the file while anything else is refused.
/// </summary>
public sealed class FrameworkResourceEndpointTests
{
    private const string ResourceId = "res-1";
    private const string Window = "Mozilla/5.0 FravashiWindow/1.0";
    private const string PlainBrowser = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/126.0 Safari/537.36";

    [Fact]
    public async Task A_request_without_the_window_marker_is_refused()
    {
        await using var host = await EndpointHost.StartAsync();

        var response = await host.GetAsync(PlainBrowser);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_request_carrying_the_window_marker_gets_the_resource()
    {
        await using var host = await EndpointHost.StartAsync();

        var response = await host.GetAsync(Window);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hello", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_configured_token_is_required_even_with_the_window_marker()
    {
        await using var host = await EndpointHost.StartAsync(requiredToken: "abc123");

        var withoutToken = await host.GetAsync(Window);
        var withWrongToken = await host.GetAsync($"{Window} tk/zzz999");
        var withToken = await host.GetAsync($"{Window} tk/abc123");

        Assert.Equal(HttpStatusCode.Forbidden, withoutToken.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, withWrongToken.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withToken.StatusCode);
    }

    /// <summary>Backlog 287: an upload is served in the studio's origin, so the browser never guesses its type.</summary>
    [Fact]
    public async Task Every_resource_is_sent_nosniff_and_as_an_attachment()
    {
        await using var host = await EndpointHost.StartAsync();

        var response = await host.GetAsync(Window);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
    }

    /// <summary>Backlog 287: an html or xml upload is never served as itself, and runs nothing even if it were shown.</summary>
    [Theory]
    [InlineData("text/html")]
    [InlineData("TEXT/HTML; charset=utf-8")]
    [InlineData("application/xhtml+xml")]
    [InlineData("text/xml")]
    [InlineData("application/xml")]
    public async Task An_html_or_xml_upload_is_an_inert_octet_stream_download(string contentType)
    {
        await using var host = await EndpointHost.StartAsync(contentType: contentType, fileName: "page.html");

        var response = await host.GetAsync(Window);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(FrameworkResourceEndpoint.InertPolicy, response.Headers.GetValues("Content-Security-Policy").Single());
    }

    /// <summary>
    /// An uploaded SVG icon keeps its type, because SkillIconDisplay draws it through an img element (where it runs no
    /// script), but it is still a download and carries the policy that runs nothing.
    /// </summary>
    [Fact]
    public async Task An_svg_upload_keeps_its_type_but_is_an_inert_download()
    {
        await using var host = await EndpointHost.StartAsync(contentType: "image/svg+xml", fileName: "icon.svg");

        var response = await host.GetAsync(Window);

        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(FrameworkResourceEndpoint.InertPolicy, response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task A_resource_with_no_stored_type_is_an_octet_stream()
    {
        await using var host = await EndpointHost.StartAsync(contentType: null, fileName: "blob.bin");

        var response = await host.GetAsync(Window);

        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    private sealed class EndpointHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private EndpointHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        private HttpClient Client { get; }

        public static async Task<EndpointHost> StartAsync(
            string? requiredToken = null, string? contentType = "text/plain", string fileName = "icon.txt")
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            if (requiredToken is not null)
            {
                builder.Configuration[WindowOnlyGate.TokenConfigKey] = requiredToken;
            }

            var store = new FakeFrameworkArtifactStore();
            store.Resources[ResourceId] = new FrameworkResource(
                ResourceId, "artifact-1", fileName, contentType, "hello"u8.ToArray(), DateTimeOffset.UtcNow);
            builder.Services.AddSingleton<Fravashi.Application.Framework.IFrameworkArtifactStore>(store);

            var app = builder.Build();

            // The real production endpoint, exactly as Program.cs maps it.
            app.MapFrameworkResources();

            await app.StartAsync();
            var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            return new EndpointHost(app, client);
        }

        public async Task<HttpResponseMessage> GetAsync(string userAgent)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/framework/resources/{ResourceId}");
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
}
