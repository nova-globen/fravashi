using Fravashi.Application.Onboarding.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Fravashi.Infrastructure.Onboarding.Auth;

/// <summary>
/// Registers the provider sign-in (SSO/OIDC) services: the config store, the device-code and auth-code
/// authenticators (each with its own <see cref="HttpClient"/>), the CSRF state ledger, the callback and
/// sign-in-start services, the token refresher, and the background refresh loop. The PAT path needs none of
/// this and is unaffected.
/// </summary>
public static class AuthServiceCollectionExtensions
{
    public static IServiceCollection AddProviderAuth(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<AuthConfigStore>();
        services.AddSingleton<IAuthConfigStore>(sp => sp.GetRequiredService<AuthConfigStore>());

        // Device-code flow (GitHub, Entra) - one shared HTTP engine, two thin authenticators.
        services.AddHttpClient<OAuthDeviceFlow>();
        services.AddTransient<GitHubDeviceCodeAuthenticator>();
        services.AddTransient<EntraDeviceCodeAuthenticator>();

        // Auth-code flow (Atlassian 3LO).
        services.AddHttpClient<AtlassianAuthCodeAuthenticator>();

        // The owner-IdP sign-in gate flow (plan Phase 3): code + PKCE, session envelope, refresh.
        services.AddHttpClient<IdpAuthService>();
        services.AddScoped<IdpSessionState>();
        // Backlog 234: one per process, so the refresh, the loop, the gate and every page say the same thing.
        services.TryAddSingleton<IdpSessionGrace>();
        // #372: the sign-in is the studio's, not a window's: one per process, so every window sees the same attempt and
        // each callback state is handled once.
        services.TryAddSingleton<IdpSignInCoordinator>();
        services.AddHostedService<IdpSessionRefreshHostedService>();
        // Backlog 94: take a session the launcher obtained before the container started, and tell the host where
        // this studio signs in. Registered with the other auth services so its start pass runs before the server
        // listens.
        services.AddHttpClient<IIdpHandoverChannel, BridgeIdpHandoverChannel>();
        services.AddHostedService<IdpHandoverHostedService>();

        services.AddTransient<IProviderAuthenticatorResolver, ProviderAuthenticatorResolver>();

        // CSRF ledger + orchestration for the out-of-circuit callback.
        services.AddSingleton<IAuthCodeStateStore, InMemoryAuthCodeStateStore>();
        services.AddTransient<AuthCodeSignInStarter>();
        services.AddTransient<AuthCodeCallbackService>();

        // Refresh + the background loop that keeps access tokens fresh.
        services.AddHttpClient<IProviderTokenRefresher, ProviderTokenRefresher>();
        services.AddHostedService<TokenRefreshHostedService>();

        return services;
    }
}
