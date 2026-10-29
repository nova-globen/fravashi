using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fravashi.Bridge;

/// <summary>Everything the bridge host needs to serve: the shared token and the URLs to bind.</summary>
/// <param name="PickFolder">
/// Host UI callback for the folder picker endpoint (plan 2b-ii): fravashi.exe shows its dialog on the
/// UI thread and returns the chosen path, or null when cancelled. Absent (headless/test hosts)
/// the endpoint answers 404.
/// </param>
/// <param name="CliProjectsRoot">
/// Where the host's Claude CLI keeps its per-directory conversation folders (backlog 43). Null means the
/// CLI's own default (<see cref="BridgeCliConversationService.DefaultProjectsRoot"/>); tests point it at a
/// temp folder.
/// </param>
/// <param name="CliConversationMaxBytes">The largest conversation file the bridge hands over; tests lower it.</param>
/// <param name="Identity">
/// The sign-in hand-over (backlog 94): where the studio publishes its sign-in coordinates and takes a session the
/// launcher obtained. Absent, both endpoints answer 404 and the studio simply signs in by itself.
/// </param>
/// <param name="EventHeartbeat">
/// How long the event stream may stay quiet before the bridge writes a heartbeat comment (backlog 257). Null means
/// <see cref="BridgeApplication.DefaultEventHeartbeat"/>; tests shorten it.
/// </param>
/// <param name="GitExecutable">
/// The program the git endpoints start: the literal <c>git</c> on the host's PATH. Tests name one that does not exist
/// to stand in for a machine with no git (backlog 266). Host code only; never read from a request.
/// </param>
/// <param name="ShowToast">
/// Host callback for the waiting-notification endpoint (backlog 304, ADR 0031 decision 12): fravashi.exe raises the
/// message as a tray balloon. Absent (headless/test hosts), the endpoint answers 404, the same shape as
/// <see cref="PickFolder"/>.
/// </param>
public sealed record BridgeServerOptions(
    string Token,
    IReadOnlyList<string> Urls,
    Func<Task<string?>>? PickFolder = null,
    string? CliProjectsRoot = null,
    long CliConversationMaxBytes = BridgeCliConversationService.MaxBytes,
    IdentityHandover? Identity = null,
    TimeSpan? EventHeartbeat = null,
    string GitExecutable = "git",
    Func<string, Task>? ShowToast = null);

/// <summary>
/// Builds the bridge's <see cref="WebApplication"/>: token auth on everything except
/// <c>/bridge/healthz</c>, exception-to-status mapping, and the file/git/search/watch endpoints
/// over a shared <see cref="WorkspaceRegistry"/>. Hosted by fravashi.exe; self-hosted on an ephemeral
/// port by the integration tests. Kestrel binds raw sockets, so no URLACL is needed for the
/// WSL-facing address (spike 2026-08-01).
/// </summary>
public static class BridgeApplication
{
    public const string TokenHeader = "X-Fravashi-Bridge";

    /// <summary>Response header carrying a served file's last-write stamp (round-trip "O" format).</summary>
    public const string ModifiedUtcHeader = "X-Fravashi-Modified-Utc";

    /// <summary>Request header carrying the writer's freshness precondition (round-trip "O" format).</summary>
    public const string ExpectedUtcHeader = "X-Fravashi-Expected-Utc";

    /// <summary>
    /// The longest the event stream stays quiet before a heartbeat comment is written (backlog 257). The studio drops a
    /// stream after three of these pass with nothing, but only once that stream has delivered a heartbeat.
    /// </summary>
    public static readonly TimeSpan DefaultEventHeartbeat = TimeSpan.FromSeconds(15);

    // The heartbeat line: an SSE comment, which every reader of the stream ignores (the studio's skips any line that
    // does not start with "data: ", so an older studio is unaffected).
    private const string HeartbeatLine = ": heartbeat\n\n";

    private static readonly JsonSerializerOptions SseJson = new(JsonSerializerDefaults.Web);

