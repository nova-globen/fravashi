using Fravashi.Infrastructure.Bridge;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// The circuit's workspace binding (plan 2b-ii). The regression here (field defect 2026-08-01):
/// the interactive circuit's DI scope belongs to the /_blazor WebSocket request, whose URL never
/// carries the page's ?ws= query, so a capture that only reads HttpContext silently drops the
/// workspace and the window falls back to the local clone.
/// </summary>
public class WorkspaceSelectionCaptureTests
{
    private sealed class FakeAccessor(HttpContext? context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }

    private sealed class FakeNavigationManager : NavigationManager
    {
        public FakeNavigationManager(string uri) => Initialize("http://localhost:9090/", uri);
    }

    private sealed class UninitializedNavigationManager : NavigationManager;

    private static HttpContext RequestFor(string queryString)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(queryString);
        return context;
    }

    private static ServiceProvider Provider(IHttpContextAccessor? accessor, NavigationManager? navigation)
    {
        var services = new ServiceCollection();
        if (accessor is not null)
        {
            services.AddSingleton(accessor);
        }

        if (navigation is not null)
        {
            services.AddSingleton(navigation);
        }

        return services.BuildServiceProvider();
    }

    [Fact]
    public void Captures_the_ws_query_from_the_http_request()
    {
        using var provider = Provider(new FakeAccessor(RequestFor("?ws=abc123")), navigation: null);

        var selection = WorkspaceSelectionCapture.Create(provider);

        Assert.Equal("abc123", selection.WorkspaceId);
    }

    [Fact]
    public void The_interactive_circuit_falls_back_to_the_navigation_uri()
    {
        // The circuit scope's HttpContext is the WebSocket upgrade request: it has a query, but
        // never the page's ws. The circuit's NavigationManager carries the real browser URL.
        using var provider = Provider(
            new FakeAccessor(RequestFor("?id=Cra18eVsr_E3uZIEhVVMSg")),
            new FakeNavigationManager("http://localhost:9090/code?ws=64fb661c481dcd06"));

        var selection = WorkspaceSelectionCapture.Create(provider);

        Assert.Equal("64fb661c481dcd06", selection.WorkspaceId);
    }

    [Fact]
    public void No_ws_anywhere_captures_null()
    {
        using var provider = Provider(
            new FakeAccessor(RequestFor("")),
            new FakeNavigationManager("http://localhost:9090/welcome"));

        var selection = WorkspaceSelectionCapture.Create(provider);

        Assert.True(selection.Captured);
        Assert.Null(selection.WorkspaceId);
    }

    [Fact]
    public void An_uninitialized_navigation_manager_is_tolerated()
    {
        using var provider = Provider(new FakeAccessor(context: null), new UninitializedNavigationManager());

        var selection = WorkspaceSelectionCapture.Create(provider);

        Assert.Null(selection.WorkspaceId);
    }
}
