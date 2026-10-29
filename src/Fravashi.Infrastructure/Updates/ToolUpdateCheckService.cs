using Fravashi.Application.Updates;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fravashi.Infrastructure.Updates;

/// <summary>
/// Refreshes the dashboard's tool-update availability on a schedule (Type A / F1, ADR 0006): once shortly after
/// app start, then daily. This is deliberately <b>not</b> per-CLI-launch nagging - it populates the settings-table
/// cache the card renders from and emits a notification only when a new update appears. It runs off the startup
/// path (a <see cref="BackgroundService"/>, so <see cref="StartAsync"/> returns immediately) and swallows every
/// failure, so a slow or unreachable registry never blocks or crashes the app.
/// </summary>
public sealed class ToolUpdateCheckService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Period = TimeSpan.FromHours(24);

    private readonly IToolUpdateService _updates;
    private readonly ILogger<ToolUpdateCheckService> _logger;

    public ToolUpdateCheckService(IToolUpdateService updates, ILogger<ToolUpdateCheckService> logger)
    {
        _updates = updates;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
            await RunOnceAsync(stoppingToken);

            using var timer = new PeriodicTimer(Period);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _updates.CheckNowAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scheduled tool-update check failed; the cached availability is unchanged.");
        }
    }
}
