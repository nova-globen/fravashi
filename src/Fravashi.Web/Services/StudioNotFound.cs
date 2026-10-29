using Microsoft.AspNetCore.Diagnostics;

namespace Fravashi.Web.Services;

/// <summary>
/// Backlog 280: a full navigation to a path the studio does not route used to end in endpoint routing's bare 404,
/// which WebView2 shows as Chromium's own error page, and the window's title bar is drawn by the page (ADR 0014), so
/// that page had none. A page request that ends in 404 is now re-executed to <see cref="PagePath"/>, the studio's own
/// Not found page inside the shell, keeping its 404 status. Machine paths keep their plain 404: the API, the Blazor
/// framework/SignalR paths, liveness and static assets, and anything but a GET.
/// <para>
/// Backlog 283: a static asset is what <see cref="StaticAssetRoutes"/> says it is, the same answer the gates ask, not a
/// dot in the path. So a missing dotted page path (<c>/code/gone.md</c>) gets the page like any other, and a missing file
/// the app never served (<c>/nothing.css</c>) does too, still with its 404: its shape is no longer evidence of anything.
/// </para>
/// <para>
/// Program.cs wires this BEFORE every gate, so the re-executed request walks the window-only gate and each other gate
/// again in their order; no gate's exempt list changes. A gate's own answer is never replaced: the window gate's 403
/// carries a body, and the other gates answer with redirects, which status code pages leave alone.
/// </para>
/// </summary>
public static class StudioNotFound
{
    /// <summary>The route of the studio's Not found page (<c>Components/Pages/NotFoundPage.razor</c>).</summary>
    public const string PagePath = "/not-found";

    /// <summary>True when a response with this status, for this request, is answered with the Not found page.</summary>
    public static bool ShowsPage(string method, string path, int statusCode, StaticAssetRoutes assets) =>
        statusCode == StatusCodes.Status404NotFound &&
        HttpMethods.IsGet(method) &&
        !path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) &&
        !path.StartsWith("/healthz", StringComparison.OrdinalIgnoreCase) &&
        !path.StartsWith("/_", StringComparison.Ordinal) &&
        !assets.Serves(path);

    /// <summary>Re-executes an unrouted page request to <see cref="PagePath"/>; every other error status is left as it was.</summary>
    public static IApplicationBuilder UseStudioNotFound(this IApplicationBuilder app)
    {
        app.UseStatusCodePagesWithReExecute(PagePath, createScopeForStatusCodePages: true);
        return app.Use(async (context, next) =>
        {
            await next();

            // Read after the rest of the pipeline has answered: the status code middleware decides on its way out.
            var feature = context.Features.Get<IStatusCodePagesFeature>();
            if (feature is not null &&
                !ShowsPage(
                    context.Request.Method,
                    context.Request.Path.Value ?? "/",
                    context.Response.StatusCode,
                    context.RequestServices.GetRequiredService<StaticAssetRoutes>()))
            {
                feature.Enabled = false;
            }
        });
    }
}
