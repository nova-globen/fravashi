using Fravashi.Application.Autopilot;
using Fravashi.Domain.Autopilot;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fravashi.Infrastructure.Autopilot;

/// <summary>
/// The HOST ADR 0031 decisions 10 and 14 call for (#309): the only thing that calls
/// <see cref="AutopilotEngine.ResumeAllAsync"/> and <see cref="AutopilotEngine.TickAndSaveAsync"/> in the
/// running studio. Without this, a dispatched run (#298) saves a row nothing ever advances, which is exactly
/// the limitation #298's own plan recorded and this slice closes.
/// <para>
/// Resumes every open run once, at start, then on an interval lists every run still open and ticks each ONE
/// AT A TIME, through <see cref="AutopilotTickGate"/> rather than a lock that would make the host wait: a run
/// still mid-tick is skipped for this cycle, so two ticks of one run never overlap, and a tick that throws is
/// logged and never retried inside the same cycle, which is what keeps a crashed tick bounded instead of
/// spinning the host.
/// </para>
/// </summary>
public sealed class AutopilotEngineHost : BackgroundService
{
    /// <summary>How often the host looks for open runs to tick.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(15);

    private readonly AutopilotEngine _engine;
    private readonly IAutopilotRunStore _store;
    private readonly AutopilotTickGate _gate;
    private readonly TimeSpan _interval;
    private readonly ILogger _logger;

    public AutopilotEngineHost(
        AutopilotEngine engine,
        IAutopilotRunStore store,
        AutopilotTickGate gate,
        TimeSpan? interval = null,
        ILogger<AutopilotEngineHost>? logger = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _interval = interval ?? DefaultInterval;
        _logger = logger ?? NullLogger<AutopilotEngineHost>.Instance;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ResumeAsync(stoppingToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(_interval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await TickAllOpenRunsAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task ResumeAsync(CancellationToken ct)
    {
        try
        {
            var resumed = await _engine.ResumeAllAsync(ct).ConfigureAwait(false);
            if (resumed.Count > 0)
            {
                _logger.LogInformation("Resumed {Count} autopilot run(s) at studio start.", resumed.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not resume open autopilot runs at studio start.");
        }
    }

    private async Task TickAllOpenRunsAsync(CancellationToken ct)
    {
        IReadOnlyList<AutopilotRun> open;
        try
        {
            open = await _store.OpenRunsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not list open autopilot runs.");
            return;
        }

        foreach (var run in open)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            await TickOneRunAsync(run.Id, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Ticks one run under its own gate. Internal, and awaited one run at a time by
    /// <see cref="TickAllOpenRunsAsync"/>, but also callable directly (as the tests do) to prove two
    /// concurrent calls for the SAME run never both tick it.
    /// </summary>
    internal async Task TickOneRunAsync(Guid runId, CancellationToken ct)
    {
        if (!_gate.TryEnter(runId))
        {
            return;
        }

        try
        {
            await _engine.TickAndSaveAsync(runId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Autopilot run {RunId} failed to tick.", runId);
        }
        finally
        {
            _gate.Exit(runId);
        }
    }
}
