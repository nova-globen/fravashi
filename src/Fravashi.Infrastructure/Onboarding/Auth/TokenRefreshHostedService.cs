using Fravashi.Application.Onboarding.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fravashi.Infrastructure.Onboarding.Auth;

/// <summary>
/// Keeps OAuth access tokens fresh in the background so the studio's adapters never present an expired token.
/// Every <see cref="Interval"/> it asks the refresher to renew each connection whose credential is expiring;
/// the refresher is a no-op for PATs, for GitHub device tokens (which do not expire), and for credentials that
/// are still valid - so with no OAuth connection configured this simply idles. Any successful refresh triggers
/// a configuration reload, which re-projects the new token through the secret→adapter bridge.
/// </summary>
public sealed class TokenRefreshHostedService : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private static readonly string[] ConnectionIds = ["source", "wiki", "board"];

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<TokenRefreshHostedService> _logger;

    public TokenRefreshHostedService(IServiceScopeFactory scopes, ILogger<TokenRefreshHostedService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            await RefreshAllAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await SafeWaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private async Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var refresher = scope.ServiceProvider.GetRequiredService<IProviderTokenRefresher>();
            foreach (var id in ConnectionIds)
            {
                if (await refresher.RefreshIfNeededAsync(id, cancellationToken).ConfigureAwait(false))
                {
                    _logger.LogInformation("Refreshed the OAuth access token for the {Connection} connection.", id);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The background token refresh pass failed; it will retry.");
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
