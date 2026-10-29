using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fravashi.Application.Verification;
using Fravashi.Application.WorkItems;
using Microsoft.Extensions.Configuration;

namespace Fravashi.Infrastructure.AppRuns;

/// <summary>
/// <see cref="IApplicationRuns"/> over the host's app runner (backlog 87, ADR 0027), which fravashi.exe hosts beside the
/// bridge and hands this container as <c>FRAVASHI_APPRUNNER_URL</c> and <c>FRAVASHI_APPRUNNER_TOKEN</c>, a token of its
/// own and never the bridge's. It asks for exactly the ADR's operations: build the workspace from its spec, start
/// it, wait until it accepts connections, and remove it. What it gets back is the relay's address on the run's edge
/// network, which the app runner has joined this container to, so a scenario step reaches the application by name.
/// </summary>
public sealed class AppRunnerClient(HttpClient http, IConfiguration configuration) : IApplicationRuns
{
    public const string UrlKey = "FRAVASHI_APPRUNNER_URL";
    public const string TokenKey = "FRAVASHI_APPRUNNER_TOKEN";
    private const string TokenHeader = "X-Fravashi-AppRunner";

    /// <summary>How long a build may take, the app runner's own ceiling plus a margin for the answer to come back.</summary>
    public static readonly TimeSpan BuildWait = TimeSpan.FromMinutes(16);

    /// <summary>How long an application has to start accepting connections.</summary>
    public static readonly TimeSpan ReadyWait = TimeSpan.FromMinutes(2);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool Available =>
        !string.IsNullOrWhiteSpace(configuration[UrlKey]) && !string.IsNullOrWhiteSpace(configuration[TokenKey]);

    public async Task<ApplicationRun> StartAsync(string workspaceId, CancellationToken cancellationToken)
    {
        if (!Available)
        {
            return ApplicationRun.Failed("no app runner is configured for this studio");
        }

        try
        {
            using var buildBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            buildBudget.CancelAfter(BuildWait);
            using var built = await SendAsync(HttpMethod.Post, "apprunner/builds", new { workspaceId }, buildBudget.Token).ConfigureAwait(false);
            var buildBody = await ReadAsync(built, cancellationToken).ConfigureAwait(false);
            if (built.StatusCode == HttpStatusCode.UnprocessableEntity && buildBody.TryGetProperty("refusals", out var refusals))
            {
                return ApplicationRun.Failed("the project's container spec was refused: " + string.Join("; ",
                    refusals.EnumerateArray().Select(r => $"{r.GetProperty("field").GetString()}: {r.GetProperty("reason").GetString()}")));
            }

            if (!built.IsSuccessStatusCode || !buildBody.TryGetProperty("buildId", out var buildId))
            {
                return ApplicationRun.Failed(ErrorOf(buildBody, built.StatusCode, "the build failed"));
            }

            // Backlog 226: every run the verifier starts is a verification run, so the app runner mints a window token for
            // it and hands it back here. This client never reads the live studio's token; the run's is the only one.
            using var started = await SendAsync(
                HttpMethod.Post, "apprunner/runs", new { buildId = buildId.GetString(), verification = true }, cancellationToken).ConfigureAwait(false);
            var startBody = await ReadAsync(started, cancellationToken).ConfigureAwait(false);
            if (!started.IsSuccessStatusCode || !startBody.TryGetProperty("runId", out var runIdElement))
            {
                return ApplicationRun.Failed(ErrorOf(startBody, started.StatusCode, "the application could not be started"));
            }

            var engineUrl = startBody.GetProperty("engineUrl").GetString();
            var windowToken = startBody.TryGetProperty("windowToken", out var minted) && minted.ValueKind == JsonValueKind.String
                ? minted.GetString()
                : null;
            var run = new ApplicationRun(runIdElement.GetString(), engineUrl, null) { Marker = WindowMarker.For(engineUrl, windowToken) };
            var why = await WaitUntilReadyAsync(run.RunId!, cancellationToken).ConfigureAwait(false);
            if (why is null)
            {
                return run;
            }

            await RemoveAsync(run, CancellationToken.None).ConfigureAwait(false);
            return ApplicationRun.Failed(why);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            return ApplicationRun.Failed($"the app runner could not be reached ({ex.GetType().Name})");
        }
    }

    /// <summary>Null once the run accepts connections; otherwise why it never will, with the tail of its output.</summary>
    private async Task<string?> WaitUntilReadyAsync(string runId, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + ReadyWait;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await SendAsync(HttpMethod.Get, $"apprunner/runs/{runId}/ready", null, cancellationToken).ConfigureAwait(false);
            var body = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                if (body.GetProperty("ready").GetBoolean())
                {
                    return null;
                }

                if (!body.GetProperty("appRunning").GetBoolean())
                {
                    var exit = body.TryGetProperty("exitCode", out var code) && code.ValueKind == JsonValueKind.Number ? code.GetInt32().ToString() : "unknown";
                    return $"the application exited (code {exit}) before accepting connections. Its output ended: {await TailAsync(runId, cancellationToken).ConfigureAwait(false)}";
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        return $"the application did not accept connections within {ReadyWait.TotalMinutes:0} minutes. Its output ended: {await TailAsync(runId, cancellationToken).ConfigureAwait(false)}";
    }

    private async Task<string> TailAsync(string runId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Get, $"apprunner/runs/{runId}/logs", null, cancellationToken).ConfigureAwait(false);
            var body = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
            var text = body.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            text = text.Trim();
            return text.Length <= 400 ? text : "..." + text[^400..];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return "(its output could not be read)";
        }
    }

    public async Task<string?> LogsAsync(ApplicationRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.RunId is null || !Available)
        {
            return null;
        }

        try
        {
            using var response = await SendAsync(HttpMethod.Get, $"apprunner/runs/{run.RunId}/logs", null, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
            var text = body.TryGetProperty("text", out var t) ? t.GetString() : null;
            return body.TryGetProperty("truncated", out var cut) && cut.ValueKind == JsonValueKind.True
                ? text + "\n(the app runner cut the output at its ceiling)"
                : text;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    public async Task RemoveAsync(ApplicationRun run, CancellationToken cancellationToken)
    {
        if (run.RunId is null || !Available)
        {
            return;
        }

        try
        {
            using var _ = await SendAsync(HttpMethod.Delete, $"apprunner/runs/{run.RunId}", null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // The app runner's own sweep removes it at its deadline; a run is never left without one.
        }
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, new Uri(new Uri(configuration[UrlKey]!.TrimEnd('/') + "/"), path));
        request.Headers.Add(TokenHeader, configuration[TokenKey]);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        return http.SendAsync(request, cancellationToken);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }

        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static string ErrorOf(JsonElement body, HttpStatusCode status, string fallback) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty("error", out var error) && error.GetString() is { Length: > 0 } text
            ? text
            : $"{fallback} ({(int)status})";
}
