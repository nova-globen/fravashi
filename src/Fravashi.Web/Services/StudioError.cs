using Microsoft.AspNetCore.Diagnostics;

namespace Fravashi.Web.Services;

/// <summary>
/// Backlog 281: UseExceptionHandler re-executes EVERY unhandled exception to <see cref="PagePath"/>, whatever path
/// threw, rewriting the request path before it does. A page request gets the studio's own Error page inside the
/// shell; an API (or other machine) request keeps a plain 500 with no body, told apart by the ORIGINAL path on
/// <see cref="IExceptionHandlerPathFeature"/>, since <c>context.Request.Path</c> itself is already <see cref="PagePath"/>
/// by the time the guard below runs.
/// <para>
/// Program.cs wires this where UseExceptionHandler used to be wired alone, ahead of UseStudioNotFound and every
/// gate, so a re-executed page request walks each gate again in their order, same as backlog 280's not-found
/// re-execution. This is NOT a no-op: every redirecting gate (SignInGate, IntegrityLock, CriticalUpdateLock,
/// VpnGate and the inline onboarding gate) exempts its own lock/gate page from itself so that page can render
/// while the gate is closed, and a thrown request on one of those pages re-executes to <see cref="PagePath"/>
/// just the same as any other page. Each of those five now also exempts <see cref="PagePath"/>, or the second
/// pass would redirect the Error page straight back to the page that threw (a 302 instead of a 500, and a
/// redirect loop when the throw is deterministic). The window-only gate is deliberately NOT in that list: only
/// the studio window may ever see the Error page, same as every other page.
/// </para>
/// <para>
/// That exemption is granted to the PAGE, not to a request for it: the five gates exempt <see cref="PagePath"/> by
/// its route (<see cref="IsPagePath"/>, which every one of them calls rather than rolling their own check, so they
/// can never disagree with the guard below about what counts as the Error page), so a DIRECT GET of <c>/Error</c>,
/// with no exception and so no <see cref="IExceptionHandlerPathFeature"/> on the request, would also sail past
/// every closed gate and start a live interactive circuit under MinimalWindowLayout, including its title bar's
/// workspace and branch switchers, while a gate is closed for every reason that gate exists. The guard below
/// refuses exactly that: a request for <see cref="PagePath"/> (or anything under it, same as the gates' own
/// StartsWith) that carries no exception feature is sent home, where every gate runs again from the top.
/// </para>
/// </summary>
public static class StudioError
{
    /// <summary>The route of the studio's Error page (<c>Components/Pages/ErrorPage.razor</c>).</summary>
    public const string PagePath = "/Error";

    /// <summary>
    /// True for this path or anything under it (a trailing slash included), ordinal and case-insensitive. The
    /// one place every gate exemption and the direct-request guard below both ask, so a mismatch between them
    /// (the guard refusing an exact path while a gate exempts a prefix, say) cannot reopen the hole either was
    /// meant to close.
    /// </summary>
    public static bool IsPagePath(string path) => path.StartsWith(PagePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the request named by this ORIGINAL path, having thrown, should see the Error page rather than a plain
    /// 500. Backlog 283: a static asset is what <see cref="StaticAssetRoutes"/> says, the answer the gates ask, so a
    /// thrown dotted page (<c>/code/README.md</c>) gets the page like any other page.
    /// </summary>
    public static bool ShowsPage(string originalPath, StaticAssetRoutes assets) =>
        !originalPath.StartsWith("/api", StringComparison.OrdinalIgnoreCase) &&
        !originalPath.StartsWith("/healthz", StringComparison.OrdinalIgnoreCase) &&
        !originalPath.StartsWith("/_", StringComparison.Ordinal) &&
        !assets.Serves(originalPath);

    /// <summary>
    /// Wires UseExceptionHandler to PagePath, then the direct-request guard (<see cref="UseStudioErrorGuard"/>).
    /// Program.cs calls this outside Development only, where the framework's own developer exception page would
    /// otherwise answer first. In Development, Program.cs calls <see cref="UseStudioErrorGuard"/> alone: the five
    /// gate exemptions are UNCONDITIONAL, so without the guard running there too, a direct GET of /Error on a dev
    /// host would pass every closed gate and reach the live page (caught in review).
    /// </summary>
    public static IApplicationBuilder UseStudioError(this IApplicationBuilder app)
    {
        app.UseExceptionHandler(PagePath, createScopeForErrors: true);
        return app.UseStudioErrorGuard();
    }

    /// <summary>
    /// The direct-request guard alone, with no dependency on UseExceptionHandler having run: keeps a machine
    /// request's plain 500 rather than the page's HTML when it finds <see cref="IExceptionHandlerPathFeature"/>;
    /// refuses a direct (non-re-executed) request for the Error page by sending it home otherwise, so the gate
    /// exemption above only ever reaches the page by way of a real thrown exception.
    /// </summary>
    public static IApplicationBuilder UseStudioErrorGuard(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var originalPath = context.Features.Get<IExceptionHandlerPathFeature>()?.Path;
            if (originalPath is not null)
            {
                if (!ShowsPage(originalPath, context.RequestServices.GetRequiredService<StaticAssetRoutes>()))
                {
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    return;
                }
            }
            else if (IsPagePath(context.Request.Path.Value ?? "/"))
            {
                context.Response.Redirect("/");
                return;
            }

            await next();
        });
}
