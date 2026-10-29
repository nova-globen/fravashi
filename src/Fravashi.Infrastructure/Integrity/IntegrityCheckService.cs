using Fravashi.Application.Integrity;
using Fravashi.Application.Vpn;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Fravashi.Domain;

namespace Fravashi.Infrastructure.Integrity;

/// <summary>
/// The fail-closed integrity <c>BackgroundService</c> (F3 / ADR 0007 #9). At startup (after a short delay) and then
/// on an interval it reads the launcher-written expected-shape record, verifies its HMAC with the launcher-injected
/// key, and compares the running shape (env, non-root uid, mounts, digest, port) to it via
/// <see cref="IntegrityEvaluator"/>. It publishes the result to <see cref="IntegrityStatusProvider"/> so the lock
/// middleware and the Settings surface reflect it.
/// <para>
/// <b>Fail-closed</b> (only in <see cref="IntegrityMode.Enforced"/>: in-container, non-Development, provisioned): on
/// a genuine mismatch it (1) publishes the failed status so the lock middleware serves the loud
/// <c>/integrity-failed</c> page for every route, (2) logs a prominent banner, and (3) after a short grace (so the
/// page + logs are observable) calls <see cref="IHostApplicationLifetime.StopApplication"/>, which stops Kestrel
/// and exits the container's process. This is NOT data-destructive - <c>/data</c> is untouched - and is fully
/// recoverable: a legitimate <c>fravashi</c> relaunch re-pins the digest, rewrites a correct record, and the app
/// self-check passes. Outside a container / in Development / on an unprovisioned container it is report-only and
/// never terminates (do-no-harm).
/// </para>
/// </summary>
public sealed class IntegrityCheckService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(5);

    private readonly FileIntegrityRecordReader _reader;
    private readonly IRuntimeShapeProbe _probe;
    private readonly IntegrityStatusProvider _status;
    private readonly IHostEnvironment _environment;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IVpnConnectivity _vpn;
    private readonly TimeProvider _clock;
    private readonly ILogger<IntegrityCheckService> _logger;
    private bool _terminating;

    public IntegrityCheckService(
        FileIntegrityRecordReader reader,
        IRuntimeShapeProbe probe,
        IntegrityStatusProvider status,
        IHostEnvironment environment,
        IHostApplicationLifetime lifetime,
        IVpnConnectivity vpn,
        ILogger<IntegrityCheckService> logger,
        TimeProvider? clock = null)
    {
        _reader = reader;
        _probe = probe;
        _status = status;
        _environment = environment;
        _lifetime = lifetime;
        _vpn = vpn;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, _clock, stoppingToken);
            if (await EvaluateAndMaybeFailClosedAsync(stoppingToken))
            {
                return; // Fail-closed engaged; stop the loop.
            }

            using var timer = new PeriodicTimer(Period, _clock);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (await EvaluateAndMaybeFailClosedAsync(stoppingToken))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Runs one evaluation, publishes it, and - under enforcement on a genuine mismatch - engages fail-closed.
    /// Returns true when fail-closed engaged (so the caller stops looping). Exposed for the scenario harness.
    /// </summary>
    public async Task<bool> EvaluateAndMaybeFailClosedAsync(CancellationToken cancellationToken)
    {
        var status = Evaluate();
        _status.Set(status);

        if (!status.IsFailClosed || _terminating)
        {
            return status.IsFailClosed;
        }

        _terminating = true;
        var failed = status.Checks
            .Where(c => c.Status == IntegrityCheckStatus.Fail)
            .Select(c => $"{c.Name}: {c.Detail}")
            .ToList();

        _logger.LogCritical(
            "INTEGRITY CHECK FAILED - the running container does not match the launcher-recorded expected shape. " +
            $"Failing closed and stopping the app. Failing checks: {{Failures}}. Recover with a legitimate `{ProductName.Command}` relaunch.",
            string.Join(" | ", failed));

        Console.Error.WriteLine("");
        Console.Error.WriteLine("  ****************************  INTEGRITY CHECK FAILED  ****************************");
        foreach (var line in failed)
        {
            Console.Error.WriteLine($"    - {line}");
        }

        Console.Error.WriteLine($"  The app is locked and will stop. Recover by relaunching with `{ProductName.Command}` (non-destructive).");
        Console.Error.WriteLine("  ********************************************************************************");
        Console.Error.WriteLine("");

        try
        {
            await Task.Delay(TerminateGrace(), _clock, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Shutting down anyway.
        }

        _lifetime.StopApplication();
        return true;
    }

    private IntegrityStatus Evaluate()
    {
        var record = _reader.Read();
        var key = DecodeKey(_probe.GetEnv(IntegrityCanonical.KeyEnvVar));
        // The live tunnel state feeds the sidecar component's report-only VPN-tunnel check (VPN4); ignored when the
        // record has no sidecar component.
        return IntegrityEvaluator.Evaluate(
            record, key, _probe, _environment.IsDevelopment(), _clock.GetUtcNow(), _vpn.Current.IsConnected);
    }

    private static byte[]? DecodeKey(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(base64.Trim());
            return bytes.Length > 0 ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The grace before termination, so the lock page + logs are observable. Configurable via
    /// <c>FRAVASHI_INTEGRITY_TERMINATE_GRACE_SECONDS</c> (default 8, clamped 0..120).
    /// </summary>
    private static TimeSpan TerminateGrace()
    {
        var raw = Environment.GetEnvironmentVariable("FRAVASHI_INTEGRITY_TERMINATE_GRACE_SECONDS");
        if (int.TryParse(raw, out var seconds))
        {
            return TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 120));
        }

        return TimeSpan.FromSeconds(8);
    }
}
