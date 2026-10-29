using Fravashi.Application.Prototypes;
using Microsoft.AspNetCore.Components.Server;

namespace Fravashi.Web.Services;

/// <summary>
/// Backlog 287: the studio's Content-Security-Policy, defence in depth after #286. Markup that reaches a page with a
/// <c>javascript:</c> URL or an inline event handler ran with the window's own session (#286 measured a wiki link doing
/// it); with <c>script-src</c> limited to the studio's own files, neither runs wherever it came from. So the studio
/// has no inline script and no inline handler of its own (App.razor's bootstrap is <c>wwwroot/bootstrap.js</c>, the
/// title bar's buttons are wired in <c>windowShell.js</c>), and <c>InlineHandlerGuardTests</c> keeps it that way.
/// <para>
/// Every directive that is wider than <c>'self'</c> says why beside it. A violation found while driving the installed
/// window is fixed, or the directive is widened here with its reason.
/// </para>
/// </summary>
public static class StudioContentSecurityPolicy
{
    public const string HeaderName = "Content-Security-Policy";

    /// <summary>The policy, one directive per entry.</summary>
    public static IReadOnlyList<string> Directives { get; } =
    [
        "default-src 'self'",
        // The prototype viewer's frame is a srcdoc document, and a srcdoc document INHERITS this policy on top of its
        // own, so the one script it runs (PrototypeRenderer.Script) has to be admitted here too. By its hash: that
        // exact text and nothing else, and a hash never admits an inline handler or a javascript: URL. No
        // 'unsafe-inline' and no 'unsafe-eval': Blazor Server needs neither, and Monaco's worker loader tries eval
        // and falls back to importScripts when it is refused.
        $"script-src 'self' '{PrototypeRenderer.ScriptHash}'",
        // 'unsafe-inline' for styles: Monaco, FluentUI and the components set inline styles and style elements, and
        // a style cannot run script, which is what this policy is for.
        "style-src 'self' 'unsafe-inline'",
        // data: and blob: for the images the studio builds itself (pasted images, browser pane screenshots, design
        // check evidence, the image preview); https: because rendered markdown and mirrored wiki pages show images
        // by their own address. An image runs no script.
        "img-src 'self' data: blob: https:",
        // data: for fonts a vendored stylesheet inlines.
        "font-src 'self' data:",
        // blob: because Monaco starts each worker from a blob URL that imports its worker script from 'self'.
        "worker-src 'self' blob:",
        // 'self' covers the circuit's WebSocket to the same host.
        "connect-src 'self'",
        "object-src 'none'",
        "base-uri 'self'",
        "form-action 'self'",
        "frame-ancestors 'none'",
    ];

    /// <summary>The header's value.</summary>
    public static string Value { get; } = string.Join("; ", Directives);

    /// <summary>
    /// For <c>AddInteractiveServerRenderMode</c>: Blazor's own <c>Content-Security-Policy: frame-ancestors 'self'</c>
    /// is switched off, because it is added while the page renders, so it was already there when
    /// <see cref="UseStudioContentSecurityPolicy"/> looked and every page went out with Blazor's one directive instead of
    /// this policy (the host measured it on 7acce2e). Blazor sends it to guard WebSocket compression, which stays ON:
    /// a compressed circuit must not run in a page another origin frames, and this policy's <c>frame-ancestors
    /// 'none'</c> is stricter than Blazor's <c>'self'</c>, so that guard is still sent, by the studio. Null is the
    /// framework's documented setting for an app that sends frame-ancestors itself.
    /// </summary>
    public static void LeaveFrameAncestorsToTheStudio(ServerComponentsEndpointOptions options) =>
        options.ContentSecurityFrameAncestorsPolicy = null;

    /// <summary>
    /// Sends the policy on every response the studio makes (pages, lock pages, the window-only refusal, errors), set
    /// as the response starts so a re-executed error or Not found response, whose headers are cleared on the way,
    /// still carries it. A response that already names its own policy keeps it.
    /// </summary>
    public static IApplicationBuilder UseStudioContentSecurityPolicy(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.TryAdd(HeaderName, Value);
                return Task.CompletedTask;
            });

            await next();
        });
}
