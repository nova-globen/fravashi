using Fravashi.Application.Persistence;
using Fravashi.Application.Wiki;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fravashi.Infrastructure.PostOnboarding;

/// <summary>
/// Fires the F2 post-onboarding work on app start: when onboarding is complete, it runs the
/// initial wiki sync (if not yet synced). Idempotent (<c>EnsureStartedAsync</c> no-ops when
/// already done or already running), so this covers the "app restarted with onboarding already
/// complete" case and, together with the dashboard card's own on-open trigger, the "just finished
/// onboarding" case. Never throws: a failure lands in the card's state. (The repository clone
/// half retired with the in-container clone model, plan 2c decision 6.)
/// </summary>
public sealed class PostOnboardingTasksService(
    IOnboardingState onboarding,
    IWikiSyncService wiki,
    ILogger<PostOnboardingTasksService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Let the app finish starting before touching git / the network.
            await Task.Delay(TimeSpan.FromSeconds(4), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (!onboarding.IsComplete)
            {
                return;
            }

            await wiki.EnsureStartedAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Post-onboarding tasks trigger failed; the card can retry manually.");
        }
    }
}
