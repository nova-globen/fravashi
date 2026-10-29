using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fravashi.Infrastructure.Onboarding.Auth;

/// <summary>
/// The studio's half of the sign-in hand-over (backlog 94).
/// <para>
/// At START, before the studio serves anything (this runs in <see cref="StartAsync"/>, which the host awaits before
/// it listens, so the window's first request already sees the outcome): an expired session that can be refreshed
/// is refreshed, and when there is still no session, one the launcher left is taken and adopted through
/// <see cref="IdpAuthService.AdoptHandedSessionAsync"/>, which validates it with the identity provider. Refusal, a
/// missing bridge or a slow provider only mean the sign-in page appears as it always has. The pass is bounded, so
/// a provider that hangs cannot hold the studio's start.
/// </para>
/// <para>
/// Then, every minute and whenever it changes, the studio tells the host where it signs in and whether its session
/// is usable. That is what lets the NEXT start decide whether signing in before the container is worth a browser
/// window at all.
/// </para>
/// </summary>
public sealed class IdpHandoverHostedService(
    IServiceScopeFactory scopes, ILogger<IdpHandoverHostedService> logger) : BackgroundService
{
    public static readonly TimeSpan StartBudget = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan PublishInterval = TimeSpan.FromMinutes(1);

    private IdpSignInCoordinates? _published;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(StartBudget);
        try
        {
            await FirstPassAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("The sign-in hand-over did not finish within {Budget}; the sign-in page takes over.", StartBudget);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The sign-in hand-over failed; the sign-in page takes over.");
        }

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The start pass. Public so a test drives it without a host; the host calls it from StartAsync.</summary>
    public async Task FirstPassAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var idp = scope.ServiceProvider.GetRequiredService<IdpAuthService>();
        var session = scope.ServiceProvider.GetRequiredService<IdpSessionState>();
        var channel = scope.ServiceProvider.GetRequiredService<IIdpHandoverChannel>();
        if (!idp.IsConfigured)
        {
            return;
        }

        if (!session.SignedIn)
        {
            // A session that expired while the studio was down: renewing it is quieter than any sign-in.
            await idp.RefreshIfNeededAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!session.SignedIn && channel.Available
            && await channel.TakeAsync(cancellationToken).ConfigureAwait(false) is { } handed)
        {
            var adopted = await idp.AdoptHandedSessionAsync(handed, cancellationToken).ConfigureAwait(false);
            if (adopted.Success)
            {
                logger.LogInformation("Signed in with the session the launcher obtained before start ({Detail}).", adopted.Detail);
            }
            else
            {
                logger.LogWarning("The session the launcher handed over was refused: {Detail} The sign-in page takes over.", adopted.Detail);
            }
        }

        await PublishAsync(idp, session, channel, cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PublishInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await PublishAsync(
                        scope.ServiceProvider.GetRequiredService<IdpAuthService>(),
                        scope.ServiceProvider.GetRequiredService<IdpSessionState>(),
                        scope.ServiceProvider.GetRequiredService<IIdpHandoverChannel>(),
                        stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "Publishing the sign-in coordinates failed; the next pass retries.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    private async Task PublishAsync(
        IdpAuthService idp, IdpSessionState session, IIdpHandoverChannel channel, CancellationToken cancellationToken)
    {
        if (!channel.Available || idp.Coordinates(session.SignedIn) is not { } coordinates || coordinates == _published)
        {
            return;
        }

        await channel.PublishAsync(coordinates, cancellationToken).ConfigureAwait(false);
        _published = coordinates;
    }
}
