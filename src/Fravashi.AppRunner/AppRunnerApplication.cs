using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Fravashi.AppRunner;

/// <summary>
/// The app runner's own per-install token (ADR 0027, "what holds its token"): deliberately NOT the bridge's, so a
/// compromise of one is not a compromise of both. Minted by fravashi.exe beside <c>bridge.token</c>, handed to the
/// studio container the same way, required on every request, compared in constant time.
/// </summary>
public static class AppRunnerToken
{
    public const string FileName = "apprunner.token";
    public const string Header = "X-Fravashi-AppRunner";
    public const string UrlEnvironmentVariable = "FRAVASHI_APPRUNNER_URL";
    public const string TokenEnvironmentVariable = "FRAVASHI_APPRUNNER_TOKEN";

    public static string GetOrCreate(string configDirectory)
    {
        var path = Path.Combine(configDirectory, FileName);
        try
        {
            if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } existing)
            {
                return existing;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Mint a fresh one below.
        }

        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        try
        {
            Directory.CreateDirectory(configDirectory);
            File.WriteAllText(path, token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unpersistable: still usable for this process.
        }

        return token;
    }

    public static bool Verify(string? presented, string expected) =>
        !string.IsNullOrEmpty(presented) && !string.IsNullOrEmpty(expected)
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected));
}

/// <summary>Request bodies. Nothing in them names an image, a mount, a flag or a container.</summary>
public sealed record BuildRequest(string? WorkspaceId, string? Spec);

public sealed record LimitsRequest(double? Cpus, int? MemoryMb, int? Processes, int? WritableMb, int? RunMinutes);

/// <param name="Verification">Backlog 226: start it as a verification run, with a window token minted for it.</param>
public sealed record StartRequest(string? BuildId, int? EntryPort, LimitsRequest? Limits, bool? Verification = null);

/// <summary>
/// The app runner's HTTP surface: exactly the ADR's seven operations and nothing else, behind its own token, on the
/// addresses fravashi.exe binds (loopback, and the WSL-facing one the studio container reaches). Refusals are answered
/// with the fields they name, never swallowed.
/// </summary>
public static class AppRunnerApplication
{
    public static WebApplication Create(string token, IReadOnlyList<string> urls, AppRunner runner)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls([.. urls]);
        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            if (!AppRunnerToken.Verify(context.Request.Headers[AppRunnerToken.Header].ToString(), token))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next(context);
        });

        app.MapPost("/apprunner/builds", async (BuildRequest body, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.WorkspaceId))
            {
                return Results.BadRequest(new { error = "workspaceId is required" });
            }

            var built = await runner.BuildAsync(body.WorkspaceId, body.Spec, ct);
            if (built.Refusals.Count > 0)
            {
                return Results.Json(
                    new { refusals = built.Refusals.Select(r => new { field = r.Field, reason = r.Reason }) },
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            return built.Built
                ? Results.Json(new { buildId = built.BuildId, entryPort = built.EntryPort, notRead = built.NotRead, skippedLinks = built.SkippedLinks })
                : Results.Json(new { error = built.Failure, output = built.Output }, statusCode: StatusCodes.Status502BadGateway);
        });

        app.MapPost("/apprunner/runs", async (StartRequest body, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.BuildId))
            {
                return Results.BadRequest(new { error = "buildId is required" });
            }

            var started = await runner.StartAsync(body.BuildId, body.EntryPort, body.Limits, ct, body.Verification == true);
            // The minted token goes back to the caller that started the run, in this answer only (backlog 226).
            return started.Started
                ? Results.Json(new
                {
                    runId = started.RunId, loopbackUrl = started.LoopbackUrl, engineUrl = started.EngineUrl, deadline = started.Deadline,
                    windowToken = started.WindowToken?.Value,
                })
                : Results.Json(new { error = started.Refusal, output = started.Output }, statusCode: StatusCodes.Status422UnprocessableEntity);
        });

        app.MapGet("/apprunner/runs/{id}/ready", async (string id, CancellationToken ct) =>
        {
            var ready = await runner.ReadyAsync(id, ct);
            return ready.Known
                ? Results.Json(new { ready = ready.Ready, appRunning = ready.AppRunning, exitCode = ready.ExitCode, seconds = (int)ready.Elapsed.TotalSeconds, stopped = ready.Stopped })
                : Results.NotFound();
        });

        app.MapGet("/apprunner/runs/{id}/logs", async (string id, int? cursor, CancellationToken ct) =>
        {
            var logs = await runner.LogsAsync(id, cursor ?? 0, ct);
            return logs.Known ? Results.Json(new { text = logs.Text, next = logs.Next, truncated = logs.Truncated }) : Results.NotFound();
        });

        app.MapPost("/apprunner/runs/{id}/stop", async (string id, CancellationToken ct) =>
        {
            await runner.StopAsync(id, ct);
            return Results.NoContent();
        });

        app.MapDelete("/apprunner/runs/{id}", async (string id, CancellationToken ct) =>
        {
            var stuck = await runner.RemoveAsync(id, ct);
            return stuck.Count == 0
                ? Results.NoContent()
                : Results.Json(new { error = "not everything could be removed", stuck }, statusCode: StatusCodes.Status500InternalServerError);
        });

        app.MapGet("/apprunner/runs", async (CancellationToken ct) => Results.Json(await runner.ListAsync(ct)));

        return app;
    }
}
