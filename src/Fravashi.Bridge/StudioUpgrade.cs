using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Fravashi.Bridge;

/// <summary>
/// The studio-upgrade operation's own per-install token (backlog 157): deliberately neither the bridge's nor the app
/// runner's, so holding one is not holding the power to replace the product. Minted by fravashi.exe beside them, handed to
/// the studio container the same way, required on every request, compared in constant time.
/// </summary>
public static class StudioUpgradeToken
{
    public const string FileName = "upgrade.token";
    public const string Header = "X-Fravashi-Upgrade";
    public const string UrlEnvironmentVariable = "FRAVASHI_UPGRADE_URL";
    public const string TokenEnvironmentVariable = "FRAVASHI_UPGRADE_TOKEN";

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

/// <summary>What the update channel offers this install right now.</summary>
/// <param name="Installed">The version installed, as the launcher records it.</param>
/// <param name="Offered">The newer version the channel offers, or null when there is none.</param>
/// <param name="Critical">Whether the channel marks that version required.</param>
/// <param name="Said">Why there is nothing to offer, in words, when <paramref name="Offered"/> is null.</param>
public sealed record StudioUpgradeOffer(string Installed, string? Offered, bool Critical, string? Said);

/// <summary>Where an upgrade stands. Lives as long as fravashi.exe does, which outlives every container it replaces.</summary>
public enum StudioUpgradeState
{
    Idle,
    Running,
    Succeeded,
    Failed,
}

/// <summary>The last upgrade's state, the version it was for, what it said, and when it last moved.</summary>
public sealed record StudioUpgradeStatus(StudioUpgradeState State, string? Version, string? Said, DateTimeOffset? At)
{
    public static readonly StudioUpgradeStatus Idle = new(StudioUpgradeState.Idle, null, null, null);
}

/// <summary>What applying an update came to: whether the container now runs it, and what the launcher said.</summary>
public sealed record StudioUpgradeOutcome(bool Applied, string Said);

/// <summary>
/// Runs the one upgrade at a time fravashi.exe allows (backlog 157). Applying replaces the studio container, so the studio
/// that asks can never wait for the answer: <see cref="TryStart"/> answers at once and the work runs in the background,
/// and whoever comes up afterwards reads <see cref="Status"/>. The checking and the applying are the launcher's own
/// (handed in, so this class stays testable and knows nothing of containers).
/// </summary>
public sealed class StudioUpgradeRunner(
    Func<CancellationToken, Task<StudioUpgradeOffer>> check,
    Func<string, CancellationToken, Task<StudioUpgradeOutcome>> apply,
    TimeProvider? clock = null,
    Action<string>? log = null)
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private StudioUpgradeStatus _status = StudioUpgradeStatus.Idle;
    private Task? _running;

    public StudioUpgradeStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>The finished background work, for tests and for a shutdown that wants to wait for it.</summary>
    public Task Running
    {
        get
        {
            lock (_gate)
            {
                return _running ?? Task.CompletedTask;
            }
        }
    }

    public Task<StudioUpgradeOffer> CheckAsync(CancellationToken ct) => check(ct);

    /// <summary>
    /// Starts upgrading to <paramref name="version"/>, which must be exactly what the channel offers NOW, so a person
    /// who approved one version is never given another. Null when it started; otherwise why not, in words.
    /// </summary>
    public async Task<string?> TryStartAsync(string version, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return "Say which version to upgrade to.";
        }

        lock (_gate)
        {
            if (_status.State == StudioUpgradeState.Running)
            {
                return $"An upgrade to {_status.Version} is already running.";
            }
        }

        var offer = await check(ct).ConfigureAwait(false);
        if (offer.Offered is null)
        {
            return offer.Said ?? $"The update channel offers nothing newer than {offer.Installed}.";
        }

        if (!string.Equals(offer.Offered, version.Trim().TrimStart('v'), StringComparison.Ordinal))
        {
            return $"The update channel offers {offer.Offered}, not {version}.";
        }

        lock (_gate)
        {
            if (_status.State == StudioUpgradeState.Running)
            {
                return $"An upgrade to {_status.Version} is already running.";
            }

            _status = new StudioUpgradeStatus(StudioUpgradeState.Running, offer.Offered, $"Upgrading from {offer.Installed}.", _clock.GetUtcNow());
            // Not the caller's token: the request that started this ends long before the work does, and the
            // container it came from is the thing being replaced.
            _running = Task.Run(() => RunAsync(offer.Offered), CancellationToken.None);
        }

        log?.Invoke($"Studio upgrade: started, {offer.Installed} -> {offer.Offered}.");
        return null;
    }

    private async Task RunAsync(string version)
    {
        StudioUpgradeOutcome outcome;
        try
        {
            outcome = await apply(version, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            outcome = new StudioUpgradeOutcome(false, $"The upgrade failed: {ex.Message}");
        }

        lock (_gate)
        {
            _status = new StudioUpgradeStatus(
                outcome.Applied ? StudioUpgradeState.Succeeded : StudioUpgradeState.Failed, version, outcome.Said, _clock.GetUtcNow());
        }

        log?.Invoke($"Studio upgrade to {version}: {(outcome.Applied ? "applied" : "not applied")}. {outcome.Said}");
    }
}

/// <summary>The body of an apply request: only the version a person approved.</summary>
public sealed record StudioUpgradeRequest(string? Version);

/// <summary>
/// The upgrade operation's HTTP surface (backlog 157): exactly check, apply and status, behind its own token, on the
/// addresses fravashi.exe binds (loopback, and the WSL-facing one the studio container reaches). It takes no image, no
/// URL and no flag from the studio: what is installed is what the update channel offers, verified by the launcher.
/// </summary>
public static class StudioUpgradeApplication
{
    public static WebApplication Create(string token, IReadOnlyList<string> urls, StudioUpgradeRunner runner)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls([.. urls]);
        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            if (!StudioUpgradeToken.Verify(context.Request.Headers[StudioUpgradeToken.Header].ToString(), token))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next(context);
        });

        app.MapGet("/upgrade/check", async (CancellationToken ct) => Results.Json(await runner.CheckAsync(ct)));

        app.MapGet("/upgrade/status", () => Results.Json(Describe(runner.Status)));

        app.MapPost("/upgrade/apply", async (StudioUpgradeRequest body, CancellationToken ct) =>
        {
            var refusal = await runner.TryStartAsync(body.Version ?? string.Empty, ct);
            return refusal is null
                ? Results.Json(Describe(runner.Status), statusCode: StatusCodes.Status202Accepted)
                : Results.Json(new { error = refusal }, statusCode: StatusCodes.Status409Conflict);
        });

        return app;
    }

    /// <summary>The status as the studio reads it: the state as a word, never an enum's number.</summary>
    private static object Describe(StudioUpgradeStatus status) =>
        new { state = status.State.ToString(), version = status.Version, said = status.Said, at = status.At };
}