    public static WebApplication Create(BridgeServerOptions options, WorkspaceRegistry registry)
    {
        var builder = WebApplication.CreateBuilder();
        // The WinExe host has no console; the bridge is silent by default (launcher log lines
        // cover lifecycle) and the studio surfaces its own errors from response statuses.
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls([.. options.Urls]);

        var files = new BridgeFileService(registry);
        var git = new BridgeGitService(registry, options.GitExecutable);
        var worktrees = new BridgeWorktreeService(registry, options.GitExecutable);
        var search = new BridgeSearchService(registry);
        var watch = new BridgeWatchHub(registry);
        var cliConversations = new BridgeCliConversationService(
            registry, options.CliProjectsRoot, options.CliConversationMaxBytes);
        builder.Services.AddSingleton(watch);

        var app = builder.Build();
        app.Lifetime.ApplicationStopping.Register(watch.Dispose);

        // Exception mapping FIRST so every endpoint (and the auth check) shares one honest shape.
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (KeyNotFoundException)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
            }
            catch (WorkspaceUnreachableException)
            {
                // Deliberately NOT a 404: the host cannot see the folder right now (an unmapped
                // drive, an unplugged disk, an offline share) and has no idea whether it is gone.
                // The studio must offer a retry on this, never "the folder no longer exists".
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
            }
            catch (UnauthorizedAccessException)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
            }
            catch (ArgumentException)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
            }
        });

        // Token gate: everything except the liveness probe requires the per-install token.
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/bridge/healthz"))
            {
                await next(context);
                return;
            }

            var presented = context.Request.Headers[TokenHeader].ToString();
            if (!BridgeAccessToken.Verify(presented, options.Token))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next(context);
        });

        app.MapGet("/bridge/healthz", () => Results.Text("ok"));

        app.MapGet("/bridge/workspaces", () => Results.Json(registry.List()));

        // Backlog 94: the sign-in hand-over. The studio says which provider it signs in with and whether its own
        // session is still usable (so the next fravashi start knows whether to sign in first), and takes, exactly
        // once, a session the launcher obtained before the container started. POST for the take because it
        // CHANGES state: a second take answers 204.
        app.MapPost("/bridge/identity/coordinates", (SignInCoordinates body) =>
        {
            if (options.Identity is null)
            {
                return Results.NotFound();
            }

            return options.Identity.Publish(body with { UpdatedUtc = DateTimeOffset.UtcNow })
                ? Results.NoContent()
                : Results.BadRequest();
        });

        app.MapPost("/bridge/identity/take", () =>
            options.Identity is null ? Results.NotFound()
            : options.Identity.Take() is { } handed ? Results.Json(handed)
            : Results.NoContent());

        app.MapPost("/bridge/workspaces", (RegisterWorkspaceRequest body) =>
            Results.Json(registry.Register(body.Path)));

        app.MapPost("/bridge/pick-folder", async () =>
        {
            if (options.PickFolder is null)
            {
                return Results.NotFound();
            }

            var picked = await options.PickFolder();
            return picked is null ? Results.NoContent() : Results.Json(registry.Register(picked));
        });

        // Plan 2c: host-side clone. The container sends ONLY the URL; the target parent folder
        // always comes from the host's own picker dialog, so a compromised studio can never
        // write outside a folder the user just chose.
        app.MapPost("/bridge/clone", async (CloneRequest body, CancellationToken ct) =>
        {
            if (!BridgeGitService.IsCloneableUrl(body.Url))
            {
                return Results.BadRequest();
            }

            if (options.PickFolder is null)
            {
                return Results.NotFound();
            }

            var picked = await options.PickFolder();
            if (picked is null)
            {
                return Results.NoContent();
            }

            try
            {
                var cloned = await git.CloneAsync(body.Url, picked, ct);
                return Results.Json(registry.Register(cloned));
            }
            catch (IOException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
            }
            catch (InvalidOperationException ex)
            {
                // git's own failure (bad host, auth refused, ...): a gateway error, message intact.
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Backlog 304 (ADR 0031 decision 12): the studio asks fravashi.exe to raise a Windows toast. No workspace is
        // involved (the waiting list is cross-workspace), so this sits beside pick-folder rather than under /ws/{id}.
        app.MapPost("/bridge/notify", async (NotifyRequest body) =>
        {
            if (string.IsNullOrWhiteSpace(body.Message))
            {
                return Results.BadRequest();
            }

            if (options.ShowToast is null)
            {
                return Results.NotFound();
            }

            await options.ShowToast(body.Message);
            return Results.NoContent();
        });

        app.MapGet("/bridge/ws/{id}/tree", (string id, string? path) =>
            Results.Json(files.ListDirectory(id, path)));

        app.MapGet("/bridge/ws/{id}/file", (string id, string path, HttpContext context) =>
        {
            var entry = files.Stat(id, path);
            context.Response.Headers[ModifiedUtcHeader] = entry.ModifiedUtc.UtcDateTime.ToString("O");
            return Results.Stream(files.OpenRead(id, path), "application/octet-stream");
        });

        app.MapPut("/bridge/ws/{id}/file", async (string id, string path, HttpContext context, CancellationToken ct) =>
        {
            DateTimeOffset? expected = null;
            if (context.Request.Headers.TryGetValue(ExpectedUtcHeader, out var header)
                && DateTimeOffset.TryParse(
                    header.ToString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            {
                expected = parsed;
            }

            try
            {
                var stamp = await files.WriteAsync(id, path, context.Request.Body, expected, ct);
                return Results.Json(new { modifiedUtc = stamp });
            }
            catch (BridgeWriteConflictException conflict)
            {
                // 412 = the file moved under the writer; 410 = it vanished. The studio maps these
                // onto its conflict banner flow (ADR 0016).
                return Results.StatusCode(conflict.Missing
                    ? StatusCodes.Status410Gone
                    : StatusCodes.Status412PreconditionFailed);
            }
        });

        app.MapPost("/bridge/ws/{id}/dir", (string id, string path) =>
        {
            files.CreateDirectory(id, path);
            return Results.NoContent();
        });

        app.MapDelete("/bridge/ws/{id}/entry", (string id, string path) =>
        {
            files.Delete(id, path);
            return Results.NoContent();
        });

        app.MapPost("/bridge/ws/{id}/rename", (string id, RenameRequest body) =>
        {
            files.Rename(id, body.From, body.To);
            return Results.NoContent();
        });

        app.MapPost("/bridge/ws/{id}/git", async (string id, GitRequest body, CancellationToken ct) =>
            Results.Json(await git.RunAsync(id, body.Args, ct)));

        // Backlog 306 (ADR 0031 decision 15): a git worktree per concurrent session, a host-driven
        // operation never exposed to an agent. The caller names only a work item number and a base
        // branch; the target folder and the branch name are both derived inside BridgeWorktreeService.
        app.MapPost("/bridge/ws/{id}/worktrees", async (string id, CreateWorktreeRequest body, CancellationToken ct) =>
        {
            try
            {
                return Results.Json(await worktrees.CreateAsync(id, body.Item, body.BaseBranch, ct));
            }
            catch (WorktreeTargetConflictException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapDelete("/bridge/ws/{id}/worktrees/{item:int}", async (string id, int item, CancellationToken ct) =>
        {
            try
            {
                var result = await worktrees.PruneAsync(id, item, ct);
                return result.Outcome switch
                {
                    BridgeWorktreePruneOutcome.Removed => Results.NoContent(),
                    BridgeWorktreePruneOutcome.NotFound => Results.NotFound(),
                    _ => Results.Json(new { error = result.Detail }, statusCode: StatusCodes.Status409Conflict),
                };
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapPost("/bridge/ws/{id}/search", async (string id, BridgeSearchRequest body, CancellationToken ct) =>
            Results.Json(await search.SearchAsync(id, body, ct)));

        // Backlog 43: conversations the user ran with the CLI OUTSIDE the studio, read-only. The folder
        // is derived from the registered workspace's host root; the caller only ever names a file.
        app.MapGet("/bridge/ws/{id}/cli-conversations", (string id) =>
            Results.Json(cliConversations.List(id)));

        app.MapGet("/bridge/ws/{id}/cli-conversations/{name}", (string id, string name, HttpContext context) =>
        {
            try
            {
                var (bytes, modifiedUtc) = cliConversations.Read(id, name);
                context.Response.Headers[ModifiedUtcHeader] = modifiedUtc.UtcDateTime.ToString("O");
                return Results.Bytes(bytes, "application/x-ndjson");
            }
            catch (BridgeCliConversationTooLargeException)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }
        });

        var heartbeat = options.EventHeartbeat ?? DefaultEventHeartbeat;
        app.MapGet("/bridge/ws/{id}/events", async (string id, HttpContext context, BridgeWatchHub hub) =>
        {
            var aborted = context.RequestAborted;
            var reader = hub.Subscribe(id, aborted);
            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            try
            {
                // A heartbeat at once, with the headers (backlog 257): it tells the client this bridge has them, which is
                // what lets the client drop a silent stream without dropping an older bridge's quiet one. Then one after
                // every interval with nothing written; an event resets it, since any line proves the stream alive.
                await context.Response.WriteAsync(HeartbeatLine, aborted);
                await context.Response.Body.FlushAsync(aborted);
                while (true)
                {
                    bool more;
                    using (var quiet = CancellationTokenSource.CreateLinkedTokenSource(aborted))
                    {
                        quiet.CancelAfter(heartbeat);
                        try
                        {
                            more = await reader.WaitToReadAsync(quiet.Token);
                        }
                        catch (OperationCanceledException) when (!aborted.IsCancellationRequested)
                        {
                            await context.Response.WriteAsync(HeartbeatLine, aborted);
                            await context.Response.Body.FlushAsync(aborted);
                            continue;
                        }
                    }

                    if (!more)
                    {
                        break;
                    }

                    while (reader.TryRead(out var fileEvent))
                    {
                        var payload = JsonSerializer.Serialize(fileEvent, SseJson);
                        await context.Response.WriteAsync($"data: {payload}\n\n", aborted);
                    }

                    await context.Response.Body.FlushAsync(aborted);
                }
            }
            catch (OperationCanceledException)
            {
                // Subscriber went away: the normal end of an SSE stream.
            }
        });

        return app;
    }

    private sealed record RenameRequest(string From, string To);

    private sealed record GitRequest(IReadOnlyList<string> Args);

    private sealed record CreateWorktreeRequest(int Item, string BaseBranch);

    private sealed record RegisterWorkspaceRequest(string Path);

    private sealed record CloneRequest(string Url);

    private sealed record NotifyRequest(string Message);
}
