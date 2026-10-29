using Fravashi.Infrastructure.Bridge;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;

namespace Fravashi.Web;

/// <summary>
/// Builds the scope's <see cref="WorkspaceSelection"/> (plan 2b-ii): the window guarantees every
/// top-level navigation carries ?ws=, and this capture must find it in BOTH scopes a page runs in.
/// The prerender scope sees it on the HTTP request; the interactive circuit's scope belongs to the
/// /_blazor WebSocket request (no page query), so there the circuit's NavigationManager, which is
/// initialized from the browser URL, is the only truthful source.
/// </summary>
internal static class WorkspaceSelectionCapture
{
    public static WorkspaceSelection Create(IServiceProvider services)
    {
        var selection = new WorkspaceSelection();
        selection.Capture(FromHttpRequest(services) ?? FromNavigation(services));
        return selection;
    }

    private static string? FromHttpRequest(IServiceProvider services)
    {
        var context = services.GetService<IHttpContextAccessor>()?.HttpContext;
        var ws = context?.Request.Query["ws"].ToString();
        return string.IsNullOrWhiteSpace(ws) ? null : ws;
    }

    private static string? FromNavigation(IServiceProvider services)
    {
        try
        {
            var navigation = services.GetService<NavigationManager>();
            if (navigation is null)
            {
                return null;
            }

            var query = new Uri(navigation.Uri).Query;
            if (string.IsNullOrEmpty(query))
            {
                return null;
            }

            var ws = QueryHelpers.ParseQuery(query).TryGetValue("ws", out var values)
                ? values.ToString()
                : null;
            return string.IsNullOrWhiteSpace(ws) ? null : ws;
        }
        catch (InvalidOperationException)
        {
            // NavigationManager exists but is not initialized (a scope resolved the selection
            // before any component ran); the settings-row fallback decides, exactly as before.
            return null;
        }
    }
}
