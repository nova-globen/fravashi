using Fravashi.Application.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Fravashi.Web.Services;

/// <summary>
/// GET /api/framework/resources/{resourceId} (uploaded skill icons / resource files, ADR 0005). It sits under
/// /api, which <see cref="WindowOnlyGate.IsExempt"/> treats as a machine path, but it serves an uploaded
/// resource's bytes rather than acting as one of the agent/session machine endpoints, so it checks
/// <see cref="WindowOnlyGate"/> itself (backlog 284). SkillIconDisplay's img is fetched by the main studio
/// webview, the same path every other gated asset already takes, so its request carries the window user agent
/// and token. SkillView's download link dropped target="_blank" for a download attribute so it is expected to
/// be fetched the same way rather than opening a same-origin popup with its own default user agent; that
/// expectation is unverified in a window in this build, and a host session owes the check after a release.
/// </summary>
public static class FrameworkResourceEndpoint
{
    public static void MapFrameworkResources(this WebApplication app) =>
        app.MapGet("/api/framework/resources/{resourceId}", Handle);

    public static IResult Handle(string resourceId, HttpContext context, IConfiguration configuration, IFrameworkArtifactStore store)
    {
        if (!WindowOnlyGate.IsAllowed(
            context.Request.Headers.UserAgent.ToString(),
            configuration[WindowOnlyGate.TokenConfigKey]))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var resource = store.GetResource(resourceId);
        if (resource is null)
        {
            return Results.NotFound();
        }

        // Backlog 287: the bytes are an upload served in the studio's own origin. The browser is told not to guess a
        // type, the response is always a download rather than a page (Results.File's file name makes it an
        // attachment, and the header is set here too so that never rests on the name being non-empty), and a type
        // that renders as a document able to run script gets a policy that runs nothing. SVG keeps its type because
        // an uploaded icon is drawn through an img element, where an SVG runs no script; html and xml become
        // octet-stream, since nothing in the studio displays them.
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.ContentDisposition = "attachment";
        var contentType = resource.ContentType is { Length: > 0 } stored ? stored : "application/octet-stream";
        if (IsActive(contentType))
        {
            context.Response.Headers[StudioContentSecurityPolicy.HeaderName] = InertPolicy;
            if (!IsSvg(contentType))
            {
                contentType = "application/octet-stream";
            }
        }

        return Results.File(resource.Content, contentType, resource.FileName);
    }

    /// <summary>The policy an active upload is served under: a sandboxed document that loads and runs nothing.</summary>
    public const string InertPolicy = "sandbox; default-src 'none'; style-src 'unsafe-inline'";

    /// <summary>
    /// True for a type a browser would render as a document able to run script: html, xhtml, svg and any xml. Only the
    /// media type is read, so a parameter (<c>; charset=utf-8</c>) or a case variant changes nothing.
    /// </summary>
    public static bool IsActive(string contentType)
    {
        var mediaType = MediaType(contentType);
        return mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("text/xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSvg(string contentType) =>
        MediaType(contentType).Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase);

    private static string MediaType(string contentType) => contentType.Split(';', 2)[0].Trim();
}
