using Fravashi.Application.Onboarding;
using Fravashi.Application.Onboarding.Auth;

namespace Fravashi.Infrastructure.Onboarding.Auth;

/// <summary>
/// Microsoft Entra device-code authenticator for Azure DevOps. Uses an owner-registered Entra <b>public
/// client</b> (allow-public-client-flows = yes, device code enabled), the <c>organizations</c> tenant, and the
/// Azure DevOps resource scope <c>499b84ac-…/.default offline_access</c>. The result is a Bearer AAD access
/// token (plus a refresh token) - <b>not</b> a PAT, so its consumers must send <c>Authorization: Bearer</c>,
/// which the bridge signals with <c>AzureDevOps:AuthScheme</c>.
/// </summary>
public sealed class EntraDeviceCodeAuthenticator : IDeviceCodeAuthenticator
{
    private readonly OAuthDeviceFlow _flow;
    private readonly AuthConfigStore _config;

    public EntraDeviceCodeAuthenticator(OAuthDeviceFlow flow, AuthConfigStore config)
    {
        _flow = flow;
        _config = config;
    }

    public string Provider => OnboardingProviders.AzureDevOps;

    public Task<DeviceCodeStart> StartAsync(CancellationToken cancellationToken = default) =>
        _flow.StartAsync(ResolveOrThrow(), cancellationToken);

    public Task<DevicePollResult> PollOnceAsync(string deviceCode, CancellationToken cancellationToken = default) =>
        _flow.PollOnceAsync(ResolveOrThrow(), deviceCode, cancellationToken);

    private OAuthDeviceFlow.Config ResolveOrThrow()
    {
        var resolved = _config.ResolveEntra()
            ?? throw new InvalidOperationException("Azure DevOps sign-in is not configured - set an Entra client id in Sign-in configuration.");
        return new OAuthDeviceFlow.Config(resolved.DeviceCodeUrl, resolved.TokenUrl, resolved.ClientId, resolved.Scope);
    }
}
